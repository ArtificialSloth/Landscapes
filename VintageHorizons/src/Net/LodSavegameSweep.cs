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
    LodColumnMap exists = new();

    /// <summary>Which columns may be skipped, and when a capture has earned its fingerprint.</summary>
    readonly LodCaptureLedger ledger;

    /// <summary>The audit's rotation over the claims this cache makes, and where it is.</summary>
    readonly LodTickAllowance auditAllowance = new();
    readonly List<LodAuditedColumn> auditScratch = new();
    readonly List<long> auditRequestScratch = new();
    List<long> auditOrder = new();
    int auditCursor;
    readonly LodTickAllowance loadAllowance = new();
    readonly LodTickAllowance refreshAllowance = new();
    readonly LodTickAllowance floorAllowance = new();
    readonly LodPlayerTerrainActivity playerTerrainActivity = new();
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
    int otherGenerationQueuePeak;
    long nextSnowPollMs;
    long listenerId;
    bool listenerRegistered;
    bool active;
    bool verifying;
    bool issuedSweepLoad;

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

    /// <summary>Load-phase ticks held so real engine terrain demand can drain first.</summary>
    public long EnginePressureYieldTicks { get; private set; }

    /// <summary>Largest real engine terrain queue observed during the load phase.</summary>
    public int LoadEngineQueuePeak { get; private set; }

    /// <summary>Load-phase ticks held while a player was introducing new terrain demand.</summary>
    public long PlayerActivityYieldTicks { get; private set; }

    /// <summary>Columns the floor rate still issued while a player hold was in force.</summary>
    public long PlayerHoldFloorLoads { get; private set; }

    /// <summary>Columns skipped because the cache already holds this exact savegame content.</summary>
    public int SkippedUnchanged { get; private set; }

    /// <summary>Columns whose capture reached the durable row and were fingerprinted.</summary>
    public long FingerprintsRecorded { get; private set; }

    /// <summary>True while the whole-world savegame index is still being read.</summary>
    public bool AwaitingSavegameIndex { get; private set; }

    /// <summary>True when existence came from the savegame rather than from engine probes.</summary>
    public bool UsedSavegameIndex { get; private set; }

    /// <summary>Columns the background audit has asked the savegame about.</summary>
    public long AuditsRequested { get; private set; }

    /// <summary>Audited columns whose savegame content no longer matched this cache.</summary>
    public long AuditMismatches { get; private set; }

    /// <summary>Completed rotations through everything this cache claims to hold.</summary>
    public long AuditPasses { get; private set; }

    /// <summary>Dormant map regions woken since startup to reveal new vanilla snapshots.</summary>
    public long SnowProbes { get; private set; }

    public int RefreshCandidates => snowRefresh.Count;
    public int PendingRefreshes => snowRefresh.Pending;
    public long RefreshCampaigns => snowRefresh.Campaigns;
    bool AutomaticSnowRefresh => refreshPerSecond > 0 && weather != null && snowAccumulationEnabled;

    /// <summary>
    /// Loads wait while this many columns are queued for capture or being captured. A swept
    /// column is not kept loaded: the engine unloads it again within seconds, and a column
    /// still queued for capture by then is dropped. Loading at the configured rate regardless
    /// let the queue reach 792 and dropped 4,043 columns in the owner's first 0.4.68 session.
    /// Near the capture worker's own depth, every load is captured before it can unload.
    /// </summary>
    internal const int MaxCaptureBacklogForLoads = 32;

    /// <summary>
    /// Real chunk generation already pending in the engine before the background sweep yields.
    /// Existing saved-column loads do not remain visible in this counter long enough to schedule
    /// against, but yielding at the first request still gives actual generation priority.
    /// </summary>
    internal const int YieldGeneratingChunks = 1;

    /// <summary>
    /// The share of the configured rate a held sweep may still use, as a divisor. A player
    /// hold must slow the sweep, not stop it forever: the hold is renewed by every
    /// chunk-boundary crossing, so above 32 blocks per <see cref="LodPlayerTerrainActivity
    /// .QuietMilliseconds"/> - 6.4 blocks/s - each crossing lands inside the previous
    /// window and an ordinary moving player would suspend the sweep for as long as they
    /// keep walking. Measured 2026-09-19 on a virgin frontier: 880 sections/min ungated,
    /// 110/min held at 5.33 blocks/s, and one whole minute at 7/min at 6.67 blocks/s.
    /// One eighth of the configured rate is 2/s at the default 16/s, close to what the
    /// below-threshold case already delivered at a 4% cost to the 1% low.
    /// </summary>
    internal const int PlayerHoldRateDivisor = 8;

    internal static int FloorPerSecond(int perSecond) =>
        Math.Max(1, Math.Max(1, perSecond) / PlayerHoldRateDivisor);

    /// <summary>
    /// Loads one tick may issue: the rate allowance, held back by engine and capture pressure.
    /// </summary>
    internal static int LoadsAllowed(int allowance, int captureBacklog, int generatingChunks) =>
        generatingChunks >= YieldGeneratingChunks
            ? 0
            : Math.Max(0, Math.Min(allowance, MaxCaptureBacklogForLoads - captureBacklog));

    /// <summary>Columns queued for capture or in the capture worker; see MaxCaptureBacklogForLoads.</summary>
    readonly Func<int> captureBacklog;

    /// <summary>
    /// Whether this column's capture is now in the durable level-0 row. Null leaves the
    /// sweep exactly as it was: nothing is skipped and no fingerprint is ever written.
    /// </summary>
    readonly System.Func<int, int, bool>? columnDurable;

    /// <summary>Records a proven capture. Paired with <see cref="columnDurable"/>.</summary>
    readonly Action<int, int, ulong>? rememberFingerprint;

    /// <summary>The whole-world savegame index, when the read path is available.</summary>
    readonly LodSavegameIndex? savegameIndex;

    /// <summary>Drops a column's stored fingerprint when the audit disproves it.</summary>
    readonly Action<int, int>? forgetFingerprint;

    /// <summary>Columns per second the background audit re-reads. 0 disables it entirely.</summary>
    readonly int auditPerSecond;

    public LodSavegameSweep(ICoreServerAPI sapi, ILogger logger, int radiusChunks, int perSecond,
        int refreshPerSecond, WeatherSystemBase? weather, Action<int, int> queueColumn,
        Func<int> captureBacklog, LodSavegameIndex? savegameIndex = null,
        Dictionary<long, ulong>? capturedFingerprints = null,
        System.Func<int, int, bool>? columnDurable = null,
        Action<int, int, ulong>? rememberFingerprint = null,
        Action<int, int>? forgetFingerprint = null, int auditPerSecond = 0)
    {
        this.forgetFingerprint = forgetFingerprint;
        this.auditPerSecond = Math.Max(0, auditPerSecond);
        this.sapi = sapi;
        this.logger = logger;
        this.radiusChunks = radiusChunks;
        this.perSecond = Math.Max(1, perSecond);
        this.refreshPerSecond = Math.Max(0, refreshPerSecond);
        this.weather = weather;
        this.queueColumn = queueColumn;
        this.captureBacklog = captureBacklog;
        this.savegameIndex = savegameIndex;
        this.columnDurable = columnDurable;
        this.rememberFingerprint = rememberFingerprint;
        ledger = new LodCaptureLedger(capturedFingerprints);
        chunksPerRegion = Math.Max(1, sapi.WorldManager.RegionSize / GlobalConstants.ChunkSize);
        snowAccumulationEnabled = sapi.World.Config.GetBool("snowAccum", true);
    }

    /// <summary>
    /// Waits for the whole-world savegame index instead of probing the engine.
    ///
    /// Nothing else runs while it is pending. Probing in parallel would pay the cost this
    /// exists to remove, and the read either succeeds in tens of seconds or fails almost
    /// immediately - a file that cannot be opened says so at once.
    /// </summary>
    void StepSavegameIndex()
    {
        if (savegameIndex == null)
        {
            AwaitingSavegameIndex = false;
            return;
        }

        if (savegameIndex.TryTake(out LodSavegameIndexResult? indexed) && indexed != null)
        {
            exists = indexed.Exists;
            ledger.SetSavegame(indexed.Fingerprints);
            AwaitingSavegameIndex = false;
            UsedSavegameIndex = true;
            Probing = false;
            reported = 0;

            // The audit checks the claims this cache makes, so its rotation is built once
            // the savegame truth is known. It grows as the sweep proves more columns; a
            // column captured after this is picked up on the next pass.
            auditOrder = ledger.CapturedKeys();
            auditCursor = 0;

            logger.Notification(
                "Savegame sweep: {0} columns hold generated terrain, read from the savegame "
                + "itself rather than by probing the engine. {1} carry a fingerprint this cache "
                + "already matches and will be skipped. Loading the rest with a complete "
                + "neighbourhood.{2}",
                exists.Count, ledger.AlreadyCapturedCount,
                auditPerSecond > 0
                    ? $" Afterwards {auditPerSecond} columns/s are re-read in the background and "
                        + "compared against what this cache proved it captured."
                    : "");
            return;
        }

        if (!savegameIndex.Finished) return;

        // Failed. Everything below this point behaves exactly as it did before the read path
        // existed, which is the point of it being an accelerator.
        AwaitingSavegameIndex = false;
        Probing = true;
        logger.Notification(
            "Savegame sweep: reading the savegame directly was not available, so the engine is "
            + "probed for {0} positions as before.", ProbeTotal);
    }

    /// <summary>
    /// The background audit: re-read a rotating slice of the world from the savegame and
    /// compare it against what this cache proved it captured.
    ///
    /// Continuous watching is not airtight. The owner's 2026-09-17 log shows three columns
    /// that unloaded before the capture queue reached them, and a crash, a capture error or
    /// terrain edited while the mod was disabled does the same. Those failures are silent:
    /// the cache simply holds terrain that is no longer true, and nothing else in the design
    /// would ever notice. This is what notices.
    ///
    /// It yields to exactly the same gates as the load pass, because its recaptures are the
    /// same priority loads. It never generates: a mismatch outside the complete-neighbourhood
    /// rule is left alone, like any frontier column.
    /// </summary>
    void StepAudit(long now)
    {
        if (savegameIndex == null || auditPerSecond <= 0 || !UsedSavegameIndex) return;

        // Collect whatever the read path finished since the last tick first, so a result is
        // never stranded behind a gate that is holding the requests.
        auditScratch.Clear();
        if (savegameIndex.TakeAudited(auditScratch) > 0) ApplyAuditResults();

        int allowance = LoadsAllowed(
            auditAllowance.Available(now, auditPerSecond),
            captureBacklog(), sapi.WorldManager.CurrentGeneratingChunkCount);
        if (playerTerrainActivity.MustYield(now))
        {
            allowance = Math.Min(allowance,
                floorAllowance.Available(now, FloorPerSecond(auditPerSecond)));
        }
        if (allowance <= 0) return;

        if (auditOrder.Count == 0)
        {
            // Nothing proven yet, or the previous pass ended. Rebuilding here is what makes
            // the rotation cover columns this session captured after it was first built.
            auditOrder = ledger.CapturedKeys();
            auditCursor = 0;
            if (auditOrder.Count == 0) return;
        }

        auditRequestScratch.Clear();
        while (auditRequestScratch.Count < allowance && auditCursor < auditOrder.Count)
        {
            auditRequestScratch.Add(auditOrder[auditCursor++]);
        }

        if (auditCursor >= auditOrder.Count)
        {
            // One pass complete. The world keeps changing, so an audit that ran once is a
            // one-off check rather than a safety net: start again, over whatever the cache
            // claims by then.
            AuditPasses++;
            auditOrder = new List<long>();
            auditCursor = 0;
        }

        if (auditRequestScratch.Count == 0) return;
        savegameIndex.RequestAudit(auditRequestScratch);
        auditAllowance.Spend(auditRequestScratch.Count);
        AuditsRequested += auditRequestScratch.Count;
    }

    /// <summary>
    /// Acts on what the audit read back. A column whose savegame content no longer matches
    /// the capture this cache proved is queued for recapture through the ordinary load path,
    /// and its stale fingerprint is dropped so nothing skips it in the meantime.
    /// </summary>
    void ApplyAuditResults()
    {
        foreach (LodAuditedColumn audited in auditScratch)
        {
            if (!ledger.Disagrees(audited.Key, audited.Fingerprint)) continue;

            int cx = LodColumnMap.KeyCx(audited.Key);
            int cz = LodColumnMap.KeyCz(audited.Key);

            // The safety rule is not negotiable for an audit either. A column whose
            // neighbourhood is incomplete would make the engine generate terrain, which is
            // the one thing the sweep promises never to do.
            if (exists.Classify(cx, cz) != EnumColumnAction.Load) continue;

            // Not QueueColumn: that serves a column the world already holds loaded, and an
            // audited column is almost never near a player. The engine has to be asked for
            // it exactly as the load pass asks, and the ordinary load hook then captures it.
            ledger.UpdateSavegame(audited.Key, audited.Fingerprint);
            ledger.Forget(audited.Key);
            forgetFingerprint?.Invoke(cx, cz);
            sapi.WorldManager.LoadChunkColumnPriority(cx, cz);
            ledger.Submitted(audited.Key);
            AuditMismatches++;
        }
        auditScratch.Clear();
    }

    /// <summary>
    /// Writes the fingerprint for every submitted column whose capture has since reached the
    /// durable row. Bounded per tick, and the wait is unbounded on purpose: a column that
    /// never becomes durable is never recorded, so the next start examines it again.
    /// </summary>
    void CommitDurableFingerprints()
    {
        if (columnDurable == null || rememberFingerprint == null || ledger.Pending == 0) return;

        const int MaxCommitsPerTick = 32;
        FingerprintsRecorded += ledger.CommitDurable(
            key => columnDurable(LodColumnMap.KeyCx(key), LodColumnMap.KeyCz(key)),
            MaxCommitsPerTick,
            (key, print) =>
                rememberFingerprint(LodColumnMap.KeyCx(key), LodColumnMap.KeyCz(key), print));
    }

    public void Start()
    {
        active = true;
        spawnCx = (int)sapi.World.DefaultSpawnPosition.X / GlobalConstants.ChunkSize;
        spawnCz = (int)sapi.World.DefaultSpawnPosition.Z / GlobalConstants.ChunkSize;
        ObserveOtherGenerationQueue();

        if (savegameIndex != null)
        {
            AwaitingSavegameIndex = true;
            savegameIndex.Start();
        }

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
        long now = sapi.World.ElapsedMilliseconds;
        ObservePlayerTerrainActivity(now);
        ObserveOtherGenerationQueue();
        CommitDurableFingerprints();
        LodPhaseStart phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        if (Done)
        {
            StepSnowSignals();
            StepRefresh();
            StepAudit(now);
            RefreshIssueCost.Add(phaseStart);
        }
        else if (AwaitingSavegameIndex)
        {
            StepSavegameIndex();
            ProbeIssueCost.Add(phaseStart);
        }
        else if (Probing)
        {
            StepProbe();
            ProbeIssueCost.Add(phaseStart);
        }
        else
        {
            StepLoad(now);
            LoadIssueCost.Add(phaseStart);
        }
    }

    void ObservePlayerTerrainActivity(long now)
    {
        playerTerrainActivity.BeginSample();
        foreach (IPlayer player in sapi.World.AllOnlinePlayers)
        {
            if (player.Entity == null) continue;
            playerTerrainActivity.Observe(player.PlayerUID,
                (int)Math.Floor(player.Entity.Pos.X / GlobalConstants.ChunkSize),
                (int)Math.Floor(player.Entity.Pos.Z / GlobalConstants.ChunkSize), now);
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

    void StepLoad(long now)
    {
        int generatingChunks = sapi.WorldManager.CurrentGeneratingChunkCount;
        LoadEngineQueuePeak = Math.Max(LoadEngineQueuePeak, generatingChunks);
        int allowance = LoadsAllowed(
            loadAllowance.Available(now, perSecond),
            captureBacklog(), generatingChunks);
        if (generatingChunks >= YieldGeneratingChunks) EnginePressureYieldTicks++;

        // A held sweep is slowed to a floor rather than stopped. The engine gate above stays
        // absolute: it means real generation is in flight and there is nothing to be polite
        // about. This one means a player is streaming saved terrain, which the engine counter
        // cannot see (G229), and which a walking player renews indefinitely (G234).
        bool playerHeld = playerTerrainActivity.MustYield(now);
        if (playerHeld)
        {
            PlayerActivityYieldTicks++;
            allowance = Math.Min(allowance,
                floorAllowance.Available(now, FloorPerSecond(perSecond)));
        }

        var tickBudget = new LodWorkBudget(TickWorkBudgetMs);
        int loaded = 0;
        // The rate allowance bounds loads, not decisions. Deciding that a column needs
        // nothing costs a dictionary lookup, and gating that behind the load rate would
        // leave a world where nothing changed still crawling through its spiral one
        // allowance at a time - which is the whole cost this is meant to remove.
        while (loadIndex < LoadTotal && !tickBudget.Expired)
        {
            (int dx, int dz) = LodColumnMap.SpiralAt(loadIndex++);
            int cx = spawnCx + dx;
            int cz = spawnCz + dz;

            long key = LodColumnMap.Key(cx, cz);
            EnumSweepColumnAction action = LodSweepDecision.Decide(
                exists.Classify(cx, cz), ledger.AlreadyCaptured(key));

            // One registration for every outcome that owns real terrain, driven by the rule
            // rather than repeated down each branch. A branch that forgot it would silently
            // stop distant snow maintenance reaching that ground.
            if (LodSweepDecision.RegistersSnowCandidate(action))
                snowRefresh.Add(cx, cz, chunksPerRegion);

            // Not in the savegame: the sweep indexes what exists and creates nothing, so
            // there is no work here. Generation (/vhgen) is the feature that acts on this.
            if (action == EnumSweepColumnAction.Nothing) continue;

            // On the frontier of explored terrain. A load here would generate whatever is
            // missing beside it - the one thing this must not do. Ordinary exploration may
            // fill that neighbourhood later, and its load event then captures it.
            if (action == EnumSweepColumnAction.Frontier)
            {
                SkippedEdge++;
                continue;
            }

            // The savegame holds exactly what this cache already proved it captured, so
            // there is nothing to load. This does not consume the rate allowance: a skip
            // costs a dictionary lookup, not a chunk load.
            if (action == EnumSweepColumnAction.AlreadyCaptured)
            {
                SkippedUnchanged++;
                continue;
            }

            if (loaded >= allowance)
            {
                // This column needs a real load and the rate has none left. Put it back so
                // the next tick starts here rather than past it, and stop: everything after
                // it is further out on the spiral and can wait its turn.
                loadIndex--;
                break;
            }

            // Not KeepLoaded: each column needs to pass through capture once, not stay
            // resident. A radius worth sweeping is far more terrain than fits in memory.
            issuedSweepLoad = true;
            sapi.WorldManager.LoadChunkColumnPriority(cx, cz);
            if (rememberFingerprint != null) ledger.Submitted(key);
            Loaded++;
            loaded++;
        }
        loadAllowance.Spend(loaded);
        // Only a held tick draws on the floor. Leaving it untouched otherwise keeps its own
        // credit capped at one tick's share, so resuming after a long hold cannot burst.
        if (playerHeld)
        {
            floorAllowance.Spend(loaded);
            PlayerHoldFloorLoads += loaded;
        }

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
        // Set when the absence verifier started, and until 2026-09-20 never cleared. Step()
        // begins with `if (!active || verifying) return;`, so every tick after the startup
        // sweep returned immediately and forever: automatic dormant-terrain snow refresh,
        // its canary and its regional campaigns have never run once in any released build
        // (introduced 0596d30, 2026-08-02, and present in the accepted 0.4.64 baseline).
        // Nothing logged it, because the status line reads state rather than work.
        verifying = false;
        Done = true;
        PrimeSnowActivity();
        nextSnowPollMs = sapi.World.ElapsedMilliseconds + SnowPollIntervalMs;

        EnumSweepAbsenceResult absenceResult = ClassifyAbsenceResult(
            verified.Regrown, otherGenerationQueuePeak);
        if (absenceResult == EnumSweepAbsenceResult.Unsafe)
        {
            logger.Warning(
                "Savegame sweep: {0} of {1} sampled positions that did not exist before the "
                + "sweep now exist. The sweep must generate nothing, so worldgen on this "
                + "server reaches further than the measured safe neighbourhood - a worldgen "
                + "mod is the likely cause. Consider SweepSavegame: false here, and please "
                + "report this.", verified.Regrown, verified.Checked);
        }

        string absenceDescription = absenceResult == EnumSweepAbsenceResult.Inconclusive
            ? $"Absence check inconclusive: {verified.Regrown} of {verified.Checked} sampled "
                + "positions that were absent now exist, but the engine's generation/load queue "
                + $"was already active before the sweep issued any loads (peak {otherGenerationQueuePeak})"
            : verified.Describe();
        string generationDescription = absenceResult == EnumSweepAbsenceResult.Inconclusive
            ? "the sweep requested only terrain that already existed"
            : "nothing generated";

        logger.Notification(
            "Savegame sweep finished: {0} columns loaded from terrain that already existed, "
            + "{1} skipped on the frontier, {2}. {3}. Capture continues in the background; "
            + "the cache is complete once no columns remain queued. {4}",
            Loaded, SkippedEdge, generationDescription, absenceDescription,
            AutomaticSnowRefresh
                ? $"Automatic snow refresh is watching {snowRefresh.Count} existing columns "
                    + $"in {snowRefresh.RegionCount} map regions; weather changes create finite "
                    + $"regional campaigns at up to {refreshPerSecond}/s."
                : "Automatic snow refresh is disabled.");

        if (!AutomaticSnowRefresh) Stop();
    }

    /// <summary>
    /// The engine's public count contains both loads and generation requests. It is therefore
    /// independent evidence only until this sweep submits its first load. During the probe pass
    /// the sweep submits database-existence tests, not chunk-column requests, so a non-zero value
    /// then proves some other activity had the engine queue in use. That makes later growth
    /// impossible to attribute to the sweep and turns a warning into an inconclusive result.
    /// </summary>
    void ObserveOtherGenerationQueue()
    {
        if (issuedSweepLoad) return;
        otherGenerationQueuePeak = Math.Max(
            otherGenerationQueuePeak, sapi.WorldManager.CurrentGeneratingChunkCount);
    }

    internal static EnumSweepAbsenceResult ClassifyAbsenceResult(
        int regrown, int otherGenerationQueuePeak)
    {
        if (regrown <= 0) return EnumSweepAbsenceResult.Verified;
        return otherGenerationQueuePeak > 0
            ? EnumSweepAbsenceResult.Inconclusive
            : EnumSweepAbsenceResult.Unsafe;
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

        // The same gates as the load pass, and for the same reason: these are the same
        // engine priority loads, in the same fast lane, competing with the same player
        // terrain. This arm was never gated because it had never run - the bug fixed in
        // Finish meant it could not - so enabling it without these would put an unmeasured
        // ungated load path into the world (G227, G229).
        long now = sapi.World.ElapsedMilliseconds;
        int allowance = LoadsAllowed(
            refreshAllowance.Available(now, refreshPerSecond),
            captureBacklog(), sapi.WorldManager.CurrentGeneratingChunkCount);
        if (playerTerrainActivity.MustYield(now))
        {
            allowance = Math.Min(allowance,
                floorAllowance.Available(now, FloorPerSecond(refreshPerSecond)));
        }
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
    public string Status => AwaitingSavegameIndex
        ? "sweeping savegame: reading the savegame directly instead of probing the engine"
        : Done
        ? AutomaticSnowRefresh
            ? $"savegame sweep complete ({Loaded} columns loaded, {SkippedUnchanged} already "
                + $"captured, {SkippedEdge} skipped on the frontier); "
                + $"snow refresh {Refreshed} recaptures, {snowRefresh.Pending} pending from "
                + $"{snowRefresh.Campaigns} regional campaigns, {SnowProbes} dormant-region "
                + $"probes over {snowRefresh.Count} candidates at {refreshPerSecond}/s; "
                + AuditStatus
            : $"savegame sweep complete ({Loaded} columns loaded, {SkippedUnchanged} already captured, {SkippedEdge} skipped on the frontier); {AuditStatus}"
        : verifying
            ? $"savegame sweep verifying: re-probing {Loaded} loads' surroundings"
        : Probing
            ? $"sweeping savegame: examined {probeIndex}/{ProbeTotal}, {exists.Count} hold terrain"
            : $"sweeping savegame: loaded {Loaded}, {SkippedUnchanged} already captured, {SkippedEdge} skipped on the frontier; "
                + $"held {PlayerActivityYieldTicks} ticks for player movement "
                + $"({PlayerHoldFloorLoads} columns still loaded at the "
                + $"{FloorPerSecond(perSecond)}/s floor) and "
                + $"{EnginePressureYieldTicks} for engine terrain demand";

    /// <summary>One clause for the status line; says plainly when the audit is off.</summary>
    string AuditStatus => auditPerSecond <= 0
        ? "background audit off"
        : !UsedSavegameIndex
            ? "background audit unavailable: the savegame read path is not in use"
            : !Done
            // Deliberate: during first adoption almost nothing is captured yet, so there is
            // little to audit and the load pass is doing the real work. Every later start
            // skips most columns and reaches this point in minutes.
            ? "background audit waiting for the startup sweep to finish"
            : $"background audit {AuditsRequested} columns re-read at {auditPerSecond}/s, "
                + $"{AuditMismatches} no longer matched and were queued for recapture, "
                + $"{AuditPasses} full passes over {auditOrder.Count} claims";

    public void ResetPhaseCosts()
    {
        ProbeIssueCost.Reset();
        ProbePublishCost.Reset();
        LoadIssueCost.Reset();
        RefreshIssueCost.Reset();
    }
}

/// <summary>What the sweep's load pass does at one column.</summary>
internal enum EnumSweepColumnAction
{
    /// <summary>Not in the savegame. The sweep creates nothing, so there is no work.</summary>
    Nothing,

    /// <summary>On the frontier of explored terrain; loading it would generate its neighbours.</summary>
    Frontier,

    /// <summary>Held, and the cache already proved it captured exactly this content.</summary>
    AlreadyCaptured,

    /// <summary>Held, and the cache needs it.</summary>
    Load,
}

/// <summary>
/// The load pass's per-column decision, separated from the sweep so the one invariant that
/// fails silently can be tested without a game process.
///
/// That invariant is snow. The dormant-terrain snow candidate set is built from this arm, so
/// any outcome that leaves a column out of it stops distant snow maintenance ever reaching
/// that ground - no error, no log line, just no snow in the distance months later. Adding a
/// skip outcome is exactly how that regression gets introduced, which is why the rule lives
/// here with a check on it rather than as an ordering inside a loop.
/// </summary>
internal static class LodSweepDecision
{
    public static EnumSweepColumnAction Decide(EnumColumnAction classified, bool alreadyCaptured) =>
        classified switch
        {
            EnumColumnAction.Peek => EnumSweepColumnAction.Nothing,
            EnumColumnAction.SkipFrontier => EnumSweepColumnAction.Frontier,
            _ => alreadyCaptured ? EnumSweepColumnAction.AlreadyCaptured : EnumSweepColumnAction.Load,
        };

    /// <summary>
    /// Whether this outcome must register the column as a snow-refresh candidate.
    ///
    /// Everything the savegame actually holds must, including the frontier columns the sweep
    /// refuses to load - ordinary exploration may fill their missing neighbours later - and
    /// including columns skipped because they are already captured, which are precisely the
    /// ground distant snow has to keep reaching.
    /// </summary>
    public static bool RegistersSnowCandidate(EnumSweepColumnAction action) =>
        action != EnumSweepColumnAction.Nothing;

    /// <summary>Whether this outcome spends a column from the rate allowance.</summary>
    public static bool CostsAllowance(EnumSweepColumnAction action) =>
        action == EnumSweepColumnAction.Load;
}

/// <summary>
/// Decides which columns the sweep may skip, and when a submitted column has earned its
/// fingerprint. Deliberately free of game types so the rule is testable without a game
/// process, for the same reason <see cref="LodColumnMap"/> is: both of its mistakes are
/// silent, and both would be discovered as missing distant terrain months later.
///
/// The two fingerprints it compares are different facts. One is what the savegame holds
/// now; the other is what this cache last *proved* it captured. Skipping needs both, and a
/// column earns the second only when its capture reached the durable row - never when its
/// savegame row was read (G232).
/// </summary>
internal sealed class LodCaptureLedger
{
    readonly Dictionary<long, ulong> captured;
    readonly Dictionary<long, ulong> awaiting = new();
    readonly List<long> scratch = new();
    Dictionary<long, ulong> savegame = new();

    public LodCaptureLedger(Dictionary<long, ulong>? captured = null) =>
        this.captured = captured ?? new Dictionary<long, ulong>();

    /// <summary>Adopt the whole-world savegame index once it has been read.</summary>
    public void SetSavegame(Dictionary<long, ulong> prints) => savegame = prints;

    /// <summary>Columns submitted for capture that have not yet proved durable.</summary>
    public int Pending => awaiting.Count;

    /// <summary>How many columns the savegame holds that this cache has already captured.</summary>
    public int AlreadyCapturedCount
    {
        get
        {
            int matched = 0;
            foreach ((long key, ulong print) in savegame)
            {
                if (captured.TryGetValue(key, out ulong held) && held == print) matched++;
            }
            return matched;
        }
    }

    /// <summary>
    /// True when the cache holds exactly this savegame content. Either fingerprint being
    /// unknown means "assume changed", which is the behaviour that existed before any of
    /// this: an unread savegame or an unproven capture may never cause a skip.
    /// </summary>
    public bool AlreadyCaptured(long key) =>
        savegame.TryGetValue(key, out ulong current)
        && captured.TryGetValue(key, out ulong held)
        && held == current;

    /// <summary>
    /// Remember that a column was submitted for capture at its current savegame content.
    /// Does nothing when the savegame content is unknown, so a column read through the old
    /// probe path is never fingerprinted from thin air.
    /// </summary>
    public void Submitted(long key)
    {
        if (savegame.TryGetValue(key, out ulong print)) awaiting[key] = print;
    }

    /// <summary>
    /// Columns this cache currently claims to hold, in a stable order. These are exactly
    /// the claims an audit exists to check: a column with no claim has nothing to disprove.
    /// </summary>
    public List<long> CapturedKeys()
    {
        var keys = new List<long>(captured.Keys);
        keys.Sort();
        return keys;
    }

    /// <summary>
    /// True when this cache claims a proven capture and the savegame no longer agrees.
    ///
    /// A column with no claim is not a mismatch. It is simply uncaptured, which the ordinary
    /// load pass already handles, and treating it as an audit failure would make the audit
    /// re-report the entire uncaptured world forever.
    /// </summary>
    public bool Disagrees(long key, ulong current) =>
        captured.TryGetValue(key, out ulong held) && held != current;

    /// <summary>Record what the savegame holds now, after the audit re-read it.</summary>
    public void UpdateSavegame(long key, ulong current) => savegame[key] = current;

    /// <summary>
    /// Drop a claim. The column stops being skippable immediately, so nothing relies on the
    /// stale capture while its recapture is in flight.
    /// </summary>
    public void Forget(long key) => captured.Remove(key);

    /// <summary>
    /// Commits up to <paramref name="max"/> submitted columns whose capture is now durable.
    /// A column that never becomes durable is never committed and simply waits; the next
    /// world start examines it again, which is the safe direction.
    /// </summary>
    public int CommitDurable(System.Func<long, bool> durable, int max, Action<long, ulong> write)
    {
        if (awaiting.Count == 0) return 0;

        scratch.Clear();
        foreach ((long key, ulong _) in awaiting)
        {
            if (scratch.Count >= max) break;
            if (durable(key)) scratch.Add(key);
        }

        foreach (long key in scratch)
        {
            ulong print = awaiting[key];
            awaiting.Remove(key);
            captured[key] = print;
            write(key, print);
        }
        return scratch.Count;
    }
}

/// <summary>
/// Tracks terrain-streaming activity without allocating per tick. A new/rejoining player or a
/// chunk-boundary crossing gets a short quiet window in which the background sweep issues no
/// priority loads. Multiple players naturally extend the window whenever any one keeps moving.
/// </summary>
internal sealed class LodPlayerTerrainActivity
{
    public const long QuietMilliseconds = 5_000;

    readonly Dictionary<string, Observation> players = new();
    long sample;
    long lastActivityMilliseconds = long.MinValue;

    public void BeginSample() => sample++;

    public void Observe(string playerUid, int cx, int cz, long nowMilliseconds)
    {
        bool continuouslyPresent = players.TryGetValue(playerUid, out Observation previous)
            && previous.Sample == sample - 1;
        if (!continuouslyPresent || previous.Cx != cx || previous.Cz != cz)
            lastActivityMilliseconds = nowMilliseconds;

        players[playerUid] = new Observation(cx, cz, sample);
    }

    public bool MustYield(long nowMilliseconds)
    {
        if (lastActivityMilliseconds == long.MinValue) return false;
        long elapsed = nowMilliseconds - lastActivityMilliseconds;
        return elapsed >= 0 && elapsed < QuietMilliseconds;
    }

    readonly record struct Observation(int Cx, int Cz, long Sample);
}
