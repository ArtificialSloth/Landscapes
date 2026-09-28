using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using VintageHorizons.Net;
using Vintagestory.Server;

namespace VintageHorizons.Checks;

/// <summary>
/// The chunk thread rest (Session 111). The game method it hooks must still exist in the
/// installed game, Harmony must accept the hook and remove it by exact id, and the hook must
/// rest only where the engine would otherwise go straight round again: after a pass that found
/// nothing, with a negative update interval, while the rest is on. A pass that found work never
/// rests, and a failure inside the hook must never reach the chunk thread.
/// </summary>
public static class ChunkThreadRestChecks
{
    public static void Run(Check c)
    {
        FindsTheGameScan(c);
        InstallsAndRemovesByExactId(c);
        RestsOnlyWhereTheGameWouldSpin(c);
        FailsOpen(c);
    }

    /// <summary>A chunk supplier whose update interval the check chooses.</summary>
    sealed class FakeSupplier : ServerSystem
    {
        public int Interval;
        public FakeSupplier(int interval) : base(null!) => Interval = interval;
        public override int GetUpdateInterval() => Interval;
    }

    sealed class BrokenSupplier : ServerSystem
    {
        public BrokenSupplier() : base(null!) { }
        public override int GetUpdateInterval() => throw new InvalidOperationException("broken on purpose");
    }

    static void FindsTheGameScan(Check c)
    {
        MethodInfo? scan = ChunkThreadRest.FindScan();
        c.True(scan != null,
            "the installed game still has the chunk supplier's queue scan that the rest hooks");
        Type? supplier = typeof(ServerSystem).Assembly.GetType(ChunkThreadRest.SupplierTypeName);
        MethodInfo? interval = supplier?.GetMethod(nameof(ServerSystem.GetUpdateInterval),
            BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
        c.True(interval?.DeclaringType == supplier,
            "the chunk supplier still decides its own update interval, the rest's test for a spin");
    }

    static void InstallsAndRemovesByExactId(Check c)
    {
        var log = new CaptureLogger();
        MethodInfo? scan = ChunkThreadRest.FindScan();
        bool installed = false;
        c.NoThrow(() => installed = ChunkThreadRest.Install(log, restOn: true),
            "the rest installs against the current game assembly");
        c.True(installed && ChunkThreadRest.Installed && ChunkThreadRest.Resting,
            "Harmony accepts the queue-scan hook, and the rest starts on");
        c.True(scan != null && Harmony.GetPatchInfo(scan)?.Postfixes
                .Any(p => p.owner == ChunkThreadRest.HarmonyId) == true,
            "the scan carries a postfix owned by the exact Vintage Horizons id");
        c.True(ChunkThreadRest.SetResting(false, "check").Contains("off for this session", StringComparison.Ordinal),
            "the rest switches off for the session");
        c.False(ChunkThreadRest.Resting, "and is then off");

        c.NoThrow(ChunkThreadRest.Uninstall, "the rest removes cleanly");
        c.False(ChunkThreadRest.Installed, "and says it is removed");
        c.False(scan != null && Harmony.GetPatchInfo(scan)?.Owners.Contains(ChunkThreadRest.HarmonyId) == true,
            "the scan no longer has the Vintage Horizons owner");
        c.True(log.Contains("Chunk thread over this session"), "removal logs what the session saw");
        c.NoThrow(ChunkThreadRest.Uninstall, "removal is idempotent");
    }

    static void RestsOnlyWhereTheGameWouldSpin(Check c)
    {
        var log = new CaptureLogger();
        ChunkThreadRest.Install(log, restOn: true);
        try
        {
            var spinning = new FakeSupplier(-1);
            var sleeping = new FakeSupplier(5);

            ChunkThreadRest.Counts before = ChunkThreadRest.Read();
            ChunkThreadRest.AfterScan(true, spinning);
            ChunkThreadRest.AfterScan(false, sleeping);
            ChunkThreadRest.Counts mid = ChunkThreadRest.Read();
            c.Eq(0L, mid.Rests - before.Rests,
                "a pass that found work, or one the engine sleeps after anyway, never rests");
            c.Eq(1L, mid.PassesWithWork - before.PassesWithWork, "the pass that found work is counted");
            c.Eq(0L, mid.IdlePasses - before.IdlePasses, "a pass the engine sleeps after is not an idle pass");

            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) ChunkThreadRest.AfterScan(false, spinning);
            double restedMs = clock.Elapsed.TotalMilliseconds;
            ChunkThreadRest.Counts after = ChunkThreadRest.Read();
            c.Eq(5L, after.Rests - mid.Rests, "an idle pass where the engine would spin rests");
            c.True(restedMs >= 4, $"and really sleeps ({restedMs:0.0} ms for five passes)");

            ChunkThreadRest.SetResting(false, "check");
            for (int i = 0; i < 5; i++) ChunkThreadRest.AfterScan(false, spinning);
            ChunkThreadRest.Counts off = ChunkThreadRest.Read();
            c.Eq(0L, off.Rests - after.Rests, "switched off, the same pass does not rest");
            c.Eq(5L, off.IdlePasses - after.IdlePasses, "but is still counted, so a report can compare");

            string line = ChunkThreadRest.Describe(before, off, 1);
            c.True(line.Contains("rest off (switched during this window)", StringComparison.Ordinal),
                "a window that saw the switch says so: " + line);
            c.True(line.Contains("10 idle passes/s (5 rested)", StringComparison.Ordinal),
                "and counts the idle passes and the rests separately: " + line);
        }
        finally
        {
            ChunkThreadRest.Uninstall();
        }
    }

    static void FailsOpen(Check c)
    {
        var log = new CaptureLogger();
        ChunkThreadRest.Install(log, restOn: true);
        try
        {
            c.NoThrow(() => ChunkThreadRest.AfterScan(false, new BrokenSupplier()),
                "a failure inside the rest never reaches the chunk thread");
            c.False(ChunkThreadRest.Resting, "it switches the rest off, which is the game's own behaviour");
            c.True(log.Contains("chunk thread rest failed"), "and says so in the log");
        }
        finally
        {
            ChunkThreadRest.Uninstall();
        }
    }
}
