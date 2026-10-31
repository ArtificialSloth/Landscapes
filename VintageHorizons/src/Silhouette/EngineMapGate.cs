using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;

namespace VintageHorizons.Silhouette;

/// <summary>
/// Makes Algernon's Terrain Sampler and the game's chunk thread take turns with the game's world
/// map layers.
///
/// Those layers are not safe to run twice at once. <c>NoiseLandforms.GetParentLandformIndexAt</c>
/// writes each variant's weight into a shared static list, sums them, then walks the list again
/// subtracting; a second thread rewriting the weights between the two loops can leave the walk
/// without an answer, and its last line then reads one past the end of the array. Every layer
/// also keeps its position seed on the one shared object, so an overlap that does not crash can
/// still pick the wrong landform. The game only ever runs them on its chunk thread, from
/// <c>GenMaps.OnMapRegionGen</c>. Algernon's sampler runs them from whichever thread calls it, and
/// its <c>lock (mapGen)</c> only keeps its own callers apart, because the game never takes that
/// lock. Our silhouette builder calls it on a background thread, so a map region created at the
/// wrong moment killed the chunk thread and with it the server (Akekho, 0.5.1).
///
/// Holding one lock around both sides closes every layer at once, leaves the game's code and its
/// results exactly as they were, and costs an uncontended lock per region. Watersheds needs none of
/// this: it replaces the game's map generation and gives each thread its own generators.
/// </summary>
internal static class EngineMapGate
{
    internal const string HarmonyId = "dodenslandscapes.enginemapgate";
    internal const string GenMapsTypeName = "Vintagestory.ServerMods.GenMaps";
    internal const string MapRegionMethodName = "OnMapRegionGen";

    static readonly object install = new();
    static Harmony? harmony;
    static MethodInfo? patched;

    /// <summary>Held while the game builds a map region, and around each sampler call.</summary>
    internal static readonly object MapLayers = new();

    /// <summary>
    /// Puts the game's map region generation behind <see cref="MapLayers"/> for this server's
    /// lifetime. False, having said why in the log, when this game's map generator is not the one
    /// this build knows; the caller must then not run the sampler on another thread.
    /// </summary>
    public static bool Install(Type? genMaps, ILogger logger)
    {
        lock (install)
        {
            if (patched != null) return true;

            MethodInfo? target = FindTarget(genMaps);
            if (target == null)
            {
                logger.Warning("Horizon silhouettes: this game's map generation is not the one "
                    + "this build knows, so Algernon's Terrain Sampler is not used and silhouettes "
                    + "build from engine previews.");
                return false;
            }

            try
            {
                harmony ??= new Harmony(HarmonyId);
                harmony.Patch(target,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(EngineMapGate), nameof(Enter))),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(EngineMapGate), nameof(Exit))));
            }
            catch (Exception e)
            {
                try
                {
                    harmony?.Unpatch(target, HarmonyPatchType.All, HarmonyId);
                }
                catch
                {
                }
                logger.Warning("Horizon silhouettes: could not protect the game's map generation "
                    + "({0}), so Algernon's Terrain Sampler is not used and silhouettes build from "
                    + "engine previews.", e.Message);
                return false;
            }

            Volatile.Write(ref patched, target);
            return true;
        }
    }

    /// <summary>The server is going away: remove the patch.</summary>
    public static void Uninstall(ILogger logger)
    {
        lock (install)
        {
            MethodInfo? target = patched;
            if (target == null) return;
            try
            {
                harmony?.Unpatch(target, HarmonyPatchType.All, HarmonyId);
            }
            catch (Exception e)
            {
                logger.Warning("Could not remove the map generation lock cleanly: {0}", e.Message);
            }
            Volatile.Write(ref patched, null);
        }
    }

    internal static bool Installed => Volatile.Read(ref patched) != null;

    internal static MethodInfo? FindTarget(Type? genMaps)
    {
        if (genMaps == null) return null;
        MethodInfo? method = AccessTools.DeclaredMethod(genMaps, MapRegionMethodName);
        return method != null && !method.IsStatic && method.ReturnType == typeof(void) ? method : null;
    }

    internal static void Enter(out bool __state)
    {
        __state = false;
        Monitor.Enter(MapLayers, ref __state);
    }

    internal static void Exit(bool __state)
    {
        if (__state) Monitor.Exit(MapLayers);
    }
}
