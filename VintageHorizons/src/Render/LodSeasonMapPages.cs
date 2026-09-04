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

    /// <summary>
    /// The mean of the engine's column jitter, which is what the mod takes the column at.
    ///
    /// colormap.vsh moves the year by `b/40`, b being a value noise in 0..2, so the mean is
    /// 1/40. Reproducing the jitter itself would cost two more value noises per vertex to
    /// move the year by at most three columns of 128, and the flat colour this one is blended
    /// against is integrated over the same range centred on this value - so taking the mean
    /// also puts the two ends of that blend at the same point in the year.
    /// </summary>
    public const float MeanColumnJitter = 1f / 40f;

    readonly List<ColorMap> pages = new();
    readonly Dictionary<ColorMap, int> pageOf = new();
    int[] texels = Array.Empty<int>();
    int[] liveColumn = Array.Empty<int>();
    int liveColumnIndex = -1;
    int liveColumnVersion = -1;

    public int PageCount => pages.Count;
    public int[] Texels => texels;
    public int Width => PageWidth;
    public int Height => Math.Max(1, pages.Count) * PageHeight;
    public int Version { get; private set; }

    /// <summary>
    /// The column of the year the shader draws from, from the engine's own season fraction.
    ///
    /// On the CPU rather than in the shader because it is the same value for every vertex in
    /// the frame - see <see cref="LiveColumnTexels"/> for why that matters.
    /// </summary>
    public static int ColumnFor(float seasonRel)
    {
        float rel = Math.Clamp(seasonRel + MeanColumnJitter, 0.01f, 0.99f);
        return Math.Clamp((int)(rel * PageWidth), 0, PageWidth - 1);
    }

    /// <summary>
    /// Just the column the shader can actually reach this frame: 16 rows per page, no more
    /// than <see cref="MaxPages"/> of them, so at most 512 texels - two kilobytes.
    ///
    /// The whole seasonal store is 136 KiB and a vertex fetched a scattered texel out of it,
    /// which measured 14 microseconds of the feature's 35 at the bench viewpoint. But the
    /// COLUMN is the point in the year, and that is one value for the entire frame - only the
    /// ROW varies per vertex. So everything every vertex can possibly read is these two
    /// kilobytes, which fits in texture cache instead of missing out of a 136 KiB map.
    ///
    /// Laid out row-across, page-down so that one page's sixteen rows share a scanline: the
    /// row is what varies between neighbouring vertices, the page is fixed per block type.
    ///
    /// Rebuilt when the column moves, which is 128 times a game year, or when a page is
    /// adopted. No mesh is touched either way.
    /// </summary>
    public int[] LiveColumnTexels => liveColumn;
    public int LiveColumnWidth => PageHeight;
    public int LiveColumnHeight => Math.Max(1, pages.Count);

    /// <summary>Fills <see cref="LiveColumnTexels"/>. True when it changed.</summary>
    public bool RefreshLiveColumn(int column)
    {
        column = Math.Clamp(column, 0, PageWidth - 1);
        int wanted = PageHeight * Math.Max(1, pages.Count);
        if (column == liveColumnIndex && Version == liveColumnVersion
            && liveColumn.Length == wanted)
        {
            return false;
        }
        if (liveColumn.Length != wanted) liveColumn = new int[wanted];
        for (int page = 0; page < pages.Count; page++)
        {
            int from = page * PageWidth * PageHeight + column;
            int into = page * PageHeight;
            for (int row = 0; row < PageHeight; row++)
            {
                liveColumn[into + row] = texels[from + row * PageWidth];
            }
        }
        liveColumnIndex = column;
        liveColumnVersion = Version;
        return true;
    }

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
