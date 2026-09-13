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
    /// Every value here is an input to Vintage Horizons: a saved option, a live .vh command
    /// setting, or a development/server configuration. Runtime counters, engine state and
    /// hardware information are deliberately absent, so an unchanged screenshot is cheap.
    /// </summary>
    string[] BuildConfigurationColumns()
    {
        var left = new List<string> { "Vintage Horizons " + Mod.Info.Version };
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
        Entry(lines, ".vhdefer", !config.IgnoreOtherLodMods);
        Entry(lines, ".vhfar", DistanceOrUnlimited(config.FarViewDistanceCap));
        AddThresholds(lines, thresholds);
        Entry(lines, ".vhdetail auto", config.AutoLodThresholds);
        Entry(lines, ".vhdetail target", Number(config.LodTargetPixels, "0.00") + " px");
        Entry(lines, ".vhmask", config.ChunkMask);
        Entry(lines, ".vhblend", visual.BlendPipeline);
        Entry(lines, ".vhband", visual.SeamBand);
        Entry(lines, ".vhtexture", visual.BlockTextures);
        Entry(lines, ".vhtint field", visual.ClimateTint);
        Entry(lines, "climate tint range", BlocksPerVoxel(visual.ClimateTintRange));
        Entry(lines, ".vhtint climatewide", visual.FarBiomeColours);
        Entry(lines, ".vhtint leaves", visual.LeafSeasonColours);
        Entry(lines, ".vhslabs", visual.HalfBlockTerrain);
        Entry(lines, ".vhtaa", visual.DistantAntiAliasing);
        Entry(lines, ".vhtaa reach", Number(config.TaaReachPixels, "0.#") + " px");
        Entry(lines, ".vhtaa nudge", Number(config.TaaNudge, "0.00"));
        Entry(lines, ".vhtaa sharpen", Number(config.TaaSharpen, "0.00"));
        Entry(lines, ".vhtaa blend", Number(config.TaaBlend, "0.00"));
        Entry(lines, ".vhtaa still", Number(config.TaaStillBlend, "0.00"));
        Entry(lines, ".vhtaa clip", Number(config.TaaClip, "0.00"));
        Entry(lines, ".vhwater fresnel", visual.WaterEffects);
        Entry(lines, ".vhwater depth", config.WaterDepthShading);
        Entry(lines, ".vhwater alpha", Number(config.WaterMaxAlpha, "0.00"));
        Entry(lines, ".vhwater clarity", Number(config.WaterClarity, "0.0") + " blocks");
        Entry(lines, ".vhwater wave", config.WaterSeamStripBlocks + " blocks");
        Entry(lines, ".vhwater walls", !config.SuppressUnknownWaterWalls);
        Entry(lines, ".vhgpu", config.GpuArenas);
        Entry(lines, "GPU indirect", config.IndirectDraw);
        Entry(lines, "GPU depth cull", config.DepthCull);
        Entry(lines, "GPU late depth", config.LateDepthPicture);
    }

    void AddLiveClientConfiguration(List<string> lines)
    {
        int[] thresholds = LodWorld.GetLevelThresholds();

        Heading(lines, "CLIENT CORE / SHAPE");
        Entry(lines, ".vhdefer", !config.IgnoreOtherLodMods);
        Entry(lines, ".vhfar", DistanceOrUnlimited(renderer.FarViewDistanceCap));
        AddThresholds(lines, thresholds);
        Entry(lines, ".vhdetail auto", renderer.AutoLodThresholds);
        Entry(lines, ".vhdetail target", Number(renderer.LodTargetPixels, "0.00") + " px");
        Entry(lines, ".vhmask", renderer.ChunkMaskEnabled);
        Entry(lines, ".vhgap", renderer.HandoffGapLog);
        Entry(lines, ".vhgap release", renderer.HandoffGapRelease);
        Entry(lines, ".vhsky", renderer.SkyWitness);
        Entry(lines, ".vhsky verify", renderer.SkyMaskVerify);
        Entry(lines, ".vhlaterange", renderer.LateRangeCheck);
        Entry(lines, ".vhblend", renderer.BlendPipelineEnabled);
        Entry(lines, ".vhnear", Number(renderer.NearSuppressionBlocks, "0") + " blocks");
        Entry(lines, ".vhslabs", HalfBlockMode(renderer));
        Entry(lines, ".vhcutout", renderer.CutoutBlocksCover);

        Heading(lines, ".VHBAND");
        Entry(lines, ".vhband", renderer.BandEnabled);
        Entry(lines, ".vhband shape", renderer.BandDebugPaint);
        Entry(lines, ".vhband width", Number(renderer.BandWidthBlocks, "0") + " blocks");
        Entry(lines, ".vhband step", Number(renderer.BandStepBlocks, "0") + " blocks");
        Entry(lines, ".vhband inner", renderer.BandInnerDerived
            ? "auto (" + Number(renderer.BandInnerBlocks, "0") + " blocks)"
            : Number(renderer.BandInnerBlocks, "0") + " blocks (pinned)");
        Entry(lines, ".vhband strength", Number(renderer.BandStrength, "0.00"));
        Entry(lines, ".vhband speckle", Number(renderer.BandSpeckle, "0.00"));
        Entry(lines, ".vhband darken", Number(renderer.BandDarken, "0.00"));
        Entry(lines, ".vhband saturate", Number(renderer.BandSaturate, "0.00"));
        Entry(lines, ".vhband overlap", Number(renderer.OverlapMarginBlocks, "0") + " blocks");
        Entry(lines, ".vhband sink", renderer.BandSinkVoxels + "/16 block");

        Heading(lines, "TEXTURE / TINT");
        Entry(lines, ".vhtexture", renderer.BlockTexturesEnabled);
        Entry(lines, ".vhtexture strength", Number(renderer.BlockTextureStrength, "0.00"));
        Entry(lines, ".vhtexture detail", Number(renderer.BlockTextureDetail, "0.0"));
        Entry(lines, ".vhtexture mips", Number(renderer.BlockTextureMipLimit, "0.0"));
        Entry(lines, ".vhtint field", renderer.ClimateFieldTint);
        Entry(lines, ".vhtint climatewide", renderer.ClimateFadeBlocks <= 0f);
        Entry(lines, "climate tint range", BlocksPerVoxel(renderer.ClimateFadeBlocks));
        Entry(lines, ".vhtint leaves", renderer.LeafSeasonVariation > 0f);
        Entry(lines, "leaf season amount", Number(renderer.LeafSeasonVariation, "0.00"));
        Entry(lines, ".vhtint leafwide", renderer.LeafSeasonFadeBlocks > 2f);
        Entry(lines, "leaf fade", BlocksPerVoxel(renderer.LeafSeasonFadeBlocks));
        Entry(lines, ".vhtint leafcheap", renderer.LeafSeasonCheapRow);

        Heading(lines, "LIGHTING");
        Entry(lines, ".vhtoplight", renderer.FlatTopLight);
        Entry(lines, ".vhlight sun", renderer.LightSunLevel);
        Entry(lines, ".vhlight moondir", renderer.LightMoonDirection);
        Entry(lines, ".vhlight ramp", renderer.LightVanillaRamp);
        Entry(lines, ".vhlight ambient", renderer.LightAmbientColor);
        Entry(lines, ".vhlight boost", renderer.LightDayBoost);
        Entry(lines, ".vhlight sky", renderer.LightSkyDayLight);
    }

    void AddHandoffConfiguration(List<string> lines)
    {
        Heading(lines, "HANDOFF / CULLING");
        Entry(lines, ".vhpaint", renderer.MaskDebugPaint);
        Entry(lines, ".vhskip", renderer.WholeMeshSkip);
        Entry(lines, ".vhbackface", renderer.OpaqueBackfaceCulling);
        Entry(lines, "section height cull", renderer.SectionHeightCulling);
        Entry(lines, ".vhsubtree", renderer.SubtreeHeightCulling);
        Entry(lines, ".vhcavecull", renderer.CaveCulling);
        Entry(lines, "cave cull reach", renderer.CaveCullReach + " blocks");
        Entry(lines, ".vhfront", renderer.OpaqueFrontToBack);
        Entry(lines, ".vhocclusion", renderer.OcclusionCullingEnabled);
        Entry(lines, ".vhtemporal", renderer.TemporalOcclusionEnabled);
        Entry(lines, ".vhtemporalprofile", renderer.TemporalOcclusionProfileName);
        Entry(lines, ".vhgeom", renderer.GeometryOwnershipRule);
        Entry(lines, ".vhexact", renderer.ExactDrawRange);
    }

    void AddWaterConfiguration(List<string> lines)
    {
        Heading(lines, ".VHWATER");
        Entry(lines, ".vhwater near", renderer.WaterNearChunks + " chunks");
        Entry(lines, ".vhwater edge", renderer.WaterEdgeChunks < 0
            ? "auto" : renderer.WaterEdgeChunks + " chunks");
        Entry(lines, ".vhwater pipeline", renderer.WaterOitEnabled ? "oit" : "opaque");
        Entry(lines, ".vhwater surface", renderer.WaterSurfaceVisible);
        Entry(lines, ".vhwater texture", renderer.WaterSurfaceTexture);
        Entry(lines, ".vhwater fresnel", renderer.WaterFresnel);
        Entry(lines, ".vhwater glint", Number(renderer.WaterGlint, "0.00"));
        Entry(lines, ".vhwater foam", Number(renderer.WaterFoam, "0.00"));
        Entry(lines, "water shallowness", Number(renderer.WaterShallowness, "0.00"));
        Entry(lines, ".vhwater depth", renderer.WaterDepthShading);
        Entry(lines, ".vhwater alpha", Number(renderer.WaterMaxAlpha, "0.00"));
        Entry(lines, ".vhwater clarity", Number(renderer.WaterClarity, "0.0") + " blocks");
        Entry(lines, ".vhwater wave", renderer.WaterSeamStripBlocks + " blocks");
        Entry(lines, "water wave reach", Number(renderer.WaterWaveReachBlocks, "0") + " blocks");
        Entry(lines, ".vhwater walls", !renderer.SuppressUnknownWaterWalls);
        Entry(lines, ".vhwater murk", renderer.WaterGroundMurk);
        Entry(lines, ".vhwater coverage", renderer.WaterDepthCoverage);
        Entry(lines, ".vhwater junction", renderer.WaterJunctionDepth);
        Entry(lines, ".vhwater repair", renderer.WaterDepthRepair);
    }

    void AddGpuConfiguration(List<string> lines)
    {
        Heading(lines, "GPU");
        Entry(lines, ".vhgpu", renderer.GpuShadowRequested);
        Entry(lines, "GPU indirect", renderer.IndirectDrawEnabled);
        Entry(lines, "GPU packed", renderer.PackedDrawEnabled);
        Entry(lines, "GPU clusters", renderer.ClusterDrawEnabled);
        Entry(lines, "GPU depth cull", renderer.GpuCullEnabled);
        Entry(lines, ".vhhzb", renderer.DepthPyramidEnabled);
        Entry(lines, "GPU late depth", renderer.LateDepthPyramid);
        Entry(lines, ".vhtaa", renderer.DistantAntiAliasing);
        Entry(lines, ".vhtaa show", renderer.TaaShowWeight);
        Entry(lines, ".vhtaa reach", Number(renderer.TaaReachPixels, "0.#") + " px");
        Entry(lines, ".vhtaa nudge", Number(renderer.TaaNudge, "0.00"));
        Entry(lines, ".vhtaa sharpen", Number(renderer.TaaSharpen, "0.00"));
        Entry(lines, ".vhtaa blend", Number(renderer.TaaFeedback, "0.00"));
        Entry(lines, ".vhtaa still", Number(renderer.TaaStillFeedback, "0.00"));
        Entry(lines, ".vhtaa clip", Number(renderer.TaaClip, "0.00"));
    }

    void AddDevelopmentConfiguration(List<string> lines)
    {
        Heading(lines, "DEVELOPMENT MOD");
        Entry(lines, ".vhdiag", diagnosticsEnabled);
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
            Entry(lines, $".vhdetail L{i + 1}", thresholds[i] + " blocks");
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
