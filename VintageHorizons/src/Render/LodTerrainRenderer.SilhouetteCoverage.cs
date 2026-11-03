using VintageHorizons.Silhouette;

namespace VintageHorizons;

/// <summary>
/// Remembers each 512-block tile's cached-terrain coverage for Farseer and the seam fog.
///
/// Worked out afresh, a tile inside the cached terrain cost about 8 microseconds a frame, 1.8 ms
/// at an 8192-block Cache (dev.29's timing line), and almost every frame gave the answer the
/// frame before had. Two layers are kept per tile:
///
/// 1. Which sections are resident at each level and which 32-block cells their ground covers.
///    This depends on the resident meshes alone, so it is exact bookkeeping: a section installed
///    or removed marks the tiles under it to read their terrain again.
/// 2. The answer itself. It also depends on the camera, through each cell's wanted level and the
///    draw's cap, and on the snapped camera and the terrain's end. Alongside it goes how far the
///    camera, and separately the terrain's end, may move before any cell could cross one of those
///    lines. Distances never change faster than the camera moves, so inside that margin the answer
///    is exactly what working it out again would give. The snapped camera, the thresholds and the
///    cap are compared outright.
///
/// Vanilla's readiness is not remembered: it changes as chunks finish and only touches the tiles
/// near the player, so it is added afresh on every call.
/// </summary>
public partial class LodTerrainRenderer
{
    /// <summary>
    /// Cached is the default. Fresh works every answer out each frame, as before 0.5.2-dev.30, for
    /// an A/B. Check remembers as Cached does and also works each answer out afresh, counting any
    /// difference into the stats log: a difference is a missed trigger, a hole or a doubled edge.
    /// </summary>
    internal enum SilhouetteCoverageMode { Cached, Fresh, Check }

    internal SilhouetteCoverageMode CoverageMode { get; set; } = SilhouetteCoverageMode.Cached;

    /// <summary>One tile's answer and how far the camera and the terrain's end may move under it.</summary>
    internal readonly record struct SilhouetteCoverageDecision(
        LodSilhouetteCoverage Covered, double CameraMargin, double EndMargin);

    sealed class SilhouetteCoverageEntry
    {
        public readonly LodSilhouetteCoverage[] Resident = new LodSilhouetteCoverage[LodWorld.MaxLevel + 1];
        public readonly LodSilhouetteCoverage[] Covered = new LodSilhouetteCoverage[LodWorld.MaxLevel + 1];
        public bool LevelsValid;
        public bool DecisionValid;
        public int Context;
        public double CameraX, CameraZ;
        public float TerrainEnd;
        public SilhouetteCoverageDecision Decision;
        public long LastUsedFrame;
    }

    // Answers within this of a line are worked out again: rounding, not geometry.
    const double SilhouetteCoverageMarginSlack = 0.01;
    const long SilhouetteCoverageUnusedFrames = 1200;

    readonly Dictionary<long, SilhouetteCoverageEntry> silhouetteCoverageCache = new();
    long silhouetteCoverageFrame;
    int silhouetteCoverageContext;
    double contextSnappedX = double.NaN, contextSnappedZ = double.NaN;
    int contextDetailRevision = int.MinValue;
    float contextCullDistanceSquared = float.NaN;
    int coverageMismatchesLogged;

    internal long SilhouetteCoverageReused { get; private set; }
    internal long SilhouetteCoverageWorkedOut { get; private set; }
    internal long SilhouetteCoverageTerrainReads { get; private set; }
    internal long SilhouetteCoverageChecked { get; private set; }
    internal long SilhouetteCoverageMismatches { get; private set; }
    internal long SilhouetteCoverageMismatchesTotal { get; private set; }

    void ResetSilhouetteCoverageCounts()
    {
        SilhouetteCoverageReused = 0;
        SilhouetteCoverageWorkedOut = 0;
        SilhouetteCoverageTerrainReads = 0;
        SilhouetteCoverageChecked = 0;
        SilhouetteCoverageMismatches = 0;
    }

    /// <summary>Main thread, once a frame: forgets tiles nothing has asked about for a while.</summary>
    void AgeSilhouetteCoverageCache()
    {
        silhouetteCoverageFrame++;
        if (silhouetteCoverageFrame % SilhouetteCoverageUnusedFrames != 0
            || silhouetteCoverageCache.Count == 0) return;
        long oldest = silhouetteCoverageFrame - SilhouetteCoverageUnusedFrames;
        List<long>? stale = null;
        foreach (var (key, entry) in silhouetteCoverageCache)
            if (entry.LastUsedFrame < oldest) (stale ??= new()).Add(key);
        if (stale == null) return;
        foreach (long key in stale) silhouetteCoverageCache.Remove(key);
    }

    /// <summary>A section's mesh came or went: the tiles under it read their terrain again.</summary>
    void NoteSilhouetteResidencyChanged(long sectionKey)
    {
        if (silhouetteCoverageCache.Count == 0) return;
        int footprint = LodSection.SectionBlocks << LodWorld.KeyLevel(sectionKey);
        long minBlockX = (long)LodWorld.KeySx(sectionKey) * footprint;
        long minBlockZ = (long)LodWorld.KeySz(sectionKey) * footprint;
        const int tileBlocks = LodSilhouetteTile.TileBlocks;
        int firstTileX = (int)Math.Floor(minBlockX / (double)tileBlocks);
        int firstTileZ = (int)Math.Floor(minBlockZ / (double)tileBlocks);
        int lastTileX = (int)Math.Floor((minBlockX + footprint - 1) / (double)tileBlocks);
        int lastTileZ = (int)Math.Floor((minBlockZ + footprint - 1) / (double)tileBlocks);
        for (int tileZ = firstTileZ; tileZ <= lastTileZ; tileZ++)
            for (int tileX = firstTileX; tileX <= lastTileX; tileX++)
                if (silhouetteCoverageCache.TryGetValue(LodSilhouetteTile.KeyOf(tileX, tileZ),
                        out SilhouetteCoverageEntry? entry))
                    entry.LevelsValid = false;
    }

    void ClearSilhouetteCoverageCache() => silhouetteCoverageCache.Clear();

    /// <summary>
    /// The tile's cells cached terrain owns, without vanilla's readiness: remembered, worked out
    /// afresh or both, by <see cref="CoverageMode"/>.
    /// </summary>
    LodSilhouetteCoverage SilhouetteCachedTerrainCoverage(int tileX, int tileZ,
        double snappedCameraX, double snappedCameraZ, float terrainEnd)
    {
        float cullDistanceSquared = LodFarDistance.CullDistanceSquared(FarViewDistanceCap);
        if (CoverageMode == SilhouetteCoverageMode.Fresh)
            return FreshSilhouetteCoverage(tileX, tileZ, snappedCameraX, snappedCameraZ,
                terrainEnd, cullDistanceSquared).Covered;

        if (snappedCameraX != contextSnappedX || snappedCameraZ != contextSnappedZ
            || LodWorld.DetailPolicyRevision != contextDetailRevision
            || cullDistanceSquared != contextCullDistanceSquared)
        {
            contextSnappedX = snappedCameraX;
            contextSnappedZ = snappedCameraZ;
            contextDetailRevision = LodWorld.DetailPolicyRevision;
            contextCullDistanceSquared = cullDistanceSquared;
            silhouetteCoverageContext++;
        }

        long key = LodSilhouetteTile.KeyOf(tileX, tileZ);
        if (!silhouetteCoverageCache.TryGetValue(key, out SilhouetteCoverageEntry? entry))
        {
            entry = new SilhouetteCoverageEntry();
            silhouetteCoverageCache[key] = entry;
        }
        entry.LastUsedFrame = silhouetteCoverageFrame;

        if (!entry.LevelsValid)
        {
            FillSilhouetteResidentLevels(tileX, tileZ, entry.Resident, entry.Covered);
            entry.LevelsValid = true;
            entry.DecisionValid = false;
            SilhouetteCoverageTerrainReads++;
        }

        double movedX = camPos.X - entry.CameraX;
        double movedZ = camPos.Z - entry.CameraZ;
        SilhouetteCoverageDecision held = entry.Decision;
        if (entry.DecisionValid && entry.Context == silhouetteCoverageContext
            && movedX * movedX + movedZ * movedZ < held.CameraMargin * held.CameraMargin
            && Math.Abs(terrainEnd - entry.TerrainEnd) < held.EndMargin)
        {
            SilhouetteCoverageReused++;
        }
        else
        {
            entry.Decision = DecideSilhouetteCoverage(entry.Resident, entry.Covered, tileX, tileZ,
                camPos.X, camPos.Z, snappedCameraX, snappedCameraZ, terrainEnd, cullDistanceSquared);
            entry.DecisionValid = true;
            entry.Context = silhouetteCoverageContext;
            entry.CameraX = camPos.X;
            entry.CameraZ = camPos.Z;
            entry.TerrainEnd = terrainEnd;
            SilhouetteCoverageWorkedOut++;
        }

        if (CoverageMode == SilhouetteCoverageMode.Check)
            CheckSilhouetteCoverage(tileX, tileZ, entry.Decision.Covered, snappedCameraX,
                snappedCameraZ, terrainEnd, cullDistanceSquared);
        return entry.Decision.Covered;
    }

    SilhouetteCoverageDecision FreshSilhouetteCoverage(int tileX, int tileZ,
        double snappedCameraX, double snappedCameraZ, float terrainEnd, float cullDistanceSquared)
    {
        Span<LodSilhouetteCoverage> residentByLevel =
            stackalloc LodSilhouetteCoverage[LodWorld.MaxLevel + 1];
        Span<LodSilhouetteCoverage> coveredByLevel =
            stackalloc LodSilhouetteCoverage[LodWorld.MaxLevel + 1];
        FillSilhouetteResidentLevels(tileX, tileZ, residentByLevel, coveredByLevel);
        return DecideSilhouetteCoverage(residentByLevel, coveredByLevel, tileX, tileZ,
            camPos.X, camPos.Z, snappedCameraX, snappedCameraZ, terrainEnd, cullDistanceSquared);
    }

    void CheckSilhouetteCoverage(int tileX, int tileZ, LodSilhouetteCoverage remembered,
        double snappedCameraX, double snappedCameraZ, float terrainEnd, float cullDistanceSquared)
    {
        LodSilhouetteCoverage fresh = FreshSilhouetteCoverage(tileX, tileZ, snappedCameraX,
            snappedCameraZ, terrainEnd, cullDistanceSquared).Covered;
        SilhouetteCoverageChecked++;
        if (fresh == remembered) return;
        SilhouetteCoverageMismatches++;
        SilhouetteCoverageMismatchesTotal++;
        if (coverageMismatchesLogged >= 20) return;
        coverageMismatchesLogged++;
        LodSilhouetteCoverage onlyRemembered = remembered.Intersect(new LodSilhouetteCoverage(
            ~fresh.Word0, ~fresh.Word1, ~fresh.Word2, ~fresh.Word3));
        LodSilhouetteCoverage onlyFresh = fresh.Intersect(new LodSilhouetteCoverage(
            ~remembered.Word0, ~remembered.Word1, ~remembered.Word2, ~remembered.Word3));
        capi.Logger.Warning(
            "[Landscapes] Farseer coverage check: tile {0},{1} remembered {2} cells a fresh answer "
            + "does not give Farseer-free (a possible hole) and missed {3} (a possible doubled edge); "
            + "camera {4:0},{5:0}, terrain end {6:0}",
            tileX, tileZ, CountCells(onlyRemembered), CountCells(onlyFresh),
            camPos.X, camPos.Z, terrainEnd);
    }

    static int CountCells(LodSilhouetteCoverage coverage) =>
        System.Numerics.BitOperations.PopCount(coverage.Word0)
        + System.Numerics.BitOperations.PopCount(coverage.Word1)
        + System.Numerics.BitOperations.PopCount(coverage.Word2)
        + System.Numerics.BitOperations.PopCount(coverage.Word3);

    /// <summary>
    /// For every level, the tile's cells some resident section spans and the cells its ground
    /// covers. Depends on the resident meshes alone.
    /// </summary>
    void FillSilhouetteResidentLevels(int tileX, int tileZ,
        Span<LodSilhouetteCoverage> residentByLevel, Span<LodSilhouetteCoverage> coveredByLevel)
    {
        residentByLevel.Clear();
        coveredByLevel.Clear();
        int tileBlockX = tileX * LodSilhouetteTile.TileBlocks;
        int tileBlockZ = tileZ * LodSilhouetteTile.TileBlocks;
        int tileMaxBlockX = tileBlockX + LodSilhouetteTile.TileBlocks;
        int tileMaxBlockZ = tileBlockZ + LodSilhouetteTile.TileBlocks;
        for (int level = 0; level <= LodWorld.MaxLevel; level++)
        {
            int footprint = LodSection.SectionBlocks << level;
            int firstSectionX = (int)Math.Floor(tileBlockX / (double)footprint);
            int firstSectionZ = (int)Math.Floor(tileBlockZ / (double)footprint);
            int lastSectionX = (int)Math.Floor((tileMaxBlockX - 1) / (double)footprint);
            int lastSectionZ = (int)Math.Floor((tileMaxBlockZ - 1) / (double)footprint);
            for (int sectionZ = firstSectionZ; sectionZ <= lastSectionZ; sectionZ++)
            {
                for (int sectionX = firstSectionX; sectionX <= lastSectionX; sectionX++)
                {
                    long key = LodWorld.SectionKey(level, sectionX, sectionZ);
                    if (!liveMeshStats.TryGetValue(key, out LodLiveMeshStats resident)
                        || resident.OpaqueIndices == 0) continue;

                    int sectionBlockX = sectionX * footprint;
                    int sectionBlockZ = sectionZ * footprint;
                    int firstCellX = Math.Max(0,
                        (sectionBlockX - tileBlockX) / LodMeshGroundCoverage.CellBlocks);
                    int firstCellZ = Math.Max(0,
                        (sectionBlockZ - tileBlockZ) / LodMeshGroundCoverage.CellBlocks);
                    int lastCellX = Math.Min(LodSilhouetteField.CoverageCellsPerEdge,
                        (sectionBlockX + footprint - tileBlockX)
                            / LodMeshGroundCoverage.CellBlocks);
                    int lastCellZ = Math.Min(LodSilhouetteField.CoverageCellsPerEdge,
                        (sectionBlockZ + footprint - tileBlockZ)
                            / LodMeshGroundCoverage.CellBlocks);
                    for (int cellZ = firstCellZ; cellZ < lastCellZ; cellZ++)
                    {
                        for (int cellX = firstCellX; cellX < lastCellX; cellX++)
                        {
                            residentByLevel[level] =
                                residentByLevel[level].WithCovered(cellX, cellZ);
                            int blockX = tileBlockX
                                + cellX * LodMeshGroundCoverage.CellBlocks;
                            int blockZ = tileBlockZ
                                + cellZ * LodMeshGroundCoverage.CellBlocks;
                            int localCellX = (blockX - sectionBlockX)
                                / LodMeshGroundCoverage.CellBlocks;
                            int localCellZ = (blockZ - sectionBlockZ)
                                / LodMeshGroundCoverage.CellBlocks;
                            if (resident.OpaqueGroundCoverage.Covered(localCellX, localCellZ))
                                coveredByLevel[level] =
                                    coveredByLevel[level].WithCovered(cellX, cellZ);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Which of the tile's cells cached terrain owns from this camera, and how far the camera and
    /// the terrain's end may move before any of them could change hands. Each cell starts at its
    /// distance-selected level (<see cref="SelectStableSilhouetteLevel"/>), and is owned when that
    /// level's ground covers it, its section passes the draw's cap and the whole cell lies inside
    /// the terrain's end. A cell no level's ground covers is Farseer's wherever the camera stands,
    /// so it sets no margin.
    /// </summary>
    internal static SilhouetteCoverageDecision DecideSilhouetteCoverage(
        ReadOnlySpan<LodSilhouetteCoverage> residentByLevel,
        ReadOnlySpan<LodSilhouetteCoverage> coveredByLevel,
        int tileX, int tileZ, double cameraX, double cameraZ,
        double snappedCameraX, double snappedCameraZ, float terrainEnd, float cullDistanceSquared)
    {
        int tileBlockX = tileX * LodSilhouetteTile.TileBlocks;
        int tileBlockZ = tileZ * LodSilhouetteTile.TileBlocks;
        LodSilhouetteCoverage anyCovered = LodSilhouetteCoverage.Empty;
        foreach (LodSilhouetteCoverage level in coveredByLevel) anyCovered = anyCovered.Union(level);

        double cull = cullDistanceSquared == float.MaxValue
            ? double.PositiveInfinity : Math.Sqrt(cullDistanceSquared);
        double reach = terrainEnd - LodFarDistance.OwnedEdgeSlackBlocks;
        double cameraMargin = double.PositiveInfinity;
        double endMargin = double.PositiveInfinity;
        LodSilhouetteCoverage result = LodSilhouetteCoverage.Empty;
        const int cellBlocks = LodMeshGroundCoverage.CellBlocks;
        for (int cellZ = 0; cellZ < LodSilhouetteField.CoverageCellsPerEdge; cellZ++)
        {
            for (int cellX = 0; cellX < LodSilhouetteField.CoverageCellsPerEdge; cellX++)
            {
                if (!anyCovered.Covered(cellX, cellZ)) continue;
                double cellMinX = tileBlockX + cellX * cellBlocks;
                double cellMinZ = tileBlockZ + cellZ * cellBlocks;
                double dx = Math.Max(0, Math.Max(cellMinX - cameraX,
                    cameraX - (cellMinX + cellBlocks)));
                double dz = Math.Max(0, Math.Max(cellMinZ - cameraZ,
                    cameraZ - (cellMinZ + cellBlocks)));
                double distanceSq = dx * dx + dz * dz;
                int wanted = Math.Clamp(LodWorld.WantedLevelForSq(distanceSq), 0, LodWorld.MaxLevel);
                cameraMargin = Math.Min(cameraMargin, ThresholdMargin(Math.Sqrt(distanceSq)));

                int selected = SelectStableSilhouetteLevel(residentByLevel, wanted, cellX, cellZ);
                if (selected < 0 || !coveredByLevel[selected].Covered(cellX, cellZ)) continue;

                int footprint = LodSection.SectionBlocks << selected;
                double originX = Math.Floor(cellMinX / footprint) * footprint;
                double originZ = Math.Floor(cellMinZ / footprint) * footprint;
                if (!double.IsPositiveInfinity(cull))
                {
                    double centreX = originX + footprint / 2.0 - cameraX;
                    double centreZ = originZ + footprint / 2.0 - cameraZ;
                    cameraMargin = Math.Min(cameraMargin,
                        Math.Abs(Math.Sqrt(centreX * centreX + centreZ * centreZ) - cull));
                }
                if (LodFarDistance.SectionPastCull(originX, originZ, footprint,
                        cameraX, cameraZ, cullDistanceSquared)) continue;

                // The farthest corner, as CellInsideTerrain judges it, from the snapped camera.
                double farX = Math.Max(Math.Abs(cellMinX - snappedCameraX),
                    Math.Abs(cellMinX + cellBlocks - snappedCameraX));
                double farZ = Math.Max(Math.Abs(cellMinZ - snappedCameraZ),
                    Math.Abs(cellMinZ + cellBlocks - snappedCameraZ));
                endMargin = Math.Min(endMargin,
                    Math.Abs(Math.Sqrt(farX * farX + farZ * farZ) - reach));
                if (!LodFarDistance.CellInsideTerrain(cellMinX, cellMinZ, cellBlocks,
                        snappedCameraX, snappedCameraZ, terrainEnd)) continue;
                result = result.WithCovered(cellX, cellZ);
            }
        }
        return new SilhouetteCoverageDecision(result,
            cameraMargin - SilhouetteCoverageMarginSlack, endMargin - SilhouetteCoverageMarginSlack);
    }

    /// <summary>How far a distance is from the nearest level threshold.</summary>
    static double ThresholdMargin(double distance)
    {
        double margin = double.PositiveInfinity;
        for (int level = 1; level <= LodWorld.MaxLevel; level++)
            margin = Math.Min(margin, Math.Abs(distance - LodWorld.ThresholdForLevel(level)));
        return margin;
    }
}
