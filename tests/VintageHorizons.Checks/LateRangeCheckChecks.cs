using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.GameContent;

namespace VintageHorizons.Checks;

/// <summary>
/// The handoff-flash fix rests on three orders inside the engine's Before stage: the preparation
/// must precede vanilla's liquid-depth pass (0.995), the cached-water depth pass must follow it,
/// and the late range check must follow the player's physics step. That last order is read from
/// the installed engine itself, so a game update that moves the physics step fails here instead
/// of bringing the flash back in play.
/// </summary>
public static class LateRangeCheckChecks
{
    public static void Run(Check c)
    {
        TheLateCheckFollowsThePlayersPhysicsStep(c);
        TheWaterPassesKeepTheirPlaces(c);
        TheLateCheckChangesOnlyGroundThroughTheSweep(c);
    }

    static void TheLateCheckFollowsThePlayersPhysicsStep(Check c)
    {
        // RenderOrder is a constant property; nothing the constructor sets is read.
        var physics = (IRenderer)RuntimeHelpers.GetUninitializedObject(typeof(EntityBehaviorPlayerPhysics));
        double physicsOrder = physics.RenderOrder;
        c.Eq(1.0, physicsOrder, "the installed engine moves the player at Before/1.0");
        c.True(LodTerrainRenderer.PrepareBeforeOrder < physicsOrder,
            "the preparation runs before the player is moved - which is why the late check exists");
        c.True(LodTerrainRenderer.LateRangeBeforeOrder > physicsOrder,
            "the late range check runs after the player has been moved for this frame");
    }

    static void TheWaterPassesKeepTheirPlaces(Check c)
    {
        c.True(LodTerrainRenderer.PrepareBeforeOrder < 0.995,
            "the preparation still precedes vanilla's liquid-depth pass at 0.995");
        c.True(LodTerrainRenderer.WaterDepthBeforeOrder > 0.995,
            "the cached-water depth pass still follows it");
        c.True(LodTerrainRenderer.WaterDepthBeforeOrder < LodTerrainRenderer.LateRangeBeforeOrder,
            "and has already run when the late check does, which is why the late check leaves "
            + "water's footprint for the next preparation");
    }

    static void TheLateCheckChangesOnlyGroundThroughTheSweep(Check c)
    {
        string render = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src", "Render");
        string late = File.ReadAllText(Path.Combine(render, "LodTerrainRenderer.LateRangeCheck.cs"));
        string stages = File.ReadAllText(Path.Combine(render, "LodTerrainRenderer.WaterDepth.cs"));
        string main = File.ReadAllText(Path.Combine(render, "LodTerrainRenderer.cs"));

        c.True(late.Contains("SweepRangeLosses(frameCounter);"),
            "the late check withdraws ownership only through the range sweep's handshake");
        c.True(!late.Contains(".Set("), "and never writes a mask texel itself");
        c.True(late.Contains("PublishReadinessMask(rebuildWaterFootprint: false);"),
            "it publishes ground without rebuilding water's footprint");
        c.True(main.Contains("if (rebuildWaterFootprint && readiness != null"),
            "the footprint rebuild honours that");
        c.True(main.Contains("if (!readinessMask.Dirty && readinessMaskActive && !footprintRebuilt) return;"),
            "and a deferred footprint is uploaded by the next preparation even when nothing else changed");
        c.True(stages.Contains("new BeforeStageRenderer(RunLateRangeCheck, LateRangeBeforeOrder)"),
            "the late check is registered at its own order");
        c.True(stages.Contains("capi.Event.UnregisterRenderer(lateRangeRenderer, EnumRenderStage.Before);"),
            "and unregistered with the other Before-stage renderers");
    }
}
