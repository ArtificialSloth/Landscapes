using System.Numerics;

namespace VintageHorizons.Tools;

/// <summary>
/// The shader's fogNoise sampler in software: the mod's baked texels, box-filtered mipmaps like
/// glGenerateMipmap, repeat wrapping, trilinear filtering within a level and linear between
/// levels (GL_LINEAR_MIPMAP_LINEAR). textureLod(fogNoise, coord, lod) is Sample(coord, lod).
/// </summary>
sealed class NoiseTexture
{
    readonly List<(int Size, float[] Texels)> levels = new();

    public NoiseTexture()
    {
        int size = LodSilhouetteFogNoise.Size;
        byte[] bytes = LodSilhouetteFogNoise.Texels;
        var level = new float[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) level[i] = bytes[i] / 255f;
        levels.Add((size, level));
        while (size > 1)
        {
            int next = size / 2;
            var down = new float[next * next * next * 4];
            for (int z = 0; z < next; z++)
            for (int y = 0; y < next; y++)
            for (int x = 0; x < next; x++)
            for (int c = 0; c < 4; c++)
            {
                float sum = 0f;
                for (int dz = 0; dz < 2; dz++)
                for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                    sum += level[(((z * 2 + dz) * size + y * 2 + dy) * size + x * 2 + dx) * 4 + c];
                down[((z * next + y) * next + x) * 4 + c] = sum / 8f;
            }
            levels.Add((next, down));
            level = down;
            size = next;
        }
    }

    public Vector4 Sample(Vector3 coord, float lod)
    {
        lod = Math.Clamp(lod, 0f, levels.Count - 1);
        int low = (int)MathF.Floor(lod);
        int high = Math.Min(low + 1, levels.Count - 1);
        float t = lod - low;
        Vector4 a = Trilinear(levels[low], coord);
        return t <= 0f || high == low ? a : Vector4.Lerp(a, Trilinear(levels[high], coord), t);
    }

    static Vector4 Trilinear((int Size, float[] Texels) level, Vector3 coord)
    {
        int size = level.Size;
        float px = coord.X * size - 0.5f, py = coord.Y * size - 0.5f, pz = coord.Z * size - 0.5f;
        int x0 = (int)MathF.Floor(px), y0 = (int)MathF.Floor(py), z0 = (int)MathF.Floor(pz);
        float fx = px - x0, fy = py - y0, fz = pz - z0;
        Vector4 result = Vector4.Zero;
        for (int dz = 0; dz < 2; dz++)
        for (int dy = 0; dy < 2; dy++)
        for (int dx = 0; dx < 2; dx++)
        {
            float w = (dx == 0 ? 1f - fx : fx) * (dy == 0 ? 1f - fy : fy) * (dz == 0 ? 1f - fz : fz);
            if (w == 0f) continue;
            int x = Wrap(x0 + dx, size), y = Wrap(y0 + dy, size), z = Wrap(z0 + dz, size);
            int at = ((z * size + y) * size + x) * 4;
            float[] t = level.Texels;
            result += w * new Vector4(t[at], t[at + 1], t[at + 2], t[at + 3]);
        }
        return result;
    }

    static int Wrap(int value, int size) => ((value % size) + size) % size;
}
