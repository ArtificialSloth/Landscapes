using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VintageHorizons.Silhouette;

namespace VintageHorizons;

/// <summary>
/// The silhouette pass (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER 4.8), first-look slice.
///
/// Drawn by this renderer rather than a second one, so the far plane keeps one owner (G5). It
/// runs as its own step after the cached draw, because that draw returns early whenever no
/// cached terrain is in view, and a view out to sea is exactly that case. No join blending yet:
/// the cached terrain's edge still fades to sky (P4 changes the fade target).
/// </summary>
public partial class LodTerrainRenderer
{
    /// <summary>Set by the mod system. Null while silhouettes do not exist in this session.</summary>
    internal LodSilhouetteClient? Silhouettes { get; set; }

    sealed class SilhouetteGpuMesh
    {
        public required MeshRef Mesh;
        public required int TileX;
        public required int TileZ;
        public required float MinY;
        public required float MaxY;
    }

    readonly Dictionary<long, SilhouetteGpuMesh> silhouetteMeshes = new();
    readonly LodFrustum silhouetteFrustum = new();
    IShaderProgram? silhouetteProg;
    bool silhouetteShaderOk;
    bool silhouetteShaderWanted;
    int silhouetteMeshEpoch = -1;

    const int SilhouetteUploadsPerFrame = 4;

    // Farseer 1.4.0's client defaults: the classic look (owner decision D11) until they are tuned.
    internal float SilhouetteSkyTint = 5.0f;
    internal Vec4f SilhouetteColorTint = new(0.26f, 0.29f, 0.45f, 0.40f);
    internal float SilhouetteLightLevelBias = 0.70f;
    internal float SilhouetteFadeBias = 0.4f;
    internal float SilhouetteGlobeEffect = 0.05f;

    internal int SilhouetteDrawsLastFrame { get; private set; }
    internal int SilhouetteMeshCount => silhouetteMeshes.Count;

    internal string SilhouetteShaderState => !silhouetteShaderWanted
        ? "not compiled (never switched on)"
        : silhouetteShaderOk ? "ok" : "FAILED to compile, silhouettes are off for this session";

    /// <summary>
    /// The projection distance silhouettes need, or 0 while they draw nothing. The far plane is
    /// then exactly what it is without them (plan 4.10).
    /// </summary>
    float SilhouetteProjectionDistance =>
        Silhouettes is { Enabled: true } silhouettes && silhouetteMeshes.Count > 0
            ? silhouettes.ReachBlocks + LodFarDistance.ProjectionMargin
            : 0f;

    void LoadSilhouetteShader()
    {
        // Compiled only once silhouettes are first switched on, so a player who never uses them
        // pays no compile (plan 4.10). A shader reload after that recompiles it with the others.
        if (!silhouetteShaderWanted) return;
        silhouetteProg = capi.Shader.NewShaderProgram();
        silhouetteProg.AssetDomain = "vintagehorizons";
        silhouetteProg.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        silhouetteProg.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
        capi.Shader.RegisterFileShaderProgram("lodsilhouette", silhouetteProg);
        silhouetteShaderOk = silhouetteProg.Compile();
        if (silhouetteShaderOk) LodTerrainSamplerLayout.Configure(silhouetteProg);
        if (silhouetteShaderOk)
        {
            capi.Logger.Notification(
                "[VintageHorizons] the lodsilhouette shader compiled; horizon silhouettes can draw.");
        }
        else
        {
            // Fails visibly, never plausibly (settled decision 26): nothing is drawn in its place.
            capi.Logger.Error(
                "[VintageHorizons] the lodsilhouette shader failed to compile; horizon silhouettes "
                + "stay off for this session. This is ours, not your setup.");
        }
    }

    void DrawSilhouettes()
    {
        SilhouetteDrawsLastFrame = 0;
        LodSilhouetteClient? silhouettes = Silhouettes;
        if (silhouettes == null || !silhouettes.Enabled)
        {
            if (silhouetteMeshes.Count > 0) ReleaseSilhouetteMeshes();
            return;
        }
        if (!silhouetteShaderWanted)
        {
            silhouetteShaderWanted = true;
            LoadSilhouetteShader();
        }
        if (!silhouetteShaderOk || silhouetteProg == null || silhouetteProg.LoadError)
        {
            if (silhouetteMeshes.Count > 0) ReleaseSilhouetteMeshes();
            return;
        }
        if (silhouetteMeshEpoch != silhouettes.Epoch)
        {
            ReleaseSilhouetteMeshes();
            silhouetteMeshEpoch = silhouettes.Epoch;
        }
        UploadSilhouetteMeshes(silhouettes);

        var rapi = capi.Render;
        if (silhouetteMeshes.Count == 0 || rapi.FrameWidth == 0) return;

        // A no-op unless the needed distance changed; a change applies from the next frame.
        ApplyZFar();
        Vec3d cam = capi.World.Player.Entity.CameraPos;
        silhouetteFrustum.Update(rapi.CurrentProjectionMatrix, rapi.CameraMatrixOriginf);
        float reach = silhouettes.ReachBlocks;
        // The near sink, the edge sink and the globe curve only ever lower a vertex.
        float drop = SilhouetteGlobeEffect * reach + 5f + LodSilhouetteField.DefaultSinkBlocks;
        const int tileBlocks = LodSilhouetteTile.TileBlocks;

        IShaderProgram p = silhouetteProg;
        p.Use();
        // The mesh is a height field seen from above and from below at steep slopes; its
        // winding is not worth pinning for the first look.
        rapi.GlDisableCullFace();
        p.UniformMatrix("viewMatrix", rapi.CameraMatrixOriginf);
        p.UniformMatrix("projectionMatrix", rapi.CurrentProjectionMatrix);
        p.Uniform("rgbaFogIn", capi.Ambient.BlendedFogColor);
        p.Uniform("fogMinIn", capi.Ambient.BlendedFogMin);
        p.Uniform("fogDensityIn", capi.Ambient.BlendedFogDensity);
        p.Uniform("horizonFog", capi.Ambient.BlendedCloudDensity);
        p.Uniform("sunPosition", capi.World.Calendar.SunPositionNormalized);
        p.Uniform("sunColor", capi.World.Calendar.SunColor);
        p.Uniform("dayLight", Math.Max(0, capi.World.Calendar.DayLightStrength));
        p.Uniform("viewDistance", ApprovedViewDistance());
        p.Uniform("farViewDistance", reach);
        p.Uniform("globeStart", EffectiveFarDistance);
        p.Uniform("globeEffect", SilhouetteGlobeEffect);
        p.Uniform("skyTint", SilhouetteSkyTint);
        p.Uniform("colorTint", SilhouetteColorTint);
        p.Uniform("lightLevelBias", SilhouetteLightLevelBias);
        p.Uniform("fadeBias", SilhouetteFadeBias);
        p.Uniform("seaLevel", capi.World.SeaLevel);

        double reachSq = (double)reach * reach;
        foreach (SilhouetteGpuMesh mesh in silhouetteMeshes.Values)
        {
            double relX = mesh.TileX * (double)tileBlocks - cam.X;
            double relZ = mesh.TileZ * (double)tileBlocks - cam.Z;
            double nearX = Math.Clamp(0, relX, relX + tileBlocks);
            double nearZ = Math.Clamp(0, relZ, relZ + tileBlocks);
            if (nearX * nearX + nearZ * nearZ > reachSq) continue;
            if (!silhouetteFrustum.BoxInView(relX, mesh.MinY - drop - cam.Y, relZ,
                relX + tileBlocks, mesh.MaxY - cam.Y, relZ + tileBlocks)) continue;
            p.Uniform("tileOrigin", new Vec3f((float)relX, (float)-cam.Y, (float)relZ));
            rapi.RenderMesh(mesh.Mesh);
            SilhouetteDrawsLastFrame++;
        }
        rapi.GlEnableCullFace();
        p.Stop();
    }

    void UploadSilhouetteMeshes(LodSilhouetteClient silhouettes)
    {
        for (int n = 0; n < SilhouetteUploadsPerFrame
             && silhouettes.TryTakeFinished(out LodSilhouetteClient.MeshResult result); n++)
        {
            if (silhouetteMeshes.Remove(result.Key, out SilhouetteGpuMesh? old)) old.Mesh.Dispose();
            if (result.Mesh == null) continue;

            var data = new MeshData(false);
            data.SetVerticesCount(result.Mesh.VertexCount);
            data.SetIndicesCount(result.Mesh.IndexCount);
            data.xyz = result.Mesh.Xyz;
            data.Indices = result.Mesh.Indices;
            silhouetteMeshes[result.Key] = new SilhouetteGpuMesh
            {
                Mesh = capi.Render.UploadMesh(data),
                TileX = result.TileX,
                TileZ = result.TileZ,
                MinY = result.Mesh.MinY,
                MaxY = result.Mesh.MaxY,
            };
        }
    }

    /// <summary>Main thread. Frees every silhouette mesh on the GPU.</summary>
    internal void ReleaseSilhouetteMeshes()
    {
        foreach (SilhouetteGpuMesh mesh in silhouetteMeshes.Values) mesh.Mesh.Dispose();
        silhouetteMeshes.Clear();
    }
}
