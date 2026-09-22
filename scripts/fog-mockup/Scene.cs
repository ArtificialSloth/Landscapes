using System.Numerics;

namespace VintageHorizons.Tools;

/// <summary>
/// A test landscape at y = 100 with an exact 32-block ownership staircase: real (LOD) terrain
/// on the low-z side, Farseer on the high-z side. The fog field is analytic here - the signed
/// lateral distance to the smooth curve the staircase was quantized from - where the game traces,
/// simplifies and rounds the staircase itself; both stray from it by a similar amount. The seam
/// ground the fog samples is always y = 100.
///
/// Farseer hills rise from 90 blocks past the seam, so a view across the bank shows whether the
/// terrain behind it can be made out. <see cref="Ramp"/> tilts the ground across the seam, so the
/// real ground at the staircase is up to that many blocks off the sampled seam height, as on a
/// hillside. <see cref="Shift"/> moves the ownership frontier toward Farseer, as when cached
/// terrain loads in; the fog's current field follows it and the old one is the residual.
/// Colours are display values sampled from the owner's screenshots.
/// </summary>
static class Scene
{
    public const float GroundY = 100f;
    public const float FarViewDistance = 4096f;
    public const float HillHeight = 55f;
    public static readonly Vector3 FogColour = new(0.935f, 0.945f, 0.93f);
    public static readonly Vector3 SunDirection = Vector3.Normalize(new Vector3(0.5f, 0.64f, -0.58f));
    public static readonly Vector3 SunColour = new(1f, 0.97f, 0.92f);
    public const float DayLight = 1f;

    /// <summary>Blocks the ground rises across the seam, toward Farseer.</summary>
    public static float Ramp;

    /// <summary>Blocks the ownership frontier has moved toward Farseer.</summary>
    public static float Shift;

    public static float CentreZ(float x) =>
        520f + 70f * MathF.Sin(x / 230f) + 30f * MathF.Sin(x / 83f + 1.3f);

    static float Slope(float x) =>
        70f / 230f * MathF.Cos(x / 230f) + 30f / 83f * MathF.Cos(x / 83f + 1.3f);

    /// <summary>Signed lateral distance to the original smooth centreline; Farseer positive.</summary>
    public static float Signed(float x, float z)
    {
        float s = Slope(x);
        return (z - CentreZ(x)) / MathF.Sqrt(1f + s * s);
    }

    /// <summary>The same for the frontier after <see cref="Shift"/>.</summary>
    public static float SignedNow(float x, float z) => Signed(x, z - Shift);

    /// <summary>Exact ownership: whole 32-block cells, so the handoff is a staircase.</summary>
    public static bool Farseer(float x, float z)
    {
        float cx = MathF.Floor(x / 32f), cz = MathF.Floor(z / 32f);
        return cz * 32f + 16f > CentreZ(cx * 32f + 16f) + Shift;
    }

    /// <summary>
    /// The staircase's largest lateral distance from the centreline over a span, measured the way
    /// the field builder does: at every edge vertex and midpoint.
    /// </summary>
    public static float StaircaseDeviation(float minX, float maxX)
    {
        float worst = 0f;
        for (float x = MathF.Floor(minX / 32f) * 32f; x <= maxX; x += 16f)
        {
            float cz = MathF.Floor(CentreZ(x) / 32f) * 32f;
            for (float z = cz - 96f; z <= cz + 96f; z += 16f)
            {
                bool here = Farseer(x + 0.5f, z + 0.5f);
                if (here != Farseer(x - 0.5f, z + 0.5f) || here != Farseer(x + 0.5f, z - 0.5f))
                    worst = MathF.Max(worst, MathF.Abs(Signed(x, z)));
            }
        }
        return worst;
    }

    /// <summary>Terrain height: the ramp across the seam plus Farseer's hills.</summary>
    public static float Height(float x, float z)
    {
        float signed = SignedNow(x, z);
        float h = GroundY + Ramp * MathF.Tanh(signed / 30f);
        float hills = SmoothStep(90f, 220f, signed);
        if (hills > 0f)
        {
            float n = LodSilhouetteFogNoise.ValueNoise(x / 140f, 7.3f, z / 140f, 3) * 0.7f
                + LodSilhouetteFogNoise.ValueNoise(x / 55f, 2.1f, z / 55f, 1) * 0.3f;
            h += HillHeight * hills * SmoothStep(0.25f, 0.85f, n);
        }
        return h;
    }

    /// <summary>
    /// Distance along a ray to the terrain, or infinity. The flat stretch is exact; hills and
    /// the ramp are marched then bisected.
    /// </summary>
    public static float Intersect(Vector3 origin, Vector3 dir, float maxDistance)
    {
        float top = GroundY + MathF.Abs(Ramp) + HillHeight + 1f;
        float bottom = GroundY - MathF.Abs(Ramp) - 1f;
        float t = 0f, end = maxDistance;
        if (!ShippedFog.Slab(origin.Y, dir.Y, bottom, top, ref t, ref end)) return float.PositiveInfinity;
        t = MathF.Max(0f, t);
        float previous = t;
        while (t <= end)
        {
            Vector3 p = origin + dir * t;
            float above = p.Y - Height(p.X, p.Z);
            if (above <= 0f)
            {
                float lo = previous, hi = t;
                for (int i = 0; i < 24; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    Vector3 m = origin + dir * mid;
                    if (m.Y - Height(m.X, m.Z) > 0f) lo = mid; else hi = mid;
                }
                return hi;
            }
            previous = t;
            t += Math.Clamp(above * 0.6f, 0.2f + t * 0.001f, 48f);
        }
        return float.PositiveInfinity;
    }

    public static Vector3 Ground(float x, float z, float distance)
    {
        Vector3 c;
        if (Farseer(x, z))
        {
            float n = LodSilhouetteFogNoise.ValueNoise(x / 90f, 3.7f, z / 90f, 0);
            c = new Vector3(0.60f, 0.62f, 0.71f) * (0.96f + 0.06f * n);
        }
        else
        {
            float block = LodSilhouetteFogNoise.ValueNoise(MathF.Floor(x) + 0.5f, 1.5f, MathF.Floor(z) + 0.5f, 1);
            float patch = LodSilhouetteFogNoise.ValueNoise(x / 24f, 5.1f, z / 24f, 2);
            c = new Vector3(0.37f, 0.44f, 0.31f) * (0.88f + 0.10f * patch + 0.06f * block);
        }
        // Hills and slopes are sunlit on one face and shaded on the other.
        const float e = 1.5f;
        var normal = Vector3.Normalize(new Vector3(Height(x - e, z) - Height(x + e, z), 2f * e,
            Height(x, z - e) - Height(x, z + e)));
        float lambert = MathF.Max(0f, Vector3.Dot(normal, SunDirection));
        float flat = MathF.Max(0f, SunDirection.Y);
        c *= 0.55f + 0.45f * lambert / flat;
        float haze = 1f - MathF.Exp(-distance / 3500f);
        return Vector3.Lerp(c, FogColour * 0.96f, haze * 0.85f);
    }

    public static Vector3 Sky(Vector3 dir) =>
        Vector3.Lerp(FogColour, new Vector3(0.52f, 0.64f, 0.85f), SmoothStep(0f, 0.35f, dir.Y));

    public static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}

/// <summary>A camera; yaw 0 looks toward +z (from real terrain toward Farseer).</summary>
record View(string Name, Vector3 Camera, float YawDeg, float PitchDeg, float VFovDeg);
