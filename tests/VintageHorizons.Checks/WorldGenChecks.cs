using VintageHorizons.Net;

namespace VintageHorizons.Checks;

/// <summary>Pure safety boundaries for the resumable real-savegame generation job.</summary>
public static class WorldGenChecks
{
    public static void Run(Check c)
    {
        AreaAndClipping(c);
        TileOrder(c);
        StateRoundTrip(c);
        SkipRules(c);
        Gates(c);
        Leases(c);
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

    static void SkipRules(Check c)
    {
        c.Eq(EnumWorldGenSkipReason.None, LodWorldGenSkip.Classify(false, false),
            "an uncached unfinished column is issued");
        c.Eq(EnumWorldGenSkipReason.DurableState, LodWorldGenSkip.Classify(false, true),
            "the resumable durable bitmap skips completed work");
        c.Eq(EnumWorldGenSkipReason.ServerCache, LodWorldGenSkip.Classify(true, false),
            "an existing server L0 cache row skips generation");
        c.Eq(EnumWorldGenSkipReason.ServerCache, LodWorldGenSkip.Classify(true, true),
            "the live server cache is the strongest skip evidence");
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
