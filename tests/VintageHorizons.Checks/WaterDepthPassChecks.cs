using System.Reflection;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace VintageHorizons.Checks;

public static class WaterDepthPassChecks
{
    public static void Run(Check c)
    {
        // Verify against the installed registration, without constructing a game system.
        var constructor = typeof(SystemRenderTerrain).GetConstructors().Single();
        var instructions = PatchProcessor.GetOriginalInstructions(constructor);
        c.True(instructions.Any(i => i.operand is double d && d == 0.995),
            "the installed terrain renderer retains its Before-stage liquid-depth order");
        c.True(LodTerrainRenderer.PrepareBeforeOrder < 0.995
            && LodTerrainRenderer.WaterDepthBeforeOrder > 0.995,
            "mesh/projection preparation precedes vanilla water depth and cached water follows it");

        string root = GameAssemblies.RepoRoot;
        string renderer = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src", "Render", "LodTerrainRenderer.cs"));
        string pass = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src", "Render", "LodTerrainRenderer.WaterDepth.cs"));
        string body = ModShaderSource.Read(Path.Combine(root, "VintageHorizons", "assets", "dodenslandscapes", "shaderincludes", "lodterrainbody.fsh"));
        string vertex = File.ReadAllText(Path.Combine(root, "VintageHorizons", "assets", "dodenslandscapes", "shaders", "lodwaterdepth.vsh"));
        string fragment = File.ReadAllText(Path.Combine(root, "VintageHorizons", "assets", "dodenslandscapes", "shaders", "lodwaterdepth.fsh"));
        string vertexBody = File.ReadAllText(Path.Combine(root, "VintageHorizons", "assets", "dodenslandscapes", "shaderincludes", "lodwaterbody.vsh"));
        c.True(vertex.Contains("#include lodwaterbody.vsh")
            && fragment.Contains("#include lodterrainbody.fsh")
            && fragment.Contains("#define VH_WATER_DEPTH 1"),
            "depth and visible water share positions, wave displacement and ownership code");
        int colorInit = vertexBody.IndexOf("vec4 vhSurfaceColor = vertexColorIn;", StringComparison.Ordinal);
        int slotInit = vertexBody.IndexOf("vhBlockSlot = -1;", StringComparison.Ordinal);
        int sharedVertex = vertexBody.IndexOf("#include lodvertexsurface.vsh", StringComparison.Ordinal);
        c.True(colorInit > 0 && slotInit > colorInit && sharedVertex > slotInit,
            "expanded water initializes every value consumed by the shared terrain vertex body");
        int depth = body.IndexOf("// The same water mesh, warping and ownership");
        int colour = body.IndexOf("#else", depth);
        string depthBody = body[depth..colour];
        c.True(depthBody.Contains("if (ownershipBand != 1) discard;")
            && !depthBody.Contains("getUnderwaterMurkiness") && !depthBody.Contains("texture("),
            "depth output excludes thin cover and does not sample the image being written");
        c.True(body.IndexOf("if (maskEnabled == 1)") < depth,
            "depth coverage obeys the same ownership decision before any output");
        c.True(pass.Contains("EnumFrameBuffer.LiquidDepth")
            && pass.Contains("GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, target.FboId);")
            && !pass.Contains("GL.Clear("),
            "the pass appends to liquid depth without clearing vanilla or touching primary depth");
        c.True(pass.Contains("GL.DepthFunc(DepthFunction.Less);")
            && pass.Contains("GL.DepthMask(true);")
            && pass.Contains("GL.ColorMask(false, false, false, false);"),
            "only the nearest water depth is written, with colour writes disabled");
        c.True(pass.Contains("pipeline.Restore();")
            && pass.Contains("LodGlStateGuard.TryRestore(stateApi, state,")
            && pass.Contains("ShaderProgramBase.CurrentShaderProgram == waterDepthProg"),
            "the finally path restores pipeline and engine/program state");
        c.True(pass.Contains("!WaterGroundMurk || !WaterDepthCoverage")
            && pass.Contains("!WaterSurfaceVisible"),
            "the master murk and surface switches gate the whole extra pass");
        c.True(pass.Contains("waterDepthCompletedThisFrame = true;")
            && pass.Contains("ReportRequiredWaterFailure(\"cached water-depth draw failed:"),
            "a requested depth pass must complete or visibly suppress cached water");
        c.True(pass.Contains("foreach (long key in drawList)")
            && pass.Contains("waterMeshes.TryGetValue(key,"),
            "depth draws the prepared current-frame selection using the existing water resources");
        int prepare = renderer.IndexOf("void PrepareRenderFrame(");
        int draw = renderer.IndexOf("void RenderFrame(", prepare);
        int upload = renderer.IndexOf("UploadFinishedMeshes();", prepare);
        int projection = renderer.IndexOf("ApplyZFar();", prepare);
        int walk = renderer.IndexOf("foreach (long top in world.TopLevelKeys) CollectDrawNodes(top);", prepare);
        c.True(prepare < upload && upload < projection && projection < walk && walk < draw,
            "uploads, projection and one selection finish before either water depth or visible drawing");
        c.True(renderer.IndexOf("BuildDepthPyramid();", draw) > draw,
            "opaque occlusion capture stays in the original opaque callback after vanilla ground");
        string oit = File.ReadAllText(Path.Combine(root, "VintageHorizons/src/Render/LodTerrainRenderer.WaterOit.cs"));
        c.True(pass.Contains("beforeStageStart = frameTimeline.Begin();")
            && renderer.Contains("beforeStageTicks += Stopwatch.GetTimestamp() - drawStart")
            && oit.Contains("beforeStageTicks += Stopwatch.GetTimestamp() - started")
            && oit.Contains("frameTimeline.End(beforeStageStart, beforeStageStart + beforeStageTicks);"),
            "frame timing includes all mod callbacks without charging intervening vanilla rendering");
        c.True(pass.Contains("UnregisterRenderer(prepareStageRenderer, EnumRenderStage.Before)")
            && pass.Contains("UnregisterRenderer(waterDepthStageRenderer, EnumRenderStage.Before)"),
            "both additional callbacks unregister on world teardown");

        // Majority coverage (0.5.2-dev.25): a quarter texel took cached water from its centre
        // sample alone, so sub-texel water toggled with every one-pixel turn.
        string full = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src", "Render", "LodTerrainRenderer.WaterDepthFull.cs"));
        string resolve = File.ReadAllText(Path.Combine(root, "VintageHorizons", "assets", "dodenslandscapes", "shaders", "lodwaterresolve.fsh"));
        string ownership = File.ReadAllText(Path.Combine(root, "VintageHorizons", "assets", "dodenslandscapes", "shaderincludes", "lodownershiptest.fsh"));
        c.True(full.Contains("public bool WaterDepthMajority { get; set; } = true;"),
            "majority coverage is on by default and can be switched off to compare");
        c.True(full.Contains("EnumFrameBuffer.Primary") && full.Contains("GL.Clear(ClearBufferMask.DepthBufferBit);")
            && full.Contains("GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, waterFullDepthFramebuffer);"),
            "cached water is drawn at the primary framebuffer's size into a cleared image of our own");
        int drawn = pass.IndexOf("DrawWaterDepthMeshes(cullSq);\n            prog.Stop();", StringComparison.Ordinal);
        if (drawn < 0) drawn = pass.IndexOf("DrawWaterDepthMeshes(cullSq);\r\n            prog.Stop();", StringComparison.Ordinal);
        int resolved = pass.IndexOf("if (majority) ResolveWaterDepthMajority(target);", StringComparison.Ordinal);
        int repaired = pass.IndexOf("RepairWaterDepth(target);", StringComparison.Ordinal);
        c.True(drawn > 0 && resolved > drawn && repaired > resolved,
            "the quarter image is resolved after the full draw and before hole repair");
        c.True(resolve.Contains("if (total == 0 || water * 2 < total) discard;")
            && resolve.Contains("gl_FragDepth = sum / float(water);"),
            "a quarter texel takes cached water only where at least half its pixels are water");
        c.True(!full.Contains("GL.DepthFunc(") && !full.Contains("GL.Clear(ClearBufferMask.DepthBufferBit);\n        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, quarter"),
            "the resolve composes under the pass's Less test and never clears the quarter image");
        c.True(ownership.Contains("ivec2(gl_FragCoord.xy * vhWaterDepthTexelScale)")
            && pass.Contains("prog.Uniform(\"vhWaterDepthTexelScale\", texelScale);"),
            "the junction test reads the quarter snapshot at the right texel when drawing at full size");
        c.True(pass.Contains("waterCaptureDirectory != null && !majority"),
            "the capture's mask-only reference is not drawn into the quarter image ahead of a majority resolve");

        // Exact readers (0.5.2-dev.26): even resolved, the quarter image leaves a rim at every
        // shore, and cached ground under it also loses its fog.
        string shaders = Path.Combine(root, "VintageHorizons", "assets", "dodenslandscapes");
        string murk = File.ReadAllText(Path.Combine(shaders, "shaderincludes", "lodwatermurk.fsh"));
        string finish = File.ReadAllText(Path.Combine(shaders, "shaderincludes", "lodsurfacefinish.fsh"));
        string farseer = File.ReadAllText(Path.Combine(shaders, "shaders", "lodsilhouette.fsh"));
        string silhouettePass = File.ReadAllText(Path.Combine(root, "VintageHorizons", "src", "Render", "LodTerrainRenderer.Silhouette.cs"));
        c.True(murk.Contains("return min(vhVanillaWaterDepthAt(pixel), cached);")
            && murk.Contains("texelFetch(vhCachedWaterDepth,"),
            "exact readers take cached water from the full-resolution image and vanilla's from its own snapshot");
        c.True(murk.Contains("if (vhExactWaterDepth == 0) return getUnderwaterMurkiness();")
            && murk.Contains("if (vhExactWaterDepth == 0) return getSkyMurkiness();"),
            "without this frame's images the engine's own helpers answer, as before");
        c.True(!finish.Contains("getUnderwaterMurkiness()") && !finish.Contains("getSkyMurkiness()")
            && !farseer.Contains("getSkyMurkiness()"),
            "cached ground, its fade to the sky and Farseer all read the exact picture");
        c.True(full.Contains("WaterDepthMajority && waterDepthCompletedThisFrame")
            && full.Contains("&& waterJunctionSnapshotReady"),
            "the exact picture is used only when this frame drew both images");
        c.True(renderer.Contains("BindExactWaterDepth(target);")
            && silhouettePass.Contains("BindExactWaterDepth(p);"),
            "every program that reads it binds it before drawing");
        c.True(LodTerrainSamplerLayout.Bindings.Contains(("vhCachedWaterDepth", LodTerrainSamplerLayout.CachedWaterDepth)),
            "the full-resolution image has a reserved unit the engine cannot hand to a table");
    }
}
