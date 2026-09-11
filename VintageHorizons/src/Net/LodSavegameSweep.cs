using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace VintageHorizons.Net;

/// <summary>
/// Builds the LOD cache from terrain the world already has, by loading chunk columns that
/// were generated in some earlier session.
///
/// This is the cheap half of pre-generation and the one worth having on by default. A
/// savegame accumulates terrain for as long as anyone plays, while the LOD cache only ever
/// saw the fraction that happened to stream past a player running this mod - measured on a
/// test world at 12,632 generated columns against 620 captured sections, and a world played
/// for weeks skews far harder than that. All of it is already on disk, already paid for.
///
/// The distinction from pre-generation (<see cref="LodPlayerPregen"/>) is the whole
/// point: that creates terrain nobody has visited, and reveals places no player has
/// been. Sweeping creates nothing. It indexes what exists, so it is safe to default on
/// where pre-generation is not.
///
/// Keeping that promise takes more than checking the target column, which is what the first
/// version did and why this does two passes now. Loading a column whose surroundings are
/// absent makes the engine generate them, because worldgen runs in passes and finishing one
/// column requires its neighbours to have reached an earlier pass - which requires theirs.
/// A one-pass sweep of 8,464 existing columns added 1,460 brand new ones to the savegame.
/// Sweeping the same world with a radius that fell entirely inside generated terrain added
/// exactly zero, which is what identified the cause as the frontier rather than the loads.
///
/// So: probe everything first, then load only columns whose whole neighbourhood is already
/// on disk. The cost is a border of real terrain going uncaptured, which for any world worth
/// sweeping is a rounding error against not generating anything at all.
/// </summary>
public class LodSavegameSweep
{
    readonly ICoreServerAPI sapi;
    readonly ILogger logger;
    readonly int radiusChunks;
    readonly int perSecond;
    readonly int refreshPerSecond;
    readonly Action<int, int> queueColumn;
    readonly WeatherSystemBase? weather;
    readonly int chunksPerRegion;
    readonly bool snowAccumulationEnabled;

    /// <summary>
    /// Probes outstanding with the engine. Bounded because the spiral would otherwise queue
    /// every position on the first tick - 66k callbacks for the default radius, all landing
    /// before anything useful had happened.
    /// </summary>
    const int MaxProbesInFlight = 256;
    const int MaxProbeIssuesPerTick = 16;
    const int MaxSnowSnapshotsPerPoll = 480;
    const double TickWorkBudgetMs = 1.0;
    const long SnowPollIntervalMs = 30_000;

    /// <summary>Which positions hold generated terrain, and the safety rule over them.</summary>
    readonly LodColumnMap exists = new();
    readonly LodTickAllowance loadAllowance = new();
    readonly LodTickAllowance refreshAllowance = new();
    readonly LodSnowRefreshScheduler snowRefresh = new();
    readonly LodSnowActivityTracker snowActivity = new();
    readonly Queue<long> weatherPollQueue = new();
    readonly HashSet<long> weatherPollPending = new();
    readonly List<SnowAccumSnapshot> snapshotScratch = new();

    /// <summary>Owning-thread sweep costs since the last server telemetry report.</summary>
    public LodPhaseCost ProbeIssueCost, ProbePublishCost, LoadIssueCost, RefreshIssueCost;

    /// <summary>Allocation sampling is opt-in; elapsed timing remains always active.</summary>
    public bool TrackPhaseAllocations { get; set; }

    int probeIndex;
    int probesInFlight;
    int loadIndex;
    int reported;
    int nextCanaryRegion;
    long nextSnowPollMs;
    long listenerId;
    bool listenerRegistered;
    bool active;
    bool verifying;

    int spawnCx;
    int spawnCz;

    /// <summary>
    /// Probing reaches past the load area by the neighbourhood width, so a column on the
    /// edge of the sweep still has known neighbours rather than being skipped for want of
    /// information about positions nobody looked at.
    /// </summary>
    int ProbeRadius => radiusChunks + LodColumnMap.SafeNeighbourhood;
    int ProbeTotal => (2 * ProbeRadius + 1) * (2 * ProbeRadius + 1);
    int LoadTotal => (2 * radiusChunks + 1) * (2 * radiusChunks + 1);

    public bool Probing { get; private set; } = true;
    public bool Done { get; private set; }

    /// <summary>Positions that held generated terrain. Not all of them get loaded.</summary>
    public int Found => exists.Count;

    /// <summary>Columns actually loaded - those with a complete neighbourhood.</summary>
    public int Loaded { get; private set; }

    /// <summary>Existing columns skipped because a neighbour was missing.</summary>
    public int SkippedEdge { get; private set; }

    /// <summary>Campaign recaptures requested since the startup sweep completed.</summary>
    public long Refreshed { get; private set; }

    /// <summary>Dormant map regions woken since startup to reveal new vanilla snapshots.</summary>
    public long SnowProbes { get; private set; }

    public int RefreshCandidates => snowRefresh.Count;
    public int PendingRefreshes => snowRefresh.Pending;
    public long RefreshCampaigns => snowRefresh.Campaigns;
    bool AutomaticSnowRefresh => refreshPerSecond > 0 && weather != null && snowAccumulationEnabled;

    public LodSavegameSweep(ICoreServerAPI sapi, ILogger logger, int radiusChunks, int perSecond,
        int refreshPerSecond, WeatherSystemBase? weather, Action<int, int> queueColumn)
    {
        this.sapi = sapi;
        this.logger = logger;
        this.radiusChunks = radiusChunks;
        this.perSecond = Math.Max(1, perSecond);
        this.refreshPerSecond = Math.Max(0, refreshPerSecond);
        this.weather = weather;
        this.queueColumn = queueColumn;
        chunksPerRegion = Math.Max(1, sapi.WorldManager.RegionSize / GlobalConstants.ChunkSize);
        snowAccumulationEnabled = sapi.World.Config.GetBool("snowAccum", true);
    }

    public void Start()
    {
        active = true;
        spawnCx = (int)sapi.World.DefaultSpawnPosition.X / GlobalConstants.ChunkSize;
        spawnCz = (int)sapi.World.DefaultSpawnPosition.Z / GlobalConstants.ChunkSize;

        logger.Notification(
            "Sweeping the savegame for terrain that already exists, out to {0} blocks around "
            + "spawn ({1} positions to examine). Nothing is generated: columns that were never "
            + "visited are skipped, and so are columns on the edge of explored terrain, "
            + "because loading those would make the engine generate their missing neighbours. "
            + "Set SweepSavegame to false to disable. Progress every 10%. Afterward, {2}.",
            radiusChunks * GlobalConstants.ChunkSize, ProbeTotal,
            AutomaticSnowRefresh
                ? $"snow changes will start finite regional refreshes at up to {refreshPerSecond}/s"
                : "automatic snow refresh is disabled");

        listenerId = sapi.Event.RegisterGameTickListener(_ => Step(), LodTickAllowance.TickMilliseconds);
        listenerRegistered = true;
    }

    void Step()
    {
        if (!active || verifying) return;
        LodPhaseStart phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        if (Done)
        {
            StepSnowSignals();
            StepRefresh();
            RefreshIssueCost.Add(phaseStart);
        }
        else if (Probing)
        {
            StepProbe();
            ProbeIssueCost.Add(phaseStart);
        }
        else
        {
            StepLoad();
            LoadIssueCost.Add(phaseStart);
        }
    }

    void StepProbe()
    {
        // Refill the outstanding cap in small pieces. The old one-second callback could
        // issue all 256 probes together and their main-thread publications could land as
        // the same periodic burst.
        var tickBudget = new LodWorkBudget(TickWorkBudgetMs);
        int issued = 0;
        while (probeIndex < ProbeTotal && probesInFlight < MaxProbesInFlight
            && issued < MaxProbeIssuesPerTick && !tickBudget.Expired)
        {
            (int dx, int dz) = LodColumnMap.SpiralAt(probeIndex++);
            int cx = spawnCx + dx;
            int cz = spawnCz + dz;

            probesInFlight++;
            issued++;
            sapi.WorldManager.TestMapChunkExists(cx, cz, hit =>
            {
                // The callback need not be on the main thread, and HashSet is not safe.
                sapi.Event.EnqueueMainThreadTask(() =>
                {
                    LodPhaseStart publishStart = LodPhaseCost.Start(TrackPhaseAllocations);
                    probesInFlight--;
                    if (hit) exists.Add(cx, cz);
                    ProbePublishCost.Add(publishStart);
                }, "vh-sweep-probe");
            });
        }

        int percent = probeIndex * 100 / ProbeTotal;
        if (percent >= reported + 10)
        {
            reported = percent - percent % 10;
            logger.Notification("Savegame sweep: examined {0}% ({1}/{2}), {3} hold terrain",
                reported, probeIndex, ProbeTotal, exists.Count);
        }

        if (probeIndex < ProbeTotal || probesInFlight > 0) return;

        Probing = false;
        reported = 0;
        logger.Notification(
            "Savegame sweep: {0} of {1} positions hold generated terrain. Loading those with a "
            + "complete neighbourhood.", exists.Count, ProbeTotal);
    }

    void StepLoad()
    {
        int allowance = loadAllowance.Available(sapi.World.ElapsedMilliseconds, perSecond);
        var tickBudget = new LodWorkBudget(TickWorkBudgetMs);
        int loaded = 0;
        while (loadIndex < LoadTotal && loaded < allowance && !tickBudget.Expired)
        {
            (int dx, int dz) = LodColumnMap.SpiralAt(loadIndex++);
            int cx = spawnCx + dx;
            int cz = spawnCz + dz;

            switch (exists.Classify(cx, cz))
            {
                case EnumColumnAction.Peek:
                    // Not in the savegame. The sweep indexes what exists and creates
                    // nothing, so there is no work here. Generation (/vhgen) is the
                    // feature that acts on this arm.
                    continue;

                case EnumColumnAction.SkipFrontier:
                    // On the frontier of explored terrain. A load here would generate
                    // whatever is missing beside it - the one thing this must not do. Keep
                    // it in the snow candidate set: ordinary exploration may fill that
                    // missing neighbourhood later, and its load event then captures it.
                    snowRefresh.Add(cx, cz, chunksPerRegion);
                    SkippedEdge++;
                    continue;
            }

            snowRefresh.Add(cx, cz, chunksPerRegion);
            // Not KeepLoaded: each column needs to pass through capture once, not stay
            // resident. A radius worth sweeping is far more terrain than fits in memory.
            sapi.WorldManager.LoadChunkColumnPriority(cx, cz);
            Loaded++;
            loaded++;
        }
        loadAllowance.Spend(loaded);

        int percent = loadIndex * 100 / LoadTotal;
        if (percent >= reported + 10)
        {
            reported = percent - percent % 10;
            logger.Notification("Savegame sweep: loaded {0}% ({1} columns, {2} skipped as edge)",
                reported, Loaded, SkippedEdge);
        }

        if (loadIndex < LoadTotal) return;

        // The promise gets measured before it gets announced: re-probe a sample of the
        // positions that did not exist, and report the result on the finish line.
        verifying = true;
        new LodAbsenceVerifier(sapi,
            exists.AbsentSample(spawnCx, spawnCz, ProbeRadius, LodAbsenceVerifier.MaxSample, LodAbsenceVerifier.AwayFromPlayers(sapi)),
            Finish).Start();
    }

    void Finish(LodAbsenceVerifier verified)
    {
        Done = true;
        PrimeSnowActivity();
        nextSnowPollMs = sapi.World.ElapsedMilliseconds + SnowPollIntervalMs;

        if (verified.Regrown > 0)
        {
            logger.Warning(
                "Savegame sweep: {0} of {1} sampled positions that did not exist before the "
                + "sweep now exist. The sweep must generate nothing, so worldgen on this "
                + "server reaches further than the measured safe neighbourhood - a worldgen "
                + "mod is the likely cause. Consider SweepSavegame: false here, and please "
                + "report this.", verified.Regrown, verified.Checked);
        }

        logger.Notification(
            "Savegame sweep finished: {0} columns loaded from terrain that already existed, "
            + "{1} skipped on the frontier, nothing generated. {2}. Capture continues in the "
            + "background; the cache is complete once no columns remain queued. {3}",
            Loaded, SkippedEdge, verified.Describe(),
            AutomaticSnowRefresh
                ? $"Automatic snow refresh is watching {snowRefresh.Count} existing columns "
                    + $"in {snowRefresh.RegionCount} map regions; weather changes create finite "
                    + $"regional campaigns at up to {refreshPerSecond}/s."
                : "Automatic snow refresh is disabled.");

        if (!AutomaticSnowRefresh) Stop();
    }

    /// <summary>
    /// Learn about terrain generated or loaded after the initial probe. Chunk-load events are
    /// on the server thread, the same owner as Step, so both collections stay single-threaded.
    /// The wider probe bound is retained because an edge column can complete the safety proof
    /// for a refresh candidate just inside the configured radius.
    /// </summary>
    public void ObserveLoadedColumn(int cx, int cz)
    {
        if (!active || !Within(cx, cz, ProbeRadius)) return;
        exists.Add(cx, cz);
        if (Within(cx, cz, radiusChunks)) snowRefresh.Add(cx, cz, chunksPerRegion);
        if (Done && weather != null)
        {
            QueueWeatherPoll(LodColumnMap.Key(
                FloorDiv(cx, chunksPerRegion), FloorDiv(cz, chunksPerRegion)));
        }
    }

    /// <summary>
    /// Learn the authoritative result of a loaded-column capture. This is not a capability
    /// test: zero covered columns simply means bare right now, not "cannot ever snow".
    /// </summary>
    public void ObserveSnowState(int cx, int cz, int observedColumns, int coveredColumns)
    {
        if (!active) return;
        snowRefresh.Observe(cx, cz, observedColumns, coveredColumns);
    }

    void StepSnowSignals()
    {
        if (!AutomaticSnowRefresh) return;

        long now = sapi.World.ElapsedMilliseconds;
        if (now >= nextSnowPollMs)
        {
            nextSnowPollMs = now + SnowPollIntervalMs;
            ScheduleActiveWeatherPolls();
            WakeOneDormantRegion();
        }

        PollOneWeatherRegion();
    }

    void PrimeSnowActivity()
    {
        if (!AutomaticSnowRefresh) return;

        double now = sapi.World.Calendar.TotalHours;
        for (int i = 0; i < snowRefresh.RegionCount; i++)
        {
            if (!snowRefresh.TryRegionAt(i, out int rx, out int rz, out _, out _)) continue;
            long key = LodColumnMap.Key(rx, rz);
            double latest = weather!.weatherSimByMapRegion.TryGetValue(key, out WeatherSimulationRegion? sim)
                ? LatestSnapshotHours(sim, now)
                : now;
            snowActivity.Prime(key, latest);
        }
    }

    void ScheduleActiveWeatherPolls()
    {
        // The weather dictionary retains stale simulations until the next world save.
        // AllLoadedMapRegions is the set vanilla actually advances every 200 ms.
        foreach (long key in sapi.WorldManager.AllLoadedMapRegions.Keys)
        {
            if (snowRefresh.ContainsRegion(key)
                && weather!.weatherSimByMapRegion.ContainsKey(key)) QueueWeatherPoll(key);
        }
    }

    /// <summary>
    /// Vanilla advances snow history only for loaded map regions. One safe canary column
    /// every 30 seconds wakes a dormant region, so all regions are eventually checked while
    /// replacing the old one-column-per-second forever sweep with one thirtieth of a load per
    /// second when everything is dormant.
    /// </summary>
    void WakeOneDormantRegion()
    {
        int count = snowRefresh.RegionCount;
        for (int examined = 0; examined < count; examined++)
        {
            int index = nextCanaryRegion++;
            if (nextCanaryRegion >= count) nextCanaryRegion = 0;
            if (!snowRefresh.TryRegionAt(index, out int rx, out int rz, out _, out _)) return;

            long regionKey = LodColumnMap.Key(rx, rz);
            if (sapi.WorldManager.GetMapRegion(regionKey) != null)
            {
                QueueWeatherPoll(regionKey);
                continue;
            }

            if (!snowRefresh.TryRegionCanaryAt(index,
                    (candidateX, candidateZ) =>
                        exists.Classify(candidateX, candidateZ) == EnumColumnAction.Load,
                    out _, out _, out int cx, out int cz)) continue;
            if (sapi.WorldManager.GetMapChunk(cx, cz) != null)
            {
                QueueWeatherPoll(regionKey);
            }
            else
            {
                sapi.WorldManager.LoadChunkColumnPriority(cx, cz);
                SnowProbes++;
            }
            return;
        }
    }

    void QueueWeatherPoll(long regionKey)
    {
        if (weatherPollPending.Add(regionKey)) weatherPollQueue.Enqueue(regionKey);
    }

    void PollOneWeatherRegion()
    {
        if (!weatherPollQueue.TryDequeue(out long regionKey)) return;
        weatherPollPending.Remove(regionKey);
        if (!weather!.weatherSimByMapRegion.TryGetValue(
                regionKey, out WeatherSimulationRegion? sim)) return;

        // A newly woken region advances at most 480 missed game-hours per vanilla weather
        // tick. Do not launch loads from a partial history: vanilla's immediate chunk snow
        // update also refuses while this gap exceeds one hour. Revisit on a later 50 ms tick
        // only while the map region is still resident and therefore making progress.
        if (sapi.World.Calendar.TotalHours - sim.LastUpdateTotalHours > 1)
        {
            if (sapi.WorldManager.GetMapRegion(regionKey) != null) QueueWeatherPoll(regionKey);
            return;
        }

        double lastHours = snowActivity.LastSnapshotHours(regionKey);
        snapshotScratch.Clear();
        bool historyTruncated = false;
        lock (WeatherSimulationRegion.snowAccumSnapshotLock)
        {
            int length = sim.SnowAccumSnapshots.Length;
            int index = sim.SnowAccumSnapshots.EndPosition - 1;
            if (index < 0) index += length;
            for (int n = 0; n < length; n++)
            {
                SnowAccumSnapshot? snapshot = sim.SnowAccumSnapshots[index];
                if (snapshot == null || snapshot.TotalHours <= lastHours) break;
                if (snapshotScratch.Count >= MaxSnowSnapshotsPerPoll)
                {
                    historyTruncated = true;
                    break;
                }
                snapshotScratch.Add(snapshot);
                if (--index < 0) index += length;
            }
        }

        float limit = Math.Max(1, weather.GeneralConfig.SnowLayerBlocks.Count) + 0.6f;
        // A time jump larger than vanilla's own 480-hour update slice is rare. One
        // conservative reconciliation in both directions is cheaper and safer than
        // processing a game-year ring in one server tick or guessing what was skipped.
        EnumLodSnowChange change = historyTruncated
            ? EnumLodSnowChange.Accumulating | EnumLodSnowChange.Melting
            : EnumLodSnowChange.None;
        for (int i = snapshotScratch.Count - 1; i >= 0; i--)
        {
            SnowAccumSnapshot snapshot = snapshotScratch[i];
            change |= snowActivity.Observe(regionKey, snapshot.TotalHours,
                snapshot.SnowAccumulationByRegionCorner.Data, limit);
        }

        if (change != EnumLodSnowChange.None)
        {
            snowRefresh.Signal(LodColumnMap.KeyCx(regionKey),
                LodColumnMap.KeyCz(regionKey), change);
        }
    }

    static double LatestSnapshotHours(WeatherSimulationRegion sim, double fallback)
    {
        lock (WeatherSimulationRegion.snowAccumSnapshotLock)
        {
            int length = sim.SnowAccumSnapshots.Length;
            int index = sim.SnowAccumSnapshots.EndPosition - 1;
            if (index < 0) index += length;
            SnowAccumSnapshot? latest = sim.SnowAccumSnapshots[index];
            return latest?.TotalHours ?? fallback;
        }
    }

    void StepRefresh()
    {
        if (!AutomaticSnowRefresh || snowRefresh.Pending == 0) return;

        int allowance = refreshAllowance.Available(
            sapi.World.ElapsedMilliseconds, refreshPerSecond);
        if (allowance <= 0) return;

        var tickBudget = new LodWorkBudget(TickWorkBudgetMs);
        int examined = 0;
        while (examined < allowance && !tickBudget.Expired
            && snowRefresh.TryNext(out int cx, out int cz, out _))
        {
            examined++;

            // Re-prove the neighbourhood for every campaign. A candidate may have been a frontier
            // column at startup, and loading it before ordinary exploration filled the edge
            // would violate the sweep's central promise by invoking worldgen.
            if (exists.Classify(cx, cz) != EnumColumnAction.Load) continue;

            if (sapi.WorldManager.GetMapChunk(cx, cz) != null)
            {
                // Vanilla already scans resident columns for snow every three seconds. A
                // direct recapture is cheaper and avoids asking the loader for a no-op.
                QueueRefreshCapture(cx, cz);
            }
            else
            {
                // Vanilla's snow accumulator performs its immediate catch-up during the
                // begin-load event, before this completion callback. KeepLoaded stays false
                // so a regional campaign cannot grow RAM.
                sapi.WorldManager.LoadChunkColumnPriority(cx, cz, new ChunkLoadOptions
                {
                    OnLoaded = () => QueueRefreshCapture(cx, cz),
                });
            }
            Refreshed++;
        }
        refreshAllowance.Spend(examined);
    }

    void QueueRefreshCapture(int cx, int cz)
    {
        if (active) queueColumn(cx, cz);
    }

    bool Within(int cx, int cz, int radius) =>
        Math.Abs((long)cx - spawnCx) <= radius && Math.Abs((long)cz - spawnCz) <= radius;

    static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    /// <summary>Stop the persistent listener before its capture pipeline closes.</summary>
    public void Stop()
    {
        active = false;
        if (!listenerRegistered) return;
        sapi.Event.UnregisterGameTickListener(listenerId);
        listenerRegistered = false;
    }

    /// <summary>One line for /vhserver; null when no sweep was configured.</summary>
    public string Status => Done
        ? AutomaticSnowRefresh
            ? $"savegame sweep complete ({Loaded} columns loaded, {SkippedEdge} skipped on the frontier); "
                + $"snow refresh {Refreshed} recaptures, {snowRefresh.Pending} pending from "
                + $"{snowRefresh.Campaigns} regional campaigns, {SnowProbes} dormant-region "
                + $"probes over {snowRefresh.Count} candidates at {refreshPerSecond}/s"
            : $"savegame sweep complete ({Loaded} columns loaded, {SkippedEdge} skipped on the frontier)"
        : verifying
            ? $"savegame sweep verifying: re-probing {Loaded} loads' surroundings"
        : Probing
            ? $"sweeping savegame: examined {probeIndex}/{ProbeTotal}, {exists.Count} hold terrain"
            : $"sweeping savegame: loaded {Loaded}, {SkippedEdge} skipped on the frontier";

    public void ResetPhaseCosts()
    {
        ProbeIssueCost.Reset();
        ProbePublishCost.Reset();
        LoadIssueCost.Reset();
        RefreshIssueCost.Reset();
    }
}
