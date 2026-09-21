namespace VintageHorizons;

/// <summary>
/// The seam fog's noise, baked once into a small tiling 3D texture. Each of the four channels is
/// an independent smooth value noise with <see cref="PeriodCells"/> lattice cells per repeat,
/// stored at <see cref="TexelsPerCell"/> texels per cell so hardware trilinear filtering
/// reproduces the smooth interpolation. One texture fetch then replaces the eight hashes and
/// seven blends of a procedural lookup, and its mipmaps average detail that would otherwise
/// sparkle once it is smaller than a pixel. Dependency-free so the offline mock-up in
/// scripts/fog-mockup samples exactly the same texels the shader does.
/// </summary>
internal static class LodSilhouetteFogNoise
{
    public const int PeriodCells = 16;
    public const int TexelsPerCell = 4;
    public const int Size = PeriodCells * TexelsPerCell;
    public const int Channels = 4;

    static readonly Lazy<byte[]> texels = new(Generate);

    /// <summary>RGBA8 texels, x fastest, then y, then z. Computed once, on first use.</summary>
    public static byte[] Texels => texels.Value;

    static byte[] Generate()
    {
        var result = new byte[Size * Size * Size * Channels];
        for (int z = 0; z < Size; z++)
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            int at = ((z * Size + y) * Size + x) * Channels;
            for (int channel = 0; channel < Channels; channel++)
            {
                float value = ValueNoise(
                    (x + 0.5f) / TexelsPerCell, (y + 0.5f) / TexelsPerCell,
                    (z + 0.5f) / TexelsPerCell, channel);
                result[at + channel] = (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
            }
        }
        return result;
    }

    /// <summary>Smooth, tiling value noise in [0, 1] at lattice coordinates.</summary>
    internal static float ValueNoise(float px, float py, float pz, int channel)
    {
        int cx = (int)MathF.Floor(px), cy = (int)MathF.Floor(py), cz = (int)MathF.Floor(pz);
        float fx = Fade(px - cx), fy = Fade(py - cy), fz = Fade(pz - cz);
        float n000 = Lattice(cx, cy, cz, channel), n100 = Lattice(cx + 1, cy, cz, channel);
        float n010 = Lattice(cx, cy + 1, cz, channel), n110 = Lattice(cx + 1, cy + 1, cz, channel);
        float n001 = Lattice(cx, cy, cz + 1, channel), n101 = Lattice(cx + 1, cy, cz + 1, channel);
        float n011 = Lattice(cx, cy + 1, cz + 1, channel);
        float n111 = Lattice(cx + 1, cy + 1, cz + 1, channel);
        float n00 = Lerp(n000, n100, fx), n10 = Lerp(n010, n110, fx);
        float n01 = Lerp(n001, n101, fx), n11 = Lerp(n011, n111, fx);
        return Lerp(Lerp(n00, n10, fy), Lerp(n01, n11, fy), fz);
    }

    static float Lattice(int x, int y, int z, int channel)
    {
        uint h = (uint)Wrap(x) * 0x8DA6B343u ^ (uint)Wrap(y) * 0xD8163841u
            ^ (uint)Wrap(z) * 0xCB1AB31Fu ^ (uint)(channel + 1) * 0x9E3779B9u;
        h ^= h >> 16;
        h *= 0x7FEB352Du;
        h ^= h >> 15;
        h *= 0x846CA68Bu;
        h ^= h >> 16;
        return (h & 0xFFFFFF) / 16777215f;
    }

    static int Wrap(int value) => ((value % PeriodCells) + PeriodCells) % PeriodCells;

    static float Fade(float t) => t * t * (3f - 2f * t);

    static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
