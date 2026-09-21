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
        public required LodSilhouetteCoverage ApronCells;
        public required float[] FogHeightGrid;
        public required ulong FogHeightHash;

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

    readonly record struct SilhouetteFrameDraw(
        SilhouetteGpuMesh Mesh, float RelX, float RelZ, LodSilhouetteCoverage Replacements,
        int Resolution);

    readonly Dictionary<long, SilhouetteGpuMesh> silhouetteMeshes = new();
    readonly List<SilhouetteFrameDraw> silhouetteFrameDraws = new();
    readonly LodFrustum silhouetteFrustum = new();
    readonly LodSilhouetteSeamMask silhouetteSeamMask = new();
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
    internal float SilhouetteUnderlayDepthBlocks = LodSilhouetteField.DefaultUnderlayDepthBlocks;
    internal float SilhouetteUnderlayWidthBlocks = LodSilhouetteField.DefaultUnderlayWidthBlocks;
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
        && silhouetteShaderOk && silhouetteMeshes.Count > 0 && SilhouetteFogReady;

    bool SilhouetteFogReady => !silhouetteSeamMask.HasEdges || silhouetteFogFieldTexture != 0;

    internal string SilhouetteShaderState => !silhouetteShaderWanted
        ? "not compiled (never switched on)"
        : silhouetteFogVolumeFailed ? "FAILED to draw seam fog, silhouettes are off for this session"
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
        silhouetteFogVolumeProg = capi.Shader.NewShaderProgram();
        silhouetteFogVolumeProg.AssetDomain = "vintagehorizons";
        silhouetteFogVolumeProg.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        silhouetteFogVolumeProg.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
        capi.Shader.RegisterFileShaderProgram("lodsilhouettefogvolume", silhouetteFogVolumeProg);
        bool terrainOk = silhouetteProg.Compile();
        bool fogOk = silhouetteFogVolumeProg.Compile();
        silhouetteShaderOk = terrainOk && fogOk;
        if (terrainOk) LodTerrainSamplerLayout.Configure(silhouetteProg);
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

    /// <summary>
    /// Uploads and selects the Farseer tiles before cached terrain draws, then publishes one
    /// continuous fog field over the exact current ownership edge. Preparing first keeps the fog
    /// centreline synchronized with the geometry that will draw later in the same frame.
    /// </summary>
    void PrepareSilhouetteFrame()
    {
        silhouetteFrameDraws.Clear();
        LodSilhouetteClient? silhouettes = Silhouettes;
        if (silhouettes == null || !silhouettes.Enabled)
        {
            if (silhouetteMeshes.Count > 0 || silhouetteFogFieldTexture != 0
                || silhouetteFogFramebuffer != 0)
                ReleaseSilhouetteMeshes();
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
        if (silhouetteMeshes.Count == 0)
        {
            silhouetteSeamMask.Deactivate();
            ClearSilhouetteFogField();
            return;
        }
        if (!silhouetteShaderWanted)
        {
            silhouetteShaderWanted = true;
            LoadSilhouetteShader();
        }
        if (!silhouetteShaderOk || silhouetteProg == null || silhouetteProg.LoadError
            || silhouetteFogVolumeProg == null || silhouetteFogVolumeProg.LoadError
            || silhouetteFogVolumeFailed)
        {
            ReleaseSilhouetteMeshes();
            // A compile/load/fog failure is a session failure, not a plausible no-fog fallback.
            // Shutting the client feature down also stops offers, decoding, meshing and uploads.
            silhouettes.SetEnabled(false);
            return;
        }

        var rapi = capi.Render;
        if (rapi.FrameWidth == 0)
        {
            silhouetteSeamMask.Deactivate();
            ClearSilhouetteFogField();
            return;
        }
        if (!PublishSilhouetteFogVolume(silhouettes)) return;

        Vec3d cam = capi.World.Player.Entity.CameraPos;
        silhouetteFrustum.Update(rapi.CurrentProjectionMatrix, rapi.CameraMatrixOriginf);
        float reach = silhouettes.ReachBlocks;
        double reachSq = (double)reach * reach;
        float drop = SilhouetteGlobeEffect * reach + 5f
            + SilhouetteUnderlayDepthBlocks;
        const int tileBlocks = LodSilhouetteTile.TileBlocks;
        int minTileX = int.MaxValue, minTileZ = int.MaxValue;
        int maxTileX = int.MinValue, maxTileZ = int.MinValue;
        ulong signatureXor = 0;
        ulong signatureSum = 0;

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

            LodSilhouetteCoverage replacements = SilhouetteStableReplacementCoverage(mesh);
            int resolution = LodSilhouetteField.ResolutionFor(
                ScreenBlockScale, Math.Max(1.0, Math.Sqrt(nearestDistanceSq)),
                SilhouetteOutlineDetail);
            silhouetteFrameDraws.Add(new SilhouetteFrameDraw(
                mesh, (float)relX, (float)relZ, replacements, resolution));
            minTileX = Math.Min(minTileX, mesh.TileX);
            minTileZ = Math.Min(minTileZ, mesh.TileZ);
            maxTileX = Math.Max(maxTileX, mesh.TileX);
            maxTileZ = Math.Max(maxTileZ, mesh.TileZ);
            ulong tileSignature = SilhouetteSeamTileSignature(mesh, replacements);
            signatureXor ^= tileSignature;
            signatureSum += tileSignature;
        }

        if (silhouetteFrameDraws.Count == 0)
        {
            silhouetteSeamMask.Deactivate();
            ClearSilhouetteFogField();
            return;
        }

        ulong signature = signatureXor ^ RotateLeft(signatureSum, 23)
            ^ ((ulong)silhouetteFrameDraws.Count * 0x9E3779B97F4A7C15UL);
        if (silhouetteSeamMask.BeginBuild(
            minTileX, minTileZ, maxTileX, maxTileZ, signature))
        {
            foreach (SilhouetteFrameDraw draw in silhouetteFrameDraws)
                silhouetteSeamMask.SetTile(
                    draw.Mesh.TileX, draw.Mesh.TileZ, draw.Replacements);
            silhouetteSeamMask.CompleteBuild();
            QueueSilhouetteFogVolume(silhouettes);
        }
        else if (silhouetteSeamMask.HasEdges
            && (silhouetteFogGeometryDirty
                || silhouetteFogFieldTexture == 0
                    && silhouetteFogBuildTask == null && silhouetteFogPendingBuild == null))
            QueueSilhouetteFogVolume(silhouettes);
    }

    void DrawSilhouettes()
    {
        SilhouetteDrawsLastFrame = 0;
        SilhouetteHighDrawsLastFrame = 0;
        SilhouetteMediumDrawsLastFrame = 0;
        SilhouetteLowDrawsLastFrame = 0;
        SilhouetteTrianglesLastFrame = 0;
        LodSilhouetteClient? silhouettes = Silhouettes;
        if (silhouettes == null || !silhouettes.Enabled || silhouetteFrameDraws.Count == 0
            || !silhouetteShaderOk || silhouetteProg == null || silhouetteProg.LoadError
            || !SilhouetteFogReady) return;

        var rapi = capi.Render;
        if (rapi.FrameWidth == 0) return;

        // A no-op unless the needed distance changed; a change applies from the next frame.
        ApplyZFar();
        Vec3d cam = capi.World.Player.Entity.CameraPos;
        float reach = silhouettes.ReachBlocks;

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
        p.Uniform("underlaySinkBlocks", SilhouetteUnderlayDepthBlocks);
        p.Uniform("underlayWidthBlocks", SilhouetteUnderlayWidthBlocks);
        p.Uniform("silhouetteSkyTint", SilhouetteSkyTint);
        p.Uniform("silhouetteColorTint", SilhouetteColorTint);
        p.Uniform("silhouetteLightLevelBias", SilhouetteLightLevelBias);
        p.Uniform("silhouetteFadeBias", SilhouetteFadeBias);
        p.Uniform("silhouetteSeaLevel", capi.World.SeaLevel);

        foreach (SilhouetteFrameDraw draw in silhouetteFrameDraws)
        {
            SilhouetteGpuMesh mesh = draw.Mesh;
            p.Uniform("tileOrigin", new Vec3f(draw.RelX, (float)-cam.Y, draw.RelZ));
            p.Uniform("replacementCells0", CoverageWords(draw.Replacements, 0));
            p.Uniform("replacementCells1", CoverageWords(draw.Replacements, 4));
            p.Uniform("replacementCells2", CoverageWords(draw.Replacements, 8));
            p.Uniform("replacementCells3", CoverageWords(draw.Replacements, 12));
            // The apron was shaped from cached-data hints on a worker. It may sink only after
            // every centre-tile hint has a live replacement; a load or eviction race therefore
            // reveals ordinary Farseer height, never a stale trench.
            p.Uniform("underlayActive",
                !mesh.ApronCells.IsEmpty && draw.Replacements.Contains(mesh.ApronCells) ? 1 : 0);
            rapi.RenderMesh(mesh.At(draw.Resolution));
            SilhouetteTrianglesLastFrame += mesh.TrianglesAt(draw.Resolution);
            if (draw.Resolution == LodSilhouetteField.HighQuadsPerEdge)
                SilhouetteHighDrawsLastFrame++;
            else if (draw.Resolution == LodSilhouetteField.MediumQuadsPerEdge)
                SilhouetteMediumDrawsLastFrame++;
            else SilhouetteLowDrawsLastFrame++;
            SilhouetteDrawsLastFrame++;
        }
        p.Stop();
    }

    static ulong SilhouetteSeamTileSignature(
        SilhouetteGpuMesh mesh, LodSilhouetteCoverage replacements)
    {
        ulong hash = 1469598103934665603UL;
        void Add(ulong value)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        Add(unchecked((uint)mesh.TileX));
        Add(unchecked((uint)mesh.TileZ));
        Add(replacements.Word0);
        Add(replacements.Word1);
        Add(replacements.Word2);
        Add(replacements.Word3);
        Add(mesh.FogHeightHash);
        hash ^= hash >> 32;
        hash *= 0xD6E8FEB86659FD93UL;
        return hash ^ (hash >> 32);
    }

    static ulong RotateLeft(ulong value, int bits) =>
        (value << bits) | (value >> (64 - bits));

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
                float[] fogHeightGrid = ExtractSilhouetteFogHeights(result.Meshes.High);
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
                    ApronCells = result.Meshes.CoveredCells,
                    FogHeightGrid = fogHeightGrid,
                    FogHeightHash = SilhouetteFogHeightHash(fogHeightGrid),
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

    static ulong SilhouetteFogHeightHash(float[] heights)
    {
        ulong hash = 1469598103934665603UL;
        foreach (float height in heights)
        {
            hash ^= BitConverter.SingleToUInt32Bits(height);
            hash *= 1099511628211UL;
        }
        return hash;
    }

    MeshRef UploadSilhouetteMesh(LodSilhouetteMeshArrays mesh)
    {
        var data = new MeshData(false);
        data.SetVerticesCount(mesh.VertexCount);
        data.SetIndicesCount(mesh.IndexCount);
        data.xyz = mesh.Xyz;
        data.Uv = mesh.EdgeDistanceUv;
        data.Indices = mesh.Indices;
        return capi.Render.UploadMesh(data);
    }

    static float[] ExtractSilhouetteFogHeights(LodSilhouetteMeshArrays high)
    {
        int edge = LodSilhouetteField.CoverageCellsPerEdge + 1;
        if (high.QuadsPerEdge != LodSilhouetteField.HighQuadsPerEdge
            || high.VertexCount != edge * edge)
            throw new InvalidOperationException("Farseer high mesh no longer matches its 32-block grid.");
        var heights = new float[high.VertexCount];
        for (int i = 0; i < heights.Length; i++) heights[i] = high.Xyz[i * 3 + 1];
        return heights;
    }

    // GLSL integer-vector uniforms are unsafe through this engine API (G16). Four exact 16-bit
    // values fit losslessly in one float vector, cutting 16 scalar uploads down to four calls.
    static Vec4f CoverageWords(LodSilhouetteCoverage coverage, int first) => new(
        coverage.ShortWord(first), coverage.ShortWord(first + 1),
        coverage.ShortWord(first + 2), coverage.ShortWord(first + 3));

    /// <summary>
    /// Picks the stable resident owner for one Farseer cell. The wanted level is the normal
    /// distance choice. A missing target leaves the renderer on a coarser parent when one exists;
    /// only a missing parent chain can expose a finer descendant. Camera direction is deliberately
    /// absent from the decision.
    /// </summary>
    internal static int SelectStableSilhouetteLevel(
        ReadOnlySpan<LodSilhouetteCoverage> residentByLevel,
        int wanted, int cellX, int cellZ)
    {
        if ((uint)wanted >= (uint)residentByLevel.Length) return -1;
        if (residentByLevel[wanted].Covered(cellX, cellZ)) return wanted;
        for (int level = wanted + 1; level < residentByLevel.Length; level++)
            if (residentByLevel[level].Covered(cellX, cellZ)) return level;
        for (int level = wanted - 1; level >= 0; level--)
            if (residentByLevel[level].Covered(cellX, cellZ)) return level;
        return -1;
    }

    /// <summary>
    /// Combines stable resident ground at every LOD level with vanilla's committed readiness.
    ///
    /// The 0.4.99 trial derived LOD ownership from this frame's selected draw commands. Selection
    /// is frustum-dependent, so merely turning the camera moved both the discard edge and the
    /// apron. Coarse commands also claimed their whole footprint even where no ground existed.
    /// Here residency is independent of camera direction, and every installed mesh carries the
    /// exact 32-block ground mask produced with its own geometry. Each cell starts at its
    /// distance-selected level, falls back to a coarser parent while that target is missing, and
    /// only then falls through to finer descendants. An incomplete selected mesh is never
    /// overruled by another level.
    /// Every unknown or partial state remains fail-open to Farseer.
    /// </summary>
    LodSilhouetteCoverage SilhouetteStableReplacementCoverage(SilhouetteGpuMesh mesh)
    {
        Span<LodSilhouetteCoverage> residentByLevel =
            stackalloc LodSilhouetteCoverage[LodWorld.MaxLevel + 1];
        Span<LodSilhouetteCoverage> coveredByLevel =
            stackalloc LodSilhouetteCoverage[LodWorld.MaxLevel + 1];
        residentByLevel.Clear();
        coveredByLevel.Clear();
        int tileBlockX = mesh.TileX * LodSilhouetteTile.TileBlocks;
        int tileBlockZ = mesh.TileZ * LodSilhouetteTile.TileBlocks;
        int tileMaxBlockX = tileBlockX + LodSilhouetteTile.TileBlocks;
        int tileMaxBlockZ = tileBlockZ + LodSilhouetteTile.TileBlocks;
        for (int level = 0; level <= LodWorld.MaxLevel; level++)
        {
            int footprint = LodSection.SectionBlocks << level;
            int firstSectionX = (int)Math.Floor(tileBlockX / (double)footprint);
            int firstSectionZ = (int)Math.Floor(tileBlockZ / (double)footprint);
            int lastSectionX = (int)Math.Floor((tileMaxBlockX - 1) / (double)footprint);
            int lastSectionZ = (int)Math.Floor((tileMaxBlockZ - 1) / (double)footprint);
            for (int sectionZ = firstSectionZ; sectionZ <= lastSectionZ; sectionZ++)
            {
                for (int sectionX = firstSectionX; sectionX <= lastSectionX; sectionX++)
                {
                    long key = LodWorld.SectionKey(level, sectionX, sectionZ);
                    if (!liveMeshStats.TryGetValue(key, out LodLiveMeshStats resident)
                        || resident.OpaqueIndices == 0) continue;

                    int sectionBlockX = sectionX * footprint;
                    int sectionBlockZ = sectionZ * footprint;
                    int firstCellX = Math.Max(0,
                        (sectionBlockX - tileBlockX) / LodMeshGroundCoverage.CellBlocks);
                    int firstCellZ = Math.Max(0,
                        (sectionBlockZ - tileBlockZ) / LodMeshGroundCoverage.CellBlocks);
                    int lastCellX = Math.Min(LodSilhouetteField.CoverageCellsPerEdge,
                        (sectionBlockX + footprint - tileBlockX)
                            / LodMeshGroundCoverage.CellBlocks);
                    int lastCellZ = Math.Min(LodSilhouetteField.CoverageCellsPerEdge,
                        (sectionBlockZ + footprint - tileBlockZ)
                            / LodMeshGroundCoverage.CellBlocks);
                    for (int cellZ = firstCellZ; cellZ < lastCellZ; cellZ++)
                    {
                        for (int cellX = firstCellX; cellX < lastCellX; cellX++)
                        {
                            residentByLevel[level] =
                                residentByLevel[level].WithCovered(cellX, cellZ);
                            int blockX = tileBlockX
                                + cellX * LodMeshGroundCoverage.CellBlocks;
                            int blockZ = tileBlockZ
                                + cellZ * LodMeshGroundCoverage.CellBlocks;
                            int localCellX = (blockX - sectionBlockX)
                                / LodMeshGroundCoverage.CellBlocks;
                            int localCellZ = (blockZ - sectionBlockZ)
                                / LodMeshGroundCoverage.CellBlocks;
                            if (resident.OpaqueGroundCoverage.Covered(localCellX, localCellZ))
                                coveredByLevel[level] =
                                    coveredByLevel[level].WithCovered(cellX, cellZ);
                        }
                    }
                }
            }
        }

        LodSilhouetteCoverage result = LodSilhouetteCoverage.Empty;
        for (int cellZ = 0; cellZ < LodSilhouetteField.CoverageCellsPerEdge; cellZ++)
        {
            for (int cellX = 0; cellX < LodSilhouetteField.CoverageCellsPerEdge; cellX++)
            {
                double cellMinX = tileBlockX + cellX * LodMeshGroundCoverage.CellBlocks;
                double cellMinZ = tileBlockZ + cellZ * LodMeshGroundCoverage.CellBlocks;
                double dx = Math.Max(0, Math.Max(cellMinX - camPos.X,
                    camPos.X - (cellMinX + LodMeshGroundCoverage.CellBlocks)));
                double dz = Math.Max(0, Math.Max(cellMinZ - camPos.Z,
                    camPos.Z - (cellMinZ + LodMeshGroundCoverage.CellBlocks)));
                int wanted = Math.Clamp(LodWorld.WantedLevelForSq(dx * dx + dz * dz),
                    0, LodWorld.MaxLevel);

                int selected = SelectStableSilhouetteLevel(
                    residentByLevel, wanted, cellX, cellZ);
                if (selected >= 0 && coveredByLevel[selected].Covered(cellX, cellZ))
                    result = result.WithCovered(cellX, cellZ);
            }
        }
        if (!readinessMaskActive || readiness == null || readinessMask == null) return result;

        int tileChunkX = mesh.TileX * LodSilhouetteField.CoverageCellsPerEdge;
        int tileChunkZ = mesh.TileZ * LodSilhouetteField.CoverageCellsPerEdge;
        int minX = Math.Max(tileChunkX, readiness.ActiveMinChunkX);
        int minZ = Math.Max(tileChunkZ, readiness.ActiveMinChunkZ);
        int maxX = Math.Min(tileChunkX + LodSilhouetteField.CoverageCellsPerEdge,
            readiness.ActiveMinChunkX + readiness.ActiveWidth);
        int maxZ = Math.Min(tileChunkZ + LodSilhouetteField.CoverageCellsPerEdge,
            readiness.ActiveMinChunkZ + readiness.ActiveDepth);
        for (int z = minZ; z < maxZ; z++)
            for (int x = minX; x < maxX; x++)
                if (readinessMask.ColumnHasReady(x, z))
                    result = result.WithCovered(x - tileChunkX, z - tileChunkZ);
        return result;
    }

    /// <summary>Main thread. Frees every silhouette mesh on the GPU.</summary>
    internal void ReleaseSilhouetteMeshes()
    {
        foreach (SilhouetteGpuMesh mesh in silhouetteMeshes.Values) mesh.Dispose();
        silhouetteMeshes.Clear();
        silhouetteFrameDraws.Clear();
        silhouetteSeamMask.Clear();
        ReleaseSilhouetteFogVolume();
    }
}
