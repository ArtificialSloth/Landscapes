using Vintagestory.API.Common;
using VintageHorizons.Silhouette;

namespace VintageHorizons;

/// <summary>
/// Horizon silhouettes (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER P4), available only where the
/// server advertises the feature. The client preference and appearance are persisted.
/// </summary>
public partial class VintageHorizonsModSystem
{
    /// <summary>Null while deferring to another LOD mod.</summary>
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
        capi.ChatCommands.Create("vhsilhouette")
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
                            "[VintageHorizons] use .vhsilhouette on, off, log or stats");
                }

                // Chat cannot be copied, so the same line always reaches the log.
                string line = "[VintageHorizons] " + DescribeSilhouettes();
                Mod.Logger.Notification("{0}", line);
                return TextCommandResult.Success(line);
            });
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
            + $"{renderer.SilhouetteUnderlayWidthBlocks:0} wide edge apron). "
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
        config.SilhouetteHillSoftness).Normalize();

    LodSilhouetteSettings CurrentSilhouetteSettings()
    {
        if (deferringTo != null || silhouettes == null) return ConfiguredSilhouetteSettings();
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
            silhouettes.HillSoftness).Normalize();
    }

    void PreviewSilhouetteSettings(LodSilhouetteSettings value)
    {
        if (deferringTo == null) ApplySilhouetteSettings(value);
    }

    void ApplySilhouetteSettings(LodSilhouetteSettings value)
    {
        value = value.Normalize();
        if (deferringTo != null || silhouettes == null) return;
        renderer.SilhouetteSkyTint = value.SkyTint;
        renderer.SilhouetteColorTint = new Vintagestory.API.MathTools.Vec4f(
            value.TintRed, value.TintGreen, value.TintBlue, value.TintStrength);
        renderer.SilhouetteLightLevelBias = value.LightLevelBias;
        renderer.SilhouetteFadeBias = value.FadeBias;
        renderer.SilhouetteGlobeEffect = value.GlobeEffect;
        renderer.SilhouetteUnderlayDepthBlocks = value.UnderlayDepthBlocks;
        renderer.SilhouetteUnderlayWidthBlocks = value.UnderlayWidthBlocks;
        renderer.SilhouetteOutlineDetail = value.OutlineDetail;
        silhouettes.SetReachBlocks(value.ReachBlocks);
        silhouettes.SetHillSoftness(value.HillSoftness);
        silhouettes.SetEnabled(value.Enabled);
        if (!value.Enabled) renderer.ReleaseSilhouetteMeshes();
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
    }
}
