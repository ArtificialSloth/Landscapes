using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace VintageHorizons.Net;

/// <summary>
/// Guarded adapter for the engine's force-loaded-column lease. The public
/// UnloadChunkColumn method disposes a column immediately and does not save it; removing
/// only this lease lets the ordinary ageing path save and unload in the normal order.
/// </summary>
internal sealed class LodKeptColumnReleaser
{
    readonly IWorldManagerAPI worldManager;
    readonly Action<long> remove;

    LodKeptColumnReleaser(IWorldManagerAPI worldManager, Action<long> remove)
    {
        this.worldManager = worldManager;
        this.remove = remove;
    }

    public static bool TryCreate(IWorldManagerAPI worldManager,
        out LodKeptColumnReleaser? releaser, out string reason)
    {
        releaser = null;
        if (!TryBindRelease(worldManager, out Action<long>? remove, out reason)
            || remove == null) return false;
        releaser = new LodKeptColumnReleaser(worldManager, remove);
        return true;
    }

    /// <summary>
    /// The public API returns the engine's private WorldAPI wrapper. The compatible release
    /// method is on ServerMain behind that wrapper in 1.22, while some engine builds expose it
    /// directly. Bind either exact shape; never fall back to UnloadChunkColumn, which disposes
    /// the terrain before the normal save/unload path can persist it.
    /// </summary>
    internal static bool TryBindRelease(object worldManager,
        out Action<long>? remove, out string reason)
    {
        remove = null;
        reason = "";
        try
        {
            object target = worldManager;
            MethodInfo? method = ReleaseMethod(target.GetType());
            if (method == null)
            {
                FieldInfo? serverField = InstanceField(worldManager.GetType(), "server");
                object? server = serverField?.GetValue(worldManager);
                if (server != null)
                {
                    target = server;
                    method = ReleaseMethod(server.GetType());
                }
            }
            if (method == null || method.ReturnType != typeof(void))
            {
                reason = "this engine build's world-manager wrapper has no compatible "
                    + "kept-column release method";
                return false;
            }

            remove = (Action<long>)method.CreateDelegate(typeof(Action<long>), target);
            return true;
        }
        catch (Exception e)
        {
            reason = "the kept-column release method could not be bound: " + e.Message;
            return false;
        }
    }

    static MethodInfo? ReleaseMethod(Type type) => type.GetMethod(
        "RemoveChunkColumnFromForceLoadedList",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null, types: new[] { typeof(long) }, modifiers: null);

    static FieldInfo? InstanceField(Type? type, string name)
    {
        while (type != null)
        {
            FieldInfo? field = type.GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.DeclaredOnly);
            if (field != null) return field;
            type = type.BaseType;
        }
        return null;
    }

    public bool TryRelease(int cx, int cz, out string reason)
    {
        try
        {
            remove(worldManager.MapChunkIndex2D(cx, cz));
            reason = "";
            return true;
        }
        catch (Exception e)
        {
            reason = e.Message;
            return false;
        }
    }
}

/// <summary>Atomic persistence for the resumable job cursor.</summary>
internal sealed class LodWorldGenStateStore
{
    public string Path { get; }

    public LodWorldGenStateStore(string path) => Path = path;

    public bool Exists => File.Exists(Path);

    public bool TryLoad(out LodWorldGenState? state, out string reason)
    {
        state = null;
        reason = "";
        try
        {
            if (!File.Exists(Path)) return true;
            state = LodWorldGenState.Deserialize(File.ReadAllText(Path));
            if (state != null) return true;
            reason = "the state file is not valid VintageHorizons world-generation JSON";
            return false;
        }
        catch (Exception e)
        {
            reason = e.Message;
            return false;
        }
    }

    public bool TrySave(LodWorldGenState state, out string reason)
    {
        string temporary = Path + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(temporary, LodWorldGenState.Serialize(state));
            File.Move(temporary, Path, overwrite: true);
            reason = "";
            return true;
        }
        catch (Exception e)
        {
            reason = e.Message;
            return false;
        }
    }

    public bool TryDelete(out string reason)
    {
        try
        {
            if (File.Exists(Path)) File.Delete(Path);
            string temporary = Path + ".tmp";
            if (File.Exists(temporary)) File.Delete(temporary);
            reason = "";
            return true;
        }
        catch (Exception e)
        {
            reason = e.Message;
            return false;
        }
    }
}

/// <summary>
/// Generates real, saved terrain in a bounded tile plan and holds every issued column
/// until the LOD capture result lands. The host drives <see cref="Tick"/> from the same
/// owning-thread tick as the pipeline and forwards capture/persistence notifications.
/// </summary>
internal readonly record struct LodWorldGenProgress(
    int DoneColumns, int TotalColumns, int Percent, double ColumnsPerSecond,
    long EtaSeconds, long SavegameBytes, long CacheBytes, string Gate,
    int EngineQueue, int QueueTarget, int InFlight, int Retries, int Failures);

internal sealed class LodWorldGenJob
{
    public const int EstimatedSavegameBytesPerColumn = 50 * 1024;
    public const int EstimatedCacheBytesPerColumn = 21 * 1024;
    public const int EstimatedTotalBytesPerColumn =
        EstimatedSavegameBytesPerColumn + EstimatedCacheBytesPerColumn;
    public const double BaselineColumnsPerSecond = 9.5;

    const long ColumnTimeoutMs = 300_000;
    const int MaxAttempts = 2;
    const int MaxIssuesPerTick = 16;
    const double TickIssueBudgetMs = 1.0;

    readonly ICoreServerAPI sapi;
    readonly ILogger logger;
    readonly LodPipeline pipeline;
    readonly LodWorldGenPlan plan;
    readonly LodWorldGenState state;
    readonly LodWorldGenStateStore store;
    readonly LodKeptColumnReleaser releaser;
    readonly LodServerConfig config;
    readonly LodWorldGenLeases leases = new();
    readonly HashSet<int> capturedThisRun = new();
    readonly HashSet<int> failedThisRun = new();
    readonly Queue<long> completedAtMs = new();
    readonly string savegamePath;

    int scanIndex;
    int currentTile = -1;
    int doneCount;
    int persistedCheckpointPercent;
    int nextLogPercent = 5;
    int failures;
    int retries;
    bool stopRequested;
    long nextReleaseRetryMs;
    readonly long rateWindowStartedMs = Environment.TickCount64;
    string faultReason = "";

    public bool Paused { get; private set; }
    public bool Done { get; private set; }
    public bool Faulted => faultReason.Length > 0;
    public EnumWorldGenGate Gate { get; private set; }
    public int InFlight => leases.Count;
    public int DoneColumns => doneCount;
    public int TotalColumns => plan.Count;
    public int Failures => failures;
    public string StartedBy => state.StartedBy;
    public LodWorldGenState State => state;

    public LodWorldGenJob(ICoreServerAPI sapi, ILogger logger, LodPipeline pipeline,
        LodWorldGenPlan plan, LodWorldGenState state, string statePath,
        LodKeptColumnReleaser releaser, LodServerConfig config)
    {
        this.sapi = sapi;
        this.logger = logger;
        this.pipeline = pipeline;
        this.plan = plan;
        this.state = state;
        store = new LodWorldGenStateStore(statePath);
        this.releaser = releaser;
        this.config = config;
        savegamePath = sapi.WorldManager.CurrentWorldFilepath;

        // Only this job's own durable cursor may omit an unfinished plan column. A server
        // cache row can come from /vhgen or a frontier peek and therefore does not prove the
        // real savegame contains that terrain. Every other column takes the engine's real
        // load/generate path; an unchanged authoritative row completes after capture below.
        doneCount = state.DoneCount();
        persistedCheckpointPercent = Percent;
        nextLogPercent = Math.Min(100, (Percent / 5 + 1) * 5);
    }

    int Percent => doneCount * 100 / Math.Max(1, plan.Count);

    public void Start(bool resumed)
    {
        SaveState("start");
        logger.Notification(
            "Savegame world generation {0} by {1}: {2} {3} columns around chunk {4},{5}, "
            + "radius {6}, in {7} nearest-first 16x16 tiles. Queue target {8}, kept-column "
            + "limit {9}. This permanently adds terrain to the world.",
            resumed ? "resumed" : "started", StartedBy, plan.Count,
            state.Shape.ToString().ToLowerInvariant(), state.CentreCx, state.CentreCz,
            state.RadiusChunks, plan.Tiles.Count, config.WorldGenQueueTarget,
            config.WorldGenMaxInFlight);
    }

    public void Tick(bool serverOverloaded)
    {
        if (Done) return;
        long now = Environment.TickCount64;
        TrimRateWindow(now);

        if (stopRequested)
        {
            Gate = EnumWorldGenGate.Paused;
            if (now >= nextReleaseRetryMs)
            {
                TryReleaseAll("stop");
                nextReleaseRetryMs = now + 1000;
            }
            if (leases.Count == 0) FinishStopped();
            return;
        }

        ExpireColumns(now);
        if (Faulted)
        {
            Gate = EnumWorldGenGate.Faulted;
            return;
        }

        int next = FindNextPending();
        if (next >= plan.Count)
        {
            EnumWorldGenGate finalGate = BoundaryGate(
                final: true, inFlight: leases.Count,
                capturedAwaitingPersistence: capturedThisRun.Count);
            if (finalGate != EnumWorldGenGate.None)
            {
                if (leases.Count == 0) pipeline.RequestPersistenceCheckpoint();
                Gate = finalGate;
                return;
            }
            FinishComplete();
            return;
        }

        int nextTile = plan.Columns[next].Tile;
        if (currentTile < 0) currentTile = nextTile;
        if (nextTile != currentTile)
        {
            int tileUndurable = capturedThisRun.Count(index =>
                plan.Columns[index].Tile == currentTile);
            EnumWorldGenGate boundaryGate = BoundaryGate(
                final: false, inFlight: leases.Count,
                capturedAwaitingPersistence: tileUndurable);
            if (boundaryGate != EnumWorldGenGate.None)
            {
                if (leases.Count == 0) pipeline.RequestPersistenceCheckpoint();
                Gate = boundaryGate;
                return;
            }
            SaveState("tile boundary");
            currentTile = nextTile;
        }

        Gate = LodWorldGenGates.Select(Paused, Faulted, serverOverloaded,
            pipeline.CaptureBacklogFull, pipeline.DirtySections,
            config.WorldGenDirtySectionLimit, pipeline.OldestDirtyAgeMs,
            config.WorldGenOldestDirtySeconds * 1000L,
            sapi.WorldManager.CurrentGeneratingChunkCount, config.WorldGenQueueTarget,
            leases.Count, config.WorldGenMaxInFlight);
        if (Gate != EnumWorldGenGate.None) return;

        var budget = Stopwatch.StartNew();
        int issued = 0;
        while (issued < MaxIssuesPerTick && budget.Elapsed.TotalMilliseconds < TickIssueBudgetMs)
        {
            next = FindNextPending();
            if (next >= plan.Count || plan.Columns[next].Tile != currentTile) break;
            Gate = LodWorldGenGates.Select(Paused, Faulted, serverOverloaded,
                pipeline.CaptureBacklogFull, pipeline.DirtySections,
                config.WorldGenDirtySectionLimit, pipeline.OldestDirtyAgeMs,
                config.WorldGenOldestDirtySeconds * 1000L,
                sapi.WorldManager.CurrentGeneratingChunkCount, config.WorldGenQueueTarget,
                leases.Count, config.WorldGenMaxInFlight);
            if (Gate != EnumWorldGenGate.None) break;

            LodWorldGenColumn column = plan.Columns[next];
            scanIndex = next + 1;
            if (!leases.TryBegin(next, column.Cx, column.Cz, now)) continue;
            if (!TryIssue(column.Cx, column.Cz)) return;
            issued++;
        }
    }

    /// <summary>
    /// A tile boundary is owned by this job, not by every producer sharing the pipeline.
    /// Sweep, snow refresh, frontier capture, and another player's loaded terrain can keep
    /// the global capture/dirty counters non-zero indefinitely. The job waits only for its
    /// kept columns and its own captured L0 rows to become durable; ordinary backlog gates
    /// still brake the next tile while the rest of the pyramid catches up.
    /// </summary>
    internal static EnumWorldGenGate BoundaryGate(
        bool final, int inFlight, int capturedAwaitingPersistence)
    {
        if (inFlight > 0)
            return final ? EnumWorldGenGate.InFlight : EnumWorldGenGate.TilePyramid;
        if (capturedAwaitingPersistence > 0)
            return final ? EnumWorldGenGate.FinalPersistence : EnumWorldGenGate.TilePyramid;
        return EnumWorldGenGate.None;
    }

    int FindNextPending()
    {
        while (scanIndex < plan.Count)
        {
            if (!state.IsDone(scanIndex)
                && !capturedThisRun.Contains(scanIndex)
                && !failedThisRun.Contains(scanIndex)) return scanIndex;
            scanIndex++;
        }
        return scanIndex;
    }

    bool TryIssue(int cx, int cz)
    {
        try
        {
            sapi.WorldManager.LoadChunkColumn(cx, cz, keepLoaded: true);

            // LoadChunkColumn is a no-op for a fully resident column and then emits no
            // ChunkColumnLoaded event. Queueing is idempotent and covers that path.
            if (IsColumnFullyLoaded(cx, cz))
            {
                ObserveLoaded(cx, cz);
                pipeline.QueueColumn(cx, cz);
            }
            return true;
        }
        catch (Exception e)
        {
            if (releaser.TryRelease(cx, cz, out string releaseError)) leases.Remove(cx, cz);
            else SetFault($"load failed at {cx},{cz}, then its lease could not be released: {releaseError}");
            failures++;
            logger.Warning("Savegame world generation could not issue column {0},{1}: {2}",
                cx, cz, e.Message);
            return !Faulted;
        }
    }

    bool IsColumnFullyLoaded(int cx, int cz)
    {
        if (sapi.WorldManager.GetMapChunk(cx, cz)?.RainHeightMap == null) return false;
        int chunksY = (sapi.WorldManager.MapSizeY + sapi.WorldManager.ChunkSize - 1)
            / sapi.WorldManager.ChunkSize;
        for (int cy = 0; cy < chunksY; cy++)
        {
            if (sapi.WorldManager.GetChunk(cx, cy, cz) == null) return false;
        }
        return true;
    }

    /// <summary>Called by the engine only once this job's requested column is complete.</summary>
    public void ObserveLoaded(int cx, int cz)
    {
        if (leases.TryGet(cx, cz, out LodWorldGenLeases.Lease? lease) && lease != null)
            lease.Loaded = true;
    }

    /// <summary>Called after a worker capture has been applied on the owning thread.</summary>
    public void ObserveCaptured(int cx, int cz)
    {
        if (!leases.TryGet(cx, cz, out LodWorldGenLeases.Lease? lease) || lease == null) return;
        // A capture already queued before this request cannot complete its obligation.
        // Wait for ChunkColumnLoaded (or the fully-resident check) and its fresh capture.
        if (!lease.Loaded) return;
        lease.Captured = true;
        capturedThisRun.Add(lease.Index);
        if (releaser.TryRelease(cx, cz, out string reason))
        {
            leases.Remove(cx, cz);
            // If applying this authoritative capture changed nothing, the existing local row
            // is already the newest durable answer and no save completion will be emitted.
            if (pipeline.IsAuthoritativeChunkDurable(cx, cz))
                MarkDurable(lease.Index, Environment.TickCount64);
            else if (!pipeline.IsChunkAwaitingPersistence(cx, cz))
                SetFault($"captured column {cx},{cz} produced no complete authoritative "
                    + "distant-terrain row and no persistence obligation");
        }
        else
        {
            SetFault($"captured column {cx},{cz} could not release its kept-column lease: {reason}");
        }
    }

    /// <summary>Only durable L0 rows advance the restart bitmap.</summary>
    public void ObservePersisted(long key)
    {
        if (LodWorld.KeyLevel(key) != 0) return;
        long now = Environment.TickCount64;
        // One level-zero row covers 2x2 engine chunks. Credit only chunks this run captured
        // before this newest-row durability acknowledgement; the callback deliberately does
        // not guess one chunk from the section coordinate.
        int[] durable = capturedThisRun
            .Where(index =>
            {
                LodWorldGenColumn column = plan.Columns[index];
                return LodWorldGenSections.ContainsChunk(key, column.Cx, column.Cz);
            })
            .ToArray();
        foreach (int index in durable) MarkDurable(index, now);
    }

    void MarkDurable(int index, long now)
    {
        if (!state.MarkDone(index)) return;
        capturedThisRun.Remove(index);
        failedThisRun.Remove(index);
        doneCount++;
        completedAtMs.Enqueue(now);
        TrimRateWindow(now);

        int percent = Percent;
        if (percent >= nextLogPercent)
        {
            logger.Notification("{0}", Status);
            nextLogPercent = Math.Min(100, (percent / 5 + 1) * 5);
        }
        if (percent >= persistedCheckpointPercent + 5)
        {
            SaveState("five-percent checkpoint");
        }
    }

    void ExpireColumns(long now)
    {
        foreach (LodWorldGenLeases.Lease lease in leases.Expired(now, ColumnTimeoutMs))
        {
            if (lease.Attempts < MaxAttempts)
            {
                lease.Attempts++;
                lease.IssuedAtMs = now;
                retries++;
                logger.Warning("Savegame world generation timed out at {0},{1}; retrying once.",
                    lease.Cx, lease.Cz);
                if (!TryIssue(lease.Cx, lease.Cz)) return;
                continue;
            }

            failures++;
            failedThisRun.Add(lease.Index);
            if (releaser.TryRelease(lease.Cx, lease.Cz, out string reason))
            {
                leases.Remove(lease.Cx, lease.Cz);
                logger.Warning("Savegame world generation gave up column {0},{1} after two "
                    + "five-minute waits. Resume the saved job to retry it.", lease.Cx, lease.Cz);
            }
            else
            {
                SetFault($"timed-out column {lease.Cx},{lease.Cz} could not release its "
                    + $"kept-column lease: {reason}");
                return;
            }
        }
    }

    public void Pause()
    {
        if (Done || Paused) return;
        Paused = true;
        SaveState("pause");
        logger.Notification("Savegame world generation paused. In-flight columns may finish; "
            + "no new columns will be issued until resume.");
    }

    public void Resume()
    {
        if (Done || !Paused || stopRequested) return;
        Paused = false;
        logger.Notification("Savegame world generation resumed.");
    }

    public void Checkpoint()
    {
        if (!Done && !stopRequested) SaveState("world save");
    }

    public void Stop()
    {
        if (Done || stopRequested) return;
        stopRequested = true;
        Paused = true;
        if (!store.TryDelete(out string reason))
            logger.Warning("Could not delete stopped world-generation state {0}: {1}", store.Path, reason);
        TryReleaseAll("stop");
        if (leases.Count == 0) FinishStopped();
    }

    /// <summary>Preserve durable progress, then release every lease before pipeline teardown.</summary>
    public void Shutdown()
    {
        if (!Done && !stopRequested) SaveState("shutdown");
        TryReleaseAll("shutdown");
        if (leases.Count > 0)
        {
            logger.Error("Savegame world generation shutdown still holds {0} kept columns. "
                + "The engine rejected their safe lease release; no immediate unload was attempted.",
                leases.Count);
        }
    }

    void TryReleaseAll(string context)
    {
        int before = leases.Count;
        int released = leases.ReleaseAll((cx, cz) =>
            releaser.TryRelease(cx, cz, out _));
        if (before - released > 0)
        {
            SetFault($"{before - released} kept-column leases could not be released during {context}");
        }
    }

    void FinishStopped()
    {
        if (Done) return;
        Done = true;
        logger.Notification("Savegame world generation stopped after {0}/{1} durable columns; "
            + "all kept-column leases were released. Existing generated terrain remains in the world.",
            doneCount, plan.Count);
    }

    void FinishComplete()
    {
        Done = true;
        Gate = EnumWorldGenGate.None;
        if (failures == 0)
        {
            if (!store.TryDelete(out string reason))
                logger.Warning("Could not delete completed world-generation state {0}: {1}",
                    store.Path, reason);
        }
        else
        {
            SaveState("completion with failures");
        }
        logger.Notification("Savegame world generation finished: {0}/{1} durable columns, "
            + "{2} retries, {3} failures, and zero kept-column leases remain. {4}",
            doneCount, plan.Count, retries, failures,
            failures == 0 ? "The resume state was removed." : "Resume will retry the failures.");
    }

    void SetFault(string reason)
    {
        if (faultReason.Length > 0) return;
        faultReason = reason;
        logger.Error("Savegame world generation safety stop: {0}. Use /vhworldgen stop "
            + "to retry releasing every kept column.", reason);
    }

    void SaveState(string context)
    {
        if (store.TrySave(state, out string reason))
        {
            persistedCheckpointPercent = Percent;
            return;
        }
        SetFault($"could not save resumable state at {context}: {reason}");
    }

    void TrimRateWindow(long now)
    {
        while (completedAtMs.Count > 0 && now - completedAtMs.Peek() > 60_000)
            completedAtMs.Dequeue();
    }

    static long SafeLength(string? path)
    {
        try { return !string.IsNullOrEmpty(path) && File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    public string Status
    {
        get
        {
            LodWorldGenProgress progress = Snapshot();
            string eta = progress.EtaSeconds >= 0
                ? FormatDuration(progress.EtaSeconds)
                : "measuring";
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[VintageHorizons] World generation: {0}/{1} columns ({2}%), {3:0.0}/s over "
                + "the last minute, ETA {4}; disk +{5:0.0} MiB savegame, +{6:0.0} MiB cache; "
                + "{7}; engine queue {8}/{9}, {10} kept, {11} retries, {12} failures.",
                progress.DoneColumns, progress.TotalColumns, progress.Percent,
                progress.ColumnsPerSecond, eta,
                progress.SavegameBytes / (1024.0 * 1024.0),
                progress.CacheBytes / (1024.0 * 1024.0), progress.Gate,
                progress.EngineQueue, progress.QueueTarget, progress.InFlight,
                progress.Retries, progress.Failures);
        }
    }

    /// <summary>Owning-thread status snapshot for the command and network page.</summary>
    public LodWorldGenProgress Snapshot()
    {
        long now = Environment.TickCount64;
        TrimRateWindow(now);
        double rateSeconds = Math.Clamp((now - rateWindowStartedMs) / 1000.0, 1.0, 60.0);
        double rate = completedAtMs.Count / rateSeconds;
        int remaining = Math.Max(0, plan.Count - doneCount);
        long etaSeconds = rate > 0.01 ? (long)Math.Ceiling(remaining / rate) : -1;
        long saveGrowth = Math.Max(0, SafeLength(savegamePath) - state.SavegameBytesAtStart);
        long cacheGrowth = Math.Max(0, SafeLength(pipeline.DbPath) - state.CacheBytesAtStart);
        string gate = Faulted ? faultReason : LodWorldGenGates.Describe(Gate);
        return new LodWorldGenProgress(doneCount, plan.Count, Percent, rate, etaSeconds,
            saveGrowth, cacheGrowth, gate, sapi.WorldManager.CurrentGeneratingChunkCount,
            config.WorldGenQueueTarget, leases.Count, retries, failures);
    }

    static string FormatDuration(long seconds)
    {
        if (seconds < 120) return seconds + " seconds";
        if (seconds < 7200) return (seconds + 30) / 60 + " minutes";
        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:0.0} hours", seconds / 3600.0);
    }

    public static string StatePath(ICoreServerAPI sapi)
    {
        string worldKey = sapi.World.SavegameIdentifier;
        if (string.IsNullOrEmpty(worldKey)) worldKey = "seed-" + sapi.World.Seed;
        worldKey = Regex.Replace(worldKey, "[^A-Za-z0-9_-]", "_");
        return System.IO.Path.Combine(sapi.GetOrCreateDataPath("ModData/vintagehorizons"),
            worldKey + "-worldgen.json");
    }

    public static long FileLength(string? path) => SafeLength(path);
}
