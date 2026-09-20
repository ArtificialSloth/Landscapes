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
        AuditRules(c);
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
        ledger.CommitDurable(_ => true, 8, (_, _) => { });
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
        ledger.CommitDurable(_ => true, 8, (_, _) => { });
        c.True(ledger.AlreadyCaptured(held), "the recaptured column is claimed again");
        c.Eq(999UL, captured[held],
            "the re-earned claim records what the audit read, not the content it replaced");

        // The rotation covers what the cache claims, so a column proven later is included
        // when the order is rebuilt rather than being audited never.
        List<long> order = ledger.CapturedKeys();
        c.Eq(1, order.Count, "the rotation covers the claims that exist");
        c.SeqEq(new[] { held }, order, "and only those");
        ledger.Submitted(uncaptured);
        ledger.CommitDurable(_ => true, 8, (_, _) => { });
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

        int written = ledger.CommitDurable(_ => false, 32, (_, _) =>
            c.True(false, "a capture that is not durable must never be written"));
        c.Eq(0, written, "nothing commits while the capture has not reached the durable row");
        c.Eq(1, ledger.Pending, "the column is still waiting, not dropped");

        var writes = new List<long>();
        written = ledger.CommitDurable(key => key == a, 32, (key, _) => writes.Add(key));
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
        c.Eq(4, many.CommitDurable(_ => true, 4, (_, _) => { }),
            "a bounded commit writes at most its limit");
        c.Eq(6, many.Pending, "the rest keep waiting rather than being dropped");
        c.Eq(6, many.CommitDurable(_ => true, 32, (_, _) => { }),
            "the remainder commits on a later tick");
        c.Eq(0, many.Pending, "nothing is left behind");
    }
}
