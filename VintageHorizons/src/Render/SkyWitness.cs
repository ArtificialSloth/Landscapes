using System.Text;

namespace VintageHorizons;

/// <summary>
/// The word layout <c>lodsky.fsh</c> writes for `.dlsky`, one buffer per frame in flight. The
/// shader indexes these words by number; <c>SkyWitnessChecks</c> holds the two together.
/// </summary>
internal static class SkyWitnessLayout
{
    public const int Binding = 5;
    public const int Slots = 4;
    public const int TilesX = 16;
    public const int TilesY = 9;

    public const int Total = 0;
    public const int BeyondReach = 1;
    public const int MinX = 2;
    public const int MinY = 3;
    public const int MaxX = 4;
    public const int MaxY = 5;
    public const int FirstTile = 8;
    public const int WordCount = FirstTile + TilesX * TilesY;

    /// <summary>
    /// What a slot is reset to before its frame is drawn: no pixels, and a box whose minimum
    /// starts above any pixel, so the first one counted sets it.
    /// </summary>
    public static uint[] ClearWords()
    {
        var words = new uint[WordCount];
        words[MinX] = uint.MaxValue;
        words[MinY] = uint.MaxValue;
        return words;
    }
}

/// <summary>
/// The readiness mask as one frame's terrain draw saw it: the window bounds the shader was
/// given. The texels travel beside it in <see cref="SkyWitnessFrame"/>.
/// </summary>
internal readonly record struct SkyMaskWindow(
    bool Enabled, int MinX, int MinZ, int Width, int Depth, int Capacity, int VerticalChunks);

/// <summary>
/// What the CPU knew about one witnessed frame, kept until the graphics card's count for it is
/// read back two or three frames later. Reused per slot, so it holds no reference to live state.
/// </summary>
internal sealed class SkyWitnessFrame
{
    public long Frame;
    public double CameraX;
    public double CameraY;
    public double CameraZ;
    public readonly float[] InverseViewProjection = new float[16];
    public int ViewportWidth;
    public int ViewportHeight;
    public SkyMaskWindow Mask;
    public int[] Texels = Array.Empty<int>();
    /// <summary>Texels changed after this frame's upload, so the copy may differ from what the card sampled.</summary>
    public bool MaskDirty;
    /// <summary>Texel changes and uploads since the previous witnessed frame.</summary>
    public long MaskChanges;
    public long MaskUploads;
    public bool ChunkCrossing;
    public bool DepthSplit;
    public bool GpuCull;
}

internal readonly record struct SkyTile(int X, int Y, uint Pixels);

/// <summary>
/// Tells a flash from sky that is simply always there - an unfinished cache edge, or a crack
/// that persists - by comparing each frame with a slow running level. The level follows every
/// frame, more slowly while it is reporting, so sky that stays stops being reported within a
/// few dozen frames while a one- or two-frame flash barely moves it.
/// </summary>
internal sealed class SkyWitnessBaseline
{
    public const uint MinimumPixels = 48;

    public double Level { get; private set; }

    public bool IsFlash(uint total)
    {
        bool flash = total >= MinimumPixels && total >= 2.0 * Level + MinimumPixels;
        Level += (total - Level) * (flash ? 0.03 : 0.1);
        return flash;
    }

    public void Reset() => Level = 0;
}

internal static class SkyWitnessReport
{
    /// <summary>The busiest screen tiles, most pixels first, empty tiles left out.</summary>
    public static List<SkyTile> TopTiles(ReadOnlySpan<uint> words, int count)
    {
        var tiles = new List<SkyTile>();
        for (int y = 0; y < SkyWitnessLayout.TilesY; y++)
        {
            for (int x = 0; x < SkyWitnessLayout.TilesX; x++)
            {
                uint pixels = words[SkyWitnessLayout.FirstTile + y * SkyWitnessLayout.TilesX + x];
                if (pixels > 0) tiles.Add(new SkyTile(x, y, pixels));
            }
        }
        tiles.Sort((a, b) => b.Pixels.CompareTo(a.Pixels));
        if (tiles.Count > count) tiles.RemoveRange(count, tiles.Count - count);
        return tiles;
    }

    /// <summary>
    /// The normalised view ray through a viewport pixel, from the inverse of the
    /// camera-relative view-projection the frame was drawn with. <paramref name="pixelY"/>
    /// counts from the BOTTOM, as gl_FragCoord does. False for a degenerate matrix.
    /// </summary>
    public static bool TryRay(float[] inverseViewProjection, double pixelX, double pixelY,
        int width, int height, out double dx, out double dy, out double dz)
    {
        dx = dy = dz = 0;
        if (width <= 0 || height <= 0) return false;
        double nx = pixelX / width * 2.0 - 1.0;
        double ny = pixelY / height * 2.0 - 1.0;
        if (!Unproject(inverseViewProjection, nx, ny, -1.0, out double ax, out double ay, out double az))
            return false;
        if (!Unproject(inverseViewProjection, nx, ny, 1.0, out double bx, out double by, out double bz))
            return false;

        dx = bx - ax;
        dy = by - ay;
        dz = bz - az;
        double length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (!(length > 1e-12) || double.IsInfinity(length))
        {
            dx = dy = dz = 0;
            return false;
        }
        dx /= length;
        dy /= length;
        dz /= length;
        return true;
    }

    // Column-major, as OpenGL and Mat4f store it: m[column * 4 + row].
    static bool Unproject(float[] m, double x, double y, double z,
        out double ox, out double oy, out double oz)
    {
        ox = oy = oz = 0;
        double w = m[3] * x + m[7] * y + m[11] * z + m[15];
        if (!(Math.Abs(w) > 1e-20)) return false;
        ox = (m[0] * x + m[4] * y + m[8] * z + m[12]) / w;
        oy = (m[1] * x + m[5] * y + m[9] * z + m[13]) / w;
        oz = (m[2] * x + m[6] * y + m[10] * z + m[14]) / w;
        return true;
    }

    /// <summary>
    /// Whether a captured mask hands a cell's GROUND to vanilla: lodownership.fsh's
    /// vanillaOwnsCell for the ground band, with the window that frame was drawn with. Outside
    /// the window, or with the mask off, the answer is no, as it is in the shader.
    /// </summary>
    public static bool GroundOwned(int[] texels, in SkyMaskWindow window, int x, int y, int z)
    {
        if (!window.Enabled || window.Capacity <= 0) return false;
        if (y < 0 || y >= window.VerticalChunks) return false;
        if (x < window.MinX || x >= window.MinX + window.Width) return false;
        if (z < window.MinZ || z >= window.MinZ + window.Depth) return false;

        int index = VanillaReadinessMask.TexelIndex(x, y, z, window.Capacity, window.VerticalChunks);
        if (index < 0 || index >= texels.Length) return false;
        // BGRA bytes in one int: red is bits 16-23, and the shader's `r > 0.5` is red >= 128.
        return ((texels[index] >> 16) & 0xFF) > 127;
    }

    /// <summary>The layers of one column whose ground a captured mask hands to vanilla, as "2,3", or "none".</summary>
    public static string OwnedLayers(int[] texels, in SkyMaskWindow window, int x, int z)
    {
        var layers = new StringBuilder();
        for (int y = 0; y < window.VerticalChunks; y++)
        {
            if (!GroundOwned(texels, window, x, y, z)) continue;
            if (layers.Length > 0) layers.Append(',');
            layers.Append(y);
        }
        return layers.Length == 0 ? "none" : layers.ToString();
    }
}
