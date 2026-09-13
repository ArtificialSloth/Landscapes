using VintageHorizons.Net;
using Vintagestory.API.Config;

namespace VintageHorizons.Checks;

/// <summary>Pure safety boundaries for the resumable real-savegame generation job.</summary>
public static class WorldGenChecks
{
    public static void Run(Check c)
    {
        AreaAndClipping(c);
        TileOrder(c);
        DurableSectionMapping(c);
        StateRoundTrip(c);
        GeneratePageMath(c);
        Gates(c);
        BoundaryAndRestartStatus(c);
        Leases(c);
        KeptColumnReleaseBinding(c);
        Authority(c);
    }

    static void AreaAndClipping(Check c)
    {
        c.Eq(EnumWorldGenShape.Circle, LodWorldGenPlan.DefaultShape,
            "savegame world generation defaults to a circular footprint");

        var square = new LodWorldGenPlan(20, 20, 2, EnumWorldGenShape.Square, 100, 100);
        c.Eq(25, square.Count, "a square radius two contains its full inclusive 5x5 area");
        c.Eq(25, square.Columns.Select(x => (x.Cx, x.Cz)).Distinct().Count(),
            "every square column appears exactly once");

        var circle = new LodWorldGenPlan(20, 20, 2, EnumWorldGenShape.Circle, 100, 100);
        c.Eq(13, circle.Count, "a lattice circle radius two contains the expected 13 columns");
        c.True(circle.Columns.All(x =>
                (x.Cx - 20) * (x.Cx - 20) + (x.Cz - 20) * (x.Cz - 20) <= 4),
            "a circular plan never leaks through a square corner");

        var clipped = new LodWorldGenPlan(0, 0, 2, EnumWorldGenShape.Square, 10, 10);
        c.Eq(9, clipped.Count, "an edge-centred square clips to the world instead of wrapping");
        c.True(clipped.Columns.All(x => x.Cx >= 0 && x.Cz >= 0 && x.Cx < 10 && x.Cz < 10),
            "every clipped column remains within map bounds");

        int mapped = 0;
        for (int i = 0; i < square.Count; i++)
        {
            LodWorldGenColumn item = square.Columns[i];
            if (square.TryGetIndex(item.Cx, item.Cz, out int found) && found == i) mapped++;
        }
        c.Eq(square.Count, mapped, "persistence callbacks map every planned coordinate to its index");
    }

    static void TileOrder(Check c)
    {
        var plan = new LodWorldGenPlan(31, 31, 24, EnumWorldGenShape.Square, 128, 128);
        c.True(plan.Tiles.Count > 4, "the ordering fixture spans several 16x16 tiles");

        int centreTileX = 31 / LodWorldGenPlan.TileChunks;
        int centreTileZ = 31 / LodWorldGenPlan.TileChunks;
        c.True(plan.Tiles[0].X == centreTileX && plan.Tiles[0].Z == centreTileZ,
            "the player-containing tile is always generated first");

        int previousBand = int.MinValue;
        double previousAngle = double.NegativeInfinity;
        bool outwardBands = true;
        bool clockwiseWithinBands = true;
        bool columnsNearestFirst = true;
        bool boundaries = true;
        for (int tileIndex = 0; tileIndex < plan.Tiles.Count; tileIndex++)
        {
            LodWorldGenTile tile = plan.Tiles[tileIndex];
            int band = LodWorldGenPlan.TileRadialBand(31, 31, tile.X, tile.Z);
            double angle = LodWorldGenPlan.Angle(
                tile.X * LodWorldGenPlan.TileChunks
                    + (LodWorldGenPlan.TileChunks - 1) / 2.0 - 31,
                tile.Z * LodWorldGenPlan.TileChunks
                    + (LodWorldGenPlan.TileChunks - 1) / 2.0 - 31);
            outwardBands &= band >= previousBand;
            if (band != previousBand) previousAngle = double.NegativeInfinity;
            clockwiseWithinBands &= angle >= previousAngle;
            previousBand = band;
            previousAngle = angle;

            long previousDistanceSq = -1;
            for (int i = tile.FirstColumn; i < tile.FirstColumn + tile.ColumnCount; i++)
            {
                LodWorldGenColumn column = plan.Columns[i];
                boundaries &= column.Tile == tileIndex
                    && column.Cx / LodWorldGenPlan.TileChunks == tile.X
                    && column.Cz / LodWorldGenPlan.TileChunks == tile.Z;
                long dx = column.Cx - 31L, dz = column.Cz - 31L;
                long distanceSq = dx * dx + dz * dz;
                columnsNearestFirst &= distanceSq >= previousDistanceSq;
                previousDistanceSq = distanceSq;
            }
        }
        c.True(outwardBands, "tile bands never move outward then back inward");
        c.True(clockwiseWithinBands, "each radial tile band sweeps clockwise from east");
        c.True(columnsNearestFirst, "columns within every tile start nearest the player");
        c.True(boundaries, "every tile range contains only that globally aligned tile");
    }

    static void StateRoundTrip(Check c)
    {
        c.Eq(3, LodWorldGenState.CurrentFormat,
            "resume format rejects cursors written with the broken L0/chunk acknowledgement mapping");
        var plan = new LodWorldGenPlan(20, 20, 4, EnumWorldGenShape.Circle, 100, 100);
        var state = LodWorldGenState.Create(plan, 20, 20, 4, EnumWorldGenShape.Circle,
            100, 100, "tester", 1234, 5678);
        c.True(state.MarkDone(0), "the first durable completion changes the bitmap");
        c.False(state.MarkDone(0), "a duplicate persistence callback is idempotent");
        c.True(state.MarkDone(plan.Count - 1), "the tail bit fits a partial final byte");
        c.Eq(2, state.DoneCount(), "the compact bitmap counts durable columns exactly");

        LodWorldGenState? loaded = LodWorldGenState.Deserialize(LodWorldGenState.Serialize(state));
        c.True(loaded != null, "a state file round-trips through JSON");
        c.True(loaded!.Matches(plan, 100, 100), "a resumed state matches its deterministic plan");
        c.True(loaded.IsDone(0) && loaded.IsDone(plan.Count - 1),
            "resume preserves both ends of the done bitmap");
        c.False(loaded.Matches(plan, 101, 100), "changed world bounds refuse an ambiguous resume");
        loaded.Format = LodWorldGenState.CurrentFormat - 1;
        c.False(loaded.Matches(plan, 100, 100),
            "a cursor from the earlier traversal order is refused instead of misassigned");
        c.True(LodWorldGenState.Deserialize("{not json") == null,
            "malformed state is refused instead of becoming a new job");

        string path = Path.Combine(Path.GetTempPath(),
            "vintagehorizons-worldgen-check-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new LodWorldGenStateStore(path);
        try
        {
            c.True(store.TrySave(state, out _), "the resumable cursor writes atomically");
            c.True(store.TryLoad(out LodWorldGenState? fromFile, out _),
                "the resumable cursor reads from its real state file");
            c.Eq(2, fromFile!.DoneCount(), "the state file retains the durable bitmap");
            c.True(store.TryDelete(out _), "a completed or stopped job removes its state file");
            c.False(File.Exists(path), "state deletion leaves no resumable job behind");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
        }
    }

    static void Gates(Check c)
    {
        c.Eq(EnumWorldGenGate.None, Select(), "an idle healthy pipeline permits an issue");
        c.Eq(EnumWorldGenGate.EngineQueue, Select(engineQueue: 120),
            "the engine queue gates exactly at its target");
        c.Eq(EnumWorldGenGate.InFlight, Select(inFlight: 160),
            "the kept-column cap gates exactly at its limit");
        c.Eq(EnumWorldGenGate.SaveBacklogCount, Select(dirty: 512),
            "the dirty-section count gates exactly at its limit");
        c.Eq(EnumWorldGenGate.SaveBacklogAge, Select(oldest: 120_000),
            "old unsaved terrain gates exactly at the configured age");
        c.Eq(EnumWorldGenGate.CaptureBacklog, Select(capture: true, engineQueue: 999),
            "capture pressure outranks the engine-queue explanation");
        c.Eq(EnumWorldGenGate.ServerOverload, Select(overload: true, capture: true),
            "server overload is the first resource gate reported");
        c.Eq(EnumWorldGenGate.Paused, Select(paused: true, overload: true),
            "an explicit pause is reported ahead of incidental pressure");
        c.Eq(EnumWorldGenGate.Faulted, Select(faulted: true, paused: true),
            "a safety fault cannot be hidden by pause");
    }

    static void BoundaryAndRestartStatus(Check c)
    {
        c.Eq(EnumWorldGenGate.None, LodWorldGenJob.BoundaryGate(
                final: false, inFlight: 0, capturedAwaitingPersistence: 0),
            "unrelated shared-pipeline work cannot hold a completed tile boundary");
        c.Eq(EnumWorldGenGate.TilePyramid, LodWorldGenJob.BoundaryGate(
                final: false, inFlight: 0, capturedAwaitingPersistence: 1),
            "a tile waits for its own captured L0 row to become durable");
        c.Eq(EnumWorldGenGate.InFlight, LodWorldGenJob.BoundaryGate(
                final: true, inFlight: 1, capturedAwaitingPersistence: 0),
            "the final boundary still waits for this job's kept column");
        c.Eq(EnumWorldGenGate.FinalPersistence, LodWorldGenJob.BoundaryGate(
                final: true, inFlight: 0, capturedAwaitingPersistence: 1),
            "the final boundary waits for this job's last durable L0 row");

        c.False(LodServerCaptureSystem.ShouldReportWorldGenJob(
                jobExists: true, jobDone: true, pendingConfirmation: true),
            "a stopped job's final summary cannot hide a newly prepared confirmation");
        c.True(LodServerCaptureSystem.ShouldReportWorldGenJob(
                jobExists: true, jobDone: false, pendingConfirmation: true),
            "an active job remains authoritative if a stale confirmation also exists");
        c.True(LodServerCaptureSystem.ShouldReportWorldGenJob(
                jobExists: true, jobDone: true, pendingConfirmation: false),
            "a finished job is still shown until the next area is prepared");
    }

    static void DurableSectionMapping(Check c)
    {
        c.Eq(GlobalConstants.ChunkSize, LodWorldGenSections.ChunkBlocks,
            "the resumable chunk/L0 mapping matches the installed engine chunk size");
        long first = LodWorldGenSections.KeyForChunk(0, 0);
        c.Eq(first, LodWorldGenSections.KeyForChunk(1, 1),
            "all four engine chunks in a 64-block L0 footprint map to one section");
        c.True(first != LodWorldGenSections.KeyForChunk(2, 0),
            "the next chunk pair maps to the next L0 section");
        c.True(LodWorldGenSections.ContainsChunk(first, 1, 0),
            "a persistence key recognizes a captured chunk in its 2x2 footprint");
        c.False(LodWorldGenSections.ContainsChunk(first, 2, 0),
            "a persistence key cannot credit a chunk in the neighbouring footprint");

        var section = new LodSection();
        int chunkColumns = LodSection.GridSize / LodWorldGenSections.ChunksPerLevelZeroSection;
        for (int z = 0; z < chunkColumns; z++)
        {
            for (int x = chunkColumns; x < chunkColumns * 2; x++)
            {
                int col = LodSection.ColumnIndex(x, z);
                section.SetColumn(col, ReadOnlySpan<ulong>.Empty);
            }
        }
        c.False(LodWorldGenSections.HasAuthoritativeColumns(section, 1, 0),
            "captured peek data is not proof that terrain exists in the savegame");
        for (int z = 0; z < chunkColumns; z++)
        {
            for (int x = chunkColumns; x < chunkColumns * 2; x++)
                section.SetSnowStateObserved(LodSection.ColumnIndex(x, z), observed: true);
        }
        c.True(LodWorldGenSections.HasAuthoritativeColumns(section, 1, 0),
            "a complete loaded-terrain provenance footprint is authoritative");
        c.False(LodWorldGenSections.HasAuthoritativeColumns(section, 0, 0),
            "provenance in one quadrant cannot make a sibling chunk authoritative");
    }

    static void GeneratePageMath(Check c)
    {
        c.Eq(13L, LodWorldGenPageMath.ColumnCount(2, EnumWorldGenShape.Circle),
            "the Generate page's circle estimate matches the plan lattice");
        c.Eq(25L, LodWorldGenPageMath.ColumnCount(2, EnumWorldGenShape.Square),
            "the Generate page's square estimate includes both radius edges");

        LodWorldGenPreferences normalized = new LodWorldGenPreferences(
            999, (EnumWorldGenShape)99, (EnumWorldGenCentreMode)99,
            -5, -7, "future").Normalize(64);
        c.Eq(64, normalized.RadiusChunks,
            "remembered radius is clamped to the server-advertised maximum");
        c.Eq(EnumWorldGenShape.Circle, normalized.Shape,
            "an unknown remembered shape falls back to the circular default");
        c.Eq(EnumWorldGenCentreMode.Here, normalized.CentreMode,
            "an unknown remembered centre falls back to the player");
        c.Eq((0, 0), (normalized.X, normalized.Z),
            "remembered coordinates cannot become negative");
        c.Eq("Balanced", normalized.Preset,
            "an unmeasured remembered preset cannot reach the server");

        var status = new AssistWorldGenStatus
        {
            DoneColumns = 25,
            TotalColumns = 100,
            ColumnsPerSecond = 9.5,
            EtaSeconds = 8,
            EngineQueue = 20,
            QueueTarget = 120,
            InFlight = 7,
            MaxInFlight = 160,
            Retries = 2,
            Failures = 1,
            Gate = "waiting for distant-terrain saves",
        };
        c.Eq(25, LodWorldGenPageMath.Percent(status),
            "structured progress drives the page percentage without parsing prose");
        string progress = LodWorldGenPageMath.Progress(status);
        c.True(progress.Contains("Queue 20/120, 7/160 kept", StringComparison.Ordinal)
            && progress.Contains("2 retries, 1 failures", StringComparison.Ordinal),
            "the Generate page reports queue pressure and retry/failure counts");

        c.True(VintageHorizonsConfigDialog.NeedsLiveWorldGenWindow(
                embedded: true, singlePlayer: true, gamePaused: true),
            "paused singleplayer Settings uses the live-window handoff");
        c.False(VintageHorizonsConfigDialog.NeedsLiveWorldGenWindow(
                embedded: false, singlePlayer: true, gamePaused: true),
            "the standalone window remains live while singleplayer is paused elsewhere");
        c.False(VintageHorizonsConfigDialog.NeedsLiveWorldGenWindow(
                embedded: true, singlePlayer: false, gamePaused: false),
            "multiplayer Settings keeps direct server controls");
    }

    static EnumWorldGenGate Select(bool paused = false, bool faulted = false,
        bool overload = false, bool capture = false, int dirty = 0, long oldest = 0,
        int engineQueue = 0, int inFlight = 0) => LodWorldGenGates.Select(
            paused, faulted, overload, capture, dirty, 512, oldest, 120_000,
            engineQueue, 120, inFlight, 160);

    static void Leases(Check c)
    {
        var leases = new LodWorldGenLeases();
        c.True(leases.TryBegin(0, 10, 20, 100), "the first kept-column lease is tracked");
        c.False(leases.TryBegin(1, 10, 20, 100), "a coordinate cannot acquire two leases");
        c.True(leases.TryBegin(2, 11, 20, 200), "a second coordinate gets its own lease");
        c.Eq(0, leases.Expired(299, 200).Count, "a lease does not time out one tick early");
        c.Eq(1, leases.Expired(300, 200).Count, "the oldest lease expires at the exact timeout");

        var released = new HashSet<(int, int)>();
        int first = leases.ReleaseAll((cx, cz) =>
        {
            if (cx == 11) return false;
            released.Add((cx, cz));
            return true;
        });
        c.Eq(1, first, "a stop releases every lease the adapter accepts");
        c.Eq(1, leases.Count, "a failed release remains tracked for shutdown retry");
        int second = leases.ReleaseAll((cx, cz) => released.Add((cx, cz)));
        c.Eq(1, second, "shutdown retries the one failed stop release");
        c.Eq(0, leases.Count, "no kept column survives a successful shutdown retry");
        c.Eq(2, released.Count, "each successful coordinate was released exactly once");
    }

    static void KeptColumnReleaseBinding(Check c)
    {
        var direct = new ReleaseOwner();
        c.True(LodKeptColumnReleaser.TryBindRelease(
                direct, out Action<long>? directRelease, out _),
            "a world manager that exposes the safe release method binds directly");
        directRelease!(17);
        c.SeqEq(new[] { 17L }, direct.Released,
            "the direct release delegate reaches its exact map-column index");

        var wrappedOwner = new ReleaseOwner();
        var wrapper = new DerivedReleaseWrapper(wrappedOwner);
        c.True(LodKeptColumnReleaser.TryBindRelease(
                wrapper, out Action<long>? wrappedRelease, out _),
            "the 1.22 WorldAPI shape binds ServerMain through an inherited server field");
        wrappedRelease!(23);
        c.SeqEq(new[] { 23L }, wrappedOwner.Released,
            "the wrapper delegate releases through its underlying server owner");

        c.False(LodKeptColumnReleaser.TryBindRelease(
                new object(), out _, out string refusal),
            "an unknown engine wrapper fails closed instead of unloading unsaved terrain");
        c.True(refusal.Contains("no compatible", StringComparison.Ordinal),
            "the compatibility refusal explains the missing safe release seam");
    }

    sealed class ReleaseOwner
    {
        internal readonly List<long> Released = new();
        public void RemoveChunkColumnFromForceLoadedList(long index) => Released.Add(index);
    }

    class ReleaseWrapper
    {
        // Field name and inheritance mirror WorldAPI -> ServerAPIComponentBase in 1.22.
        readonly ReleaseOwner server;
        protected ReleaseWrapper(ReleaseOwner server) => this.server = server;
    }

    sealed class DerivedReleaseWrapper : ReleaseWrapper
    {
        internal DerivedReleaseWrapper(ReleaseOwner server) : base(server) { }
    }

    static void Authority(Check c)
    {
        var authority = new LodWorldGenAuthority();
        c.True(authority.TryAcquire(EnumWorldGenAuthority.StartupPregen,
            "startup pre-generation", out _), "the first generator acquires authority");
        c.False(authority.TryAcquire(EnumWorldGenAuthority.WorldGeneration,
            "real generation", out string refusal), "a second generator is refused");
        c.True(refusal.Contains("startup pre-generation"),
            "the refusal names the generator that is already active");
        c.False(authority.Release(EnumWorldGenAuthority.TransientGeneration),
            "the wrong owner cannot release another generator's authority");
        c.True(authority.Release(EnumWorldGenAuthority.StartupPregen),
            "the owning generator releases authority");
        c.True(authority.TryAcquire(EnumWorldGenAuthority.WorldGeneration,
            "real generation", out _), "the next generator can start after release");
    }
}
