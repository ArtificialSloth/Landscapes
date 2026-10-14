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
        c.True(fog.Contains("QueueSilhouetteFogVolume(farDistance, TerrainFogHeightAtGrid, globe: false)",
                StringComparison.Ordinal),
            "its heights are the cached surface, and the cached terrain's edge is not lowered by the globe");
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
}
