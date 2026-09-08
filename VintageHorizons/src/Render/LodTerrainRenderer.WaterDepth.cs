using System.Diagnostics;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VintageHorizons;

public partial class LodTerrainRenderer
{
    // Vanilla builds liquid depth at Before/0.995. Prepare current meshes/projection
    // before that, then append cached water before either opaque ground renderer runs.
    internal const double PrepareBeforeOrder = 0.994;
    internal const double WaterDepthBeforeOrder = 0.996;
    BeforeStageRenderer? prepareStageRenderer;
    BeforeStageRenderer? waterDepthStageRenderer;
    IShaderProgram? waterDepthProg;
    bool waterDepthShaderOk;
    bool waterDepthFailed;
    bool waterDepthCompletedThisFrame;
    bool beforeStageRan;
    readonly LodWaterFrameState waterFrame = new();
    bool preparedFrameReady => waterFrame.IsPrepared(frameCounter, currentWorldEpoch());
    bool frameTimingOpen;
    long beforeStageStart;
    long beforeStageTicks;
    public int WaterDepthDraws { get; private set; }
    public string WaterDepthSkipReason { get; private set; } = "not run";
    bool waterJunctionDepth = true;
    public bool WaterJunctionDepth
    {
        get => waterJunctionDepth;
        set
        {
            if (waterJunctionDepth == value) return;
            waterJunctionDepth = value;
            gpuTelemetry.ResetWaterDepthInterval();
        }
    }
    public string WaterJunctionStatus { get; private set; } = "not run";
    bool waterJunctionFailed;
    bool waterJunctionSnapshotReady;

    sealed class BeforeStageRenderer(Action<float> render, double order) : IRenderer
    {
        public double RenderOrder => order;
        public int RenderRange => 9999;
        public void OnRenderFrame(float dt, EnumRenderStage stage) => render(dt);
        public void Dispose() { }
    }

    void RegisterWaterDepthStages()
    {
        prepareStageRenderer = new BeforeStageRenderer(PrepareBeforeFrame, PrepareBeforeOrder);
        waterDepthStageRenderer = new BeforeStageRenderer(CaptureWaterDepthStage, WaterDepthBeforeOrder);
        waterCaptureEndRenderer = new BeforeStageRenderer(FinishWaterFrame, 0.99);
        waterOitRenderer = new BeforeStageRenderer(DrawWaterOit, WaterOitOrder);
        capi.Event.RegisterRenderer(prepareStageRenderer, EnumRenderStage.Before, "vintagehorizons-prepare");
        capi.Event.RegisterRenderer(waterDepthStageRenderer, EnumRenderStage.Before, "vintagehorizons-waterdepth");
        capi.Event.RegisterRenderer(waterCaptureEndRenderer, EnumRenderStage.AfterOIT, "vintagehorizons-watercapture");
        capi.Event.RegisterRenderer(waterOitRenderer, EnumRenderStage.OIT, "vintagehorizons-water");
    }

    void UnregisterWaterDepthStages()
    {
        if (prepareStageRenderer != null)
            capi.Event.UnregisterRenderer(prepareStageRenderer, EnumRenderStage.Before);
        if (waterDepthStageRenderer != null)
            capi.Event.UnregisterRenderer(waterDepthStageRenderer, EnumRenderStage.Before);
        prepareStageRenderer = waterDepthStageRenderer = null;
        if (waterCaptureEndRenderer != null)
            capi.Event.UnregisterRenderer(waterCaptureEndRenderer, EnumRenderStage.AfterOIT);
        waterCaptureEndRenderer = null;
        if (waterOitRenderer != null)
            capi.Event.UnregisterRenderer(waterOitRenderer, EnumRenderStage.OIT);
        waterOitRenderer = null;
        waterFrame.Invalidate();
        EndWaterFrameTiming();
        waterCaptureDirectory = null;
        DisposeWaterOitCoverageResources();
        DisposeWaterRepairResources();
    }

    void PrepareBeforeFrame(float dt)
    {
        EndWaterFrameTiming();
        beforeStageStart = frameTimeline.Begin();
        frameTimingOpen = true;
        beforeStageTicks = 0;
        beforeStageRan = true;
        WaterDepthDraws = 0;
        waterDepthCompletedThisFrame = false;
        WaterDepthSkipReason = "not run";
        WaterJunctionStatus = "depth pass not run";
        waterJunctionSnapshotReady = false;
        WaterOitDraws = 0;
        WaterOitSkipReason = "not selected";
        try
        {
            PrepareRenderFrame(dt);
            BeginWaterCapture();
        }
        finally { beforeStageTicks = Stopwatch.GetTimestamp() - beforeStageStart; }
    }

    void LoadWaterDepthShader()
    {
        waterDepthProg = capi.Shader.NewShaderProgram();
        waterDepthProg.AssetDomain = "vintagehorizons";
        waterDepthProg.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        waterDepthProg.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
        capi.Shader.RegisterFileShaderProgram("lodwaterdepth", waterDepthProg);
        waterDepthShaderOk = waterDepthProg.Compile();
        if (waterDepthShaderOk) LodTerrainSamplerLayout.Configure(waterDepthProg);
        LoadWaterRepairShader();
        waterDepthFailed = false;
        waterJunctionFailed = false;
        if (!ShaderLoadIsProvisional)
        {
            if (waterDepthShaderOk)
                capi.Logger.Notification("[VintageHorizons] cached water-depth shader ready.");
            else
                capi.Logger.Error(
                    "[VintageHorizons] cached water-depth shader unavailable; when murk/coverage "
                    + "is requested cached water will be suppressed instead of using vanilla-only depth.");
        }
    }

    void DrawWaterDepthBefore(float dt)
    {
        if (!preparedFrameReady || !WaterGroundMurk || !WaterDepthCoverage
            || !WaterSurfaceVisible || !waterDepthShaderOk || waterDepthFailed
            || waterDepthProg == null || waterDepthProg.LoadError || waterMeshes.Count == 0)
        {
            WaterDepthSkipReason = !preparedFrameReady ? "frame not prepared"
                : !WaterGroundMurk ? "murk off" : !WaterDepthCoverage ? "coverage off"
                : !WaterSurfaceVisible ? "surface off" : waterMeshes.Count == 0 ? "no meshes"
                : "depth shader unavailable";
            return;
        }
        // The engine's Before renderer stops its shader before returning. Do not take
        // over a program left active by another renderer.
        if (ShaderProgramBase.CurrentShaderProgram != null)
        {
            WaterDepthSkipReason = "another shader is active";
            return;
        }

        var buffers = capi.Render.FrameBuffers;
        int index = (int)EnumFrameBuffer.LiquidDepth;
        if (buffers == null || index >= buffers.Count)
        {
            WaterDepthSkipReason = "liquid framebuffer missing";
            return;
        }
        var target = buffers[index];
        if (target == null || target.FboId == 0 || target.DepthTextureId == 0
            || target.Width <= 0 || target.Height <= 0)
        {
            WaterDepthSkipReason = "liquid framebuffer invalid";
            return;
        }
        WaterDepthSkipReason = "none";

        long started = Stopwatch.GetTimestamp();
        var stateApi = new LodOpenGlStateApi();
        var state = LodGlStateGuard.Capture(stateApi,
            LodGlStateMask.Framebuffers | LodGlStateMask.VertexArray | LodGlStateMask.Program
            | LodGlStateMask.Texture2DUnit0);
        var pipeline = LodHzbPipelineState.Capture();
        IShaderProgram? previous = prog;
        bool previousBuilding = indirectPassActive;
        int previousCulled = culledThisFrame;
        int previousVerticalCulled = verticalCulledThisFrame;
        GL.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + LodTerrainSamplerLayout.VanillaWaterDepth));
        int previousJunctionTexture = GL.GetInteger(GetPName.TextureBinding2D);
        // Diagnostic readback stalls the GPU; keep capture frames out of timings.
        bool timeDepth = waterCaptureDirectory == null;
        if (timeDepth) gpuTelemetry.BeginWaterDepth();
        try
        {
            GL.Disable(EnableCap.ScissorTest);
            // Freeze vanilla's actual coverage before appending any cached geometry.
            // On allocation/copy failure retain the established mask-only depth path.
            bool junctionReady = false;
            WaterJunctionStatus = !WaterJunctionDepth ? "off" : "snapshot unavailable";
            if (WaterJunctionDepth && !waterJunctionFailed)
            {
                try
                {
                    SnapshotWaterDepth(target, vanillaOnly: true);
                    junctionReady = true;
                    waterJunctionSnapshotReady = true;
                    WaterJunctionStatus = "active";
                }
                catch (Exception e)
                {
                    waterJunctionFailed = true;
                    throw new InvalidOperationException(
                        "required water junction-depth snapshot failed", e);
                }
            }
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, target.FboId);
            GL.Viewport(0, 0, target.Width, target.Height);
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.ScissorTest);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Less);
            GL.DepthMask(true);
            GL.ColorMask(false, false, false, false);
            // Never clear: the nearest of existing vanilla water and surviving cached
            // water belongs in this separate image. Primary terrain depth is untouched.
            prog = waterDepthProg;
            indirectPassActive = false;
            prog.Use();
            ApplyFrameUniforms(prog, ApprovedViewDistance());
            prog.Uniform("maskDebug", 0);
            prog.Uniform("waterJunctionDepth", 0);
            if (junctionReady)
                prog.BindTexture2D("vanillaWaterDepth", waterVanillaDepthTexture,
                    LodTerrainSamplerLayout.VanillaWaterDepth);
            float cull = FarViewDistanceCap > 0
                ? FarViewDistanceCap + LodSection.SectionBlocks : float.MaxValue;
            float cullSq = cull == float.MaxValue ? float.MaxValue : cull * cull;
            // A requested capture also records the old mask-only result in this exact
            // frame. Ordinary frames need one mesh traversal. Depth-min composition
            // makes the diagnostic traversal observational: it cannot change the result.
            if (junctionReady && waterCaptureDirectory != null)
            {
                DrawWaterDepthMeshes(cullSq);
                CaptureWaterImage("02-mask-only-liquid-depth", EnumFrameBuffer.LiquidDepth, depth: true);
                WaterDepthDraws = 0;
            }
            prog.Uniform("waterJunctionDepth", junctionReady ? 1 : 0);
            DrawWaterDepthMeshes(cullSq);
            prog.Stop();
            CaptureWaterImage("02a-unrepaired-liquid-depth", EnumFrameBuffer.LiquidDepth, depth: true);
            RepairWaterDepth(target);
            if (!RequiredWaterDepthPrerequisitesAvailable(out string completionFailure))
                throw new InvalidOperationException(completionFailure);
            waterDepthCompletedThisFrame = true;
        }
        catch (Exception e)
        {
            waterDepthFailed = true;
            WaterDepthSkipReason = "depth draw failed";
            ReportRequiredWaterFailure("cached water-depth draw failed: " + e.Message);
        }
        finally
        {
            if (timeDepth) gpuTelemetry.EndWaterDepth();
            if (ShaderProgramBase.CurrentShaderProgram == waterDepthProg) waterDepthProg.Stop();
            prog = previous;
            indirectPassActive = previousBuilding;
            culledThisFrame = previousCulled;
            verticalCulledThisFrame = previousVerticalCulled;
            GL.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + LodTerrainSamplerLayout.VanillaWaterDepth));
            GL.BindTexture(TextureTarget.Texture2D, previousJunctionTexture);
            pipeline.Restore();
            if (!LodGlStateGuard.TryRestore(stateApi, state, out string restoreFailure))
            {
                waterDepthFailed = true;
                waterDepthCompletedThisFrame = false;
                ReportRequiredWaterFailure("water-depth state restore failed: " + restoreFailure);
            }
            beforeStageTicks += Stopwatch.GetTimestamp() - started;
        }
    }

    void DrawWaterDepthMeshes(float cullSq)
    {
        foreach (long key in drawList)
        {
            if (!waterMeshes.TryGetValue(key, out MeshRef? mesh)) continue;
            if (!SetupSectionTransform(key, cullSq, waterPass: true)) continue;
            capi.Render.RenderMesh(mesh);
            WaterDepthDraws++;
        }
    }
}
