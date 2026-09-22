using System.Numerics;

namespace VintageHorizons.Tools;

/// <summary>
/// The 0.4.124 seam fog (commit 1fb10f5), frozen as the before picture for 0.4.125: a tall
/// noisy bank with a full-height seal over the core, both densities blended across the
/// shoulder. Its constants are the 0.4.124 shader's. Kept only as a comparison column.
/// </summary>
sealed class Fog0124
{
    readonly Dictionary<string, float> k;
    readonly FogDials d;
    readonly NoiseTexture noise;
    readonly float coreBlocks;

    static readonly Dictionary<string, float> Constants = new()
    {
        ["SealOpticalDepth"] = 3.5f, ["BankOpticalDepth"] = 3.2f, ["HazeOpticalDepth"] = 0.25f,
        ["BankMeanDensity"] = 0.6f, ["ShoulderFraction"] = 0.4f, ["HazeFraction"] = 0.6f,
        ["SealFalloffFraction"] = 0.3f, ["FarseerReachScale"] = 1.6f,
        ["GroundLayerFraction"] = 0.3f, ["ScaleHeightFraction"] = 0.42f,
        ["SealColumnFraction"] = 0.5923f, ["BankColumnFraction"] = 0.5548f,
        ["NoisePeriod"] = 16f, ["NoiseTexelsPerCell"] = 4f, ["NoiseFlatLod"] = 5f,
        ["SunProbeBlocks"] = 14f, ["FarStepFraction"] = 0.02f, ["MaxFogSteps"] = 192f,
    };

    public Fog0124(FogDials dials, NoiseTexture noise, float coreBlocks)
    {
        k = Constants;
        d = dials;
        this.noise = noise;
        this.coreBlocks = coreBlocks;
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

    /// <summary>vhFogExtinction. The field is analytic: signed distance, ground, end fade 1.</summary>
    float Extinction(Vector3 fogWorld, float signedDistance, float height, float pixelBlocks,
        bool cheap, out float top)
    {
        float billowBlocks = MathF.Max(1f, d.Billow);
        float cellsPerBlock = 1f / billowBlocks;
        Vector3 world = fogWorld + new Vector3(d.DriftBlocks, 0f, d.DriftBlocks * -0.67f);
        const float endFade = 1f;

        var broadCells = world * new Vector3(cellsPerBlock * 0.55f, cellsPerBlock * 0.15f,
            cellsPerBlock * 0.55f);
        Vector4 broad = Noise(broadCells, NoiseLod(pixelBlocks, cellsPerBlock * 0.55f));

        float beyond = MathF.Max(0f, MathF.Abs(signedDistance) - coreBlocks);
        float wanderBlocks = Clamp01(d.Softness) * 0.5f * d.Spill;
        float rim = MathF.Max(0f, beyond + (broad.X - 0.5f) * 1.8f * wanderBlocks);
        float side = signedDistance < 0f ? d.Spill : d.Spill * K("FarseerReachScale");
        float cut = 1f - SmoothStep(side, side * 1.5f, rim);
        float envelope = MathF.Exp(-rim / MathF.Max(1f, side * K("ShoulderFraction"))) * endFade * cut;

        top = d.Height * (0.45f + 0.55f * envelope) * (0.7f + 0.6f * broad.W);
        float hs = MathF.Max(0f, height);
        float scaleHeight = MathF.Max(1f, K("ScaleHeightFraction") * top);
        float heightDensity = MathF.Exp(-MathF.Max(0f, hs - K("GroundLayerFraction") * top) / scaleHeight);
        float lowerFade = MathF.Max(2f, MathF.Min(10f, d.Floor * 0.35f + 2f));
        float under = SmoothStep(-d.Floor, -d.Floor + lowerFade, height);
        float ceiling = 1f - SmoothStep(0.35f * top, 1.1f * top, hs);

        Vector3 wind = Vector3.Normalize(new Vector3(1f, 0f, -0.67f));
        var crosswind = new Vector3(-wind.Z, 0f, wind.X);
        Vector3 swirled = world + new Vector3(broad.Y - 0.5f, 0f, broad.Z - 0.5f) * (billowBlocks * 1.1f);
        Vector3 q = new Vector3(Vector3.Dot(swirled, wind) * 0.5f, swirled.Y * 2.2f,
            Vector3.Dot(swirled, crosswind)) * cellsPerBlock;
        float baseLod = NoiseLod(pixelBlocks, cellsPerBlock);
        float n = cheap ? Noise(q, baseLod).X * 0.9f + 0.05f : Fbm(q, baseLod);
        n = SmoothStep(0.18f, 0.82f, n);
        float coverage = envelope * ceiling;
        float threshold = 1f - coverage * Mix(1f, 1.7f, Clamp01(d.Coverage));
        float density = Clamp01((n - threshold) / MathF.Max(0.001f, 1f - threshold));
        density = Mix(density, ExpectedDensity(threshold), SmoothStep(1f, 3f, baseLod));

        if (!cheap)
        {
            float detailLod = NoiseLod(pixelBlocks, cellsPerBlock * 3.3f);
            float detailWeight = 1f - SmoothStep(-0.3f, 1.4f, detailLod);
            if (detailWeight > 0f)
            {
                float thread = Noise(q * new Vector3(3.3f, 2.7f, 3.3f) + new Vector3(5.2f, 1.7f, 9.4f),
                    detailLod).W;
                float ridge = 1f - MathF.Abs(2f * thread - 1f);
                float erosion = Clamp01(d.Softness) * 0.8f * ridge * detailWeight * (1f - 0.7f * density);
                density = Clamp01((density - erosion) / MathF.Max(0.001f, 1f - erosion));
            }
        }
        density *= MathF.Pow(envelope, 0.75f);

        float columnBlocks = MathF.Max(1f, top);
        float bank = K("BankOpticalDepth") / (K("BankColumnFraction") * columnBlocks * K("BankMeanDensity"))
            * density * heightDensity;
        float sealFalloff = MathF.Max(1f, K("SealFalloffFraction") * d.Spill);
        float sealProfile = MathF.Exp(-beyond / sealFalloff) * endFade;
        float sealBillows = Mix(1f, 0.3f + 0.7f * n, SmoothStep(0f, sealFalloff, beyond));
        float sealCeiling = 1f - SmoothStep(0.5f * top, 1.15f * top, hs);
        float seal = K("SealOpticalDepth") / (K("SealColumnFraction") * columnBlocks)
            * sealProfile * sealBillows * heightDensity * sealCeiling;
        float haze = K("HazeOpticalDepth") / (K("BankColumnFraction") * columnBlocks)
            * MathF.Exp(-rim / MathF.Max(1f, side * K("HazeFraction"))) * endFade * cut * heightDensity * ceiling;
        float seamWeight = MathF.Exp(-rim / MathF.Max(1f, side * K("ShoulderFraction")));
        float bankStrength = Mix(MathF.Max(0f, d.Strength), MathF.Max(0f, d.SeamStrength), seamWeight);
        return (MathF.Max(bank * bankStrength, seal * MathF.Max(0f, d.SeamStrength))
            + haze * MathF.Max(0f, d.Strength)) * under;
    }

    /// <summary>main(): the camera is in world coordinates; the field is unbounded.</summary>
    public Fog March(Vector3 cam, Vector3 rayDirection, float endpointDistance, int px, int py,
        float pixelScale)
    {
        float fieldMinY = Scene.GroundY - d.Floor - 4f;
        float fieldMaxY = Scene.GroundY + d.Height * 1.5f + 4f;
        float marchStart = 0f, marchEnd = endpointDistance;
        if (!Slab(cam.Y, rayDirection.Y, fieldMinY, fieldMaxY, ref marchStart, ref marchEnd)
            || marchEnd <= MathF.Max(0f, marchStart)) return default;

        float baseStep = Mix(6f, 2.5f, Clamp01(d.Quality));
        float rayDistance = MathF.Max(0f, marchStart)
            + baseStep * Hash(new Vector3(px + 0.5f, py + 0.5f, 0.731f));
        float transmittance = 1f;
        Vector3 scattered = Vector3.Zero;
        float conservativeReach = coreBlocks + d.Spill * K("FarseerReachScale") * 1.5f
            + Clamp01(d.Softness) * 0.5f * d.Spill + 8f * 1.5f;
        float bankCeiling = d.Height * 1.5f;

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
            float signedDistance = Scene.Signed(fieldPosition.X, fieldPosition.Z);
            float horizontalOutside = MathF.Max(0f, MathF.Abs(signedDistance) - conservativeReach);
            float heightAboveGround = fieldPosition.Y - Scene.GroundY;
            float verticalOutside = MathF.Max(0f, MathF.Max(-d.Floor - heightAboveGround,
                heightAboveGround - bankCeiling));
            float outside = MathF.Max(horizontalOutside, verticalOutside * 0.35f);
            float farStep = MathF.Max(baseStep, rayDistance * K("FarStepFraction"));
            float stepBlocks = outside > 0f
                ? Math.Clamp(outside * 0.72f, baseStep, 512f)
                : previousExtinction > 0f
                    ? Math.Clamp(0.35f / previousExtinction, 0.75f, farStep) : farStep;
            stepBlocks = MathF.Min(stepBlocks, marchEnd - rayDistance);
            previousExtinction = 0f;

            if (horizontalOutside <= 0f && verticalOutside <= 0f)
            {
                float middle = rayDistance + stepBlocks * 0.5f;
                fieldPosition = cam + rayDirection * middle;
                signedDistance = Scene.Signed(fieldPosition.X, fieldPosition.Z);
                heightAboveGround = fieldPosition.Y - Scene.GroundY;
                float pixelBlocks = MathF.Max(0.001f, middle * pixelScale);
                float extinction = Extinction(fieldPosition, signedDistance, heightAboveGround,
                    pixelBlocks, false, out float top);
                previousExtinction = extinction;
                if (extinction > 0.0000001f)
                {
                    float segmentAlpha = 1f - MathF.Exp(-extinction * stepBlocks);
                    float contribution = transmittance * segmentAlpha;
                    transmittance *= 1f - segmentAlpha;
                    if (contribution >= 0.0005f)
                    {
                    float sunProbe = K("SunProbeBlocks");
                    Vector3 probe = fieldPosition + sunDirection * sunProbe;
                    float probeExtinction = Extinction(probe, Scene.Signed(probe.X, probe.Z),
                        probe.Y - Scene.GroundY, pixelBlocks, true, out _);
                    float towardSun = (extinction * 0.35f + probeExtinction) * sunProbe * 1.6f;
                    float direct = (MathF.Exp(-towardSun) + 0.6f * MathF.Exp(-towardSun * 0.25f)) / 1.6f;
                    float sky = Mix(0.55f, 1f, Clamp01(MathF.Max(0f, heightAboveGround) / MathF.Max(1f, top)));
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

    static float Hash(Vector3 p) => ShippedFog.Hash(p);

    static bool Slab(float origin, float direction, float min, float max,
        ref float near, ref float far) => ShippedFog.Slab(origin, direction, min, max, ref near, ref far);
}
