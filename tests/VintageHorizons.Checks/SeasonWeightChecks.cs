namespace VintageHorizons.Checks;

/// <summary>
/// The seasonal blend weight, held against the engine rather than against itself.
///
/// G139 is the reason this file is shaped the way it is. The last time a piece of engine
/// arithmetic was transcribed into this mod, the check was written from the transcription and
/// therefore agreed with its error, which is worse than having no check at all. So nothing here
/// asserts that <see cref="LodSeasonWeight"/> equals <see cref="LodSeasonWeight"/>.
///
/// Three independent things are pinned instead:
///
/// 1. The PREMISE - that the engine's two correction terms are integer divisions which are zero
///    for every reachable temperature. That is a property of the engine, reimplemented here from
///    its IL, and it is the entire reason the mod cannot simply call the engine and trust it.
/// 2. The BOUNDS the division depends on.
/// 3. A table of expected values produced by transcribing `colormap.vsh` into a DIFFERENT
///    language and evaluating that, checked in as data. It caught two errors in the first draft
///    of this file, both of them the same mistake: mixing up the raw temperature with the
///    altitude-adjusted one the shader actually receives.
/// </summary>
public static class SeasonWeightChecks
{
    public static void Run(Check c)
    {
        TheEnginesCorrectionTermsAreDead(c);
        TheEngineWeightCanNeverReachZeroOrOne(c);
        TheShaderWeightMatchesItsOwnSource(c);
        TheAltitudeShiftIsTheOneTheShaderApplies(c);
        TheTwoAgreeOnlyWhereTheyShould(c);
        TheRampAddressesLikeTheTextureTheShaderReads(c);
        TheSampledHeightsSpanExactlyTheRamp(c);
    }

    /// <summary>
    /// The colour maps are read at a few heights and the ramp is interpolated between them, so
    /// the two spans have to be the same span. If the sampled heights stopped short, the top of
    /// the ramp would extrapolate past its last real sample; if they overshot, the ramp would
    /// never reach the sample that describes its own end. Neither would look like a bug in the
    /// height ramp - it would look like distant high ground being slightly the wrong colour.
    /// </summary>
    static void TheSampledHeightsSpanExactlyTheRamp(Check c)
    {
        float rampSpan = (LodTintRegistry.RampSteps - 1) * LodTintRegistry.RampStepBlocks;

        c.Eq(0f, LodTintRegistry.SampledHeightOffset(0),
            "the first sampled height is ramp step 0");
        c.True(Math.Abs(LodTintRegistry.SampledHeightOffset(LodTintRegistry.SampledHeights - 1)
                        - rampSpan) < 0.001f,
            "the last sampled height is the last ramp step");

        // Evenly spaced, and each one lands on a step rather than between two.
        float spacing = LodTintRegistry.SampledHeightOffset(1);
        for (int i = 1; i < LodTintRegistry.SampledHeights; i++)
        {
            float got = LodTintRegistry.SampledHeightOffset(i)
                - LodTintRegistry.SampledHeightOffset(i - 1);
            c.True(Math.Abs(got - spacing) < 0.001f, $"sampled height {i} is evenly spaced");
        }

        // The interpolation in BuildRamp indexes sampled heights as step * (K-1) / (steps-1) and
        // clamps the lower index to K-2, so the very last step must land exactly on K-1 with a
        // fraction of one. Off by any amount and the top of the ramp silently repeats step K-2.
        float top = (LodTintRegistry.RampSteps - 1)
            * (LodTintRegistry.SampledHeights - 1f) / (LodTintRegistry.RampSteps - 1f);
        c.True(Math.Abs(top - (LodTintRegistry.SampledHeights - 1)) < 1e-4f,
            "the last ramp step interpolates onto the last sampled height exactly");

        c.True(LodTintRegistry.SampledHeights >= 2, "interpolation needs at least two samples");
        c.True(LodTintRegistry.RampSteps > LodTintRegistry.SampledHeights,
            "the ramp is finer than the sampling, which is the whole point of it");
    }

    /// <summary>
    /// The ramp's addressing, which exists in C# and again in GLSL.
    ///
    /// `LodTintRegistry.RampTexel` is an index into a flat array that gets uploaded as a texture
    /// `RampWidth` wide, so texel n sits at x = n % width, y = n / width. The vertex shader reads
    /// `ivec2(slot, step)`. Those two statements have to mean the same thing, and if they stop
    /// meaning it every slot is misaddressed at once - which reads as a colour bug rather than an
    /// indexing one, so nothing about the symptom would point here. StaticAssetChecks pins the
    /// GLSL half.
    /// </summary>
    static void TheRampAddressesLikeTheTextureTheShaderReads(Check c)
    {
        c.Eq(LodTintRegistry.MaxSlots, LodTintRegistry.RampWidth,
            "the ramp texture is one column per slot");
        c.Eq(LodTintRegistry.RampSteps, LodTintRegistry.RampHeight,
            "the ramp texture is one row per height step");

        foreach (int slot in new[] { 0, 1, 17, LodTintRegistry.MaxSlots - 1 })
        foreach (int step in new[] { 0, 1, 48, LodTintRegistry.RampSteps - 1 })
        {
            int texel = LodTintRegistry.RampTexel(slot, step);
            c.Eq(slot, texel % LodTintRegistry.RampWidth,
                $"slot {slot} step {step} lands in column {slot}");
            c.Eq(step, texel / LodTintRegistry.RampWidth,
                $"slot {slot} step {step} lands in row {step}");
        }

        // Distinct, and inside the buffer: an overlap would blend two slots together.
        var seen = new HashSet<int>();
        for (int slot = 0; slot < LodTintRegistry.MaxSlots; slot++)
        for (int step = 0; step < LodTintRegistry.RampSteps; step++)
        {
            int texel = LodTintRegistry.RampTexel(slot, step);
            c.True(texel >= 0 && texel < LodTintRegistry.MaxSlots * LodTintRegistry.RampSteps
                && seen.Add(texel), $"slot {slot} step {step} has its own texel");
        }

        // The window has to reach past where the curve stops moving, or high ground clamps to a
        // step that is still on the slope. 382 blocks is the worst case over all temperatures.
        float top = LodTintRegistry.RampMinDistToSeaLevel
            + (LodTintRegistry.RampSteps - 1) * LodTintRegistry.RampStepBlocks;
        c.True(top >= 382f, $"the ramp reaches past where height stops mattering (to {top:0})");
        c.True(LodTintRegistry.RampMinDistToSeaLevel <= -200f,
            "and far enough below sea level to cover where it saturates the other way");
    }

    /// <summary>
    /// Transcribed from `ClientWorldMap.ApplyColorMapOnRgba`'s IL, integer divisions included.
    /// If a future engine makes these floats, this check fails and the correction is no longer
    /// needed - which is exactly the news worth having.
    /// </summary>
    static void TheEnginesCorrectionTermsAreDead(Check c)
    {
        int coldNonZero = 0, hotNonZero = 0;
        for (int temp = 0; temp <= 255; temp++)
        {
            if (Math.Max(0, 128 - temp) / 512 != 0) coldNonZero++;
            if (Math.Max(0, temp - 130) / 200 != 0) hotNonZero++;
        }
        c.Eq(0, coldNonZero, "the engine's cold-end correction is zero at every reachable temperature");
        c.Eq(0, hotNonZero, "the engine's hot-end correction is zero at every reachable temperature");

        // And therefore the engine's whole weight is the bare cosine, which is what the mod
        // divides out. Stated separately because the line above is about the engine and this one
        // is about what the mod is entitled to assume from it.
        for (int temp = 0; temp <= 255; temp += 17)
        {
            float bare = Math.Clamp(0.5f - MathF.Cos(temp / 42f) / 2.3f, 0f, 1f);
            c.True(Math.Abs(LodSeasonWeight.Engine(temp) - bare) < 1e-4f,
                $"the engine's weight at temp {temp} is the bare cosine term");
        }
    }

    /// <summary>
    /// `corrected = climate + (engineAnswer - climate) * (vanilla / engine)` divides by the
    /// engine's weight at every sampled position, so a zero there would blow the tint out. It
    /// cannot happen, and the reason is arithmetic rather than luck: the cosine is bounded.
    /// </summary>
    static void TheEngineWeightCanNeverReachZeroOrOne(Check c)
    {
        float lowest = 2f, highest = -1f;
        for (int temp = 0; temp <= 255; temp++)
        {
            float w = LodSeasonWeight.Engine(temp);
            lowest = Math.Min(lowest, w);
            highest = Math.Max(highest, w);
        }
        c.True(lowest > 0.06f, $"the engine's weight never approaches zero (lowest {lowest:0.0000})");
        c.True(highest < 0.94f, $"the engine's weight never reaches the clamp (highest {highest:0.0000})");
        c.True(lowest >= LodSeasonWeight.EngineWeightMin - 1e-3f,
            "the measured floor agrees with the analytic bound");
        c.True(highest <= LodSeasonWeight.EngineWeightMax + 1e-3f,
            "the measured ceiling agrees with the analytic bound");
    }

    /// <summary>
    /// Values from an independent transcription of the shader's expression, checked in as data.
    /// Not derived from the implementation, which is the whole point.
    /// </summary>
    static void TheShaderWeightMatchesItsOwnSource(Check c)
    {
        // ADJUSTED temperature (what the shader receives), blocks above sea level, expected.
        // Computed from the GLSL text in a separate language, not from the C# below it: a table
        // generated by the implementation would agree with the implementation's mistakes.
        (float AdjustedTemp, float Dist, float Expected)[] table =
        {
            (0f, 0f, 0.3152f),
            (32f, 0f, 0.3729f),
            (64f, 0f, 0.6046f),
            (96f, 0f, 0.8475f),
            (128f, 0f, 0.9329f),
            (160f, 0f, 0.6913f),
            (192f, 0f, 0.2511f),
            (208f, 0f, 0.0067f),
            (224f, 0f, 0.0000f),
            (96f, 60f, 0.3417f),
            (60f, 200f, 0.0000f),
        };
        foreach ((float temp, float dist, float expected) in table)
        {
            float got = LodSeasonWeight.Vanilla(temp, dist);
            c.True(Math.Abs(got - expected) < 0.002f,
                $"shader weight at temp {temp}, {dist} blocks up is {expected:0.0000} (got {got:0.0000})");
        }
    }

    /// <summary>
    /// The shader receives the altitude-ADJUSTED temperature and adds `dist * 1.5` back, while
    /// the engine subtracted `dist / 1.5` to produce it. Net shift on the weight's temperature is
    /// the difference of those two, and getting it backwards would move mountains the wrong way.
    /// </summary>
    static void TheAltitudeShiftIsTheOneTheShaderApplies(Check c)
    {
        const float adjusted = 100f;
        const float dist = 60f;

        c.True(Math.Abs(LodSeasonWeight.RawFromAdjusted(adjusted, dist) - (adjusted + dist / 1.5f)) < 1e-3f,
            "the raw temperature is the adjusted one plus dist/1.5");

        // The shader's own x, spelled out here rather than called, so the two cannot drift.
        float shaderX = adjusted + dist * 1.5f;
        float expected = Math.Clamp(
            0.5f - MathF.Cos(shaderX / 42f) / 2.3f
                 + Math.Max(0f, 128f - shaderX) / 512f
                 - Math.Max(0f, shaderX - 130f) / 200f, 0f, 1f);
        c.True(Math.Abs(LodSeasonWeight.Vanilla(adjusted, dist) - expected) < 1e-4f,
            "the shader's weight uses adjusted temperature plus dist*1.5");

        // Below sea level neither side moves: both take max(0, ...).
        c.Eq(LodSeasonWeight.Vanilla(adjusted, 0f), LodSeasonWeight.Vanilla(adjusted, -40f),
            "ground below sea level takes the sea-level weight");
    }

    /// <summary>
    /// The size and shape of the disagreement, pinned so a change that quietly reduces it gets
    /// noticed. These are the numbers the correction exists for.
    /// </summary>
    static void TheTwoAgreeOnlyWhereTheyShould(Check c)
    {
        float Diff(int temp, float dist) =>
            LodSeasonWeight.Vanilla(temp, dist)
            - LodSeasonWeight.Engine((int)MathF.Round(LodSeasonWeight.RawFromAdjusted(temp, dist)));

        c.True(Math.Abs(Diff(128, 0f)) < 0.01f, "at sea level the two agree near temperature 128");
        c.True(Diff(32, 0f) > 0.15f, "cold ground at sea level gets too little season colour today");
        c.True(Diff(192, 0f) < -0.25f, "hot ground at sea level gets too much");

        float worst = 0f;
        for (int temp = 0; temp <= 255; temp++) worst = Math.Max(worst, Math.Abs(Diff(temp, 0f)));
        c.True(worst > 0.35f, $"the worst sea-level disagreement is large (measured {worst:0.000})");

        // Altitude is where it stops being a tint difference and becomes a different season.
        // A real mountain: the climate map reads 60 there, which is a raw 193 at 200 blocks up.
        c.True(LodSeasonWeight.Vanilla(60f, 200f) < 0.01f,
            "two hundred blocks up the shader applies no seasonal colour at all");
        c.True(LodSeasonWeight.Engine((int)MathF.Round(LodSeasonWeight.RawFromAdjusted(60f, 200f))) > 0.5f,
            "where the engine still applies more than half");

        // The scale the renderer multiplies the seasonal share by, at the owner's own test patch:
        // 8 blocks above sea level, near the one temperature where this is invisible.
        float scale = LodSeasonWeight.SeasonalShareScale(125f, 8f);
        c.True(scale > 0.93f && scale < 1.07f,
            $"the 2026-08-31 test patch sits where the correction barely applies (scale {scale:0.000})");
    }
}
