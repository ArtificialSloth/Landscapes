using System.Globalization;

namespace VintageHorizons;

public partial class VintageHorizonsModSystem
{
    const double Mebibyte = 1024 * 1024;
    VintageHorizonsDiagnosticsHud? diagnosticsHud;
    bool diagnosticsEnabled = false;

    void InstallDiagnostics()
    {
        diagnosticsHud = new VintageHorizonsDiagnosticsHud(capi, BuildConfigurationColumns);
        capi.Event.LevelFinalize += OnDiagnosticsLevelFinalize;
        capi.Event.LeaveWorld += OnDiagnosticsLeaveWorld;
    }

    void OnDiagnosticsLevelFinalize()
    {
        if (!diagnosticsEnabled) return;
        diagnosticsHud?.RefreshNow();
        diagnosticsHud?.TryOpen(false);
    }

    bool SetDiagnosticsEnabled(bool enabled)
    {
        diagnosticsEnabled = enabled;
        if (!enabled)
        {
            diagnosticsHud?.TryClose();
            return false;
        }

        diagnosticsHud?.RefreshNow();
        return diagnosticsHud?.TryOpen(false) == true;
    }

    void OnDiagnosticsLeaveWorld() => diagnosticsHud?.TryClose();

    void DisposeDiagnostics()
    {
        capi.Event.LevelFinalize -= OnDiagnosticsLevelFinalize;
        capi.Event.LeaveWorld -= OnDiagnosticsLeaveWorld;
        diagnosticsHud?.TryClose();
        diagnosticsHud?.Dispose();
        diagnosticsHud = null;
    }

    /// <summary>
    /// Every value here is an input to Doden's Landscapes: a saved option, a live .dl command
    /// setting, or a development/server configuration. Runtime counters, engine state and
    /// hardware information are deliberately absent, so an unchanged screenshot is cheap.
    /// </summary>
    string[] BuildConfigurationColumns()
    {
        var left = new List<string> { "Doden's Landscapes " + Mod.Info.Version };
        var right = new List<string>();

        if (renderer is null)
        {
            AddSavedClientConfiguration(left);
        }
        else
        {
            AddLiveClientConfiguration(left);
            AddHandoffConfiguration(right);
            AddWaterConfiguration(right);
            AddGpuConfiguration(right);
        }

        AddDevelopmentConfiguration(right);
        AddServerConfiguration(left);
        return new[] { string.Join('\n', left), string.Join('\n', right) };
    }

    void AddSavedClientConfiguration(List<string> lines)
    {
        LodVisualSettings visual = ConfiguredVisualSettings();
        int[] thresholds = LodWorld.NormalizeLevelThresholds(
            config.LodThresholds, config.DetailDistance);

        Heading(lines, "SAVED CLIENT MOD");
        Entry(lines, ".dldefer", !config.IgnoreOtherLodMods);
        Entry(lines, ".dlfar", DistanceOrUnlimited(config.FarViewDistanceCap));
        AddThresholds(lines, thresholds);
        Entry(lines, ".dldetail auto", config.AutoLodThresholds);
        Entry(lines, ".dldetail target", Number(config.LodTargetPixels, "0.00") + " px");
        Entry(lines, ".dlmask", config.ChunkMask);
        Entry(lines, ".dlblend", visual.BlendPipeline);
        Entry(lines, ".dlband", visual.SeamBand);
        Entry(lines, ".dltexture", visual.BlockTextures);
        Entry(lines, ".dltint field", visual.ClimateTint);
        Entry(lines, "climate tint range", BlocksPerVoxel(visual.ClimateTintRange));
        Entry(lines, ".dltint climatewide", visual.FarBiomeColours);
        Entry(lines, ".dltint leaves", visual.LeafSeasonColours);
        Entry(lines, ".dlslabs", visual.HalfBlockTerrain);
        Entry(lines, ".dltaa", visual.DistantAntiAliasing);
        Entry(lines, ".dltaa reach", Number(config.TaaReachPixels, "0.#") + " px");
        Entry(lines, ".dltaa nudge", Number(config.TaaNudge, "0.00"));
        Entry(lines, ".dltaa sharpen", Number(config.TaaSharpen, "0.00"));
        Entry(lines, ".dltaa blend", Number(config.TaaBlend, "0.00"));
        Entry(lines, ".dltaa still", Number(config.TaaStillBlend, "0.00"));
        Entry(lines, ".dltaa clip", Number(config.TaaClip, "0.00"));
        Entry(lines, ".dlwater fresnel", visual.WaterEffects);
        Entry(lines, ".dlwater depth", config.WaterDepthShading);
        Entry(lines, ".dlwater alpha", Number(config.WaterMaxAlpha, "0.00"));
        Entry(lines, ".dlwater clarity", Number(config.WaterClarity, "0.0") + " blocks");
        Entry(lines, ".dlwater wave", config.WaterSeamStripBlocks + " blocks");
        Entry(lines, ".dlwater walls", !config.SuppressUnknownWaterWalls);
        Entry(lines, ".dlgpu", config.GpuArenas);
        Entry(lines, "GPU indirect", config.IndirectDraw);
        Entry(lines, "GPU depth cull", config.DepthCull);
        Entry(lines, "GPU late depth", config.LateDepthPicture);
    }

    void AddLiveClientConfiguration(List<string> lines)
    {
        int[] thresholds = LodWorld.GetLevelThresholds();

        Heading(lines, "CLIENT CORE / SHAPE");
        Entry(lines, ".dldefer", !config.IgnoreOtherLodMods);
        Entry(lines, ".dlfar", DistanceOrUnlimited(renderer.FarViewDistanceCap));
        AddThresholds(lines, thresholds);
        Entry(lines, ".dldetail auto", renderer.AutoLodThresholds);
        Entry(lines, ".dldetail target", Number(renderer.LodTargetPixels, "0.00") + " px");
        Entry(lines, ".dlmask", renderer.ChunkMaskEnabled);
        Entry(lines, ".dlgap", renderer.HandoffGapLog);
        Entry(lines, ".dlgap release", renderer.HandoffGapRelease);
        Entry(lines, ".dlsky", renderer.SkyWitness);
        Entry(lines, ".dlsky verify", renderer.SkyMaskVerify);
        Entry(lines, ".dllaterange", renderer.LateRangeCheck);
        Entry(lines, ".dlblend", renderer.BlendPipelineEnabled);
        Entry(lines, ".dlnear", Number(renderer.NearSuppressionBlocks, "0") + " blocks");
        Entry(lines, ".dlslabs", HalfBlockMode(renderer));
        Entry(lines, ".dlcutout", renderer.CutoutBlocksCover);

        Heading(lines, ".VHBAND");
        Entry(lines, ".dlband", renderer.BandEnabled);
        Entry(lines, ".dlband shape", renderer.BandDebugPaint);
        Entry(lines, ".dlband width", Number(renderer.BandWidthBlocks, "0") + " blocks");
        Entry(lines, ".dlband step", Number(renderer.BandStepBlocks, "0") + " blocks");
        Entry(lines, ".dlband inner", renderer.BandInnerDerived
            ? "auto (" + Number(renderer.BandInnerBlocks, "0") + " blocks)"
            : Number(renderer.BandInnerBlocks, "0") + " blocks (pinned)");
        Entry(lines, ".dlband strength", Number(renderer.BandStrength, "0.00"));
        Entry(lines, ".dlband speckle", Number(renderer.BandSpeckle, "0.00"));
        Entry(lines, ".dlband darken", Number(renderer.BandDarken, "0.00"));
        Entry(lines, ".dlband saturate", Number(renderer.BandSaturate, "0.00"));
        Entry(lines, ".dlband overlap", Number(renderer.OverlapMarginBlocks, "0") + " blocks");
        Entry(lines, ".dlband sink", renderer.BandSinkVoxels + "/16 block");

        Heading(lines, "TEXTURE / TINT");
        Entry(lines, ".dltexture", renderer.BlockTexturesEnabled);
        Entry(lines, ".dltexture strength", Number(renderer.BlockTextureStrength, "0.00"));
        Entry(lines, ".dltexture detail", Number(renderer.BlockTextureDetail, "0.0"));
        Entry(lines, ".dltexture mips", Number(renderer.BlockTextureMipLimit, "0.0"));
        Entry(lines, ".dltint field", renderer.ClimateFieldTint);
        Entry(lines, ".dltint climatewide", renderer.ClimateFadeBlocks <= 0f);
        Entry(lines, "climate tint range", BlocksPerVoxel(renderer.ClimateFadeBlocks));
        Entry(lines, ".dltint leaves", renderer.LeafSeasonVariation > 0f);
        Entry(lines, "leaf season amount", Number(renderer.LeafSeasonVariation, "0.00"));
        Entry(lines, ".dltint leafwide", renderer.LeafSeasonFadeBlocks > 2f);
        Entry(lines, "leaf fade", BlocksPerVoxel(renderer.LeafSeasonFadeBlocks));
        Entry(lines, ".dltint leafcheap", renderer.LeafSeasonCheapRow);

        Heading(lines, "LIGHTING");
        Entry(lines, ".dltoplight", renderer.FlatTopLight);
        Entry(lines, ".dllight sun", renderer.LightSunLevel);
        Entry(lines, ".dllight moondir", renderer.LightMoonDirection);
        Entry(lines, ".dllight ramp", renderer.LightVanillaRamp);
        Entry(lines, ".dllight ambient", renderer.LightAmbientColor);
        Entry(lines, ".dllight boost", renderer.LightDayBoost);
        Entry(lines, ".dllight sky", renderer.LightSkyDayLight);
    }

    void AddHandoffConfiguration(List<string> lines)
    {
        Heading(lines, "HANDOFF / CULLING");
        Entry(lines, ".dlstandin", renderer.StandInFix);
        Entry(lines, ".dlpaint", renderer.MaskDebugPaint);
        Entry(lines, ".dlskip", renderer.WholeMeshSkip);
        Entry(lines, ".dlbackface", renderer.OpaqueBackfaceCulling);
        Entry(lines, "section height cull", renderer.SectionHeightCulling);
        Entry(lines, ".dlsubtree", renderer.SubtreeHeightCulling);
        Entry(lines, ".dlcavecull", renderer.CaveCulling);
        Entry(lines, "cave cull reach", renderer.CaveCullReach + " blocks");
        Entry(lines, ".dlfront", renderer.OpaqueFrontToBack);
        Entry(lines, ".dlocclusion", renderer.OcclusionCullingEnabled);
        Entry(lines, ".dltemporal", renderer.TemporalOcclusionEnabled);
        Entry(lines, ".dltemporalprofile", renderer.TemporalOcclusionProfileName);
        Entry(lines, ".dlgeom", renderer.GeometryOwnershipRule);
        Entry(lines, ".dlexact", renderer.ExactDrawRange);
    }

    void AddWaterConfiguration(List<string> lines)
    {
        Heading(lines, ".VHWATER");
        Entry(lines, ".dlwater near", renderer.WaterNearChunks + " chunks");
        Entry(lines, ".dlwater edge", renderer.WaterEdgeChunks < 0
            ? "auto" : renderer.WaterEdgeChunks + " chunks");
        Entry(lines, ".dlwater pipeline", renderer.WaterOitEnabled ? "oit" : "opaque");
        Entry(lines, ".dlwater surface", renderer.WaterSurfaceVisible);
        Entry(lines, ".dlwater texture", renderer.WaterSurfaceTexture);
        Entry(lines, ".dlwater fresnel", renderer.WaterFresnel);
        Entry(lines, ".dlwater glint", Number(renderer.WaterGlint, "0.00"));
        Entry(lines, ".dlwater foam", Number(renderer.WaterFoam, "0.00"));
        Entry(lines, "water shallowness", Number(renderer.WaterShallowness, "0.00"));
        Entry(lines, ".dlwater depth", renderer.WaterDepthShading);
        Entry(lines, ".dlwater alpha", Number(renderer.WaterMaxAlpha, "0.00"));
        Entry(lines, ".dlwater clarity", Number(renderer.WaterClarity, "0.0") + " blocks");
        Entry(lines, ".dlwater wave", renderer.WaterSeamStripBlocks + " blocks");
        Entry(lines, "water wave reach", Number(renderer.WaterWaveReachBlocks, "0") + " blocks");
        Entry(lines, ".dlwater walls", !renderer.SuppressUnknownWaterWalls);
        Entry(lines, ".dlwater murk", renderer.WaterGroundMurk);
        Entry(lines, ".dlwater coverage", renderer.WaterDepthCoverage);
        Entry(lines, ".dlwater junction", renderer.WaterJunctionDepth);
        Entry(lines, ".dlwater repair", renderer.WaterDepthRepair);
    }

    void AddGpuConfiguration(List<string> lines)
    {
        Heading(lines, "GPU");
        Entry(lines, ".dlgpu", renderer.GpuShadowRequested);
        Entry(lines, "GPU indirect", renderer.IndirectDrawEnabled);
        Entry(lines, "GPU packed", renderer.PackedDrawEnabled);
        Entry(lines, "GPU clusters", renderer.ClusterDrawEnabled);
        Entry(lines, "GPU depth cull", renderer.GpuCullEnabled);
        Entry(lines, ".dlhzb", renderer.DepthPyramidEnabled);
        Entry(lines, "GPU late depth", renderer.LateDepthPyramid);
        Entry(lines, ".dltaa", renderer.DistantAntiAliasing);
        Entry(lines, ".dltaa show", renderer.TaaShowWeight);
        Entry(lines, ".dltaa reach", Number(renderer.TaaReachPixels, "0.#") + " px");
        Entry(lines, ".dltaa nudge", Number(renderer.TaaNudge, "0.00"));
        Entry(lines, ".dltaa sharpen", Number(renderer.TaaSharpen, "0.00"));
        Entry(lines, ".dltaa blend", Number(renderer.TaaFeedback, "0.00"));
        Entry(lines, ".dltaa still", Number(renderer.TaaStillFeedback, "0.00"));
        Entry(lines, ".dltaa clip", Number(renderer.TaaClip, "0.00"));
    }

    void AddDevelopmentConfiguration(List<string> lines)
    {
        Heading(lines, "DEVELOPMENT MOD");
        Entry(lines, ".dldiag", diagnosticsEnabled);
        Entry(lines, "allocation telemetry", allocationTelemetryEnabled);
        Entry(lines, "auto explore", autoExplore);
        Entry(lines, "auto unpause", renderer?.AutoUnpause
            ?? Environment.GetEnvironmentVariable("VINTAGEHORIZONS_AUTOUNPAUSE") == "1");
        Entry(lines, "auto command", EnvironmentSetting("VINTAGEHORIZONS_AUTOCMD", "off"));
        Entry(lines, "explore hop", ExploreHopBlocks + " blocks");
        Entry(lines, "creative test start",
            Environment.GetEnvironmentVariable("VINTAGEHORIZONS_CREATIVE") == "1");
        Entry(lines, "cave audit",
            Environment.GetEnvironmentVariable("VINTAGEHORIZONS_CAVE_AUDIT") == "1");
        Entry(lines, "GPU timing telemetry",
            Environment.GetEnvironmentVariable("VINTAGEHORIZONS_GPU_STATS") == "1");
        Entry(lines, "GPU renderer pref",
            EnvironmentSetting("VINTAGEHORIZONS_GPU_RENDERER", "default"));
        Entry(lines, "GPU arena mode",
            LodGpuArenaPolicy.Parse(Environment.GetEnvironmentVariable(
                "VINTAGEHORIZONS_GPU_ARENA")).ToString().ToLowerInvariant());
        Entry(lines, "GPU arena ceiling",
            renderer is null ? EnvironmentSetting("VINTAGEHORIZONS_GPU_ARENA_MB", "automatic")
            : Number(LodGpuArenaPolicy.CeilingBytesFor(
                renderer.FarViewDistanceCap,
                Environment.GetEnvironmentVariable("VINTAGEHORIZONS_GPU_ARENA_MB")) / Mebibyte,
                "0") + " MiB");
        Entry(lines, "GPU arena page",
            Number(LodGpuArenaPolicy.VertexPageBytes / Mebibyte, "0") + " MiB");
        Entry(lines, "GPU failure injection",
            EnvironmentSetting("VINTAGEHORIZONS_GPU_INJECT_FAILURE", "off"));
        Entry(lines, "server dev tools",
            Environment.GetEnvironmentVariable("VINTAGEHORIZONS_DEVTOOLS") == "1");
        Entry(lines, "mip pause marker",
            HasEnvironmentValue("VINTAGEHORIZONS_INTERRUPT_MIP_MARKER"));
        Entry(lines, "mip release marker",
            HasEnvironmentValue("VINTAGEHORIZONS_INTERRUPT_MIP_RELEASE"));
        Entry(lines, "offer-miss test hook",
            HasEnvironmentValue("VINTAGEHORIZONS_TEST_LOCAL_OFFER_MISS_MARKER"));
        Entry(lines, "offer-install test hook",
            HasEnvironmentValue("VINTAGEHORIZONS_TEST_LOCAL_OFFER_INSTALL_MARKER"));
    }

    void AddServerConfiguration(List<string> lines)
    {
        Heading(lines, "SERVER MOD CONFIG");
        if (assist?.ServerSettings is not { } server)
        {
            Entry(lines, "settings snapshot", "unavailable");
            return;
        }

        Entry(lines, "capture", server.EnableCapture);
        Entry(lines, "serving", server.EnableServing);
        Entry(lines, "serve radius", DistanceOrUnlimited(server.ServeRadiusBlocks));
        Entry(lines, "serve/player/sec", server.MaxSectionsPerSecondPerPlayer.ToString(CultureInfo.InvariantCulture));
        Entry(lines, "serve total/sec", server.MaxSectionsPerSecondTotal.ToString(CultureInfo.InvariantCulture));
        Entry(lines, "savegame sweep", server.SweepSavegame);
        Entry(lines, "sweep radius", server.SweepRadiusChunks + " chunks");
        Entry(lines, "sweep columns/sec", server.SweepColumnsPerSecond.ToString(CultureInfo.InvariantCulture));
        Entry(lines, "snow campaign columns/sec", server.RefreshColumnsPerSecond.ToString(CultureInfo.InvariantCulture));
        Entry(lines, "generate command", server.EnableGenerateCommand);
        Entry(lines, "gen max radius", server.GenerateMaxRadiusChunks + " chunks");
        Entry(lines, "gen default radius", server.GenerateDefaultRadiusChunks + " chunks");
        Entry(lines, "gen columns/sec", server.GenerateColumnsPerSecond.ToString(CultureInfo.InvariantCulture));
        Entry(lines, "gen max in-flight", server.GenerateMaxInFlight.ToString(CultureInfo.InvariantCulture));
        Entry(lines, "pregen radius", server.PregenRadiusChunks + " chunks");
        Entry(lines, "pregen columns/sec", server.PregenColumnsPerSecond.ToString(CultureInfo.InvariantCulture));
    }

    static void AddThresholds(List<string> lines, IReadOnlyList<int> thresholds)
    {
        for (int i = 0; i < thresholds.Count; i++)
            Entry(lines, $".dldetail L{i + 1}", thresholds[i] + " blocks");
    }

    static string HalfBlockMode(LodTerrainRenderer value)
    {
        if (value.HalfBlockLevels <= 0) return "off";
        if (value.SurfaceHalfBlocksOnly) return "surface";
        if (value.HalfBlockLevels >= 8) return "all";
        return "near (" + value.HalfBlockLevels + " levels)";
    }

    static string DistanceOrUnlimited(int blocks) =>
        blocks <= 0 ? "unlimited" : blocks + " blocks";

    static string BlocksPerVoxel(float blocks) =>
        blocks <= 0f ? "all levels" : Number(blocks, "0.#") + " blocks/voxel";

    static string Number(double value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);

    static string EnvironmentSetting(string name, string fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        string oneLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return oneLine.Length <= 42 ? oneLine : oneLine[..39] + "...";
    }

    static bool HasEnvironmentValue(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        && !value.Equals("0", StringComparison.OrdinalIgnoreCase)
        && !value.Equals("off", StringComparison.OrdinalIgnoreCase);

    static void Heading(List<string> lines, string text)
    {
        lines.Add("");
        lines.Add("[ " + text + " ]");
    }

    static void Entry(List<string> lines, string name, bool enabled) =>
        Entry(lines, name, enabled ? "ON" : "off");

    static void Entry(List<string> lines, string name, string value) =>
        lines.Add(name + " = " + value);
}
