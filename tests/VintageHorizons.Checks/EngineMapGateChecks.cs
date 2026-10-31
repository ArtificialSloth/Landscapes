using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using VintageHorizons.Silhouette;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;

namespace VintageHorizons.Checks;

/// <summary>
/// The lock between Algernon's Terrain Sampler and the game's map region generation (Akekho's
/// crash, 0.5.1). The method it guards must still exist in the installed game, Harmony must accept
/// the guard and remove it by exact id, a failing map layer must still fail as it would have and
/// release the lock, and the game's own landform picker must not crash with two callers once both
/// hold the lock.
/// </summary>
public static class EngineMapGateChecks
{
    public static void Run(Check c)
    {
        FindsTheGameMethod(c);
        RefusesAnUnknownGenerator(c);
        HoldsTheLockAndAlwaysReleasesIt(c);
        InstallsOnTheRealMethod(c);
        TheGamePickerSurvivesTwoCallersBehindTheLock(c);
    }

    sealed class FakeGenMaps
    {
        public bool HeldTheLock;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void OnMapRegionGen(IMapRegion mapRegion, int regionX, int regionZ, ITreeAttribute? chunkGenParams = null)
        {
            HeldTheLock = Monitor.IsEntered(EngineMapGate.MapLayers);
            if (regionX < 0) throw new InvalidOperationException("a map layer failed");
        }
    }

    static bool FreeOnAnotherThread()
    {
        bool free = false;
        var other = new Thread(() =>
        {
            free = Monitor.TryEnter(EngineMapGate.MapLayers);
            if (free) Monitor.Exit(EngineMapGate.MapLayers);
        });
        other.Start();
        other.Join();
        return free;
    }

    static void FindsTheGameMethod(Check c)
    {
        MethodInfo? target = EngineMapGate.FindTarget(typeof(GenMaps));
        c.True(target != null, "the installed game still builds map regions in the method the lock guards");
        c.Eq(typeof(GenMaps).FullName, EngineMapGate.GenMapsTypeName,
            "the mod system is found by the game's real type name");
    }

    static void RefusesAnUnknownGenerator(Check c)
    {
        var log = new CaptureLogger();
        c.False(EngineMapGate.Install(null, log), "with no map generator nothing is installed");
        c.False(EngineMapGate.Installed, "and it says so");
        c.True(log.Contains("Algernon's Terrain Sampler is not used"),
            "the caller's fallback to engine previews is named in the log");
    }

    static void HoldsTheLockAndAlwaysReleasesIt(Check c)
    {
        var log = new CaptureLogger();
        var maps = new FakeGenMaps();
        c.True(EngineMapGate.Install(typeof(FakeGenMaps), log), "Harmony accepts the guard");
        MethodInfo target = EngineMapGate.FindTarget(typeof(FakeGenMaps))!;
        try
        {
            Patches? patches = Harmony.GetPatchInfo(target);
            c.True(patches?.Prefixes.Any(p => p.owner == EngineMapGate.HarmonyId) == true
                && patches.Finalizers.Any(p => p.owner == EngineMapGate.HarmonyId),
                "the guard is owned by the exact id");

            maps.OnMapRegionGen(null!, 1, 1);
            c.True(maps.HeldTheLock, "the game's map region runs inside the lock");
            c.True(FreeOnAnotherThread(), "and the lock is free once it returns");

            bool threw = false;
            try
            {
                maps.OnMapRegionGen(null!, -1, 1);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            c.True(threw, "a failing map layer fails exactly as it would have");
            c.True(FreeOnAnotherThread(), "and cannot leave the lock held for the sampler to wait on");
        }
        finally
        {
            EngineMapGate.Uninstall(log);
        }

        c.False(EngineMapGate.Installed, "the guard removes cleanly");
        c.False(Harmony.GetPatchInfo(target)?.Owners.Contains(EngineMapGate.HarmonyId) == true,
            "and leaves the method without the owner");
        maps.OnMapRegionGen(null!, 1, 1);
        c.False(maps.HeldTheLock, "the method then runs as the game wrote it");
        c.NoThrow(() => EngineMapGate.Uninstall(log), "removal is idempotent");
    }

    static void InstallsOnTheRealMethod(Check c)
    {
        var log = new CaptureLogger();
        bool installed = false;
        c.NoThrow(() => installed = EngineMapGate.Install(typeof(GenMaps), log),
            "the guard installs against the current game assembly");
        c.True(installed && EngineMapGate.Installed, "Harmony accepts the real map region method");
        c.NoThrow(() => EngineMapGate.Uninstall(log), "and removes from it");
    }

    /// <summary>
    /// The exact crash, on the game's own code. Two variants that need opposite rainfall give two
    /// callers opposite weights, the condition that lets one walk the list with the other's
    /// numbers. Behind the lock no call may throw. The unguarded count is printed rather than
    /// asserted, because a race's hit rate depends on the machine.
    /// </summary>
    static void TheGamePickerSurvivesTwoCallersBehindTheLock(Check c)
    {
        LandformsWorldProperty saved = NoiseLandforms.landforms;
        try
        {
            NoiseLandforms.landforms = new LandformsWorldProperty
            {
                Variants = new[]
                {
                    new LandformVariant { index = 0, Weight = 1, UseClimateMap = true, MinRain = 0, MaxRain = 100 },
                    new LandformVariant { index = 1, Weight = 1, UseClimateMap = true, MinRain = 150, MaxRain = 255 },
                },
            };
            var noise = (NoiseLandforms)RuntimeHelpers.GetUninitializedObject(typeof(NoiseLandforms));

            int unguarded = Hammer(noise, gated: false);
            Console.WriteLine($"  [info] the game's landform picker, two callers, no lock: {unguarded} crashes in {Calls * 2} calls");
            c.Eq(0, Hammer(noise, gated: true), "the game's landform picker never crashes behind the lock");
        }
        finally
        {
            NoiseLandforms.landforms = saved;
        }
    }

    const int Calls = 1_000_000;

    static int Hammer(NoiseLandforms noise, bool gated)
    {
        int crashes = 0;
        using var start = new ManualResetEventSlim(false);
        void Caller(int rain)
        {
            start.Wait();
            for (int i = 0; i < Calls; i++)
            {
                try
                {
                    if (gated)
                    {
                        lock (EngineMapGate.MapLayers) noise.GetParentLandformIndexAt(i, rain, 0, rain);
                    }
                    else
                    {
                        noise.GetParentLandformIndexAt(i, rain, 0, rain);
                    }
                }
                catch (IndexOutOfRangeException)
                {
                    Interlocked.Increment(ref crashes);
                }
            }
        }

        var chunkThread = new Thread(() => Caller(50));
        var sampler = new Thread(() => Caller(200));
        chunkThread.Start();
        sampler.Start();
        start.Set();
        chunkThread.Join();
        sampler.Join();
        return crashes;
    }
}
