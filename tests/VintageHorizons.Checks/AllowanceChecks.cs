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
    }
}
