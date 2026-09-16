using System.Collections.Concurrent;
using Vintagestory.API.Common;
using VintageHorizons.Net;

namespace VintageHorizons.Silhouette;

/// <summary>
/// Client half of horizon silhouettes (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER P1-P5).
///
/// It takes tile manifests, requests only missing revisions, decides which tiles need a new mesh,
/// and builds meshes
/// on worker threads from each tile's neighbourhood (<see cref="LodSilhouetteFill"/>). The
/// renderer uploads and draws the results. Everything here runs on the client main thread except
/// the network intake queue, store and mesh jobs. A held tile is never mutated: another immutable
/// tile replaces it whole, which is what lets a mesh job read its neighbourhood without a copy.
///
/// The saved client switch owns whether this object does any work. P5's local store opens only
/// after an enabled client reaches a server that offers silhouettes. Off closes it, drops all CPU
/// state and advances the epoch, which also makes every already-running worker result stale.
/// </summary>
internal sealed class LodSilhouetteClient : IDisposable
{
    const int MaxDecodesPerPump = 16;
    const int MaxJobsInFlight = 4;
    const int CoverageChecksPerPump = 48;

    readonly ILogger logger;
    readonly Func<bool> available;
    readonly System.Func<bool, int, long, bool> sendRequest;
    readonly System.Func<long[], long, bool> sendTileRequest;
    readonly ConcurrentQueue<AssistSilhouetteManifest> manifests = new();
    readonly ConcurrentQueue<AssistSilhouetteTile> arrivals = new();
    readonly ConcurrentQueue<MeshResult> finished = new();
    readonly Dictionary<long, LodSilhouetteTile> tiles = new();
    readonly List<long> tileOrder = new();
    readonly Dictionary<long, ulong> coverage = new();
    readonly Dictionary<long, int> meshVersion = new();
    readonly HashSet<long> dirty = new();
    readonly HashSet<long> networkConfirmed = new();
    readonly HashSet<long> storeReads = new();
    readonly HashSet<long> storeLoads = new();
    readonly LodSilhouetteTransferState transfer = new();
    LodSilhouetteStore? store;
    string? storePath;
    HashSet<long>? storedKeys;
    bool storeIndexTaken;
    static long nextSessionId;
    int coverageCursor;
    int lastPruneTileX = int.MinValue, lastPruneTileZ = int.MinValue;
    int jobsInFlight;
    bool requestSent;
    bool enabled;
    int seaLevel;
    double lastX, lastZ;
    long knownCells;
    long capturedCells, previewCells, acceleratorCells;

    public readonly record struct MeshResult(long Key, int TileX, int TileZ, int Version, int Epoch,
        LodSilhouetteMeshSet? Meshes, bool Removal = false);

    public LodSilhouetteClient(ILogger logger, Func<bool> available,
        System.Func<bool, int, long, bool> sendRequest,
        System.Func<long[], long, bool> sendTileRequest)
    {
        this.logger = logger;
        this.available = available;
        this.sendRequest = sendRequest;
        this.sendTileRequest = sendTileRequest;
        transfer.Reset(Interlocked.Increment(ref nextSessionId));
    }

    public bool Enabled => enabled;
    public int ReachBlocks { get; private set; } = LodSilhouetteBuilder.DefaultReachBlocks;

    /// <summary>Changes whenever everything held is dropped, so the renderer drops its meshes too.</summary>
    public int Epoch { get; private set; }

    public bool RequestSent => requestSent;
    public int TileCount => tiles.Count;
    public int OfferedTiles => transfer.Offered;
    public int TilesInFlight => transfer.InFlight;
    public int TilesRefused => transfer.Refused;
    public bool StoreOpen => store != null;
    public int StoredTiles => store?.StoredTiles ?? storedKeys?.Count ?? 0;
    public int StorePendingSaves => store?.PendingSaves ?? 0;

    /// <summary>Cells with a height, across every tile held.</summary>
    public long KnownCells => knownCells;
    public long CapturedCells => capturedCells;
    public long PreviewCells => previewCells;
    public long AcceleratorCells => acceleratorCells;

    public int PendingMeshes => dirty.Count + Volatile.Read(ref jobsInFlight);
    public int TilesReceived, TilesLoadedFromStore, TilesRejected, MeshesBuilt, EmptyMeshes, MeshFailures;

    public void SetEnabled(bool on)
    {
        if (enabled == on) return;
        enabled = on;
        if (on)
        {
            EnsureStore();
            return;
        }

        // Off is the last silhouette message until the player switches it back on (plan 4.10).
        if (requestSent && available()) sendRequest(false, ReachBlocks, transfer.SessionId);
        Clear();
        CloseStore(clearPath: false);
    }

    public void SetReachBlocks(int blocks)
    {
        int clamped = LodSilhouetteSettings.ClampReach(blocks);
        if (ReachBlocks == clamped) return;
        if (enabled && requestSent && available())
            sendRequest(false, ReachBlocks, transfer.SessionId);
        ReachBlocks = clamped;
        Clear();
    }

    /// <summary>Network thread.</summary>
    public void OnManifestArrived(AssistSilhouetteManifest manifest)
    {
        if (Volatile.Read(ref enabled)) manifests.Enqueue(manifest);
    }

    /// <summary>Network thread.</summary>
    public void OnTileArrived(AssistSilhouetteTile tile)
    {
        if (Volatile.Read(ref enabled)) arrivals.Enqueue(tile);
    }

    /// <summary>
    /// Supplies the current world's client-store path. Merely naming it never opens the file;
    /// enabled state and an advertised server feature are both required.
    /// </summary>
    public void SetStorePath(string? path)
    {
        if (string.Equals(storePath, path, StringComparison.OrdinalIgnoreCase)) return;
        CloseStore(clearPath: true);
        storePath = path;
        EnsureStore();
    }

    /// <summary>World leave. The session switch survives; the next join asks again.</summary>
    public void Reset()
    {
        Clear();
        CloseStore(clearPath: true);
    }

    void Clear()
    {
        requestSent = false;
        Epoch++;
        transfer.Reset(Interlocked.Increment(ref nextSessionId));
        tiles.Clear();
        tileOrder.Clear();
        coverage.Clear();
        meshVersion.Clear();
        dirty.Clear();
        networkConfirmed.Clear();
        storeReads.Clear();
        storeReads.UnionWith(storeLoads);
        coverageCursor = 0;
        lastPruneTileX = lastPruneTileZ = int.MinValue;
        knownCells = 0;
        capturedCells = previewCells = acceleratorCells = 0;
        while (manifests.TryDequeue(out _)) { }
        while (arrivals.TryDequeue(out _)) { }
        while (finished.TryDequeue(out _)) { }
    }

    void EnsureStore()
    {
        if (store != null || storePath == null || !enabled || !available()) return;
        try
        {
            // Unlike the server store, this is an exact cache of authoritative replies. A
            // server-side rebuild may restart its revisions, so merging an older client row
            // would preserve stale heights under a newly authoritative answer.
            store = new LodSilhouetteStore(storePath, logger, mergeStoredRows: false);
        }
        catch (Exception e)
        {
            logger.Warning("Horizon silhouettes: no client tile store this session, so rejoining "
                + "must fetch the tiles again: {0}", e.Message);
        }
    }

    void CloseStore(bool clearPath)
    {
        LodSilhouetteStore? closing = store;
        store = null;
        storedKeys = null;
        storeIndexTaken = false;
        storeReads.Clear();
        storeLoads.Clear();
        if (clearPath) storePath = null;
        closing?.Dispose();
    }

    /// <summary>Main thread, once per client tick.</summary>
    public void Pump(LodWorld world, double playerX, double playerZ, int worldSeaLevel)
    {
        if (!enabled) return;
        lastX = playerX;
        lastZ = playerZ;
        seaLevel = worldSeaLevel;
        PruneOutsideReach(playerX, playerZ);
        EnsureStore();
        PumpStore(playerX, playerZ);
        if (!requestSent)
        {
            if (available())
            {
                requestSent = sendRequest(true, ReachBlocks, transfer.SessionId);
                if (requestSent)
                    logger.Notification("Horizon silhouettes: asked the server for a {0:N0}-block reach.",
                        ReachBlocks);
            }
        }

        if (requestSent)
        {
            // One bounded manifest chunk per tick, mirroring the ordinary section path.
            if (manifests.TryDequeue(out AssistSilhouetteManifest? manifest))
                transfer.Apply(manifest);

            for (int n = 0; n < MaxDecodesPerPump
                && arrivals.TryDequeue(out AssistSilhouetteTile? message); n++)
                AcceptReply(message);
            RequestMissing(playerX, playerZ);
        }
        CheckCoverage(world);
        Schedule(world);
    }

    /// <summary>
    /// Take completed local reads, then keep the store's bounded read allowance full with the
    /// nearest not-yet-read tiles inside the moving reach. Store decode happens on its owning
    /// thread; only immutable tiles arrive here.
    /// </summary>
    void PumpStore(double playerX, double playerZ)
    {
        LodSilhouetteStore? active = store;
        if (active == null) return;
        if (!storeIndexTaken && active.TryTakeIndex(out HashSet<long> keys))
        {
            storeIndexTaken = true;
            (storedKeys ??= new HashSet<long>()).UnionWith(keys);
            logger.Notification("Horizon silhouettes: the client tile store holds {0} tiles from "
                + "earlier sessions ({1}).", keys.Count, System.IO.Path.GetFileName(active.Path));
        }

        while (active.TryTakeLoaded(out LodSilhouetteStoredTile result))
        {
            long key = LodSilhouetteTile.KeyOf(result.TileX, result.TileZ);
            storeLoads.Remove(key);
            if (result.Tile == null)
            {
                storedKeys?.Remove(key);
                storeReads.Remove(key);
                continue;
            }
            if (!WithinReach(key, playerX, playerZ, ReachBlocks + LodSilhouetteTile.TileBlocks))
            {
                storeReads.Remove(key);
                continue;
            }
            if (networkConfirmed.Contains(key)) continue;
            if (InstallTile(result.Tile, key, force: false, persist: false)
                && ReferenceEquals(tiles.GetValueOrDefault(key), result.Tile))
                TilesLoadedFromStore++;
        }

        if (storedKeys == null) return;
        while (storeLoads.Count < LodSilhouetteStore.MaxLoadsOutstanding)
        {
            long best = 0;
            double bestDistance = double.MaxValue;
            foreach (long key in storedKeys)
            {
                if (storeReads.Contains(key)
                    || !WithinReach(key, playerX, playerZ, ReachBlocks + LodSilhouetteTile.TileBlocks))
                    continue;
                double x = (LodWorld.KeySx(key) + 0.5) * LodSilhouetteTile.TileBlocks - playerX;
                double z = (LodWorld.KeySz(key) + 0.5) * LodSilhouetteTile.TileBlocks - playerZ;
                double distance = x * x + z * z;
                if (distance >= bestDistance) continue;
                best = key;
                bestDistance = distance;
            }
            if (bestDistance == double.MaxValue
                || !active.TryRequestLoad(LodWorld.KeySx(best), LodWorld.KeySz(best))) break;
            storeReads.Add(best);
            storeLoads.Add(best);
        }
    }

    void RequestMissing(double playerX, double playerZ)
    {
        long[] keys = transfer.Select(playerX, playerZ,
            key => tiles.TryGetValue(key, out LodSilhouetteTile? tile) ? tile.Revision : 0,
            Environment.TickCount64);
        if (keys.Length > 0 && !sendTileRequest(keys, transfer.SessionId))
            transfer.SendFailed(keys);
    }

    void AcceptReply(AssistSilhouetteTile message)
    {
        long key = message.SessionId != 0
            ? message.Key
            : LodSilhouetteTile.KeyOf(message.TileX, message.TileZ);

        // A 0.4.77-0.4.85 server ignores the append-only transport fields and pushes tiles. Keep
        // that old/new combination useful; current servers always echo a non-zero session.
        if (message.SessionId == 0)
        {
            if (message.Payload is { Length: > 0 } && LodWorld.KeyLevel(key) == LodSilhouetteTile.Level)
                Accept(message, key);
            return;
        }
        if (!transfer.BeginReply(message.SessionId, key)) return;
        int wantedRevision = transfer.OfferedRevision(key);
        if (wantedRevision <= 0)
        {
            transfer.Succeeded(key);
            return;
        }
        if (message.Payload is { Length: > 0 }
            && (LodWorld.KeyLevel(key) != LodSilhouetteTile.Level
                || LodWorld.KeySx(key) != message.TileX || LodWorld.KeySz(key) != message.TileZ))
        {
            transfer.Failed(key, retryable: false, Environment.TickCount64);
            return;
        }
        if (message.Payload is not { Length: > 0 })
        {
            transfer.Failed(key, message.Retryable, Environment.TickCount64);
            return;
        }
        if (message.Revision < wantedRevision)
        {
            transfer.Failed(key, retryable: true, Environment.TickCount64);
            return;
        }
        if (Accept(message, key)) transfer.Succeeded(key);
        else transfer.Failed(key, retryable: false, Environment.TickCount64);
    }

    void PruneOutsideReach(double playerX, double playerZ)
    {
        (int tileX, int tileZ) = LodSilhouetteTile.TileOfBlock(
            (int)Math.Floor(playerX), (int)Math.Floor(playerZ));
        if (tileX == lastPruneTileX && tileZ == lastPruneTileZ) return;
        lastPruneTileX = tileX;
        lastPruneTileZ = tileZ;
        bool Keep(long key) => LodWorld.KeyLevel(key) == LodSilhouetteTile.Level
            && WithinReach(key, playerX, playerZ, ReachBlocks + LodSilhouetteTile.TileBlocks);
        transfer.Prune(Keep);

        List<long>? removed = null;
        foreach (KeyValuePair<long, LodSilhouetteTile> entry in tiles)
        {
            if (Keep(entry.Key)) continue;
            (removed ??= new List<long>()).Add(entry.Key);
            knownCells -= entry.Value.KnownCells;
            CountSources(entry.Value, -1);
            coverage.Remove(entry.Key);
            dirty.Remove(entry.Key);
            networkConfirmed.Remove(entry.Key);
            if (storedKeys?.Contains(entry.Key) == true) storeReads.Remove(entry.Key);
            int version = meshVersion.GetValueOrDefault(entry.Key) + 1;
            meshVersion[entry.Key] = version;
            finished.Enqueue(new MeshResult(entry.Key, entry.Value.TileX, entry.Value.TileZ,
                version, Epoch, null, Removal: true));
        }
        if (removed == null) return;
        foreach (long key in removed) tiles.Remove(key);
        var removedSet = new HashSet<long>(removed);
        tileOrder.RemoveAll(removedSet.Contains);
        foreach (long key in removed)
        {
            foreach ((int tx, int tz) in LodSilhouetteField.TilesReading(
                LodWorld.KeySx(key), LodWorld.KeySz(key)))
            {
                long neighbour = LodSilhouetteTile.KeyOf(tx, tz);
                if (tiles.ContainsKey(neighbour)) dirty.Add(neighbour);
            }
        }
        coverageCursor = 0;
    }

    internal static bool WithinReach(long key, double playerX, double playerZ, double reach)
    {
        double blockX = LodWorld.KeySx(key) * (double)LodSilhouetteTile.TileBlocks;
        double blockZ = LodWorld.KeySz(key) * (double)LodSilhouetteTile.TileBlocks;
        double nearestX = Math.Clamp(playerX, blockX, blockX + LodSilhouetteTile.TileBlocks);
        double nearestZ = Math.Clamp(playerZ, blockZ, blockZ + LodSilhouetteTile.TileBlocks);
        double dx = nearestX - playerX, dz = nearestZ - playerZ;
        return dx * dx + dz * dz <= reach * reach;
    }

    bool Accept(AssistSilhouetteTile message, long key)
    {
        // Once this session has installed a server answer, repeats cannot move it backwards. A
        // persisted tile has no such authority: the server may have rebuilt its store and
        // restarted at a lower revision, so its first valid answer replaces the local copy.
        if (networkConfirmed.Contains(key)
            && tiles.TryGetValue(key, out LodSilhouetteTile? confirmed)
            && confirmed.Revision >= message.Revision) return true;
        LodSilhouetteTile? tile = LodSilhouetteTile.Decode(message.TileX, message.TileZ,
            message.Revision, message.Payload);
        if (tile == null)
        {
            if (++TilesRejected == 1)
                logger.Warning("Horizon silhouettes: tile {0},{1} did not decode and was dropped.",
                    message.TileX, message.TileZ);
            return false;
        }

        TilesReceived++;
        networkConfirmed.Add(key);
        return InstallTile(tile, key, force: true, persist: true);
    }

    bool InstallTile(LodSilhouetteTile tile, long key, bool force, bool persist)
    {
        tiles.TryGetValue(key, out LodSilhouetteTile? held);
        if (!force && held != null && held.Revision >= tile.Revision) return true;
        if (held == null) tileOrder.Add(key);
        knownCells += tile.KnownCells - (held?.KnownCells ?? 0);
        if (held != null) CountSources(held, -1);
        CountSources(tile, 1);
        tiles[key] = tile;

        if (persist && store?.Save(tile.Snapshot()) == true)
        {
            (storedKeys ??= new HashSet<long>()).Add(key);
            storeReads.Add(key);
        }

        // A tile's heights reach into its neighbours' fills, so they re-mesh with it.
        foreach ((int tx, int tz) in LodSilhouetteField.TilesReading(tile.TileX, tile.TileZ))
            dirty.Add(LodSilhouetteTile.KeyOf(tx, tz));
        return true;
    }

    void CountSources(LodSilhouetteTile tile, int direction)
    {
        for (int z = 0; z < LodSilhouetteTile.CellsPerEdge; z++)
        {
            for (int x = 0; x < LodSilhouetteTile.CellsPerEdge; x++)
            {
                switch (tile.OriginAt(x, z))
                {
                    case EnumSilhouetteOrigin.Captured: capturedCells += direction; break;
                    case EnumSilhouetteOrigin.Preview: previewCells += direction; break;
                    case EnumSilhouetteOrigin.Accelerator: acceleratorCells += direction; break;
                }
            }
        }
    }

    /// <summary>
    /// Rolling: a few tiles per tick ask the cache which of their 64 level-0 cells hold terrain.
    /// A change re-meshes the tile, so silhouettes give way as the player explores.
    /// </summary>
    void CheckCoverage(LodWorld world)
    {
        if (tileOrder.Count == 0) return;
        for (int n = 0; n < CoverageChecksPerPump && n < tileOrder.Count; n++)
        {
            if (coverageCursor >= tileOrder.Count) coverageCursor = 0;
            long key = tileOrder[coverageCursor++];
            if (!tiles.TryGetValue(key, out LodSilhouetteTile? tile)) continue;
            ulong mask = CoverageOf(world, tile.TileX, tile.TileZ);
            if (coverage.TryGetValue(key, out ulong held) && held == mask) continue;
            coverage[key] = mask;
            dirty.Add(key);
        }
    }

    static ulong CoverageOf(LodWorld world, int tileX, int tileZ)
    {
        ulong mask = 0;
        int cellsPerEdge = LodSilhouetteTile.TileBlocks / LodSection.SectionBlocks;
        for (int localZ = 0; localZ < cellsPerEdge; localZ++)
        {
            for (int localX = 0; localX < cellsPerEdge; localX++)
            {
                long key = LodWorld.SectionKey(0, tileX * cellsPerEdge + localX, tileZ * cellsPerEdge + localZ);
                if (world.HasDataSet.Contains(key))
                    mask |= 1UL << LodSilhouetteField.CoverageBit(localX, localZ);
            }
        }
        return mask;
    }

    void Schedule(LodWorld world)
    {
        int radius = LodSilhouetteFill.NeighbourhoodTiles / 2;
        while (Volatile.Read(ref jobsInFlight) < MaxJobsInFlight && dirty.Count > 0)
        {
            long key = NearestDirty();
            dirty.Remove(key);
            if (!tiles.TryGetValue(key, out LodSilhouetteTile? tile)) continue;

            if (!coverage.TryGetValue(key, out ulong mask))
                coverage[key] = mask = CoverageOf(world, tile.TileX, tile.TileZ);
            int version = meshVersion.TryGetValue(key, out int v) ? v + 1 : 1;
            meshVersion[key] = version;

            // References, not copies: held tiles never change (see the class summary).
            var neighbourhood = new LodSilhouetteTile?[LodSilhouetteFill.NeighbourhoodTiles,
                LodSilhouetteFill.NeighbourhoodTiles];
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                    neighbourhood[dz + radius, dx + radius] = tiles.GetValueOrDefault(
                        LodSilhouetteTile.KeyOf(tile.TileX + dx, tile.TileZ + dz));

            // Live LodSections are owner-thread objects. Copy only the visible top heights that
            // boundary vertices may need; the worker never observes a mutable section.
            float[] anchors = CaptureAnchors(world, tile.TileX, tile.TileZ, mask);
            int tileX = tile.TileX, tileZ = tile.TileZ, epoch = Epoch, sea = seaLevel;
            Interlocked.Increment(ref jobsInFlight);
            Task.Run(() =>
            {
                LodSilhouetteMeshSet? meshes = null;
                try
                {
                    meshes = LodSilhouetteField.BuildMeshes(neighbourhood, mask, sea, anchors);
                }
                catch (Exception e)
                {
                    if (Interlocked.Increment(ref MeshFailures) == 1)
                        logger.Error("Horizon silhouettes: a tile mesh failed to build: {0}", e);
                }
                finally
                {
                    finished.Enqueue(new MeshResult(key, tileX, tileZ, version, epoch, meshes));
                    Interlocked.Decrement(ref jobsInFlight);
                }
            });
        }
    }

    static float[] CaptureAnchors(LodWorld world, int tileX, int tileZ, ulong mask)
    {
        int edge = LodSilhouetteField.AnchorVerticesPerEdge;
        int spacing = LodSilhouetteTile.TileBlocks / LodSilhouetteField.HighQuadsPerEdge;
        int cells = LodSilhouetteTile.TileBlocks / LodSection.SectionBlocks;
        var result = new float[edge * edge];
        Array.Fill(result, float.NaN);

        for (int vz = 0; vz < edge; vz++)
        {
            int blockZ = vz * spacing;
            int cellZ = Math.Min(cells - 1, blockZ / LodSection.SectionBlocks);
            int minDz = blockZ > 0 && blockZ < LodSilhouetteTile.TileBlocks
                && blockZ % LodSection.SectionBlocks == 0 ? -1 : 0;
            for (int vx = 0; vx < edge; vx++)
            {
                int blockX = vx * spacing;
                int cellX = Math.Min(cells - 1, blockX / LodSection.SectionBlocks);
                int minDx = blockX > 0 && blockX < LodSilhouetteTile.TileBlocks
                    && blockX % LodSection.SectionBlocks == 0 ? -1 : 0;
                bool covered = false, uncovered = false;
                LodSection? source = null;
                int sourceCellX = 0, sourceCellZ = 0;
                for (int dz = minDz; dz <= 0; dz++)
                {
                    for (int dx = minDx; dx <= 0; dx++)
                    {
                        int cx = cellX + dx, cz = cellZ + dz;
                        if (cx < 0 || cx >= cells || cz < 0 || cz >= cells) continue;
                        bool cellCovered = LodSilhouetteField.Covered(mask, cx, cz);
                        covered |= cellCovered;
                        uncovered |= !cellCovered;
                        if (!cellCovered || source != null) continue;
                        long key = LodWorld.SectionKey(0, tileX * cells + cx, tileZ * cells + cz);
                        if (world.Sections.TryGetValue(key, out LodSection? section))
                        {
                            source = section;
                            sourceCellX = cx;
                            sourceCellZ = cz;
                        }
                    }
                }
                if (!covered || !uncovered || source == null) continue;

                int localX = Math.Clamp(blockX - sourceCellX * LodSection.SectionBlocks,
                    0, LodSection.GridSize - 1);
                int localZ = Math.Clamp(blockZ - sourceCellZ * LodSection.SectionBlocks,
                    0, LodSection.GridSize - 1);
                int col = LodSection.ColumnIndex(localX, localZ);
                if (!source.Captured[col]) continue;
                Span<ulong> runs = source.ColumnRuns(col);
                if (runs.Length > 0) result[vz * edge + vx] = LodSection.RunYTop(runs[0]);
            }
        }
        return result;
    }

    long NearestDirty()
    {
        long best = 0;
        double bestDistance = double.MaxValue;
        foreach (long key in dirty)
        {
            double x = (LodWorld.KeySx(key) + 0.5) * LodSilhouetteTile.TileBlocks - lastX;
            double z = (LodWorld.KeySz(key) + 0.5) * LodSilhouetteTile.TileBlocks - lastZ;
            double distance = x * x + z * z;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = key;
        }
        return best;
    }

    /// <summary>
    /// Render thread (the client main thread). Only results still current: a later version of
    /// the same tile, or a world change, makes an older result stale, and it is dropped here.
    /// </summary>
    public bool TryTakeFinished(out MeshResult result)
    {
        while (finished.TryDequeue(out result))
        {
            if (result.Epoch != Epoch) continue;
            if (!meshVersion.TryGetValue(result.Key, out int version) || version != result.Version) continue;
            if (result.Removal)
            {
                meshVersion.Remove(result.Key);
                return true;
            }
            if (result.Meshes == null && !result.Removal) EmptyMeshes++;
            else MeshesBuilt++;
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        enabled = false;
        Reset();
    }
}
