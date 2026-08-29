namespace VintageHorizons.Checks;

/// <summary>
/// The seam distance field, proven offline rather than by looking at the game.
///
/// The band's whole shape comes from this one array, so a wrong distance here is a wrong
/// ring on screen with nothing else to blame - and "the band looked wrong" is an expensive
/// way to discover an off-by-one in a wrapped address. Every case below is a fixed grid with
/// a hand-computed answer.
/// </summary>
public static class SeamFieldChecks
{
    const int MaskCapacity = 8;
    const int VerticalChunks = 4;
    const int FieldCapacity = 64;
    const int Margin = 4;

    public static void Run(Check c)
    {
        SingleOwnedColumn(c);
        ColumnCountsWhenAnyLevelIsOwned(c);
        EmptyOwnershipLeavesNoBand(c);
        WindowAndEncoding(c);
        RefusesAWindowLargerThanItsCapacity(c);
    }

    /// <summary>
    /// One owned chunk in the middle of an otherwise empty window. The chunk covers four
    /// cells per axis, so distance is zero across all sixteen of them and grows outward from
    /// the block rather than from its centre.
    /// </summary>
    static void SingleOwnedColumn(Check c)
    {
        var mask = new VanillaReadinessMask(MaskCapacity, VerticalChunks);
        mask.Set(new VanillaChunkCell(4, 1, 4), true);

        var field = Build(mask);

        c.Eq(0, DistanceAt(field, 16, 16), "the owned chunk's first cell is at the seam");
        c.Eq(0, DistanceAt(field, 19, 19), "the owned chunk's last cell is at the seam");
        c.Eq(1, DistanceAt(field, 20, 16), "one cell past the owned chunk is one cell out");
        c.Eq(2, DistanceAt(field, 21, 16), "two cells past is two cells out");
        c.Eq(5, DistanceAt(field, 24, 16), "five cells past is five cells out");
        c.Eq(1, DistanceAt(field, 15, 19), "the band reaches back across the low edge too");

        // Chamfer, not Manhattan: a diagonal step costs 4/3 of an orthogonal one, so the
        // contours stay within about 2% of circular instead of forming a diamond.
        c.Eq(1, DistanceAt(field, 20, 20), "a single diagonal step still rounds to one cell");
        // Three diagonal steps from the owned corner at (19,19): 3 * 4 / 3 = 4 cells, against
        // a true Euclidean 4.24 and a Manhattan 6.
        c.Eq(4, DistanceAt(field, 22, 22), "diagonal distance stays near Euclidean, not Manhattan");

        // Monotonic outward. A dip anywhere would put a brighter ring inside a darker one.
        bool monotonic = true;
        for (int x = 20; x < 20 + Margin * 4; x++)
        {
            if (DistanceAt(field, x, 16) > DistanceAt(field, x + 1, 16)) monotonic = false;
        }
        c.True(monotonic, "distance never decreases as it moves away from vanilla");
    }

    /// <summary>
    /// Ownership is per 32-block cell but the band is a horizontal question. A column whose
    /// ground chunk is owned is inside vanilla's footprint even though the air chunks stacked
    /// above it are deliberately excluded from the mask; asking for every level would report
    /// solid ground as being outside vanilla altogether.
    /// </summary>
    static void ColumnCountsWhenAnyLevelIsOwned(Check c)
    {
        foreach (int y in new[] { 0, 1, VerticalChunks - 1 })
        {
            var mask = new VanillaReadinessMask(MaskCapacity, VerticalChunks);
            mask.Set(new VanillaChunkCell(4, y, 4), true);
            var field = Build(mask);
            c.Eq(0, DistanceAt(field, 17, 17), $"a column owned only at level {y} is still vanilla");
        }
    }

    /// <summary>
    /// No ownership means no band at all, which is what every failure path collapses to:
    /// cached terrain keeps exactly the colouring it has today.
    /// </summary>
    static void EmptyOwnershipLeavesNoBand(Check c)
    {
        var mask = new VanillaReadinessMask(MaskCapacity, VerticalChunks);
        var field = Build(mask);

        c.Eq(VanillaSeamDistanceField.MaxDistanceCells, DistanceAt(field, 16, 16),
            "with nothing owned every cell is maximally far from vanilla");
        c.Eq(VanillaSeamDistanceField.MaxDistanceCells, DistanceAt(field, 0, 0),
            "including the window's own corner");

        var cleared = Build(mask);
        cleared.Clear();
        c.Eq(VanillaSeamDistanceField.MaxDistanceCells, DistanceAt(cleared, 16, 16),
            "clearing returns the field to no band");
        c.Eq(0, cleared.WidthCells, "a cleared field describes no window");
    }

    /// <summary>
    /// The window straddles zero, so the low margin addresses negative cells and exercises
    /// the wrapped ring the fragment shader reconstructs. The encoding is pinned too: the
    /// shader reads the red channel and expects an opaque texel.
    /// </summary>
    static void WindowAndEncoding(Check c)
    {
        var mask = new VanillaReadinessMask(MaskCapacity, VerticalChunks);
        mask.Set(new VanillaChunkCell(0, 0, 0), true);
        var field = Build(mask);

        c.Eq(-Margin, field.MinCellX, "the window starts a margin below the mask's own");
        c.Eq(-Margin, field.MinCellZ, "on both axes");
        c.Eq(MaskCapacity * VanillaSeamDistanceField.CellsPerChunk + Margin * 2, field.WidthCells,
            "the window spans the mask plus a margin on each side");

        c.Eq(0, DistanceAt(field, 0, 0), "the owned chunk at the origin is at the seam");
        c.Eq(1, DistanceAt(field, -1, 0), "a negative cell addresses through the ring");
        c.Eq(Margin, DistanceAt(field, -Margin, 0), "the far edge of the low margin is reachable");

        int texel = field.Texels[VanillaSeamDistanceField.TexelIndex(-1, 0, field.Capacity)];
        c.Eq(255, (texel >> 24) & 0xFF, "texels are opaque so an unwritten one cannot pass for a value");
        c.Eq(1, (texel >> 16) & 0xFF, "distance is carried in the red channel the shader reads");
        c.Eq(0, texel & 0xFFFF, "no other channel carries meaning");
    }

    static void RefusesAWindowLargerThanItsCapacity(Check c)
    {
        var mask = new VanillaReadinessMask(MaskCapacity, VerticalChunks);
        var field = new VanillaSeamDistanceField(32);
        bool threw = false;
        try
        {
            field.Build(mask, 0, 0, MaskCapacity, MaskCapacity, Margin);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }
        c.True(threw, "a window wider than the ring is refused rather than silently wrapped onto itself");
    }

    static VanillaSeamDistanceField Build(VanillaReadinessMask mask)
    {
        var field = new VanillaSeamDistanceField(FieldCapacity);
        field.Build(mask, 0, 0, MaskCapacity, MaskCapacity, Margin);
        return field;
    }

    static int DistanceAt(VanillaSeamDistanceField field, int cellX, int cellZ) =>
        (field.Texels[VanillaSeamDistanceField.TexelIndex(cellX, cellZ, field.Capacity)] >> 16) & 0xFF;
}
