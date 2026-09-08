using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VintageHorizons.Checks;

public static class WaterOitChecks
{
    public static void Run(Check c)
    {
        var frame = new LodWaterFrameState();
        c.True(!frame.TryClaimOit(0, 1), "unprepared callbacks cannot draw stale meshes");
        foreach (bool requested in new[] { false, true })
        foreach (bool transparent in new[] { false, true })
        foreach (bool available in new[] { false, true })
        {
            frame.Prepare(10, 2, requested, transparent, available);
            c.Eq(requested && transparent && available, frame.UseOit,
                "the frame selects OIT only when every prerequisite is available");
            c.True(frame.IsPrepared(10, 2), "opaque/depth keep the current prepared selection even on fallback");
            c.True(!frame.TryClaimOit(10, 2), "OIT cannot run before opaque completes");
            frame.FinishOpaque(9, 2);
            c.True(!frame.TryClaimOit(10, 2), "an old opaque callback cannot unlock this frame");
            frame.FinishOpaque(10, 2);
            c.True(!frame.TryClaimOit(10, 3) && !frame.TryClaimOit(11, 2), "world/frame mismatch cannot consume the draw");
            c.Eq(requested && transparent && available, frame.TryClaimOit(10, 2), "only the selected pass draws");
            c.True(!frame.TryClaimOit(10, 2), "a duplicate OIT callback cannot blend twice");
        }
        frame.Prepare(11, 2, true, true, true);
        frame.FinishOpaque(11, 2);
        frame.Invalidate();
        c.True(!frame.TryClaimOit(11, 2) && !frame.IsPrepared(11, 2), "teardown/failed preparation invalidates deferred work");
        frame.Prepare(12, 3, true, true, true);
        frame.FinishOpaque(12, 3);
        c.True(frame.TryClaimOit(12, 3), "fresh preparation recovers after invalidation");

        c.True(typeof(ClientMain).GetField("doTransparentRenderPass") != null,
            "selection reads the installed engine's actual transparent-pass gate");
        c.True(LodTerrainRenderer.WaterOitOrder > 0.37
            && LodTerrainRenderer.WaterOitOrder < new SystemRenderOITLayers.AfterOIT().RenderOrder,
            "cached water sits after vanilla liquid and before engine OIT resolve");

        string root = GameAssemblies.RepoRoot;
        string file = Path.Combine(root, "VintageHorizons/src/Render/LodTerrainRenderer.WaterOit.cs");
        string oit = File.ReadAllText(file);
        c.True(!oit.Contains("GL.BlendFunc(") && !oit.Contains("rapi.GlToggleBlend(")
            && !oit.Contains("DrawLegacyWater();"), "OIT cannot replace indexed blending with straight alpha helpers");
        c.True(oit.Contains("GL.DepthMask(false);") && !oit.Contains("GL.DepthMask(true);"),
            "the OIT draw never enables primary depth writes");
        c.True(oit.Contains("waterOitTextures[i] = GL.GetInteger")
            && oit.Contains("GL.BindTexture(TextureTarget.Texture2D, waterOitTextures[i]);"),
            "program activation restores every sampler unit it can change");
        c.True(oit.IndexOf("TrySnapshotVanillaOitRevealage(target)", StringComparison.Ordinal)
                < oit.IndexOf("prog.Use();", StringComparison.Ordinal)
            && oit.Contains("GL.ReadBuffer(ReadBufferMode.ColorAttachment1);")
            && oit.Contains("ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest"),
            "OIT freezes vanilla's full-resolution revealage before cached water can alter it");
        c.True(oit.Contains("waterVanillaRevealageFramebuffer")
            && oit.Contains("FramebufferAttachment.ColorAttachment0")
            && oit.Contains("prog.BindTexture2D(\"vanillaOitRevealage\", waterVanillaRevealageTexture,")
            && !oit.Contains("prog.BindTexture2D(\"vanillaOitRevealage\", target.ColorTextureIds[1]"),
            "the shader samples an independent revealage copy rather than its live render target");
        c.True(oit.Contains("DisposeWaterOitCoverageResources()")
            && oit.Contains("GL.DeleteFramebuffer(waterVanillaRevealageFramebuffer)")
            && oit.Contains("GL.DeleteTexture(waterVanillaRevealageTexture)"),
            "the full-resolution coverage resources have an explicit disposal path");

        string shaders = Path.Combine(root, "VintageHorizons/assets/vintagehorizons/shaders");
        c.Eq(ModShaderSource.Read(Path.Combine(shaders, "lodwater.vsh")),
            ModShaderSource.Read(Path.Combine(shaders, "lodwateroit.vsh")),
            "opaque and OIT surface programs use identical vertex source");
        string wrapper = File.ReadAllText(Path.Combine(shaders, "lodwateroit.fsh"));
        c.True(wrapper.Contains("#include lodwaterfragment.fsh")
            && !wrapper.Contains("#define main") && !wrapper.Contains("vhShadeWater"),
            "OIT uses the shared fragment main directly instead of driver-sensitive function renaming");
        string expanded = ModShaderSource.Read(Path.Combine(shaders, "lodwateroit.fsh"));
        c.Eq(3, expanded.Split("OIT(outColor, vhWaterGlow.r);", StringSplitOptions.None).Length - 1,
            "ordinary output and both diagnostic early returns compose into OIT exactly once");
        c.True(!expanded.Contains("sampler2D depthTex") && !expanded.Contains("sampler2D primaryDepth"),
            "this stage-only step introduces no sampling of primary depth while it is attached");
        c.True(expanded.Contains("uniform sampler2D vanillaOitRevealage;")
            && expanded.Contains("uniform int waterJunctionSurface;"),
            "the OIT variant alone can use the immutable full-resolution surface signal");

        string surface = File.ReadAllText(Path.Combine(root,
            "VintageHorizons/src/Render/LodTerrainRenderer.WaterSurface.cs"));
        string mod = File.ReadAllText(Path.Combine(root,
            "VintageHorizons/src/VintageHorizonsModSystem.cs"));
        c.True(surface.Contains("public bool DedicatedWaterShader => true;")
            && !surface.Contains("DrawLegacyWater")
            && !mod.Contains("renderer.DedicatedWaterShader ="),
            "the legacy water shader cannot be selected by runtime code or chat command");
        c.True(surface.Contains("REQUIRED WATER PATH FAILED")
            && !oit.Contains("opaque next frame") && !surface.Contains("legacy next frame"),
            "water failures suppress the cached pass and never select an older pipeline");
    }
}
