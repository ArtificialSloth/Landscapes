using VintageHorizons.Net;

namespace VintageHorizons.Checks;

public static class AllowanceChecks
{
    public static void Run(Check c)
    {
        c.Eq(1, LodTickAllowance.MaxPerTick(1), "a one-per-second rate still makes progress");
        c.Eq(1, LodTickAllowance.MaxPerTick(16), "the default sweep rate issues one per tick");
        c.Eq(4, LodTickAllowance.MaxPerTick(64), "a high rate rounds one tick's share upward");

        // A sweep loads only as fast as capture drains. Past the limit a swept column waits
        // longer than the engine keeps it loaded, and is dropped uncaptured.
        c.Eq(1, LodSavegameSweep.LoadsAllowed(1, 0, 0),
            "idle engine and capture queues let the sweep load at its rate");
        c.Eq(2, LodSavegameSweep.LoadsAllowed(4,
                LodSavegameSweep.MaxCaptureBacklogForLoads - 2, 0),
            "a nearly full capture queue admits only the loads it has room for");
        c.Eq(0, LodSavegameSweep.LoadsAllowed(4,
                LodSavegameSweep.MaxCaptureBacklogForLoads, 0),
            "a full capture queue holds the sweep");
        c.Eq(0, LodSavegameSweep.LoadsAllowed(4, 792, 0),
            "the owner's backed-up queue holds the sweep instead of going negative");
        c.Eq(0, LodSavegameSweep.LoadsAllowed(
                1, 0, LodSavegameSweep.YieldGeneratingChunks),
            "any real engine terrain demand holds the background priority load");

        var players = new LodPlayerTerrainActivity();
        c.False(players.MustYield(0), "a server with no players does not hold the sweep");
        players.BeginSample();
        players.Observe("one", 10, 20, 1_000);
        c.True(players.MustYield(1_000), "a joining player gets first claim on terrain loads");
        players.BeginSample();
        players.Observe("one", 10, 20, 5_999);
        c.True(players.MustYield(5_999), "an idle player retains the original quiet window");
        c.False(players.MustYield(6_000), "the sweep resumes after five quiet seconds");
        players.BeginSample();
        players.Observe("one", 11, 20, 7_000);
        c.True(players.MustYield(11_999), "crossing a chunk boundary restarts the quiet window");
        players.BeginSample();
        players.BeginSample();
        players.Observe("one", 11, 20, 13_000);
        c.True(players.MustYield(13_000), "a rejoining player gets a fresh quiet window");

        var low = new LodTickAllowance();
        c.Eq(1, low.Available(0, 8), "a new low-rate queue can serve one item immediately");
        low.Spend(1);
        c.Eq(0, low.Available(100, 8), "fractional credit does not spend early");
        c.Eq(1, low.Available(125, 8), "fractional credit reaches one at the configured rate");
        low.Spend(1);

        int issued = 0;
        for (long now = 175; now <= 1125; now += 50)
        {
            int available = low.Available(now, 8);
            issued += available;
            low.Spend(available);
        }
        c.Eq(8, issued, "normal ticks preserve the long-run per-second rate");

        var high = new LodTickAllowance();
        c.Eq(4, high.Available(0, 64), "initial credit is only one tick's high-rate share");
        high.Spend(4);
        c.Eq(3, high.Available(50, 64), "a 50ms tick accrues its fractional high-rate share");
        high.Spend(3);
        c.Eq(4, high.Available(10_000, 64), "a delayed tick cannot release catch-up work");
        high.Spend(4);
        c.Eq(3, high.Available(10_050, 64), "the tick after a delay resumes the normal rate");
        high.Spend(3);
        c.Eq(0, high.Available(9_000, 64), "a backwards clock jump grants no credit");

        var unused = new LodTickAllowance();
        c.Eq(2, unused.Available(0, 32), "the global default starts with a two-item ceiling");
        c.Eq(2, unused.Available(30_000, 32), "idle credit remains capped to one tick");

        c.Throws<ArgumentOutOfRangeException>(() => unused.Spend(3),
            "callers cannot overspend an allowance silently");

        // The player hold is a floor, not a stop. A walking player renews the quiet window
        // faster than it expires, so a hold that granted nothing would suspend the sweep for
        // as long as they keep moving; measured at 7 sections/min on 2026-09-19 (G234).
        c.Eq(2, LodSavegameSweep.FloorPerSecond(16),
            "the default 16/s sweep keeps a 2/s floor while a player holds it");
        c.Eq(1, LodSavegameSweep.FloorPerSecond(1),
            "the slowest configured sweep still keeps a one-per-second floor");
        c.Eq(8, LodSavegameSweep.FloorPerSecond(64),
            "the floor scales with the configured rate rather than being a fixed constant");
        c.True(LodSavegameSweep.FloorPerSecond(16) < 16,
            "the floor is a reduction; a hold must still cost the sweep most of its rate");

        // The floor is drawn from its own allowance, which only a held tick spends. A long
        // hold must not let it accumulate into a burst when movement stops.
        var floor = new LodTickAllowance();
        int floorRate = LodSavegameSweep.FloorPerSecond(16);
        c.Eq(1, floor.Available(0, floorRate), "a held tick may issue one column at the floor");
        floor.Spend(1);
        c.Eq(0, floor.Available(50, floorRate), "the floor does not grant on every 50ms tick");
        c.Eq(1, floor.Available(500, floorRate), "the floor grants again at its own rate");
        floor.Spend(1);
        c.Eq(1, floor.Available(600_000, floorRate),
            "ten minutes of holding cannot release a catch-up burst at the floor");

        CaptureLedger(c);
        SweepDecision(c);
        SweepOrder(c);
        SweepAbsenceSample(c);
        ImportProgress(c);
        AuditRules(c);
        SettledClaims(c);
    }

    /// <summary>
    /// The load pass covers every explored column, nearest a player first.
    ///
    /// Until 0.5.1 it walked a fixed square 4,096 blocks around spawn, so explored land further
    /// out never reached the cache however long the server ran (a player report, 2026-10-21).
    /// </summary>
    static void SweepOrder(Check c)
    {
        (int, int)[] spawn = { (0, 0) };
        long near = LodColumnMap.Key(2, 1);
        long middle = LodColumnMap.Key(-40, 30);
        long far = LodColumnMap.Key(10_000, -10_000);   // 320,000 blocks out

        long[] sorted = LodSweepOrder.SortRemaining(
            new[] { far, middle, near }, 0, null, spawn);
        c.Eq(3, sorted.Length, "nothing is dropped for being far away: there is no limit");
        c.Eq(near, sorted[0], "the column nearest the target comes first");
        c.Eq(far, sorted[2], "the farthest column comes last, but it comes");

        // Two players: each is served from their own surroundings outwards, so a column beside
        // the second player goes ahead of one halfway between them.
        (int, int)[] two = { (0, 0), (10_000, -10_000) };
        long besideSecond = LodColumnMap.Key(10_001, -10_000);
        long between = LodColumnMap.Key(5_000, -5_000);
        sorted = LodSweepOrder.SortRemaining(new[] { between, besideSecond }, 0, null, two);
        c.Eq(besideSecond, sorted[0], "distance is to the nearest player, not to one of them");

        // What came before the cursor, or was already visited, is not sorted back in.
        sorted = LodSweepOrder.SortRemaining(
            new[] { near, middle, far }, 1, new HashSet<long> { far }, spawn);
        c.Eq(1, sorted.Length, "a re-sort keeps only what is left to visit");
        c.Eq(middle, sorted[0], "and that is the unvisited remainder");

        // A re-sort while the pass carries on visits every column exactly once. The swap only
        // happens at Collect, so the visits between Resort and Collect are the ones that ran
        // while the sort did - deterministically, however fast the sorting thread is.
        var keys = new List<long>();
        for (int x = -6; x <= 6; x++)
        for (int z = -6; z <= 6; z++)
            keys.Add(LodColumnMap.Key(x * 7, z * 7));

        var order = new LodSweepOrder();
        order.Begin(keys.ToArray(), spawn);
        c.False(order.Finished, "a pass is not finished while its first sort runs");
        WaitForSort(order);
        c.Eq(LodColumnMap.Key(0, 0), Peek(order), "the first order starts at the target");

        var visited = new List<long>();
        for (int i = 0; i < 5; i++) Visit(order, visited);
        order.Resort(new[] { (42, 42) });
        for (int i = 0; i < 7; i++) Visit(order, visited);
        WaitForSort(order);
        c.Eq(LodColumnMap.Key(42, 42), Peek(order),
            "after the swap the pass continues from the new target");
        while (order.TryPeek(out _)) Visit(order, visited);

        c.True(order.Finished, "the pass finishes once everything is visited");
        c.Eq(keys.Count, visited.Count, "every column is visited");
        c.Eq(keys.Count, visited.Distinct().Count(), "and none twice across the re-sort");
        c.Eq(keys.Count, order.Visited, "the progress count matches");
        c.Eq(2L, order.Sorts, "both orders were swapped in");

        // Re-sorting is for real movement, not every step.
        c.False(order.WantsResort(new[] { (42 + 8, 42 - 8) }, 8),
            "a player within the move distance of the last sort does not re-sort");
        c.True(order.WantsResort(new[] { (42 + 9, 42) }, 8),
            "a player past it does");
        c.True(order.WantsResort(new[] { (42, 42), (0, 0) }, 8),
            "a join re-sorts at once, wherever the newcomer is");
    }

    /// <summary>
    /// What a player is told about the import (owner, 2026-10-21): everyone affected, but only
    /// about land inside their own Cache distance, and only when it is worth telling.
    /// </summary>
    static void ImportProgress(Check c)
    {
        // The tally, in a world whose spawn is chunk 8000 (block 256,000), like the test world.
        var tally = new LodImportTally();
        for (int i = 0; i < 100; i++) tally.Add(LodColumnMap.Key(8000 + i % 10, 8000 + i / 10));
        for (int i = 0; i < 50; i++) tally.Add(LodColumnMap.Key(8500 + i, 8000));   // 16 km east
        tally.Add(LodColumnMap.Key(-3, -3));                                       // negative cells
        c.Eq(151, tally.Pending, "every counted column is pending");
        int[] bands = tally.Histogram(256_000, 256_000);
        c.Eq(LodImportStatusBands.Count, bands.Length, "one count per band");
        c.Eq(151, bands.Sum(), "the bands account for every pending column");
        c.Eq(100, bands[0] + bands[1], "the land around the player sits in the nearest bands");
        c.Eq(51, LodImportView.PendingWithin(bands, 0) - LodImportView.PendingWithin(bands, 8192),
            "land 16 km away, and the far-off corner, are outside an 8,192-block Cache distance");
        c.Eq(150, LodImportView.PendingWithin(bands, 32768),
            "a 32,768-block Cache distance reaches the land 16 km away");
        tally.Remove(LodColumnMap.Key(8000, 8000));
        tally.Remove(LodColumnMap.Key(123, 456));   // never counted
        c.Eq(150, tally.Pending, "a column the pass reaches stops counting; an uncounted one is ignored");

        // The bands a Cache distance takes: those that start inside it.
        int[] simple = new int[LodImportStatusBands.Count];
        simple[0] = 10; simple[1] = 20; simple[2] = 30; simple[^1] = 1000;
        c.Eq(10, LodImportView.PendingWithin(simple, 512), "a 512-block distance takes the first band");
        c.Eq(30, LodImportView.PendingWithin(simple, 1000), "a band that starts inside the distance counts");
        c.Eq(1060, LodImportView.PendingWithin(simple, 0), "an unlimited distance takes everything");
        c.Eq(1060, LodImportView.PendingWithin(simple, 40_000),
            "a distance past the last band's start takes the far band too");

        c.Eq("under a minute", LodImportView.Describe(30), "short imports are not given false precision");
        c.Eq("about 10 min", LodImportView.Describe(600), "minutes under an hour");
        c.Eq("about 1 h", LodImportView.Describe(3600), "a whole hour");
        c.Eq("about 1 h 20 min", LodImportView.Describe(4790), "hours in five-minute steps");

        // The pace is measured, never assumed once it can be.
        var meter = new LodRateMeter();
        meter.Note(0, 0);
        meter.Note(2_000, 999);   // within five seconds of the last sample: ignored
        for (long t = 5_000; t <= 25_000; t += 5_000) meter.Note(t, t / 1000 * 8);
        c.Eq(0.0, meter.PerSecond, "no pace is claimed from under 30 seconds");
        meter.Note(30_000, 240);
        c.Near(8.0, meter.PerSecond, 1e-9, "the pace is loads over the measured window");

        // The presenter, one step a second.
        AssistImportStatus Status(int nearby, double pace) => new()
        {
            Active = true, BandBlocks = LodImportStatusBands.BandBlocks,
            PendingByBand = new[] { nearby }.Concat(new int[LodImportStatusBands.Count - 1]).ToArray(),
            ColumnsPerSecond = pace,
        };
        var view = new LodImportPresenter();
        view.Step(null, 8192, 0);
        c.True(view.OverlayText == null && !view.PopupDue, "nothing shows before the server reports");
        view.Step(Status(100, 0), 8192, 1_000);
        c.True(view.OverlayText == null && !view.PopupDue,
            "a few seconds' import nearby bothers nobody");

        var farOnly = Status(0, 8);
        farOnly.PendingByBand[40] = 50_000;   // 20 km away
        view.Step(farOnly, 8192, 2_000);
        c.True(view.OverlayText == null && !view.PopupDue,
            "land outside the player's Cache distance does not count for them");

        view.Step(Status(6000, 0), 8192, 3_000);
        c.True(view.PopupDue, "an import of ten minutes or more nearby brings the popup");
        c.True(view.OverlayText?.Contains("working out the time left") == true,
            "the overlay does not claim a time before the pace is measured");
        c.Near(LodImportPresenter.GhostAlpha, view.OverlayAlpha, 1e-6, "and it is faint");
        view.PopupShown();
        view.Step(Status(3000, 5), 8192, 4_000);
        c.False(view.PopupDue, "the popup shows once a session");
        c.Eq("Importing explored land: 50% · about 10 min left", view.OverlayText,
            "progress against the most seen nearby, and the measured time left");

        view.Step(Status(0, 5), 8192, 5_000);
        c.Eq("Explored land nearby is imported", view.OverlayText, "done is said, not just vanished");
        view.Step(Status(0, 5), 8192, 5_000 + LodImportPresenter.DoneHoldMs + LodImportPresenter.FadeMs / 2);
        c.True(view.OverlayAlpha > 0 && view.OverlayAlpha < LodImportPresenter.GhostAlpha,
            "then it fades");
        view.Step(Status(0, 5), 8192, 5_000 + LodImportPresenter.DoneHoldMs + LodImportPresenter.FadeMs);
        c.True(view.OverlayText == null, "and is gone");

        c.True(LodImportNotice.Text(12345, 8192, "about 1 h 20 min", true, false).StartsWith(
                "This world has explored land that is not in your distant view yet: your Cache distance "
                + "is set to 8,192 blocks - about 12,345 chunks within that distance need to be imported.",
                StringComparison.Ordinal),
            "the popup opens with the owner's wording (2026-10-21)");
        c.True(LodImportNotice.Text(5, 0, "under a minute", false, true)
                .Contains("your Cache distance is unlimited - about 5 chunks across the world", StringComparison.Ordinal)
                && LodImportNotice.Text(5, 0, "under a minute", false, true)
                    .Contains("shared by everyone online", StringComparison.Ordinal),
            "the unlimited and server forms say so");

        var finished = new LodImportPresenter();
        finished.Step(Status(2000, 8), 8192, 0);
        finished.Step(new AssistImportStatus { Finished = true }, 8192, 1_000);
        c.Eq("Explored land nearby is imported", finished.OverlayText,
            "the server's last status ends the line the same way");
    }

    static long Peek(LodSweepOrder order) => order.TryPeek(out long key) ? key : long.MinValue;

    static void Visit(LodSweepOrder order, List<long> visited)
    {
        if (!order.TryPeek(out long key)) return;
        visited.Add(key);
        order.Advance();
    }

    static void WaitForSort(LodSweepOrder order)
    {
        // The swap happens in Collect; the sort itself is on the thread pool.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        long sorts = order.Sorts;
        while (order.Sorts == sorts && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(1);
            order.Collect();
        }
    }

    /// <summary>
    /// With no square to sample from, the absence check samples beside the frontier, which is
    /// where a load that reached too far would have generated terrain.
    /// </summary>
    static void SweepAbsenceSample(Check c)
    {
        const int R = 12;
        var map = new LodColumnMap();
        var frontier = new List<long>();
        for (int x = -R; x <= R; x++)
        for (int z = -R; z <= R; z++)
            map.Add(x, z);
        for (int x = -R; x <= R; x++)
        for (int z = -R; z <= R; z++)
        {
            if (map.Classify(x, z) == EnumColumnAction.SkipFrontier)
                frontier.Add(LodColumnMap.Key(x, z));
        }

        List<long> sample = map.AbsentBesideFrontier(frontier, 64);
        bool onlyAbsent = true, beside = true;
        foreach (long key in sample)
        {
            int cx = LodColumnMap.KeyCx(key), cz = LodColumnMap.KeyCz(key);
            onlyAbsent &= !map.Contains(cx, cz);
            int outside = Math.Max(Math.Abs(cx), Math.Abs(cz)) - R;
            beside &= outside >= 1 && outside <= LodColumnMap.SafeNeighbourhood;
        }
        c.True(sample.Count > 0, "an explored area with an edge yields a sample");
        c.True(sample.Count <= 64, "the sample respects its cap");
        c.True(onlyAbsent, "the sample holds only positions absent from the map");
        c.True(beside, "every sampled position is just past the explored edge");
        c.Eq(sample.Count, sample.Distinct().Count(), "no position is sampled twice");

        Func<int, int, bool> eastOnly = (cx, _) => cx > R;
        List<long> filtered = map.AbsentBesideFrontier(frontier, 8, eastOnly);
        bool East(long k) => eastOnly(LodColumnMap.KeyCx(k), LodColumnMap.KeyCz(k));
        c.True(filtered.Any(East), "the filter's preferred positions are sampled");
        c.True(filtered.SkipWhile(East).All(k => !East(k)),
            "and taken before the fallback tops the sample up");

        c.Eq(0, map.AbsentBesideFrontier(new List<long>(), 64).Count,
            "no frontier, nothing to sample");
    }

    /// <summary>
    /// When a claim is allowed to exist, and what it records.
    ///
    /// The fingerprint read during the startup index describes the file before the column was
    /// loaded, and loading a dormant column is itself what changes it - the engine catches up
    /// its transient blocks, snow and block entities on the way in, and the next save writes
    /// that. A claim made from the index-time reading therefore describes a file state this
    /// cache never captured, and costs a reload on the next start for nothing: 399 of 10,439
    /// columns on the owner's world on 2026-09-20. So taking a durable capture and claiming it
    /// are two separate steps, with a re-read of the file in between.
    /// </summary>
    static void SettledClaims(Check c)
    {
        long key = LodColumnMap.Key(11, 12);
        const ulong AtIndexTime = 100;
        const ulong AfterTheSave = 777;

        var captured = new Dictionary<long, ulong>();
        var ledger = new LodCaptureLedger(captured);
        ledger.SetSavegame(new Dictionary<long, ulong> { [key] = AtIndexTime });
        ledger.Submitted(key);

        ulong taken = 0;
        int handed = ledger.TakeDurable(_ => true, 8, (_, print) => taken = print);
        c.Eq(1, handed, "a durable capture is handed over");
        c.Eq(AtIndexTime, taken, "with the fingerprint read when it was submitted");
        c.False(ledger.AlreadyCaptured(key),
            "taking a durable capture does not by itself claim the column");
        c.Eq(0, captured.Count, "and writes nothing");

        // The claim the re-read produces has to move both halves. The skip test compares what
        // the savegame says against what this cache claims, so leaving the savegame side at
        // the older reading would make the column look changed for the rest of the session -
        // the same reload this exists to prevent, one session earlier.
        ledger.Claim(key, AfterTheSave);
        c.True(ledger.AlreadyCaptured(key), "a claim taken after the save is skippable");
        c.Eq(AfterTheSave, captured[key], "and records what the file held after the save");
        c.False(ledger.Disagrees(key, AfterTheSave),
            "the audit agrees with a claim that matches the file");
        c.True(ledger.Disagrees(key, 999),
            "and still disputes one the file has moved away from");

        // A column that never becomes durable is never handed over, so it cannot be claimed
        // from either reading. It is examined again next start, which is the safe direction.
        long never = LodColumnMap.Key(13, 14);
        ledger.SetSavegame(new Dictionary<long, ulong> { [never] = 55 });
        ledger.Submitted(never);
        c.Eq(0, ledger.TakeDurable(_ => false, 8, (_, _) => { }),
            "a capture that is not durable is not handed over");
        c.False(ledger.AlreadyCaptured(never), "and the column stays unclaimed");
        c.Eq(1, ledger.Pending, "it simply waits");
    }

    /// <summary>
    /// What the background audit acts on. Its job is to disprove claims this cache makes,
    /// so the question it asks is narrower than it first looks: a column with no claim is
    /// not a failed audit, it is simply uncaptured.
    /// </summary>
    static void AuditRules(Check c)
    {
        long held = LodColumnMap.Key(4, 5);
        long uncaptured = LodColumnMap.Key(6, 7);

        var captured = new Dictionary<long, ulong>();
        var ledger = new LodCaptureLedger(captured);
        ledger.SetSavegame(new Dictionary<long, ulong> { [held] = 100, [uncaptured] = 200 });

        c.False(ledger.Disagrees(held, 100),
            "a column with no claim is not a mismatch even when the savegame knows it");

        ledger.Submitted(held);
        ledger.TakeDurable(_ => true, 8, ledger.Claim);
        c.True(ledger.AlreadyCaptured(held), "the column is now claimed");

        c.False(ledger.Disagrees(held, 100), "a claim matching the savegame is not a mismatch");
        c.True(ledger.Disagrees(held, 999),
            "a claim the savegame no longer agrees with is what the audit exists to find");

        // The rule that stops the audit re-reporting every uncaptured column in the world
        // forever, which would bury the real findings and load terrain nobody asked for.
        c.False(ledger.Disagrees(uncaptured, 12345),
            "an uncaptured column is never an audit mismatch, whatever the savegame holds");

        // Acting on a mismatch drops the claim first, so nothing skips the column while its
        // recapture is in flight.
        ledger.UpdateSavegame(held, 999);
        ledger.Forget(held);
        c.False(ledger.AlreadyCaptured(held),
            "a disproved column stops being skippable immediately");
        c.False(ledger.Disagrees(held, 999),
            "a forgotten column has no claim left to disagree with");

        // And the recapture re-earns the claim at the content the audit actually saw.
        ledger.Submitted(held);
        ledger.TakeDurable(_ => true, 8, ledger.Claim);
        c.True(ledger.AlreadyCaptured(held), "the recaptured column is claimed again");
        c.Eq(999UL, captured[held],
            "the re-earned claim records what the audit read, not the content it replaced");

        // The rotation covers what the cache claims, so a column proven later is included
        // when the order is rebuilt rather than being audited never.
        List<long> order = ledger.CapturedKeys();
        c.Eq(1, order.Count, "the rotation covers the claims that exist");
        c.SeqEq(new[] { held }, order, "and only those");
        ledger.Submitted(uncaptured);
        ledger.TakeDurable(_ => true, 8, ledger.Claim);
        c.Eq(2, ledger.CapturedKeys().Count,
            "a column proven after the rotation was built appears when it is rebuilt");
    }

    /// <summary>
    /// The load pass's per-column outcome, and the snow invariant the plan calls the single
    /// easiest regression to introduce: every column the savegame actually holds must stay a
    /// snow-refresh candidate, including the ones the sweep decides not to load.
    /// </summary>
    static void SweepDecision(Check c)
    {
        c.Eq(EnumSweepColumnAction.Nothing,
            LodSweepDecision.Decide(EnumColumnAction.Peek, alreadyCaptured: false),
            "a column the savegame does not hold is no work for the sweep");
        c.Eq(EnumSweepColumnAction.Frontier,
            LodSweepDecision.Decide(EnumColumnAction.SkipFrontier, alreadyCaptured: false),
            "a frontier column is refused so the engine is not made to generate its neighbours");
        c.Eq(EnumSweepColumnAction.Frontier,
            LodSweepDecision.Decide(EnumColumnAction.SkipFrontier, alreadyCaptured: true),
            "the frontier rule outranks the fingerprint; a held column is still not loadable");
        c.Eq(EnumSweepColumnAction.Load,
            LodSweepDecision.Decide(EnumColumnAction.Load, alreadyCaptured: false),
            "a safe column the cache lacks is loaded");
        c.Eq(EnumSweepColumnAction.AlreadyCaptured,
            LodSweepDecision.Decide(EnumColumnAction.Load, alreadyCaptured: true),
            "a safe column the cache already proved it captured is skipped");

        // The invariant, stated over every outcome rather than over the ones that exist
        // today, so a future outcome has to decide this question deliberately.
        foreach (EnumSweepColumnAction action in Enum.GetValues<EnumSweepColumnAction>())
        {
            c.Eq(action != EnumSweepColumnAction.Nothing,
                LodSweepDecision.RegistersSnowCandidate(action),
                $"{action} registers a snow candidate exactly when the savegame holds the column");
        }

        c.True(LodSweepDecision.RegistersSnowCandidate(EnumSweepColumnAction.AlreadyCaptured),
            "a column skipped as already captured is still snow-maintained - this is the "
            + "regression that shows up months later as no snow in the distance");
        c.True(LodSweepDecision.RegistersSnowCandidate(EnumSweepColumnAction.Frontier),
            "a frontier column stays a snow candidate for when exploration fills its edge");

        // Only a real load may spend the rate. Charging a skip would leave a world where
        // nothing changed crawling through its spiral one allowance at a time.
        c.True(LodSweepDecision.CostsAllowance(EnumSweepColumnAction.Load),
            "a real load spends the rate allowance");
        c.False(LodSweepDecision.CostsAllowance(EnumSweepColumnAction.AlreadyCaptured),
            "deciding a column needs nothing does not spend the rate allowance");
        c.False(LodSweepDecision.CostsAllowance(EnumSweepColumnAction.Frontier),
            "refusing a frontier column does not spend the rate allowance");
        c.False(LodSweepDecision.CostsAllowance(EnumSweepColumnAction.Nothing),
            "an absent column does not spend the rate allowance");
    }

    /// <summary>
    /// The skip rule and its fingerprint commit point. Both of this class's mistakes are
    /// silent: skipping a column that changed leaves distant terrain permanently wrong, and
    /// recording a fingerprint for a capture that failed does the same while also making the
    /// audit blind to it.
    /// </summary>
    static void CaptureLedger(Check c)
    {
        long a = LodColumnMap.Key(10, 20);
        long b = LodColumnMap.Key(11, 20);
        long never = LodColumnMap.Key(99, 99);

        var captured = new Dictionary<long, ulong>();
        var ledger = new LodCaptureLedger(captured);

        // Before the savegame has been read, nothing may be skipped. This is the state a
        // world is in when the read path failed, and it must behave exactly as the sweep did
        // before fingerprints existed.
        c.False(ledger.AlreadyCaptured(a), "nothing is skipped before the savegame is read");
        ledger.Submitted(a);
        c.Eq(0, ledger.Pending,
            "a column submitted with no savegame fingerprint is not queued for one");

        ledger.SetSavegame(new Dictionary<long, ulong> { [a] = 111, [b] = 222 });
        c.False(ledger.AlreadyCaptured(a),
            "a column the cache has never proved it captured is never skipped");
        c.Eq(0, ledger.AlreadyCapturedCount, "an empty cache matches nothing");

        // The commit point. A submitted column earns its fingerprint only once its capture
        // is durable, never when its savegame row was read.
        ledger.Submitted(a);
        c.Eq(1, ledger.Pending, "a submitted column waits for its capture");
        c.False(ledger.AlreadyCaptured(a),
            "a column whose capture is still in flight is not yet skippable");

        int written = ledger.TakeDurable(_ => false, 32, (_, _) =>
            c.True(false, "a capture that is not durable must never be handed over"));
        c.Eq(0, written, "nothing commits while the capture has not reached the durable row");
        c.Eq(1, ledger.Pending, "the column is still waiting, not dropped");

        var writes = new List<long>();
        written = ledger.TakeDurable(key => key == a, 32, (key, print) =>
        {
            writes.Add(key);
            ledger.Claim(key, print);
        });
        c.Eq(1, written, "a durable capture commits exactly once");
        c.SeqEq(new[] { a }, writes, "the durable column is the one written");
        c.Eq(0, ledger.Pending, "committing clears the column from the waiting set");
        c.True(ledger.AlreadyCaptured(a), "a proven column is skippable afterwards");
        c.Eq(111UL, captured[a], "the committed fingerprint is the savegame content it was submitted at");

        // The whole point of comparing content rather than existence: the savegame changing
        // underneath a proven column must make it loadable again.
        ledger.SetSavegame(new Dictionary<long, ulong> { [a] = 999, [b] = 222 });
        c.False(ledger.AlreadyCaptured(a),
            "a column whose savegame content changed is no longer skippable");
        c.Eq(0, ledger.AlreadyCapturedCount, "the changed column stops counting as captured");

        // A column nobody has ever mentioned is not skippable in either direction.
        c.False(ledger.AlreadyCaptured(never), "an unknown column is never skipped");

        // The per-tick bound must not lose anything it did not commit.
        var many = new LodCaptureLedger();
        var savegame = new Dictionary<long, ulong>();
        for (int i = 0; i < 10; i++) savegame[LodColumnMap.Key(i, 0)] = (ulong)(i + 1);
        many.SetSavegame(savegame);
        for (int i = 0; i < 10; i++) many.Submitted(LodColumnMap.Key(i, 0));
        c.Eq(10, many.Pending, "every submitted column is waiting");
        c.Eq(4, many.TakeDurable(_ => true, 4, many.Claim),
            "a bounded commit writes at most its limit");
        c.Eq(6, many.Pending, "the rest keep waiting rather than being dropped");
        c.Eq(6, many.TakeDurable(_ => true, 32, many.Claim),
            "the remainder commits on a later tick");
        c.Eq(0, many.Pending, "nothing is left behind");
    }
}
