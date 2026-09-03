using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// The game's seasonal colour maps, copied onto the GPU so the shader can pick a ROW the way
/// the game does instead of drawing the average of all sixteen.
///
/// A seasonal map is 128 columns of 16 rows: the column is the point in the year, and the row is
/// chosen per position from noise. Distant terrain averages the rows on purpose (G47) - one
/// distant pixel covers many blocks, and an early build that picked a single row painted every
/// field in view with it. But the row is exactly what makes a forest turn unevenly, and for
/// LEAVES the engine draws that noise at a 2-to-6 block scale rather than ground's 12-to-24, so
/// during the autumn turn the sixteen rows of `birchtint.png` run from (175, 224, 16), clearly
/// green, to (177, 156, 42), clearly yellow. Averaging them draws a forest with no yellow in it.
///
/// So both are kept: the shader picks a row per vertex where a voxel is still a block or two,
/// and blends toward the averaged colour as the voxel grows, because G47's reasoning is right
/// once one voxel is a neighbourhood.
///
/// Seventeen maps of 128x16 is 136 KiB - a fraction of one climate map page. `Pixels` is
/// repacked by <see cref="LodClimateMapPages.PackTexel"/>: same store, same red-in-the-low-byte
/// layout, same reason.
/// </summary>
internal sealed class LodSeasonMapPages
{
    /// <summary>Columns in a seasonal map: the point in the year.</summary>
    public const int PageWidth = 128;

    /// <summary>Rows: the per-position variation this class exists to stop averaging away.</summary>
    public const int PageHeight = 16;

    /// <summary>Stored in a byte, so this is the value that means "this slot has no map".</summary>
    public const int NoPage = 255;

    /// <summary>The base game ships seventeen; the tint table cannot hold more than its slots.</summary>
    public const int MaxPages = 32;

    readonly List<ColorMap> pages = new();
    readonly Dictionary<ColorMap, int> pageOf = new();
    int[] texels = Array.Empty<int>();

    public int PageCount => pages.Count;
    public int[] Texels => texels;
    public int Width => PageWidth;
    public int Height => Math.Max(1, pages.Count) * PageHeight;
    public int Version { get; private set; }

    public bool TryPageOf(ColorMap? map, out int page)
    {
        page = NoPage;
        if (map == null) return false;
        if (pageOf.TryGetValue(map, out page)) return true;
        page = NoPage;
        return false;
    }

    /// <summary>
    /// A map can be paged only at the size the addressing assumes. The tropical map is refused
    /// as well: the shader takes a different weight entirely for it and this mod does not
    /// reproduce that branch, so those slots keep the averaged colour.
    /// </summary>
    public static bool CanPage(ColorMap? map) =>
        map != null
        && map.Pixels != null
        && map.OuterSize != null
        && map.ExtraFlags == 0
        && map.OuterSize.Width - 2 * map.Padding == PageWidth
        && map.OuterSize.Height - 2 * map.Padding == PageHeight
        && map.Pixels.Length >= map.OuterSize.Width * map.OuterSize.Height;

    /// <summary>
    /// Adopt any seasonal map these blocks use that is not paged yet. Pages are only appended,
    /// so an index already handed to a published slot table stays valid.
    /// </summary>
    public bool Adopt(IReadOnlyList<Block?> representative)
    {
        bool grew = false;
        for (int i = 0; i < representative.Count; i++)
        {
            ColorMap? map = representative[i]?.SeasonColorMapResolved;
            if (map == null || pageOf.ContainsKey(map)) continue;
            if (!CanPage(map) || pages.Count >= MaxPages) continue;
            pageOf[map] = pages.Count;
            pages.Add(map);
            grew = true;
        }
        if (grew) Rebuild();
        return grew;
    }

    void Rebuild()
    {
        texels = new int[PageWidth * PageHeight * Math.Max(1, pages.Count)];
        for (int page = 0; page < pages.Count; page++)
        {
            ColorMap map = pages[page];
            int outerWidth = map.OuterSize.Width;
            int pad = map.Padding;
            int into = page * PageWidth * PageHeight;
            for (int y = 0; y < PageHeight; y++)
            {
                int from = (y + pad) * outerWidth + pad;
                for (int x = 0; x < PageWidth; x++)
                {
                    texels[into + y * PageWidth + x] =
                        LodClimateMapPages.PackTexel(map.Pixels[from + x]);
                }
            }
        }
        Version++;
    }

    /// <summary>
    /// The colour at one page, column and row, as the SHADER fetches it. Mirrors the texelFetch
    /// in lodterrainbody.vsh so a diagnostic can print what is drawn and a check can hold the
    /// two to one answer.
    /// </summary>
    public bool TrySample(int page, int column, int row, out float r, out float g, out float b)
    {
        r = g = b = 0f;
        if (page < 0 || page >= pages.Count) return false;
        column = Math.Clamp(column, 0, PageWidth - 1);
        row = Math.Clamp(row, 0, PageHeight - 1);
        int texel = texels[page * PageWidth * PageHeight + row * PageWidth + column];
        r = ((texel >> 16) & 0xFF) / 255f;
        g = ((texel >> 8) & 0xFF) / 255f;
        b = (texel & 0xFF) / 255f;
        return true;
    }
}
