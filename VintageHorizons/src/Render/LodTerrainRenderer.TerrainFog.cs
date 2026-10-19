using System.Diagnostics;
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
    /// Tiles whose coverage is worked out again each second, round robin. Each costs a walk over
    /// the resident sections under it at every level. With no view-dependence here, a fixed
    /// share keeps the cost flat whichever way the camera faces. It was 16 a frame until
    /// 0.4.156: at 400 frames a second that was over 6,000 walks a second for an outline that
    /// moves as fast as terrain loads. Now it is the same 16 a frame at 60 frames a second, and
    /// the whole disc is still fresh within about half a second at a 32,000-block far distance.
    /// </summary>
    internal const int TerrainFogCoverageRefreshesPerSecond = 960;

    /// <summary>
    /// Tiles never worked out are a hole in the outline, so up to this many a frame are done at
    /// once whatever the budget; and no frame does more than this after a long pause.
    /// </summary>
    internal const int TerrainFogCoverageRefreshesPerFrameMax = 16;

    /// <summary>
    /// The disc of tiles is gathered again once the camera has moved this far, or the reach has
    /// changed; between, the same tiles stand.
    /// </summary>
    internal const double TerrainFogCollectMoveBlocks = 8;

    /// <summary>Where cached terrain begins to fade into the sky, as a share of its far distance.</summary>
    internal const float TerrainFogReachFraction = 0.75f;

    /// <summary>
    /// The foot of the cached terrain's wall at its edge. Every column is captured down to y = 1
    /// (LodWorker's capture loop) and the mesher walls off a missing neighbour from the column's
    /// top to its foot, so the wall the curtain must cover always reaches down to here.
    /// </summary>
    internal const float TerrainFogWallFootY = 1f;

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
    double terrainFogRefreshCredit;
    long terrainFogRefreshClock;
    double terrainFogCollectedX = double.NaN, terrainFogCollectedZ = double.NaN;
    float terrainFogCollectedRadius = -1f;

    // Whether any tile's coverage or the set of tiles changed since the outline was last
    // signed: until one does, the same signature would come out, so it is not worked out.
    bool terrainFogChanged = true;

    /// <summary>Whether the fog last built follows the cached terrain's edge rather than Farseer's seam.</summary>
    internal bool SilhouetteFogFollowsTerrain { get; private set; }

    internal int TerrainFogTileCount => terrainFogTiles.Count;

    /// <summary>The published field's ground range and grid, for `.dlfog stats`.</summary>
    internal string SilhouetteFogFieldSummary => silhouetteFogFieldTexture == 0
        ? "no field published"
        : $"field {silhouetteFogFieldWidth}x{silhouetteFogFieldHeight} at "
            + $"{silhouetteFogFieldTexelBlocks:0} blocks per texel, ground {silhouetteFogMinGroundY:0} to "
            + $"{silhouetteFogMaxGroundY:0}, {silhouetteFogBoundaryEdges} edges, reach {silhouetteFogReachBlocks:0}";

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
        terrainFogFrame++;
        CollectTerrainFogTiles(cam.X, cam.Z, radius);
        RefreshTerrainFogCoverage(cam.X, cam.Z, radius);

        if (!terrainFogChanged)
        {
            if (silhouetteSeamMask.HasEdges
                && (silhouetteFogGeometryDirty
                    || silhouetteFogFieldTexture == 0
                        && silhouetteFogBuildTask == null && silhouetteFogPendingBuild == null))
            {
                SilhouetteFogFollowsTerrain = true;
                terrainFogHeights.Clear();
                QueueSilhouetteFogVolume(farDistance, TerrainFogHeightAtGrid, globe: false, curtain: true);
            }
            return;
        }
        terrainFogChanged = false;

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
            QueueSilhouetteFogVolume(farDistance, TerrainFogHeightAtGrid, globe: false, curtain: true);
        }
        else if (silhouetteSeamMask.HasEdges
            && (silhouetteFogGeometryDirty
                || silhouetteFogFieldTexture == 0
                    && silhouetteFogBuildTask == null && silhouetteFogPendingBuild == null))
        {
            SilhouetteFogFollowsTerrain = true;
            terrainFogHeights.Clear();
            QueueSilhouetteFogVolume(farDistance, TerrainFogHeightAtGrid, globe: false, curtain: true);
        }
    }

    /// <summary>
    /// Every tile touching the disc, kept from frame to frame; tiles that left it go. Gathered
    /// again only once the camera has moved or the reach changed.
    /// </summary>
    void CollectTerrainFogTiles(double camX, double camZ, float radius)
    {
        const int tileBlocks = LodSilhouetteTile.TileBlocks;
        double movedX = camX - terrainFogCollectedX, movedZ = camZ - terrainFogCollectedZ;
        if (radius == terrainFogCollectedRadius
            && movedX * movedX + movedZ * movedZ < TerrainFogCollectMoveBlocks * TerrainFogCollectMoveBlocks)
            return;
        terrainFogCollectedX = camX;
        terrainFogCollectedZ = camZ;
        terrainFogCollectedRadius = radius;
        int before = terrainFogFrameTiles.Count;
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
                    terrainFogChanged = true;
                }
                tile.SeenFrame = terrainFogFrame;
                terrainFogFrameTiles.Add(tile);
            }
        }

        if (terrainFogFrameTiles.Count != before) terrainFogChanged = true;
        if (terrainFogTiles.Count == terrainFogFrameTiles.Count) return;
        terrainFogChanged = true;
        terrainFogStaleKeys.Clear();
        foreach ((long key, TerrainFogTile tile) in terrainFogTiles)
            if (tile.SeenFrame != terrainFogFrame) terrainFogStaleKeys.Add(key);
        foreach (long key in terrainFogStaleKeys) terrainFogTiles.Remove(key);
    }

    /// <summary>
    /// Works the coverage out again for a share of the disc's tiles each second, never-seen
    /// tiles first and at once.
    /// </summary>
    void RefreshTerrainFogCoverage(double camX, double camZ, float radius)
    {
        int count = terrainFogFrameTiles.Count;
        long now = Stopwatch.GetTimestamp();
        double seconds = terrainFogRefreshClock == 0 ? 0.0
            : (now - terrainFogRefreshClock) / (double)Stopwatch.Frequency;
        terrainFogRefreshClock = now;
        terrainFogRefreshCredit = Math.Min(TerrainFogCoverageRefreshesPerFrameMax,
            terrainFogRefreshCredit + seconds * TerrainFogCoverageRefreshesPerSecond);
        if (count == 0) return;

        // New ground first: a tile with no coverage is a hole in the outline.
        int fresh = TerrainFogCoverageRefreshesPerFrameMax;
        foreach (TerrainFogTile tile in terrainFogFrameTiles)
        {
            if (fresh == 0) return;
            if (tile.CoverageFrame >= 0) continue;
            RefreshTerrainFogTile(tile, camX, camZ, radius);
            fresh--;
            terrainFogRefreshCredit -= 1.0;
        }

        int budget = (int)Math.Max(0.0, terrainFogRefreshCredit);
        terrainFogRefreshCredit -= budget;
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
        LodSilhouetteCoverage available = TerrainFogAvailableCells(tile.TileX, tile.TileZ, camX, camZ, radius);
        LodSilhouetteCoverage replacements = SilhouetteStableReplacementCoverage(tile.TileX, tile.TileZ);
        if (tile.CoverageFrame < 0 || !SameCoverage(available, tile.Available)
            || !SameCoverage(replacements, tile.Replacements))
            terrainFogChanged = true;
        tile.Available = available;
        tile.Replacements = replacements;
        tile.CoverageFrame = terrainFogFrame;
    }

    static bool SameCoverage(LodSilhouetteCoverage a, LodSilhouetteCoverage b) =>
        a.Word0 == b.Word0 && a.Word1 == b.Word1 && a.Word2 == b.Word2 && a.Word3 == b.Word3;

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

    /// <summary>The next frame gathers the disc afresh and signs the outline again.</summary>
    void ForgetTerrainFogCollection()
    {
        terrainFogCollectedX = double.NaN;
        terrainFogCollectedZ = double.NaN;
        terrainFogCollectedRadius = -1f;
        terrainFogChanged = true;
    }

    /// <summary>
    /// Main-thread time per frame, averaged: Farseer's and the fog's preparation before the
    /// terrain draws, and the fog pass's own calls. The graphics card's work is not counted.
    /// </summary>
    internal double SilhouettePrepareMicros { get; private set; }
    internal double SilhouetteFogDrawMicros { get; private set; }

    internal void NoteSilhouettePrepare(long ticks) =>
        SilhouettePrepareMicros += (ticks * 1e6 / Stopwatch.Frequency - SilhouettePrepareMicros) * 0.01;

    void NoteSilhouetteFogDraw(long ticks) =>
        SilhouetteFogDrawMicros += (ticks * 1e6 / Stopwatch.Frequency - SilhouetteFogDrawMicros) * 0.01;

    /// <summary>
    /// Drops the fog with nothing kept: its tiles, outline and field, whichever edge built it.
    /// Switched off, the fog costs nothing (owner rule: an off switch removes the cost).
    /// </summary>
    void ClearTerrainFog()
    {
        terrainFogTiles.Clear();
        terrainFogFrameTiles.Clear();
        terrainFogHeights.Clear();
        ForgetTerrainFogCollection();
        SilhouetteFogFollowsTerrain = false;
        if (silhouetteSeamMask.Active) silhouetteSeamMask.Deactivate();
        if (silhouetteFogFieldTexture != 0 || silhouetteFogBuildTask != null
            || silhouetteFogPendingBuild != null)
            ClearSilhouetteFogField();
    }
}
