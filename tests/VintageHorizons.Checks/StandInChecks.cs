namespace VintageHorizons.Checks;

/// <summary>
/// The 0.4.135 stand-in fix. A coarse section drawn in place of finer terrain that is not ready
/// used to cover its whole area, so one new piece of ground at the edge of what the game had
/// loaded put kilometre-wide coarse terrain over everything around the player. It now covers only
/// the quarters that are waiting, and freshly announced chunks are rechecked every frame until the
/// game takes them over.
///
/// The walk itself runs inside the renderer, so what is checked here is everything it rests on:
/// that a quarter means the same area to the traversal, the cluster streams and the shader, that
/// the clusters of the four quarters partition the geometry exactly, and that every draw path is
/// wired to the same quarter mask.
/// </summary>
public static class StandInChecks
{
    public static void Run(Check c)
    {
        QuarterBitsFollowChildKeys(c);
        StandInPolicy(c);
        foreach (int level in new[] { 0, 3, LodWorld.MaxLevel })
            ClustersPartitionTheSectionByQuarter(c, level);
    }

    /// <summary>
    /// A quarter bit names the child whose footprint it is. The traversal sets bits from child
    /// indices, the cluster filter from cluster cells and the shader from the fragment position;
    /// if any of the three numbered them differently a stand-in would cover one quarter and leave
    /// the waiting one empty - a hole, which is the one thing a stand-in exists to prevent.
    /// </summary>
    static void QuarterBitsFollowChildKeys(Check c)
    {
        long parent = LodWorld.SectionKey(4, 11, 7);
        for (int qz = 0; qz < 2; qz++)
        {
            for (int qx = 0; qx < 2; qx++)
            {
                long child = LodWorld.ChildKey(parent, qx, qz);
                c.Eq(LodWorld.KeySx(parent) * 2 + qx, LodWorld.KeySx(child),
                    $"child ({qx},{qz}) lies on the {(qx == 0 ? "low" : "high")} X half");
                c.Eq(LodWorld.KeySz(parent) * 2 + qz, LodWorld.KeySz(child),
                    $"child ({qx},{qz}) lies on the {(qz == 0 ? "low" : "high")} Z half");
            }
        }

        // The shader adds 1 for the high X half and 2 for the high Z half.
        c.Eq(1 << 1, LodStandInPolicy.QuarterBit(1, 0), "the high X half is bit one, as the shader numbers it");
        c.Eq(1 << 2, LodStandInPolicy.QuarterBit(0, 1), "the high Z half is bit two, as the shader numbers it");
        c.Eq(1 << 3, LodStandInPolicy.QuarterBit(1, 1), "and both is bit three");

        int perAxis = LodPackedClusterBuilder.SubdivisionsPerAxis;
        int columns = LodPackedClusterBuilder.ColumnsPerCell;
        for (int cell = 0; cell < LodPackedClusterBuilder.CellCount; cell++)
        {
            int centreX = cell % perAxis * columns + columns / 2;
            int centreZ = cell / perAxis * columns + columns / 2;
            int expected = LodStandInPolicy.QuarterBit(
                centreX < LodSection.GridSize / 2 ? 0 : 1,
                centreZ < LodSection.GridSize / 2 ? 0 : 1);
            c.Eq(expected, LodStandInPolicy.QuarterOfCluster(cell),
                $"cluster cell {cell} belongs to the quarter its columns lie in");
        }
    }

    static void StandInPolicy(Check c)
    {
        int nwSe = LodStandInPolicy.QuarterBit(0, 0) | LodStandInPolicy.QuarterBit(1, 1);
        c.Eq(nwSe, LodStandInPolicy.StandInQuarters(nwSe, quarterStandIns: true),
            "a stand-in covers exactly the quarters whose finer piece is waiting");
        c.Eq(0, LodStandInPolicy.StandInQuarters(nwSe, quarterStandIns: false),
            "with the fix off it covers its whole section, as before 0.4.135");
        c.Eq(0, LodStandInPolicy.StandInQuarters(LodStandInPolicy.AllQuarters, quarterStandIns: true),
            "all four quarters waiting is simply the whole section");
        c.Eq(0, LodStandInPolicy.StandInQuarters(0, quarterStandIns: true),
            "nothing waiting is no restriction");

        for (int cell = 0; cell < LodPackedClusterBuilder.CellCount; cell++)
            c.True(LodStandInPolicy.DrawsCluster(0, cell), $"a whole-section draw keeps cluster {cell}");
        int ne = LodStandInPolicy.QuarterBit(1, 0);
        c.True(LodStandInPolicy.DrawsCluster(ne, 2) && LodStandInPolicy.DrawsCluster(ne, 7),
            "a north-east stand-in keeps the north-east clusters");
        c.False(LodStandInPolicy.DrawsCluster(ne, 0) || LodStandInPolicy.DrawsCluster(ne, 15),
            "and drops the rest");

        c.Eq("whole", LodStandInPolicy.Describe(0), "zero is described as the whole section");
        c.Eq("whole", LodStandInPolicy.Describe(LodStandInPolicy.AllQuarters), "and so is all four");
        c.Eq("NW+SE", LodStandInPolicy.Describe(nwSe), "a partial stand-in names its quarters");
    }

    /// <summary>
    /// Selecting a quarter's clusters selects that quarter's geometry and nothing else. The ground
    /// here steps up in every quarter, so walls stand exactly on the quarter planes, which is where
    /// a face could otherwise be drawn by both sides or by neither.
    /// </summary>
    static void ClustersPartitionTheSectionByQuarter(Check c, int level)
    {
        long key = LodWorld.SectionKey(level, 3, 5);
        MeshResult mesh = LodMesher.BuildMesh(Fixtures.Job(SteppedSection(), key));
        int step = LodWorld.ColumnStepBlocks(level);
        float half = LodSection.GridSize / 2 * step;

        int total = mesh.ClusteredPackedOpaqueQuads.Length / LodPackedQuadFormat.WordsPerQuad;
        int selected = 0;
        for (int qz = 0; qz < 2; qz++)
        {
            for (int qx = 0; qx < 2; qx++)
            {
                int quarter = LodStandInPolicy.QuarterBit(qx, qz);
                foreach (LodPackedCluster cluster in mesh.PackedOpaqueClusters)
                {
                    if (!LodStandInPolicy.DrawsCluster(quarter, cluster.Cell)) continue;
                    selected += cluster.QuadCount;
                    c.True(cluster.MinX >= qx * half - 0.001f && cluster.MaxX <= (qx + 1) * half + 0.001f
                        && cluster.MinZ >= qz * half - 0.001f && cluster.MaxZ <= (qz + 1) * half + 0.001f,
                        $"L{level} cluster {cluster.Cell} lies inside quarter {LodStandInPolicy.Describe(quarter)}");
                }
            }
        }
        c.Eq(total, selected,
            $"L{level}: the four quarters' clusters hold every quad exactly once between them");

        bool planeWalls = false;
        foreach (LodPackedCluster cluster in mesh.PackedOpaqueClusters)
        {
            if (Math.Abs(cluster.MinX - half) < 0.001f || Math.Abs(cluster.MaxX - half) < 0.001f
                || Math.Abs(cluster.MinZ - half) < 0.001f || Math.Abs(cluster.MaxZ - half) < 0.001f)
                planeWalls = true;
        }
        c.True(planeWalls, $"L{level} setup: geometry reaches the quarter planes, so the partition is tested there");
    }

    /// <summary>Ground one height per quarter, eight blocks apart, so every quarter plane has a wall.</summary>
    static LodSection SteppedSection()
    {
        var section = new LodSection();
        section.FindOrAddPaletteEntry(blockId: 1, color: 0x00806040, flags: 0);
        for (int cz = 0; cz < LodSection.GridSize; cz++)
        {
            for (int cx = 0; cx < LodSection.GridSize; cx++)
            {
                int quarter = (cx < LodSection.GridSize / 2 ? 0 : 1) + (cz < LodSection.GridSize / 2 ? 0 : 2);
                section.SetColumn(LodSection.ColumnIndex(cx, cz),
                    new[] { LodSection.PackRun(0, 20 + 8 * quarter, 0) });
            }
        }
        return section;
    }

    /// <summary>
    /// Every path that draws a stand-in has to agree about which quarters it covers. None of these
    /// can be seen failing at runtime except as terrain drawn twice or not at all.
    /// </summary>
    public static void RunWiring(Check c)
    {
        string root = GameAssemblies.RepoRoot;
        string renderer = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src", "Render",
            "LodTerrainRenderer.cs"));
        string inputs = File.ReadAllText(Path.Combine(root, "VintageHorizons", "assets",
            "dodenslandscapes", "shaderincludes", "lodfragmentinputs.fsh"));
        string test = File.ReadAllText(Path.Combine(root, "VintageHorizons", "assets",
            "dodenslandscapes", "shaderincludes", "lodownershiptest.fsh"));
        string mod = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src",
            "VintageHorizonsModSystem.cs"));

        // The shader.
        int declared = inputs.IndexOf("uniform int vhStandInQuarters;", StringComparison.Ordinal);
        int indirectBlock = inputs.IndexOf("#ifdef VH_INDIRECT", StringComparison.Ordinal);
        c.True(declared > 0 && indirectBlock > declared,
            "every program declares the quarter mask, the indirect ones included, where it stays zero");
        c.True(test.Contains("float vhHalfSection = columnBlocks * 32.0;", StringComparison.Ordinal),
            "the shader's quarter is half of the section's sixty-four columns");
        c.True(test.Contains("(sectionLocal.x < vhHalfSection ? 0 : 1)", StringComparison.Ordinal)
            && test.Contains("(sectionLocal.z < vhHalfSection ? 0 : 2)", StringComparison.Ordinal),
            "and is numbered as LodStandInPolicy.QuarterBit numbers it");
        c.True(test.Contains("if ((vhStandInQuarters & (1 << vhQuarter)) == 0) discard;", StringComparison.Ordinal),
            "a fragment outside the covered quarters is discarded");
        c.True(test.Contains("if (vhStandInQuarters != 0)", StringComparison.Ordinal),
            "zero - every ordinary draw - skips the test");
        int quarterTest = test.IndexOf("if (vhStandInQuarters != 0)", StringComparison.Ordinal);
        int maskTest = test.IndexOf("if (maskEnabled == 1)", StringComparison.Ordinal);
        c.True(quarterTest > 0 && maskTest > quarterTest,
            "the quarter is decided before the ownership lookup, so a discarded fragment costs no fetch");

        // The draw paths.
        int setup = renderer.IndexOf("bool SetupSectionTransform(", StringComparison.Ordinal);
        int setupEnd = setup < 0 ? -1 : renderer.IndexOf("if (shadowBuilder != null)", setup, StringComparison.Ordinal);
        string setupBody = setup >= 0 && setupEnd > setup ? renderer[setup..setupEnd] : string.Empty;
        c.True(setupBody.Contains("prog.Uniform(\"vhStandInQuarters\", StandInQuartersFor(key));", StringComparison.Ordinal),
            "the passes drawn section by section - water, thin cover, depth, OIT - set the mask for every section");
        c.True(setupBody.IndexOf("if (!indirectPassActive)", StringComparison.Ordinal)
                < setupBody.IndexOf("vhStandInQuarters", StringComparison.Ordinal),
            "and only outside the batched walk, which selects clusters instead");
        c.True(renderer.Contains("builder.Add(section, lastSectionFacts, quarters: StandInQuartersFor(key))",
                StringComparison.Ordinal),
            "opaque ground records only the covered quarters' clusters");
        c.Eq(2, Count(renderer, "drawList.Clear();"), "the draw list is cleared in two places");
        c.Eq(2, Count(renderer, "standInQuarters.Clear();"),
            "and the quarter masks with it each time, so no mask outlives its frame");

        // The walk.
        int uncovered = renderer.IndexOf("int uncovered = UncoveredVisibleChildren(key);", StringComparison.Ordinal);
        int meshless = renderer.IndexOf("if (!hasMesh) return CollectChildren(key, skipQuarters: 0);", StringComparison.Ordinal);
        c.True(uncovered > 0 && meshless > uncovered,
            "the waiting children are counted before a meshless node descends, as they always were");
        string flattened = System.Text.RegularExpressions.Regex.Replace(renderer, @"\s+", " ");
        c.True(flattened.Contains("LodStandInPolicy.StandInQuarters(uncovered, StandInFix && clusterDrawingThisFrame)",
                StringComparison.Ordinal),
            "stand-ins are split only while the clustered path, the one that can honour a quarter, draws");
        c.True(renderer.Contains("CollectChildren(key, skipQuarters: quarters);", StringComparison.Ordinal)
            && renderer.Contains("standInQuarters[key] = (byte)quarters;", StringComparison.Ordinal),
            "a partial stand-in descends into its ready quarters and covers only the waiting ones");
        c.False(renderer.Contains("AllVisibleChildrenCovered(", StringComparison.Ordinal),
            "the all-or-nothing gate is gone");

        // The fresh-chunk recheck.
        c.True(renderer.Contains("readiness?.Watch(cell, Environment.TickCount64);", StringComparison.Ordinal),
            "every chunk the game announces is watched");
        int promoteWatched = renderer.IndexOf("readiness.PromoteWatched(", StringComparison.Ordinal);
        int promoteObserved = renderer.IndexOf("readiness.PromoteObserved(", StringComparison.Ordinal);
        c.True(promoteWatched > 0 && promoteObserved > promoteWatched,
            "watched cells go to the front before deferred observations are queued");
        c.True(renderer.Contains("promote: StandInFix", StringComparison.Ordinal),
            "and only while the fix is on");
        c.True(renderer.Contains("VanillaChunkGeometry.TryMeshStillComing(decisionChunk, out bool coming)",
                StringComparison.Ordinal),
            "a chunk whose mesh is not coming leaves the list at its first probe after upload");

        // The switch, and where the owner can see it.
        c.True(mod.Contains("ChatCommands.Create(\"dlstandin\")", StringComparison.Ordinal),
            ".dlstandin switches both fixes together for comparison");
    }

    static int Count(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
