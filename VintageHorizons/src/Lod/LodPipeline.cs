using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace VintageHorizons;

/// <summary>
/// Colour and tint slot for a captured block. The only part of capture that is not
/// side-agnostic: block colour comes from <c>capi.BlockTextureAtlas</c>, which a
/// dedicated server does not have. See DESIGN.md §10.4.
/// </summary>
/// <param name="blockId">Live block id from the capture.</param>
/// <param name="cx">Chunk column X, for sampling position.</param>
/// <param name="cz">Chunk column Z, for sampling position.</param>
/// <param name="sampleY">Y of the run's top, for sampling position.</param>
public delegate (int Color, byte TintSlot) LodPaletteDescriber(int blockId, int blockX, int blockY, int blockZ);

/// <summary>Which live tint applies to a block. The server has none and answers 0.</summary>
public delegate byte LodTintSlotResolver(Block block);

/// <summary>
/// The colour a stored palette entry should hold now, or 0 for a block whose colour
/// genuinely depends on where it stands and must keep whatever was captured. Sections
/// already on disk were written before the colour became section-independent, so they
/// are corrected as they load rather than discarded - see G46.
/// </summary>
public delegate int LodStableColorResolver(Block block);

/// <summary>
/// The block's slot in the texture table, or 0 for none. Client-side only: it reads the block
/// texture atlas, which a server does not have. Resolved on load like the tint slot and never
/// persisted, so an existing cache gains textures with no re-exploration.
/// </summary>
public delegate ushort LodTextureSlotResolver(Block block);

/// <summary>
/// Bounded owning-thread repair for resident palettes created while block textures were off.
/// New capture/load work resolves slots directly once the feature is enabled; this queue covers
/// only the sections that were already resident at that transition.
/// </summary>
public sealed class LodTextureSlotBackfill
{
    readonly Queue<long> pending = new();
    long currentKey;
    int nextPaletteIndex;
    bool hasCurrent;

    public bool Complete { get; private set; } = true;
    public int PendingSections => pending.Count + (hasCurrent ? 1 : 0);

    public void Begin(IEnumerable<long> keys)
    {
        pending.Clear();
        foreach (long key in keys) pending.Enqueue(key);
        currentKey = 0;
        nextPaletteIndex = 0;
        hasCurrent = false;
        Complete = pending.Count == 0;
    }

    public void Cancel()
    {
        pending.Clear();
        currentKey = 0;
        nextPaletteIndex = 0;
        hasCurrent = false;
        Complete = true;
    }

    /// <summary>
    /// Resolve at most <paramref name="maxEntries"/> palette entries and stop at an entry
    /// boundary after the elapsed allowance. One first entry always progresses.
    /// </summary>
    public int Pump(IReadOnlyDictionary<long, LodSection> sections,
        System.Func<int, ushort> resolve, int maxEntries, double maxMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(resolve);
        if (maxEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));

        if (Complete) return 0;
        long started = Stopwatch.GetTimestamp();
        int examined = 0;

        while (examined < maxEntries)
        {
            if (!hasCurrent)
            {
                if (!pending.TryDequeue(out currentKey))
                {
                    Complete = true;
                    break;
                }
                nextPaletteIndex = 0;
                hasCurrent = true;
            }

            if (!sections.TryGetValue(currentKey, out LodSection? section))
            {
                hasCurrent = false;
                continue;
            }

            if (nextPaletteIndex >= section.Palette.Count)
            {
                hasCurrent = false;
                continue;
            }

            LodPaletteEntry entry = section.Palette[nextPaletteIndex];
            if (entry.BlockId > 0 && entry.TextureSlot == 0)
            {
                entry.TextureSlot = resolve(entry.BlockId);
                section.Palette[nextPaletteIndex] = entry;
            }
            nextPaletteIndex++;
            examined++;

            if (examined > 0 && maxMilliseconds >= 0
                && (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency
                    >= maxMilliseconds)
            {
                break;
            }
        }

        if (hasCurrent && sections.TryGetValue(currentKey, out LodSection? current)
            && nextPaletteIndex >= current.Palette.Count)
        {
            hasCurrent = false;
        }
        if (!hasCurrent && pending.Count == 0) Complete = true;
        return examined;
    }
}

public enum LodForeignQueueOutcome
{
    Queued,
    Retryable,
    Unavailable,
}

/// <param name="Retryable">
/// Not installed, but not refused either: a merge whose target had left RAM. The source's
/// offer stays open and the pipeline asks again.
/// </param>
public readonly record struct LodForeignInstallCompletion(
    long Key, LodForeignSource Source, bool Installed, bool Retryable = false);

/// <summary>
/// Everything between "a chunk column arrived" and "a section is on disk": capture
/// scheduling, palette registration, mip propagation and persistence. Owns all mutation
/// of the <see cref="LodWorld"/>; the worker thread only reads immutable snapshots.
///
/// Side-agnostic on purpose. The client drives it from `ChunkDirty` and also renders from
/// the same LodWorld; a server drives it from `ChunkColumnLoaded` and never renders. What
/// differs between them is which chunks arrive and what a palette entry's colour is, so
/// those are the two things injected rather than branched on - a copy of this per side
/// would drift, and the mip and persistence rules are exactly what must not.
///
/// Every method here must be called from the thread that owns the world (the game tick).
/// </summary>
public class LodPipeline
{
    const int CaptureSchedulesPerTick = 8;
    const int CaptureAppliesPerTick = 8;
    const int PropagationsPerTick = 3;
    const int MaxMipBacklog = 12;
    internal const int PersistenceSnapshotsPerTick = 8;
    internal const long PersistenceCheckpointIntervalMs = 30_000;
    const int MaxWorkerCaptureBacklog = 24;
    const int TextureSlotBackfillEntriesPerTick = 256;
    const double TextureSlotBackfillMaxMillisecondsPerTick = 1.0;
    const int ChunkSize = GlobalConstants.ChunkSize;

    /// <summary>
    /// Queued snapshots hold copies of their section's run data, so an unbounded queue
    /// is an unbounded memory leak if the disk can't keep up. Past this depth the
    /// sections simply stay dirty (and therefore RAM-resident) and retry later.
    /// </summary>
    internal const int MaxStorageBacklog = 256;
    const int StorageShutdownTimeoutMs = 15000;

    readonly ICoreAPI api;
    readonly ILogger logger;
    readonly LodPaletteDescriber describePalette;

    /// <summary>Tint slot for a block; the server has no tints and leaves it 0.</summary>
    readonly LodTintSlotResolver tintSlotFor;

    /// <summary>Section-independent colour for a block; the server has no atlas and leaves it 0.</summary>
    readonly LodStableColorResolver stableColorFor;
    readonly LodTextureSlotResolver textureSlotFor;
    readonly LodTextureSlotBackfill textureSlotBackfill = new();

    public bool TextureSlotsEnabled { get; private set; }
    public bool TextureSlotsPrepared => TextureSlotsEnabled && textureSlotBackfill.Complete;
    public int PendingTextureSlotBackfillSections => textureSlotBackfill.PendingSections;

    public LodWorld World { get; }
    public LodWorker Worker { get; }

    LodStore? store;
    LodStorageThread? storageThread;

    /// <summary>Block codes the registry never answered for; empty is the norm.</summary>
    public string[] UnresolvedBlockCodes() => store?.UnresolvedCodes() ?? Array.Empty<string>();

    /// <summary>
    /// Colours palette entries that were saved without one. Set by the client, which has
    /// the texture atlas; a server leaves it null, because storing 0 is what a server is
    /// supposed to do. See LodPaletteRepair for why an existing cache needs this.
    /// </summary>
    public System.Func<LodSection, int>? RepairUncoloredPalette;

    /// <summary>
    /// Gives server-authored palette entries client atlas colours after their structural
    /// decode and live block resolution. Client-only; a dedicated server leaves it null.
    /// </summary>
    public Action<LodSection>? RecolorForeignSection;

    /// <summary>Palette entries given a colour on load because the cache had none.</summary>
    public int PaletteEntriesRepaired { get; private set; }

    /// <summary>
    /// Chiselled blocks captured as their most visible material, and chisels whose contents
    /// were read but held no material that can stand in. See LodChiselStandIn.
    /// </summary>
    public long ChiselStandIns { get; private set; }
    public long ChiselsWithoutMaterial { get; private set; }

    /// <summary>Columns whose chisel contents could not be copied; they capture as before.</summary>
    public int ChiselCollectErrors => Volatile.Read(ref chiselCollectErrors);
    int chiselCollectErrors;

    readonly ConcurrentDictionary<long, byte> queuedColumns = new();
    readonly ConcurrentQueue<long> pendingColumns = new();
    readonly BlockPos paletteSamplePos = new(0, 0, 0);

    /// <summary>
    /// False until the store exists. Nothing may touch sections before then: applying a
    /// capture to a freshly-created empty section would shadow (and later overwrite) the
    /// stored row. The column queue holds work until it flips.
    /// </summary>
    public bool Active { get; private set; }

    public bool Persisting => store != null;
    public int CachedSectionsLoaded { get; private set; }
    public int ColumnsCaptured { get; private set; }
    public int PendingColumns => pendingColumns.Count;
    public int CaptureBacklogTotal => PendingColumns + CaptureBacklog;
    public long CaptureDropsUnloaded { get; private set; }
    public int CaptureErrors => Volatile.Read(ref Worker.CaptureErrors);
    public int DirtySections => World.SaveDirty.Count;
    public long OldestDirtyAgeMs => World.SaveDirty.OldestAgeMilliseconds;
    public long SectionsSaved => storageThread == null
        ? 0
        : Math.Max(0, Interlocked.Read(ref storageThread.SectionsWritten));
    public double SavesPerSecond
    {
        get
        {
            long elapsedMs = Math.Max(0, Environment.TickCount64 - persistenceStartedMs);
            return persistenceStartedMs == 0 || elapsedMs == 0
                ? 0
                : SectionsSaved * 1000.0 / elapsedMs;
        }
    }
    public string? DbPath { get; private set; }

    // Main-thread storage cost, measured to decide whether moving SQLite work to a
    // background thread is worth its complexity.
    readonly System.Diagnostics.Stopwatch storageClock = new();
    public double SaveMsMax { get; private set; }
    public double SaveMsTotal { get; private set; }
    public int SaveCalls { get; private set; }
    public double LoadMsMax { get; private set; }
    public double LoadMsTotal { get; private set; }
    public int LoadCalls { get; private set; }
    public LodStorageThread? StorageThread => storageThread;

    /// <summary>Owning-thread pipeline phases since the last telemetry report.</summary>
    public LodPhaseCost LoadInstallCost, ForeignInstallCost, CaptureScheduleCost, CaptureApplyCost,
        MipApplyCost, MipScheduleCost, SaveSnapshotCost;
    public bool TrackPhaseAllocations { get; set; }

    /// <summary>Completed background-load publications since the last telemetry reset.</summary>
    public int LoadInstallItems { get; private set; }
    public long LoadInstallBytes { get; private set; }
    public int PendingLoadResults => storageThread?.PendingLoadResults ?? 0;
    public long PendingLoadResultBytes => storageThread?.PendingLoadResultBytes ?? 0;
    public long OldestLoadResultAgeMs => storageThread?.OldestLoadResultAgeMs ?? 0;
    public int ForeignInstallItems { get; private set; }
    public long ForeignInstallBytes { get; private set; }
    public int PendingForeignResults => storageThread?.PendingForeignResults ?? 0;
    public long PendingForeignResultBytes => storageThread?.PendingForeignResultBytes ?? 0;
    public long OldestForeignResultAgeMs => storageThread?.OldestForeignResultAgeMs ?? 0;
    public int CaptureApplyItems { get; private set; }
    public long CaptureApplyBytes { get; private set; }
    public int PendingCaptureResults => Worker.PendingCaptureResults + deferredCaptures.Count;
    public long PendingCaptureResultBytes => Worker.PendingCaptureResultBytes
        + deferredCaptures.Sum(result => result.EstimatedBytes);
    public long OldestCaptureResultAgeMs => Math.Max(Worker.OldestCaptureResultAgeMs,
        deferredCaptures.Count > 0
            ? Math.Max(0, Environment.TickCount64 - deferredCaptures[0].ReadyAtMilliseconds)
            : 0);

    readonly Queue<LodForeignInstallCompletion> foreignInstallCompletions = new();
    readonly Dictionary<long, SaveRetryState> saveRetries = new();
    readonly Queue<long> persistenceCheckpointKeys = new();
    bool persistenceCheckpointActive;
    long nextPersistenceCheckpointMs;
    long persistenceStartedMs;

    readonly record struct SaveRetryState(long Revision, int Failures, long NotBeforeMs);

    int tickCounter;
    long worldEpoch;
    public long WorldEpoch => worldEpoch;

    public LodPipeline(ICoreAPI api, ILogger logger, LodPaletteDescriber describePalette,
        LodTintSlotResolver? tintSlotFor = null, LodStableColorResolver? stableColorFor = null,
        LodTextureSlotResolver? textureSlotFor = null, bool textureSlotsEnabled = true)
    {
        this.api = api;
        this.logger = logger;
        this.describePalette = describePalette;
        this.tintSlotFor = tintSlotFor ?? (_ => 0);
        this.stableColorFor = stableColorFor ?? (_ => 0);
        this.textureSlotFor = textureSlotFor ?? (_ => 0);
        TextureSlotsEnabled = textureSlotsEnabled;
        World = new LodWorld();
        Worker = new LodWorker();
        Remote = new LodRemoteKeySet(World);
    }

    /// <summary>
    /// Change whether palette work may resolve block textures. Enabling starts a bounded
    /// resident-L0 backfill; capture and cache loads arriving afterward resolve directly.
    /// </summary>
    public int SetTextureSlotsEnabled(bool enabled)
    {
        if (TextureSlotsEnabled == enabled) return textureSlotBackfill.PendingSections;

        TextureSlotsEnabled = enabled;
        if (!enabled)
        {
            textureSlotBackfill.Cancel();
            return 0;
        }

        textureSlotBackfill.Begin(World.Sections.Keys.Where(key => LodWorld.KeyLevel(key) == 0));
        return textureSlotBackfill.PendingSections;
    }

    ushort ResolveTextureSlot(Block? block) =>
        TextureSlotsEnabled && block != null ? textureSlotFor(block) : (ushort)0;

    ushort ResolveTextureSlot(int blockId) =>
        ResolveTextureSlot(blockId > 0 ? api.World.GetBlock(blockId) : null);

    public void ResetStorageStats()
    {
        SaveMsMax = SaveMsTotal = LoadMsMax = LoadMsTotal = 0;
        SaveCalls = LoadCalls = 0;
    }

    public void ResetPhaseCosts()
    {
        LoadInstallCost.Reset();
        ForeignInstallCost.Reset();
        CaptureScheduleCost.Reset();
        CaptureApplyCost.Reset();
        MipApplyCost.Reset();
        MipScheduleCost.Reset();
        SaveSnapshotCost.Reset();
        LoadInstallItems = 0;
        LoadInstallBytes = 0;
        ForeignInstallItems = 0;
        ForeignInstallBytes = 0;
        CaptureApplyItems = 0;
        CaptureApplyBytes = 0;
    }

    /// <summary>Note a chunk column as needing (re)capture. Safe from any thread.</summary>
    public void QueueColumn(int cx, int cz)
    {
        long key = ((long)cz << 32) | (uint)cx;
        if (queuedColumns.TryAdd(key, 0)) pendingColumns.Enqueue(key);
    }

    /// <summary>
    /// Capture a chunk column the caller already holds, rather than one the world can
    /// be asked for. <see cref="QueueColumn"/> cannot serve a peeked column: a peek
    /// puts nothing in the loaded chunk list, so the BlockAccessor lookup in
    /// ScheduleCaptures finds nothing - or, for a coordinate that also exists on disk,
    /// finds the savegame's version instead.
    ///
    /// Safe from any thread. The chunk array is copied and the rain map is cloned, so
    /// the caller can drop its references as soon as this returns. Deliberately not
    /// routed through the queued-column dedup dictionaries: that keeps this lock-free,
    /// and a duplicate capture of identical data is idempotent at apply time.
    /// </summary>
    /// <returns>False when the pipeline is closed or the inputs cannot describe a column.</returns>
    public bool CaptureColumn(int cx, int cz, IWorldChunk?[] chunks, ushort[] rainMap)
    {
        if (!Active) return false;
        if (chunks.Length == 0 || rainMap.Length < ChunkSize * ChunkSize) return false;

        var refs = new IWorldChunk?[chunks.Length];
        Array.Copy(chunks, refs, chunks.Length);

        Worker.EnqueueCapture(new CaptureJob
        {
            Epoch = worldEpoch,
            Cx = cx,
            Cz = cz,
            // This overload is used by transient worldgen peeks. It has generated
            // terrain, not the savegame's current snow accumulation.
            SnowStateObserved = false,
            Chunks = refs,
            RainMap = (ushort[])rainMap.Clone(),
            Chisels = CollectChisels(refs, cx, cz),
        });
        return true;
    }

    /// <summary>
    /// Combined capture jobs and completed/deferred results retained by the pipeline.
    /// A producer that can throttle itself (chunk generation) must stop at the source:
    /// jobs hold unpacked chunk columns, while results retain their converted run arrays.
    /// </summary>
    public int CaptureBacklog => Worker.PendingCaptures + PendingCaptureResults;
    public bool CaptureBacklogFull => CaptureBacklog >= MaxWorkerCaptureBacklog;

    /// <summary>
    /// Open (or create) the LOD cache for the current world and adopt its key set.
    /// Failing to open is not fatal: capture and rendering work without persistence.
    /// </summary>
    /// <param name="subdir">ModData-relative directory for the cache file.</param>
    /// <param name="suffix">
    /// Appended to the world key. Belt and braces after a real bug: client and server
    /// resolve the same ModData path from the same savegame identifier, so in one process
    /// they opened one file through two connections. Naming them apart means that class of
    /// mistake cannot silently corrupt a cache even if the two ever coexist again.
    /// </param>
    public void Open(string subdir, string suffix = "")
    {
        worldEpoch++;
        textureSlotBackfill.Cancel();
        string worldKey = api.World.SavegameIdentifier;
        if (string.IsNullOrEmpty(worldKey)) worldKey = "seed-" + api.World.Seed;
        worldKey = Regex.Replace(worldKey, "[^A-Za-z0-9_-]", "_");

        string dir = api.GetOrCreateDataPath(subdir);
        string dbPath = Path.Combine(dir, worldKey + suffix + ".db");

        var newStore = new LodStore(logger);
        if (!newStore.Open(dbPath))
        {
            newStore.Dispose();
            Active = true; // no persistence this session; everything else still works
            return;
        }

        store = newStore;
        DbPath = dbPath;
        newStore.ClassifyBlock = blockId =>
        {
            Block? block = blockId > 0 ? api.World.GetBlock(blockId) : null;
            return block == null
                ? ((byte)0, (byte)0, 0, (ushort)0)
                : (LodBlockPolicy.FlagsFor(block), tintSlotFor(block), stableColorFor(block),
                    ResolveTextureSlot(block));
        };
        // In integrated singleplayer both pipelines share one process and therefore one
        // environment. The guarded crash hook belongs to the client cache only; a server
        // row winning the marker race would not prove client recovery on restart.
        storageThread = new LodStorageThread(
            newStore, enableMipInterruptionHook: string.IsNullOrEmpty(suffix),
            autoCommitSaves: false);
        persistenceStartedMs = Environment.TickCount64;
        nextPersistenceCheckpointMs = persistenceStartedMs + PersistenceCheckpointIntervalMs;

        // Background reloads for the render path. The loader runs on the storage
        // thread; results are installed on the world thread in Tick.
        storageThread.SetLoader(key => newStore.LoadSection(
            LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key), api.World, resolveBlockIds: false));
        // Routing, not just loading: a key the server offered and local disk has never
        // held would come back empty from the store and land in LoadFailed, which is
        // permanent. Those go to the network instead, and the quadtree's own
        // LoadsInFlight bookkeeping covers both paths unchanged.
        World.RequestAsyncLoad = key =>
        {
            if (!Remote.WantFromRemote(key)) storageThread?.RequestLoad(key);
        };

        World.LoadFromStore = key =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            LodSection? loaded = store?.LoadSection(
                LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key), api.World);
            double ms = clock.Elapsed.TotalMilliseconds;
            LoadCalls++;
            LoadMsTotal += ms;
            if (ms > LoadMsMax) LoadMsMax = ms;
            return loaded;
        };
        CachedSectionsLoaded = store.LoadAllKeys((level, sx, sz, applyToParent) =>
        {
            Remote.AddLocalKey(LodWorld.SectionKey(level, sx, sz));
            World.InstallStoredKey(level, sx, sz, applyToParent);
        });
        // Server-cache rows this cache's copies already contain, so a restart does not merge
        // them again. Always empty on the server half, which never merges.
        int mergeRecords = store.LoadSiblingMerges(Remote.AddMergeRecord);
        Active = true;
        logger.Notification("LOD cache: {0} ({1} persisted mip obligations)",
            dbPath, World.MipDirty.Count);
        if (mergeRecords > 0)
            logger.Notification("LOD cache: {0} server-cache rows already merged into this cache",
                mergeRecords);
    }

    /// <summary>
    /// Transfer one foreign compressed blob to the storage-owned decoder. The worker
    /// retains block codes and stored flags only; registry resolution and publication
    /// happen later on this pipeline's owning thread. A copy already in RAM is merged into,
    /// never replaced, and only when there is a reason to: a pending fill, or an offer the
    /// client wrote its own content into first. Otherwise local data has won.
    /// </summary>
    /// <param name="sourceRowid">The sibling-cache rowid the blob was read from; 0 off the network.</param>
    public LodForeignQueueOutcome QueueForeignBlob(
        long key, byte[] blob, LodForeignSource source, long sourceRowid = 0)
    {
        if (store == null || storageThread == null || blob.Length == 0)
            return LodForeignQueueOutcome.Unavailable;
        if (World.Sections.ContainsKey(key) && !Remote.CanMergeInto(key))
            return LodForeignQueueOutcome.Unavailable;
        long expectedContentRevision = World.ContentRevision(key);
        return storageThread.TryEnqueueForeign(
            worldEpoch, key, source, blob, sourceRowid, expectedContentRevision)
            ? LodForeignQueueOutcome.Queued
            : LodForeignQueueOutcome.Retryable;
    }

    public bool CanQueueForeignBlob(LodForeignSource source) =>
        storageThread?.CanEnqueueForeign(source) == true;

    public bool TryTakeForeignInstallCompletion(out LodForeignInstallCompletion completion) =>
        foreignInstallCompletions.TryDequeue(out completion);

    public int ForeignSectionsInstalled { get; private set; }

    /// <summary>
    /// Which keys a remote source can supply and which the view is waiting on. Its own
    /// class so the set logic can be tested without a game API - see LodRemoteKeySet.
    /// Private, and reached only through the delegating members below: the pipeline is
    /// the facade the mod system talks to, and two doors to the same state is how they
    /// drift apart.
    /// </summary>
    readonly LodRemoteKeySet Remote;

    /// <inheritdoc cref="LodRemoteKeySet.RemoteOnly"/>
    public HashSet<long> RemoteOnly => Remote.RemoteOnly;

    /// <inheritdoc cref="LodRemoteKeySet.MarkUnavailable"/>
    public void MarkRemoteUnavailable(long key) => Remote.MarkUnavailable(key);

    /// <inheritdoc cref="LodRemoteKeySet.AddRemoteKeys"/>
    public int AddRemoteKeys(IEnumerable<long> keys) => Remote.AddRemoteKeys(keys);

    /// <inheritdoc cref="LodRemoteKeySet.AddRemoteUpdates"/>
    public int AddRemoteUpdates(IEnumerable<long> keys) => Remote.AddRemoteUpdates(keys);

    /// <inheritdoc cref="LodRemoteKeySet.AddSiblingRows"/>
    public LodSiblingDelta AddSiblingRows(IEnumerable<LodSiblingRow> rows) =>
        Remote.AddSiblingRows(rows);

    /// <inheritdoc cref="LodRemoteKeySet.FillWanted"/>
    public long[] FillWanted() => Remote.FillWanted();

    /// <summary>Held sections still waiting for a merge from a source that has more of them.</summary>
    public int PendingFills => Remote.PendingFills;

    /// <summary>Foreign copies merged into a resident copy, and what those merges moved.</summary>
    public long ForeignMerges { get; private set; }
    public long ColumnsFilled { get; private set; }
    public long ColumnsReplaced { get; private set; }
    public long ColumnsKeptObserved { get; private set; }

    /// <summary>
    /// Newer-wins merges applied as plain fills because the client captured the section after
    /// the server's copy was read (G7): that capture is the newer of the two.
    /// </summary>
    public long MergesKeptNewerCapture { get; private set; }

    /// <summary>One line for the log: how far merging into held sections has got.</summary>
    public string DescribeFills() =>
        $"{Remote.PendingFills} fills pending ({Remote.FillsWantedNow} ready to read, "
        + $"{Remote.FillsInFlight} reading, {Remote.EagerFillsActive} loading for a level-0 rewrite), "
        + $"{ForeignMerges} merged: {ColumnsFilled} columns filled, {ColumnsReplaced} replaced by a "
        + $"newer save, {ColumnsKeptObserved} kept because only ours was observed, "
        + $"{MergesKeptNewerCapture} kept a newer capture; {Remote.FillsAlreadyComplete} already "
        + $"complete, {Remote.FillsDeferred} deferred, {Remote.FillsAbandoned} unreadable, "
        + $"{Remote.MergeRecordsAwaitingSave} records waiting for their save";

    /// <inheritdoc cref="LodRemoteKeySet.Wanted"/>
    public long[] RemoteWanted() => Remote.Wanted();

    /// <inheritdoc cref="LodRemoteKeySet.MarkRequested"/>
    public void MarkRemoteRequested(IEnumerable<long> sent) => Remote.MarkRequested(sent);

    /// <inheritdoc cref="LodRemoteKeySet.CompleteLocalOffer"/>
    public void CompleteLocalOffer(long key, LodLocalOfferOutcome outcome) =>
        Remote.CompleteLocalOffer(key, outcome);

    /// <inheritdoc cref="LodRemoteKeySet.MarkLocalOfferAccepted"/>
    public void MarkLocalOfferAccepted(long key) => Remote.MarkLocalOfferAccepted(key);

    /// <inheritdoc cref="LodRemoteKeySet.MarkRetryable"/>
    public void MarkRemoteRetryable(long key) => Remote.MarkRetryable(key);

    /// <summary>
    /// Pending fills examined per tick, a dictionary lookup each. The cap only stops a first
    /// session with tens of thousands of held keys from examining all of them in one tick.
    /// </summary>
    const int FillChecksPerTick = 256;

    /// <summary>One step of the whole pipeline. Call once per game tick.</summary>
    public void Tick()
    {
        if (!Active) return;

        ApplySaveCompletions();
        DrainMergeRecords();

        LodPhaseStart phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        InstallForeignSections();
        ForeignInstallCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        InstallLoadedSections();
        // Right after loads land, so a fill whose target just arrived is wanted this tick;
        // timed with the loads it follows.
        Remote.PumpFills(FillChecksPerTick);
        LoadInstallCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        ScheduleCaptures();
        CaptureScheduleCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        ApplyCaptureResults();
        CaptureApplyCost.Add(phaseStart);

        if (TextureSlotsEnabled && !textureSlotBackfill.Complete)
        {
            textureSlotBackfill.Pump(World.Sections, ResolveTextureSlot,
                TextureSlotBackfillEntriesPerTick, TextureSlotBackfillMaxMillisecondsPerTick);
        }

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        ApplyMipResults();
        MipApplyCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        ScheduleMipJobs();
        MipScheduleCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(TrackPhaseAllocations);
        PumpPersistenceCheckpoint();
        SaveSnapshotCost.Add(phaseStart);
        tickCounter++;
    }

    /// <summary>
    /// Finish decoded foreign sections on the owning thread. Palette lookup, live policy
    /// classification, atlas recolouring, skip filtering, and publication all remain
    /// here and share the same elapsed-time/byte policy as other section installs.
    ///
    /// A key with no copy in RAM or on local disk is adopted whole. A key with a copy in RAM
    /// is merged into it (MergeForeign). A key whose only copy is on local disk cannot be
    /// merged yet, so its fill waits for that copy to load.
    /// </summary>
    void InstallForeignSections()
    {
        if (storageThread == null || store == null) return;

        var budget = new LodDrainBudget();
        while (storageThread.TryPeekForeignResult(out LodForeignDecodeResult waiting)
            && budget.TryStart(waiting.EstimatedBytes))
        {
            if (!storageThread.TryTakeForeignResult(out LodForeignDecodeResult result)) break;
            if (result.Epoch != worldEpoch) continue;

            LodSection? section = result.Section;
            bool installed = false;
            bool retryable = false;
            if (section == null)
            {
                // Corruption is terminal for this offered copy. MarkUnavailable leaves a
                // resident or locally stored copy alone, without poisoning its future reload.
                Remote.MarkUnavailable(result.Key);
            }
            else
            {
                store.ResolvePendingPalette(section, api.World);
                RecolorForeignSection?.Invoke(section);
                section.RemoveRunsWithFlag(LodPaletteEntry.FlagSkip);

                // Residency is decided here, after the callbacks above and beside the
                // mutation, so no worker result can overwrite data that became
                // authoritative after submission.
                if (World.Sections.TryGetValue(result.Key, out LodSection? held))
                {
                    MergeForeign(result, held, section);
                    installed = true;
                }
                else if (Remote.IsHeldLocally(result.Key))
                {
                    Remote.DeferFill(result.Key);
                    retryable = true;
                }
                else
                {
                    World.InstallLoaded(result.Key, section);
                    // Persist it: re-fetching a mean 45.9 KB a section every session is not
                    // an option, so a foreign section becomes local cache data after adoption.
                    World.MarkChanged(result.Key);
                    Remote.MarkInstalled(result.Key);
                    Remote.NoteMerged(result.Key, result.SourceRowid);
                    ForeignSectionsInstalled++;
                    installed = true;
                }
            }

            foreignInstallCompletions.Enqueue(new LodForeignInstallCompletion(
                result.Key, result.Source, installed, retryable));
        }

        ForeignInstallItems += budget.Items;
        ForeignInstallBytes += budget.Bytes;
    }

    /// <summary>
    /// Merge a decoded foreign copy into the resident one. The only asynchronous step was
    /// decoding the source; the merge reads and writes the live section in this one step, so
    /// no stale snapshot of the client's copy can be published (G7). A newer-wins merge also
    /// needs the client's copy unchanged since the source was handed to the decoder: anything
    /// captured after that is newer than the server's row, so it is filled around but never
    /// overwritten.
    /// </summary>
    void MergeForeign(LodForeignDecodeResult result, LodSection held, LodSection source)
    {
        LodMergeMode mode = Remote.MergeModeFor(result.Key);
        if (mode == LodMergeMode.NewerWins
            && World.ContentRevision(result.Key) != result.ExpectedContentRevision)
        {
            mode = LodMergeMode.FillMissing;
            MergesKeptNewerCapture++;
        }

        LodMergeResult merged = held.MergeFrom(source, mode);
        if (merged.ContentChanged)
        {
            World.MarkChanged(result.Key, merged.TouchedEdges);
            if (merged.SnowStateChanged) World.NoteSnowStateObservationChanged();
        }
        else if (merged.SnowStateChanged)
        {
            World.MarkSnowStateObservationChanged(result.Key, merged.SnowAppearanceChanged);
        }

        ForeignMerges++;
        ColumnsFilled += merged.ColumnsFilled;
        ColumnsReplaced += merged.ColumnsReplaced;
        ColumnsKeptObserved += merged.ColumnsKeptObserved;
        Remote.MarkInstalled(result.Key);
        Remote.NoteMerged(result.Key, result.SourceRowid);
    }

    /// <summary>Hand merge records whose merged copy is now durable to the storage worker.</summary>
    void DrainMergeRecords()
    {
        if (storageThread == null) return;
        List<(long Key, long Rowid)>? ready = Remote.TakeReadyMergeRecords();
        if (ready != null) storageThread.SaveSiblingMerges(ready);
    }

    void ApplyMipResults()
    {
        for (int n = 0; n < PropagationsPerTick && Worker.MipResults.TryDequeue(out MipResult? result); n++)
        {
            if (result.Epoch == worldEpoch) World.CompletePropagation(result);
        }
    }

    void ScheduleMipJobs()
    {
        int capacity = MaxMipBacklog - World.MipInFlightCount;
        if (capacity <= 0) return;

        foreach (MipJob job in World.CreatePropagationJobs(
            worldEpoch, Math.Min(PropagationsPerTick, capacity)))
        {
            Worker.EnqueueMip(job);
        }
    }

    /// <summary>
    /// Drop a few cold sections from RAM around an anchor. The world keeps a rolling queue,
    /// so this never turns one game tick into a full resident-section scan.
    /// </summary>
    public bool MaybeEvictAround(double x, double z)
    {
        if (World.LoadFromStore == null) return false;
        World.EvictColdSections(x, z, 2);
        return tickCounter % 1200 == 0;
    }

    /// <summary>
    /// Adopt sections the storage thread finished reading. Decompression already happened
    /// off-thread, but palette resolution and policy repair still touch the live registry
    /// here, so publication is bounded by both elapsed time and estimated section bytes.
    /// </summary>
    void InstallLoadedSections()
    {
        if (storageThread == null) return;

        var budget = new LodDrainBudget();
        while (storageThread.TryPeekLoadResult(out LodLoadResult waiting)
            && budget.TryStart(waiting.EstimatedBytes))
        {
            // Single owning-thread consumer: after the successful peek this can fail only
            // during teardown, in which case no live world remains to publish into.
            if (!storageThread.TryTakeLoadResult(out LodLoadResult result)) break;

            int repaired = 0;
            // Palette ids are resolved here, on the world thread, before anything can
            // read them: the storage thread must not touch the block registry.
            if (result.Section != null && store != null)
            {
                store.ResolvePendingPalette(result.Section, api.World);
                // Reclassify has just refreshed flags from the live blocks, so this drops
                // runs for anything that is no longer terrain (fire, meta) from sections
                // captured under an older policy, without needing a re-explore.
                result.Section.RemoveRunsWithFlag(LodPaletteEntry.FlagSkip);
                repaired = RepairUncoloredPalette?.Invoke(result.Section) ?? 0;
            }
            World.InstallLoaded(result.Key, result.Section);

            // Written back, so the repair is done once rather than on every load for the
            // rest of the world's life. This is the only reason a read marks a section
            // dirty, and it stops as soon as the cache is clean.
            if (repaired > 0)
            {
                PaletteEntriesRepaired += repaired;
                World.MarkChanged(result.Key);
            }
        }

        LoadInstallItems += budget.Items;
        LoadInstallBytes += budget.Bytes;
    }

    // ---- Capture scheduling (world thread gathers refs, worker reads blocks) ----

    void ScheduleCaptures()
    {
        int capacity = MaxWorkerCaptureBacklog - CaptureBacklog;
        if (capacity <= 0) return;

        int chunkYCount = api.World.BlockAccessor.MapSizeY / ChunkSize;

        int scheduleBudget = Math.Min(CaptureSchedulesPerTick, capacity);
        for (int n = 0; n < scheduleBudget && pendingColumns.TryDequeue(out long key); n++)
        {
            queuedColumns.TryRemove(key, out _);
            int cx = (int)(key & 0xFFFFFFFF);
            int cz = (int)(key >> 32);

            IMapChunk? mapChunk = api.World.BlockAccessor.GetMapChunk(cx, cz);
            ushort[]? rainMap = mapChunk?.RainHeightMap;
            if (rainMap == null)
            {
                CaptureDropsUnloaded++;
                continue;
            }

            var chunks = new IWorldChunk?[chunkYCount];
            for (int cy = 0; cy < chunkYCount; cy++)
            {
                chunks[cy] = api.World.BlockAccessor.GetChunk(cx, cy, cz);
            }

            Worker.EnqueueCapture(new CaptureJob
            {
                Epoch = worldEpoch,
                Cx = cx,
                Cz = cz,
                // Loaded client/server chunks contain the actual snow layers and
                // snow-covered block variants that vanilla is drawing.
                SnowStateObserved = true,
                Chunks = chunks,
                RainMap = (ushort[])rainMap.Clone(),
                Chisels = CollectChisels(chunks, cx, cz),
            });
        }
    }

    /// <summary>
    /// Copy what the column's chiselled blocks are made of, on the thread creating the job
    /// (LodChiselStandIn.Collect). A failure costs the column its stand-ins, never its
    /// capture: its chisels are then recorded exactly as they were before 0.4.62.
    /// </summary>
    List<LodChiselRecord>? CollectChisels(IWorldChunk?[] chunks, int cx, int cz)
    {
        try
        {
            return LodChiselStandIn.Collect(chunks, cx, cz, IsChiselBlock, StandsInForChisel);
        }
        catch (Exception e)
        {
            if (Interlocked.Increment(ref chiselCollectErrors) == 1)
            {
                logger.Warning("Could not read a chiselled block's contents; that column records its "
                    + "chisels as plain blocks. {0}", e);
            }
            return null;
        }
    }

    /// <summary>A chisel: the engine's own block class, whichever mod registered the code.</summary>
    bool IsChiselBlock(int blockId) =>
        chiselByBlockId.GetOrAdd(blockId, id => BlockOrNull(id) is Vintagestory.GameContent.BlockMicroBlock);

    /// <summary>
    /// Whether a material may stand in for a chisel: something the distant terrain draws as
    /// ground. Snow, liquid, markers and flowers would turn a carved block into a snow layer,
    /// water or nothing at all, and a block with its own entity has no fixed colour either,
    /// so each of those gives way to the next most visible material.
    /// </summary>
    bool StandsInForChisel(int blockId) =>
        standInByBlockId.GetOrAdd(blockId, id =>
        {
            Block? block = BlockOrNull(id);
            if (block == null || block.BlockId <= 0 || block.EntityClass != null) return false;
            const byte notGround = LodPaletteEntry.FlagSkip | LodPaletteEntry.FlagSnowLayer
                | LodPaletteEntry.FlagWater | LodPaletteEntry.FlagThin;
            return (LodBlockPolicy.FlagsFor(block) & notGround) == 0;
        });

    Block? BlockOrNull(int blockId) =>
        blockId > 0 && blockId < api.World.Blocks.Count ? api.World.Blocks[blockId] : null;

    // Concurrent because CaptureColumn may create jobs from any thread.
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> chiselByBlockId = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> standInByBlockId = new();

    // ---- Applying capture results: block ids → section palette ids ----

    /// <summary>
    /// Capture results whose section was evicted and is being reloaded. Bounded, and the
    /// bound is not decoration: past it the tick takes the blocking load rather than let
    /// this grow without limit, because throwing a result away loses captured terrain.
    /// </summary>
    readonly List<CaptureResult> deferredCaptures = new();

    const int MaxDeferredCaptures = 64;

    void ApplyCaptureResults()
    {
        int remaining = CaptureAppliesPerTick;
        var drain = new LodDrainBudget();

        // Results waiting on a reload get first refusal, so a section that has come back
        // is merged before anything newer touches it.
        for (int i = 0; i < deferredCaptures.Count && remaining > 0;)
        {
            if (!World.EnsureResident(deferredCaptures[i].SectionKey)) { i++; continue; }
            if (!drain.TryStart(deferredCaptures[i].EstimatedBytes)) break;
            ApplyOneCaptureResult(deferredCaptures[i]);
            deferredCaptures.RemoveAt(i);
            remaining--;
        }

        while (remaining > 0 && Worker.TryPeekCaptureResult(out CaptureResult result))
        {
            if (result.Epoch != worldEpoch)
            {
                if (!Worker.TryTakeCaptureResult(out _)) break;
                remaining--;
                continue;
            }

            // An evicted section has to come back from disk before capture may merge into
            // it, or the merge writes into an empty section that then overwrites the
            // stored row. That was solved for mip propagation and not here, so this path
            // still paid a synchronous SQLite read and a Deflate on the game tick:
            // measured at 10.60ms average and 112.98ms worst, in a 50ms tick.
            //
            // EnsureResident starts the background reload and says "not yet". The result
            // waits a tick or two, which is invisible, instead of the whole world waiting
            // on a decompress.
            if (!World.EnsureResident(result.SectionKey))
            {
                // Room is made by forcing the OLDEST result through, blocking load and
                // all, never by letting this one past. A newer result that overtook an
                // older one for the same section would leave the older one landing last,
                // writing columns that have already been superseded. The list is a queue
                // for that reason, and the bound is enforced from its head.
                if (deferredCaptures.Count >= MaxDeferredCaptures)
                {
                    if (!drain.TryStart(deferredCaptures[0].EstimatedBytes)) break;
                    if (!Worker.TryTakeCaptureResult(out result)) break;
                    ApplyOneCaptureResult(deferredCaptures[0]);
                    deferredCaptures.RemoveAt(0);
                }
                else if (!Worker.TryTakeCaptureResult(out result)) break;
                deferredCaptures.Add(result);
                remaining--;
                continue;
            }

            if (!drain.TryStart(result.EstimatedBytes)) break;
            if (!Worker.TryTakeCaptureResult(out result)) break;
            ApplyOneCaptureResult(result);
            remaining--;
        }

        CaptureApplyItems += drain.Items;
        CaptureApplyBytes += drain.Bytes;
    }

    void ApplyOneCaptureResult(CaptureResult result)
    {
        LodSection section = World.GetOrCreateSection(result.SectionKey);

        var pidByBlockId = new Dictionary<int, int>();
        ulong[]?[] batch = result.RunsByColumn;
        bool snowStateChanged = false;
        bool snowAppearanceChanged = false;
        int snowObservedColumns = 0;
        int snowCoveredColumns = 0;

        for (int col = 0; col < batch.Length; col++)
        {
            ulong[]? runs = batch[col];
            if (runs == null) continue;

            int kept = 0;
            bool snowCovered = false;
            bool foundSurface = false;
            for (int i = 0; i < runs.Length; i++)
            {
                int blockId = LodSection.RunPaletteId(runs[i]); // raw block id from capture
                if (!pidByBlockId.TryGetValue(blockId, out int pid))
                {
                    // One palette entry per block id per section, coloured from the first
                    // run seen. Chiselled blocks arrive already recorded as the material that
                    // shows most (LodChiselStandIn), so they share that material's entry; only
                    // a chisel whose contents could not be read is still a chisel here, and
                    // one such chisel's answer then stands in for the whole section.
                    pid = RegisterPaletteEntry(section, result.SectionKey, blockId, col, runs[i]);
                    pidByBlockId[blockId] = pid;
                }

                // Decorative ground cover never becomes terrain: a flower would
                // otherwise be a solid, pale-grey 1-block cube.
                if ((section.Palette[pid].Flags & LodPaletteEntry.FlagSkip) != 0) continue;

                // Runs are top-down. Vanilla snow blocks are appearance layered over
                // the first real surface below them; a snow-covered block variant is
                // already that surface. Only the first retained surface answers the
                // column, so buried snow cannot repaint the landscape above it.
                if (!foundSurface)
                {
                    byte flags = section.Palette[pid].Flags;

                    // A chisel recorded as its material keeps the chisel's own snow: a
                    // snow-covered chisel variant is a different block from its planks.
                    if (result.StandInOriginals != null
                        && result.StandInOriginals.TryGetValue(
                            LodChiselStandIn.RunKey(col, LodSection.RunYTop(runs[i])), out int chisel))
                    {
                        flags = LodBlockPolicy.FlagsFor(api.World.Blocks[chisel]);
                    }

                    if ((flags & LodPaletteEntry.FlagSnowLayer) != 0)
                    {
                        snowCovered = true;
                    }
                    else
                    {
                        snowCovered |= (flags & LodPaletteEntry.FlagSnowCovered) != 0;
                        foundSurface = true;
                    }
                }

                runs[kept++] = LodSection.PackRun(pid, LodSection.RunYTop(runs[i]), LodSection.RunYBottom(runs[i]));
            }

            // An authoritative recapture upgrades generated/legacy data and can clear
            // snow that melted. A later generated peek can never erase that stronger
            // provenance or its paired coverage answer.
            if (result.SnowStateObserved)
            {
                snowObservedColumns++;
                if (snowCovered) snowCoveredColumns++;
                bool wasEffectivelyCovered = section.IsSnowStateObserved(col)
                    && section.IsSnowCovered(col);
                bool coveredChanged = section.SetSnowCovered(col, snowCovered);
                bool observedChanged = section.SetSnowStateObserved(col, observed: true);
                snowStateChanged |= coveredChanged || observedChanged;
                bool isEffectivelyCovered = section.IsSnowStateObserved(col)
                    && section.IsSnowCovered(col);
                snowAppearanceChanged |= wasEffectivelyCovered != isEffectivelyCovered;
            }

            if (kept != runs.Length) batch[col] = runs[..kept];
        }

        ColumnsCaptured++;
        ChiselStandIns += result.ChiselStandIns;
        ChiselsWithoutMaterial += result.ChiselsWithoutMaterial;

        // Whether anything changed and which edges it touched are two different questions.
        // A block placed in the middle of a section changes it and touches no edge, so the
        // edge mask alone is EdgeNone - and treating that as "nothing happened" let the new
        // runs into resident RAM without dirtying the L0 mesh, the persistent row or the mip
        // obligation. MarkChanged already does the right thing with EdgeNone: it advances the
        // revisions and skips all four neighbours. See G138.
        int touchedEdges = section.ReplaceColumns(batch, out bool contentChanged);
        if (contentChanged)
        {
            World.MarkChanged(result.SectionKey, touchedEdges);
            if (snowStateChanged) World.NoteSnowStateObservationChanged();

            // A capture forced into a key only a source offered leaves a partial copy in front
            // of the source's complete one; this asks for the source's other columns.
            Remote.NoteLocalContent(result.SectionKey);
        }
        else if (snowStateChanged)
        {
            // Observed-bare provenance still persists and propagates, but it no longer
            // remeshes an identical bare surface. Effective coverage changes rebuild only
            // the section's ordinary mesh; snow adds no geometry.
            World.MarkSnowStateObservationChanged(
                result.SectionKey, snowAppearanceChanged);
        }

        if (result.SnowStateObserved)
        {
            SnowStateCaptured?.Invoke(result.Cx, result.Cz,
                snowObservedColumns, snowCoveredColumns);
        }

        // Owning-thread completion for jobs that keep real engine columns resident
        // until capture has actually consumed them. This is intentionally after every
        // result, including an idempotent one: unchanged content is still a completed
        // capture obligation and its force-loaded lease must be released.
        ColumnCaptured?.Invoke(result.Cx, result.Cz);
    }

    /// <summary>
    /// World position of the top block of a run. The describer must get the block's own
    /// position, not a stand-in: chiselled blocks answer GetColorWithoutTint from the
    /// block entity at that exact position, and a probe at the chunk-column centre made
    /// every chisel average unknown.png instead - a real cache held that near-white
    /// (0x00FCFCFC) 8319 times. Capture results are always level 0, where a column is
    /// one block wide, and yTop is exclusive (a run spans [yBottom, yTop)).
    /// </summary>
    public static (int X, int Y, int Z) CaptureBlockPos(long sectionKey, int col, ulong run)
    {
        int localX = col % LodSection.GridSize;
        int localZ = col / LodSection.GridSize;
        return (LodWorld.KeySx(sectionKey) * LodSection.SectionBlocks + localX,
                LodSection.RunYTop(run) - 1,
                LodWorld.KeySz(sectionKey) * LodSection.SectionBlocks + localZ);
    }

    int RegisterPaletteEntry(LodSection section, long sectionKey, int blockId, int col, ulong run)
    {
        Block block = api.World.Blocks[blockId];
        (int x, int y, int z) = CaptureBlockPos(sectionKey, col, run);
        (int color, byte tintSlot) = describePalette(blockId, x, y, z);
        return section.FindOrAddPaletteEntry(blockId, color, LodBlockPolicy.FlagsFor(block),
            tintSlot, ResolveTextureSlot(block));
    }

    // ---- Persistence ----

    void ApplySaveCompletions()
    {
        if (storageThread == null) return;

        while (storageThread.TryTakeSaveCompletion(out LodSaveCompletion completion))
        {
            bool newestBecameDurable = World.CompleteSave(
                completion.Key, completion.Revision, completion.Succeeded);

            if (completion.Succeeded)
            {
                // A durable row is now a valid local fallback even when a still-newer
                // revision remains dirty.
                Remote.MarkPersisted(completion.Key, completion.Revision);
                if (newestBecameDurable)
                {
                    saveRetries.Remove(completion.Key);
                    SectionPersisted?.Invoke(completion.Key);
                }
                continue;
            }

            // A stale failure must not delay a newer snapshot already in flight.
            if (World.PersistenceRevision(completion.Key) != completion.Revision
                || World.QueuedSaveRevision(completion.Key) != 0) continue;

            int failures = 1;
            if (saveRetries.TryGetValue(completion.Key, out SaveRetryState prior)
                && prior.Revision == completion.Revision)
            {
                failures = prior.Failures + 1;
            }
            saveRetries[completion.Key] = new SaveRetryState(
                completion.Revision, failures,
                Environment.TickCount64 + LodSaveRetry.DelayMilliseconds(failures));
        }
    }

    /// <summary>
    /// Owning-thread notification that the newest row for a key became durable. The server
    /// uses level-zero notifications to tell clients about refreshed terrain only after the
    /// blob reader can actually serve it.
    /// </summary>
    public Action<long>? SectionPersisted;

    /// <summary>Owning-thread notification that a queued column capture was applied.</summary>
    public Action<int, int>? ColumnCaptured;

    /// <summary>
    /// Owning-thread summary of an authoritative loaded-column snow observation. Used by
    /// the server's finite snow scheduler; zero covered means bare now, never incapable.
    /// </summary>
    public Action<int, int, int, int>? SnowStateCaptured;

    void SaveSomeDirtySections(int budget)
    {
        if (store == null || storageThread == null || World.SaveDirty.Count == 0 || budget <= 0) return;

        storageClock.Restart();
        foreach (long key in World.SaveDirty)
        {
            if (storageThread.Backlog >= MaxStorageBacklog) break;
            long revision = World.PersistenceRevision(key);
            if (saveRetries.TryGetValue(key, out SaveRetryState retry)
                && retry.Revision == revision && Environment.TickCount64 < retry.NotBeforeMs)
                continue;
            if (!World.Sections.TryGetValue(key, out LodSection? section)) continue;
            if (!World.TryQueueSave(key, out revision)) continue;

            // Freeze on this thread (the section keeps mutating), compress and
            // write on the storage thread.
            var snap = LodSaveSnapshot.Of(LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key),
                revision, section, api.World, World.MipDirty.Contains(key));
            if (!storageThread.Enqueue(snap))
            {
                World.CancelQueuedSave(key, revision);
                break;
            }
            if (--budget <= 0) break;
        }

        double ms = storageClock.Elapsed.TotalMilliseconds;
        SaveCalls++;
        SaveMsTotal += ms;
        if (ms > SaveMsMax) SaveMsMax = ms;
    }

    /// <summary>
    /// Freeze one fixed RAM checkpoint over several ticks, then publish it to SQLite as
    /// one transaction. Newer mutations remain dirty and belong to the next checkpoint.
    /// Snapshot creation must stay on this thread because sections mutate in place, but
    /// its elapsed time, bytes and item count are bounded; compression and I/O are not.
    /// </summary>
    void PumpPersistenceCheckpoint()
    {
        if (store == null || storageThread == null) return;

        long now = Environment.TickCount64;
        if (!persistenceCheckpointActive)
        {
            // Do not overlap transactions. An immediate follow-up begins on the first
            // tick after the previous batch is durable and its acknowledgements apply.
            if (now < nextPersistenceCheckpointMs || storageThread.Backlog > 0) return;

            // Cap one checkpoint's frozen copies. Anything beyond the cap remains dirty
            // in authoritative RAM for the next checkpoint instead of forcing extra disk
            // transactions or an unbounded snapshot allocation burst.
            foreach (long key in World.SaveDirty)
            {
                if (!ReadyForPersistenceSnapshot(key, now)) continue;
                persistenceCheckpointKeys.Enqueue(key);
                if (persistenceCheckpointKeys.Count >= MaxStorageBacklog) break;
            }
            persistenceCheckpointActive = persistenceCheckpointKeys.Count > 0;
            if (!persistenceCheckpointActive)
            {
                nextPersistenceCheckpointMs = now + PersistenceCheckpointIntervalMs;
                return;
            }
        }

        storageClock.Restart();
        var drain = new LodDrainBudget();
        int remaining = PersistenceSnapshotsPerTick;
        while (remaining > 0 && persistenceCheckpointKeys.TryDequeue(out long key))
        {
            long revision = World.PersistenceRevision(key);
            if (saveRetries.TryGetValue(key, out SaveRetryState retry)
                && retry.Revision == revision && now < retry.NotBeforeMs)
                continue;
            if (!World.Sections.TryGetValue(key, out LodSection? section)) continue;
            if (World.QueuedSaveRevision(key) != 0) continue;
            if (!drain.TryStart(section.EstimatedContentBytes))
            {
                persistenceCheckpointKeys.Enqueue(key);
                break;
            }
            if (!World.TryQueueSave(key, out revision)) continue;

            var snapshot = LodSaveSnapshot.Of(
                LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key),
                revision, section, api.World, World.MipDirty.Contains(key));
            if (!storageThread.Enqueue(snapshot))
            {
                World.CancelQueuedSave(key, revision);
                persistenceCheckpointKeys.Enqueue(key);
                break;
            }
            remaining--;
        }

        if (drain.Items > 0)
        {
            double ms = storageClock.Elapsed.TotalMilliseconds;
            SaveCalls++;
            SaveMsTotal += ms;
            if (ms > SaveMsMax) SaveMsMax = ms;
        }

        if (persistenceCheckpointKeys.Count == 0)
        {
            // Snapshot keys still have queued revisions until this transaction finishes,
            // so any ready key here belongs to later work. Keep draining such a backlog
            // checkpoint-by-checkpoint without inserting the ordinary 30-second pause.
            bool readyFollowUp = HasReadyPersistenceSnapshot(now);
            storageThread.CommitPendingSaves();
            persistenceCheckpointActive = false;
            nextPersistenceCheckpointMs = NextPersistenceCheckpointStart(now, readyFollowUp);
        }
    }

    internal static long NextPersistenceCheckpointStart(long now, bool readyFollowUp) =>
        readyFollowUp ? now : now + PersistenceCheckpointIntervalMs;

    bool ReadyForPersistenceSnapshot(long key, long now)
    {
        if (!World.Sections.ContainsKey(key) || World.QueuedSaveRevision(key) != 0) return false;
        long revision = World.PersistenceRevision(key);
        return !saveRetries.TryGetValue(key, out SaveRetryState retry)
            || retry.Revision != revision
            || now >= retry.NotBeforeMs;
    }

    bool HasReadyPersistenceSnapshot(long now)
    {
        foreach (long key in World.SaveDirty)
        {
            if (ReadyForPersistenceSnapshot(key, now)) return true;
        }
        return false;
    }

    bool FlushPersistence(int timeoutMs)
    {
        if (storageThread == null) return World.SaveDirty.Count == 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            ApplySaveCompletions();
            SaveSomeDirtySections(int.MaxValue);
            if (World.SaveDirty.Count == 0 && storageThread.Backlog == 0) return true;

            int remaining = timeoutMs - (int)clock.ElapsedMilliseconds;
            if (remaining <= 0) break;
            if (storageThread.Backlog > 0) storageThread.Drain(Math.Min(100, remaining));
            else Thread.Sleep(Math.Min(10, remaining));
        }

        ApplySaveCompletions();
        return World.SaveDirty.Count == 0 && storageThread.Backlog == 0;
    }

    string DescribeUnresolvedSaves() => string.Join(", ", World.SaveDirty
        .OrderBy(key => key)
        .Select(key => $"{LodWorld.KeyLevel(key)}:{LodWorld.KeySx(key)}:{LodWorld.KeySz(key)}"
            + $"@r{World.PersistenceRevision(key)}"
            + (World.QueuedSaveRevision(key) == 0 ? "" : $"(queued-r{World.QueuedSaveRevision(key)})")));

    /// <summary>
    /// Flush and shut the cache down. Order matters: queue everything, let the writer
    /// finish, stop the thread, and only then close the connection it was writing through.
    /// </summary>
    public void Close()
    {
        Active = false;
        worldEpoch++;
        World.LoadFromStore = null;
        World.RequestAsyncLoad = null;

        if (store != null)
        {
            if (storageThread != null)
            {
                FlushPersistence(StorageShutdownTimeoutMs);
                // Records whose merged copy the flush made durable reach the worker before it
                // stops, and its shutdown writes them. One released after this is lost, which
                // only means that merge runs again next session.
                DrainMergeRecords();
                storageThread.Dispose();
                ApplySaveCompletions();
                if (World.SaveDirty.Count > 0 || storageThread.Backlog > 0)
                    logger.Warning("Storage shutdown left unresolved saves: {0}; writer backlog: {1}",
                        DescribeUnresolvedSaves(), storageThread.Backlog);
                storageThread = null;
            }
            store.Close();
            store.Dispose();
            store = null;
        }

        queuedColumns.Clear();
        pendingColumns.Clear();
        Remote.Clear();
        saveRetries.Clear();
        persistenceCheckpointKeys.Clear();
        persistenceCheckpointActive = false;
        nextPersistenceCheckpointMs = 0;
        persistenceStartedMs = 0;
        foreignInstallCompletions.Clear();
        // Results for the world we are leaving must not be applied to the next one.
        // Both queues, or a result held back for a reload would cross worlds.
        Worker.ClearCaptureResults();
        Worker.ClearMeshWork();
        Worker.ClearMipWork();
        textureSlotBackfill.Cancel();
        deferredCaptures.Clear();
        World.Clear();
        CachedSectionsLoaded = 0;
        ColumnsCaptured = 0;
        CaptureDropsUnloaded = 0;
        ForeignMerges = 0;
        ColumnsFilled = 0;
        ColumnsReplaced = 0;
        ColumnsKeptObserved = 0;
        MergesKeptNewerCapture = 0;
        DbPath = null;
    }

    public void Dispose()
    {
        Close();
        Worker.Dispose();
    }
}
