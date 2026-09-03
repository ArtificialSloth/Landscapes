namespace VintageHorizons.Checks;

/// <summary>
/// Rainfall's height terms, which the climate field has to undo and the shader has to put back.
///
/// This is the arithmetic that made 0.3.180 slightly worse than the height ramp it replaced.
/// The ramp asked the engine for a finished colour at each height, so the engine applied these
/// terms itself and the mod never had to know they existed. The field stores climate instead,
/// so it owns them - and it read rainfall at sea level, which is precisely the height where
/// the bonus term is at its maximum, then spent that number on terrain at every other height.
/// </summary>
public static class ClimateRainfallChecks
{
    public static void Run(Check c)
    {
        TheOffsetMatchesTheEngine(c);
        RawRainfallRoundTrips(c);
        SaturatedReadingsAreRefused(c);
        SeaLevelIsTheWorstPlaceToSample(c);
    }

    /// <summary>
    /// Climate.GetRainFall(raw, y) = clamp(raw + (y - sea)/2 + 5*clamp(8 + sea - y, 0, 8), 0, 255),
    /// transcribed from its IL. Integer division truncates toward zero, which matters below
    /// sea level where a floor would give a different answer.
    /// </summary>
    static void TheOffsetMatchesTheEngine(Check c)
    {
        const int sea = 110;

        // The bonus is full at and below sea level, gone eight blocks above, linear between.
        c.Eq(40, LodClimateMath.RainfallOffset(sea, sea) - (sea - sea) / 2,
            "the bonus is 40 at sea level");
        c.Eq(0, LodClimateMath.RainfallOffset(sea + 8, sea) - 8 / 2,
            "and zero eight blocks above it");
        for (int above = 0; above <= 8; above++)
        {
            int bonus = LodClimateMath.RainfallOffset(sea + above, sea) - above / 2;
            c.Eq(5 * (8 - above), bonus, $"the bonus falls by 5 per block, at +{above}");
        }

        // Below sea level the bonus stays capped and the height term goes negative.
        c.Eq(40 + (-10) / 2, LodClimateMath.RainfallOffset(sea - 10, sea),
            "below sea level the bonus is still capped at 40");
        c.Eq(-5, (-10) / 2, "and the height term truncates toward zero, as the engine's does");

        // Well above sea level it is the height term alone, rising steadily.
        c.Eq(50, LodClimateMath.RainfallOffset(sea + 100, sea),
            "a hundred blocks up adds fifty counts of rainfall and no bonus");

        c.Eq(255, LodClimateMath.RainfallAt(250, sea + 100, sea), "and the result clamps");
        c.Eq(0, LodClimateMath.RainfallAt(0, sea - 200, sea), "at both ends");
    }

    static void RawRainfallRoundTrips(Check c)
    {
        foreach (int sea in new[] { 110, 165 })
        {
            foreach (int sampleY in new[] { sea, sea + 8, sea + 40 })
            {
                for (int raw = 0; raw <= 200; raw += 10)
                {
                    int sampled = LodClimateMath.RainfallAt(raw, sampleY, sea);
                    if (sampled >= 255 || sampled <= 0) continue;
                    c.True(LodClimateMath.TryRawRainfall(sampled, sampleY, sea, out float got),
                        $"raw {raw} read at {sampleY} can be recovered");
                    c.Eq((float)raw, got, $"and comes back as {raw}");
                }
            }
        }
    }

    static void SaturatedReadingsAreRefused(Check c)
    {
        const int sea = 110;
        // 230 raw at sea level saturates: 230 + 40 is past 255, so the reading cannot say
        // which raw value produced it. Inventing one would paint a whole wet biome wrong.
        c.Eq(255, LodClimateMath.RainfallAt(230, sea, sea), "a wet biome saturates at sea level");
        c.False(LodClimateMath.TryRawRainfall(255f, sea, sea, out _),
            "and a saturated reading is refused rather than guessed");
        c.False(LodClimateMath.TryRawRainfall(0f, sea, sea, out _),
            "as is one clamped at the bottom");
    }

    /// <summary>
    /// The size of the mistake, in the units the colour map is indexed by. This is not a
    /// property of the code so much as a record of why it was worth fixing: the map is 256
    /// counts wide, so tens of counts is a different part of it entirely.
    /// </summary>
    static void SeaLevelIsTheWorstPlaceToSample(Check c)
    {
        const int sea = 110;
        const int raw = 115;

        // The error is exactly the bonus that should have gone away, less the height term
        // that should have been added: 40 - above/2. Largest just above sea level, gone by
        // eighty blocks up, and it is stated exactly rather than bounded so that a change to
        // either term fails here instead of quietly staying "large enough".
        int atSeaLevel = LodClimateMath.RainfallAt(raw, sea, sea);
        foreach (int above in new[] { 10, 20, 40, 80 })
        {
            int correct = LodClimateMath.RainfallAt(raw, sea + above, sea);
            c.Eq(40 - above / 2, atSeaLevel - correct,
                $"the sea level reading is 40 - {above}/2 counts too wet {above} blocks up");
        }
        c.True(atSeaLevel - LodClimateMath.RainfallAt(raw, sea + 10, sea) >= 35,
            "which is most of a sixth of the whole rainfall axis on ordinary low ground");

        // And it is the maximum: no other sample height is worse than sea level.
        for (int y = sea - 30; y <= sea + 30; y++)
        {
            c.True(LodClimateMath.RainfallOffset(y, sea) <= LodClimateMath.RainfallOffset(sea, sea)
                || y > sea + 8,
                $"sea level carries the largest bonus, checked at {y}");
        }
    }
}
