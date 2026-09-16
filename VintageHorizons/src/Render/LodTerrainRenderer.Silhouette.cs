using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VintageHorizons.Silhouette;

namespace VintageHorizons;

/// <summary>
/// The silhouette pass (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER P4).
///
/// Drawn by this renderer rather than a second one, so the far plane keeps one owner (G5). It
/// runs as its own step after the cached draw, because that draw returns early whenever no
/// cached terrain is in view, and a view out to sea is exactly that case.
/// </summary>
public partial class LodTerrainRenderer
{
    /// <summary>Set by the mod system. Null while silhouettes do not exist in this session.</summary>
    internal LodSilhouetteClient? Silhouettes { get; set; }

    sealed class SilhouetteGpuMesh
    {
        public required MeshRef High;
        public required MeshRef Medium;
        public required MeshRef Low;
        public required int HighTriangles;
        public required int MediumTriangles;
        public required int LowTriangles;
        public required int TileX;
        public required int TileZ;
        public required float MinY;
        public required float MaxY;

        public MeshRef At(int quadsPerEdge) => quadsPerEdge switch
        {
            LodSilhouetteField.HighQuadsPerEdge => High,
            LodSilhouetteField.MediumQuadsPerEdge => Medium,
            _ => Low,
        };

        public int TrianglesAt(int quadsPerEdge) => quadsPerEdge switch
        {
            LodSilhouetteField.HighQuadsPerEdge => HighTriangles,
            LodSilhouetteField.MediumQuadsPerEdge => MediumTriangles,
            _ => LowTriangles,
        };

        public void Dispose()
        {
            High.Dispose();
            Medium.Dispose();
            Low.Dispose();
        }
    }

    readonly Dictionary<long, SilhouetteGpuMesh> silhouetteMeshes = new();
    readonly LodFrustum silhouetteFrustum = new();
    IShaderProgram? silhouetteProg;
    bool silhouetteShaderOk;
    bool silhouetteShaderWanted;
    int silhouetteMeshEpoch = -1;

    const int SilhouetteUploadsPerFrame = 4;
    const long SilhouetteUploadBytesPerFrame = 2 * 1024 * 1024;
    const double SilhouetteUploadMillisecondsPerFrame = 2.0;

    // Farseer 1.4.0's client defaults: the classic look (owner decision D11) until they are tuned.
    internal float SilhouetteSkyTint = 5.0f;
    internal Vec4f SilhouetteColorTint = new(0.26f, 0.29f, 0.45f, 0.40f);
    internal float SilhouetteLightLevelBias = 0.70f;
    internal float SilhouetteFadeBias = 0.4f;
    internal float SilhouetteGlobeEffect = 0.05f;
    internal float SilhouetteOutlineDetail = LodSilhouetteField.DefaultOutlineDetail;

    internal int SilhouetteDrawsLastFrame { get; private set; }
    internal int SilhouetteHighDrawsLastFrame { get; private set; }
    internal int SilhouetteMediumDrawsLastFrame { get; private set; }
    internal int SilhouetteLowDrawsLastFrame { get; private set; }
    internal long SilhouetteTrianglesLastFrame { get; private set; }
    internal int SilhouetteMeshCount => silhouetteMeshes.Count;
    int silhouetteUploadFailures;

    /// <summary>Cached terrain changes its fade target only when a silhouette can actually draw.</summary>
    bool SilhouetteFadeTargetActive => Silhouettes is { Enabled: true }
        && silhouetteShaderOk && silhouetteMeshes.Count > 0;

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
        SilhouetteHighDrawsLastFrame = 0;
        SilhouetteMediumDrawsLastFrame = 0;
        SilhouetteLowDrawsLastFrame = 0;
        SilhouetteTrianglesLastFrame = 0;
        LodSilhouetteClient? silhouettes = Silhouettes;
        if (silhouettes == null || !silhouettes.Enabled)
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
        // No offered/decoded tile means no compile either. This keeps a server that does not
        // offer silhouettes at the exact old render cost even when the saved client switch is on.
        if (silhouetteMeshes.Count == 0) return;
        if (!silhouetteShaderWanted)
        {
            silhouetteShaderWanted = true;
            LoadSilhouetteShader();
        }
        if (!silhouetteShaderOk || silhouetteProg == null || silhouetteProg.LoadError)
        {
            ReleaseSilhouetteMeshes();
            // A compile/load failure is a session failure, not a per-frame retry. Shutting the
            // client feature down also stops offers, decoding, meshing and future uploads.
            silhouettes.SetEnabled(false);
            return;
        }

        var rapi = capi.Render;
        if (rapi.FrameWidth == 0) return;

        // A no-op unless the needed distance changed; a change applies from the next frame.
        ApplyZFar();
        Vec3d cam = capi.World.Player.Entity.CameraPos;
        silhouetteFrustum.Update(rapi.CurrentProjectionMatrix, rapi.CameraMatrixOriginf);
        float reach = silhouettes.ReachBlocks;
        // The buried near underlay, original near sink and globe curve only lower vertices.
        float drop = SilhouetteGlobeEffect * reach + 5f
            + LodSilhouetteField.UnderlaySinkBlocks;
        const int tileBlocks = LodSilhouetteTile.TileBlocks;

        IShaderProgram p = silhouetteProg;
        p.Use();
        // These are consistently wound top surfaces. Back-face culling avoids shading the
        // hidden side of distant hills and leaves the render state in its normal terrain mode.
        rapi.GlEnableCullFace();
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
        p.Uniform("underlaySinkBlocks", LodSilhouetteField.UnderlaySinkBlocks);
        p.Uniform("silhouetteSkyTint", SilhouetteSkyTint);
        p.Uniform("silhouetteColorTint", SilhouetteColorTint);
        p.Uniform("silhouetteLightLevelBias", SilhouetteLightLevelBias);
        p.Uniform("silhouetteFadeBias", SilhouetteFadeBias);
        p.Uniform("silhouetteSeaLevel", capi.World.SeaLevel);
        float cacheDistStart = ApprovedViewDistance() * 0.785f;
        p.Uniform("cacheFadeStart", cacheDistStart
            + 0.75f * Math.Max(0f, EffectiveFarDistance - cacheDistStart - 512f));

        double reachSq = (double)reach * reach;
        foreach (SilhouetteGpuMesh mesh in silhouetteMeshes.Values)
        {
            double relX = mesh.TileX * (double)tileBlocks - cam.X;
            double relZ = mesh.TileZ * (double)tileBlocks - cam.Z;
            double nearX = Math.Clamp(0, relX, relX + tileBlocks);
            double nearZ = Math.Clamp(0, relZ, relZ + tileBlocks);
            double nearestDistanceSq = nearX * nearX + nearZ * nearZ;
            if (nearestDistanceSq > reachSq) continue;
            if (!silhouetteFrustum.BoxInView(relX, mesh.MinY - drop - cam.Y, relZ,
                relX + tileBlocks, mesh.MaxY - cam.Y, relZ + tileBlocks)) continue;
            p.Uniform("tileOrigin", new Vec3f((float)relX, (float)-cam.Y, (float)relZ));
            int resolution = LodSilhouetteField.ResolutionFor(
                ScreenBlockScale, Math.Max(1.0, Math.Sqrt(nearestDistanceSq)),
                SilhouetteOutlineDetail);
            rapi.RenderMesh(mesh.At(resolution));
            SilhouetteTrianglesLastFrame += mesh.TrianglesAt(resolution);
            if (resolution == LodSilhouetteField.HighQuadsPerEdge) SilhouetteHighDrawsLastFrame++;
            else if (resolution == LodSilhouetteField.MediumQuadsPerEdge) SilhouetteMediumDrawsLastFrame++;
            else SilhouetteLowDrawsLastFrame++;
            SilhouetteDrawsLastFrame++;
        }
        p.Stop();
    }

    void UploadSilhouetteMeshes(LodSilhouetteClient silhouettes)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long bytes = 0;
        for (int n = 0; n < SilhouetteUploadsPerFrame
             && silhouettes.TryTakeFinished(out LodSilhouetteClient.MeshResult result); n++)
        {
            if (result.Removal || result.Meshes == null)
            {
                if (silhouetteMeshes.Remove(result.Key, out SilhouetteGpuMesh? removed))
                    removed.Dispose();
                continue;
            }

            silhouetteMeshes.TryGetValue(result.Key, out SilhouetteGpuMesh? old);
            SilhouetteGpuMesh? next = null;
            try
            {
                // All three uploads finish before the dictionary changes. A failed replacement
                // keeps the last complete tile rather than turning a transient upload error into
                // a visible horizon hole.
                MeshRef high = UploadSilhouetteMesh(result.Meshes.High);
                MeshRef medium;
                MeshRef low;
                try
                {
                    medium = UploadSilhouetteMesh(result.Meshes.Medium);
                    try { low = UploadSilhouetteMesh(result.Meshes.Low); }
                    catch { medium.Dispose(); throw; }
                }
                catch { high.Dispose(); throw; }
                next = new SilhouetteGpuMesh
                {
                    High = high,
                    Medium = medium,
                    Low = low,
                    HighTriangles = result.Meshes.High.IndexCount / 3,
                    MediumTriangles = result.Meshes.Medium.IndexCount / 3,
                    LowTriangles = result.Meshes.Low.IndexCount / 3,
                    TileX = result.TileX,
                    TileZ = result.TileZ,
                    MinY = result.Meshes.MinY,
                    MaxY = result.Meshes.MaxY,
                };
            }
            catch (Exception e)
            {
                next?.Dispose();
                if (++silhouetteUploadFailures == 1)
                    capi.Logger.Error("[VintageHorizons] a horizon silhouette upload failed; "
                        + "the previous complete tile remains: {0}", e);
                continue;
            }

            // Publish the complete replacement before retiring the prior GPU objects.
            silhouetteMeshes[result.Key] = next;
            old?.Dispose();
            bytes += result.Meshes.ByteCount;
            double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (bytes >= SilhouetteUploadBytesPerFrame
                || elapsed >= SilhouetteUploadMillisecondsPerFrame) break;
        }
    }

    MeshRef UploadSilhouetteMesh(LodSilhouetteMeshArrays mesh)
    {
        var data = new MeshData(false);
        data.SetVerticesCount(mesh.VertexCount);
        data.SetIndicesCount(mesh.IndexCount);
        data.xyz = mesh.Xyz;
        data.Indices = mesh.Indices;
        return capi.Render.UploadMesh(data);
    }

    /// <summary>Main thread. Frees every silhouette mesh on the GPU.</summary>
    internal void ReleaseSilhouetteMeshes()
    {
        foreach (SilhouetteGpuMesh mesh in silhouetteMeshes.Values) mesh.Dispose();
        silhouetteMeshes.Clear();
    }
}
