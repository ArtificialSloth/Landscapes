using System.Diagnostics;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VintageHorizons;

public partial class LodTerrainRenderer
{
    internal const double WaterOitOrder = 0.38;
    BeforeStageRenderer? waterOitRenderer;
    IShaderProgram? waterOitProg;
    bool waterOitShaderOk;
    bool waterOitFailed;
    bool previousWaterOit;
    readonly int[] waterOitTextures = new int[LodTerrainSamplerLayout.Bindings.Length];
    int waterVanillaRevealageTexture;
    int waterVanillaRevealageFramebuffer;
    int waterVanillaRevealageWidth;
    int waterVanillaRevealageHeight;
    int waterVanillaRevealageFormat;
    bool waterJunctionSurfaceFailed;

    public bool WaterOitEnabled { get; set; } = true;
    public string WaterPipelineStatus { get; private set; } = "not prepared";
    public string WaterOitSkipReason { get; private set; } = "not selected";
    public string WaterJunctionSurfaceStatus { get; private set; } = "not run";
    public int WaterOitDraws { get; private set; }

    void LoadWaterOitShader()
    {
        waterOitProg = capi.Shader.NewShaderProgram();
        waterOitProg.AssetDomain = "dodenslandscapes";
        waterOitProg.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        waterOitProg.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
        capi.Shader.RegisterFileShaderProgram("lodwateroit", waterOitProg);
        waterOitShaderOk = waterOitProg.Compile();
        if (waterOitShaderOk) LodTerrainSamplerLayout.Configure(waterOitProg);
        waterOitFailed = false;
        waterJunctionSurfaceFailed = false;
        if (waterOitShaderOk)
            capi.Logger.Notification(
                "[Landscapes] dedicated water OIT shader ready; fallback disabled.");
        else
            capi.Logger.Error(
                "[Landscapes] dedicated water OIT shader failed to compile; an OIT "
                + "request will suppress cached water instead of selecting opaque rendering.");
    }

    void PrepareWaterSurfaceStage()
    {
        var buffers = capi.Render.FrameBuffers;
        var target = buffers != null && buffers.Count > (int)EnumFrameBuffer.Transparent
            ? buffers[(int)EnumFrameBuffer.Transparent] : null;
        bool transparent = capi.World is ClientMain client && client.doTransparentRenderPass;
        bool available = waterOitShaderOk && !waterOitFailed && waterOitProg != null
            && !waterOitProg.LoadError && target != null && !target.Disposed
            && target.FboId != 0 && target.Width > 0 && target.Height > 0;
        bool depthReady = RequiredWaterDepthPrerequisitesAvailable(out string depthFailure);
        waterPipelineFailedThisFrame = WaterOitEnabled
            && (!transparent || !available || !depthReady);
        waterFrame.Prepare(frameCounter, currentWorldEpoch(),
            WaterOitEnabled, transparent, available && depthReady);
        WaterPipelineStatus = waterFrame.UseOit ? "oit"
            : !WaterOitEnabled ? "opaque (requested)"
            : !depthReady ? "FAILED: " + depthFailure
            : !transparent ? "FAILED: engine transparency is disabled"
            : "FAILED: dedicated OIT is unavailable";
        if (waterPipelineFailedThisFrame)
            ReportRequiredWaterFailure(!depthReady ? depthFailure
                : !transparent ? "engine transparency is disabled while OIT is requested"
                : "the dedicated OIT shader/framebuffer is unavailable");
        if (previousWaterOit != waterFrame.UseOit)
        {
            gpuTelemetry.ResetWaterInterval();
            previousWaterOit = waterFrame.UseOit;
            capi.Logger.Notification("[Landscapes] water pipeline: {0}; surface timing interval restarted.", WaterPipelineStatus);
        }
        if (waterFrame.UseOit) WaterOitSkipReason = "awaiting OIT";
        WaterJunctionSurfaceStatus = waterFrame.UseOit ? "awaiting OIT" : "opaque pipeline";
    }

    void DrawWaterOit(float dt)
    {
        if (!waterFrame.TryClaimOit(frameCounter, currentWorldEpoch())) return;
        long started = Stopwatch.GetTimestamp();
        try
        {
            if (!RequiredWaterDepthCompleted(out string depthFailure))
                throw new InvalidOperationException(depthFailure);
            var target = capi.Render.FrameBuffers[(int)EnumFrameBuffer.Transparent];
            if (waterOitProg == null || waterOitProg.LoadError || target.Disposed
                || GL.GetInteger(GetPName.DrawFramebufferBinding) != target.FboId)
                throw new InvalidOperationException("OIT framebuffer/program changed after preparation");
            if (ShaderProgramBase.CurrentShaderProgram != null)
                throw new InvalidOperationException("another shader is still active at water OIT");
            if (!GL.IsEnabled(EnableCap.Blend) || !GL.IsEnabled(EnableCap.DepthTest))
                throw new InvalidOperationException("engine OIT blend/depth state unavailable");
            ErrorCode incomingError = GL.GetError();
            if (incomingError != ErrorCode.NoError)
                throw new InvalidOperationException("GL error already present before water OIT: " + incomingError);

            CaptureWaterOitRevealage("05a-vanilla-oit-revealage", target);

            var stateApi = new LodOpenGlStateApi();
            var state = LodGlStateGuard.Capture(stateApi, LodGlStateMask.Program | LodGlStateMask.VertexArray);
            bool cull = GL.IsEnabled(EnableCap.CullFace);
            bool depthWrite = GL.GetBoolean(GetPName.DepthWritemask);
            int activeTexture = GL.GetInteger(GetPName.ActiveTexture);
            int savedTextures = 0;
            IShaderProgram? previous = prog;
            bool previousIndirect = indirectPassActive;
            bool waterTimingStarted = false;
            try
            {
                for (int i = 0; i < waterOitTextures.Length; i++)
                {
                    GL.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + LodTerrainSamplerLayout.Bindings[i].Unit));
                    waterOitTextures[i] = GL.GetInteger(GetPName.TextureBinding2D);
                    savedTextures++;
                }
                gpuTelemetry.BeginWater();
                waterTimingStarted = true;

                bool junctionReady = waterJunctionSnapshotReady
                    && WaterJunctionDepth && waterVanillaDepthTexture != 0;
                if (!junctionReady)
                    WaterJunctionSurfaceStatus = WaterJunctionDepth
                        ? "depth unavailable" : "off";
                bool surfaceJunctionReady = junctionReady
                    && TrySnapshotVanillaOitRevealage(target);
                // Preserve the engine's indexed blend functions and draw buffers.
                // No ordinary GlToggleBlend call here: it replaces those functions.
                GL.Disable(EnableCap.CullFace);
                GL.DepthMask(false);
                prog = waterOitProg;
                indirectPassActive = false;
                prog.Use();
                ApplyFrameUniforms(prog, ApprovedViewDistance());
                RebindOwnSamplers(prog);
                prog.Uniform("waterJunctionDepth", junctionReady ? 1 : 0);
                prog.Uniform("waterJunctionSurface", surfaceJunctionReady ? 1 : 0);
                if (junctionReady)
                    prog.BindTexture2D("vanillaWaterDepth", waterVanillaDepthTexture,
                        LodTerrainSamplerLayout.VanillaWaterDepth);
                if (surfaceJunctionReady)
                    prog.BindTexture2D("vanillaOitRevealage", waterVanillaRevealageTexture,
                        LodTerrainSamplerLayout.VanillaOitRevealage);
                foreach (long key in drawList)
                {
                    if (!waterMeshes.TryGetValue(key, out MeshRef? mesh)) continue;
                    if (!SetupSectionTransform(key, renderCullDistanceSquared, waterPass: true)) continue;
                    SubmitWaterMesh(key, mesh);
                    WaterOitDraws++;
                }
                ErrorCode drawError = GL.GetError();
                if (drawError != ErrorCode.NoError)
                    throw new InvalidOperationException("water OIT GL error: " + drawError);
                gpuTelemetry.EndWater();
                waterTimingStarted = false;
                CaptureWaterOitRevealage("05b-combined-oit-revealage", target);
                WaterOitSkipReason = "none";
                ClearRequiredWaterFailure();
            }
            finally
            {
                if (waterTimingStarted) gpuTelemetry.EndWater();
                if (ShaderProgramBase.CurrentShaderProgram == waterOitProg) waterOitProg.Stop();
                prog = previous;
                indirectPassActive = previousIndirect;
                GL.DepthMask(depthWrite);
                if (cull) GL.Enable(EnableCap.CullFace); else GL.Disable(EnableCap.CullFace);
                for (int i = 0; i < savedTextures; i++)
                {
                    GL.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + LodTerrainSamplerLayout.Bindings[i].Unit));
                    GL.BindTexture(TextureTarget.Texture2D, waterOitTextures[i]);
                }
                GL.ActiveTexture((TextureUnit)activeTexture);
                if (!LodGlStateGuard.TryRestore(stateApi, state, out string failure))
                    throw new InvalidOperationException("water OIT state restore: " + failure);
            }
        }
        catch (Exception e)
        {
            waterOitFailed = true;
            WaterOitSkipReason = e.Message;
            ReportRequiredWaterFailure("dedicated OIT draw failed: " + e.Message);
        }
        finally { beforeStageTicks += Stopwatch.GetTimestamp() - started; }
    }

    /// <summary>
    /// Copy vanilla's full-resolution ordinary revealage after its liquid draw. The live
    /// texture is attached to the framebuffer this method returns to, so the cached shader
    /// must never sample it directly. A failed optional copy retains the quarter-resolution
    /// depth junction and leaves ordinary ownership unchanged.
    /// </summary>
    bool TrySnapshotVanillaOitRevealage(FrameBufferRef target)
    {
        if (waterJunctionSurfaceFailed)
        {
            WaterJunctionSurfaceStatus = "unavailable";
            throw new InvalidOperationException(
                "required full-resolution water junction surface snapshot is unavailable");
        }

        try
        {
            SnapshotVanillaOitRevealage(target);
            WaterJunctionSurfaceStatus = "active";
            return true;
        }
        catch (Exception e)
        {
            waterJunctionSurfaceFailed = true;
            WaterJunctionSurfaceStatus = "unavailable";
            throw new InvalidOperationException(
                "required full-resolution water junction surface snapshot failed", e);
        }
    }

    void SnapshotVanillaOitRevealage(FrameBufferRef target)
    {
        if (target.ColorTextureIds == null || target.ColorTextureIds.Length < 2
            || target.ColorTextureIds[1] == 0)
        {
            throw new InvalidOperationException("OIT revealage attachment is unavailable");
        }

        ErrorCode incoming = GL.GetError();
        if (incoming != ErrorCode.NoError)
            throw new InvalidOperationException(
                "GL error already present before OIT revealage snapshot: " + incoming);

        int previousReadFramebuffer = GL.GetInteger(GetPName.ReadFramebufferBinding);
        int previousDrawFramebuffer = GL.GetInteger(GetPName.DrawFramebufferBinding);
        int previousReadBuffer = GL.GetInteger(GetPName.ReadBuffer);
        int activeTexture = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0);
        int previousTexture = GL.GetInteger(GetPName.TextureBinding2D);
        try
        {
            GL.BindTexture(TextureTarget.Texture2D, target.ColorTextureIds[1]);
            GL.GetTexLevelParameter(TextureTarget.Texture2D, 0,
                GetTextureParameter.TextureInternalFormat, out int format);
            if (waterVanillaRevealageTexture == 0
                || waterVanillaRevealageWidth != target.Width
                || waterVanillaRevealageHeight != target.Height
                || waterVanillaRevealageFormat != format)
            {
                DisposeWaterOitCoverageResources();
                waterVanillaRevealageTexture = GL.GenTexture();
                waterVanillaRevealageFramebuffer = GL.GenFramebuffer();
                AllocateWaterOitRevealageSnapshot(target, format);
                waterVanillaRevealageWidth = target.Width;
                waterVanillaRevealageHeight = target.Height;
                waterVanillaRevealageFormat = format;
            }

            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, target.FboId);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment1);
            GL.BindFramebuffer(
                FramebufferTarget.DrawFramebuffer, waterVanillaRevealageFramebuffer);
            GL.BlitFramebuffer(0, 0, target.Width, target.Height,
                0, 0, target.Width, target.Height,
                ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            ErrorCode copied = GL.GetError();
            if (copied != ErrorCode.NoError)
                throw new InvalidOperationException("OIT revealage snapshot copy failed: " + copied);
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, previousReadFramebuffer);
            GL.ReadBuffer((ReadBufferMode)previousReadBuffer);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, previousDrawFramebuffer);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, previousTexture);
            GL.ActiveTexture((TextureUnit)activeTexture);
        }
    }

    void AllocateWaterOitRevealageSnapshot(FrameBufferRef target, int format)
    {
        GL.BindTexture(TextureTarget.Texture2D, waterVanillaRevealageTexture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, (PixelInternalFormat)format,
            target.Width, target.Height, 0, PixelFormat.Red, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
        GL.BindFramebuffer(
            FramebufferTarget.Framebuffer, waterVanillaRevealageFramebuffer);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D,
            waterVanillaRevealageTexture, 0);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer)
            != FramebufferErrorCode.FramebufferComplete)
        {
            throw new InvalidOperationException("OIT revealage snapshot framebuffer incomplete");
        }
    }

    void DisposeWaterOitCoverageResources()
    {
        if (waterVanillaRevealageFramebuffer != 0)
            GL.DeleteFramebuffer(waterVanillaRevealageFramebuffer);
        if (waterVanillaRevealageTexture != 0)
            GL.DeleteTexture(waterVanillaRevealageTexture);
        waterVanillaRevealageFramebuffer = waterVanillaRevealageTexture = 0;
        waterVanillaRevealageWidth = waterVanillaRevealageHeight = waterVanillaRevealageFormat = 0;
    }

    void EndWaterFrameTiming()
    {
        if (!frameTimingOpen) return;
        frameTimeline.End(beforeStageStart, beforeStageStart + beforeStageTicks);
        frameTimingOpen = false;
    }

    void FinishWaterFrame(float dt)
    {
        if (waterFrame.UseOit && WaterOitSkipReason == "awaiting OIT")
        {
            waterOitFailed = true;
            WaterOitSkipReason = "OIT callback missing";
            ReportRequiredWaterFailure("the dedicated OIT callback did not run");
        }
        EndWaterFrameTiming();
        FinishWaterCapture(dt);
        waterFrame.Invalidate();
    }

    public string DescribeWaterPipeline()
    {
        gpuTelemetry.RequestTiming();
        return $"{WaterPipelineStatus}; OIT draws {WaterOitDraws}, skip {WaterOitSkipReason}; "
            + $"junction depth {WaterJunctionStatus}, surface fill {WaterJunctionSurfaceStatus}; "
            + $"GPU surface p95 {gpuTelemetry.WaterCost.P95Us:0} us ({gpuTelemetry.WaterCost.Calls} samples), "
            + $"depth+repair p95 {gpuTelemetry.WaterDepthCost.P95Us:0} us ({gpuTelemetry.WaterDepthCost.Calls} samples); "
            + $"timing {gpuTelemetry.DescribeTiming()}";
    }
}
