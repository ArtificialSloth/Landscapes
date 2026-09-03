using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// The game's climate colour maps, copied onto the GPU so the shader can do the lookup that
/// the CPU has been doing once per refresh at one position.
///
/// This is what makes a per-position tint affordable. The climate half of a tint is a single
/// read of one of these maps at (adjusted temperature, rainfall); moving that read into the
/// shader means the only per-position data the CPU has to produce is the climate itself - two
/// bytes per cell - instead of a finished colour per block type per cell per height.
///
/// Copied rather than sampled from the block atlas the maps also live in. The atlas rect would
/// have to be read out of engine state, the maps can land on any atlas page, and the padding
/// each map carries is inside that rect; owning the copy costs about a megabyte and removes
/// all three questions.
///
/// `Pixels` stores red in the LOW byte, and every texture this renderer uploads wants it at
/// bits 16-23, so the copy swaps them. That is not a guess about the loader: the engine's own
/// read of these pixels swaps the two channels on the way out, which is why the tint the mod
/// has always got from `ApplyColorMapOnRgba` has red at bits 16-23 while the array behind it
/// does not. Copying the array verbatim drew autumn forest deep blue - orange is high red and
/// low blue, so a swap is maximally visible and unmistakably a swap.
///
/// Pages are stacked down the Y axis, one map per page, and read with texelFetch, so nothing
/// filters across a page boundary and no padding between them is needed.
/// </summary>
internal sealed class LodClimateMapPages
{
    /// <summary>
    /// Inner size of a climate map, both axes: temperature across, rainfall down, one texel
    /// per count of 0..255. Every climate map in the base game is 264 square with 4 of padding.
    /// A map of any other size is refused rather than resampled - see <see cref="TryPageOf"/>.
    /// </summary>
    public const int PageSize = 256;

    /// <summary>Stored in a byte, so this is the value that means "this slot has no map".</summary>
    public const int NoPage = 255;

    /// <summary>More than the base game has, and far more than the 64 tint slots can need.</summary>
    public const int MaxPages = 16;

    readonly List<ColorMap> pages = new();
    readonly Dictionary<ColorMap, int> pageOf = new();
    int[] texels = Array.Empty<int>();

    public int PageCount => pages.Count;
    public int[] Texels => texels;
    public int Width => PageSize;
    public int Height => Math.Max(1, pages.Count) * PageSize;
    public int Version { get; private set; }
    public long Bytes => texels.LongLength * sizeof(int);

    /// <summary>Which page holds this map, or false when it has none and cannot get one.</summary>
    public bool TryPageOf(ColorMap? map, out int page)
    {
        page = NoPage;
        if (map == null) return false;
        if (pageOf.TryGetValue(map, out page)) return true;
        page = NoPage;
        return false;
    }

    /// <summary>
    /// A map can be paged only if it is the size the addressing assumes. Resampling one that
    /// is not would be a second transcription of the engine's lookup, and a modded map of an
    /// odd size is better left on the path that asks the engine directly.
    /// </summary>
    public static bool CanPage(ColorMap? map) =>
        map != null
        && map.Pixels != null
        && map.OuterSize != null
        && map.OuterSize.Width - 2 * map.Padding == PageSize
        && map.OuterSize.Height - 2 * map.Padding == PageSize
        && map.Pixels.Length >= map.OuterSize.Width * map.OuterSize.Height;

    /// <summary>
    /// Adopt any climate map these blocks use that is not paged yet. Returns true when the
    /// texture changed and has to be re-uploaded. Pages are only ever appended, so a page
    /// index already handed to a published slot table stays valid.
    /// </summary>
    public bool Adopt(IReadOnlyList<Block?> representative)
    {
        bool grew = false;
        for (int i = 0; i < representative.Count; i++)
        {
            ColorMap? map = representative[i]?.ClimateColorMapResolved;
            if (map == null || pageOf.ContainsKey(map)) continue;
            if (!CanPage(map) || pages.Count >= MaxPages) continue;
            pageOf[map] = pages.Count;
            pages.Add(map);
            grew = true;
        }
        if (grew) Rebuild();
        return grew;
    }

    /// <summary>
    /// Copy every page's inner pixels into one column of pages. The padding is dropped here
    /// rather than allowed for in the shader, so the shader's coordinate is exactly the
    /// temperature and rainfall it was given.
    /// </summary>
    void Rebuild()
    {
        texels = new int[PageSize * PageSize * Math.Max(1, pages.Count)];
        for (int page = 0; page < pages.Count; page++)
        {
            ColorMap map = pages[page];
            int outerWidth = map.OuterSize.Width;
            int pad = map.Padding;
            int into = page * PageSize * PageSize;
            for (int y = 0; y < PageSize; y++)
            {
                int from = (y + pad) * outerWidth + pad;
                for (int x = 0; x < PageSize; x++)
                    texels[into + y * PageSize + x] = PackTexel(map.Pixels[from + x]);
            }
        }
        Version++;
    }

    /// <summary>
    /// One stored pixel, in the layout the upload path and every other table here use: red at
    /// bits 16-23. `ColorMap.Pixels` keeps red in the low byte, so red and blue trade places.
    /// Alpha is forced opaque - a colour map's own alpha is not part of the tint, and a zero
    /// would upload the whole page transparent.
    /// </summary>
    public static int PackTexel(int pixel) =>
        unchecked((int)0xFF000000)
        | ((pixel & 0xFF) << 16)
        | (pixel & 0x00FF00)
        | ((pixel >> 16) & 0xFF);

    /// <summary>
    /// The engine's own climate lookup, as arithmetic, so a check can hold the shader's
    /// transcription against it without a GPU.
    ///
    /// `ClientWorldMap.ApplyColorMapOnRgba` truncates to a whole texel and reads one pixel;
    /// the shader interpolates between texels instead, because the map is sampled by a
    /// temperature that moves one count per 1.5 blocks of height and a nearest lookup would
    /// draw visible steps up every slope. The two therefore agree to within one texel, which
    /// is about five counts on this map - well inside its own per-texel noise.
    /// </summary>
    public static void Coordinate(float temperature, float rainfall, out float u, out float v)
    {
        u = Math.Clamp(temperature, 0f, 255f) / 255f * (PageSize - 1);
        v = Math.Clamp(rainfall, 0f, 255f) / 255f * (PageSize - 1);
    }

    /// <summary>
    /// The colour the SHADER would fetch: bilinear over one page at the same coordinates.
    /// Mirrors climateColor in lodterrainbody.vsh so a diagnostic can print what is being
    /// drawn, and so the two can be held to the same answer by a check.
    /// </summary>
    public bool TrySample(int page, float temperature, float rainfall,
        out float red, out float green, out float blue)
    {
        red = green = blue = 0f;
        if (page < 0 || page >= pages.Count) return false;

        float u = Math.Clamp(temperature, 0f, 255f);
        float v = Math.Clamp(rainfall, 0f, 255f);
        int x0 = Math.Clamp((int)u, 0, PageSize - 2);
        int y0 = Math.Clamp((int)v, 0, PageSize - 2);
        float fx = u - x0;
        float fy = v - y0;

        Span<float> acc = stackalloc float[3];
        for (int ch = 0; ch < 3; ch++)
        {
            int shift = ch == 0 ? 16 : ch == 1 ? 8 : 0;
            float c00 = (texels[TexelIndex(page, x0, y0)] >> shift) & 0xFF;
            float c10 = (texels[TexelIndex(page, x0 + 1, y0)] >> shift) & 0xFF;
            float c01 = (texels[TexelIndex(page, x0, y0 + 1)] >> shift) & 0xFF;
            float c11 = (texels[TexelIndex(page, x0 + 1, y0 + 1)] >> shift) & 0xFF;
            float a = c00 + (c10 - c00) * fx;
            float b = c01 + (c11 - c01) * fx;
            acc[ch] = (a + (b - a) * fy) / 255f;
        }
        red = acc[0]; green = acc[1]; blue = acc[2];
        return true;
    }

    /// <summary>Where one page's texel lives in <see cref="Texels"/>.</summary>
    public static int TexelIndex(int page, int x, int y) =>
        page * PageSize * PageSize + y * PageSize + x;
}
