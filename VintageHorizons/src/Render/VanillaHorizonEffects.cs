using System.Reflection;
using System.Threading;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace VintageHorizons.Render;

/// <summary>
/// Removes two false edges from vanilla terrain without changing client settings,
/// atmosphere, shader assets, draw range or streaming.
///
/// Vintage Story exposes the ambient inputs but no event between a vanilla renderer's
/// shader activation and draw. A shader-activation hook avoids the per-uniform hot path,
/// and a second hook catches the liquid-depth program's explicit late distance upload.
/// Its liquid tessellator also substitutes an empty chunk for a neighbour that has not
/// arrived yet, then emits a vertical water side against that invented air. A third hook
/// rejects only those horizontal side faces at the boundary of a genuinely missing chunk.
/// All hooks survive shader reloads because they belong to stable engine methods rather
/// than GL objects. Each effect fails open independently.
/// </summary>
internal sealed class VanillaHorizonEffects : IDisposable
{
    internal const string HarmonyId = "vintagehorizons.horizoneffects";

    static VanillaHorizonEffects? active;

    readonly ICoreClientAPI capi;
    readonly Harmony harmony = new(HarmonyId);
    readonly List<MethodBase> patchedMethods = new();
    readonly HashSet<string> failedPasses = new(StringComparer.Ordinal);
    ILogger? logger;
    bool installed;
    int liquidBoundaryFailed;
    long suppressedLiquidBoundaryFaces;

    internal VanillaHorizonEffects(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    internal IReadOnlyList<MethodBase> PatchedMethods => patchedMethods;
    internal bool PassFailed(string passName) => failedPasses.Contains(passName);
    internal long SuppressedLiquidBoundaryFaces => Interlocked.Read(ref suppressedLiquidBoundaryFaces);
    internal bool LiquidBoundaryFailed => Volatile.Read(ref liquidBoundaryFailed) != 0;

    internal bool Install(ILogger logger)
    {
        if (installed) return true;
        this.logger = logger;

        MethodInfo? shaderUse = AccessTools.Method(
            typeof(ShaderProgramBase), nameof(ShaderProgramBase.Use));
        MethodInfo? liquidDistance = AccessTools.PropertySetter(
            typeof(ShaderProgramChunkliquiddepth),
            nameof(ShaderProgramChunkliquiddepth.ViewDistance));
        MethodInfo? liquidFace = AccessTools.DeclaredMethod(
            typeof(LiquidTesselator), "DrawLiquidBlockFace");
        MethodInfo? afterUse = AccessTools.Method(
            typeof(VanillaHorizonEffects), nameof(AfterShaderUse));
        MethodInfo? beforeLiquidDistance = AccessTools.Method(
            typeof(VanillaHorizonEffects), nameof(BeforeLiquidDistance));
        MethodInfo? beforeLiquidFace = AccessTools.Method(
            typeof(VanillaHorizonEffects), nameof(BeforeLiquidFace));

        if (Interlocked.CompareExchange(ref active, this, null) != null)
        {
            logger.Error(
                "Vintage Horizons found a second horizon-effects owner; vanilla horizon effects remain active.");
            return false;
        }

        TryInstallHook(shaderUse, afterUse, postfix: true, "terrain shader activation");
        TryInstallHook(
            liquidDistance, beforeLiquidDistance, postfix: false,
            "liquid-depth distance upload");
        TryInstallHook(
            liquidFace, beforeLiquidFace, postfix: false,
            "missing-neighbour liquid side face");

        if (patchedMethods.Count == 0)
        {
            Interlocked.CompareExchange(ref active, null, this);
            logger.Error(
                "Vintage Horizons could not install terrain-wall suppression; vanilla behavior remains active.");
            return false;
        }

        installed = true;
        logger.Notification(
            "Vanilla terrain render-threshold fade and false outer liquid walls suppressed; "
            + "atmospheric and non-terrain fades remain unchanged");
        return true;
    }

    void TryInstallHook(
        MethodInfo? target, MethodInfo? patch, bool postfix, string description)
    {
        if (target == null || patch == null)
        {
            logger?.Error(
                "Vintage Horizons could not find the {0} hook; that pass keeps vanilla behavior.",
                description);
            return;
        }

        try
        {
            var harmonyMethod = new HarmonyMethod(patch);
            if (postfix) harmony.Patch(target, postfix: harmonyMethod);
            else harmony.Patch(target, prefix: harmonyMethod);
            patchedMethods.Add(target);
        }
        catch (Exception e)
        {
            try
            {
                // Harmony patching is expected to be atomic, but exact-ID cleanup keeps a
                // partially applied target from escaping this independently failing seam.
                harmony.Unpatch(target, HarmonyPatchType.All, HarmonyId);
            }
            catch
            {
            }
            logger?.Error(
                "Vintage Horizons could not install the {0} hook; that pass keeps vanilla behavior: {1}",
                description, e.Message);
        }
    }

    static void AfterShaderUse(ShaderProgramBase __instance)
    {
        VanillaHorizonEffects? owner = Volatile.Read(ref active);
        owner?.TryMoveTerrainFade(__instance);
    }

    internal void TryMoveTerrainFade(ShaderProgramBase program)
    {
        string? passName = program.PassName;
        if (passName == null || !VanillaHorizonPolicy.UsesVanillaDistanceFade(passName)
            || failedPasses.Contains(passName))
        {
            return;
        }

        try
        {
            if (!program.HasUniform(VanillaHorizonPolicy.ViewDistanceUniform))
            {
                FailPass(passName, "the active shader variant has no viewDistance uniform");
                return;
            }

            float configuredDistance = capi.Settings.Int["viewDistance"];
            if (!VanillaHorizonPolicy.TryDistanceBeyondDrawableTerrain(
                    configuredDistance, out float replacement))
            {
                FailPass(passName, "the configured view distance is invalid");
                return;
            }

            program.Uniform(VanillaHorizonPolicy.ViewDistanceUniform, replacement);
        }
        catch (Exception e)
        {
            FailPass(passName, e.Message);
        }
    }

    static void BeforeLiquidDistance(
        ShaderProgramChunkliquiddepth __instance, ref float value)
    {
        VanillaHorizonEffects? owner = Volatile.Read(ref active);
        string? passName = __instance.PassName;
        if (owner == null || passName == null || passName != "chunkliquiddepth"
            || owner.failedPasses.Contains(passName))
        {
            return;
        }

        try
        {
            if (!VanillaHorizonPolicy.TryDistanceBeyondDrawableTerrain(
                    value, out float replacement))
            {
                owner.FailPass(passName, "the liquid-depth view distance is invalid");
                return;
            }

            value = replacement;
        }
        catch (Exception e)
        {
            owner.FailPass(passName, e.Message);
        }
    }

    /// <summary>
    /// Skip the vertical side of a vanilla liquid block only when it lies in a horizontal
    /// chunk boundary and the chunk across that boundary does not exist in the client map.
    ///
    /// The engine fills missing neighbours with its shared EmptyChunk before visible-face
    /// calculation. That is useful for solids, but for an ocean it creates a one-block-tall
    /// curtain around the streaming frontier. Seen nearly edge-on, the curtain is the broad,
    /// pale, chunk-stepped band reproduced with no LOD mod installed. A loaded empty chunk is
    /// deliberately not suppressed: that is real information and may describe a shoreline.
    /// </summary>
    static bool BeforeLiquidFace(TCTCache vars, int tileSide)
    {
        VanillaHorizonEffects? owner = Volatile.Read(ref active);
        return owner == null || owner.ShouldDrawLiquidFace(vars, tileSide);
    }

    internal bool ShouldDrawLiquidFace(TCTCache vars, int tileSide)
    {
        if (LiquidBoundaryFailed
            || !TryOutwardLiquidNeighborOffset(
                tileSide, vars.lx, vars.lz, out int dx, out int dz))
        {
            return true;
        }

        try
        {
            int chunkX = (vars.posX >> 5) + dx;
            int chunkY = (vars.posY >> 5) + vars.dimension * 1024;
            int chunkZ = (vars.posZ >> 5) + dz;
            if (capi.World.BlockAccessor.GetChunk(chunkX, chunkY, chunkZ) != null)
                return true;

            Interlocked.Increment(ref suppressedLiquidBoundaryFaces);
            return false;
        }
        catch (Exception e)
        {
            if (Interlocked.Exchange(ref liquidBoundaryFailed, 1) == 0)
            {
                try
                {
                    logger?.Error(
                        "Vintage Horizons left vanilla liquid boundary faces unchanged after "
                        + "the missing-neighbour check failed: {0}", e.Message);
                }
                catch
                {
                    // A diagnostic failure must not turn a fail-open tessellation callback
                    // into a chunk-meshing failure.
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Maps the liquid tessellator's horizontal tile sides to the adjacent chunk, but only
    /// when the liquid block is actually on that chunk boundary. Top, bottom, shorelines and
    /// all interior faces return false before any world lookup.
    /// </summary>
    internal static bool TryOutwardLiquidNeighborOffset(
        int tileSide, int localX, int localZ, out int dx, out int dz)
    {
        dx = 0;
        dz = 0;
        switch (tileSide)
        {
            case 0 when localZ == 0:  // north
                dz = -1;
                return true;
            case 1 when localX == 31: // east
                dx = 1;
                return true;
            case 2 when localZ == 31: // south
                dz = 1;
                return true;
            case 3 when localX == 0:  // west
                dx = -1;
                return true;
            default:
                return false;
        }
    }

    internal string DescribeLiquidBoundary() => LiquidBoundaryFailed
        ? "vanilla missing-neighbour water-wall guard failed open"
        : $"vanilla missing-neighbour water walls suppressed {SuppressedLiquidBoundaryFaces}";

    void FailPass(string passName, string reason)
    {
        if (!failedPasses.Add(passName)) return;

        try
        {
            logger?.Error(
                "Vintage Horizons left terrain pass '{0}' unchanged after its wall-suppression override failed: {1}",
                passName, reason);
        }
        catch
        {
            // A diagnostic failure must not turn a fail-open shader callback into a render failure.
        }
    }

    public void Dispose()
    {
        // Disable the behavior first. If teardown happens on an unusual engine thread and
        // Harmony refuses the structural unpatch, every subsequent call is still a no-op.
        Interlocked.CompareExchange(ref active, null, this);

        if (!installed) return;
        try
        {
            UnpatchInstalledMethods();
        }
        catch (Exception cleanupError)
        {
            // Active is already null, so even a structural hook Harmony could not remove
            // is inert. Do not let unusual engine-thread teardown skip later cleanup.
            try
            {
                logger?.Error(
                    "Vintage Horizons terrain-wall hook cleanup was refused but remains inactive: {0}",
                    cleanupError.Message);
            }
            catch
            {
            }
        }
        installed = false;
    }

    void UnpatchInstalledMethods()
    {
        foreach (MethodBase method in patchedMethods)
            harmony.Unpatch(method, HarmonyPatchType.All, HarmonyId);
        patchedMethods.Clear();
    }
}
