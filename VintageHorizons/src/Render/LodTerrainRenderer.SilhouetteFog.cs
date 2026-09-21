using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VintageHorizons.Silhouette;

namespace VintageHorizons;

public partial class LodTerrainRenderer
{
    readonly record struct SilhouetteFogBuildOutcome(
        long Revision, LodSilhouetteFogFieldArrays? Field, string Failure, bool Cancelled);

    internal const double SilhouetteFogAfterOitOrder = 1.0;

    /// <summary>
    /// Ground speed of the billows at drift speed one. The pattern is anchored to the world, so
    /// this is real blocks per second. 0.4.109 and earlier moved it by 0.006 noise units per
    /// second, which at any usable billow size is under a block per second and invisible; the
    /// only motion anyone ever saw was the pattern jumping as the dial itself moved.
    /// </summary>
    internal const float SilhouetteFogDriftBlocksPerSecond = 6f;

    /// <summary>One frame may not teleport the billows after a pause, a load or an alt-tab.</summary>
    internal const float SilhouetteFogMaxDriftStepSeconds = 0.25f;

    /// <summary>Wrapped so a long session cannot erode the noise interpolation's precision.</summary>
    internal const float SilhouetteFogDriftWrapBlocks = 1 << 20;
    BeforeStageRenderer? silhouetteFogStageRenderer;
    IShaderProgram? silhouetteFogVolumeProg;
    int silhouetteFogFieldTexture;
    int silhouetteFogVao;
    bool silhouetteFogVolumeFailed;
    int silhouetteFogFieldOriginX;
    int silhouetteFogFieldOriginZ;
    int silhouetteFogFieldWidth;
    int silhouetteFogFieldHeight;
    float silhouetteFogFieldTexelBlocks;
    float silhouetteFogMinGroundY;
    float silhouetteFogMaxGroundY;
    int silhouetteFogBoundaryEdges;
    int silhouetteFogFramebuffer;
    int silhouetteFogAttachedColor;
    bool silhouetteFogGeometryDirty;
    Task<SilhouetteFogBuildOutcome>? silhouetteFogBuildTask;
    CancellationTokenSource? silhouetteFogBuildCancellation;
    LodSilhouetteFogVolume.Prepared? silhouetteFogPendingBuild;
    long silhouetteFogBuildRevision;
    long silhouetteFogPendingRevision;
    int silhouetteFogErrorChecksLeft = 240;
    bool silhouetteFogReportedPendingError;
    float silhouetteFogDriftBlocks;
    int silhouetteFogNoiseTexture;
    int silhouetteFogNoiseProgram;
    int silhouetteFogNoiseLocation = -1;
    float silhouetteFogCoreBlocks = LodSilhouetteFogVolume.MinCoreBlocks;
    float silhouetteFogStaircaseDeviation;

    /// <summary>
    /// The player's seam-fog switch. Off draws no fog pass and builds no fog field, so the
    /// difference in frame time is the fog's whole cost; Farseer is then not held for a field.
    /// </summary>
    internal bool SilhouetteFogEnabled = true;
    internal float SilhouetteFogCoreBlocks => silhouetteFogCoreBlocks;
    internal float SilhouetteFogStaircaseDeviation => silhouetteFogStaircaseDeviation;
    internal float SilhouetteFogStrength = LodSilhouetteFogVolume.DefaultStrength;
    internal float SilhouetteFogCoverage = LodSilhouetteFogVolume.DefaultCoverage;
    internal float SilhouetteFogSpillBlocks = LodSilhouetteFogVolume.DefaultSpillBlocks;
    internal float SilhouetteFogHeightBlocks = LodSilhouetteFogVolume.DefaultHeightBlocks;
    internal float SilhouetteFogFloorDepthBlocks = LodSilhouetteFogVolume.DefaultFloorDepthBlocks;
    internal float SilhouetteFogBillowSizeBlocks = LodSilhouetteFogVolume.DefaultBillowSizeBlocks;
    internal float SilhouetteFogDriftSpeed = LodSilhouetteFogVolume.DefaultDriftSpeed;
    internal float SilhouetteFogLayerDetail = LodSilhouetteFogVolume.DefaultLayerDetail;
    internal float SilhouetteFogEdgeSoftness = LodSilhouetteFogVolume.DefaultEdgeSoftness;
    readonly LodOpenGlStateApi silhouetteFogStateApi = new();
    readonly int[] silhouetteFogViewport = new int[4];
    readonly bool[] silhouetteFogColorMask = new bool[4];
    readonly float[] silhouetteFogInvProjection = new float[16];
    readonly float[] silhouetteFogInvView = new float[16];

    void RegisterSilhouetteFogStage()
    {
        silhouetteFogStageRenderer = new BeforeStageRenderer(
            DrawSilhouetteFogVolume, SilhouetteFogAfterOitOrder);
        capi.Event.RegisterRenderer(silhouetteFogStageRenderer, EnumRenderStage.AfterOIT,
            "vintagehorizons-silhouettefog");
    }

    void UnregisterSilhouetteFogStage()
    {
        if (silhouetteFogStageRenderer == null) return;
        capi.Event.UnregisterRenderer(silhouetteFogStageRenderer, EnumRenderStage.AfterOIT);
        silhouetteFogStageRenderer = null;
    }

    void QueueSilhouetteFogVolume(LodSilhouetteClient silhouettes)
    {
        if (!SilhouetteFogEnabled || !silhouetteSeamMask.HasEdges)
        {
            ClearSilhouetteFogField();
            silhouetteSeamMask.MarkUploaded();
            return;
        }

        try
        {
            LodSilhouetteFogVolume.Prepared prepared = LodSilhouetteFogVolume.Prepare(
                silhouetteSeamMask, SilhouetteFogHeightAtGrid, SilhouetteFogSpillBlocks);
            silhouetteFogPendingBuild = prepared;
            silhouetteFogPendingRevision = ++silhouetteFogBuildRevision;
            silhouetteFogGeometryDirty = false;
            // One transform at a time. A moving visible window replaces the pending snapshot but
            // does not repeatedly cancel the running job and starve publication under motion.
            if (silhouetteFogBuildTask == null)
                StartPendingSilhouetteFogBuild();
        }
        catch (Exception e)
        {
            StopSilhouetteFog(silhouettes, e.Message);
        }
    }

    void StartPendingSilhouetteFogBuild()
    {
        LodSilhouetteFogVolume.Prepared? prepared = silhouetteFogPendingBuild;
        if (prepared == null || silhouetteFogBuildTask != null) return;
        long revision = silhouetteFogPendingRevision;
        silhouetteFogPendingBuild = null;
        silhouetteFogBuildCancellation = new CancellationTokenSource();
        CancellationToken token = silhouetteFogBuildCancellation.Token;
        silhouetteFogBuildTask = Task.Run(() =>
        {
            try
            {
                // Bake the noise here, once, so the render thread only ever uploads it.
                _ = LodSilhouetteFogNoise.Texels;
                return new SilhouetteFogBuildOutcome(
                    revision, LodSilhouetteFogVolume.Build(prepared, token), "", false);
            }
            catch (OperationCanceledException)
            {
                return new SilhouetteFogBuildOutcome(revision, null, "", true);
            }
            catch (Exception e)
            {
                return new SilhouetteFogBuildOutcome(revision, null, e.Message, false);
            }
        });
    }

    /// <summary>Main/render thread. Publishes only the newest completed immutable field.</summary>
    bool PublishSilhouetteFogVolume(LodSilhouetteClient silhouettes)
    {
        Task<SilhouetteFogBuildOutcome>? task = silhouetteFogBuildTask;
        if (task == null)
        {
            try { StartPendingSilhouetteFogBuild(); }
            catch (Exception e)
            {
                StopSilhouetteFog(silhouettes, e.Message);
                return false;
            }
            return true;
        }
        if (!task.IsCompleted) return true;

        SilhouetteFogBuildOutcome outcome = task.GetAwaiter().GetResult();
        silhouetteFogBuildCancellation?.Dispose();
        silhouetteFogBuildCancellation = null;
        silhouetteFogBuildTask = null;

        bool newest = outcome.Revision == silhouetteFogBuildRevision
            && silhouetteFogPendingBuild == null;
        if (newest && !outcome.Cancelled && outcome.Field == null)
        {
            StopSilhouetteFog(silhouettes, outcome.Failure);
            return false;
        }
        if (newest && outcome.Field != null)
        {
            try
            {
                PublishSilhouetteFogField(outcome.Field);
            }
            catch (Exception e)
            {
                StopSilhouetteFog(silhouettes, e.Message);
                return false;
            }
        }
        try { StartPendingSilhouetteFogBuild(); }
        catch (Exception e)
        {
            StopSilhouetteFog(silhouettes, e.Message);
            return false;
        }
        return true;
    }

    void PublishSilhouetteFogField(LodSilhouetteFogFieldArrays arrays)
    {
        int next = UploadSilhouetteFogField(arrays);
        if (silhouetteFogNoiseTexture == 0)
        {
            try
            {
                silhouetteFogNoiseTexture = UploadSilhouetteFogNoise();
            }
            catch
            {
                GL.DeleteTexture(next);
                throw;
            }
        }
        int old = silhouetteFogFieldTexture;
        silhouetteFogFieldTexture = next;
        silhouetteFogCoreBlocks = arrays.CoreBlocks;
        silhouetteFogStaircaseDeviation = arrays.StaircaseDeviationBlocks;
        silhouetteFogFieldOriginX = arrays.OriginBlockX;
        silhouetteFogFieldOriginZ = arrays.OriginBlockZ;
        silhouetteFogFieldWidth = arrays.Width;
        silhouetteFogFieldHeight = arrays.Height;
        silhouetteFogFieldTexelBlocks = arrays.TexelBlocks;
        silhouetteFogMinGroundY = arrays.MinGroundY;
        silhouetteFogMaxGroundY = arrays.MaxGroundY;
        silhouetteFogBoundaryEdges = arrays.BoundaryEdges;
        silhouetteSeamMask.MarkUploaded();
        if (old != 0) GL.DeleteTexture(old);
    }

    void CancelSilhouetteFogBuild()
    {
        silhouetteFogBuildRevision++;
        silhouetteFogPendingBuild = null;
        silhouetteFogBuildCancellation?.Cancel();
        silhouetteFogBuildCancellation?.Dispose();
        silhouetteFogBuildCancellation = null;
        // Worker outcomes never fault; a cancelled transform can finish harmlessly after teardown.
        silhouetteFogBuildTask = null;
    }

    internal void SetSilhouetteFogSettings(bool enabled,
        float strength, float coverage, float spillBlocks, float heightBlocks,
        float floorDepthBlocks, float billowSizeBlocks, float driftSpeed, float layerDetail,
        float edgeSoftness)
    {
        bool geometryChanged = SilhouetteFogSpillBlocks != spillBlocks
            || enabled && !SilhouetteFogEnabled;
        // Turning off releases the field in the next prepared frame, on the render thread.
        SilhouetteFogEnabled = enabled;
        SilhouetteFogStrength = strength;
        SilhouetteFogCoverage = coverage;
        SilhouetteFogSpillBlocks = spillBlocks;
        SilhouetteFogHeightBlocks = heightBlocks;
        SilhouetteFogFloorDepthBlocks = floorDepthBlocks;
        SilhouetteFogBillowSizeBlocks = billowSizeBlocks;
        SilhouetteFogDriftSpeed = driftSpeed;
        SilhouetteFogLayerDetail = layerDetail;
        SilhouetteFogEdgeSoftness = edgeSoftness;
        silhouetteFogGeometryDirty |= geometryChanged;
    }

    int UploadSilhouetteFogField(LodSilhouetteFogFieldArrays field)
    {
        if (!LodGlErrorIsolation.TryEstablishBaseline(
                () => (int)GL.GetError(), out string advisory, out string baselineFailure))
            throw new InvalidOperationException(baselineFailure);
        if (advisory.Length > 0 && !silhouetteFogReportedPendingError)
        {
            silhouetteFogReportedPendingError = true;
            capi.Logger.Warning(
                "[VintageHorizons] {0} before seam-fog field upload; those errors came from an "
                + "earlier renderer.", advisory);
        }
        LodGlStateSnapshot state = LodGlStateGuard.Capture(
            silhouetteFogStateApi, LodGlStateMask.Texture2DUnit0);
        int texture = 0;
        Exception? failure = null;
        try
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            texture = GL.GenTexture();
            if (texture == 0)
                throw new InvalidOperationException("the seam-fog field texture was not created");
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);
            // Half float keeps even the maximum bounded field compact. The CPU supplies float
            // input and the driver performs the one-time conversion during an ownership change.
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f,
                field.Width, field.Height, 0, PixelFormat.Rgba, PixelType.Float, field.Texels);
            ErrorCode uploadError = GL.GetError();
            if (uploadError != ErrorCode.NoError)
                throw new InvalidOperationException(
                    "seam-fog field upload raised OpenGL " + uploadError);
        }
        catch (Exception e)
        {
            failure = e;
        }
        finally
        {
            if (!LodGlStateGuard.TryRestore(
                    silhouetteFogStateApi, state, out string restoreFailure))
                failure ??= new InvalidOperationException(
                    "state restore after seam-fog field upload failed: " + restoreFailure);
        }
        if (failure == null) return texture;
        if (texture != 0) GL.DeleteTexture(texture);
        throw failure;
    }

    /// <summary>
    /// The baked tiling noise as a mipmapped 3D texture. Uploaded once, after the first field,
    /// so the field upload's error baseline has already been taken on this frame.
    /// </summary>
    int UploadSilhouetteFogNoise()
    {
        byte[] texels = LodSilhouetteFogNoise.Texels;
        int size = LodSilhouetteFogNoise.Size;
        int activeTexture = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture0);
        int previous = GL.GetInteger(GetPName.TextureBinding3D);
        int texture = 0;
        try
        {
            texture = GL.GenTexture();
            if (texture == 0)
                throw new InvalidOperationException("the seam-fog noise texture was not created");
            GL.BindTexture(TextureTarget.Texture3D, texture);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR,
                (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.LinearMipmapLinear);
            GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
            GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.Rgba8,
                size, size, size, 0, PixelFormat.Rgba, PixelType.UnsignedByte, texels);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture3D);
            ErrorCode uploadError = GL.GetError();
            if (uploadError != ErrorCode.NoError)
                throw new InvalidOperationException(
                    "seam-fog noise upload raised OpenGL " + uploadError);
            return texture;
        }
        catch
        {
            if (texture != 0) GL.DeleteTexture(texture);
            throw;
        }
        finally
        {
            GL.BindTexture(TextureTarget.Texture3D, previous);
            GL.ActiveTexture((TextureUnit)activeTexture);
        }
    }

    float SilhouetteFogHeightAtGrid(int gridX, int gridZ)
    {
        int cells = LodSilhouetteField.CoverageCellsPerEdge;
        int tileX = FloorDiv(gridX, cells);
        int tileZ = FloorDiv(gridZ, cells);
        int localX = gridX - tileX * cells;
        int localZ = gridZ - tileZ * cells;
        int xCandidates = localX == 0 ? 2 : 1;
        int zCandidates = localZ == 0 ? 2 : 1;
        int stride = cells + 1;
        for (int dz = 0; dz < zCandidates; dz++)
        {
            for (int dx = 0; dx < xCandidates; dx++)
            {
                int candidateX = tileX - dx;
                int candidateZ = tileZ - dz;
                if (!silhouetteMeshes.TryGetValue(
                        LodSilhouetteTile.KeyOf(candidateX, candidateZ), out SilhouetteGpuMesh? mesh))
                    continue;
                int x = gridX - candidateX * cells;
                int z = gridZ - candidateZ * cells;
                if ((uint)x > (uint)cells || (uint)z > (uint)cells) continue;
                return mesh.FogHeightGrid[z * stride + x];
            }
        }
        return capi.World.SeaLevel;
    }

    static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    /// <summary>
    /// Integrate the drift, never elapsed time multiplied by the live speed: multiplying moves the
    /// whole pattern by the elapsed time every time the dial changes, so the fog lurches while the
    /// slider is dragged and then sits still. Integrating makes the dial a rate.
    /// </summary>
    void AdvanceSilhouetteFogDrift(float deltaSeconds)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0f) return;
        float step = MathF.Min(deltaSeconds, SilhouetteFogMaxDriftStepSeconds)
            * Math.Clamp(SilhouetteFogDriftSpeed,
                LodSilhouetteFogVolume.MinDriftSpeed, LodSilhouetteFogVolume.MaxDriftSpeed)
            * SilhouetteFogDriftBlocksPerSecond;
        silhouetteFogDriftBlocks =
            (silhouetteFogDriftBlocks + step) % SilhouetteFogDriftWrapBlocks;
    }

    void DrawSilhouetteFogVolume(float deltaSeconds)
    {
        AdvanceSilhouetteFogDrift(deltaSeconds);
        LodSilhouetteClient? silhouettes = Silhouettes;
        if (silhouettes == null || !silhouettes.Enabled || silhouetteFogVolumeFailed
            || !SilhouetteFogEnabled || silhouetteFogFieldTexture == 0
            || silhouetteFogNoiseTexture == 0 || silhouetteFogBoundaryEdges == 0
            || silhouetteFogVolumeProg == null || silhouetteFogVolumeProg.LoadError
            || SilhouetteFogStrength <= 0f) return;
        FrameBufferRef? primary = PrimaryFrameBuffer();
        if (primary == null) return;

        float[] projection = capi.Render.CurrentProjectionMatrix;
        float[] view = capi.Render.CameraMatrixOriginf;
        if (Mat4f.Invert(silhouetteFogInvProjection, projection) == null
            || Mat4f.Invert(silhouetteFogInvView, view) == null)
            return;
        if (silhouetteFogVao == 0) silhouetteFogVao = GL.GenVertexArray();

        bool checkErrors = silhouetteFogErrorChecksLeft > 0;
        string advisory = "";
        if (checkErrors && !LodGlErrorIsolation.TryEstablishBaseline(
                () => (int)GL.GetError(), out advisory, out string baselineFailure))
        {
            StopSilhouetteFog(silhouettes, baselineFailure);
            return;
        }
        if (checkErrors && advisory.Length > 0 && !silhouetteFogReportedPendingError)
        {
            silhouetteFogReportedPendingError = true;
            capi.Logger.Warning(
                "[VintageHorizons] {0} before volumetric seam fog; those errors came from an "
                + "earlier renderer.", advisory);
        }

        LodGlStateSnapshot state = LodGlStateGuard.Capture(silhouetteFogStateApi,
            LodGlStateMask.Program | LodGlStateMask.Framebuffers
            | LodGlStateMask.Texture2DUnit0 | LodGlStateMask.VertexArray);
        int activeTexture = GL.GetInteger(GetPName.ActiveTexture);
        GL.ActiveTexture(TextureUnit.Texture1);
        int textureUnit1 = GL.GetInteger(GetPName.TextureBinding2D);
        GL.ActiveTexture(TextureUnit.Texture2);
        int textureUnit2 = GL.GetInteger(GetPName.TextureBinding3D);
        GL.ActiveTexture((TextureUnit)activeTexture);
        IShaderProgram? incoming = ShaderProgramBase.CurrentShaderProgram;
        bool depthTest = GL.IsEnabled(EnableCap.DepthTest);
        bool depthWrite = GL.GetBoolean(GetPName.DepthWritemask);
        bool blend = GL.IsEnabled(EnableCap.Blend);
        bool cull = GL.IsEnabled(EnableCap.CullFace);
        bool scissor = GL.IsEnabled(EnableCap.ScissorTest);
        int blendSrcRgb = GL.GetInteger(GetPName.BlendSrcRgb);
        int blendDstRgb = GL.GetInteger(GetPName.BlendDstRgb);
        int blendSrcAlpha = GL.GetInteger(GetPName.BlendSrcAlpha);
        int blendDstAlpha = GL.GetInteger(GetPName.BlendDstAlpha);
        int blendEquationRgb = GL.GetInteger(GetPName.BlendEquationRgb);
        int blendEquationAlpha = GL.GetInteger(GetPName.BlendEquationAlpha);
        GL.GetInteger(GetPName.Viewport, silhouetteFogViewport);
        GL.GetBoolean(GetPName.ColorWritemask, silhouetteFogColorMask);
        Exception? failure = null;
        try
        {
            incoming?.Stop();
            AttachSilhouetteFogFramebuffer(primary);
            GL.Viewport(0, 0, primary.Width, primary.Height);
            // This target deliberately has no depth attachment. Sampling the engine's depth
            // texture keeps it read-only and avoids attaching live engine storage to a second
            // framebuffer, which raised InvalidOperation every frame in 0.4.103.
            GL.Disable(EnableCap.DepthTest);
            GL.DepthMask(false);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.ScissorTest);
            // Primary alpha is owned by the later OIT/water composition. Fog changes RGB only.
            GL.ColorMask(true, true, true, false);
            // This framebuffer has only ColorAttachment0. The engine blend helper also
            // configures the SSAO attachments when SSAO is enabled, which is invalid here.
            GL.Enable(EnableCap.Blend);
            GL.BlendEquationSeparate(BlendEquationMode.FuncAdd, BlendEquationMode.FuncAdd);
            GL.BlendFuncSeparate(
                BlendingFactorSrc.SrcAlpha,
                BlendingFactorDest.OneMinusSrcAlpha,
                BlendingFactorSrc.One,
                BlendingFactorDest.OneMinusSrcAlpha);

            Vec3d cam = capi.World.Player.Entity.CameraPos;
            IShaderProgram p = silhouetteFogVolumeProg;
            p.Use();
            p.UniformMatrix("invProjection", silhouetteFogInvProjection);
            p.UniformMatrix("invView", silhouetteFogInvView);
            p.Uniform("cameraInField", new Vec3f(
                (float)(cam.X - silhouetteFogFieldOriginX), (float)cam.Y,
                (float)(cam.Z - silhouetteFogFieldOriginZ)));
            p.Uniform("fieldWorldOriginX", (float)silhouetteFogFieldOriginX);
            p.Uniform("fieldWorldOriginZ", (float)silhouetteFogFieldOriginZ);
            p.Uniform("fieldWidth", (float)silhouetteFogFieldWidth);
            p.Uniform("fieldHeight", (float)silhouetteFogFieldHeight);
            p.Uniform("fieldTexelBlocks", silhouetteFogFieldTexelBlocks);
            p.Uniform("farViewDistance", (float)silhouettes.ReachBlocks);
            p.Uniform("globeStart", EffectiveFarDistance);
            p.Uniform("globeEffect", SilhouetteGlobeEffect);
            float globeDrop = MathF.Max(0f, SilhouetteGlobeEffect) * silhouettes.ReachBlocks;
            float globeRise = MathF.Max(0f, -SilhouetteGlobeEffect) * silhouettes.ReachBlocks;
            p.Uniform("fieldMinY",
                silhouetteFogMinGroundY - SilhouetteFogFloorDepthBlocks - globeDrop - 4f);
            // The tallest bank top is 1.3 heights, and its ceiling ends at 1.15 of that.
            p.Uniform("fieldMaxY",
                silhouetteFogMaxGroundY + SilhouetteFogHeightBlocks * 1.5f + globeRise + 4f);
            // One pixel's width in blocks per block of distance, from the vertical field of view.
            float focal = MathF.Abs(projection[5]);
            p.Uniform("fogPixelScale",
                focal > 1e-6f ? 2f / (focal * Math.Max(1, primary.Height)) : 0.001f);
            p.Uniform("rgbaFogIn", capi.Ambient.BlendedFogColor);
            p.Uniform("sunPosition", capi.World.Calendar.SunPositionNormalized);
            p.Uniform("sunColor", capi.World.Calendar.SunColor);
            p.Uniform("dayLight", Math.Max(0, capi.World.Calendar.DayLightStrength));
            p.Uniform("fogCoreBlocks", silhouetteFogCoreBlocks);
            p.Uniform("fogStrength", SilhouetteFogStrength);
            p.Uniform("fogCoverage", SilhouetteFogCoverage);
            p.Uniform("fogSpillBlocks", SilhouetteFogSpillBlocks);
            p.Uniform("fogHeightBlocks", SilhouetteFogHeightBlocks);
            p.Uniform("fogFloorDepthBlocks", SilhouetteFogFloorDepthBlocks);
            p.Uniform("fogBillowSizeBlocks", SilhouetteFogBillowSizeBlocks);
            p.Uniform("fogEdgeSoftness", SilhouetteFogEdgeSoftness);
            p.Uniform("fogLayerDetail", SilhouetteFogLayerDetail);
            p.Uniform("fogDriftBlocks", silhouetteFogDriftBlocks);
            p.BindTexture2D("sceneDepth", primary.DepthTextureId, 0);
            p.BindTexture2D("fogField", silhouetteFogFieldTexture, 1);
            // The engine's uniform collector has no sampler3D, so this unit is bound directly.
            if (silhouetteFogNoiseProgram != p.ProgramId)
            {
                silhouetteFogNoiseLocation = GL.GetUniformLocation(p.ProgramId, "fogNoise");
                silhouetteFogNoiseProgram = p.ProgramId;
            }
            if (silhouetteFogNoiseLocation < 0)
                throw new InvalidOperationException("the seam-fog shader has no fogNoise sampler");
            GL.ActiveTexture(TextureUnit.Texture2);
            GL.BindTexture(TextureTarget.Texture3D, silhouetteFogNoiseTexture);
            GL.Uniform1(silhouetteFogNoiseLocation, 2);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindVertexArray(silhouetteFogVao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
            p.Stop();
        }
        catch (Exception e)
        {
            failure = e;
        }
        finally
        {
            if (ShaderProgramBase.CurrentShaderProgram == silhouetteFogVolumeProg)
                silhouetteFogVolumeProg.Stop();
            GL.BlendEquationSeparate(
                (BlendEquationMode)blendEquationRgb, (BlendEquationMode)blendEquationAlpha);
            GL.BlendFuncSeparate(
                (BlendingFactorSrc)blendSrcRgb, (BlendingFactorDest)blendDstRgb,
                (BlendingFactorSrc)blendSrcAlpha, (BlendingFactorDest)blendDstAlpha);
            if (blend) GL.Enable(EnableCap.Blend); else GL.Disable(EnableCap.Blend);
            if (depthTest) GL.Enable(EnableCap.DepthTest); else GL.Disable(EnableCap.DepthTest);
            GL.DepthMask(depthWrite);
            if (cull) GL.Enable(EnableCap.CullFace); else GL.Disable(EnableCap.CullFace);
            if (scissor) GL.Enable(EnableCap.ScissorTest); else GL.Disable(EnableCap.ScissorTest);
            GL.Viewport(silhouetteFogViewport[0], silhouetteFogViewport[1],
                silhouetteFogViewport[2], silhouetteFogViewport[3]);
            GL.ColorMask(silhouetteFogColorMask[0], silhouetteFogColorMask[1],
                silhouetteFogColorMask[2], silhouetteFogColorMask[3]);
            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, textureUnit1);
            GL.ActiveTexture(TextureUnit.Texture2);
            GL.BindTexture(TextureTarget.Texture3D, textureUnit2);
            GL.ActiveTexture((TextureUnit)state.ActiveTexture);
            if (!LodGlStateGuard.TryRestore(silhouetteFogStateApi, state, out string restoreFailure))
                failure ??= new InvalidOperationException(
                    "state restore after volumetric seam fog failed: " + restoreFailure);
            if (incoming != null && ShaderProgramBase.CurrentShaderProgram != incoming)
            {
                incoming.Use();
                if (ReferenceEquals(incoming, prog)) RebindOwnSamplers(prog!);
            }
        }

        if (checkErrors)
        {
            silhouetteFogErrorChecksLeft--;
            ErrorCode error = GL.GetError();
            if (error != ErrorCode.NoError)
                failure ??= new InvalidOperationException(
                    "volumetric seam fog raised OpenGL " + error);
        }

        if (failure == null) return;
        StopSilhouetteFog(silhouettes, failure.Message);
    }

    void StopSilhouetteFog(LodSilhouetteClient silhouettes, string reason)
    {
        silhouetteFogVolumeFailed = true;
        capi.Logger.Error(
            "[VintageHorizons] volumetric Farseer seam fog stopped; Farseer is off for this "
            + "session instead of corrupting later render stages: {0}", reason);
        silhouettes.SetEnabled(false);
        ReleaseSilhouetteMeshes();
    }

    void AttachSilhouetteFogFramebuffer(FrameBufferRef primary)
    {
        if (silhouetteFogFramebuffer == 0) silhouetteFogFramebuffer = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, silhouetteFogFramebuffer);
        int color = primary.ColorTextureIds[0];
        // Reattach every frame. The engine can delete and recreate a target with the same name;
        // an old FBO attachment does not follow a recycled integer name to the new texture.
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, color, 0);
        if (silhouetteFogAttachedColor == color) return;
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
        FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException("volumetric seam fog framebuffer incomplete: " + status);
        silhouetteFogAttachedColor = color;
    }

    void DisposeSilhouetteFogField()
    {
        if (silhouetteFogFieldTexture != 0) GL.DeleteTexture(silhouetteFogFieldTexture);
        silhouetteFogFieldTexture = 0;
        silhouetteFogBoundaryEdges = 0;
    }

    void ClearSilhouetteFogField()
    {
        CancelSilhouetteFogBuild();
        DisposeSilhouetteFogField();
    }

    void ReleaseSilhouetteFogVolume()
    {
        try
        {
            ClearSilhouetteFogField();
            if (silhouetteFogVao != 0) GL.DeleteVertexArray(silhouetteFogVao);
            if (silhouetteFogFramebuffer != 0) GL.DeleteFramebuffer(silhouetteFogFramebuffer);
            if (silhouetteFogNoiseTexture != 0) GL.DeleteTexture(silhouetteFogNoiseTexture);
        }
        catch
        {
            // Teardown must continue over objects the driver already released.
        }
        silhouetteFogVao = 0;
        silhouetteFogFramebuffer = 0;
        silhouetteFogAttachedColor = 0;
        silhouetteFogNoiseTexture = 0;
        silhouetteFogNoiseProgram = 0;
        silhouetteFogNoiseLocation = -1;
    }
}
