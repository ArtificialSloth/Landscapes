using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageHorizons.Checks;

/// <summary>
/// The season colour, read at the column the terrain SHADER reads rather than the one the
/// engine's CPU colour call reads.
///
/// This is the defect that made distant grass 5 to 6 percent too red against vanilla in three
/// of the owner's screenshots, and it had been there since the mod first tinted anything. The
/// two paths choose the season map's column differently - the shader adds a noise term of up to
/// 0.05 of a year, the CPU adds nothing - and everything the mod knew about the season came
/// from the CPU call. Its size depends on how fast the map is moving at the time of year, which
/// is why it was invisible for months and then obvious during the autumn turn.
/// </summary>
public static class SeasonColorChecks
{
    public static void Run(Check c)
    {
        TheJitterMatchesTheEngine(c);
        TheColumnAdvancesByTheJitter(c);
        RowsAreAveragedFlat(c);
        PaddingIsSkipped(c);
        ChannelsComeOutRedFirst(c);
        UnusableMapsAreRefused(c);
        TheLiveColumnMatchesTheWholeStore(c);
    }

    /// <summary>
    /// The shader now reads a two-kilobyte table holding only the column of the year this frame
    /// draws from, instead of a scattered texel out of the whole 136 KiB store. That is a
    /// performance change and must not be a visual one, so the assertion is exact equality:
    /// every page, every row, the same packed texel the full store holds at that column.
    ///
    /// The layout is transposed on the way - 16 rows across, one page per scanline down, so a
    /// page's rows share a cache line - which is exactly the kind of swap that draws another
    /// page's colours while still looking plausible.
    /// </summary>
    static void TheLiveColumnMatchesTheWholeStore(Check c)
    {
        const int w = LodSeasonMapPages.PageWidth;
        const int h = LodSeasonMapPages.PageHeight;

        // Every texel distinct, so a transposed or off-by-one read cannot coincide with a
        // correct one: page, row and column each move a different byte.
        Block PageBlock(int page)
        {
            var pixels = new int[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++) pixels[y * w + x] = (page << 16) | (y << 8) | (x >> 1);
            }
            return new Block
            {
                SeasonColorMapResolved = new ColorMap
                {
                    Pixels = pixels,
                    OuterSize = new Size2i(w, h),
                    Padding = 0,
                    ExtraFlags = 0,
                },
            };
        }

        var pages = new LodSeasonMapPages();
        var blocks = new List<Block?> { PageBlock(1), PageBlock(2), PageBlock(3) };
        c.True(pages.Adopt(blocks), "three synthetic seasonal maps are adopted");
        c.Eq(3, pages.PageCount, "and each becomes its own page");
        c.Eq(h, pages.LiveColumnWidth, "the live table is one row per texel across");
        c.Eq(3, pages.LiveColumnHeight, "and one page per scanline down");

        foreach (int column in new[] { 0, 1, 63, 64, 127 })
        {
            c.True(pages.RefreshLiveColumn(column), $"column {column} is a fresh extraction");
            c.False(pages.RefreshLiveColumn(column), "and asking twice does no work");
            for (int page = 0; page < pages.PageCount; page++)
            {
                for (int row = 0; row < h; row++)
                {
                    c.Eq(pages.Texels[page * w * h + row * w + column],
                        pages.LiveColumnTexels[page * h + row],
                        $"page {page} row {row} at column {column} is the store's own texel");
                }
            }
        }

        // A page adopted later makes the table taller, and the renderer replaces the texture on
        // exactly this signal. If the extraction did not notice, the new page would draw
        // whatever the old buffer's tail happened to hold.
        c.True(pages.Adopt(new List<Block?> { PageBlock(4) }), "a fourth map is adopted");
        c.Eq(4, pages.LiveColumnHeight, "the live table grows with it");
        c.True(pages.RefreshLiveColumn(63), "and the same column is extracted again");
        c.Eq(pages.Texels[3 * w * h + 5 * w + 63], pages.LiveColumnTexels[3 * h + 5],
            "so the new page's rows are real rather than left over");
    }

    /// <summary>A map whose every pixel encodes its own column, so the average IS the column.</summary>
    static int[] ColumnMap(int width, int height, int padding)
    {
        var pixels = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int column = x - padding;
                int v = column < 0 || column > 255 ? 0 : column;
                pixels[y * width + x] = v | (v << 8) | (v << 16);
            }
        }
        return pixels;
    }

    /// <summary>
    /// The constant, against the engine's own shader. A game update that retuned the jitter
    /// would otherwise leave the mod confidently a few days out of season with nothing to say
    /// so - which is exactly how this defect survived as long as it did.
    /// </summary>
    static void TheJitterMatchesTheEngine(Check c)
    {
        c.Eq(2f / 40f, LodSeasonColor.MaxColumnJitter,
            "the jitter is two value noises over forty");

        string path = Path.Combine(GameAssemblies.GamePath, "assets", "game",
            "shaderincludes", "colormap.vsh");
        if (!File.Exists(path)) return;
        string source = File.ReadAllText(path);

        c.True(source.Contains("valuenoise(worldPos.xyz) + valuenoise(worldPos.xyz/2)",
                StringComparison.Ordinal),
            "the engine still builds the jitter from two value noises, so it is at most 2");
        c.True(source.Contains("clamp(seasonRel + b/40, 0.01, 0.99)", StringComparison.Ordinal),
            "and still advances the season column by b/40");
    }

    static void TheColumnAdvancesByTheJitter(Check c)
    {
        const int width = 128, height = 16;
        int[] pixels = ColumnMap(width, height, 0);

        // The CPU reads column 52 flat. The shader spreads over 52 to 52 + 0.05*128, weighted
        // toward the middle, so the average column must land near 52 + 3.2.
        c.True(LodSeasonColor.TryAverage(pixels, width, height, 0, 52f / width,
            out float r, out float g, out float b), "a well formed map is readable");
        c.True(r > 54.5f && r < 56f,
            $"the average column is about three past the CPU's, not the CPU's own (got {r:F2})");
        c.Eq(r, g, "every channel reads the same column");
        c.Eq(r, b, "every channel reads the same column");

        // Never behind the CPU's column, and never further ahead than the jitter allows.
        for (int col = 0; col < 100; col += 7)
        {
            c.True(LodSeasonColor.TryAverage(pixels, width, height, 0, col / (float)width,
                out float got, out _, out _), $"column {col} is readable");
            c.True(got >= col - 0.01f, $"column {col} is never read behind itself");
            c.True(got <= col + LodSeasonColor.MaxColumnJitter * width + 0.01f,
                $"column {col} is never read further ahead than the jitter allows");
        }

        // Late in the year the column stays on the map rather than running off the end.
        c.True(LodSeasonColor.TryAverage(pixels, width, height, 0, 0.995f,
            out float late, out _, out _), "the end of the year is readable");
        c.True(late >= 0f && late < width, $"and stays on the map (got {late:F1})");
    }

    static void RowsAreAveragedFlat(Check c)
    {
        // Colour encodes the ROW here, so the answer is the mean row: 7.5 over sixteen.
        const int width = 128, height = 16;
        var pixels = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++) pixels[y * width + x] = y | (y << 8) | (y << 16);
        }

        c.True(LodSeasonColor.TryAverage(pixels, width, height, 0, 0.4f,
            out float r, out _, out _), "readable");
        c.True(Math.Abs(r - 7.5f) < 0.01f,
            $"all sixteen rows are averaged evenly (got {r:F3})");
    }

    static void PaddingIsSkipped(Check c)
    {
        // Padding carries edge copies, not data. Reading it would drag every colour toward
        // whatever the map's border happens to be.
        const int pad = 4, inner = 32, width = inner + pad, height = 8 + 2 * pad;
        var pixels = new int[width * height];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = 0x00FFFFFF;   // poison
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < inner; x++) pixels[(y + pad) * width + x + pad] = 0x00202020;
        }

        c.True(LodSeasonColor.TryAverage(pixels, width, height, pad, 0.5f,
            out float r, out float g, out float b), "a padded map is readable");
        c.Eq(0x20, (int)Math.Round(r), "red comes from inside the padding");
        c.Eq(0x20, (int)Math.Round(g), "green too");
        c.Eq(0x20, (int)Math.Round(b), "and blue");
    }

    static void ChannelsComeOutRedFirst(Check c)
    {
        // ColorMap.Pixels stores red in the LOW byte; callers want ordinary R, G, B. Getting
        // this backwards drew an autumn forest blue once already.
        const int width = 128, height = 16;
        var pixels = new int[width * height];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (30 << 16) | (120 << 8) | 200;

        c.True(LodSeasonColor.TryAverage(pixels, width, height, 0, 0.3f,
            out float r, out float g, out float b), "readable");
        c.Eq(200, (int)Math.Round(r), "red is the low byte of the stored pixel");
        c.Eq(120, (int)Math.Round(g), "green is the middle byte");
        c.Eq(30, (int)Math.Round(b), "blue is the high byte");
    }

    static void UnusableMapsAreRefused(Check c)
    {
        c.False(LodSeasonColor.TryAverage(null, 128, 16, 0, 0.5f, out _, out _, out _),
            "a map with no pixels is refused");
        c.False(LodSeasonColor.TryAverage(new int[10], 128, 16, 0, 0.5f, out _, out _, out _),
            "and so is one whose pixel array is too short for its stated size");
        c.False(LodSeasonColor.TryAverage(new int[16], 4, 4, 4, 0.5f, out _, out _, out _),
            "and one that is all padding");
    }
}
