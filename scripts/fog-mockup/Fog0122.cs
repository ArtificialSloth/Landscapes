using System.Numerics;

namespace VintageHorizons.Tools;

/// <summary>
/// The 0.4.122 seam fog (commit d46b776), frozen as the before picture: organic billows times a
/// crossing profile, plus a noise-free seal normalized to a sideways half-crossing, painted in
/// the raw horizon fog colour. Kept only as a comparison column.
/// </summary>
static class Fog0122
{
    const float Extinction = 0.018f;

    static float Crossing(FogDials s, float sd)
    {
        float lodPlateau = -MathF.Min(14f, s.Spill * 0.29f);
        float farseerPlateau = MathF.Min(32f, s.Spill * 0.67f);
        float farseerReach = s.Spill + MathF.Min(32f, s.Spill * 0.67f);
        float linear;
        if (sd < lodPlateau) linear = (sd + s.Spill) / (lodPlateau + s.Spill);
        else if (sd <= farseerPlateau) linear = 1f;
        else linear = (farseerReach - sd) / (farseerReach - farseerPlateau);
        return Math.Clamp(linear, 0f, 1f);
    }

    static float Seal(FogDials s, float sd)
    {
        float lodReach = Math.Clamp(s.Spill * 0.67f, 12f, 32f);
        float sideReach = sd < 0f ? lodReach : Math.Clamp(s.Spill, 12f, 48f);
        float profile = 1f - Scene.SmoothStep(0.08f, 1f, MathF.Abs(sd) / sideReach);
        float target = 3f + 1.5f * Math.Clamp(s.Coverage, 0f, 1f);
        return profile * target / (Extinction * 0.54f * lodReach);
    }

    static float Noise(Vector3 p)
    {
        float cx = MathF.Floor(p.X), cy = MathF.Floor(p.Y), cz = MathF.Floor(p.Z);
        float fx = p.X - cx, fy = p.Y - cy, fz = p.Z - cz;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);
        fz = fz * fz * (3f - 2f * fz);
        float H(float x, float y, float z) => ShippedFog.Hash(new Vector3(x, y, z));
        float n00 = Lerp(H(cx, cy, cz), H(cx + 1, cy, cz), fx);
        float n10 = Lerp(H(cx, cy + 1, cz), H(cx + 1, cy + 1, cz), fx);
        float n01 = Lerp(H(cx, cy, cz + 1), H(cx + 1, cy, cz + 1), fx);
        float n11 = Lerp(H(cx, cy + 1, cz + 1), H(cx + 1, cy + 1, cz + 1), fx);
        return Lerp(Lerp(n00, n10, fy), Lerp(n01, n11, fy), fz);
    }

    static float Fbm(Vector3 p)
    {
        float v = 0.57f * Noise(p);
        p = p * 2.03f + new Vector3(11.7f, 5.3f, 17.1f);
        v += 0.29f * Noise(p);
        p = p * 2.01f + new Vector3(3.1f, 19.4f, 7.8f);
        return v + 0.14f * Noise(p);
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public static Fog March(FogDials s, Vector3 cam, Vector3 dir, float endDist, int px, int py)
    {
        float start = 0f, end = endDist;
        if (!ShippedFog.Slab(cam.Y, dir.Y, Scene.GroundY - s.Floor - 4f, Scene.GroundY + s.Height + 4f,
                ref start, ref end) || end <= MathF.Max(0f, start)) return default;
        float baseStep = Lerp(7.5f, 3f, Math.Clamp(s.Quality, 0f, 1f));
        float rd = MathF.Max(0f, start) + baseStep * ShippedFog.Hash(new Vector3(px + 0.5f, py + 0.5f, 0.731f));
        float transmittance = 1f;
        Vector3 scattered = Vector3.Zero;
        float bs = 1f / MathF.Max(1f, s.Billow);
        var drift = new Vector3(s.DriftBlocks, 0f, s.DriftBlocks * -0.67f);
        float maxWobble = MathF.Min(18f, s.Spill * 0.35f);
        float reach = s.Spill + MathF.Min(32f, s.Spill * 0.67f) + maxWobble + 12f;
        for (int i = 0; i < 160; i++)
        {
            if (rd >= end || transmittance < 0.015f) break;
            Vector3 pos = cam + dir * rd;
            float r = Scene.Signed(pos.X, pos.Z);
            float horizontalOutside = MathF.Max(0f, MathF.Abs(r) - reach);
            float hag = pos.Y - Scene.GroundY;
            float verticalOutside = MathF.Max(0f, MathF.Max(-s.Floor - hag, hag - s.Height));
            float outside = MathF.Max(horizontalOutside, verticalOutside * 0.35f);
            float step = outside > 0f ? Math.Clamp(outside * 0.72f, baseStep, 512f) : baseStep;
            step = MathF.Min(step, end - rd);
            if (horizontalOutside <= 0f && verticalOutside <= 0f)
            {
                float lowerFade = MathF.Max(2f, MathF.Min(10f, s.Floor * 0.35f + 2f));
                float vertical = Scene.SmoothStep(-s.Floor, -s.Floor + lowerFade, hag)
                    * (1f - Scene.SmoothStep(s.Height * 0.55f, s.Height, hag));
                float broad = Fbm((pos + drift) * new Vector3(bs, bs * 1.4f, bs));
                float detail = Noise((pos + drift * 1.35f) * new Vector3(bs * 2.75f, bs * 3.6f, bs * 2.75f));
                float billow = Scene.SmoothStep(0.28f, 0.8f, broad * 0.74f + detail * 0.26f);
                Vector3 wp = (pos + drift * 0.8f) * bs;
                float wobble = (Noise(wp * 1.1f) - 0.5f) * 0.8f
                    + (Noise(wp * 2.6f + new Vector3(37.1f, 11.9f, 53.7f)) - 0.5f) * 0.8f;
                float crossing = Crossing(s, r + wobble * s.Softness * maxWobble);
                crossing = crossing * crossing * (3f - 2f * crossing);
                float noiseDensity = Lerp(0.1f + 0.5f * Math.Clamp(s.Coverage, 0f, 1f), 1.45f, billow);
                float density = vertical * MathF.Max(0f, s.Strength) * (crossing * noiseDensity + Seal(s, r));
                float a = 1f - MathF.Exp(-Extinction * density * step);
                scattered += Scene.FogColour * Lerp(0.92f, 1.035f, broad) * (transmittance * a);
                transmittance *= 1f - a;
            }
            rd += MathF.Max(0.25f, step);
        }
        float alpha = Math.Clamp(1f - transmittance, 0f, 0.985f);
        return alpha < 0.002f ? default : new Fog(scattered / MathF.Max(alpha, 0.0001f), alpha);
    }
}
