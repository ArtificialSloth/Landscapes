using Vintagestory.API.Common;
using VintageHorizons.Silhouette;

namespace VintageHorizons;

/// <summary>
/// Horizon silhouettes, first look (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER P1): session-only,
/// off until `.vhsilhouette on`, and available only where the server advertises the feature,
/// which means singleplayer/LAN by default and dedicated servers whose admin opts in.
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

    void ResetSilhouettes()
    {
        silhouettes?.Reset();
        renderer?.ReleaseSilhouetteMeshes();
    }

    void RegisterSilhouetteCommand()
    {
        capi.ChatCommands.Create("vhsilhouette")
            .WithDescription("Horizon silhouettes, first look: on, off, log or stats. This session "
                + "only, and only where the server offers them.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("action"))
            .HandleWith(args =>
            {
                string action = args.Parsers[0].IsMissing
                    ? "stats"
                    : ((string)args[0]).ToLowerInvariant();
                switch (action)
                {
                    case "on":
                        silhouettes?.SetEnabled(true);
                        break;
                    case "off":
                        silhouettes?.SetEnabled(false);
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
        return $"horizon silhouettes {(s.Enabled ? "on" : "off")} (first look, this session only). "
            + $"The server {server}. {s.TileCount} tiles held of {s.OfferedTiles} offered "
            + $"({s.TilesInFlight} requested, {s.TilesRefused} refused, {s.TilesRejected} corrupt), "
            + $"{s.KnownCells:N0} cells with heights, "
            + $"{renderer.SilhouetteMeshCount} meshes ({s.PendingMeshes} waiting, {s.EmptyMeshes} "
            + $"with nothing to draw), {renderer.SilhouetteDrawsLastFrame} drawn last frame. "
            + $"Shader: {renderer.SilhouetteShaderState}.";
    }
}
