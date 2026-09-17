using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using VintageHorizons.Silhouette;

namespace VintageHorizons;

public partial class LodTerrainRenderer
{
    IShaderProgram? silhouetteFogVolumeProg;
    MeshRef? silhouetteFogVolumeMesh;
    bool silhouetteFogVolumeFailed;
    int silhouetteFogOriginX;
    int silhouetteFogOriginZ;
    int silhouetteFogBoundaryEdges;
    int silhouetteFogFramebuffer;
    int silhouetteFogAttachedColor;
    bool silhouetteFogGeometryDirty;
    int silhouetteFogErrorChecksLeft = 240;
    bool silhouetteFogReportedPendingError;
    internal float SilhouetteFogStrength = LodSilhouetteFogVolume.DefaultStrength;
    internal float SilhouetteFogCoverage = LodSilhouetteFogVolume.DefaultCoverage;
    internal float SilhouetteFogSpillBlocks = LodSilhouetteFogVolume.DefaultSpillBlocks;
    internal float SilhouetteFogHeightBlocks = LodSilhouetteFogVolume.DefaultHeightBlocks;
    internal float SilhouetteFogFloorDepthBlocks = LodSilhouetteFogVolume.DefaultFloorDepthBlocks;
    internal float SilhouetteFogBillowSizeBlocks = LodSilhouetteFogVolume.DefaultBillowSizeBlocks;
    internal float SilhouetteFogDriftSpeed = LodSilhouetteFogVolume.DefaultDriftSpeed;
    internal float SilhouetteFogLayerDetail = LodSilhouetteFogVolume.DefaultLayerDetail;
    readonly LodOpenGlStateApi silhouetteFogStateApi = new();
    readonly int[] silhouetteFogViewport = new int[4];
    readonly bool[] silhouetteFogColorMask = new bool[4];

    void UploadSilhouetteFogVolume(LodSilhouetteClient silhouettes)
    {
        if (!silhouetteSeamMask.HasEdges)
        {
            DisposeSilhouetteFogMesh();
            silhouetteSeamMask.MarkUploaded();
            return;
        }

        try
        {
            LodSilhouetteFogMeshArrays arrays = LodSilhouetteFogVolume.Build(
                silhouetteSeamMask, SilhouetteFogHeightAtGrid,
                SilhouetteFogSpillBlocks, SilhouetteFogHeightBlocks,
                SilhouetteFogFloorDepthBlocks, SilhouetteFogLayerDetail);
            MeshRef next = UploadSilhouetteFogMesh(arrays);
            MeshRef? old = silhouetteFogVolumeMesh;
            silhouetteFogVolumeMesh = next;
            silhouetteFogOriginX = arrays.OriginBlockX;
            silhouetteFogOriginZ = arrays.OriginBlockZ;
            silhouetteFogBoundaryEdges = arrays.BoundaryEdges;
            silhouetteFogGeometryDirty = false;
            silhouetteSeamMask.MarkUploaded();
            old?.Dispose();
        }
        catch (Exception e)
        {
            silhouetteFogVolumeFailed = true;
            capi.Logger.Error(
                "[VintageHorizons] volumetric Farseer seam fog failed; Farseer is off for this "
                + "session instead of drawing an exposed join: {0}", e.Message);
            silhouettes.SetEnabled(false);
            ReleaseSilhouetteMeshes();
        }
    }

    internal void SetSilhouetteFogSettings(
        float strength, float coverage, float spillBlocks, float heightBlocks,
        float floorDepthBlocks, float billowSizeBlocks, float driftSpeed, float layerDetail)
    {
        bool geometryChanged = SilhouetteFogSpillBlocks != spillBlocks
            || SilhouetteFogHeightBlocks != heightBlocks
            || SilhouetteFogFloorDepthBlocks != floorDepthBlocks
            || SilhouetteFogLayerDetail != layerDetail;
        SilhouetteFogStrength = strength;
        SilhouetteFogCoverage = coverage;
        SilhouetteFogSpillBlocks = spillBlocks;
        SilhouetteFogHeightBlocks = heightBlocks;
        SilhouetteFogFloorDepthBlocks = floorDepthBlocks;
        SilhouetteFogBillowSizeBlocks = billowSizeBlocks;
        SilhouetteFogDriftSpeed = driftSpeed;
        SilhouetteFogLayerDetail = layerDetail;
        silhouetteFogGeometryDirty |= geometryChanged;
    }

    MeshRef UploadSilhouetteFogMesh(LodSilhouetteFogMeshArrays mesh)
    {
        var data = new MeshData(false);
        data.SetVerticesCount(mesh.VertexCount);
        data.SetIndicesCount(mesh.IndexCount);
        data.xyz = mesh.Xyz;
        data.Uv = mesh.FogWeights;
        data.Indices = mesh.Indices;
        return capi.Render.UploadMesh(data);
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

    void DrawSilhouetteFogVolume()
    {
        LodSilhouetteClient? silhouettes = Silhouettes;
        if (silhouettes == null || !silhouettes.Enabled || silhouetteFogVolumeFailed
            || silhouetteFogVolumeMesh == null || silhouetteFogBoundaryEdges == 0
            || silhouetteFogVolumeProg == null || silhouetteFogVolumeProg.LoadError
            || SilhouetteFogStrength <= 0f) return;
        FrameBufferRef? primary = PrimaryFrameBuffer();
        if (primary == null) return;

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
            | LodGlStateMask.Texture2DUnit0);
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
            p.UniformMatrix("viewMatrix", capi.Render.CameraMatrixOriginf);
            p.UniformMatrix("projectionMatrix", capi.Render.CurrentProjectionMatrix);
            p.Uniform("volumeOrigin", new Vec3f(
                (float)(silhouetteFogOriginX - cam.X), (float)-cam.Y,
                (float)(silhouetteFogOriginZ - cam.Z)));
            p.Uniform("volumeWorldOriginX", (float)silhouetteFogOriginX);
            p.Uniform("volumeWorldOriginZ", (float)silhouetteFogOriginZ);
            p.Uniform("farViewDistance", silhouettes.ReachBlocks);
            p.Uniform("globeStart", EffectiveFarDistance);
            p.Uniform("globeEffect", SilhouetteGlobeEffect);
            p.Uniform("rgbaFogIn", capi.Ambient.BlendedFogColor);
            p.Uniform("fogTime", (capi.World.ElapsedMilliseconds % 1_000_000L) / 1000f);
            p.Uniform("fogStrength", SilhouetteFogStrength);
            p.Uniform("fogCoverage", SilhouetteFogCoverage);
            p.Uniform("fogBillowSizeBlocks", SilhouetteFogBillowSizeBlocks);
            p.Uniform("fogDriftSpeed", SilhouetteFogDriftSpeed);
            p.BindTexture2D("sceneDepth", primary.DepthTextureId, 0);
            capi.Render.RenderMesh(silhouetteFogVolumeMesh);
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

    void DisposeSilhouetteFogMesh()
    {
        silhouetteFogVolumeMesh?.Dispose();
        silhouetteFogVolumeMesh = null;
        silhouetteFogBoundaryEdges = 0;
    }

    void ReleaseSilhouetteFogVolume()
    {
        try
        {
            DisposeSilhouetteFogMesh();
            if (silhouetteFogFramebuffer != 0) GL.DeleteFramebuffer(silhouetteFogFramebuffer);
        }
        catch
        {
            // Teardown must continue over objects the driver already released.
        }
        silhouetteFogFramebuffer = 0;
        silhouetteFogAttachedColor = 0;
    }
}
