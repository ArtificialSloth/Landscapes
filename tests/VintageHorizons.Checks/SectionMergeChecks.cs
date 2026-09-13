namespace VintageHorizons.Checks;

/// <summary>
/// Merging a second copy of a section into the one already held (LodSection.MergeFrom).
///
/// The failure this guards was visible from across the map. Far sections copied while
/// vanilla pregen had filled a sliver of them stayed slivers, because a held key was never
/// offered again, and the only other install the client knew was "replace everything" -
/// which, applied to a copy the client had partly captured itself, erased what it had seen.
/// These checks pin the column rule: which columns move, which never do, and that palette
/// ids and snow travel with the columns that move.
/// </summary>
public static class SectionMergeChecks
{
    public static void Run(Check c)
    {
        FillCopiesOnlyMissingColumns(c);
        NewerWinsReplacesButKeepsOwnOnlyColumns(c);
        ObservedGroundIsNeverDowngraded(c);
        PaletteIsRemappedByBlock(c);
        SnowTravelsWithCopiedColumns(c);
        IdenticalCopyChangesNothing(c);
        EdgesAreReported(c);
    }

    /// <summary>One palette entry per block id, in order; a column is one run of that entry.</summary>
    static LodSection Section(int[] blockIds, params (int Col, int PaletteIndex, int Top)[] columns)
    {
        var s = new LodSection();
        foreach (int id in blockIds) s.FindOrAddPaletteEntry(id, color: id * 16, flags: 0);
        foreach ((int col, int index, int top) in columns)
            s.SetColumn(col, new[] { LodSection.PackRun(index, top, 0) });
        return s;
    }

    static int Top(LodSection s, int col) => LodSection.RunYTop(s.ColumnRuns(col)[0]);

    static void FillCopiesOnlyMissingColumns(Check c)
    {
        LodSection held = Section(new[] { 1 }, (0, 0, 10), (1, 0, 10));
        LodSection source = Section(new[] { 1 }, (1, 0, 20), (2, 0, 30));
        source.SetColumn(3, ReadOnlySpan<ulong>.Empty);

        LodMergeResult merged = held.MergeFrom(source, LodMergeMode.FillMissing);

        c.True(merged.ContentChanged, "a fill that adds columns reports a content change");
        c.Eq(2, merged.ColumnsFilled, "both columns the held copy lacked are filled");
        c.Eq(0, merged.ColumnsReplaced, "a fill replaces nothing");
        c.Eq(10, Top(held, 0), "a column only the held copy has is kept");
        c.Eq(10, Top(held, 1), "a column both copies have keeps the held copy's runs");
        c.Eq(30, Top(held, 2), "a column only the source has is copied in");
        c.True(held.Captured[3] && held.RunCount(3) == 0,
            "a captured-empty source column arrives captured and empty, not uncaptured");
        c.Eq(4, held.CapturedColumns, "the captured count is the union of both copies");
        c.False(held.Captured[4], "a column neither copy captured stays uncaptured");
    }

    /// <summary>
    /// The owner's rule for a rewritten level-0 row (2026-09-12): the server's newer copy
    /// wins column by column. What it must never do again is what the whole-section refresh
    /// did - erase a column only the client had.
    /// </summary>
    static void NewerWinsReplacesButKeepsOwnOnlyColumns(Check c)
    {
        LodSection held = Section(new[] { 1 }, (0, 0, 10), (1, 0, 10), (5, 0, 10));
        LodSection source = Section(new[] { 1 }, (1, 0, 20), (2, 0, 30), (5, 0, 10));

        LodMergeResult merged = held.MergeFrom(source, LodMergeMode.NewerWins);

        c.Eq(10, Top(held, 0), "a column only the held copy has survives a newer save");
        c.Eq(20, Top(held, 1), "a column both have takes the newer save's runs");
        c.Eq(30, Top(held, 2), "a column only the newer save has is filled");
        c.Eq(10, Top(held, 5), "an identical column is left as it was");
        c.Eq(1, merged.ColumnsFilled, "one column was filled");
        c.Eq(1, merged.ColumnsReplaced,
            "only a column whose runs actually differed counts as replaced");
        c.Eq(4, held.CapturedColumns, "no captured column is lost");
    }

    /// <summary>
    /// A generated peek (/vhgen) stores terrain with no snow observation. Capture never lets
    /// one erase observed ground, and a newer save of the same key must not either; but a
    /// newer observed save does replace observed ground, which is how a melt reaches the client.
    /// </summary>
    static void ObservedGroundIsNeverDowngraded(Check c)
    {
        LodSection held = Section(new[] { 1 }, (1, 0, 10), (2, 0, 10), (5, 0, 10));
        held.SetSnowStateObserved(1, observed: true);
        held.SetSnowStateObserved(5, observed: true);
        held.SetSnowCovered(5, covered: true);

        LodSection source = Section(new[] { 1 }, (1, 0, 20), (2, 0, 25), (5, 0, 12));
        source.SetSnowStateObserved(5, observed: true);

        LodMergeResult merged = held.MergeFrom(source, LodMergeMode.NewerWins);

        c.Eq(10, Top(held, 1), "an unobserved save does not replace an observed column");
        c.True(held.IsSnowStateObserved(1), "and the observation survives");
        c.Eq(1, merged.ColumnsKeptObserved, "the kept observed column is counted");
        c.Eq(25, Top(held, 2), "an unobserved save still replaces an unobserved column");
        c.Eq(12, Top(held, 5), "an observed save replaces an observed column");
        c.True(held.IsSnowStateObserved(5) && !held.IsSnowCovered(5),
            "and brings its answer: the snow has melted");
        c.True(merged.SnowAppearanceChanged, "a melt is an appearance change");
    }

    /// <summary>
    /// The two copies number their blocks independently. A run must reach the held copy
    /// meaning the same block it meant in the source, and a block both already know must not
    /// grow a second palette entry.
    /// </summary>
    static void PaletteIsRemappedByBlock(Check c)
    {
        const int grass = 5, stone = 7, sand = 9;
        LodSection held = Section(new[] { grass, stone }, (0, 0, 10));
        var source = new LodSection();
        int sourceStone = source.FindOrAddPaletteEntry(stone, color: 0x00707070, flags: 0);
        int sourceSand = source.FindOrAddPaletteEntry(sand, color: 0x00C0B080,
            flags: LodPaletteEntry.FlagHalf, tintSlot: 3, textureSlot: 12);
        source.SetColumn(10, new[]
        {
            LodSection.PackRun(sourceSand, 12, 8),
            LodSection.PackRun(sourceStone, 8, 0),
        });

        held.MergeFrom(source, LodMergeMode.FillMissing);

        c.Eq(3, held.Palette.Count, "only the block the held copy lacked is added to its palette");
        int heldSand = held.Palette.FindIndex(e => e.BlockId == sand);
        c.Eq(2, heldSand, "the new block is appended after the held copy's own entries");
        c.SeqEq(new[]
            {
                LodSection.PackRun(heldSand, 12, 8),
                LodSection.PackRun(1, 8, 0),
            }, held.ColumnRuns(10).ToArray(),
            "every run names the same block it named in the source");
        LodPaletteEntry added = held.Palette[heldSand];
        c.True(added.Color == 0x00C0B080 && added.Flags == LodPaletteEntry.FlagHalf
            && added.TintSlot == 3 && added.TextureSlot == 12,
            "a new entry keeps the source's colour, flags, tint and texture slot");
    }

    static void SnowTravelsWithCopiedColumns(Check c)
    {
        LodSection held = Section(new[] { 1 }, (7, 0, 10));
        held.SetSnowStateObserved(7, observed: true);
        held.SetSnowCovered(7, covered: true);

        LodSection source = Section(new[] { 1 }, (3, 0, 10), (4, 0, 10), (6, 0, 10), (7, 0, 10));
        source.SetSnowStateObserved(3, observed: true);
        source.SetSnowCovered(3, covered: true);
        source.SetSnowStateObserved(4, observed: true);
        // Coverage without an observation means nothing and must not arrive as snow.
        source.SetSnowCovered(6, covered: true);

        LodMergeResult merged = held.MergeFrom(source, LodMergeMode.FillMissing);

        c.True(held.IsSnowStateObserved(3) && held.IsSnowCovered(3),
            "an observed snowy column arrives observed and snowy");
        c.True(held.IsSnowStateObserved(4) && !held.IsSnowCovered(4),
            "an observed bare column arrives observed and bare");
        c.True(!held.IsSnowStateObserved(6) && !held.IsSnowCovered(6),
            "an unobserved column arrives unknown, whatever its stray coverage bit said");
        c.True(held.IsSnowStateObserved(7) && held.IsSnowCovered(7),
            "a held column keeps its own snow answer under a fill");
        c.True(merged.SnowStateChanged, "arriving observations are reported");
        c.True(merged.SnowAppearanceChanged, "arriving snow is an appearance change");
    }

    /// <summary>
    /// A server re-saves rows constantly while things change near the player, and in
    /// singleplayer it captured the same chunks the client did. Merging a copy that says
    /// what the held one already says must report nothing, or every save would rebuild a mesh.
    /// </summary>
    static void IdenticalCopyChangesNothing(Check c)
    {
        LodSection held = Section(new[] { 1, 2 }, (0, 0, 10), (1, 1, 11));
        held.SetSnowStateObserved(0, observed: true);
        // Same blocks and columns, numbered the other way round.
        LodSection source = Section(new[] { 2, 1 }, (0, 1, 10), (1, 0, 11));
        source.SetSnowStateObserved(0, observed: true);
        ulong[] runsBefore = (ulong[])held.Runs.Clone();

        LodMergeResult merged = held.MergeFrom(source, LodMergeMode.NewerWins);

        c.False(merged.ContentChanged, "an identical copy changes no content");
        c.False(merged.SnowStateChanged, "an identical copy changes no snow state");
        c.Eq(0, merged.ColumnsReplaced, "an identical copy replaces no column");
        c.Eq(2, held.Palette.Count, "an identical copy adds no palette entry");
        c.SeqEq(runsBefore, held.Runs, "the runs are untouched");
    }

    /// <summary>
    /// Content change and touched edges are separate answers (G138): an interior fill changes
    /// the section while touching no edge a neighbour's mesh depends on.
    /// </summary>
    static void EdgesAreReported(Check c)
    {
        int interior = LodSection.ColumnIndex(10, 10);
        LodSection held = Section(new[] { 1 });
        LodSection source = Section(new[] { 1 }, (interior, 0, 10));
        LodMergeResult merged = held.MergeFrom(source, LodMergeMode.FillMissing);
        c.True(merged.ContentChanged, "an interior fill is a content change");
        c.Eq(LodSection.EdgeNone, merged.TouchedEdges, "an interior fill touches no edge");

        LodSection cornerHeld = Section(new[] { 1 });
        LodSection corner = Section(new[] { 1 }, (0, 0, 10));
        LodMergeResult cornerMerged = cornerHeld.MergeFrom(corner, LodMergeMode.FillMissing);
        c.Eq(LodSection.EdgeMinusX | LodSection.EdgeMinusZ, cornerMerged.TouchedEdges,
            "a corner fill touches exactly the two edges it sits on");
    }
}
