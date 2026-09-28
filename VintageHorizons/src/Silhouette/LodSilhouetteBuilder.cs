using System.Collections.Concurrent;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using VintageHorizons.Net;

namespace VintageHorizons.Silhouette;

/// <summary>
/// Server half of horizon silhouettes (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER P1-P3).
///
/// Heights come from three places, in quality order. Captured terrain and engine previews outrank
/// an accepted optional accelerator. Watersheds is preferred when both supported accelerators are
/// installed; Algernon's Terrain Sampler is used when it is the one present. An accelerator fills
/// each tile every 64 blocks on a bounded worker pool, then one engine preview refines low or
/// coastal ground. If the selected accelerator fails, it is disabled for the session and the
/// same positions fall back to previews. A preview writes nothing to the savegame.
///
/// Tiles persist in <see cref="LodSilhouetteStore"/>, so each area is paid for once per world.
/// A world that has never used silhouettes gets its store with the first request. Changed tiles
/// are written at every world save and at shutdown. A stored tile is read back before the
/// builder previews anything in it, and RAM lets go of saved tiles nobody's reach covers once it
/// holds more than <see cref="SoftTileCap"/>.
///
/// It is an automatic producer, so it never takes the world-generation authority. It holds new
/// work while an explicit job holds that authority, or while the engine is generating real
/// chunks for someone (settled decision 39). With no requesting player it registers no tick work.
///
/// Every probe and preview is served by the engine's one world-generation thread. Previews stop
/// for the rest of the session if that thread stops answering (<see cref="LodGenerationStallWatch"/>)
/// or if the server logs errors while previews are being generated
/// (<see cref="LodWorldgenErrorBrake"/>); the log and chat say which.
/// </summary>
internal sealed class LodSilhouetteBuilder
{
    public const int DefaultReachBlocks = 8192;
    public const int MinReachBlocks = 1024;

    /// <summary>The distance scale's end; no server ceiling in singleplayer (plan A7).</summary>
    public const int MaxReachBlocks = 32768;

    public const int PreviewsPerSecond = 16;

    /// <summary>
    /// Previews waiting on the generator at once. The engine works one at a time, so a short
    /// queue costs no speed, and a stop leaves at most this many behind.
    /// </summary>
    public const int MaxPeeksInFlight = 4;

    /// <summary>
    /// Tiles queued off-thread at once. Calls themselves are serialized until sampling beside real
    /// generation has been measured; one caller already answered 270,000 cells in about a second.
    /// </summary>
    public const int MaxAcceleratorJobsInFlight = 4;

    public const int MaxProbesInFlight = 32;
    public const int MaxProbeIssuesPerTick = 4;

    /// <summary>
    /// Real chunk generation pending for someone: hold new previews. The engine's counter never
    /// sees peeks, so this measures only real loads. P0 sets the final value (plan 4.5).
    /// </summary>
    public const int YieldGeneratingChunks = 8;

    public const int TilesPerSecondPerPlayer = LodAssist.MaxSilhouetteTilesPerSecondPerPlayer;

    /// <summary>
    /// Tiles held in RAM above which saved tiles no reach covers are let go. An 8,192-block reach
    /// covers about 900 tiles.
    /// </summary>
    public const int SoftTileCap = 2048;

    const double TickWorkBudgetMs = 1.0;
    const long StuckPeekTimeoutMs = 300_000;
    const long StatusLogIntervalMs = 30_000;
    const int MaxSkipsPerProbe = 4096;

    readonly ICoreServerAPI sapi;
    readonly ILogger logger;
    readonly Func<EnumWorldGenAuthority> generationAuthority;
    readonly Func<EnumWorldGenGate> generationGate;
    readonly System.Func<IServerPlayer, AssistSilhouetteManifest, bool> sendManifest;
    readonly System.Func<IServerPlayer, AssistSilhouetteTile, bool> sendTile;
    readonly ILodHeightAccelerator? accelerator;
    readonly int seaLevel;
    readonly int maxReachBlocks;
    readonly int previewsPerSecond;
    readonly SemaphoreSlim acceleratorCallGate = new(1, 1);
    LodSilhouetteStore? store;
    Func<LodSilhouetteStore?>? openStore;

    sealed class Demand
    {
        public required IServerPlayer Player;
        public int ReachBlocks;
        public double CentreX, CentreZ;
        public int CentreI, CentreJ;
        public int SpiralIndex, SpiralTotal;
        public bool Exhausted;
        public int TransportVersion;
        public long SessionId;
        public int ManifestSequence;
        public bool ManifestPending;
        public readonly Dictionary<long, int> Offered = new();
        public readonly HashSet<long> Queued = new();
        public readonly Queue<(long Key, long SessionId)> Requests = new();
        public readonly LodTickAllowance SendAllowance = new();
    }

    enum EnumProbeStep { Probed, Exhausted, Paused }

    readonly record struct PeekInFlight(int Cx, int Cz, long IssuedMs);
    readonly record struct AcceleratorResult(int TileX, int TileZ, int FallbackCx, int FallbackCz,
        int[]? Heights, string? Error);

    readonly Dictionary<string, Demand> demands = new();
    readonly List<string> demandOrder = new();
    int nextDemand;
    readonly Dictionary<long, LodSilhouetteTile> tiles = new();
    readonly HashSet<long> attempted = new();
    readonly HashSet<long> claimedTiles = new();
    readonly Queue<(int Cx, int Cz)> pendingPeeks = new();
    readonly Dictionary<long, PeekInFlight> peeksInFlight = new();
    readonly HashSet<long> acceleratorInFlight = new();
    readonly ConcurrentQueue<AcceleratorResult> acceleratorResults = new();
    readonly List<long> stale = new();
    readonly LodTickAllowance previewAllowance = new();
    readonly LodTickAllowance tileSendAllowance = new();
    readonly LodGenerationStallWatch generatorWatch = new();
    readonly LodWorldgenErrorBrake errorBrake;
    LogEntryDelegate? logWatcher;
    int probesInFlight;
    int nextSendDemand;
    long listenerId;
    bool listening;
    bool firstPeekReported;
    bool acceleratorFailed;
    long nextStatusLogMs;
    long lastLandedMs = -1;
    int lastLandedCx, lastLandedCz;

    // The store's side. A tile is "ready" when RAM holds everything the store holds for it:
    // never stored, or its stored copy merged in. Nothing is previewed in a tile until it is.
    HashSet<long>? storedBeforeSession;
    readonly HashSet<long> savedThisSession = new();
    readonly HashSet<long> storeMerged = new();
    readonly HashSet<long> loadWanted = new();
    readonly HashSet<long> loading = new();
    readonly HashSet<long> dirtyTiles = new();
    readonly List<long> evictable = new();

    public int Probed, Existing, Peeked, Landed, Empty, TimedOut, YieldTicks, TilesSent;
    public int ManifestsSent, TilesRequested, TilesRefused, TilesRetryable;
    public int AcceleratorTilesIssued, AcceleratorTilesLanded, AcceleratorCells, AcceleratorFailures,
        CoastPreviews;
    public int CapturedColumns, RainColumns, StoreReads, TilesSaved, Evicted;
    public long BytesSent;

    /// <param name="store">The world's tile store, when its file already exists.</param>
    /// <param name="openStore">Creates the store on the first request, for a world that has none.</param>
    /// <param name="ownLogMarker">Marks the mod's own log lines, which never count as worldgen errors.</param>
    public LodSilhouetteBuilder(ICoreServerAPI sapi, ILogger logger,
        Func<EnumWorldGenAuthority> generationAuthority, Func<EnumWorldGenGate> generationGate,
        System.Func<IServerPlayer, AssistSilhouetteManifest, bool> sendManifest,
        System.Func<IServerPlayer, AssistSilhouetteTile, bool> sendTile, LodSilhouetteStore? store,
        Func<LodSilhouetteStore?>? openStore, string? ownLogMarker,
        ILodHeightAccelerator? accelerator = null, int? seaLevel = null,
        int maxReachBlocks = MaxReachBlocks, int previewsPerSecond = PreviewsPerSecond)
    {
        this.sapi = sapi;
        this.logger = logger;
        this.generationAuthority = generationAuthority;
        this.generationGate = generationGate;
        this.sendManifest = sendManifest;
        this.sendTile = sendTile;
        this.accelerator = accelerator;
        this.seaLevel = seaLevel ?? sapi.World.SeaLevel;
        this.maxReachBlocks = Math.Clamp(maxReachBlocks, MinReachBlocks, MaxReachBlocks);
        this.previewsPerSecond = Math.Clamp(previewsPerSecond, 1, 64);
        this.store = store;
        this.openStore = store == null ? openStore : null;
        errorBrake = new LodWorldgenErrorBrake(ownLogMarker);
    }

    public int TileCount => tiles.Count;
    public int PlayerCount => demands.Count;

    /// <summary>Main thread.</summary>
    public void Request(IServerPlayer player, AssistSilhouetteRequest request)
    {
        if (!request.Enabled)
        {
            if (Forget(player.PlayerUID))
                logger.Notification("Horizon silhouettes: {0} switched them off; their demand is dropped.",
                    player.PlayerName);
            return;
        }

        if (store == null && openStore != null)
        {
            // A world that has never used silhouettes gets its store with the first request.
            store = openStore();
            openStore = null;
        }

        int askedReach = request.ReachBlocks <= 0 ? DefaultReachBlocks : request.ReachBlocks;
        int reach = Math.Clamp(askedReach, MinReachBlocks, maxReachBlocks);
        if (!demands.TryGetValue(player.PlayerUID, out Demand? demand))
        {
            demand = new Demand { Player = player };
            demands[player.PlayerUID] = demand;
            demandOrder.Add(player.PlayerUID);
        }
        demand.Player = player;
        bool newSession = demand.SessionId != request.SessionId
            || demand.TransportVersion != request.TransportVersion;
        demand.TransportVersion = request.TransportVersion;
        demand.SessionId = request.SessionId;
        if (newSession)
        {
            demand.ManifestSequence = 0;
            demand.Offered.Clear();
            demand.Queued.Clear();
        }
        demand.ReachBlocks = reach;
        Recentre(demand, force: true);
        demand.ManifestPending = demand.TransportVersion >= LodAssist.SilhouetteTransportVersion;
        string source = accelerator == null
            ? "using one engine preview in each 512-block region"
            : $"sampling every {LodSilhouetteAcceleratorSampling.CoarseSpacingBlocks} blocks with "
                + $"{accelerator.Name} on one background caller, with engine previews for low and coastal ground";
        logger.Notification(
            "Horizon silhouettes: {0} asked for a {1:N0}-block reach, {2}, nearest first, heights "
            + "only; nothing is written to the savegame.",
            player.PlayerName, reach, source);
        if (reach != askedReach)
            logger.Notification("Horizon silhouettes: {0}'s requested reach was clamped from {1:N0} "
                + "to this server's {2:N0}-block ceiling.", player.PlayerName, askedReach, reach);
        EnsureListening();
    }

    /// <summary>Main thread: retain every accepted key until it receives one explicit answer.</summary>
    public void RequestTiles(IServerPlayer player, AssistSilhouetteTileRequest request)
    {
        long[] keys = request.Keys ?? Array.Empty<long>();
        if (!demands.TryGetValue(player.PlayerUID, out Demand? demand))
        {
            foreach (long key in keys) SendRefusal(player, key, request.SessionId, retryable: false);
            return;
        }
        foreach (long key in keys)
        {
            demand.Requests.Enqueue((key, request.SessionId));
            TilesRequested++;
        }
        if (keys.Length > 0) EnsureListening();
    }

    /// <summary>Main thread. True when the player had a demand.</summary>
    public bool Forget(string playerUid)
    {
        if (!demands.Remove(playerUid)) return false;
        demandOrder.Remove(playerUid);
        if (demands.Count == 0) StopListening();
        return true;
    }

    /// <summary>
    /// Main thread. The worldgen heights of a column the server loaded, or previewed for its own
    /// cache (plan 4.4). They are kept whether or not anyone is asking yet, they outrank the
    /// builder's own previews, and the builder never previews in a tile that holds heights.
    /// </summary>
    public void OfferCapturedHeights(int cx, int cz, ReadOnlySpan<ushort> heights, bool rainHeights)
    {
        if (heights.Length < LodSilhouetteTile.HeightMapLength) return;
        CapturedColumns++;
        if (rainHeights) RainColumns++;
        (int tileX, int tileZ) = LodSilhouetteTile.TileOfChunk(cx, cz);
        long key = LodSilhouetteTile.KeyOf(tileX, tileZ);
        if (!tiles.TryGetValue(key, out LodSilhouetteTile? tile))
            tiles[key] = tile = new LodSilhouetteTile(tileX, tileZ);
        if (!tile.SetChunk(cx, cz, heights, EnumSilhouetteOrigin.Captured)) return;
        dirtyTiles.Add(key);
        foreach (Demand demand in demands.Values)
            if (Covers(demand, tile)) demand.Queued.Add(key);
    }

    /// <summary>
    /// Main thread, at every world save and at shutdown: hand a copy of every changed tile to the
    /// store, then let go of saved tiles nobody's reach covers when RAM holds too many.
    /// </summary>
    public void SaveDirty()
    {
        if (store == null) return;
        TakeStoredIndex();
        foreach (long key in dirtyTiles)
        {
            if (!tiles.TryGetValue(key, out LodSilhouetteTile? tile)) continue;
            // RAM holds all the store has for this tile only if its stored copy was merged in,
            // or there never was one. The store merges on write either way; this only decides
            // whether the tile may later be let go of and read back without loss.
            bool complete = storeMerged.Contains(key) || storedBeforeSession != null && !IsStored(key);
            if (!store.Save(tile.Snapshot())) return;
            savedThisSession.Add(key);
            if (complete) storeMerged.Add(key);
            TilesSaved++;
        }
        dirtyTiles.Clear();
        Evict();
    }

    /// <summary>Main thread, at shutdown: saves what changed, then closes the store.</summary>
    public void Dispose()
    {
        if (listening)
        {
            sapi.Event.UnregisterGameTickListener(listenerId);
            listening = false;
        }
        if (logWatcher != null)
        {
            sapi.Logger.EntryAdded -= logWatcher;
            logWatcher = null;
        }
        SaveDirty();
        store?.Dispose();
    }

    void EnsureListening()
    {
        if (listening) return;
        listenerId = sapi.Event.RegisterGameTickListener(
            LodHitchActivity.Measured("silhouette server tick", _ => Tick()), LodTickAllowance.TickMilliseconds);
        listening = true;
        nextStatusLogMs = sapi.World.ElapsedMilliseconds + StatusLogIntervalMs;
        // The error brake reads the server log only while someone asks for silhouettes.
        logWatcher ??= errorBrake.Observe;
        sapi.Logger.EntryAdded += logWatcher;
    }

    void StopListening()
    {
        if (!listening) return;
        sapi.Event.UnregisterGameTickListener(listenerId);
        listening = false;
        if (logWatcher != null) sapi.Logger.EntryAdded -= logWatcher;
        pendingPeeks.Clear();
        loadWanted.Clear();
        LogStatus("idle, no player is asking");
    }

    void Tick()
    {
        long now = sapi.World.ElapsedMilliseconds;
        PumpStore();
        PumpAccelerators();
        WatchGenerator(now);
        BrakeOnErrors();
        ExpireStuckPeeks(now);
        foreach (Demand demand in demands.Values) Recentre(demand, force: false);
        IssueWork(now);
        SendOffers();
        ServeTiles(now);
        if (now >= nextStatusLogMs)
        {
            nextStatusLogMs = now + StatusLogIntervalMs;
            LogStatus("working");
        }
    }

    static long TileKeyOfChunk(int cx, int cz)
    {
        (int tileX, int tileZ) = LodSilhouetteTile.TileOfChunk(cx, cz);
        return LodSilhouetteTile.KeyOf(tileX, tileZ);
    }

    bool IsStored(long key) => storedBeforeSession?.Contains(key) == true || savedThisSession.Contains(key);

    /// <summary>RAM holds everything the store holds for this tile, so previewing in it is safe.</summary>
    bool TileReady(long key) =>
        store == null || storedBeforeSession != null && (!IsStored(key) || storeMerged.Contains(key));

    /// <summary>The tile already has heights, captured, stored or previewed.</summary>
    bool TileHasHeights(long key) => tiles.TryGetValue(key, out LodSilhouetteTile? tile) && tile.KnownCells > 0;

    /// <summary>Main thread: the preview count the error brake watches follows the in-flight set.</summary>
    void PeeksChanged() => errorBrake.SetPreviewsOutstanding(peeksInFlight.Count);

    void TakeStoredIndex()
    {
        if (store == null || storedBeforeSession != null || !store.TryTakeIndex(out HashSet<long> keys))
            return;
        storedBeforeSession = keys;
        logger.Notification("Horizon silhouettes: the tile store holds {0} tiles from earlier sessions ({1}).",
            keys.Count, System.IO.Path.GetFileName(store.Path));
        foreach (Demand demand in demands.Values) WantStoredTiles(demand);
    }

    void WantLoad(long key)
    {
        if (!IsStored(key) || storeMerged.Contains(key) || loading.Contains(key)) return;
        loadWanted.Add(key);
    }

    /// <summary>Every stored tile within the demand's reach that RAM does not hold in full.</summary>
    void WantStoredTiles(Demand demand)
    {
        if (storedBeforeSession == null) return;
        foreach (long key in storedBeforeSession)
            if (CoversTile(demand, LodWorld.KeySx(key), LodWorld.KeySz(key))) WantLoad(key);
        foreach (long key in savedThisSession)
            if (CoversTile(demand, LodWorld.KeySx(key), LodWorld.KeySz(key))) WantLoad(key);
    }

    /// <summary>Main thread: take what the store answered, then ask for the tiles demands need, nearest first.</summary>
    void PumpStore()
    {
        if (store == null) return;
        TakeStoredIndex();
        while (store.TryTakeLoaded(out LodSilhouetteStoredTile result))
        {
            long key = LodSilhouetteTile.KeyOf(result.TileX, result.TileZ);
            loading.Remove(key);
            storeMerged.Add(key);
            if (result.Tile == null) continue;
            StoreReads++;
            if (tiles.TryGetValue(key, out LodSilhouetteTile? held))
            {
                if (!held.MergeFrom(result.Tile)) continue;
            }
            else
            {
                tiles[key] = held = result.Tile;
            }
            foreach (Demand demand in demands.Values)
                if (Covers(demand, held)) demand.Queued.Add(key);
        }
        while (loadWanted.Count > 0)
        {
            long key = NearestTo(loadWanted);
            if (!store.TryRequestLoad(LodWorld.KeySx(key), LodWorld.KeySz(key))) break;
            loadWanted.Remove(key);
            loading.Add(key);
        }
    }

    /// <summary>After a save, let go of saved tiles nobody's reach covers, if RAM holds too many.</summary>
    void Evict()
    {
        if (tiles.Count <= SoftTileCap) return;
        evictable.Clear();
        foreach (KeyValuePair<long, LodSilhouetteTile> entry in tiles)
        {
            if (dirtyTiles.Contains(entry.Key) || !IsStored(entry.Key)) continue;
            bool wanted = false;
            foreach (Demand demand in demands.Values) wanted |= Covers(demand, entry.Value);
            if (!wanted) evictable.Add(entry.Key);
        }
        foreach (long key in evictable)
        {
            tiles.Remove(key);
            storeMerged.Remove(key);
        }
        Evicted += evictable.Count;
    }

    /// <summary>
    /// When the world-generation thread stops answering, nothing the builder does can restart
    /// it, so it stops adding work and says what it was waiting for. In 0.4.77 the only symptom
    /// was a hang when the player left the world.
    /// </summary>
    void WatchGenerator(long now)
    {
        if (!generatorWatch.Tick(now, peeksInFlight.Count > 0 || probesInFlight > 0)) return;
        pendingPeeks.Clear();

        var waiting = new List<string>();
        foreach (PeekInFlight peek in peeksInFlight.Values.OrderBy(p => p.IssuedMs))
        {
            waiting.Add($"{peek.Cx},{peek.Cz} (blocks {peek.Cx * LodSilhouetteTile.ChunkBlocks},"
                + $"{peek.Cz * LodSilhouetteTile.ChunkBlocks}, waiting {(now - peek.IssuedMs) / 1000} s)");
        }
        string last = lastLandedMs < 0
            ? "none this session"
            : $"chunk {lastLandedCx},{lastLandedCz}, {(now - lastLandedMs) / 1000} s ago";
        logger.Warning(
            "Horizon silhouettes: the world generator has not answered for {0} s, with {1} previews "
            + "and {2} existence checks outstanding. Previews stop for the rest of this session; "
            + "tiles already built stay. A world-generation thread that stops answering also stops "
            + "new chunks arriving, and leaving the world then hangs while the game waits for it. "
            + "Previews still waiting, oldest first (chunk x,z): {3}. Last preview that came back: {4}.",
            (now - generatorWatch.SilentSinceMs) / 1000, peeksInFlight.Count, probesInFlight,
            waiting.Count == 0 ? "none" : string.Join("; ", waiting), last);
        foreach (Demand demand in demands.Values)
        {
            demand.Player.SendMessage(GlobalConstants.GeneralChatGroup,
                "[VintageHorizons] The world generator stopped answering horizon silhouette previews, "
                + "so they are stopped for this session. If leaving the world now hangs, this is why; "
                + "the server log has the details.", EnumChatType.Notification);
        }
    }

    /// <summary>
    /// Stop previews for the session once the server has logged a few errors while they were
    /// being generated (<see cref="LodWorldgenErrorBrake"/>). Tiles already built and captured
    /// heights stay.
    /// </summary>
    void BrakeOnErrors()
    {
        if (!errorBrake.Tick()) return;
        pendingPeeks.Clear();
        logger.Warning(
            "Horizon silhouettes: the server logged {0} errors while silhouette previews were being "
            + "generated, so previews stop for the rest of this session; tiles already built and "
            + "captured heights stay. On the owner's world such errors came just before the world "
            + "generator stalled for minutes. The first: {1}",
            errorBrake.ErrorsDuringPreviews, errorBrake.FirstError ?? "(no text)");
        foreach (Demand demand in demands.Values)
        {
            demand.Player.SendMessage(GlobalConstants.GeneralChatGroup,
                "[VintageHorizons] The world generator reported errors during horizon silhouette "
                + "previews, so they are stopped for this session. Silhouettes already built stay; "
                + "the server log has the details.", EnumChatType.Notification);
        }
    }

    /// <summary>Main thread: a probe or a preview came back from the world-generation thread.</summary>
    void GeneratorAnswered()
    {
        long now = sapi.World.ElapsedMilliseconds;
        generatorWatch.Answered(now);
        if (generatorWatch.LateAnswers != 1) return;
        logger.Notification(
            "Horizon silhouettes: the world generator answered again {0} s after previews were "
            + "stopped, so it was slow rather than stuck. Previews stay off for this session.",
            (now - generatorWatch.StalledAtMs) / 1000);
    }

    void IssueAccelerator(int tileX, int tileZ, int fallbackCx, int fallbackCz)
    {
        ILodHeightAccelerator selected = accelerator!;
        long key = LodSilhouetteTile.KeyOf(tileX, tileZ);
        acceleratorInFlight.Add(key);
        AcceleratorTilesIssued++;
        (int X, int Z)[] positions = LodSilhouetteAcceleratorSampling.Positions(tileX, tileZ);
        Task.Run(() =>
        {
            long allocationStart = LodHitchActivity.StartJob();
            acceleratorCallGate.Wait();
            try
            {
                int[] heights = selected.Sample(positions);
                if (heights.Length != positions.Length)
                    throw new InvalidOperationException(
                        $"it returned {heights.Length} heights for {positions.Length} positions");
                acceleratorResults.Enqueue(new AcceleratorResult(
                    tileX, tileZ, fallbackCx, fallbackCz, heights, null));
            }
            catch (Exception e)
            {
                acceleratorResults.Enqueue(new AcceleratorResult(
                    tileX, tileZ, fallbackCx, fallbackCz, null, e.GetBaseException().Message));
            }
            finally
            {
                acceleratorCallGate.Release();
                LodHitchActivity.EndJob("silhouette accelerator sampling", allocationStart);
            }
        });
    }

    /// <summary>Main thread: publish bounded worker results and turn failures into previews.</summary>
    void PumpAccelerators()
    {
        while (acceleratorResults.TryDequeue(out AcceleratorResult result))
        {
            long key = LodSilhouetteTile.KeyOf(result.TileX, result.TileZ);
            if (!acceleratorInFlight.Remove(key)) continue;
            claimedTiles.Remove(key);

            if (acceleratorFailed || result.Error != null || result.Heights == null)
            {
                if (!acceleratorFailed)
                {
                    acceleratorFailed = true;
                    AcceleratorFailures++;
                    logger.Warning(
                        "Horizon silhouettes: {0} failed ({1}), so it is disabled for this session; "
                        + "engine previews take over.", accelerator?.Name ?? "the height accelerator",
                        result.Error ?? "no heights returned");
                }
                QueuePreview(result.FallbackCx, result.FallbackCz, onlyIfEmpty: true);
                continue;
            }

            if (!tiles.TryGetValue(key, out LodSilhouetteTile? tile))
                tiles[key] = tile = new LodSilhouetteTile(result.TileX, result.TileZ);
            try
            {
                if (LodSilhouetteAcceleratorSampling.Apply(tile, result.Heights))
                {
                    AcceleratorTilesLanded++;
                    AcceleratorCells += result.Heights.Length;
                    dirtyTiles.Add(key);
                    foreach (Demand demand in demands.Values)
                        if (Covers(demand, tile)) demand.Queued.Add(key);
                }
            }
            catch (Exception e)
            {
                acceleratorFailed = true;
                AcceleratorFailures++;
                logger.Warning(
                    "Horizon silhouettes: {0} returned unusable heights ({1}), so it is disabled "
                    + "for this session; engine previews take over.", accelerator?.Name ?? "the height accelerator",
                    e.GetBaseException().Message);
                QueuePreview(result.FallbackCx, result.FallbackCz, onlyIfEmpty: true);
                continue;
            }

            // The accepted accelerator is the broad cheap source. A real Terrain-pass preview
            // remains the better source at a likely coastline or lake and replaces these cells.
            if (LodSilhouetteAcceleratorSampling.TryCoastPreview(result.TileX, result.TileZ,
                    result.Heights, seaLevel, out int cx, out int cz))
            {
                if (QueuePreview(cx, cz, onlyIfEmpty: false)) CoastPreviews++;
            }
        }
    }

    bool QueuePreview(int cx, int cz, bool onlyIfEmpty)
    {
        if (generatorWatch.Stalled || errorBrake.Tripped) return false;
        long tileKey = TileKeyOfChunk(cx, cz);
        if (onlyIfEmpty && TileHasHeights(tileKey) || !claimedTiles.Add(tileKey)) return false;
        Probe(cx, cz);
        return true;
    }

    void Recentre(Demand demand, bool force)
    {
        EntityPos? pos = demand.Player.Entity?.Pos;
        if (pos == null) return;
        double dx = pos.X - demand.CentreX, dz = pos.Z - demand.CentreZ;
        if (!force && dx * dx + dz * dz < LodSilhouetteField.LatticeBlocks * LodSilhouetteField.LatticeBlocks)
            return;

        demand.CentreX = pos.X;
        demand.CentreZ = pos.Z;
        demand.CentreI = NearestNode(pos.X);
        demand.CentreJ = NearestNode(pos.Z);
        int radius = (demand.ReachBlocks + LodSilhouetteTile.TileBlocks) / LodSilhouetteField.LatticeBlocks + 2;
        demand.SpiralTotal = (2 * radius + 1) * (2 * radius + 1);
        demand.SpiralIndex = 0;
        demand.Exhausted = false;

        // An offer is a statement about the current reach, not a permanent map grant. Forget old
        // directions so returning later re-offers them after the client has pruned its RAM set.
        foreach (long key in demand.Offered.Keys
            .Where(key => !CoversTile(demand, LodWorld.KeySx(key), LodWorld.KeySz(key))).ToArray())
            demand.Offered.Remove(key);
        demand.Queued.RemoveWhere(key =>
            !CoversTile(demand, LodWorld.KeySx(key), LodWorld.KeySz(key)));

        // Anything already built that the player now reaches and does not hold goes out again,
        // and stored tiles they now reach are read back.
        foreach (KeyValuePair<long, LodSilhouetteTile> entry in tiles)
        {
            if (!Covers(demand, entry.Value)) continue;
            if (demand.Offered.TryGetValue(entry.Key, out int revision) && revision == entry.Value.Revision)
                continue;
            demand.Queued.Add(entry.Key);
        }
        WantStoredTiles(demand);
    }

    static int NearestNode(double blockCoordinate) => (int)Math.Round(
        (blockCoordinate - LodSilhouetteField.NodeOriginBlocks) / LodSilhouetteField.LatticeBlocks);

    static bool Covers(Demand demand, LodSilhouetteTile tile) => CoversTile(demand, tile.TileX, tile.TileZ);

    /// <summary>The tile's nearest point lies within the reach plus the one-tile margin.</summary>
    static bool CoversTile(Demand demand, int tileX, int tileZ)
    {
        double blockX = tileX * (double)LodSilhouetteTile.TileBlocks;
        double blockZ = tileZ * (double)LodSilhouetteTile.TileBlocks;
        double nearestX = Math.Clamp(demand.CentreX, blockX, blockX + LodSilhouetteTile.TileBlocks);
        double nearestZ = Math.Clamp(demand.CentreZ, blockZ, blockZ + LodSilhouetteTile.TileBlocks);
        double dx = nearestX - demand.CentreX, dz = nearestZ - demand.CentreZ;
        double limit = demand.ReachBlocks + LodSilhouetteTile.TileBlocks;
        return dx * dx + dz * dz <= limit * limit;
    }

    void IssueWork(long now)
    {
        // Until the store has said what it holds, any preview might repeat one already paid for.
        if (store != null && storedBeforeSession == null) return;
        if (MustYield(generationAuthority(), generationGate(),
                sapi.WorldManager.CurrentGeneratingChunkCount))
        {
            YieldTicks++;
            return;
        }

        bool previewsAvailable = !generatorWatch.Stalled && !errorBrake.Tripped;
        // Only previews spend the rate: they are the worldgen cost. Probes are existence reads,
        // bounded by their own in-flight cap and by the short queue of empty positions. A stopped
        // generator does not stop the independent accelerator path.
        int allowance = previewAllowance.Available(now, previewsPerSecond);
        var budget = new LodWorkBudget(TickWorkBudgetMs);
        int peeks = 0;
        while (previewsAvailable && peeks < allowance && pendingPeeks.Count > 0
            && peeksInFlight.Count < MaxPeeksInFlight && !budget.Expired)
        {
            (int cx, int cz) = pendingPeeks.Dequeue();
            IssuePeek(cx, cz, now);
            peeks++;
        }
        previewAllowance.Spend(peeks);

        // Then probe further out, players in turn.
        int probes = 0, idle = 0;
        while (probes < MaxProbeIssuesPerTick && idle < demandOrder.Count
            && probesInFlight < MaxProbesInFlight && pendingPeeks.Count < MaxPeeksInFlight
            && !budget.Expired)
        {
            Demand demand = demands[demandOrder[nextDemand++ % demandOrder.Count]];
            if (ProbeNext(demand, previewsAvailable) == EnumProbeStep.Probed)
            {
                probes++;
                idle = 0;
            }
            else
            {
                idle++;
            }
        }
    }

    internal static bool MustYield(EnumWorldGenAuthority authority, EnumWorldGenGate gate,
        int generatingChunks) => authority != EnumWorldGenAuthority.None
        || gate != EnumWorldGenGate.None || generatingChunks >= YieldGeneratingChunks;

    EnumProbeStep ProbeNext(Demand demand, bool previewsAvailable)
    {
        double limit = demand.ReachBlocks + LodSilhouetteTile.TileBlocks;
        int skipped = 0;
        while (demand.SpiralIndex < demand.SpiralTotal)
        {
            if (++skipped > MaxSkipsPerProbe) return EnumProbeStep.Paused;
            (int di, int dj) = LodColumnMap.SpiralAt(demand.SpiralIndex++);
            int i = demand.CentreI + di, j = demand.CentreJ + dj;
            double nx = LodSilhouetteField.NodeBlockX(i) - demand.CentreX;
            double nz = LodSilhouetteField.NodeBlockX(j) - demand.CentreZ;
            if (nx * nx + nz * nz > limit * limit) continue;
            int cx = LodSilhouetteField.LatticeChunkOfNode(i);
            int cz = LodSilhouetteField.LatticeChunkOfNode(j);
            long tileKey = TileKeyOfChunk(cx, cz);
            if (!TileReady(tileKey))
            {
                // Its stored copy may hold these heights already: read it before previewing.
                WantLoad(tileKey);
                demand.SpiralIndex--;
                return EnumProbeStep.Paused;
            }
            LodSilhouetteTile? tile = tiles.GetValueOrDefault(tileKey);
            bool useAccelerator = accelerator != null && !acceleratorFailed
                && LodSilhouetteAcceleratorSampling.NeedsSamples(tile);
            // Without an accelerator this preserves P2's one-preview-per-region rule. With one,
            // an older sparse stored tile is completed at the accepted 64-block spacing.
            if (!useAccelerator && TileHasHeights(tileKey)) continue;
            if (useAccelerator && acceleratorInFlight.Count >= MaxAcceleratorJobsInFlight
                || !useAccelerator && !previewsAvailable)
            {
                demand.SpiralIndex--;
                return EnumProbeStep.Paused;
            }
            if (!attempted.Add(LodColumnMap.Key(cx, cz))) continue;
            if (!claimedTiles.Add(tileKey)) continue;
            if (useAccelerator) IssueAccelerator(LodWorld.KeySx(tileKey), LodWorld.KeySz(tileKey), cx, cz);
            else Probe(cx, cz);
            return EnumProbeStep.Probed;
        }

        if (!demand.Exhausted)
        {
            demand.Exhausted = true;
            LogStatus($"every lattice position within {demand.ReachBlocks:N0} blocks of "
                + $"{demand.Player.PlayerName} has been requested");
        }
        return EnumProbeStep.Exhausted;
    }

    void Probe(int cx, int cz)
    {
        probesInFlight++;
        Probed++;
        long tileKey = TileKeyOfChunk(cx, cz);
        sapi.WorldManager.TestMapChunkExists(cx, cz, exists =>
        {
            // The callback need not be on the main thread; every field here is main-thread state.
            sapi.Event.EnqueueMainThreadTask(() =>
            {
                probesInFlight--;
                GeneratorAnswered();
                if (exists)
                {
                    // Existing terrain is never previewed: its heights arrive when the server
                    // loads it, and the client draws its cached terrain there anyway. Another
                    // lattice chunk of the tile may still be previewed.
                    Existing++;
                    claimedTiles.Remove(tileKey);
                }
                else if (!generatorWatch.Stalled && !errorBrake.Tripped)
                {
                    pendingPeeks.Enqueue((cx, cz));
                }
            }, "vh-silhouette-probe");
        });
    }

    void IssuePeek(int cx, int cz, long now)
    {
        peeksInFlight[LodColumnMap.Key(cx, cz)] = new PeekInFlight(cx, cz, now);
        PeeksChanged();
        Peeked++;
        sapi.WorldManager.PeekChunkColumn(cx, cz, new ChunkPeekOptions
        {
            UntilPass = EnumWorldGenPass.Terrain,
            OnGenerated = columns => OnPeeked(cx, cz, columns),
        });
    }

    void OnPeeked(int cx, int cz, Dictionary<Vec2i, IServerChunk[]>? columns)
    {
        // Copy on whatever thread this is, then publish on the main thread.
        ushort[]? heights = null;
        int zeros = 0;
        bool forestMap = false;
        if (columns != null
            && columns.TryGetValue(new Vec2i(cx, cz), out IServerChunk[]? column)
            && column is { Length: > 0 })
        {
            IMapChunk? map = column[0].MapChunk;
            ushort[]? source = map?.WorldGenTerrainHeightMap;
            if (source != null && source.Length >= LodSilhouetteTile.HeightMapLength)
            {
                heights = new ushort[LodSilhouetteTile.HeightMapLength];
                Array.Copy(source, heights, heights.Length);
                foreach (ushort h in heights)
                    if (h == 0) zeros++;
            }
            forestMap = map?.MapRegion?.ForestMap != null;
        }
        sapi.Event.EnqueueMainThreadTask(() => Land(cx, cz, heights, zeros, forestMap),
            "vh-silhouette-peeked");
    }

    void Land(int cx, int cz, ushort[]? heights, int zeros, bool forestMap)
    {
        // Even a preview already given up on shows the generator is still answering.
        GeneratorAnswered();
        // A callback after its timeout has already been counted.
        if (!peeksInFlight.Remove(LodColumnMap.Key(cx, cz))) return;
        PeeksChanged();
        (int tileX, int tileZ) = LodSilhouetteTile.TileOfChunk(cx, cz);
        long key = LodSilhouetteTile.KeyOf(tileX, tileZ);
        // The tile's one preview is over. With heights it needs no other; without, another may try.
        claimedTiles.Remove(key);
        lastLandedMs = sapi.World.ElapsedMilliseconds;
        lastLandedCx = cx;
        lastLandedCz = cz;
        if (heights == null)
        {
            if (++Empty == 1)
            {
                logger.Warning(
                    "Horizon silhouettes: the preview of chunk {0},{1} carried no worldgen terrain "
                    + "height map, so it gave no heights. If every preview reports this, silhouettes "
                    + "stay empty and the worldgen pass is the thing to investigate.", cx, cz);
            }
            return;
        }

        if (!firstPeekReported)
        {
            // P0.1: the heights are source-traced as populated with the owner's worldgen mods;
            // this line is the runtime confirmation, and ForestMap is the later canopy's input.
            firstPeekReported = true;
            logger.Notification(
                "Horizon silhouettes: first preview landed (chunk {0},{1}): {2} of 1,024 worldgen "
                + "terrain heights are zero (expect 0); map region forest map {3}.",
                cx, cz, zeros, forestMap ? "present" : "absent");
        }

        if (!tiles.TryGetValue(key, out LodSilhouetteTile? tile))
            tiles[key] = tile = new LodSilhouetteTile(tileX, tileZ);
        if (!tile.SetChunk(cx, cz, heights, EnumSilhouetteOrigin.Preview)) return;
        Landed++;
        dirtyTiles.Add(key);
        foreach (Demand demand in demands.Values)
            if (Covers(demand, tile)) demand.Queued.Add(key);
    }

    void ExpireStuckPeeks(long now)
    {
        if (peeksInFlight.Count == 0) return;
        stale.Clear();
        foreach (KeyValuePair<long, PeekInFlight> entry in peeksInFlight)
            if (now - entry.Value.IssuedMs > StuckPeekTimeoutMs) stale.Add(entry.Key);
        if (stale.Count == 0) return;
        foreach (long key in stale)
        {
            if (peeksInFlight.Remove(key, out PeekInFlight peek))
                claimedTiles.Remove(TileKeyOfChunk(peek.Cx, peek.Cz));
        }
        PeeksChanged();
        TimedOut += stale.Count;
        logger.Warning("Horizon silhouettes: {0} previews never called back and were given up on "
            + "({1} total). Those positions stay without heights this session.", stale.Count, TimedOut);
    }

    /// <summary>
    /// Offer current revisions without encoding their payloads. One protocol-sized manifest per
    /// player per tick bounds the owner-thread work; later tile changes are ordinary delta chunks.
    /// The direct-push branch exists only for clients from before the final transport landed.
    /// </summary>
    void SendOffers()
    {
        foreach (Demand demand in demands.Values)
        {
            if (demand.TransportVersion < LodAssist.SilhouetteTransportVersion) continue;
            if (demand.Queued.Count == 0 && !demand.ManifestPending) continue;

            long[] ordered = demand.Queued
                .OrderBy(key => DistanceSq(demand, key))
                .Take(LodAssist.ManifestKeysPerMessage)
                .ToArray();
            var keys = new List<long>(ordered.Length);
            var revisions = new List<int>(ordered.Length);
            foreach (long key in ordered)
            {
                if (!tiles.TryGetValue(key, out LodSilhouetteTile? tile)) continue;
                if (demand.Offered.TryGetValue(key, out int revision) && revision == tile.Revision) continue;
                keys.Add(key);
                revisions.Add(tile.Revision);
            }
            bool last = ordered.Length == demand.Queued.Count;
            var message = new AssistSilhouetteManifest
            {
                Sequence = demand.ManifestSequence,
                Last = last,
                Keys = keys.ToArray(),
                Revisions = revisions.ToArray(),
                SessionId = demand.SessionId,
            };
            if (!sendManifest(demand.Player, message)) continue;
            foreach (long key in ordered) demand.Queued.Remove(key);
            for (int i = 0; i < keys.Count; i++) demand.Offered[keys[i]] = revisions[i];
            demand.ManifestSequence++;
            demand.ManifestPending = !last;
            ManifestsSent++;
        }
    }

    /// <summary>
    /// Serve at most one reply per player per tick, rotating who starts. The independent global
    /// allowance caps aggregate work while the per-player allowances prevent one client taking it.
    /// Legacy pushes use the same caps.
    /// </summary>
    void ServeTiles(long now)
    {
        if (demandOrder.Count == 0) return;
        int global = tileSendAllowance.Available(now, LodAssist.MaxSilhouetteTilesPerSecondTotal);
        int sent = 0;
        int visited = 0;
        int start = nextSendDemand % demandOrder.Count;
        while (sent < global && visited < demandOrder.Count)
        {
            int index = (start + visited++) % demandOrder.Count;
            if (!demands.TryGetValue(demandOrder[index], out Demand? demand)) continue;
            int allowance = demand.SendAllowance.Available(now, TilesPerSecondPerPlayer);
            if (allowance <= 0) continue;

            bool used = demand.TransportVersion < LodAssist.SilhouetteTransportVersion
                ? SendLegacyTile(demand)
                : SendRequestedTile(demand);
            if (!used) continue;
            demand.SendAllowance.Spend(1);
            sent++;
        }
        tileSendAllowance.Spend(sent);
        nextSendDemand = (start + 1) % Math.Max(1, demandOrder.Count);
    }

    bool SendLegacyTile(Demand demand)
    {
        while (demand.Queued.Count > 0)
        {
            long key = NearestQueued(demand);
            if (!tiles.TryGetValue(key, out LodSilhouetteTile? tile))
            {
                demand.Queued.Remove(key);
                continue;
            }
            if (demand.Offered.TryGetValue(key, out int revision) && revision == tile.Revision)
            {
                demand.Queued.Remove(key);
                continue;
            }
            byte[] payload = tile.Encode();
            if (!sendTile(demand.Player, TileMessage(tile, payload, sessionId: 0))) return false;
            demand.Queued.Remove(key);
            demand.Offered[key] = tile.Revision;
            TilesSent++;
            BytesSent += payload.Length;
            return true;
        }
        return false;
    }

    bool SendRequestedTile(Demand demand)
    {
        if (demand.Requests.Count == 0) return false;
        (long key, long sessionId) = demand.Requests.Peek();
        if (sessionId != demand.SessionId || LodWorld.KeyLevel(key) != LodSilhouetteTile.Level
            || !demand.Offered.ContainsKey(key)
            || !CoversTile(demand, LodWorld.KeySx(key), LodWorld.KeySz(key)))
        {
            if (!SendRefusal(demand.Player, key, sessionId, retryable: false)) return false;
            demand.Requests.Dequeue();
            return true;
        }
        if (!tiles.TryGetValue(key, out LodSilhouetteTile? tile))
        {
            if (IsStored(key)) WantLoad(key);
            if (!SendRefusal(demand.Player, key, sessionId, retryable: IsStored(key))) return false;
            demand.Requests.Dequeue();
            return true;
        }

        byte[] payload = tile.Encode();
        if (!sendTile(demand.Player, TileMessage(tile, payload, sessionId))) return false;
        demand.Requests.Dequeue();
        TilesSent++;
        BytesSent += payload.Length;
        return true;
    }

    static AssistSilhouetteTile TileMessage(LodSilhouetteTile tile, byte[] payload, long sessionId) => new()
    {
        TileX = tile.TileX,
        TileZ = tile.TileZ,
        Revision = tile.Revision,
        Payload = payload,
        SessionId = sessionId,
        Key = tile.Key,
    };

    bool SendRefusal(IServerPlayer player, long key, long sessionId, bool retryable)
    {
        int tileX = LodWorld.KeyLevel(key) == LodSilhouetteTile.Level ? LodWorld.KeySx(key) : 0;
        int tileZ = LodWorld.KeyLevel(key) == LodSilhouetteTile.Level ? LodWorld.KeySz(key) : 0;
        if (!sendTile(player, new AssistSilhouetteTile
        {
            TileX = tileX,
            TileZ = tileZ,
            Payload = Array.Empty<byte>(),
            Retryable = retryable,
            SessionId = sessionId,
            Key = key,
        })) return false;
        if (retryable) TilesRetryable++;
        else TilesRefused++;
        return true;
    }

    long NearestQueued(Demand demand)
    {
        long best = 0;
        double bestDistance = double.MaxValue;
        foreach (long key in demand.Queued)
        {
            double x = (LodWorld.KeySx(key) + 0.5) * LodSilhouetteTile.TileBlocks - demand.CentreX;
            double z = (LodWorld.KeySz(key) + 0.5) * LodSilhouetteTile.TileBlocks - demand.CentreZ;
            double distance = x * x + z * z;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = key;
        }
        return best;
    }

    static double DistanceSq(Demand demand, long key)
    {
        double x = (LodWorld.KeySx(key) + 0.5) * LodSilhouetteTile.TileBlocks - demand.CentreX;
        double z = (LodWorld.KeySz(key) + 0.5) * LodSilhouetteTile.TileBlocks - demand.CentreZ;
        return x * x + z * z;
    }

    /// <summary>The key nearest to any requesting player.</summary>
    long NearestTo(HashSet<long> keys)
    {
        long best = 0;
        double bestDistance = double.MaxValue;
        foreach (long key in keys)
        {
            foreach (Demand demand in demands.Values)
            {
                double x = (LodWorld.KeySx(key) + 0.5) * LodSilhouetteTile.TileBlocks - demand.CentreX;
                double z = (LodWorld.KeySz(key) + 0.5) * LodSilhouetteTile.TileBlocks - demand.CentreZ;
                double distance = x * x + z * z;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = key;
            }
        }
        return best;
    }

    /// <summary>
    /// Main thread: every cell where the builder holds a real height (captured or previewed) in the
    /// square of (2r+1) chunks a side centred on a chunk. For the accelerator comparison (plan P0.3).
    /// </summary>
    public List<LodRealHeight> RealHeightCells(int centreCx, int centreCz, int radiusChunks)
    {
        var cells = new List<LodRealHeight>();
        for (int cz = centreCz - radiusChunks; cz <= centreCz + radiusChunks; cz++)
        {
            for (int cx = centreCx - radiusChunks; cx <= centreCx + radiusChunks; cx++)
            {
                (int tileX, int tileZ) = LodSilhouetteTile.TileOfChunk(cx, cz);
                if (!tiles.TryGetValue(LodSilhouetteTile.KeyOf(tileX, tileZ), out LodSilhouetteTile? tile)) continue;
                int cellX0 = (cx - tileX * LodSilhouetteTile.ChunksPerEdge) * LodSilhouetteTile.CellsPerChunk;
                int cellZ0 = (cz - tileZ * LodSilhouetteTile.ChunksPerEdge) * LodSilhouetteTile.CellsPerChunk;
                for (int cellZ = cellZ0; cellZ < cellZ0 + LodSilhouetteTile.CellsPerChunk; cellZ++)
                {
                    for (int cellX = cellX0; cellX < cellX0 + LodSilhouetteTile.CellsPerChunk; cellX++)
                    {
                        EnumSilhouetteOrigin origin = tile.OriginAt(cellX, cellZ);
                        if (origin != EnumSilhouetteOrigin.Captured && origin != EnumSilhouetteOrigin.Preview) continue;
                        if (!tile.TryGetHeight(cellX, cellZ, out ushort height)) continue;
                        cells.Add(new LodRealHeight(tile.BlockX + cellX * LodSilhouetteTile.CellBlocks,
                            tile.BlockZ + cellZ * LodSilhouetteTile.CellBlocks, height, origin));
                    }
                }
            }
        }
        return cells;
    }

    /// <summary>Main thread: the chunks of that square for which the builder holds real heights now.</summary>
    public HashSet<long> HeldChunks(int centreCx, int centreCz, int radiusChunks)
    {
        var held = new HashSet<long>();
        foreach (LodRealHeight cell in RealHeightCells(centreCx, centreCz, radiusChunks))
            held.Add(LodAcceleratorPresampleRun.ChunkKey(cell.X >> 5, cell.Z >> 5));
        return held;
    }

    public string Describe()
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        string text = string.Format(culture,
            "{0} player(s), {1} tiles in memory; heights from {2} loaded or previewed columns ({3} "
            + "matched their rain heights, like an old save's copy or bare ground); {4} lattice probes ({5} already hold terrain); previews {6} "
            + "issued, {7} landed, {8} without heights, {9} timed out, {10} in flight; held {11} ticks "
            + "for other generation; {12} tile messages sent ({13:0.0} KB)",
            demands.Count, tiles.Count, CapturedColumns, RainColumns, Probed, Existing, Peeked, Landed,
            Empty, TimedOut, peeksInFlight.Count, YieldTicks, TilesSent, BytesSent / 1024.0);
        text += string.Format(culture,
            "; delivery {0} manifests, {1} tile requests, {2} explicit refusals, {3} retryable",
            ManifestsSent, TilesRequested, TilesRefused, TilesRetryable);
        if (accelerator != null)
        {
            text += string.Format(culture,
                "; accelerator {0}: {1} tiles issued, {2} landed ({3:N0} cells), {4} in flight, "
                + "{5} low/coastal previews, {6}", accelerator.Name, AcceleratorTilesIssued,
                AcceleratorTilesLanded, AcceleratorCells, acceleratorInFlight.Count, CoastPreviews,
                acceleratorFailed ? "FAILED, previews took over" : "active (one background caller)");
        }
        if (store == null)
        {
            text += openStore != null
                ? "; no tile store yet: this world gets one the first time a player asks for silhouettes"
                : "; no tile store, so nothing is kept after this session";
        }
        else
        {
            text += string.Format(culture,
                "; tile store {0}: {1} tiles stored, {2} read back, {3} saves this session ({4} waiting for "
                + "the next world save, {5} being written), {6} let go from memory, {7:0.0} MiB on disk{8}",
                store.OpenFailed ? "FAILED to open" : "open",
                Math.Max(store.StoredTiles, storedBeforeSession?.Count ?? 0), StoreReads, TilesSaved,
                dirtyTiles.Count, store.PendingSaves, Evicted, store.FileBytes / (1024.0 * 1024.0),
                store.SaveErrors + store.ReadErrors + store.Undecodable == 0 ? ""
                    : string.Format(culture, ", {0} save errors, {1} read errors, {2} undecodable rows",
                        store.SaveErrors, store.ReadErrors, store.Undecodable));
        }
        if (errorBrake.Tripped)
        {
            text += string.Format(culture, "; previews STOPPED for this session: the server logged {0} "
                + "errors while they were being generated", errorBrake.ErrorsDuringPreviews);
        }
        else if (errorBrake.ErrorsDuringPreviews > 0)
        {
            text += string.Format(culture, "; {0} server errors logged during previews so far",
                errorBrake.ErrorsDuringPreviews);
        }
        if (!generatorWatch.Stalled) return text;
        return text + string.Format(culture,
            "; STOPPED for this session: the world generator went silent for {0} s ({1} answers came "
            + "back later)", (generatorWatch.StalledAtMs - generatorWatch.SilentSinceMs) / 1000,
            generatorWatch.LateAnswers);
    }

    void LogStatus(string phase) =>
        logger.Notification("Horizon silhouettes ({0}): {1}.", phase, Describe());
}
