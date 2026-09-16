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
    const int MaxDecodeJobsInFlight = 2;
    const int MaxDecodedPublishesPerPump = 4;
    const int MaxJobsInFlight = 2;
    const int MaxMeshSnapshotsPerPump = 1;
    const int MaxPrunesPerPump = 64;
    const int SoftnessRemeshesPerPump = 4;
    const int CoverageChecksPerPump = 8;
    const long ReachRequestIntervalMs = 1000;
    const long SoftnessDebounceMs = 250;

    readonly ILogger logger;
    readonly Func<bool> available;
    readonly System.Func<bool, int, long, bool> sendRequest;
    readonly System.Func<long[], long, bool> sendTileRequest;
    readonly ConcurrentQueue<AssistSilhouetteManifest> manifests = new();
    readonly ConcurrentQueue<AssistSilhouetteTile> arrivals = new();
    readonly ConcurrentQueue<DecodeResult> decoded = new();
    readonly ConcurrentQueue<MeshResult> finished = new();
    readonly Dictionary<long, LodSilhouetteTile> tiles = new();
    readonly List<long> tileOrder = new();
    readonly Dictionary<long, LodSilhouetteCoverage> coverage = new();
    readonly Dictionary<long, int> meshVersion = new();
    readonly Dictionary<long, int> decodeVersion = new();
    readonly HashSet<long> dirty = new();
    readonly Queue<long> dirtyOrder = new();
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
    int pruneCursor;
    bool prunePending;
    bool pruneOffersPending;
    int jobsInFlight;
    int decodesInFlight;
    int nextDecodeVersion;
    bool requestSent;
    bool reachRequestDirty;
    long lastReachRequestMs;
    bool enabled;
    bool softnessRemeshPending;
    bool softnessRemeshActive;
    int softnessRemeshCursor;
    long softnessChangedAtMs;
    float hillSoftness = LodSilhouetteField.DefaultHillSoftness;
    int seaLevel;
    double lastX, lastZ;
    long knownCells;
    long capturedCells, previewCells, acceleratorCells;

    public readonly record struct MeshResult(long Key, int TileX, int TileZ, int Version, int Epoch,
        LodSilhouetteMeshSet? Meshes, bool Removal = false);

    readonly record struct DecodeResult(AssistSilhouetteTile Message, long Key, int Version,
        int Epoch, LodSilhouetteTile? Tile);

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
    public float HillSoftness => hillSoftness;

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
        ReachBlocks = clamped;
        // Existing tiles and meshes remain useful. Rendering observes the new reach immediately;
        // one throttled same-session request asks the server for additions, while rolling prune
        // retires anything now outside it. Clearing here used to rebuild the whole disc on every
        // slider step and was the main source of reach-change hitches.
        reachRequestDirty = enabled;
        lastPruneTileX = lastPruneTileZ = int.MinValue;
        prunePending = true;
        pruneOffersPending = true;
    }

    public void SetHillSoftness(float value)
    {
        float normalized = LodSilhouetteField.NormalizeHillSoftness(value);
        if (Math.Abs(hillSoftness - normalized) < 0.0001f) return;
        hillSoftness = normalized;
        softnessRemeshPending = true;
        softnessRemeshActive = false;
        softnessChangedAtMs = Environment.TickCount64;
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
        decodeVersion.Clear();
        dirty.Clear();
        dirtyOrder.Clear();
        networkConfirmed.Clear();
        storeReads.Clear();
        storeReads.UnionWith(storeLoads);
        coverageCursor = 0;
        reachRequestDirty = false;
        softnessRemeshPending = false;
        softnessRemeshActive = false;
        softnessRemeshCursor = 0;
        prunePending = false;
        pruneOffersPending = false;
        pruneCursor = 0;
        lastPruneTileX = lastPruneTileZ = int.MinValue;
        knownCells = 0;
        capturedCells = previewCells = acceleratorCells = 0;
        while (manifests.TryDequeue(out _)) { }
        while (arrivals.TryDequeue(out _)) { }
        while (decoded.TryDequeue(out _)) { }
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

            // A client tick can receive current tiles before LevelFinalize has opened the
            // ordinary cache and supplied this sibling path. Those answers are already
            // authoritative; backfill them now so startup ordering cannot make the first
            // session after an upgrade the one session that fails to persist.
            foreach (long key in networkConfirmed)
            {
                if (!tiles.TryGetValue(key, out LodSilhouetteTile? tile)
                    || store.SaveImmutable(tile) != true) continue;
                (storedKeys ??= new HashSet<long>()).Add(key);
                storeReads.Add(key);
            }
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
        long now = Environment.TickCount64;
        lastX = playerX;
        lastZ = playerZ;
        seaLevel = worldSeaLevel;
        PruneOutsideReach(playerX, playerZ);
        EnsureStore();
        PumpStore(playerX, playerZ);
        if (!requestSent && now - lastReachRequestMs >= ReachRequestIntervalMs)
        {
            lastReachRequestMs = now;
            if (available())
            {
                requestSent = sendRequest(true, ReachBlocks, transfer.SessionId);
                if (requestSent)
                {
                    reachRequestDirty = false;
                    logger.Notification("Horizon silhouettes: asked the server for a {0:N0}-block reach.",
                        ReachBlocks);
                }
            }
        }
        else if (requestSent && reachRequestDirty
            && now - lastReachRequestMs >= ReachRequestIntervalMs && available())
        {
            lastReachRequestMs = now;
            if (sendRequest(true, ReachBlocks, transfer.SessionId)) reachRequestDirty = false;
        }

        if (requestSent)
        {
            // One bounded manifest chunk per tick, mirroring the ordinary section path.
            if (manifests.TryDequeue(out AssistSilhouetteManifest? manifest))
                transfer.Apply(manifest);

            PublishDecoded();
            ScheduleDecodes();
            RequestMissing(playerX, playerZ);
        }
        if (softnessRemeshPending && now - softnessChangedAtMs >= SoftnessDebounceMs)
        {
            softnessRemeshPending = false;
            softnessRemeshActive = true;
            softnessRemeshCursor = 0;
        }
        if (softnessRemeshActive)
        {
            int end = Math.Min(tileOrder.Count, softnessRemeshCursor + SoftnessRemeshesPerPump);
            while (softnessRemeshCursor < end)
                MarkDirty(tileOrder[softnessRemeshCursor++]);
            if (softnessRemeshCursor >= tileOrder.Count)
            {
                softnessRemeshActive = false;
                softnessRemeshCursor = 0;
            }
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

    void ScheduleDecodes()
    {
        while (Volatile.Read(ref decodesInFlight) < MaxDecodeJobsInFlight
            && arrivals.TryDequeue(out AssistSilhouetteTile? message))
        {
            long key = message.SessionId != 0
                ? message.Key
                : LodSilhouetteTile.KeyOf(message.TileX, message.TileZ);

            // A 0.4.77-0.4.85 server ignores the append-only transport fields and pushes tiles.
            // Keep that old/new combination useful; current servers echo a non-zero session.
            if (message.SessionId == 0)
            {
                if (message.Payload is { Length: > 0 }
                    && LodWorld.KeyLevel(key) == LodSilhouetteTile.Level)
                    StartDecode(message, key);
                continue;
            }

            if (!transfer.ExpectsReply(message.SessionId, key)
                || decodeVersion.ContainsKey(key)) continue;
            int wantedRevision = transfer.OfferedRevision(key);
            if (wantedRevision <= 0)
            {
                CompleteReply(message.SessionId, key, retryable: false, success: true);
                continue;
            }
            if (message.Payload is { Length: > 0 }
                && (LodWorld.KeyLevel(key) != LodSilhouetteTile.Level
                    || LodWorld.KeySx(key) != message.TileX
                    || LodWorld.KeySz(key) != message.TileZ))
            {
                CompleteReply(message.SessionId, key, retryable: false, success: false);
                continue;
            }
            if (message.Payload is not { Length: > 0 })
            {
                CompleteReply(message.SessionId, key, message.Retryable, success: false);
                continue;
            }
            if (message.Revision < wantedRevision)
            {
                CompleteReply(message.SessionId, key, retryable: true, success: false);
                continue;
            }
            StartDecode(message, key);
        }
    }

    void StartDecode(AssistSilhouetteTile message, long key)
    {
        int version = ++nextDecodeVersion;
        decodeVersion[key] = version;
        int epoch = Epoch;
        Interlocked.Increment(ref decodesInFlight);
        Task.Run(() =>
        {
            LodSilhouetteTile? tile = null;
            try
            {
                tile = LodSilhouetteTile.Decode(message.TileX, message.TileZ,
                    message.Revision, message.Payload);
            }
            finally
            {
                decoded.Enqueue(new DecodeResult(message, key, version, epoch, tile));
                Interlocked.Decrement(ref decodesInFlight);
            }
        });
    }

    void PublishDecoded()
    {
        for (int n = 0; n < MaxDecodedPublishesPerPump
            && decoded.TryDequeue(out DecodeResult result); n++)
        {
            if (result.Epoch != Epoch
                || !decodeVersion.TryGetValue(result.Key, out int version)
                || version != result.Version) continue;
            decodeVersion.Remove(result.Key);

            AssistSilhouetteTile message = result.Message;
            if (message.SessionId == 0)
            {
                if (result.Tile == null) RejectDecoded(message);
                else AcceptDecoded(message, result.Key, result.Tile);
                continue;
            }
            if (!transfer.BeginReply(message.SessionId, result.Key)) continue;
            int wantedRevision = transfer.OfferedRevision(result.Key);
            if (result.Tile == null)
            {
                RejectDecoded(message);
                transfer.Failed(result.Key, retryable: false, Environment.TickCount64);
            }
            else if (message.Revision < wantedRevision)
            {
                transfer.Failed(result.Key, retryable: true, Environment.TickCount64);
            }
            else if (AcceptDecoded(message, result.Key, result.Tile))
            {
                transfer.Succeeded(result.Key);
            }
            else
            {
                transfer.Failed(result.Key, retryable: false, Environment.TickCount64);
            }
        }
    }

    void CompleteReply(long sessionId, long key, bool retryable, bool success)
    {
        if (!transfer.BeginReply(sessionId, key)) return;
        if (success) transfer.Succeeded(key);
        else transfer.Failed(key, retryable, Environment.TickCount64);
    }

    void PruneOutsideReach(double playerX, double playerZ)
    {
        (int tileX, int tileZ) = LodSilhouetteTile.TileOfBlock(
            (int)Math.Floor(playerX), (int)Math.Floor(playerZ));
        if (tileX != lastPruneTileX || tileZ != lastPruneTileZ)
        {
            lastPruneTileX = tileX;
            lastPruneTileZ = tileZ;
            prunePending = true;
            pruneOffersPending = true;
            pruneCursor = 0;
        }
        if (!prunePending) return;
        bool Keep(long key) => LodWorld.KeyLevel(key) == LodSilhouetteTile.Level
            && WithinReach(key, playerX, playerZ, ReachBlocks + LodSilhouetteTile.TileBlocks);
        if (pruneOffersPending)
        {
            transfer.Prune(Keep);
            pruneOffersPending = false;
        }

        int examined = 0;
        while (examined++ < MaxPrunesPerPump && pruneCursor < tileOrder.Count)
        {
            long key = tileOrder[pruneCursor];
            if (Keep(key))
            {
                pruneCursor++;
                continue;
            }
            if (!tiles.Remove(key, out LodSilhouetteTile? tile))
            {
                tileOrder.RemoveAt(pruneCursor);
                continue;
            }
            tileOrder.RemoveAt(pruneCursor);
            knownCells -= tile.KnownCells;
            CountSources(tile, -1);
            coverage.Remove(key);
            dirty.Remove(key);
            networkConfirmed.Remove(key);
            if (storedKeys?.Contains(key) == true) storeReads.Remove(key);
            int version = meshVersion.GetValueOrDefault(key) + 1;
            meshVersion[key] = version;
            finished.Enqueue(new MeshResult(key, tile.TileX, tile.TileZ,
                version, Epoch, null, Removal: true));
            foreach ((int tx, int tz) in LodSilhouetteField.TilesReading(
                LodWorld.KeySx(key), LodWorld.KeySz(key)))
            {
                long neighbour = LodSilhouetteTile.KeyOf(tx, tz);
                if (tiles.ContainsKey(neighbour)) MarkDirty(neighbour);
            }
            coverageCursor = 0;
        }
        if (pruneCursor < tileOrder.Count) return;
        prunePending = false;
        pruneCursor = 0;
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

    bool AcceptDecoded(AssistSilhouetteTile message, long key, LodSilhouetteTile tile)
    {
        // A reply that completed after a reach reduction still ends its transfer slot, but it
        // must not repopulate memory or start mesh work outside the new disc.
        if (!WithinReach(key, lastX, lastZ, ReachBlocks + LodSilhouetteTile.TileBlocks)) return true;
        // Once this session has installed a server answer, repeats cannot move it backwards. A
        // persisted tile has no such authority: the server may have rebuilt its store and
        // restarted at a lower revision, so its first valid answer replaces the local copy.
        if (networkConfirmed.Contains(key)
            && tiles.TryGetValue(key, out LodSilhouetteTile? confirmed)
            && confirmed.Revision >= message.Revision) return true;
        TilesReceived++;
        networkConfirmed.Add(key);
        return InstallTile(tile, key, force: true, persist: true);
    }

    void RejectDecoded(AssistSilhouetteTile message)
    {
        if (++TilesRejected == 1)
            logger.Warning("Horizon silhouettes: tile {0},{1} did not decode and was dropped.",
                message.TileX, message.TileZ);
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

        if (persist && store?.SaveImmutable(tile) == true)
        {
            (storedKeys ??= new HashSet<long>()).Add(key);
            storeReads.Add(key);
        }

        // A tile's heights reach into its neighbours' fills, so they re-mesh with it.
        foreach ((int tx, int tz) in LodSilhouetteField.TilesReading(tile.TileX, tile.TileZ))
            MarkDirty(LodSilhouetteTile.KeyOf(tx, tz));
        return true;
    }

    void CountSources(LodSilhouetteTile tile, int direction)
    {
        capturedCells += direction * (long)tile.CapturedCells;
        previewCells += direction * (long)tile.PreviewCells;
        acceleratorCells += direction * (long)tile.AcceleratorCells;
    }

    /// <summary>
    /// Rolling: a few tiles per tick ask the cache which 32-block chunk footprints hold terrain.
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
            LodSilhouetteCoverage mask = CoverageOf(world, tile.TileX, tile.TileZ);
            if (coverage.TryGetValue(key, out LodSilhouetteCoverage held) && held == mask) continue;
            coverage[key] = mask;
            DirtyAnchorNeighbours(tile.TileX, tile.TileZ);
        }
    }

    /// <summary>
    /// A changed edge also changes the live anchors of the tile on its other side. Rebuild the
    /// small 3x3 neighbourhood so a newly cached section cannot leave a stale cliff on an
    /// otherwise unchanged Farseer tile.
    /// </summary>
    void DirtyAnchorNeighbours(int tileX, int tileZ)
    {
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                long neighbour = LodSilhouetteTile.KeyOf(tileX + dx, tileZ + dz);
                if (tiles.ContainsKey(neighbour)) MarkDirty(neighbour);
            }
        }
    }

    static LodSilhouetteCoverage CoverageOf(LodWorld world, int tileX, int tileZ)
    {
        LodSilhouetteCoverage mask = LodSilhouetteCoverage.Empty;
        int sectionsPerEdge = LodSilhouetteTile.TileBlocks / LodSection.SectionBlocks;
        int chunksPerSection = LodSection.SectionBlocks / LodSilhouetteField.CoverageCellBlocks;
        for (int sectionZ = 0; sectionZ < sectionsPerEdge; sectionZ++)
        {
            for (int sectionX = 0; sectionX < sectionsPerEdge; sectionX++)
            {
                long key = LodWorld.SectionKey(0,
                    tileX * sectionsPerEdge + sectionX,
                    tileZ * sectionsPerEdge + sectionZ);
                if (!world.HasDataSet.Contains(key)) continue;
                if (!world.Sections.TryGetValue(key, out LodSection? source)
                    || source.CapturedColumns >= LodSection.GridSize * LodSection.GridSize)
                {
                    for (int dz = 0; dz < chunksPerSection; dz++)
                        for (int dx = 0; dx < chunksPerSection; dx++)
                            mask = mask.WithCovered(sectionX * chunksPerSection + dx,
                                sectionZ * chunksPerSection + dz);
                    continue;
                }

                int chunkColumns = LodSection.GridSize / chunksPerSection;
                for (int dz = 0; dz < chunksPerSection; dz++)
                {
                    for (int dx = 0; dx < chunksPerSection; dx++)
                    {
                        bool complete = true;
                        int startX = dx * chunkColumns, startZ = dz * chunkColumns;
                        for (int z = startZ; complete && z < startZ + chunkColumns; z++)
                        {
                            for (int x = startX; x < startX + chunkColumns; x++)
                            {
                                if (source.Captured[LodSection.ColumnIndex(x, z)]) continue;
                                complete = false;
                                break;
                            }
                        }
                        if (complete)
                            mask = mask.WithCovered(sectionX * chunksPerSection + dx,
                                sectionZ * chunksPerSection + dz);
                    }
                }
            }
        }
        return mask;
    }

    void Schedule(LodWorld world)
    {
        int radius = LodSilhouetteFill.NeighbourhoodTiles / 2;
        int snapshots = 0;
        while (snapshots < MaxMeshSnapshotsPerPump
            && Volatile.Read(ref jobsInFlight) < MaxJobsInFlight
            && TryTakeDirty(out long key))
        {
            if (!tiles.TryGetValue(key, out LodSilhouetteTile? tile)) continue;
            snapshots++;

            if (!coverage.TryGetValue(key, out LodSilhouetteCoverage mask))
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
            float[]? anchors = mask.IsFull || !HasNearbyCachedData(world, tile.TileX, tile.TileZ)
                ? null
                : CaptureAnchors(world, tile.TileX, tile.TileZ);
            int tileX = tile.TileX, tileZ = tile.TileZ, epoch = Epoch, sea = seaLevel;
            float softness = hillSoftness;
            Interlocked.Increment(ref jobsInFlight);
            Task.Run(() =>
            {
                LodSilhouetteMeshSet? meshes = null;
                try
                {
                    meshes = LodSilhouetteField.BuildMeshes(neighbourhood, mask, sea, anchors,
                        hillSoftness: softness);
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

    static float[] CaptureAnchors(LodWorld world, int tileX, int tileZ)
    {
        int edge = LodSilhouetteField.AnchorVerticesPerEdge;
        int spacing = LodSilhouetteTile.TileBlocks / LodSilhouetteField.HighQuadsPerEdge;
        var result = new float[edge * edge];
        Array.Fill(result, float.NaN);

        for (int vz = 0; vz < edge; vz++)
        {
            int worldZ = tileZ * LodSilhouetteTile.TileBlocks + vz * spacing;
            int minDz = Mod(worldZ, LodSilhouetteField.CoverageCellBlocks) == 0 ? -1 : 0;
            for (int vx = 0; vx < edge; vx++)
            {
                int worldX = tileX * LodSilhouetteTile.TileBlocks + vx * spacing;
                int minDx = Mod(worldX, LodSilhouetteField.CoverageCellBlocks) == 0 ? -1 : 0;
                bool covered = false, uncovered = false;
                float anchor = float.NaN;
                for (int dz = minDz; dz <= 0; dz++)
                {
                    for (int dx = minDx; dx <= 0; dx++)
                    {
                        int sampleX = worldX + dx, sampleZ = worldZ + dz;
                        int sx = (int)Math.Floor(sampleX / (double)LodSection.SectionBlocks);
                        int sz = (int)Math.Floor(sampleZ / (double)LodSection.SectionBlocks);
                        long key = LodWorld.SectionKey(0, sx, sz);
                        LodSection? source = null;
                        bool cellCovered = false;
                        int localX = sampleX - sx * LodSection.SectionBlocks;
                        int localZ = sampleZ - sz * LodSection.SectionBlocks;
                        if (world.HasDataSet.Contains(key)
                            && world.Sections.TryGetValue(key, out source))
                            cellCovered = source.Captured[LodSection.ColumnIndex(localX, localZ)];
                        covered |= cellCovered;
                        uncovered |= !cellCovered;
                        if (!cellCovered || source == null) continue;
                        int col = LodSection.ColumnIndex(localX, localZ);
                        Span<ulong> runs = source.ColumnRuns(col);
                        if (runs.Length == 0) continue;
                        float top = LodSection.RunYTop(runs[0]);
                        anchor = float.IsNaN(anchor) ? top : Math.Max(anchor, top);
                    }
                }
                if (covered && uncovered && !float.IsNaN(anchor))
                    result[vz * edge + vx] = anchor;
            }
        }
        return result;
    }

    static bool HasNearbyCachedData(LodWorld world, int tileX, int tileZ)
    {
        int sections = LodSilhouetteTile.TileBlocks / LodSection.SectionBlocks;
        int baseX = tileX * sections, baseZ = tileZ * sections;
        for (int z = -1; z <= sections; z++)
            for (int x = -1; x <= sections; x++)
                if (world.HasDataSet.Contains(LodWorld.SectionKey(0, baseX + x, baseZ + z)))
                    return true;
        return false;
    }

    static int Mod(int value, int divisor)
    {
        int remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }

    void MarkDirty(long key)
    {
        if (dirty.Add(key)) dirtyOrder.Enqueue(key);
    }

    bool TryTakeDirty(out long key)
    {
        while (dirtyOrder.TryDequeue(out key))
        {
            if (dirty.Remove(key)) return true;
        }
        key = 0;
        return false;
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
