using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.Server;

namespace VintageHorizons.Net;

/// <summary>
/// Lets the game's chunk thread rest after a pass over its work queue that found nothing to do.
///
/// With the engine's default single world-generation thread on a machine with 8 or more
/// logical processors, <c>ServerSystemSupplyChunks.GetUpdateInterval</c> returns -1 whenever a
/// column request is queued, and a negative interval tells <c>ServerThread.Update</c> to go
/// round again without its usual sleep. Generating new terrain leaves a ring of neighbour
/// columns parked in that queue at the pass their neighbours needed, so while a player
/// explores, the thread re-scans those parked requests back to back, about 100,000 times a
/// second. Every scan calls <c>loadOrGenerateChunkColumn_OnChunkThread</c> for each of them,
/// and that method allocates a closure on entry whether or not it does anything. The owner's
/// 0.4.130-0.4.131 sessions measured that closure at 365-677 MB/s, 60-84% of all the garbage
/// in the process while exploring; in singleplayer the collections it forces stop the
/// renderer. Session 91's headless runs show the thread at a full core with one generation
/// thread and near zero with two or six, generating at the same speed.
///
/// Generation itself is untouched. After a pass that found nothing it could advance, and only
/// where the engine was about to go straight round again, the thread sleeps for the engine's
/// own between-pass sleep (<c>ServerThread.SleepMs</c>: 2 ms while players are connected):
/// what it already does with fewer processors, with a second generation thread, or with an
/// empty queue. A pass that found work never rests. The hook is a postfix on the scan rather than on the interval
/// because the interval is a tiny virtual method the JIT may inline into its caller before
/// the patch lands; the scan holds a loop and a try/finally and cannot be inlined.
///
/// It acts only where this mod's server half runs: singleplayer, LAN hosting, or a server with
/// the mod installed. A client of a remote server never installs it.
/// </summary>
public static class ChunkThreadRest
{
    internal const string HarmonyId = "vintagehorizons.chunkthreadrest";
    internal const string SupplierTypeName = "Vintagestory.Server.ServerSystemSupplyChunks";
    internal const string ScanMethodName = "tryLoadOrGenerateChunkColumnsInQueue";

    static readonly object gate = new();
    static Harmony? harmony;
    static MethodInfo? patched;
    static ILogger? logger;
    static volatile bool resting;
    static int failed;

    // Written only by the chunk thread; the aligned 64-bit writes are atomic, and readers go
    // through Interlocked so they never see a torn value.
    static long passes;
    static long passesWithWork;
    static long idlePasses;
    static long rests;
    static long allocatedOnThread;
    static long switches;
    static int epoch;

    /// <summary>What a report compares: counts since this server installed the rest.</summary>
    public readonly record struct Counts(
        int Epoch, long Passes, long PassesWithWork, long IdlePasses, long Rests,
        long AllocatedOnThread, long Switches);

    public static bool Installed => Volatile.Read(ref patched) != null;

    public static bool Resting => resting;

    /// <summary>Why the rest is not installed, for a report or a command reply; null when it is.</summary>
    public static string? NotInstalledReason { get; private set; }

    /// <summary>
    /// Installs the rest for this server's lifetime. False, having said why in the log, when
    /// this game version's chunk supplier is not the one this build knows.
    /// </summary>
    public static bool Install(ILogger log, bool restOn)
    {
        lock (gate)
        {
            logger = log;
            if (patched != null)
            {
                resting = restOn;
                return true;
            }

            MethodInfo? scan = FindScan();
            MethodInfo? postfix = AccessTools.Method(typeof(ChunkThreadRest), nameof(AfterScan));
            if (scan == null || postfix == null)
            {
                NotInstalledReason = "this game version's chunk thread is not the one this build "
                    + "was made for, so the game runs it unchanged";
                log.Warning("Chunk thread rest not installed: {0}.", NotInstalledReason);
                return false;
            }

            try
            {
                harmony ??= new Harmony(HarmonyId);
                harmony.Patch(scan, postfix: new HarmonyMethod(postfix));
            }
            catch (Exception e)
            {
                try
                {
                    harmony?.Unpatch(scan, HarmonyPatchType.All, HarmonyId);
                }
                catch
                {
                }
                NotInstalledReason = "the patch could not be applied, so the game runs its chunk "
                    + "thread unchanged";
                log.Warning("Chunk thread rest not installed: {0}: {1}",
                    NotInstalledReason, e.Message);
                return false;
            }

            ResetCounts();
            epoch++;
            failed = 0;
            resting = restOn;
            NotInstalledReason = null;
            Volatile.Write(ref patched, scan);
            log.Notification(
                "Chunk thread rest installed and {0}: after a pass over its queue "
                + "that found nothing to do, the game's chunk thread sleeps as its other threads do "
                + "instead of going straight round again. Generation itself is unchanged. '/vhchunkrest {1}' "
                + "switches it {1} for this session.",
                restOn ? "on" : "off", restOn ? "off" : "on");
            return true;
        }
    }

    /// <summary>Records that the admin's config left the rest out, so reports can say so.</summary>
    public static void NoteDisabledByConfig(string configFile)
    {
        lock (gate)
        {
            if (patched != null) return;
            NotInstalledReason = $"switched off in {configFile} (RestIdleChunkThread)";
        }
    }

    /// <summary>The server is going away: remove the patch and say what the session saw.</summary>
    public static void Uninstall()
    {
        lock (gate)
        {
            MethodInfo? scan = patched;
            if (scan == null) return;
            resting = false;
            try
            {
                harmony?.Unpatch(scan, HarmonyPatchType.Postfix, HarmonyId);
            }
            catch (Exception e)
            {
                logger?.Warning(
                    "Could not remove the chunk thread rest cleanly: {0}", e.Message);
            }
            Volatile.Write(ref patched, null);
            logger?.Notification("Chunk thread over this session: {0}",
                DescribeTotals(Read()));
        }
    }

    /// <summary>Switches resting for this session only; the config keeps the default.</summary>
    public static string SetResting(bool on, string who)
    {
        lock (gate)
        {
            if (patched == null)
            {
                return "[VintageHorizons] The chunk thread rest is not installed: "
                    + (NotInstalledReason ?? "this server has not started it") + ".";
            }
            if (resting == on)
            {
                return $"[VintageHorizons] The chunk thread rest is already {(on ? "on" : "off")}.";
            }
            resting = on;
            Interlocked.Increment(ref switches);
            logger?.Notification("Chunk thread rest switched {0} by {1}.",
                on ? "on" : "off", who);
            return $"[VintageHorizons] Chunk thread rest {(on ? "on" : "off")} for this session. "
                + "The hitch report's server-work line shows the difference each minute.";
        }
    }

    public static Counts Read() => new(
        Volatile.Read(ref epoch),
        Interlocked.Read(ref passes),
        Interlocked.Read(ref passesWithWork),
        Interlocked.Read(ref idlePasses),
        Interlocked.Read(ref rests),
        Interlocked.Read(ref allocatedOnThread),
        Interlocked.Read(ref switches));

    /// <summary>
    /// One report's worth, for the window between two readings: the rest's state, how often
    /// the thread went round with nothing to do, and exactly what it allocated meanwhile.
    /// </summary>
    public static string Describe(Counts start, Counts now, double seconds)
    {
        if (NotInstalledReason is { } reason) return $"game chunk thread: rest not installed ({reason})";
        if (now.Epoch == 0) return "game chunk thread: rest not installed";
        // Installed during the window, by a world starting: count from zero.
        if (start.Epoch != now.Epoch) start = new Counts(now.Epoch, 0, 0, 0, 0, 0, 0);
        if (seconds <= 0) seconds = 1;
        double idleRate = (now.IdlePasses - start.IdlePasses) / seconds;
        double workRate = (now.PassesWithWork - start.PassesWithWork) / seconds;
        double allocated = Math.Max(0, now.AllocatedOnThread - start.AllocatedOnThread) / (1024.0 * 1024.0) / seconds;
        string state = !Installed ? "rest removed" : resting ? "rest on" : "rest off";
        if (now.Switches != start.Switches) state += " (switched during this window)";
        return $"game chunk thread: {state}, {idleRate:0} idle passes/s ({now.Rests - start.Rests} rested), "
            + $"{workRate:0.0} passes/s found work, {allocated:0.0} MB/s allocated on it";
    }

    static string DescribeTotals(Counts total) =>
        $"{total.Passes} passes over its queue, {total.PassesWithWork} found work, "
        + $"{total.IdlePasses} found nothing where the game would not have slept, {total.Rests} rested, "
        + $"{total.AllocatedOnThread / (1024.0 * 1024.0):0} MB allocated on it";

    internal static MethodInfo? FindScan()
    {
        Type? supplier = typeof(ServerSystem).Assembly.GetType(SupplierTypeName);
        if (supplier == null || !typeof(ServerSystem).IsAssignableFrom(supplier)) return null;
        MethodInfo? scan = AccessTools.DeclaredMethod(supplier, ScanMethodName, Type.EmptyTypes);
        return scan != null && !scan.IsStatic && scan.ReturnType == typeof(bool) ? scan : null;
    }

    static void ResetCounts()
    {
        Interlocked.Exchange(ref passes, 0);
        Interlocked.Exchange(ref passesWithWork, 0);
        Interlocked.Exchange(ref idlePasses, 0);
        Interlocked.Exchange(ref rests, 0);
        Interlocked.Exchange(ref allocatedOnThread, 0);
        Interlocked.Exchange(ref switches, 0);
    }

    /// <summary>
    /// Runs on the chunk thread after each pass over its queue. <paramref name="__result"/>
    /// is true when the pass advanced a column, which the engine follows at once with another.
    /// </summary>
    internal static void AfterScan(bool __result, ServerSystem __instance)
    {
        try
        {
            passes++;
            allocatedOnThread = GC.GetAllocatedBytesForCurrentThread();
            if (__result)
            {
                passesWithWork++;
                return;
            }
            // A negative interval is the engine's "requests queued, one generation thread,
            // eight or more processors": it will not sleep. Anywhere else it already does.
            if (__instance.GetUpdateInterval() >= 0) return;
            idlePasses++;
            if (!resting) return;
            // The engine's own between-pass sleep, which it keeps current for every server thread.
            Thread.Sleep(Math.Max(1, ServerThread.SleepMs));
            rests++;
        }
        catch (Exception e)
        {
            // Never take the chunk thread down: stop resting, which is the game's own
            // behaviour, and say so once.
            resting = false;
            if (Interlocked.Exchange(ref failed, 1) == 0)
            {
                logger?.Error(
                    "The chunk thread rest failed and is off for this session; "
                    + "the game's chunk thread runs as it always has. {0}", e);
            }
        }
    }
}
