using Vintagestory.API.MathTools;
using VintageHorizons.Silhouette;

namespace VintageHorizons;

/// <summary>
/// The seam fog without Farseer (owner, 2026-10-13): the same fog, over the cached terrain's own
/// edge where it meets nothing.
///
/// The fog never needed Farseer's terrain. Its outline is the cached terrain's ownership edge,
/// worked out by <see cref="SilhouetteStableReplacementCoverage(int, int)"/> from our own
/// resident meshes and the game's committed chunks; Farseer's tiles only framed where that edge
/// was looked for and said how high the ground there stood. Here the frame is every 512-block
/// tile in a disc around the player and the height is the cached terrain's own surface, so a
/// player who wants the fog without Farseer's horizon pays for neither its tiles nor its draws,
/// and the fog works on a server that offers no tiles at all.
///
/// The disc ends where cached terrain begins to fade into the sky, three quarters of the way to
/// its far distance (lodsurfacefinish.fsh). Past that the terrain already dissolves, and an edge
/// there needs no veil. A cell outside the disc is no owner at all, so the disc's own rim never
/// draws fog (<see cref="LodSilhouetteSeamMask.SetTile"/>).
/// </summary>
public partial class LodTerrainRenderer
{
    sealed class TerrainFogTile
    {
        public required int TileX;
        public required int TileZ;
        public LodSilhouetteCoverage Available;
        public LodSilhouetteCoverage Replacements;

        /// <summary>The frame its coverage was last worked out, or -1 while it never has been.</summary>
        public long CoverageFrame = -1;
        public long SeenFrame;
    }

    /// <summary>
    /// Tiles whose coverage is worked out again each frame, round robin. Each costs a walk over
    /// the resident sections under it at every level. Farseer's own fog refreshes the tiles in
    /// view every frame and eight more; with no view-dependence here, a fixed share keeps the
    /// cost flat whichever way the camera faces, and the whole disc is fresh within about half a
    /// second even at a 32,000-block far distance.
    /// </summary>
    const int TerrainFogCoverageRefreshesPerFrame = 16;

    /// <summary>Where cached terrain begins to fade into the sky, as a share of its far distance.</summary>
    internal const float TerrainFogReachFraction = 0.75f;

    readonly Dictionary<long, TerrainFogTile> terrainFogTiles = new();
    readonly List<TerrainFogTile> terrainFogFrameTiles = new();
    readonly List<long> terrainFogStaleKeys = new();

    /// <summary>
    /// Heights already read for the build being prepared. The path builder asks for a corner
    /// once per centreline point near it, and each answer walks the cached levels, where
    /// Farseer's own answer was one array read.
    /// </summary>
    readonly Dictionary<long, float> terrainFogHeights = new();
    int terrainFogRefreshCursor;
    long terrainFogFrame;

    /// <summary>Whether the fog last built follows the cached terrain's edge rather than Farseer's seam.</summary>
    internal bool SilhouetteFogFollowsTerrain { get; private set; }

    internal int TerrainFogTileCount => terrainFogTiles.Count;

    /// <summary>
    /// Render thread, in the same place as Farseer's own preparation: once per frame while
    /// Farseer draws nothing - switched off, or with no tile yet.
    /// </summary>
    void PrepareTerrainFogFrame()
    {
        if (!SilhouetteFogEnabled || silhouetteFogVolumeFailed)
        {
            ClearTerrainFog();
            return;
        }

        if (!silhouetteShaderWanted)
        {
            silhouetteShaderWanted = true;
            LoadSilhouetteShader();
        }
        if (!silhouetteShaderOk || silhouetteFogVolumeProg == null || silhouetteFogVolumeProg.LoadError
            || silhouetteFogUpsampleProg == null || silhouetteFogUpsampleProg.LoadError)
        {
            // A compile failure is a session failure, as it is for Farseer: nothing plausible
            // is drawn in the fog's place.
            StopSilhouetteFog("the seam fog shaders did not compile");
            return;
        }
        if (capi.Render.FrameWidth == 0)
        {
            silhouetteSeamMask.Deactivate();
            ClearSilhouetteFogField();
            return;
        }
        if (!PublishSilhouetteFogVolume()) return;

        Vec3d cam = capi.World.Player.Entity.CameraPos;
        float farDistance = EffectiveFarDistance;
        float radius = MathF.Max(LodSilhouetteTile.TileBlocks, farDistance * TerrainFogReachFraction);
        CollectTerrainFogTiles(cam.X, cam.Z, radius);
        RefreshTerrainFogCoverage(cam.X, cam.Z, radius);

        int minTileX = int.MaxValue, minTileZ = int.MaxValue;
        int maxTileX = int.MinValue, maxTileZ = int.MinValue;
        ulong signatureXor = 0;
        ulong signatureSum = 0;
        int known = 0;
        foreach (TerrainFogTile tile in terrainFogFrameTiles)
        {
            // A tile joins the outline once its coverage is known. Until then it is no owner,
            // so its neighbours form no edge against it.
            if (tile.CoverageFrame < 0) continue;
            known++;
            minTileX = Math.Min(minTileX, tile.TileX);
            minTileZ = Math.Min(minTileZ, tile.TileZ);
            maxTileX = Math.Max(maxTileX, tile.TileX);
            maxTileZ = Math.Max(maxTileZ, tile.TileZ);
            ulong tileSignature = TerrainFogTileSignature(tile);
            signatureXor ^= tileSignature;
            signatureSum += tileSignature;
        }

        if (known == 0)
        {
            if (silhouetteSeamMask.Active) silhouetteSeamMask.Deactivate();
            if (silhouetteFogFieldTexture != 0 || silhouetteFogBuildTask != null
                || silhouetteFogPendingBuild != null)
                ClearSilhouetteFogField();
            return;
        }

        // The edge's heights are read when a build is queued, not hashed here: they change
        // only with the terrain, and the coverage that frames them is what moves.
        ulong signature = signatureXor ^ RotateLeft(signatureSum, 23)
            ^ ((ulong)known * 0x9E3779B97F4A7C15UL) ^ 0x5445_5252_4149_4EUL;
        if (silhouetteSeamMask.BeginBuild(minTileX, minTileZ, maxTileX, maxTileZ, signature))
        {
            foreach (TerrainFogTile tile in terrainFogFrameTiles)
            {
                if (tile.CoverageFrame < 0) continue;
                silhouetteSeamMask.SetTile(tile.TileX, tile.TileZ, tile.Replacements, tile.Available);
            }
            silhouetteSeamMask.CompleteBuild();
            SilhouetteFogFollowsTerrain = true;
            terrainFogHeights.Clear();
            QueueSilhouetteFogVolume(farDistance, TerrainFogHeightAtGrid, globe: false);
        }
        else if (silhouetteSeamMask.HasEdges
            && (silhouetteFogGeometryDirty
                || silhouetteFogFieldTexture == 0
                    && silhouetteFogBuildTask == null && silhouetteFogPendingBuild == null))
        {
            SilhouetteFogFollowsTerrain = true;
            terrainFogHeights.Clear();
            QueueSilhouetteFogVolume(farDistance, TerrainFogHeightAtGrid, globe: false);
        }
    }

    /// <summary>Every tile touching the disc, kept from frame to frame; tiles that left it go.</summary>
    void CollectTerrainFogTiles(double camX, double camZ, float radius)
    {
        const int tileBlocks = LodSilhouetteTile.TileBlocks;
        terrainFogFrame++;
        terrainFogFrameTiles.Clear();
        double radiusSq = (double)radius * radius;
        int minTileX = (int)Math.Floor((camX - radius) / tileBlocks);
        int maxTileX = (int)Math.Floor((camX + radius) / tileBlocks);
        int minTileZ = (int)Math.Floor((camZ - radius) / tileBlocks);
        int maxTileZ = (int)Math.Floor((camZ + radius) / tileBlocks);
        for (int tileZ = minTileZ; tileZ <= maxTileZ; tileZ++)
        {
            for (int tileX = minTileX; tileX <= maxTileX; tileX++)
            {
                double relX = tileX * (double)tileBlocks - camX;
                double relZ = tileZ * (double)tileBlocks - camZ;
                double nearX = Math.Clamp(0, relX, relX + tileBlocks);
                double nearZ = Math.Clamp(0, relZ, relZ + tileBlocks);
                if (nearX * nearX + nearZ * nearZ > radiusSq) continue;

                long key = LodSilhouetteTile.KeyOf(tileX, tileZ);
                if (!terrainFogTiles.TryGetValue(key, out TerrainFogTile? tile))
                {
                    tile = new TerrainFogTile { TileX = tileX, TileZ = tileZ };
                    terrainFogTiles[key] = tile;
                }
                tile.SeenFrame = terrainFogFrame;
                terrainFogFrameTiles.Add(tile);
            }
        }

        if (terrainFogTiles.Count == terrainFogFrameTiles.Count) return;
        terrainFogStaleKeys.Clear();
        foreach ((long key, TerrainFogTile tile) in terrainFogTiles)
            if (tile.SeenFrame != terrainFogFrame) terrainFogStaleKeys.Add(key);
        foreach (long key in terrainFogStaleKeys) terrainFogTiles.Remove(key);
    }

    /// <summary>
    /// Works the coverage out again for a fixed share of the disc's tiles, never-seen tiles first.
    /// </summary>
    void RefreshTerrainFogCoverage(double camX, double camZ, float radius)
    {
        int count = terrainFogFrameTiles.Count;
        if (count == 0) return;
        int budget = TerrainFogCoverageRefreshesPerFrame;

        // New ground first: a tile with no coverage is a hole in the outline.
        foreach (TerrainFogTile tile in terrainFogFrameTiles)
        {
            if (budget == 0) return;
            if (tile.CoverageFrame >= 0) continue;
            RefreshTerrainFogTile(tile, camX, camZ, radius);
            budget--;
        }

        int start = terrainFogRefreshCursor % count;
        int refreshed = 0;
        for (int i = 0; i < count && refreshed < budget; i++)
        {
            TerrainFogTile tile = terrainFogFrameTiles[(start + i) % count];
            if (tile.CoverageFrame == terrainFogFrame) continue;
            RefreshTerrainFogTile(tile, camX, camZ, radius);
            refreshed++;
        }
        terrainFogRefreshCursor = (start + refreshed) % count;
    }

    void RefreshTerrainFogTile(TerrainFogTile tile, double camX, double camZ, float radius)
    {
        tile.Available = TerrainFogAvailableCells(tile.TileX, tile.TileZ, camX, camZ, radius);
        tile.Replacements = SilhouetteStableReplacementCoverage(tile.TileX, tile.TileZ);
        tile.CoverageFrame = terrainFogFrame;
    }

    /// <summary>The tile's 32-block cells whose centres lie inside the disc.</summary>
    internal static LodSilhouetteCoverage TerrainFogAvailableCells(
        int tileX, int tileZ, double camX, double camZ, float radius)
    {
        const int cells = LodSilhouetteField.CoverageCellsPerEdge;
        const int cellBlocks = LodSilhouetteField.CoverageCellBlocks;
        double radiusSq = (double)radius * radius;
        double originX = tileX * (double)LodSilhouetteTile.TileBlocks;
        double originZ = tileZ * (double)LodSilhouetteTile.TileBlocks;
        LodSilhouetteCoverage available = LodSilhouetteCoverage.Empty;
        for (int z = 0; z < cells; z++)
        {
            double dz = originZ + (z + 0.5) * cellBlocks - camZ;
            for (int x = 0; x < cells; x++)
            {
                double dx = originX + (x + 0.5) * cellBlocks - camX;
                if (dx * dx + dz * dz <= radiusSq) available = available.WithCovered(x, z);
            }
        }
        return available;
    }

    static ulong TerrainFogTileSignature(TerrainFogTile tile)
    {
        ulong hash = 1469598103934665603UL;
        void Add(ulong value)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        Add(unchecked((uint)tile.TileX));
        Add(unchecked((uint)tile.TileZ));
        Add(tile.Replacements.Word0);
        Add(tile.Replacements.Word1);
        Add(tile.Replacements.Word2);
        Add(tile.Replacements.Word3);
        Add(tile.Available.Word0);
        Add(tile.Available.Word1);
        Add(tile.Available.Word2);
        Add(tile.Available.Word3);
        hash ^= hash >> 32;
        hash *= 0xD6E8FEB86659FD93UL;
        return hash ^ (hash >> 32);
    }

    /// <summary>
    /// The ground at a 32-block grid point: the highest cached surface among the four columns
    /// touching it. On the edge some of the four are outside the cached terrain and answer
    /// nothing; the highest of the rest keeps the fog on top of a cliff edge rather than halfway
    /// down it. Sea level where none answers, as Farseer's own fog does.
    /// </summary>
    float TerrainFogHeightAtGrid(int gridX, int gridZ)
    {
        long key = ((long)gridX << 32) | (uint)gridZ;
        if (terrainFogHeights.TryGetValue(key, out float known)) return known;
        float height = ReadTerrainFogHeight(gridX, gridZ);
        terrainFogHeights[key] = height;
        return height;
    }

    float ReadTerrainFogHeight(int gridX, int gridZ)
    {
        int blockX = gridX * LodSilhouetteSeamMask.CellBlocks;
        int blockZ = gridZ * LodSilhouetteSeamMask.CellBlocks;
        int best = int.MinValue;
        for (int dz = -1; dz <= 0; dz++)
            for (int dx = -1; dx <= 0; dx++)
                if (CachedSurfaceAt(blockX + dx, blockZ + dz) is int top && top > best) best = top;
        return best == int.MinValue ? capi.World.SeaLevel : best;
    }

    /// <summary>
    /// The top of the cached column at a block, from the finest resident level that captured it,
    /// or null where nothing cached holds it. Flowers are passed over, so the answer is the
    /// ground they stand on.
    /// </summary>
    int? CachedSurfaceAt(int blockX, int blockZ)
    {
        for (int level = 0; level <= LodWorld.MaxLevel; level++)
        {
            int footprint = LodSection.SectionBlocks << level;
            int sectionX = FloorDiv(blockX, footprint);
            int sectionZ = FloorDiv(blockZ, footprint);
            if (!world.Sections.TryGetValue(LodWorld.SectionKey(level, sectionX, sectionZ),
                    out LodSection? section) || section.IsEmpty) continue;
            int columnX = (blockX - sectionX * footprint) >> level;
            int columnZ = (blockZ - sectionZ * footprint) >> level;
            int column = LodSection.ColumnIndex(columnX, columnZ);
            if (!section.Captured[column]) continue;
            foreach (ulong run in section.ColumnRuns(column))
            {
                int palette = LodSection.RunPaletteId(run);
                if ((uint)palette < (uint)section.Palette.Count
                    && (section.Palette[palette].Flags
                        & (LodPaletteEntry.FlagThin | LodPaletteEntry.FlagSkip)) != 0) continue;
                return LodSection.RunYTop(run);
            }
        }
        return null;
    }

    /// <summary>
    /// Drops the fog with nothing kept: its tiles, outline and field, whichever edge built it.
    /// Switched off, the fog costs nothing (owner rule: an off switch removes the cost).
    /// </summary>
    void ClearTerrainFog()
    {
        terrainFogTiles.Clear();
        terrainFogFrameTiles.Clear();
        terrainFogHeights.Clear();
        SilhouetteFogFollowsTerrain = false;
        if (silhouetteSeamMask.Active) silhouetteSeamMask.Deactivate();
        if (silhouetteFogFieldTexture != 0 || silhouetteFogBuildTask != null
            || silhouetteFogPendingBuild != null)
            ClearSilhouetteFogField();
    }
}
