using Vintagestory.API.Common;
using VintageHorizons.Silhouette;

namespace VintageHorizons;

/// <summary>
/// Horizon silhouettes (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER P4), available only where the
/// server advertises the feature. The client preference and appearance are persisted.
/// </summary>
public partial class VintageHorizonsModSystem
{
    /// <summary>Null until the renderer starts, or if it failed to.</summary>
    LodSilhouetteClient? silhouettes;

    void CreateSilhouettes()
    {
        silhouettes = new LodSilhouetteClient(Mod.Logger,
            () => assist?.SilhouettesAvailable == true,
            (on, reach, session) => assist?.SendSilhouetteRequest(on, reach, session) == true,
            (keys, session) => assist?.SendSilhouetteTileRequest(keys, session) == true);
        renderer.Silhouettes = silhouettes;
        if (assist != null)
        {
            assist.SilhouetteManifestArrived = silhouettes.OnManifestArrived;
            assist.SilhouetteTileArrived = silhouettes.OnTileArrived;
        }
    }

    void PumpSilhouettes()
    {
        if (silhouettes == null || !silhouettes.Enabled) return;
        if (capi.World.Player?.Entity is not { } player) return;
        silhouettes.Pump(pipeline.World, player.Pos.X, player.Pos.Z, capi.World.SeaLevel);
    }

    /// <summary>
    /// The ordinary client cache has already resolved the current savegame into a safe file name.
    /// Derive P5's sibling path from it without touching a second database while silhouettes are
    /// off. <see cref="LodSilhouetteClient.SetStorePath"/> only records the name until an enabled
    /// client reaches a server that actually advertises the feature.
    /// </summary>
    void SetSilhouetteWorldStore()
    {
        string? cachePath = pipeline.DbPath;
        string? path = cachePath == null
            ? null
            : Path.Combine(Path.GetDirectoryName(cachePath)!,
                Path.GetFileNameWithoutExtension(cachePath) + "-silhouette.db");
        silhouettes?.SetStorePath(path);
    }

    void ResetSilhouettes()
    {
        silhouettes?.Reset();
        renderer?.ReleaseSilhouetteMeshes();
    }

    void RegisterSilhouetteCommand()
    {
        capi.ChatCommands.Create("dlsilhouette")
            .WithDescription("Horizon silhouettes: on, off, log or stats. The command is "
                + "session-only; the settings tab saves the switch.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("action"))
            .HandleWith(args =>
            {
                string action = args.Parsers[0].IsMissing
                    ? "stats"
                    : ((string)args[0]).ToLowerInvariant();
                switch (action)
                {
                    case "on":
                        LodSilhouetteSettings on = CurrentSilhouetteSettings() with { Enabled = true };
                        ApplySilhouetteSettings(on);
                        break;
                    case "off":
                        LodSilhouetteSettings off = CurrentSilhouetteSettings() with { Enabled = false };
                        ApplySilhouetteSettings(off);
                        break;
                    case "log":
                    case "stats":
                        break;
                    default:
                        return TextCommandResult.Error(
                            "[Landscapes] use .dlsilhouette on, off, log or stats");
                }

                // Chat cannot be copied, so the same line always reaches the log.
                string line = "[Landscapes] " + DescribeSilhouettes();
                Mod.Logger.Notification("{0}", line);
                return TextCommandResult.Success(line);
            });

        // A quick A/B switch for judging the seam fog's frame-time cost while playing.
        capi.ChatCommands.Create("dlfog")
            .WithDescription("Seam fog: on, off, or no argument to toggle. Session-only; the "
                + "Farseer tab's Seam fog switch saves it.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("action"))
            .HandleWith(args =>
            {
                if (renderer == null || silhouettes == null)
                    return TextCommandResult.Error(
                        "[Landscapes] seam fog is unavailable: the distant-terrain renderer did not start.");
                LodSilhouetteSettings current = CurrentSilhouetteSettings();
                string action = args.Parsers[0].IsMissing
                    ? (current.FogEnabled ? "off" : "on")
                    : ((string)args[0]).ToLowerInvariant();
                switch (action)
                {
                    case "on":
                    case "off":
                        ApplySilhouetteSettings(current with { FogEnabled = action == "on" });
                        break;
                    case "stats":
                        break;
                    default:
                        return TextCommandResult.Error("[Landscapes] use .dlfog, .dlfog on or .dlfog off");
                }
                string line = "[Landscapes] " + DescribeSeamFog()
                    + " (the command is session-only).";
                Mod.Logger.Notification("{0}", line);
                return TextCommandResult.Success(line);
            });
    }

    string DescribeSeamFog() => !renderer.SilhouetteFogEnabled
        ? "seam fog off: no fog pass is drawn and no fog field is built"
        : $"seam fog on, over {(renderer.SilhouetteFogFollowsTerrain
                ? $"the cached terrain's edge ({renderer.TerrainFogTileCount} tiles)"
                : "the Farseer seam")}: a {renderer.SilhouetteFogStripHeightBlocks:0}-block strip at "
            + $"{renderer.SilhouetteFogSeamStrength * 100f:0}% density, "
            + $"{renderer.SilhouetteFogStripHalfBlocks:0} blocks each side of the seam; the bank "
            + $"at {renderer.SilhouetteFogStrength * 100f:0}%, "
            + $"{renderer.SilhouetteFogSpillBlocks:0}-block spread, "
            + $"{renderer.SilhouetteFogHeightBlocks:0} blocks high, core "
            + $"{renderer.SilhouetteFogCoreBlocks:0} blocks each side "
            + $"(staircase strays {renderer.SilhouetteFogStaircaseDeviation:0.0}); moved fog "
            + $"dissolves over {renderer.SilhouetteFogFadeSeconds:0.0} s; "
            + renderer.SilhouetteFogFieldSummary
            + $"; game fog colour {capi.Ambient.BlendedFogColor.R:0.00},"
            + $"{capi.Ambient.BlendedFogColor.G:0.00},{capi.Ambient.BlendedFogColor.B:0.00} "
            + $"(fog brightness {capi.Ambient.BlendedFogBrightness:0.00}, "
            + $"scene brightness {capi.Ambient.BlendedSceneBrightness:0.00}); fog colour sources: "
            + DescribeFogColourSources();

    /// <summary>
    /// Every ambient modifier's fog colour and weight, base first, in the order the game blends
    /// them. The seam fog takes its colour from the blend, so a modifier pulling it dark shows here.
    /// </summary>
    string DescribeFogColourSources()
    {
        static string One(string name, Vintagestory.API.Common.AmbientModifier? modifier)
        {
            float[]? value = modifier?.FogColor?.Value;
            return value == null || value.Length < 3
                ? name + " none"
                : $"{name} {value[0]:0.00},{value[1]:0.00},{value[2]:0.00} x{modifier!.FogColor.Weight:0.00}";
        }
        var parts = new List<string> { One("base", capi.Ambient.Base) };
        foreach (var pair in capi.Ambient.CurrentModifiers) parts.Add(One(pair.Key, pair.Value));
        return string.Join("; ", parts);
    }

    string DescribeSilhouettes()
    {
        if (silhouettes == null)
            return "horizon silhouettes are unavailable while another LOD mod draws.";
        LodSilhouetteClient s = silhouettes;
        string server = assist?.SilhouettesAvailable != true
            ? "does not offer them"
            : s.RequestSent
                ? $"was asked for a {s.ReachBlocks:N0}-block reach"
                : "offers them";
        long cells = (long)s.TileCount * LodSilhouetteTile.CellCount;
        string fill = cells > 0 ? $"{100.0 * s.KnownCells / cells:0.0}%" : "0.0%";
        string sources = $"sources real {s.CapturedCells:N0}, preview {s.PreviewCells:N0}, "
            + $"accelerator {s.AcceleratorCells:N0}";
        string store = s.StoreOpen
            ? $"client store {s.StoredTiles:N0} tiles ({s.TilesLoadedFromStore:N0} loaded this session, "
                + $"{s.StorePendingSaves:N0} saves waiting)"
            : "client store closed";
        return $"horizon silhouettes {(s.Enabled ? "on" : "off")} (the command is session-only). "
            + $"The server {server}. {s.TileCount} tiles held of {s.OfferedTiles} offered "
            + $"({s.TilesInFlight} requested, {s.TilesRefused} refused, {s.TilesRejected} corrupt), "
            + $"{s.KnownCells:N0} cells with heights ({fill} fill; {sources}); {store}. "
            + $"{renderer.SilhouetteMeshCount} meshes ({s.PendingMeshes} waiting, {s.EmptyMeshes} "
            + $"with nothing to draw), {renderer.SilhouetteDrawsLastFrame} drawn last frame "
            + $"({renderer.SilhouetteHighDrawsLastFrame} high, "
            + $"{renderer.SilhouetteMediumDrawsLastFrame} medium, "
            + $"{renderer.SilhouetteLowDrawsLastFrame} low; "
            + $"{renderer.SilhouetteTrianglesLastFrame:N0} triangles at "
            + $"{renderer.SilhouetteOutlineDetail * 100f:0}% geometry detail, "
            + $"{s.HillSoftness * 100f:0}% hill softness, "
            + $"{renderer.SilhouetteUnderlayDepthBlocks:0} deep/"
            + $"{renderer.SilhouetteUnderlayWidthBlocks:0} wide edge apron; "
            + $"{DescribeSeamFog()}). "
            + $"Shader: {renderer.SilhouetteShaderState}.";
    }

    LodSilhouetteSettings ConfiguredSilhouetteSettings() => new LodSilhouetteSettings(
        config.HorizonSilhouettes,
        config.SilhouetteReachBlocks,
        config.SilhouetteSkyTint,
        config.SilhouetteTintRed,
        config.SilhouetteTintGreen,
        config.SilhouetteTintBlue,
        config.SilhouetteTintStrength,
        config.SilhouetteLightLevelBias,
        config.SilhouetteFadeBias,
        config.SilhouetteGlobeEffect,
        config.SilhouetteUnderlayDepthBlocks,
        config.SilhouetteUnderlayWidthBlocks,
        config.SilhouetteOutlineDetail,
        config.SilhouetteHillSoftness,
        config.SilhouetteFog,
        config.SilhouetteFogStrength,
        config.SilhouetteFogSeamStrength,
        config.SilhouetteFogCoverage,
        config.SilhouetteFogSpillBlocks,
        config.SilhouetteFogHeightBlocks,
        config.SilhouetteFogFloorDepthBlocks,
        config.SilhouetteFogBillowSizeBlocks,
        config.SilhouetteFogDriftSpeed,
        config.SilhouetteFogLayerDetail,
        config.SilhouetteFogEdgeSoftness,
        config.SilhouetteFogStripHeightBlocks,
        config.SilhouetteFogFadeSeconds).Normalize();

    LodSilhouetteSettings CurrentSilhouetteSettings()
    {
        if (renderer == null || silhouettes == null) return ConfiguredSilhouetteSettings();
        return new LodSilhouetteSettings(
            silhouettes.Enabled,
            silhouettes.ReachBlocks,
            renderer.SilhouetteSkyTint,
            renderer.SilhouetteColorTint.R,
            renderer.SilhouetteColorTint.G,
            renderer.SilhouetteColorTint.B,
            renderer.SilhouetteColorTint.A,
            renderer.SilhouetteLightLevelBias,
            renderer.SilhouetteFadeBias,
            renderer.SilhouetteGlobeEffect,
            renderer.SilhouetteUnderlayDepthBlocks,
            renderer.SilhouetteUnderlayWidthBlocks,
            renderer.SilhouetteOutlineDetail,
            silhouettes.HillSoftness,
            renderer.SilhouetteFogEnabled,
            renderer.SilhouetteFogStrength,
            renderer.SilhouetteFogSeamStrength,
            renderer.SilhouetteFogCoverage,
            renderer.SilhouetteFogSpillBlocks,
            renderer.SilhouetteFogHeightBlocks,
            renderer.SilhouetteFogFloorDepthBlocks,
            renderer.SilhouetteFogBillowSizeBlocks,
            renderer.SilhouetteFogDriftSpeed,
            renderer.SilhouetteFogLayerDetail,
            renderer.SilhouetteFogEdgeSoftness,
            renderer.SilhouetteFogStripHeightBlocks,
            renderer.SilhouetteFogFadeSeconds).Normalize();
    }

    void PreviewSilhouetteSettings(LodSilhouetteSettings value)
    {
        if (renderer != null) ApplySilhouetteSettings(value);
    }

    void ApplySilhouetteSettings(LodSilhouetteSettings value)
    {
        value = value.Normalize();
        if (renderer == null || silhouettes == null) return;
        renderer.SilhouetteSkyTint = value.SkyTint;
        renderer.SilhouetteColorTint = new Vintagestory.API.MathTools.Vec4f(
            value.TintRed, value.TintGreen, value.TintBlue, value.TintStrength);
        renderer.SilhouetteLightLevelBias = value.LightLevelBias;
        renderer.SilhouetteFadeBias = value.FadeBias;
        renderer.SilhouetteGlobeEffect = value.GlobeEffect;
        renderer.SilhouetteUnderlayDepthBlocks = value.UnderlayDepthBlocks;
        renderer.SilhouetteUnderlayWidthBlocks = value.UnderlayWidthBlocks;
        renderer.SilhouetteOutlineDetail = value.OutlineDetail;
        renderer.SetSilhouetteFogSettings(value.FogEnabled,
            value.FogStrength, value.FogSeamStrength, value.FogCoverage, value.FogSpillBlocks,
            value.FogHeightBlocks, value.FogFloorDepthBlocks,
            value.FogBillowSizeBlocks, value.FogDriftSpeed, value.FogLayerDetail,
            value.FogEdgeSoftness, value.FogStripHeightBlocks, value.FogFadeSeconds);
        silhouettes.SetReachBlocks(value.ReachBlocks);
        silhouettes.SetHillSoftness(value.HillSoftness);
        silhouettes.SetEnabled(value.Enabled);
        // The seam fog does not need Farseer (owner, 2026-10-13): off, only Farseer's own tiles
        // and meshes go, and the fog carries on from the cached terrain's edge.
        if (!value.Enabled) renderer.ReleaseSilhouetteTerrainMeshes();
    }

    void WriteSilhouetteSettings(LodSilhouetteSettings value)
    {
        value = value.Normalize();
        config.HorizonSilhouettes = value.Enabled;
        config.SilhouetteReachBlocks = value.ReachBlocks;
        config.SilhouetteSkyTint = value.SkyTint;
        config.SilhouetteTintRed = value.TintRed;
        config.SilhouetteTintGreen = value.TintGreen;
        config.SilhouetteTintBlue = value.TintBlue;
        config.SilhouetteTintStrength = value.TintStrength;
        config.SilhouetteLightLevelBias = value.LightLevelBias;
        config.SilhouetteFadeBias = value.FadeBias;
        config.SilhouetteGlobeEffect = value.GlobeEffect;
        config.SilhouetteUnderlayDepthBlocks = value.UnderlayDepthBlocks;
        config.SilhouetteUnderlayWidthBlocks = value.UnderlayWidthBlocks;
        config.SilhouetteOutlineDetail = value.OutlineDetail;
        config.SilhouetteHillSoftness = value.HillSoftness;
        config.SilhouetteFog = value.FogEnabled;
        config.SilhouetteFogStrength = value.FogStrength;
        config.SilhouetteFogSeamStrength = value.FogSeamStrength;
        config.SilhouetteFogCoverage = value.FogCoverage;
        config.SilhouetteFogSpillBlocks = value.FogSpillBlocks;
        config.SilhouetteFogHeightBlocks = value.FogHeightBlocks;
        config.SilhouetteFogFloorDepthBlocks = value.FogFloorDepthBlocks;
        config.SilhouetteFogBillowSizeBlocks = value.FogBillowSizeBlocks;
        config.SilhouetteFogDriftSpeed = value.FogDriftSpeed;
        config.SilhouetteFogLayerDetail = value.FogLayerDetail;
        config.SilhouetteFogEdgeSoftness = value.FogEdgeSoftness;
        config.SilhouetteFogStripHeightBlocks = value.FogStripHeightBlocks;
        config.SilhouetteFogFadeSeconds = value.FogFadeSeconds;
    }
}
