using System.Reflection;
using HarmonyLib;
using VintageHorizons.Render;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace VintageHorizons.Checks;

public static class HorizonEffectsChecks
{
    public static void Run(Check c)
    {
        ProgramAllowlist(c);
        HookTargets(c);
        HookInstallation(c);
        FadeDistance(c);
        LiquidBoundaryFacePolicy(c);
        PerPassFailureIsolation(c);
    }

    static void HookInstallation(Check c)
    {
        ILogger logger = DispatchProxy.Create<ILogger, RecordingProxy>();
        var effects = new VanillaHorizonEffects(null!);
        bool installed = false;

        c.NoThrow(() => installed = effects.Install(logger),
            "the terrain-edge hooks install against the current game assembly without an OpenGL context");
        c.True(installed,
            "Harmony accepts the shader and liquid-tessellation hooks");
        c.Eq(3, effects.PatchedMethods.Count,
            "the installed hook set contains exactly the three terrain-edge seams");
        c.True(effects.PatchedMethods.Any(method =>
                method.DeclaringType == typeof(ShaderProgramBase)
                && method.Name == nameof(ShaderProgramBase.Use)),
            "the hook set contains shader activation");
        c.True(effects.PatchedMethods.Any(method =>
                method == typeof(ShaderProgramChunkliquiddepth).GetProperty(
                    nameof(ShaderProgramChunkliquiddepth.ViewDistance))?.SetMethod),
            "the hook set contains the liquid-depth late distance upload");
        c.True(effects.PatchedMethods.Any(method =>
                method.DeclaringType == typeof(LiquidTesselator)
                && method.Name == "DrawLiquidBlockFace"),
            "the hook set contains the missing-neighbour liquid side-face seam");
        c.False(effects.PatchedMethods.Any(method =>
                method.DeclaringType?.Name.Contains("Ambient", StringComparison.Ordinal) == true),
            "the actual hook set contains no ambient update patch");

        foreach (MethodBase method in effects.PatchedMethods)
        {
            c.True(Harmony.GetPatchInfo(method)?.Owners.Contains(
                    VanillaHorizonEffects.HarmonyId) == true,
                method.Name + " is owned by the exact Vintage Horizons Harmony id");
        }

        MethodBase[] hooked = effects.PatchedMethods.ToArray();
        c.NoThrow(effects.Dispose,
            "the exact Vintage Horizons hooks remove cleanly");
        foreach (MethodBase method in hooked)
        {
            c.False(Harmony.GetPatchInfo(method)?.Owners.Contains(
                    VanillaHorizonEffects.HarmonyId) == true,
                method.Name + " no longer has the Vintage Horizons owner after disposal");
        }
        c.NoThrow(effects.Dispose,
            "hook disposal is idempotent");
    }

    public class RecordingProxy : DispatchProxy
    {
        public int Errors;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ILogger.Error)) Errors++;
            if (targetMethod?.ReturnType == typeof(void) || targetMethod == null) return null;
            return targetMethod.ReturnType.IsValueType
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }

    static void HookTargets(Check c)
    {
        c.True(typeof(ShaderProgramBase).GetMethod(nameof(ShaderProgramBase.Use)) != null,
            "the installed engine exposes the shader-activation hook");
        c.True(typeof(ShaderProgramChunkliquiddepth).GetProperty(
                nameof(ShaderProgramChunkliquiddepth.ViewDistance))?.SetMethod != null,
            "the installed engine exposes the liquid-depth late distance upload");
        MethodInfo? liquidFace = typeof(LiquidTesselator).GetMethod(
            "DrawLiquidBlockFace", BindingFlags.Instance | BindingFlags.NonPublic);
        c.True(liquidFace != null,
            "the installed engine exposes the liquid face-emission seam");
        c.True(liquidFace?.GetParameters() is { Length: 12 } parameters
                && parameters[0].ParameterType == typeof(TCTCache)
                && parameters[1].ParameterType == typeof(int),
            "the liquid face-emission seam still begins with TCTCache and tileSide");
    }

    static void LiquidBoundaryFacePolicy(Check c)
    {
        CheckBoundary(c, tileSide: 0, localX: 17, localZ: 0, dx: 0, dz: -1, "north");
        CheckBoundary(c, tileSide: 1, localX: 31, localZ: 9, dx: 1, dz: 0, "east");
        CheckBoundary(c, tileSide: 2, localX: 4, localZ: 31, dx: 0, dz: 1, "south");
        CheckBoundary(c, tileSide: 3, localX: 0, localZ: 21, dx: -1, dz: 0, "west");

        foreach ((int side, int x, int z) in new[]
        {
            (0, 17, 1), (1, 30, 9), (2, 4, 30), (3, 1, 21),
            (4, 0, 0), (5, 31, 31), (-1, 0, 0), (6, 31, 31),
        })
        {
            c.False(VanillaHorizonEffects.TryOutwardLiquidNeighborOffset(
                    side, x, z, out _, out _),
                $"tile side {side} at local {x},{z} is not an outward horizontal chunk face");
        }
    }

    static void CheckBoundary(
        Check c, int tileSide, int localX, int localZ, int dx, int dz, string name)
    {
        c.True(VanillaHorizonEffects.TryOutwardLiquidNeighborOffset(
                tileSide, localX, localZ, out int actualDx, out int actualDz),
            $"the {name} liquid side on its chunk boundary checks the adjacent chunk");
        c.Eq(dx, actualDx, $"the {name} liquid side selects the correct X neighbour");
        c.Eq(dz, actualDz, $"the {name} liquid side selects the correct Z neighbour");
    }

    static void ProgramAllowlist(Check c)
    {
        string[] terrainWallPrograms =
        {
            "chunkopaque",
            "chunktopsoil",
            "chunktransparent",
            "chunkliquid",
            "chunkliquiddepth",
        };

        foreach (string pass in terrainWallPrograms)
        {
            c.True(VanillaHorizonPolicy.UsesVanillaDistanceFade(pass),
                pass + " has its terrain-edge fade moved beyond the drawable boundary");
        }

        foreach (string pass in new[]
        {
            "standard", "instanced", "entityanimated", "lodterrain",
            "lodterrainpacked", "sky", "particlescube", "",
        })
        {
            c.False(VanillaHorizonPolicy.UsesVanillaDistanceFade(pass),
                pass + " retains its vanilla distance behavior");
        }

        c.False(VanillaHorizonPolicy.UsesVanillaDistanceFade(null),
            "an unidentified shader is never rewritten");
        c.False(VanillaHorizonPolicy.UsesVanillaDistanceFade("Chunkopaque"),
            "the allowlist is exact rather than matching unrelated program names");
    }

    static void FadeDistance(Check c)
    {
        foreach (float configured in new[] { 32f, 64f, 256f, 1024f, 32768f })
        {
            float replacement =
                VanillaHorizonPolicy.DistanceBeyondDrawableTerrain(configured);
            double lastDrawableVertex = Math.Sqrt(
                    configured * configured + VanillaHorizonPolicy.RangePaddingSquared)
                + VanillaHorizonPolicy.HalfChunkDiagonal
                + VanillaHorizonPolicy.CameraPositionSlack;

            c.True(replacement * VanillaHorizonPolicy.EarliestTerrainFadeRatio
                    > lastDrawableVertex,
                $"{configured:0}-block view distance keeps every terrain fade beyond the last drawable vertex");
            c.True((replacement - 1f) * VanillaHorizonPolicy.EarliestTerrainFadeRatio
                    <= lastDrawableVertex,
                $"{configured:0}-block view distance uses the smallest safe whole-block replacement");
        }

        c.Eq(0f, VanillaHorizonPolicy.DistanceBeyondDrawableTerrain(0),
            "zero distance preserves the original value");
        c.Eq(-1f, VanillaHorizonPolicy.DistanceBeyondDrawableTerrain(-1),
            "negative distance preserves the original value");
        c.True(float.IsNaN(VanillaHorizonPolicy.DistanceBeyondDrawableTerrain(float.NaN)),
            "NaN distance preserves the original value");
        c.True(float.IsPositiveInfinity(
                VanillaHorizonPolicy.DistanceBeyondDrawableTerrain(float.PositiveInfinity)),
            "infinite distance preserves the original value");
        c.Eq(float.MaxValue,
            VanillaHorizonPolicy.DistanceBeyondDrawableTerrain(float.MaxValue),
            "an unrepresentable replacement preserves the original finite value");
        c.False(VanillaHorizonPolicy.TryDistanceBeyondDrawableTerrain(
                float.NaN, out float invalidReplacement),
            "invalid input is reported to the adapter for one-time diagnosis");
        c.True(float.IsNaN(invalidReplacement),
            "reported invalid input still preserves its original value");
    }

    static void PerPassFailureIsolation(Check c)
    {
        ILogger logger = DispatchProxy.Create<ILogger, RecordingProxy>();
        var recorder = (RecordingProxy)logger;
        var effects = new VanillaHorizonEffects(null!);
        c.True(effects.Install(logger),
            "failure-isolation fixture installs the real hooks");

        var missingUniform = new ShaderProgramChunkopaque { PassName = "chunkopaque" };
        c.NoThrow(() => effects.TryMoveTerrainFade(missingUniform),
            "a missing terrain uniform fails open without escaping the render callback");
        c.True(effects.PassFailed("chunkopaque"),
            "the incompatible terrain pass is disabled");
        c.False(effects.PassFailed("chunktransparent"),
            "a missing opaque pass does not disable transparent terrain");
        int firstWarningCount = recorder.Errors;
        c.NoThrow(() => effects.TryMoveTerrainFade(missingUniform),
            "a disabled pass remains a no-op on later activations");
        c.Eq(firstWarningCount, recorder.Errors,
            "an incompatible pass is diagnosed only once");

        var failingUpload = new ShaderProgramChunktopsoil { PassName = "chunktopsoil" };
        failingUpload.uniformLocations[VanillaHorizonPolicy.ViewDistanceUniform] = 0;
        c.NoThrow(() => effects.TryMoveTerrainFade(failingUpload),
            "a pass-specific callback failure preserves vanilla behavior without escaping");
        c.True(effects.PassFailed("chunktopsoil"),
            "the failing topsoil pass is disabled independently");
        c.False(effects.PassFailed("chunkliquid"),
            "a topsoil failure cannot affect the liquid terrain pass");

        var generalObject = new ShaderProgramStandard { PassName = "standard" };
        generalObject.uniformLocations[VanillaHorizonPolicy.ViewDistanceUniform] = 0;
        c.NoThrow(() => effects.TryMoveTerrainFade(generalObject),
            "a general-object shader is rejected before any settings or GL access");
        c.False(effects.PassFailed("standard"),
            "a rejected non-terrain pass never enters failure state");

        effects.Dispose();
    }
}
