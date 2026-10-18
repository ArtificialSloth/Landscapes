using VintageHorizons.Silhouette;

namespace VintageHorizons.Checks;

/// <summary>
/// The seam fog without Farseer (owner, 2026-10-13): the outline is traced from the cached
/// terrain's own edge, over every tile in a disc around the player, and the disc's rim is not an
/// edge.
/// </summary>
public static class TerrainFogChecks
{
    public static void Run(Check c)
    {
        EdgesFormOnlyBetweenAvailableCells(c);
        TheDiscIsCellCentres(c);
        WiringFollowsTheTerrain(c);
        CurtainHangsDownTheWall(c);
    }

    static void EdgesFormOnlyBetweenAvailableCells(Check c)
    {
        // One tile: the west half is cached terrain, the east half is not.
        LodSilhouetteCoverage west = LodSilhouetteCoverage.Empty;
        for (int z = 0; z < 16; z++)
            for (int x = 0; x < 8; x++)
                west = west.WithCovered(x, z);

        var full = new LodSilhouetteSeamMask();
        full.BeginBuild(0, 0, 0, 0, 1);
        full.SetTile(0, 0, west);
        full.CompleteBuild();
        c.Eq(16, full.Boundaries.Count, "a whole tile traces the edge down its full height");

        // Only the northern half of the tile is inside the disc.
        LodSilhouetteCoverage north = LodSilhouetteCoverage.Empty;
        for (int z = 0; z < 8; z++)
            for (int x = 0; x < 16; x++)
                north = north.WithCovered(x, z);
        var partial = new LodSilhouetteSeamMask();
        partial.BeginBuild(0, 0, 0, 0, 2);
        partial.SetTile(0, 0, west, north);
        partial.CompleteBuild();
        c.Eq(8, partial.Boundaries.Count,
            "only the part inside the disc carries an edge, and the disc's rim carries none");
        c.Eq((byte)0, partial.StateAt(3, 12), "a cell outside the disc is no owner at all");
    }

    static void TheDiscIsCellCentres(Check c)
    {
        // Camera at a tile's corner, a radius of 256 blocks: only cells within reach count.
        LodSilhouetteCoverage disc = LodTerrainRenderer.TerrainFogAvailableCells(0, 0, 0, 0, 256f);
        c.True(disc.Covered(0, 0), "the cell beside the player is inside");
        c.True(disc.Covered(7, 0), "a cell centred 240 blocks out is inside");
        c.False(disc.Covered(8, 0), "a cell centred 272 blocks out is not");
        c.False(disc.Covered(7, 7), "nor one diagonally past the radius");
        c.True(LodTerrainRenderer.TerrainFogAvailableCells(0, 0, 256, 256, 1000f).IsFull,
            "a tile well inside the disc is available everywhere");
        c.True(LodTerrainRenderer.TerrainFogAvailableCells(10, 10, 0, 0, 1000f).IsEmpty,
            "and one far outside it nowhere");
        c.Eq(0.75f, LodTerrainRenderer.TerrainFogReachFraction,
            "the disc ends where cached terrain starts fading into the sky (lodsurfacefinish.fsh)");
    }

    static void WiringFollowsTheTerrain(Check c)
    {
        string src = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src", "Render");
        string fog = File.ReadAllText(Path.Combine(src, "LodTerrainRenderer.TerrainFog.cs"));
        string pass = File.ReadAllText(Path.Combine(src, "LodTerrainRenderer.Silhouette.cs"));
        string draw = File.ReadAllText(Path.Combine(src, "LodTerrainRenderer.SilhouetteFog.cs"));
        c.True(fog.Contains("SilhouetteStableReplacementCoverage(tile.TileX, tile.TileZ)", StringComparison.Ordinal),
            "the outline is the same ownership answer Farseer's fog uses, for any tile");
        c.True(fog.Contains(
                "QueueSilhouetteFogVolume(farDistance, TerrainFogHeightAtGrid, globe: false, curtain: true)",
                StringComparison.Ordinal),
            "its heights are the cached surface, the cached terrain's edge is not lowered by the "
                + "globe, and a curtain hangs down its wall");
        c.True(pass.Contains("if (silhouetteMeshes.Count == 0)\n        {\n            PrepareTerrainFogFrame();",
                StringComparison.Ordinal)
            || pass.Replace("\r\n", "\n").Contains(
                "if (silhouetteMeshes.Count == 0)\n        {\n            PrepareTerrainFogFrame();",
                StringComparison.Ordinal),
            "Farseer on but with no tile - a server that offers none - still gets the fog");
        c.False(draw.Contains("!silhouettes.Enabled", StringComparison.Ordinal),
            "drawing the fog no longer asks whether Farseer is on");
        c.True(fog.Contains("if (!SilhouetteFogEnabled || silhouetteFogVolumeFailed)", StringComparison.Ordinal)
                && fog.Contains("ClearTerrainFog();", StringComparison.Ordinal),
            "switched off, it keeps nothing");
    }

    /// <summary>
    /// The curtain (owner, 2026-10-18): with Farseer off the cached terrain's wall drops into
    /// empty sky, and the fog must cover it from the lip to its foot, with its tendrils falling.
    /// </summary>
    static void CurtainHangsDownTheWall(Check c)
    {
        // The worst stretch of wall stands out at the band's edge less its margin; the strip's
        // band ranges from its minimum to its maximum half-width.
        foreach (float stripHalf in new[]
        {
            LodSilhouetteFogVolume.MinStripHalfBlocks, 20f, LodSilhouetteFogVolume.MaxStripHalfBlocks,
        })
        {
            const float wallDepth = 120f;
            float wall = stripHalf - LodSilhouetteFogVolume.StripMarginBlocks;
            float thinnest = float.MaxValue;
            foreach (float height in new[] { -1f, -10f, -40f, -80f, -wallDepth + 0.5f })
                thinnest = MathF.Min(thinnest, LodSilhouetteFogVolume.CurtainCoverDepth(
                    wall, height, stripHalf, wallDepth, streak: 0f));
            c.True(thinnest >= 3.5f,
                $"with a {stripHalf:0}-block band, the curtain in front of the outermost wall is at "
                    + $"least 3.5 deep from lip to foot at its thinnest billow ({thinnest:0.00})");
        }

        const float band = 20f, depth = 100f;
        c.Eq(0f, LodSilhouetteFogVolume.CurtainExtinctionAt(
                -band - LodSilhouetteFogVolume.CurtainInnerFadeBlocks - 1f, -30f, band, depth, 1f),
            "inside the cached terrain, past the band, the curtain is in the rock and ends");
        c.Eq(0f, LodSilhouetteFogVolume.CurtainExtinctionAt(
                0f, -depth - LodSilhouetteFogVolume.CurtainBottomFadeBlocks - 1f, band, depth, 1f),
            "it ends a little below the wall's foot");
        c.Eq(0f, LodSilhouetteFogVolume.CurtainExtinctionAt(
                LodSilhouetteFogVolume.CurtainReach(band) + 1f, -30f, band, depth, 1f, tongueBlocks: 10f),
            "nothing of it lies past its reach, which bounds the march");
        c.True(LodSilhouetteFogVolume.CurtainExtinctionAt(band + 8f, -2f, band, depth, 0.5f)
                > LodSilhouetteFogVolume.CurtainExtinctionAt(band + 8f, -60f, band, depth, 0.5f),
            "it bulges out where it pours over the lip");
        c.Eq(1f, LodTerrainRenderer.TerrainFogWallFootY,
            "the wall reaches down to y = 1, where every captured column ends");

        string root = GameAssemblies.RepoRoot;
        string shader = File.ReadAllText(Path.Combine(root, "VintageHorizons", "assets",
            "dodenslandscapes", "shaders", "lodsilhouettefogvolume.fsh"));
        string draw = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src", "Render",
            "LodTerrainRenderer.SilhouetteFog.cs"));
        string farseer = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src", "Render",
            "LodTerrainRenderer.Silhouette.cs"));
        string worker = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src", "Lod",
            "LodWorker.cs"));
        c.True(worker.Contains("for (int y = startY; y >= 1; y--)", StringComparison.Ordinal),
            "the capture still runs every column down to y = 1, the curtain's floor");
        c.False(farseer.Contains("curtain: true", StringComparison.Ordinal),
            "Farseer's ground hides the wall: its fog has no curtain");
        c.True(shader.Contains("if (fogCurtainActive != 0 && height < CurtainLipRiseBlocks)",
                    StringComparison.Ordinal)
                && shader.Contains("uniform int fogCurtainActive;", StringComparison.Ordinal)
                && draw.Contains("p.Uniform(\"fogCurtainActive\", silhouetteFogCurtain ? 1 : 0);",
                    StringComparison.Ordinal),
            "the curtain is drawn only for a field that follows the cached terrain's edge");
        c.True(draw.Contains("silhouetteFogFallBlocks = (silhouetteFogFallBlocks", StringComparison.Ordinal)
                && draw.Contains("+ seconds * SilhouetteFogFallBlocksPerSecond)", StringComparison.Ordinal)
                && draw.Contains("p.Uniform(\"fogFallBlocks\", silhouetteFogFallBlocks);",
                    StringComparison.Ordinal)
                && shader.Contains("(path - fogFallBlocks) / CurtainStreakStretch",
                    StringComparison.Ordinal),
            "the tendrils flow by an integrated distance the drift dial scales, so drift 0 holds them");
        // 0.4.149's tendrils rode the wind as well as falling, and were stretched four times
        // along their fall: on screen they moved only sideways. 0.4.150's two cross-faded copies
        // pulsed, tore in a line where the flow turned, and crossed the top as fast as they fell
        // (owner, 2026-10-18).
        c.True(shader.Contains("vec2 along = fogWorld.xz - outward * signedDistance;",
                    StringComparison.Ordinal)
                && shader.Contains("float path = CurtainTopSlowdown * acrossTop - height;",
                    StringComparison.Ordinal)
                && !shader.Contains("phaseA", StringComparison.Ordinal)
                && !shader.Contains("vec3 s = vec3(world.x", StringComparison.Ordinal)
                && shader.Contains("float tongue = n.tongue * 2.0 * CurtainWanderBlocks;",
                    StringComparison.Ordinal)
                && LodSilhouetteFogVolume.CurtainStreakStretch <= 3f
                && LodSilhouetteFogVolume.CurtainTopSlowdown > 1f,
            "the curtain's tendrils and outline are anchored to the world, not the wind; they scroll "
                + "without restarting along the edge's own path, slower across the top than down");
        c.True(LodSilhouetteFogVolume.SpillDroop(20f, 20f) == 0f
                && LodSilhouetteFogVolume.SpillDroop(32f, 20f) > 5f
                && LodSilhouetteFogVolume.SpillDroop(44f, 20f) > 4f * LodSilhouetteFogVolume.SpillDroop(32f, 20f) - 0.01f
                && shader.Contains("height += droop;", StringComparison.Ordinal)
                && shader.Contains("stripHeight += droop;", StringComparison.Ordinal),
            "beside empty sky the fog rolls over the lip: its floor curves down past the band, "
                + "ever more steeply, instead of standing out level as a shelf");
        c.True(shader.Contains("float extinction = bank + strip + curtain;", StringComparison.Ordinal)
                && shader.Contains("curtain = max(curtain, oldCurtain);", StringComparison.Ordinal)
                && shader.Contains("-vhFogFloorBelow(curtainGap, ground, bankFloor)",
                    StringComparison.Ordinal)
                && shader.Contains("if (curtainGap > 0.0) verticalSkip = min(verticalSkip, curtainGap);",
                    StringComparison.Ordinal)
                && draw.Contains("fieldMinY = MathF.Min(fieldMinY, TerrainFogWallFootY",
                    StringComparison.Ordinal),
            "the march reaches down to the wall's foot beside the wall and never skips sideways "
                + "into the curtain; a dissolving field keeps its curtain while it fades");
    }
}
