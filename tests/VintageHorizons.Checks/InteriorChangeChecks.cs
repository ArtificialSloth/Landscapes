namespace VintageHorizons.Checks;

/// <summary>
/// A change that touches no edge is still a change.
///
/// G138: `ReplaceColumns` returns which section EDGES a mutation affected, and an interior block
/// placement correctly produces `EdgeNone` - the section changed, but no neighbour can see it.
/// Both callers treated that zero as "nothing happened" and skipped `MarkChanged`, so new runs
/// and palette entries could enter resident RAM without dirtying the level-0 mesh, the
/// persistent row or the mip obligation. Texture registration could succeed while the terrain
/// shown outside vanilla's range stayed stale, and a later EDGE mutation would publish the
/// accumulated state and hide the failure during ordinary play.
///
/// The two halves are asserted separately here, because the whole defect was one value trying
/// to answer two questions.
/// </summary>
public static class InteriorChangeChecks
{
    public static void Run(Check c)
    {
        AnInteriorChangeReportsItselfWithNoEdges(c);
        AnEdgeChangeReportsBoth(c);
        AnIdenticalReplacementReportsNothing(c);
        MarkChangedDirtiesTheSectionWithoutNeighbours(c);
    }

    static ulong[] Column(int paletteId, int yBottom, int yTop) =>
        new[] { LodSection.PackRun(paletteId, yTop, yBottom) };

    /// <summary>A section with every column captured, so replacements are real changes.</summary>
    static LodSection Filled(int paletteId = 1)
    {
        var section = new LodSection();
        section.FindOrAddPaletteEntry(1, 0x00FFFFFF, 0, 0, 0);
        var batch = new ulong[]?[LodSection.GridSize * LodSection.GridSize];
        for (int i = 0; i < batch.Length; i++) batch[i] = Column(paletteId, 0, 4);
        section.ReplaceColumns(batch, out _);
        return section;
    }

    /// <summary>A column that touches none of the four section edges.</summary>
    static int InteriorColumn()
    {
        const int mid = LodSection.GridSize / 2;
        return mid * LodSection.GridSize + mid;
    }

    static void AnInteriorChangeReportsItselfWithNoEdges(Check c)
    {
        LodSection section = Filled();
        var batch = new ulong[]?[LodSection.GridSize * LodSection.GridSize];
        batch[InteriorColumn()] = Column(1, 0, 9);

        int edges = section.ReplaceColumns(batch, out bool changed);

        c.True(changed, "a block placed in the middle of a section IS a change");
        c.Eq(LodSection.EdgeNone, edges, "and it touches no edge, which is why it was missed");
    }

    static void AnEdgeChangeReportsBoth(Check c)
    {
        LodSection section = Filled();
        var batch = new ulong[]?[LodSection.GridSize * LodSection.GridSize];
        batch[0] = Column(1, 0, 9);   // the -X/-Z corner

        int edges = section.ReplaceColumns(batch, out bool changed);

        c.True(changed, "a change on an edge is a change too");
        c.True(edges != LodSection.EdgeNone, "and it names the edges it touched");
    }

    static void AnIdenticalReplacementReportsNothing(Check c)
    {
        LodSection section = Filled();
        var batch = new ulong[]?[LodSection.GridSize * LodSection.GridSize];
        batch[InteriorColumn()] = Column(1, 0, 4);   // byte-identical to what is there

        int edges = section.ReplaceColumns(batch, out bool changed);

        c.False(changed, "replacing a column with the same runs changes nothing");
        c.Eq(LodSection.EdgeNone, edges, "and touches no edge");
    }

    /// <summary>
    /// The other half of the fix: MarkChanged with no edges must still advance the section's
    /// own revisions. It already did - the defect was never reaching it - so this pins the
    /// behaviour the call sites now depend on.
    /// </summary>
    static void MarkChangedDirtiesTheSectionWithoutNeighbours(Check c)
    {
        var world = new LodWorld();
        long key = LodWorld.SectionKey(0, 4, 4);
        long west = LodWorld.SectionKey(0, 3, 4);
        world.GetOrCreateSection(key);
        world.GetOrCreateSection(west);
        world.RenderDirty.Clear();
        world.MipDirty.Clear();

        long before = world.ContentRevision(key);
        world.MarkChanged(key, LodSection.EdgeNone);

        c.True(world.ContentRevision(key) > before,
            "an interior change advances the section's content revision");
        c.True(world.RenderDirty.Contains(key), "and its mesh is rebuilt");
        c.True(world.MipDirty.Contains(key), "and its mip obligation is raised");
        c.False(world.RenderDirty.Contains(west),
            "while the neighbour, which cannot see it, is left alone");
    }
}
