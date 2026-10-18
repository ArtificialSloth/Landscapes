using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace VintageHorizons.Tools;

/// <summary>The seam-fog dials, as the Farseer tab stores them.</summary>
record FogDials(float Strength, float Coverage, float Spill, float Height, float Floor,
    float Billow, float Softness, float Quality, float DriftBlocks, float SeamStrength,
    float StripHeight = 3f, float FallBlocks = 0f);

readonly record struct Fog(Vector3 Colour, float Alpha);

/// <summary>
/// The fields a frame draws: the current one and, while it dissolves, the one shown before the
/// seam moved. Each is the signed lateral distance to its seam (Farseer positive); the ground
/// under both is the scene's sampled seam height. <see cref="ResidualWeight"/> is what the
/// shader computes as the residual's alpha minus the elapsed fade.
/// </summary>
readonly record struct FogFields(Func<float, float, float> Current,
    Func<float, float, float>? Residual = null, float ResidualWeight = 0f);

/// <summary>
/// A line-for-line port of lodsilhouettefogvolume.fsh's march. Its constants are read from the
/// shader file, so a retune there reaches the mock-up without an edit here; a change to the
/// shader's functions must be ported by hand, keeping the same names.
/// </summary>
sealed class ShippedFog
{
    readonly Dictionary<string, float> k;
    readonly FogDials d;
    readonly NoiseTexture noise;
    readonly float coreBlocks;
    readonly float stripHalfBlocks;
    // fogCurtainActive: the cached terrain's own edge with empty sky beyond (Scene.Cliff).
    readonly bool curtain;

    public ShippedFog(string shaderSource, FogDials dials, NoiseTexture noise, float coreBlocks,
        float stripHalfBlocks, bool curtain = false)
    {
        k = Regex.Matches(shaderSource, @"const\s+(?:float|int)\s+(\w+)\s*=\s*([-\d.]+)\s*;")
            .ToDictionary(m => m.Groups[1].Value,
                m => float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        d = dials;
        this.noise = noise;
        this.coreBlocks = coreBlocks;
        this.stripHalfBlocks = stripHalfBlocks;
        this.curtain = curtain;
    }

    float K(string name) => k.TryGetValue(name, out float value)
        ? value : throw new InvalidOperationException("the fog shader has no constant " + name);

    static float SmoothStep(float a, float b, float x) => Scene.SmoothStep(a, b, x);
    static float Mix(float a, float b, float t) => a + (b - a) * t;
    static float Clamp01(float x) => Math.Clamp(x, 0f, 1f);

    float NoiseLod(float pixelBlocks, float cellsPerBlock) =>
        MathF.Log2(MathF.Max(0.0001f, pixelBlocks * cellsPerBlock * K("NoiseTexelsPerCell")));

    Vector4 Noise(Vector3 cells, float lod) =>
        noise.Sample(cells * (1f / K("NoisePeriod")), MathF.Max(0f, lod));

    float Fbm(Vector3 q, float lod)
    {
        float flat = K("NoiseFlatLod");
        float second = lod + 1.02f < flat
            ? Noise(q * 2.03f + new Vector3(11.7f, 5.3f, 17.1f), lod + 1.02f).Y : 0.5f;
        float third = lod + 2.03f < flat
            ? Noise(q * 4.09f + new Vector3(3.1f, 19.4f, 7.8f), lod + 2.03f).Z : 0.5f;
        return 0.57f * Noise(q, lod).X + 0.29f * second + 0.14f * third;
    }

    static float ExpectedDensity(float threshold) => threshold >= 0f
        ? MathF.Max(0f, 0.5f * (1f - threshold))
        : (0.5f - threshold) / (1f - threshold);

    static float Phase(float cosine, float g)
    {
        float g2 = g * g;
        return (1f - g2) / MathF.Pow(1f + g2 - 2f * g * cosine, 1.5f);
    }

    struct FogNoise
    {
        public Vector4 Broad;
        public float Body, Thread, DetailWeight, BaseLod, Streak, Tongue;
    }

    /// <summary>vhFogSampleNoise.</summary>
    float StreakNoise(Vector3 p, float streakBlocks, float lod)
    {
        var q = new Vector3(p.X, p.Y / K("CurtainStreakStretch"), p.Z) / streakBlocks;
        float fine = lod + 1f < K("NoiseFlatLod")
            ? Noise(q * 2.07f + new Vector3(13.3f, 6.1f, 2.9f), lod + 1f).Z : 0.5f;
        return 0.65f * Noise(q + new Vector3(7.1f, 3.3f, 1.9f), lod).Y + 0.35f * fine;
    }

    FogNoise SampleNoise(Vector3 fogWorld, float pixelBlocks, bool cheap, bool streak = false,
        Vector3 flow = default)
    {
        float billowBlocks = MathF.Max(1f, d.Billow);
        float cellsPerBlock = 1f / billowBlocks;
        Vector3 world = fogWorld + new Vector3(d.DriftBlocks, 0f, d.DriftBlocks * -0.67f);
        var n = new FogNoise();
        var broadCells = world * new Vector3(cellsPerBlock * 0.55f, cellsPerBlock * 0.15f,
            cellsPerBlock * 0.55f);
        n.Broad = Noise(broadCells, NoiseLod(pixelBlocks, cellsPerBlock * 0.55f));
        Vector3 wind = Vector3.Normalize(new Vector3(1f, 0f, -0.67f));
        var crosswind = new Vector3(-wind.Z, 0f, wind.X);
        Vector3 swirled = world + new Vector3(n.Broad.Y - 0.5f, 0f, n.Broad.Z - 0.5f) * (billowBlocks * 1.1f);
        Vector3 q = new Vector3(Vector3.Dot(swirled, wind) * 0.5f, swirled.Y * 2.2f,
            Vector3.Dot(swirled, crosswind)) * cellsPerBlock;
        n.BaseLod = NoiseLod(pixelBlocks, cellsPerBlock);
        float body = cheap ? Noise(q, n.BaseLod).X * 0.9f + 0.05f : Fbm(q, n.BaseLod);
        n.Body = SmoothStep(0.18f, 0.82f, body);
        n.Thread = 0.5f;
        n.DetailWeight = 0f;
        if (!cheap)
        {
            float detailLod = NoiseLod(pixelBlocks, cellsPerBlock * 3.3f);
            n.DetailWeight = 1f - SmoothStep(-0.3f, 1.4f, detailLod);
            if (n.DetailWeight > 0f)
                n.Thread = Noise(q * new Vector3(3.3f, 2.7f, 3.3f) + new Vector3(5.2f, 1.7f, 9.4f),
                    detailLod).W;
        }
        n.Streak = 0.5f;
        n.Tongue = n.Broad.X;
        if (streak)
        {
            float streakBlocks = MathF.Max(4f, billowBlocks * 0.6f);
            float streakLod = NoiseLod(pixelBlocks, 1f / streakBlocks);
            float cycleBlocks = K("CurtainFlowCycleBlocks");
            float cycle = d.FallBlocks / cycleBlocks;
            float phaseA = cycle - MathF.Floor(cycle);
            float phaseB = (cycle + 0.5f) - MathF.Floor(cycle + 0.5f);
            float a = StreakNoise(fogWorld - flow * (phaseA * cycleBlocks), streakBlocks, streakLod);
            float b = StreakNoise(fogWorld - flow * (phaseB * cycleBlocks) + new Vector3(31.7f, 11.3f, 23.9f),
                streakBlocks, streakLod);
            float weightA = 1f - MathF.Abs(2f * phaseA - 1f);
            float weightB = 1f - weightA;
            float blended = 0.5f + (weightA * (a - 0.5f) + weightB * (b - 0.5f))
                / MathF.Sqrt(MathF.Max(0.0001f, weightA * weightA + weightB * weightB));
            n.Streak = SmoothStep(K("CurtainStreakLow"), K("CurtainStreakHigh"), blended);
            n.Tongue = Noise(fogWorld * new Vector3(cellsPerBlock * 0.55f, cellsPerBlock * 0.15f,
                cellsPerBlock * 0.55f) + new Vector3(5.9f, 2.3f, 8.1f),
                NoiseLod(pixelBlocks, cellsPerBlock * 0.55f)).X;
        }
        return n;
    }

    /// <summary>vhFogShape.</summary>
    void Shape(float signedDistance, float weight, float height, float stripHeight, float wallDepth,
        in FogNoise n, out float bankExtinction, out float stripExtinction, out float stripAbove,
        out float top, out float curtainExtinction)
    {
        float lowerFade = MathF.Max(2f, MathF.Min(10f, d.Floor * 0.35f + 2f));
        float beyond = MathF.Max(0f, MathF.Abs(signedDistance) - coreBlocks);
        float wanderBlocks = Clamp01(d.Softness) * 0.5f * d.Spill;
        float rim = MathF.Max(0f, beyond + (n.Broad.X - 0.5f) * 1.8f * wanderBlocks);
        float side = signedDistance < 0f ? d.Spill : d.Spill * K("FarseerReachScale");
        float cut = 1f - SmoothStep(side, side * 1.5f, rim);
        float envelope = MathF.Exp(-rim / MathF.Max(1f, side * K("ShoulderFraction"))) * weight * cut;

        top = d.Height * (0.45f + 0.55f * envelope) * (0.7f + 0.6f * n.Broad.W);
        float hs = MathF.Max(0f, height);
        float scaleHeight = MathF.Max(1f, K("ScaleHeightFraction") * top);
        float heightDensity = MathF.Exp(-MathF.Max(0f, hs - K("GroundLayerFraction") * top) / scaleHeight);
        float under = SmoothStep(-d.Floor, -d.Floor + lowerFade, height);
        float ceiling = 1f - SmoothStep(0.35f * top, 1.1f * top, hs);

        float coverage = envelope * ceiling;
        float threshold = 1f - coverage * Mix(1f, 1.7f, Clamp01(d.Coverage));
        float density = Clamp01((n.Body - threshold) / MathF.Max(0.001f, 1f - threshold));
        density = Mix(density, ExpectedDensity(threshold), SmoothStep(1f, 3f, n.BaseLod));
        if (n.DetailWeight > 0f)
        {
            float ridge = 1f - MathF.Abs(2f * n.Thread - 1f);
            float erosion = Clamp01(d.Softness) * 0.8f * ridge * n.DetailWeight * (1f - 0.7f * density);
            density = Clamp01((density - erosion) / MathF.Max(0.001f, 1f - erosion));
        }
        density *= MathF.Pow(envelope, 0.75f);

        float shoulders = d.Spill * (1f + K("FarseerReachScale"));
        float bank = K("BankCrossingOpticalDepth")
            / (K("BankMeanDensity") * (2f * coreBlocks + K("ShoulderFraction") * shoulders))
            * density * heightDensity;
        float haze = K("HazeCrossingOpticalDepth") / (2f * coreBlocks + K("HazeFraction") * shoulders)
            * MathF.Exp(-rim / MathF.Max(1f, side * K("HazeFraction"))) * weight * cut * heightDensity * ceiling;
        bankExtinction = (bank + haze) * MathF.Max(0f, d.Strength) * under;

        float falloff = K("StripFalloffBlocks");
        float stripRim = MathF.Max(0f, MathF.Abs(signedDistance) - stripHalfBlocks
            + ((n.Broad.X - 0.5f) + (n.Body - 0.5f)) * 2f * K("StripWanderBlocks"));
        float stripProfile = MathF.Exp(-stripRim / falloff)
            * Mix(1f, 0.15f + 0.85f * n.Body, SmoothStep(0f, falloff, stripRim))
            * (1f - SmoothStep(2f * falloff, 3.5f * falloff, stripRim));
        float stripTop = MathF.Max(0.5f, d.StripHeight)
            * (0.8f + 0.4f * n.Broad.W) * (0.8f + 0.4f * n.Body)
            * (1f - 0.4f * SmoothStep(0f, 3f * falloff, stripRim));
        float stripVertical = 1f - SmoothStep(0.55f * stripTop, 1.35f * stripTop, MathF.Max(0f, stripHeight));
        float stripUnder = SmoothStep(-d.Floor, -d.Floor + lowerFade, stripHeight);
        float stripDensity = K("StripOpticalDepth") / (K("StripColumnFraction") * stripTop)
            * stripProfile * stripUnder * (0.75f + 0.5f * n.Body)
            * weight * weight * MathF.Max(0f, d.SeamStrength);
        stripExtinction = stripDensity * stripVertical;
        stripAbove = stripDensity * MathF.Max(0f, K("StripColumnFraction") * stripTop - MathF.Max(0f, stripHeight));

        curtainExtinction = 0f;
        if (curtain && height < K("CurtainLipRiseBlocks"))
        {
            float cf = K("CurtainFalloffBlocks");
            float lip = K("CurtainLipBlocks") * MathF.Exp(-MathF.Max(0f, -height) / K("CurtainLipDepthBlocks"));
            float tongue = n.Tongue * 2f * K("CurtainWanderBlocks");
            float curtainRim = MathF.Max(0f, signedDistance - stripHalfBlocks - lip - tongue);
            float lateral = MathF.Exp(-curtainRim / cf)
                * Mix(1f, n.Streak, SmoothStep(0f, cf, curtainRim))
                * (1f - SmoothStep(2f * cf, 3.5f * cf, curtainRim))
                * SmoothStep(-stripHalfBlocks - K("CurtainInnerFadeBlocks"), -stripHalfBlocks, signedDistance);
            float vertical = (1f - SmoothStep(0f, K("CurtainLipRiseBlocks"), height))
                * SmoothStep(-wallDepth - K("CurtainBottomFadeBlocks"), -wallDepth, height);
            curtainExtinction = K("CurtainExtinction") * lateral * vertical * (0.75f + 0.5f * n.Streak)
                * weight * weight * MathF.Max(0f, d.SeamStrength);
        }
    }

    float CurtainGap(float signedDistance, float curtainReach) => !curtain ? 1e6f
        : MathF.Max(signedDistance - curtainReach,
            -signedDistance - stripHalfBlocks - K("CurtainInnerFadeBlocks"));

    float FloorBelow(float curtainGap, float ground, float bankFloor) => curtainGap > 0f ? bankFloor
        : MathF.Max(bankFloor, ground - Scene.WallFootY + K("CurtainBottomFadeBlocks"));

    float StripGround(float sampledGround, float landing) =>
        sampledGround + Math.Clamp(landing - sampledGround,
            -K("StripAnchorReachBlocks"), K("StripAnchorReachBlocks"));

    /// <summary>
    /// main(): the camera is in world coordinates; the field is unbounded. <paramref name="lands"/>
    /// says the ray ends on a surface rather than the sky; <paramref name="anchor"/> false
    /// removes the strip's footing on that surface (the mock-up's control for it).
    /// </summary>
    public Fog March(Vector3 cam, Vector3 rayDirection, float endpointDistance, bool lands,
        int px, int py, float pixelScale, FogFields fields, bool anchor = true)
    {
        float stripDepth = MathF.Max(0.5f, d.StripHeight);
        float reach = K("StripAnchorReachBlocks");
        float bankCeiling = MathF.Max(d.Height * 1.5f, reach + stripDepth * 1.95f);
        float bankFloor = d.Floor + reach;
        float fieldMinY = Scene.GroundY - bankFloor - 4f;
        if (curtain) fieldMinY = MathF.Min(fieldMinY, Scene.WallFootY - K("CurtainBottomFadeBlocks") - 4f);
        float fieldMaxY = Scene.GroundY + bankCeiling + 4f;
        float marchStart = 0f, marchEnd = endpointDistance;
        if (!Slab(cam.Y, rayDirection.Y, fieldMinY, fieldMaxY, ref marchStart, ref marchEnd)
            || marchEnd <= MathF.Max(0f, marchStart)) return default;
        Vector3 landing = cam + rayDirection * endpointDistance;
        lands &= anchor;

        float baseStep = Mix(6f, 2.5f, Clamp01(d.Quality));
        float rayDistance = MathF.Max(0f, marchStart)
            + baseStep * Hash(new Vector3(px + 0.5f, py + 0.5f, 0.731f));
        float transmittance = 1f;
        Vector3 scattered = Vector3.Zero;
        float stripReach = stripHalfBlocks + 2f * K("StripWanderBlocks") + 3.5f * K("StripFalloffBlocks");
        float curtainReach = curtain ? stripHalfBlocks + K("CurtainLipBlocks")
            + 2f * K("CurtainWanderBlocks") + 3.5f * K("CurtainFalloffBlocks") : 0f;
        float conservativeReach = MathF.Max(MathF.Max(coreBlocks + d.Spill * K("FarseerReachScale") * 1.5f
            + Clamp01(d.Softness) * 0.5f * d.Spill, stripReach), curtainReach) + 8f * 1.5f;
        float wallDepth = Scene.GroundY - Scene.WallFootY;
        bool residualActive = fields.Residual != null && fields.ResidualWeight > 0f;
        float residualWeight = Clamp01(fields.ResidualWeight);

        Vector3 sunDirection = Vector3.Normalize(new Vector3(Scene.SunDirection.X,
            MathF.Max(Scene.SunDirection.Y, 0.08f), Scene.SunDirection.Z));
        float sunHeight = MathF.Max(0.25f, sunDirection.Y);
        float daylight = SmoothStep(0.02f, 0.35f, Scene.DayLight);
        float phase = Mix(1f, Phase(Vector3.Dot(rayDirection, sunDirection), 0.3f), 0.3f);
        Vector3 sunTint = Scene.SunColour / MathF.Max(0.001f,
            MathF.Max(Scene.SunColour.X, MathF.Max(Scene.SunColour.Y, Scene.SunColour.Z)));
        sunTint = Vector3.Lerp(Vector3.One, sunTint, 0.6f * daylight);
        float previousExtinction = 0f;
        Vector3 litColour = Scene.FogColour * 0.85f;
        int maxSteps = (int)K("MaxFogSteps");

        for (int stepIndex = 0; stepIndex < maxSteps; stepIndex++)
        {
            if (rayDistance >= marchEnd || transmittance < 0.01f) break;
            Vector3 fieldPosition = cam + rayDirection * rayDistance;
            float signedDistance = fields.Current(fieldPosition.X, fieldPosition.Z);
            float nearest = MathF.Abs(signedDistance);
            float stripBand = MathF.Abs(signedDistance) - stripReach;
            float curtainGap = CurtainGap(signedDistance, curtainReach);
            if (residualActive)
            {
                float oldSigned = fields.Residual!(fieldPosition.X, fieldPosition.Z);
                float old = MathF.Abs(oldSigned);
                nearest = MathF.Min(nearest, old);
                stripBand = MathF.Min(stripBand, old - stripReach);
                curtainGap = MathF.Min(curtainGap, CurtainGap(oldSigned, curtainReach));
            }
            float horizontalOutside = MathF.Max(0f, nearest - conservativeReach);
            float heightAboveGround = fieldPosition.Y - Scene.GroundY;
            float verticalOutside = MathF.Max(0f, MathF.Max(
                -FloorBelow(curtainGap, Scene.GroundY, bankFloor) - heightAboveGround,
                heightAboveGround - bankCeiling));
            float verticalSkip = verticalOutside * 0.35f;
            if (curtainGap > 0f) verticalSkip = MathF.Min(verticalSkip, curtainGap);
            float outside = MathF.Max(horizontalOutside, verticalSkip);
            float farStep = MathF.Max(baseStep, rayDistance * K("FarStepFraction"));
            float stepBlocks = outside > 0f
                ? Math.Clamp(outside * 0.72f, baseStep, 512f)
                : previousExtinction > 0f
                    ? Math.Clamp(0.35f / previousExtinction, 0.75f, farStep) : farStep;
            if (outside <= 0f && stripBand <= 0f)
            {
                float descent = MathF.Max(0.02f, -rayDirection.Y);
                float toLandingNow = new Vector2(fieldPosition.X - landing.X, fieldPosition.Z - landing.Z).Length();
                float landingNow = lands
                    ? 1f - SmoothStep(K("StripAnchorNearBlocks"), K("StripAnchorFarBlocks"), toLandingNow) : 0f;
                float stripHeightNow = fieldPosition.Y
                    - Mix(Scene.GroundY, StripGround(Scene.GroundY, landing.Y), landingNow);
                float stripRoom = MathF.Max(0f, stripHeightNow - stripDepth * 1.95f);
                stepBlocks = MathF.Min(stepBlocks, MathF.Max(0.3f, (stripRoom + 0.35f * stripDepth) / descent));
            }
            stepBlocks = MathF.Min(stepBlocks, marchEnd - rayDistance);
            previousExtinction = 0f;

            if (horizontalOutside <= 0f && verticalOutside <= 0f)
            {
                float middle = rayDistance + stepBlocks * 0.5f;
                fieldPosition = cam + rayDirection * middle;
                signedDistance = fields.Current(fieldPosition.X, fieldPosition.Z);
                float ground = Scene.GroundY;
                heightAboveGround = fieldPosition.Y - ground;
                float toLanding = new Vector2(fieldPosition.X - landing.X, fieldPosition.Z - landing.Z).Length();
                float landingWeight = lands
                    ? 1f - SmoothStep(K("StripAnchorNearBlocks"), K("StripAnchorFarBlocks"), toLanding) : 0f;
                float stripHeight = fieldPosition.Y
                    - Mix(ground, StripGround(ground, landing.Y), landingWeight);
                float pixelBlocks = MathF.Max(0.001f, middle * pixelScale);
                bool wantStreak = curtain
                    && heightAboveGround < K("CurtainLipRiseBlocks") + K("StripAnchorReachBlocks");
                Vector3 flow = -Vector3.UnitY;
                if (wantStreak)
                {
                    var outward = new Vector2(fields.Current(fieldPosition.X + 1f, fieldPosition.Z) - signedDistance,
                        fields.Current(fieldPosition.X, fieldPosition.Z + 1f) - signedDistance);
                    outward = outward.Length() > 0.00001f ? Vector2.Normalize(outward) : Vector2.Zero;
                    float over = 1f - SmoothStep(-K("CurtainFlowTurnBlocks"), K("CurtainLipRiseBlocks"), heightAboveGround);
                    flow = Vector3.Normalize(new Vector3(outward.X * (1f - over), -MathF.Max(over, 0.2f),
                        outward.Y * (1f - over)));
                }
                FogNoise n = SampleNoise(fieldPosition, pixelBlocks, false, wantStreak, flow);
                Shape(signedDistance, 1f, heightAboveGround, stripHeight, wallDepth, n,
                    out float bank, out float strip, out float stripAbove, out float top,
                    out float curtainHere);
                if (residualActive)
                {
                    Shape(fields.Residual!(fieldPosition.X, fieldPosition.Z), residualWeight,
                        heightAboveGround, stripHeight, wallDepth, n, out float oldBank, out float oldStrip,
                        out float oldAbove, out float oldTop, out float oldCurtain);
                    if (oldBank > bank) top = oldTop;
                    bank = MathF.Max(bank, oldBank);
                    strip = MathF.Max(strip, oldStrip);
                    stripAbove = MathF.Max(stripAbove, oldAbove);
                    curtainHere = MathF.Max(curtainHere, oldCurtain);
                }
                float extinction = bank + strip + curtainHere;
                previousExtinction = extinction;
                if (extinction > 0.0000001f)
                {
                    float segmentAlpha = 1f - MathF.Exp(-extinction * stepBlocks);
                    float contribution = transmittance * segmentAlpha;
                    transmittance *= 1f - segmentAlpha;
                    if (contribution >= 0.0005f)
                    {
                        float sunk = curtain ? 1f - SmoothStep(-4f, 0f, heightAboveGround) : 0f;
                        float stripShade = stripAbove * 0.5f / sunHeight * (1f - sunk);
                        float sunProbe = K("SunProbeBlocks");
                        Vector3 probe = fieldPosition + sunDirection * sunProbe;
                        FogNoise pn = SampleNoise(probe, pixelBlocks, true);
                        float probeHeight = probe.Y - Scene.GroundY;
                        float probeSigned = fields.Current(probe.X, probe.Z);
                        Shape(probeSigned, 1f, probeHeight, probeHeight, wallDepth, pn,
                            out float probeBank, out float probeStrip, out _, out _, out float probeCurtain);
                        if (residualActive)
                        {
                            Shape(fields.Residual!(probe.X, probe.Z), residualWeight, probeHeight,
                                probeHeight, wallDepth, pn, out float oldBank, out float oldStrip, out _, out _,
                                out float oldCurtain);
                            probeBank = MathF.Max(probeBank, oldBank);
                            probeStrip = MathF.Max(probeStrip, oldStrip);
                            probeCurtain = MathF.Max(probeCurtain, oldCurtain);
                        }
                        float rock = curtain
                            ? SmoothStep(0f, stripHalfBlocks, -probeSigned) * SmoothStep(0f, 4f, -probeHeight) : 0f;
                        float curtainShare = MathF.Max(curtainHere / extinction, sunk);
                        float towardSun = (bank * 0.35f + probeBank
                                + probeStrip * Mix(1f, 0.15f, curtainShare) + 0.15f * probeCurtain)
                            * sunProbe * 1.6f + stripShade + 4f * rock
                            + K("CurtainGapShade") * (curtainHere / extinction) * (1f - n.Streak);
                        float direct = (MathF.Exp(-towardSun) + 0.6f * MathF.Exp(-towardSun * 0.25f)) / 1.6f;
                        float bankLift = Clamp01(MathF.Max(0f, heightAboveGround) / MathF.Max(1f, top));
                        float stripRise = (0.8f + 0.4f * n.Broad.W) * (0.8f + 0.4f * n.Body);
                        float curtainLift = Mix(0.05f, 1f, n.Streak);
                        float stripLift = Mix(Mix(0.25f, 0.75f, Clamp01((stripRise - 0.64f) / 0.8f)),
                            curtainLift, sunk);
                        float lift = (bank * bankLift + strip * stripLift + curtainHere * curtainLift)
                            / extinction;
                        float sky = Mix(0.55f, 1f, lift);
                        float sunlit = 0.62f * direct * phase + 0.38f * sky;
                        float light = Math.Clamp(Mix(sky, sunlit, daylight), 0f, 1.2f);
                        Vector3 tint = Vector3.Lerp(new Vector3(0.90f, 0.94f, 1.04f), sunTint, Clamp01(light));
                        litColour = Scene.FogColour * Mix(0.64f, 0.93f, light) * tint;
                    }
                    scattered += litColour * contribution;
                }
            }
            rayDistance += MathF.Max(0.25f, stepBlocks);
        }
        float opacity = 1f - transmittance;
        float alpha = MathF.Min(opacity, 0.99f);
        if (alpha < 0.002f) return default;
        return new Fog(scattered / MathF.Max(opacity, 0.0001f), alpha);
    }

    internal static float Hash(Vector3 p)
    {
        static float Fract(float x) => x - MathF.Floor(x);
        p = new Vector3(Fract(p.X * 0.1031f), Fract(p.Y * 0.1031f), Fract(p.Z * 0.1031f));
        float dot = p.X * (p.Y + 33.33f) + p.Y * (p.Z + 33.33f) + p.Z * (p.X + 33.33f);
        p += new Vector3(dot);
        return Fract((p.X + p.Y) * p.Z);
    }

    internal static bool Slab(float origin, float direction, float min, float max,
        ref float near, ref float far)
    {
        if (MathF.Abs(direction) < 0.000001f) return origin >= min && origin <= max;
        float a = (min - origin) / direction, b = (max - origin) / direction;
        if (a > b) (a, b) = (b, a);
        near = MathF.Max(near, a);
        far = MathF.Min(far, b);
        return near <= far;
    }
}
