using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using VintageHorizons.Net;
using VintageHorizons.Render;

namespace VintageHorizons;

public class VintageHorizonsConfig
{
    /// <summary>
    /// Hard cap on how far cached terrain is drawn, in blocks. 0 = unlimited.
    ///
    /// A fresh install starts at 4,096 rather than unlimited. Unlimited draws every cached
    /// section a player has ever collected, so the cost of a first launch is set by how much
    /// world happens to be on disk rather than by anything the mod chose, and the mask's
    /// benefit scales with the vanilla render distance underneath it. An existing config file
    /// carries its own value - including a saved 0 - so this changes new installs only.
    /// </summary>
    public int FarViewDistanceCap = 4096;

    /// <summary>Legacy first threshold used to migrate configs without LodThresholds.</summary>
    public int DetailDistance = 512;

    /// <summary>
    /// L1-L6 transition distances. Null migrates an older config by doubling
    /// DetailDistance; saved configs always contain the explicit thresholds.
    /// </summary>
    public int[]? LodThresholds;

    /// <summary>
    /// Give each vanilla chunk its own ground instead of using one measured distance.
    /// On by default since 0.3.17. `VINTAGEHORIZONS_CHUNK_MASK` overrides this in either
    /// direction for benchmarking; see <see cref="LodTerrainRenderer.ChunkMaskEnabled"/>.
    /// </summary>
    public bool ChunkMask = true;

    /// <summary>
    /// Optional visual handoff pipeline: seam colour blending plus real block textures in
    /// packed L0 terrain. Off keeps the ownership handoff, but remeshes L0 back to its plain
    /// averaged-colour representation so the texture-table fetch disappears too.
    /// </summary>
    public bool BlendPipeline = true;

    /// <summary>
    /// The visual features that cost measurable frame time, each remembered per install.
    ///
    /// <para>
    /// These were session-only switches for their first several builds - a player who turned
    /// one off had it back on at the next launch, which is the wrong default for the people
    /// they exist for. They are the settings a machine that cannot afford them needs to be
    /// able to give up once. `.dlconfig` presents them; the `.dl*` commands still set the same
    /// values and save through the same path, so the window and the commands cannot disagree.
    /// </para>
    /// <para>
    /// Every one of them defaults ON. Turning any of them off can only remove something the
    /// renderer would otherwise draw, so a config file written by an older build - which has
    /// none of these keys - loads as "everything on" and behaves exactly as it did.
    /// </para>
    /// </summary>
    public bool SeamBand = true;
    public bool BlockTextures = true;
    public bool ClimateTint = true;

    /// <summary>
    /// The voxel size past which distant ground stops working out its own biome colour.
    /// Zero spends it at every LOD level. See <see cref="LodTerrainRenderer.ClimateFadeBlocks"/>.
    /// </summary>
    public float ClimateTintRange = LodVisualSettings.CloseClimateTintRange;

    public bool LeafSeasonColours = true;
    public bool HalfBlockTerrain = true;
    public bool WaterEffects = true;

    /// <summary>
    /// Darken ground under water by its depth, as the engine's own light does. On by default:
    /// it is a correction rather than an effect, and a config written by an older build - which
    /// has no such key - loads as on and gains it without re-exploring anything.
    /// </summary>
    public bool WaterDepthShading = true;

    /// <summary>How solid distant water may become. See LodTerrainRenderer.WaterMaxAlpha.</summary>
    public float WaterMaxAlpha = 1.3f;

    /// <summary>Blocks of water the view ray survives. See LodTerrainRenderer.WaterClarity.</summary>
    public float WaterClarity = 40f;

    /// <summary>
    /// Blocks either side of a vanilla chunk plane where water keeps one quad per block, so it
    /// can carry the game's own wave and meet its surface without an opening. See
    /// LodTerrainRenderer.WaterSeamStripBlocks.
    /// </summary>
    public int WaterSeamStripBlocks = 1;

    /// <summary>
    /// Drop water walls against any uncaptured neighbour. See
    /// LodTerrainRenderer.SuppressUnknownWaterWalls; false is the established behaviour.
    /// </summary>
    public bool SuppressUnknownWaterWalls = false;

    /// <summary>
    /// The player chose "Don't show again" on the login notice about other LOD mods. It hides
    /// only the general reminder: a listed LOD mod found switched on still brings the notice
    /// back (owner, 2026-10-10). Replaces the old step-aside switch, which an older file may
    /// still hold and the reader ignores: this mod no longer steps aside for anyone.
    /// </summary>
    public bool HideLodModNotice = false;

    /// <summary>
    /// The settings window shows its tuning dials: the Anti-Aliasing tab and Farseer's
    /// appearance and fog sliders. Off by default (owner, 2026-10-13): a player sees the
    /// distances, the feature switches and Farseer's on/off switches, and the tuned values
    /// stay in force whether or not they are shown.
    /// </summary>
    public bool ShowAdvancedSettings = false;

    /// <summary>
    /// The GPU render path switches, remembered per install.
    ///
    /// **Default ON since 0.3.103**, which is the owner's product decision after the Phase 8
    /// correctness defect was found and fixed in 0.3.101 and the cleanup landed in 0.3.102.
    /// The reasoning that kept them off - that batching suspends the delayed occlusion queries
    /// and that depth culling could not pay for itself while cached terrain could not occlude
    /// cached terrain - was answered by the same-frame near/far split and by the measured
    /// far-command suppression that followed it.
    ///
    /// Off remains reachable as an explicit failure test. It releases the arenas and leaves
    /// cached opaque terrain absent; the retired per-section renderer is never selected.
    /// </summary>
    public bool GpuArenas = true;
    public bool IndirectDraw = true;
    public bool DepthCull = true;

    /// <summary>
    /// Take the second depth picture mid-frame, between the near and far cached buckets, so
    /// cached terrain can hide cached terrain within the same frame. Default on with the rest
    /// of the path; `.dlgpu off` disables it along with everything above it.
    /// </summary>
    public bool LateDepthPicture = true;

    /// <summary>
    /// Blend distant cached terrain over several frames so it stops shimmering as the camera
    /// moves. See LodTerrainRenderer.Taa.cs. The window's "Distant Anti-Aliasing" row and .dltaa
    /// write it; on by default, and an older config file with no such key loads as on.
    /// </summary>
    public bool DistantAntiAliasing = true;

    /// <summary>
    /// Coarse terrain beyond the cached world. The client owns the switch, reach and appearance;
    /// a server may refuse to supply tiles, but cannot silently turn the saved preference off.
    /// </summary>
    public bool HorizonSilhouettes = true;
    public int SilhouetteReachBlocks = 4096;
    public float SilhouetteSkyTint = 2f;
    public float SilhouetteTintRed = 0.26f;
    public float SilhouetteTintGreen = 0.29f;
    public float SilhouetteTintBlue = 0.45f;
    public float SilhouetteTintStrength = 0.40f;
    public float SilhouetteLightLevelBias = 0.75f;
    public float SilhouetteFadeBias = 0.70f;
    public float SilhouetteGlobeEffect = 0.05f;
    public float SilhouetteUnderlayDepthBlocks =
        Silhouette.LodSilhouetteField.DefaultUnderlayDepthBlocks;
    public float SilhouetteUnderlayWidthBlocks =
        Silhouette.LodSilhouetteField.DefaultUnderlayWidthBlocks;
    public float SilhouetteOutlineDetail = Silhouette.LodSilhouetteField.DefaultOutlineDetail;
    public float SilhouetteHillSoftness = Silhouette.LodSilhouetteField.DefaultHillSoftness;

    /// <summary>
    /// The seam fog's own switch, under Farseer's. Off draws no fog pass and builds no field;
    /// an older config file with no such key loads as on.
    /// </summary>
    public bool SilhouetteFog = true;

    /// <summary>
    /// The fog model the dials below were saved for. A file without the key, or with an older
    /// model, has them reset once to the current defaults: a redesign changes what the same
    /// numbers look like, and values tuned against the old fog work against the new one.
    /// </summary>
    public int SilhouetteFogModel;
    public float SilhouetteFogStrength = LodSilhouetteFogVolume.DefaultStrength;
    public float SilhouetteFogSeamStrength = LodSilhouetteFogVolume.DefaultSeamStrength;
    public float SilhouetteFogCoverage = LodSilhouetteFogVolume.DefaultCoverage;
    public float SilhouetteFogSpillBlocks = LodSilhouetteFogVolume.DefaultSpillBlocks;
    public float SilhouetteFogHeightBlocks = LodSilhouetteFogVolume.DefaultHeightBlocks;
    public float SilhouetteFogFloorDepthBlocks = LodSilhouetteFogVolume.DefaultFloorDepthBlocks;
    public float SilhouetteFogBillowSizeBlocks = LodSilhouetteFogVolume.DefaultBillowSizeBlocks;
    public float SilhouetteFogDriftSpeed = LodSilhouetteFogVolume.DefaultDriftSpeed;
    public float SilhouetteFogEdgeSoftness = LodSilhouetteFogVolume.DefaultEdgeSoftness;
    public float SilhouetteFogLayerDetail = LodSilhouetteFogVolume.DefaultLayerDetail;

    /// <summary>Depth of the fog strip lying on the seam (0.4.125).</summary>
    public float SilhouetteFogStripHeightBlocks = LodSilhouetteFogVolume.DefaultStripHeightBlocks;

    /// <summary>Seconds fog left behind by a moved seam takes to dissolve; 0 is instant.</summary>
    public float SilhouetteFogFadeSeconds = LodSilhouetteFogVolume.DefaultFadeSeconds;

    /// <summary>
    /// Choose L1-L6 from the field of view and render resolution instead of the fixed
    /// LodThresholds above, so every level hands over at the same size on screen and follows
    /// those settings when they change. The fixed list is kept untouched for when this is off.
    /// </summary>
    public bool AutoLodThresholds = true;

    /// <summary>
    /// Continue the existing bounded cache and mesh publication work while singleplayer is
    /// paused. Rendering already continues behind that menu; this keeps its terrain source fed
    /// without unpausing simulation. Missing keys in older config files therefore load as on.
    /// </summary>
    public bool LoadWhilePaused = true;

    /// <summary>Remembered Generate-page choices; the server remains authority for limits.</summary>
    public int WorldGenRadiusChunks = 32;
    public string WorldGenShape = "circle";
    public string WorldGenCentre = "here";
    public int WorldGenCentreX;
    public int WorldGenCentreZ;

    /// <summary>The on-screen column width, in pixels, automatic thresholds hand each level over at.</summary>
    public float LodTargetPixels = LodAutoThresholds.DefaultTargetPixels;

    /// <summary>
    /// How far in distant anti-aliasing reaches: the on-screen width of one block, in pixels,
    /// where it begins. It is full strength where a block is half that wide. `.dltaa reach`
    /// writes it; larger reaches closer, but only ever over cached terrain.
    /// </summary>
    public float TaaReachPixels = LodTaaMath.DefaultReachPixels;

    /// <summary>
    /// Distant anti-aliasing's tuning dials, all set by .dltaa and explained on LodTaaMath:
    /// memory while turning and while still, how far terrain is nudged, sharpening, and the
    /// width of the safety check. `.dltaa reset` puts them back.
    /// </summary>
    public float TaaBlend = LodTaaMath.DefaultFeedback;
    public float TaaStillBlend = LodTaaMath.DefaultStillFeedback;
    public float TaaNudge = LodTaaMath.DefaultNudge;
    public float TaaSharpen = LodTaaMath.DefaultSharpen;
    public float TaaClip = LodTaaMath.DefaultClip;

    /// <summary>
    /// Start the game with .NET's all-cores (Server) garbage collector, by one property in the
    /// game's own start-up file (<see cref="GameCollectorSetting"/>). Off by default: it edits a
    /// file in the game folder, it applies only from the next start, and it uses more memory.
    /// </summary>
    public bool AllCoresGarbageCollection;
}

/// <summary>The normal 50 ms game-tick cadence, expressed over a pause-safe monotonic clock.</summary>
internal static class PausedLodLoadingPolicy
{
    internal const long IntervalMilliseconds = 50;

    internal static bool IsDue(long now, long previous) =>
        previous == long.MinValue || now < previous || now - previous >= IntervalMilliseconds;
}

/// <summary>
/// Client entry point: wires the shared <see cref="LodPipeline"/> to client events and
/// owns everything the server has no equivalent of - the renderer, tint registry, chat
/// commands and telemetry. Chunk columns arrive from `ChunkDirty` and go straight to the
/// pipeline, which does the capture, mip and persistence work.
/// See DESIGN.md at the repo root.
/// </summary>
public partial class VintageHorizonsModSystem : ModSystem
{

    ICoreClientAPI capi = null!;
    LodPipeline pipeline = null!;
    LodTerrainRenderer renderer = null!;
    LodHitchRecorder? hitches;
    VanillaHorizonEffects? horizonEffects;
    VintageHorizonsSettingsIntegration? settingsIntegration;

    /// <summary>Block -> live tint slot; shared by capture, cache loads and the renderer.</summary>
    readonly LodTintRegistry tints = new();

    /// <summary>
    /// Block -> where that block's texture lives in the game's own atlas. Built as sections
    /// resolve, read by the renderer to fill the GPU table.
    /// </summary>
    LodBlockTextureRegistry blockTextures = null!;
    long tickListenerId;
    long lastLodWorkMs = long.MinValue;

    readonly BlockPos paletteSamplePos = new(0, 0, 0);

    // Dev auto-explore (unattended stress testing): teleport along an expanding
    // spiral so fresh chunks stream through the pipeline continuously.
    bool autoExplore;
    int exploreLeg;      // spiral leg counter
    int exploreStep;     // steps taken on current leg
    int exploreDirX = 1, exploreDirZ;
    double exploreX, exploreZ;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Client;

    public override void StartPre(ICoreAPI api) => ModIdentity.TakeOverLegacyData(api, Mod.Logger);

    /// <summary>Everything the config file holds, so a partial save cannot drop a setting.</summary>
    VintageHorizonsConfig config = new();
    VintageHorizonsConfigDialog? configDialog;

    /// <summary>Other LOD mods found switched on at startup, by display name; see <see cref="OtherLodMods"/>.</summary>
    string[] otherLodMods = Array.Empty<string>();

    /// <summary>Optional server assist (DESIGN.md §10).</summary>
    LodAssistClient? assist;

    /// <summary>
    /// How far this client has caught up with a dedicated server's section cache, and which held
    /// sections that server changed since (terrain-change plan Phase B). Loaded with each world,
    /// idle in integrated play, where the sibling cache already says what changed.
    /// </summary>
    readonly LodAssistSync serverSync = new();
    System.Func<long, long, bool>? queueServerSyncFill;

    public override void StartClientSide(ICoreClientAPI api)
    {
        capi = api;
        allocationTelemetryEnabled =
            Environment.GetEnvironmentVariable("VINTAGEHORIZONS_STATS") == "1";

        bool configLoaded = false;
        try
        {
            VintageHorizonsConfig? loaded =
                capi.LoadModConfig<VintageHorizonsConfig>("dodenslandscapes.json");
            configLoaded = loaded != null;
            config = loaded ?? new VintageHorizonsConfig();
        }
        catch
        {
            config = new VintageHorizonsConfig();
        }
        ResetOutdatedFogDials(configLoaded);
        SyncGameCollector();

        // Before anything that can return early, and before the connection handshake:
        // the server learns we speak this channel at handshake time and never again. A
        // client that skips the registration makes the game log "Server sends me channel
        // name dodenslandscapes, but no client side mod registered it" on every join,
        // which reads like a broken install. The channel stays inert until Greet(), and
        // only the active path reaches that.
        assist = new LodAssistClient(capi, Mod.Logger, Mod.Info.Version);
        assist.Register();

        // Two LOD mods both extend the camera's far plane and both draw terrain out there, so
        // they draw over each other. This mod never steps aside (owner, 2026-10-10): it names
        // what it found in the log, and the login notice tells the player to keep one.
        otherLodMods = FindOtherLodMods();
        InstallDiagnostics();

        // Vanilla has no public mod-settings page registry in 1.22. The guarded adapter adds
        // one in-world header button and leaves .dlconfig as the permanent fallback if the
        // private seam ever moves.
        settingsIntegration = new VintageHorizonsSettingsIntegration(
            capi, CreateEmbeddedConfigDialog);
        if (!settingsIntegration.Install(Mod.Logger))
        {
            settingsIntegration.Dispose();
            settingsIntegration = null;
        }
        capi.Event.LevelFinalize += ShowLodModNotice;

        LodWorld.SetLevelThresholds(LodWorld.NormalizeLevelThresholds(
            config.LodThresholds, config.DetailDistance));

        blockTextures = new LodBlockTextureRegistry(ResolveBlockTexture);
        pipeline = new LodPipeline(capi, Mod.Logger, DescribePalette,
            block => (byte)TintSlotOf(block),
            block => block.EntityClass != null ? 0 : StableColorOf(block),
            // The stable colour travels with the slot: a textured quad carries the slot
            // INSTEAD of its colour, in the same field, so the table has to hand the colour
            // back wherever the texture is not shown.
            block => (ushort)blockTextures.SlotFor(
                block, block.EntityClass != null ? 0 : StableColorOf(block)),
            textureSlotsEnabled: config.BlendPipeline);
        pipeline.TrackPhaseAllocations = allocationTelemetryEnabled;

        // Repairs a cache written while a block lookup was poisoned, which saved sections
        // with no palette colour at all and drew them as black ground. Client-side only:
        // it needs the texture atlas, and a server stores 0 on purpose.
        pipeline.RepairUncoloredPalette = section =>
            LodPaletteRepair.Fill(section, AtlasColorOf) + RepairPlaceholderEntries(section);
        pipeline.RecolorForeignSection = RecolorForeignSection;
        renderer = new LodTerrainRenderer(
            capi, pipeline.World, pipeline.Worker, tints, () => pipeline.WorldEpoch,
            blockTextures)
        {
            AutoUnpause = Environment.GetEnvironmentVariable("VINTAGEHORIZONS_AUTOUNPAUSE") == "1",
            TrackPhaseAllocations = allocationTelemetryEnabled,
            FarViewDistanceCap = config.FarViewDistanceCap,
            WaterDepthShading = config.WaterDepthShading,
            WaterMaxAlpha = config.WaterMaxAlpha,
            WaterClarity = config.WaterClarity,
            WaterSeamStripBlocks = config.WaterSeamStripBlocks,
            SuppressUnknownWaterWalls = config.SuppressUnknownWaterWalls,
            TextureSlotSourceReady = pipeline.TextureSlotsPrepared,
            BlendPipelineEnabled = config.BlendPipeline,
            OpaqueBackfaceCulling =
                Environment.GetEnvironmentVariable("VINTAGEHORIZONS_BACKFACE_CULLING") != "0",
            OpaqueFrontToBack =
                Environment.GetEnvironmentVariable("VINTAGEHORIZONS_FRONT_TO_BACK") != "0",
            OcclusionCullingEnabled =
                Environment.GetEnvironmentVariable("VINTAGEHORIZONS_OCCLUSION_CULLING") != "0",
            SectionHeightCulling =
                Environment.GetEnvironmentVariable("VINTAGEHORIZONS_SECTION_HEIGHT_CULLING") != "0",
            SubtreeHeightCulling =
                Environment.GetEnvironmentVariable("VINTAGEHORIZONS_SUBTREE_HEIGHT_CULLING") != "0",
            // On unless the environment takes it away. The harness can still pin either side
            // for a controlled A/B, which is what keeps a scripted comparison honest; what
            // changed in 0.3.103 is only which way an unset variable falls.
            DepthPyramidEnabled =
                Environment.GetEnvironmentVariable("VINTAGEHORIZONS_DEPTH_PYRAMID") != "0",
        };
        renderer.ContinueLoadingWhilePaused = config.LoadWhilePaused;
        renderer.PausedLoadingPump = PumpPausedLoading;
        CreateSilhouettes();
        ApplySilhouetteSettings(ConfiguredSilhouetteSettings());

        // Install only after the competing-LOD return above. The adapter changes no
        // atmosphere, client setting, culling or streaming state: it moves only vanilla
        // terrain's visual edge fade while this renderer exists, then becomes inert and
        // removes its exact hooks on unload.
        horizonEffects = new VanillaHorizonEffects(capi);
        if (!horizonEffects.Install(Mod.Logger))
        {
            horizonEffects.Dispose();
            horizonEffects = null;
        }

        // The saved setting applies unless the environment variable has taken a side, which
        // is how the benchmark harness pins one path for a controlled comparison. The
        // renderer has already resolved the override, so only an absent variable defers here.
        if (Environment.GetEnvironmentVariable("VINTAGEHORIZONS_CHUNK_MASK") == null)
            renderer.ChunkMaskEnabled = config.ChunkMask;

        // What the player last chose to draw. Applied before the first frame, so a machine
        // that gave a feature up does not pay for it once on the way in - and no re-mesh is
        // needed here whatever it says, because nothing is resident yet.
        ApplyVisualSettings(ConfiguredVisualSettings());

        // Automatic thresholds are worked out from the screen on the first frame; until then the
        // fixed list loaded above stands, and it stays in the file either way.
        renderer.LodTargetPixels = config.LodTargetPixels;
        renderer.AutoLodThresholds = config.AutoLodThresholds;
        renderer.TaaReachPixels = config.TaaReachPixels;
        renderer.TaaFeedback = config.TaaBlend;
        renderer.TaaStillFeedback = config.TaaStillBlend;
        renderer.TaaNudge = config.TaaNudge;
        renderer.TaaSharpen = config.TaaSharpen;
        renderer.TaaClip = config.TaaClip;

        // The GPU path someone chose last session, restored in the order the pieces depend
        // on each other: arenas hold the geometry batching draws from, batching provides the
        // commands culling zeroes, and culling needs a pyramid to test against. Restoring
        // them out of order would leave a switch on with nothing under it, which is exactly
        // the state that wasted a playtest.
        //
        // An environment variable still wins, so a benchmark run pins its own path rather
        // than inheriting whatever the last session happened to leave saved.
        if (config.GpuArenas && Environment.GetEnvironmentVariable("VINTAGEHORIZONS_GPU_ARENAS") == null)
        {
            renderer.RequestGpuShadow("on");

            if (config.IndirectDraw
                && Environment.GetEnvironmentVariable("VINTAGEHORIZONS_GPU_INDIRECT") == null)
            {
                renderer.IndirectDrawEnabled = true;

                if (config.DepthCull
                    && Environment.GetEnvironmentVariable("VINTAGEHORIZONS_DEPTH_PYRAMID") == null)
                {
                    renderer.DepthPyramidEnabled = true;
                    renderer.GpuCullEnabled = true;

                    // Last, and inside the cull branch: the second same-frame picture only
                    // means anything to something that reads it.
                    if (config.LateDepthPicture) renderer.LateDepthPyramid = true;
                }
            }
        }

        capi.Event.ChunkDirty += OnChunkDirty;
        capi.Event.LevelFinalize += OnLevelFinalize;
        capi.Event.LeaveWorld += OnLeaveWorld;

        tickListenerId = capi.Event.RegisterGameTickListener(
            LodHitchActivity.Measured("client tick", OnGameTick), 50);

        // On by default while the 1% low is being chased: the owner cannot keep count of
        // hitches by eye, and a report he had to remember to start would miss the ones
        // that matter. '.dlhitch off' removes it and restores the profiler.
        hitches = new LodHitchRecorder(capi, Mod.Logger);
        capi.Event.RegisterRenderer(hitches, EnumRenderStage.Before, "dodenslandscapes-hitches");

        RegisterCommands();

        Mod.Logger.Notification("Doden's Landscapes {0} loaded (client-only)", Mod.Info.Version);
    }

    /// <summary>
    /// A dev hook for the matrix tier: send one chat command shortly after the world is
    /// up. The harness cannot reach a detached server's console, and a second entry point
    /// into a command would test something other than what players type. The delay lets
    /// the handshake and the privilege grant settle first.
    /// </summary>
    void RegisterAutoCommand()
    {
        if (Environment.GetEnvironmentVariable("VINTAGEHORIZONS_AUTOCMD") is not { Length: > 0 } autoCmd)
        {
            return;
        }

        capi.Event.RegisterCallback(_ =>
        {
            Mod.Logger.Notification("Auto-command: {0}", autoCmd);

            // SendChatMessage sends to the SERVER, so it runs '/' commands and silently
            // does nothing with a client-side '.' one: the message goes out, no handler
            // on either side claims it, and the log looks like a success. Client commands
            // have to be dispatched locally.
            if (autoCmd.StartsWith('.'))
            {
                capi.ChatCommands.ExecuteUnparsed(autoCmd, new TextCommandCallingArgs
                {
                    Caller = new Caller
                    {
                        Player = capi.World.Player,
                        Pos = capi.World.Player.Entity.Pos.XYZ,
                        FromChatGroupId = GlobalConstants.GeneralChatGroup,

                        // The game fills these in when a person types the command; a
                        // caller built here starts with none and is refused by its own
                        // privilege test. Wildcard, because this hook exists only to
                        // reproduce what a player does, and a test that cannot reach the
                        // command tests nothing.
                        CallerPrivileges = new[] { "*" },
                    },
                }, result => Mod.Logger.Notification("Auto-command result: {0}", result.StatusMessage));
                return;
            }

            capi.SendChatMessage(autoCmd);
        }, 15000);
    }

    /// <summary>
    /// The listed LOD mods that are switched on, by display name, logged either way: a bug
    /// report about a flickering horizon needs the log to name the other mod.
    /// </summary>
    string[] FindOtherLodMods()
    {
        (string[] drawing, string[] switchedOff) =
            OtherLodMods.Inspect(capi.ModLoader.IsModEnabled, ReadOtherModSwitch);

        if (switchedOff.Length > 0)
        {
            Mod.Logger.Notification("Installed but switched off in its own settings: {0}",
                string.Join(", ", switchedOff.Select(OtherLodModName)));
        }

        string[] names = drawing.Select(OtherLodModName).ToArray();
        if (names.Length > 0)
        {
            Mod.Logger.Warning("[Landscapes] {0}", LodModNotice.ConflictText(names));
        }
        return names;
    }

    /// <summary>The name the player sees in the Mod Manager, or the mod id if the game has none.</summary>
    string OtherLodModName(string modId) => capi.ModLoader.GetMod(modId)?.Info?.Name is { Length: > 0 } name
        ? name
        : modId;

    /// <summary>
    /// Once the world is up: the notice about other LOD mods. A listed one switched on always
    /// shows it; otherwise it shows until the player chooses "Don't show again".
    /// </summary>
    void ShowLodModNotice()
    {
        if (otherLodMods.Length == 0 && config.HideLodModNotice) return;
        var notice = new LodModNotice(capi, otherLodMods, otherLodMods.Length == 0, () =>
        {
            config.HideLodModNotice = true;
            SaveConfig();
            Mod.Logger.Notification("The login notice about other LOD mods will not show again "
                + "unless one of them is switched on.");
        },
            // After the notice has finished with the click that asked for it.
            () => capi.Event.EnqueueMainThreadTask(() =>
            {
                if (!OpenStandaloneConfigDialog())
                    capi.ShowChatMessage("[Landscapes] The settings window could not open; use .dlconfig.");
            }, "dodenslandscapes-notice-settings"));
        if (!notice.TryOpen())
        {
            Mod.Logger.Warning("The login notice about other LOD mods could not open.");
        }
    }

    /// <summary>Reads another mod's own switch. Null means "could not tell", which counts as on.</summary>
    bool? ReadOtherModSwitch(string file)
    {
        try
        {
            return capi.LoadModConfig<OtherLodModSwitch>(file)?.Enabled;
        }
        catch (Exception e)
        {
            // Another mod's config file is not ours to repair, and guessing "off" would
            // put us on the same ground as a mod that is still drawing.
            Mod.Logger.Warning("Could not read '{0}' to tell whether that mod is switched on: {1}",
                file, e.Message);
            return null;
        }
    }

    void OnChunkDirty(Vec3i chunkCoord, IWorldChunk chunk, EnumChunkDirtyReason reason)
    {
        pipeline.QueueColumn(chunkCoord.X, chunkCoord.Z);
    }

    void OnGameTick(float dt)
    {
        lastLodWorkMs = Environment.TickCount64;
        PumpLodWork(dt);
    }

    /// <summary>
    /// Render stages still run behind the pause menu, while ordinary game ticks do not. Drain
    /// exactly the same bounded owner-thread work at its usual cadence; no simulation tick is
    /// synthesized and no second thread is introduced.
    /// </summary>
    void PumpPausedLoading()
    {
        long now = Environment.TickCount64;
        if (!PausedLodLoadingPolicy.IsDue(now, lastLodWorkMs)) return;
        lastLodWorkMs = now;
        PumpLodWork(PausedLodLoadingPolicy.IntervalMilliseconds / 1000f);
    }

    void PumpLodWork(float dt)
    {
        if (!pipeline.Active) return;

        LodPhaseStart tickStart = LodPhaseCost.Start(allocationTelemetryEnabled);
        ReportFillIn();

        LodPhaseStart phaseStart = LodPhaseCost.Start(allocationTelemetryEnabled);
        PumpServerAssist();
        assistTickCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(allocationTelemetryEnabled);
        PumpLocalOffers();
        LogFillProgress();
        localOfferTickCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(allocationTelemetryEnabled);
        pipeline.Tick();
        renderer.TextureSlotSourceReady = pipeline.TextureSlotsPrepared;
        pipelineTickCost.Add(phaseStart);

        phaseStart = LodPhaseCost.Start(allocationTelemetryEnabled);
        var pos = capi.World.Player.Entity.Pos;
        if (pipeline.MaybeEvictAround(pos.X, pos.Z))
        {
            LodWorld world = pipeline.World;
            Mod.Logger.Notification("Evict sweep at {0},{1}: checked {2}, pinned {3}, cold {4}, total evicted {5}",
                (int)pos.X, (int)pos.Z, world.LastSweepChecked, world.LastSweepPinned,
                world.LastSweepCold, world.EvictedSectionsTotal);
        }
        residentEvictTickCost.Add(phaseStart);
        totalTickCost.Add(tickStart);
    }

    /// <summary>
    /// Adopt whatever the server sent, then ask for what the render path now wants.
    /// Both on the game tick, because both mutate the LodWorld.
    /// </summary>
    void PumpServerAssist()
    {
        // Structural decode finishes in pipeline.Tick. Release the network transport's
        // retained in-flight slot only after that owning-thread publication decision.
        while (pipeline.TryTakeForeignInstallCompletion(out LodForeignInstallCompletion done))
        {
            if (done.Source == LodForeignSource.ServerAssist)
                assist?.CompleteInstall(done.Key, done.Installed, done.Retryable);
            else if (done.Source == LodForeignSource.LocalOffer && done.Installed)
            {
                localOfferSectionsInstalled++;
                PublishTestLocalOfferInstall(done.Key);
            }
        }

        // Generate-page status is a separate advertised capability: it remains usable when
        // terrain serving is disabled, so publish/poll it before the terrain-assist gate.
        assist?.PumpWorldGen();
        // A separately advertised feature too: silhouettes need no terrain serving.
        PumpSilhouettes();
        if (assist == null || !assist.Available) return;

        // Set on the network thread before any span it precedes on the channel is queued.
        serverSync.OnWelcome(assist.ServerCacheGeneration);

        int before = pipeline.RemoteOnly.Count;
        assist.PumpAsync(
            // The version travels with the blob into the decoder: a server-sync merge settles
            // what was owed only if the copy it merged is at least that new.
            (key, blob, version) => pipeline.QueueForeignBlob(
                key, blob, LodForeignSource.ServerAssist, version) == LodForeignQueueOutcome.Queued,
            // Manifest keys become quadtree-visible here rather than in the packet
            // handler: HasDataSet belongs to this thread. Pump publishes each chunk's
            // newly accepted keys once, so the complete retained manifest is never
            // re-enumerated on ordinary ticks.
            offered => pipeline.AddRemoteKeys(offered),
            // Every failed transfer ends in one explicit state. "Not written yet"
            // releases the attempt and restores wanted; refusal, parse failure and a
            // local-win race remove the obsolete remote route.
            (key, retryable) =>
            {
                if (retryable) pipeline.MarkRemoteRetryable(key);
                else pipeline.MarkRemoteUnavailable(key);
            },
            refreshed =>
            {
                // In integrated play the sibling database is the freshness authority: its
                // rowids say exactly which rows changed, and a merge it drives is recorded
                // so a restart never repeats it. Taking the same unversioned notification
                // from both sources would merge one row twice. Dedicated multiplayer has no
                // sibling reader and continues to use the network manifest.
                if (localOffers == null) pipeline.AddRemoteUpdates(refreshed);
            },
            ApplyServerSyncSpan);

        PumpServerSync();

        // Nearest first. The render path asks for exactly the sections it wants, but far
        // more than the in-flight cap allows at once, and an unordered set hands the
        // network whatever hashes first - so distant terrain arrived while ground in front
        // of the player stayed at its coarse parent, which is what the no-holes rule draws
        // until all four children land. Sorting here rather than in the pipeline keeps the
        // pipeline free of any notion of where the viewer is.
        long[] wanted = pipeline.RemoteWanted();

        // Merges into held sections come from the sibling cache when there is one. Over the
        // network they are the dedicated server's refreshed level-0 rows.
        if (localOffers == null) wanted = [.. wanted, .. pipeline.FillWanted()];
        if (wanted.Length > 1)
        {
            var at = capi.World.Player.Entity.Pos;
            double px = at.X, pz = at.Z;
            Array.Sort(wanted, (a, b) =>
                LodWorld.NearestDistanceSqTo(a, px, pz).CompareTo(LodWorld.NearestDistanceSqTo(b, px, pz)));
        }
        pipeline.MarkRemoteRequested(assist.Request(wanted));

        if (pipeline.RemoteOnly.Count != before && !loggedRemoteKeys)
        {
            loggedRemoteKeys = true;
            Mod.Logger.Notification(
                "Server assist: {0} sections offered that this client never captured. "
                + "The mod fetches them as the view needs them.", pipeline.RemoteOnly.Count);
        }
    }

    bool loggedRemoteKeys;

    /// <summary>
    /// One chunk of a versioned span from a server that syncs by version. Held sections become
    /// owed and are fetched by <see cref="PumpServerSync"/>, nearest first; the rest are offered
    /// as any refresh offers them.
    /// </summary>
    void ApplyServerSyncSpan(LodAssistSpanChunk span)
    {
        // Integrated play: the sibling cache is the freshness authority (see the refresh
        // callback above). It can appear after the greeting, and from then on it wins.
        if (localOffers != null) return;
        if (!serverSync.Active)
        {
            // Only a server that saw us ask for versions sends a span; keep its keys all the same.
            pipeline.AddRemoteUpdates(span.Keys);
            return;
        }

        List<long>? offered = null;
        foreach (long key in span.Keys)
        {
            if (!pipeline.HoldsSection(key)) (offered ??= new List<long>()).Add(key);
        }
        if (offered != null) pipeline.AddRemoteKeys(offered);

        if (serverSync.OnSpan(span.From, span.To, span.Keys, span.Versions, span.Last,
            pipeline.HoldsSection) is not { } done) return;

        if (!done.Recorded)
        {
            Mod.Logger.Warning("Server sync: an announcement from the server did not follow on from "
                + "the one before it (versions {0}..{1}), so this session's progress is not recorded. "
                + "Its changes are still fetched; the next join re-checks from version {2}.",
                done.From, done.To, serverSync.RecordedVersion);
        }
        if (!done.Joined)
        {
            Mod.Logger.Debug("Server sync: {0} of {1} changed sections held here, versions {2}..{3}",
                done.Owed, done.Announced, done.From, done.To);
            return;
        }

        // The full key manifest precedes the join span on the channel, so it has been applied.
        int gone = serverSync.ForgetUnlisted(assist!.ServerListed);
        if (done.FromZero)
        {
            Mod.Logger.Notification(
                "Server sync: no usable record of this server's LOD cache (a first join since section "
                + "versions, or the server's cache was rebuilt). Re-checking all {0} held sections of the "
                + "{1} the server listed, nearest first, in the background; progress survives a quit.",
                done.Owed, done.Announced);
        }
        else if (serverSync.OwedCount == 0)
        {
            Mod.Logger.Notification("Server sync: already up to date with the server's LOD cache (version {0}).",
                done.To);
        }
        else
        {
            Mod.Logger.Notification(
                "Server sync: {0} held sections changed on the server since this client last caught up "
                + "(versions {1}..{2}); {3} owed in all, fetched nearest first in the background.",
                done.Owed, done.From, done.To, serverSync.OwedCount);
        }
        if (gone > 0)
        {
            Mod.Logger.Notification("Server sync: {0} owed sections are no longer on the server and were dropped.",
                gone);
        }
    }

    /// <summary>
    /// Settle what finished, hand the nearest owed sections to the pipeline, and pass record
    /// changes to the storage worker. Game tick; the tracker paces itself.
    /// </summary>
    void PumpServerSync()
    {
        long now = Environment.TickCount64;
        if (pipeline.TakeNetworkRefreshOutcomes() is { } outcomes)
        {
            foreach (LodNetworkRefreshOutcome outcome in outcomes)
            {
                switch (outcome.End)
                {
                    case LodNetworkRefreshEnd.Durable: serverSync.Absorbed(outcome.Key, outcome.Version); break;
                    case LodNetworkRefreshEnd.Merged: serverSync.Merged(outcome.Key, outcome.Version); break;
                    case LodNetworkRefreshEnd.Failed: serverSync.Failed(outcome.Key, now); break;
                    case LodNetworkRefreshEnd.NotHeld: serverSync.NotHeld(outcome.Key); break;
                }
            }
        }

        if (capi.World.Player?.Entity?.Pos is { } at && assist != null)
        {
            queueServerSyncFill ??= (key, version) =>
            {
                // A refusal earlier this session belonged to an older copy.
                assist?.ReviveForRefresh(key);
                return pipeline.QueueNetworkRefresh(key, version);
            };
            serverSync.Pump(now, at.X, at.Z, assist.ServerSettings?.ServeRadiusBlocks ?? 0,
                queueServerSyncFill);
        }

        if (serverSync.TakeWrites() is { } writes) pipeline.StorageThread?.SaveAssistSync(writes);
        if (serverSync.TakeCaughtUp())
        {
            Mod.Logger.Notification("Server sync: caught up with the server's LOD cache (version {0}); "
                + "{1} changed sections merged this session.", serverSync.RecordedVersion,
                serverSync.SettledThisSession);
        }
    }

    /// <summary>
    /// The server side's cache for this same singleplayer world, when it has swept one.
    /// Null on a dedicated server (the network assist covers that) and on any world that
    /// has never swept.
    /// </summary>
    LodLocalOfferSource? localOffers;
    bool loggedLocalOffers;
    int localOfferProbeTicks;
    int localOfferKeysDiscovered;
    int localOfferRetryableMisses;
    int localOfferSectionsAccepted;
    int localOfferSectionsInstalled;
    readonly string? testLocalOfferMissMarker =
        Environment.GetEnvironmentVariable("VINTAGEHORIZONS_TEST_LOCAL_OFFER_MISS_MARKER");
    readonly string? testLocalOfferInstallMarker =
        Environment.GetEnvironmentVariable("VINTAGEHORIZONS_TEST_LOCAL_OFFER_INSTALL_MARKER");

    /// <summary>
    /// About 5s at the 50ms tick. The retry usually answers with one failed File.Exists,
    /// so it stays cheap; anything shorter is pointless for a cache that grows for
    /// minutes once it appears.
    /// </summary>
    const int LocalOfferProbeIntervalTicks = 100;

    /// <summary>
    /// Adopt sections the server side swept out of the savegame.
    ///
    /// Identical in shape to PumpServerAssist and deliberately so - same remote-key
    /// bookkeeping, same recolour on install, same nearest-first ordering. Only the
    /// transport differs. Reads have a small in-flight cap and publication has its own
    /// budget so a sweep of ten thousand sections cannot turn into either unbounded RAM
    /// or a main-thread installation burst.
    /// </summary>
    void PumpLocalOffers()
    {
        if (localOffers == null)
        {
            // A null here means "not yet", not "never": the server side can open its
            // cache long after this client finalized - /dlgen creates one on demand,
            // and a sweep's file can appear after a slow start. Opening once at
            // LevelFinalize and never looking again left both invisible for the whole
            // session.
            if (++localOfferProbeTicks < LocalOfferProbeIntervalTicks) return;
            localOfferProbeTicks = 0;
            if (pipeline.DbPath is not string dbPath) return;
            localOffers = LodLocalOfferSource.TryOpen(dbPath, Mod.Logger);
            if (localOffers == null) return;
        }

        // A dedicated read-only connection scans the sibling cache on its worker and
        // publishes new or rewritten rows at every level, in bounded immutable batches;
        // this thread applies at most one batch per tick to the live LodWorld. A key the
        // client does not hold is offered; one it already holds is merged into.
        if (localOffers.TryTakeChangedRows(out LodSiblingRow[] rows))
        {
            LodSiblingDelta delta = pipeline.AddSiblingRows(rows);
            localOfferRowsScanned += rows.Length;
            localOfferKeysDiscovered += delta.Offered;
            localOfferFillsQueued += delta.FillsQueued;
            if (!loggedLocalOffers)
            {
                loggedLocalOffers = true;
                // Sweeps and /dlgen both fill the sibling cache; this line covers either.
                Mod.Logger.Notification(
                    "Server-side cache offers sections locally. The mod adopts the ones it "
                    + "lacks as the view needs them, and fills in the ones it already holds.");
            }
        }

        var installBudget = new LodDrainBudget();
        int completed = 0;
        while (completed < LocalOffersPerTick
            && localOffers.TryPeekBlobResult(out LodLocalOfferSource.BlobResult waiting)
            && installBudget.TryStart(waiting.Blob?.LongLength ?? 0))
        {
            // Keep sibling-cache work within its own bounded decoder allowance. Server
            // assist has a separate reservation, so local adoption cannot crowd it out.
            if (waiting.Blob is { Length: > 0 }
                && !pipeline.CanQueueForeignBlob(LodForeignSource.LocalOffer)) break;
            if (!localOffers.TryTakeBlobResult(out LodLocalOfferSource.BlobResult result)) break;
            completed++;

            // A miss is ordinary while the sweep is still running: the key was listed but
            // its row is not written yet. MarkRemoteUnavailable is permanent, so it must
            // not be used for "not yet". It also must not enter the taken batch: no source
            // accepted responsibility, so forgetting it here would strand the key in
            // LodWorld.LoadsInFlight and make a later row permanently invisible.
            if (result.Blob == null || result.Blob.Length == 0)
            {
                localOfferRetryableMisses++;
                pipeline.CompleteLocalOffer(result.Key, LodLocalOfferOutcome.RetryableMiss);
                continue;
            }

            LodForeignQueueOutcome queued = pipeline.QueueForeignBlob(
                result.Key, result.Blob, LodForeignSource.LocalOffer, result.Rowid);
            if (queued == LodForeignQueueOutcome.Queued)
            {
                localOfferSectionsAccepted++;
                pipeline.MarkLocalOfferAccepted(result.Key);
            }
            else if (queued == LodForeignQueueOutcome.Unavailable)
            {
                // Local data already won before submission, or persistence is gone.
                pipeline.CompleteLocalOffer(result.Key, LodLocalOfferOutcome.Unavailable);
            }
            else break;
        }

        localOfferItemsProcessed += installBudget.Items;
        localOfferBytesProcessed += installBudget.Bytes;

        // Held sections waiting for a merge are read through the same reader and budget, in
        // the same nearest-first order, as the sections the view is missing outright.
        long[] wanted = [.. pipeline.RemoteWanted(), .. pipeline.FillWanted()];
        if (wanted.Length == 0) return;

        if (wanted.Length > 1)
        {
            var at = capi.World.Player.Entity.Pos;
            double px = at.X, pz = at.Z;
            Array.Sort(wanted, (a, b) =>
                LodWorld.NearestDistanceSqTo(a, px, pz).CompareTo(LodWorld.NearestDistanceSqTo(b, px, pz)));
        }

        int requests = 0;
        for (int i = 0; i < wanted.Length && requests < LocalOffersPerTick
            && localOffers.CanRequestBlob; i++)
            if (localOffers.RequestBlob(wanted[i])) requests++;
    }

    void PublishTestLocalOfferInstall(long key)
    {
        if (string.IsNullOrEmpty(testLocalOfferMissMarker)
            || string.IsNullOrEmpty(testLocalOfferInstallMarker)
            || !File.Exists(testLocalOfferMissMarker)) return;

        string described = LodLocalOfferSource.DescribeKey(key);
        if (File.ReadAllText(testLocalOfferMissMarker).Trim() == described
            && !File.Exists(testLocalOfferInstallMarker))
        {
            File.WriteAllText(testLocalOfferInstallMarker, described);
        }
    }

    /// <summary>
    /// Compressed sibling-cache blobs transferred from the I/O reader to the decoder per
    /// tick. Both SQLite and structural parsing run on workers; owning-thread publication
    /// has its own elapsed-time and decoded-byte budget in LodPipeline.
    /// </summary>
    const int LocalOffersPerTick = 4;
    int localOfferItemsProcessed;
    long localOfferBytesProcessed;
    long localOfferRowsScanned;
    long localOfferFillsQueued;

    /// <summary>
    /// While held sections wait to be merged, or merges are landing, a progress line reaches
    /// the client log once a minute - the cadence the server half uses for its cache work.
    /// Game chat cannot be copied and the stats report fires once (G72).
    /// </summary>
    const long FillProgressLogIntervalMs = 60_000;
    long nextFillProgressLogMs;
    long fillMergesAtLastLog;

    void LogFillProgress()
    {
        long now = Environment.TickCount64;
        bool active = pipeline.PendingFills > 0 || pipeline.ForeignMerges != fillMergesAtLastLog;
        if (!active)
        {
            // A later burst gets a full minute before its first line.
            nextFillProgressLogMs = now + FillProgressLogIntervalMs;
            return;
        }
        if (now < nextFillProgressLogMs) return;

        Mod.Logger.Notification("Distant sections held but incomplete: {0}; {1}; server sync: {2}",
            pipeline.DescribeFills(), DescribeSiblingScanner(), serverSync.Describe());
        nextFillProgressLogMs = now + FillProgressLogIntervalMs;
        fillMergesAtLastLog = pipeline.ForeignMerges;
    }

    string DescribeSiblingScanner() => localOffers == null
        ? "no server-side cache beside this one"
        : $"server-cache scanner: {localOffers.FullScans} full and {localOffers.IncrementalScans} "
          + $"incremental scans, {localOffers.RowsPublished} rows published, busy "
          + $"{localOffers.ScanBusyShare * 100:0.000}% of one thread (an upper bound on its CPU)";

    /// <summary>
    /// Fill in palette colours for a section captured by a server, which had no texture
    /// atlas and stored 0 for every one of them (DESIGN.md §10.4). Block ids are already
    /// resolved from codes by the deserializer, so this only needs the atlas.
    /// </summary>
    void RecolorForeignSection(LodSection section)
    {
        for (int i = 0; i < section.Palette.Count; i++)
        {
            LodPaletteEntry entry = section.Palette[i];

            // A code the block registry could not answer for. It still carries the flags
            // the capturing side worked out, so it is still drawn as terrain - and a
            // server stores 0 for every colour, so leaving this one alone drew it at
            // exactly RGB 0,0,0. Black ground, correctly shaped, with nothing anywhere
            // saying why. The shader cannot save it either: shade bottoms out at 0.55 and
            // daylight is clamped to 0.02, but zero times anything is still zero.
            //
            // Neutral grey instead, and counted. Wrong-but-plausible stone beats a hole
            // in the world, and the count is what turns the next report into a log line.
            if (entry.BlockId <= 0)
            {
                entry.Color = LodPaletteRepair.UnknownBlockColor;
                section.Palette[i] = entry;
                uncoloredForeignEntries++;
                continue;
            }

            // The same colour a locally captured section would have stored, so foreign
            // and local ground meet without a seam. A chiselled block has no colour here
            // at all - its materials live in a block entity this path has no position to
            // read, the section having come from a server or a peek - so it takes the
            // same neutral grey an unidentifiable block gets.
            entry.Color = StableColorOf(capi.World.Blocks[entry.BlockId]);
            section.Palette[i] = entry;
        }
    }

    /// <summary>
    /// The client half of palette registration: the untinted colour a block is drawn as
    /// at distance, plus which live tint applies. Stored untinted on purpose, so the
    /// shader can follow the calendar instead of freezing the season it was captured in.
    /// A server has no atlas and cannot answer this at all (DESIGN.md §10.4).
    ///
    /// The exact block position is used ONLY for blocks whose colour genuinely depends on
    /// it, which are the ones carrying a block entity: a chiselled block averages the
    /// materials in its entity there, the same way the world map colours it, and a
    /// synthetic chunk-centre position made that lookup miss. Every other block takes the
    /// section-independent colour instead - see StableColorOf for why that matters.
    /// </summary>
    (int Color, byte TintSlot) DescribePalette(int blockId, int blockX, int blockY, int blockZ)
    {
        Block block = capi.World.Blocks[blockId];

        int color;
        if (block.EntityClass != null)
        {
            paletteSamplePos.Set(blockX, blockY, blockZ);
            int sampled = block.GetColorWithoutTint(capi, paletteSamplePos);

            // The last resort is what the entity answered, when it answered with a real colour.
            // A sampled placeholder means the entity was not there to ask: the game draws an
            // entity block from what the entity holds, never as the missing-texture image, so
            // that last resort is neutral grey rather than near-white. A chiselled block
            // rarely gets here now - capture records it as its most visible material
            // (LodChiselStandIn) - but clutter and bookshelves still do.
            color = RepairPlaceholder(block, sampled,
                fallback: IsPlaceholderRgb(sampled) ? LodPaletteRepair.UnknownBlockColor : sampled);
        }
        else
        {
            color = StableColorOf(block);
        }

        return (color, (byte)TintSlotOf(block));
    }

    /// <summary>
    /// One colour per block, identical in every section that ever contains that block.
    ///
    /// This has to be computed rather than simply asked for, because
    /// <c>Block.GetColorWithoutTint</c> is not a function of the block. Grass-covered
    /// ground answers it with `BlockTextureAtlas.GetRandomColor`, which returns one of
    /// thirty pixels sampled out of the grass texture at random, and a palette entry is
    /// registered once per section. So one 64-block section drew its whole surface with
    /// one random pixel and the section beside it drew its whole surface with another:
    /// measured at 38 different stored colours for `soil-low-normal` alone across 1,041
    /// cached sections, scattered with no relation to terrain, climate or height. On the
    /// ground that reads as flat green and flat brown tiles meeting at a hard edge, which
    /// is what it looked like.
    ///
    /// Averaging many draws collapses that to the texture's own mean, which is the colour
    /// the block should read as from far away, and caching it by block id makes every
    /// section agree by construction - no blending across section edges required, because
    /// there is no longer a step to blend. A block that answers deterministically averages
    /// to exactly what it already returned, so nothing else changes.
    ///
    /// The probe position is the sky above the world origin, never the block's own
    /// position, for the same reason: the base implementation hands the question to
    /// whatever DECOR sits on the up face, so sampling real ground would let one snowy or
    /// mossy sample decide the colour of that block everywhere. Nothing sits in the sky,
    /// and blocks that need their real position never reach this path.
    /// </summary>
    int StableColorOf(Block block)
    {
        block = AppearanceOf(block);
        if (stableColorByBlockId.TryGetValue(block.BlockId, out int cached)) return cached;

        // Grass-covered ground is composited, not painted, and the engine's own answer for
        // it is both incomplete and byte-swapped. Build it from the atlas instead.
        if (TryTopSoilColor(block, out int composite, out _))
        {
            stableColorByBlockId[block.BlockId] = composite;
            return composite;
        }

        colorProbePos.Set(0, capi.World.BlockAccessor.MapSizeY - 1, 0);
        int color = block.GetColorWithoutTint(capi, colorProbePos);

        // A block with no colour texture answers -1 (white) every time; averaging that
        // is meaningless and the placeholder repair below is the real answer for it.
        if (color >= 0)
        {
            long r = 0, g = 0, b = 0;
            for (int i = 0; i < StableColorSamples; i++)
            {
                int sample = block.GetColorWithoutTint(capi, colorProbePos);
                r += sample & 0xFF;
                g += (sample >> 8) & 0xFF;
                b += (sample >> 16) & 0xFF;
            }
            color = unchecked((int)0xFF000000)
                | (int)(b / StableColorSamples) << 16
                | (int)(g / StableColorSamples) << 8
                | (int)(r / StableColorSamples);
        }

        // Read the texture instead of sampling it, wherever the block's own colour texture
        // stood. The engine's answer above is `BlockTextureAtlas.GetRandomColor`, which
        // returns one of THIRTY pixels the atlas kept, and those thirty are themselves a
        // handful out of a thousand: averaging draws converges on the thirty, not on the
        // texture. Measured on granite gravel it settles at 143,133,105 where the file's
        // true average is 131,123,109 - about 8% high in red and green and 4% low in blue,
        // which reads as a warm, bright cast on distant stone next to the game's own.
        //
        // The exact average was already being computed for this same texture. It went to
        // the pattern normaliser and never to the colour, which is also why taking it here
        // is the safer of the two: the shader draws `colour * (texel / mean)`, so a colour
        // and a mean derived by different methods drift apart, and now they cannot. An
        // unreadable texture falls back to the atlas estimate for both, so the pair stays
        // consistent either way.
        //
        // Only where the placeholder test below does NOT fire. That test compares the
        // sampled value against the atlas's own average of unknown.png, so replacing the
        // value it inspects would change what counts as a missing texture.
        // The texture has to be one this can name a file for. Without one, MeanOf answers
        // with the atlas's four-pixel estimate, and four pixels is a worse average than the
        // thirty already sampled above - so that case keeps what it had.
        CompositeTexture? colorTexture = TextureFor(block, block.TextureSubIdForBlockColor);
        if (colorTexture != null && !IsPlaceholderColor(block, color))
        {
            TextureMean exact = MeanOf(colorTexture, block.TextureSubIdForBlockColor);
            if (exact.Coverage > 0f) color = PackMean(exact);
        }

        // With no real texture anywhere, a plain block whose own answer is the missing-texture
        // image is one the game itself draws as that box - typically from a mod that has since
        // been removed - so it keeps that look and distant matches near. Anything else keeps
        // the neutral grey it always had: a block with an entity is drawn from what the entity
        // holds, so its placeholder only means this path cannot see it, and a block with no
        // colour texture at all answers -1, which is no colour.
        color = RepairPlaceholder(block, color,
            fallback: block.EntityClass == null && IsPlaceholderRgb(color)
                ? color
                : LodPaletteRepair.UnknownBlockColor);
        stableColorByBlockId[block.BlockId] = color;
        return color;
    }

    /// <summary>
    /// Thirty is how many random pixels the atlas actually holds per texture, so this
    /// samples each of them about twice. Paid once per block id per session.
    ///
    /// This no longer decides the colour - the texture's own average does - but it still
    /// decides whether the block HAS a colour, which is what the placeholder test reads.
    /// A single draw would not do: the placeholder is a checkerboard, so one random pixel
    /// of it is never its average.
    /// </summary>
    const int StableColorSamples = 64;

    /// <summary>Where cached water stops and the game's own takes over, in words.</summary>
    static string DescribeWater(LodTerrainRenderer renderer)
    {
        string near = renderer.WaterNearChunks <= 0
            ? "cached water is drawn right up to the near handoff"
            : $"cached water is hidden within {renderer.WaterNearChunks} chunk(s) of yours "
                + $"({renderer.WaterNearChunks * 32} blocks, on chunk edges)";
        return $"{near} and stops at the game's own chunk edges at every distance, "
            + "the same rule distant ground follows | "
            + $"opacity {(renderer.WaterFresnel ? "the game's own, by view angle" : "flat")} "
            + $"| texture {renderer.WaterSurfaceTextureStatus} "
            + $"| glint {renderer.WaterGlint:0.00} | foam {renderer.WaterFoam:0.00}"
            + $" | ground murk {(renderer.WaterGroundMurk ? "on" : "off")}"
            + $" | cached surface {(renderer.WaterSurfaceVisible ? "on" : "off")}"
            + $" | depth coverage {(renderer.WaterDepthCoverage ? "on" : "off")}"
            + $" ({renderer.WaterDepthDraws} water sections)";
    }

    /// <summary>How many detail levels draw half-block terrain, in words.</summary>
    static string Describe(int halfBlockLevels) => halfBlockLevels switch
    {
        0 => "off",
        1 => "near only (level 0)",
        >= 8 => "every level",
        _ => $"the nearest {halfBlockLevels} levels",
    };

    static string DescribeSlabs(LodTerrainRenderer renderer) =>
        renderer.HalfBlockLevels == LodTerrainRenderer.NearHalfBlockLevels
            && renderer.SurfaceHalfBlocksOnly
            ? $"surface terrain throughout level 0 (to the {LodWorld.ThresholdForLevel(1):0}-block "
                + "L1 transition; caves stay full-height)"
            : Describe(renderer.HalfBlockLevels);

    readonly Dictionary<int, int> stableColorByBlockId = new();

    /// <summary>Sky above the world origin: guaranteed to carry no decor. See StableColorOf.</summary>
    readonly BlockPos colorProbePos = new(0, 0, 0);

    /// <summary>Tint slot for a block, with the untinted share its colour carries.</summary>
    int TintSlotOf(Block block)
    {
        block = AppearanceOf(block);
        return tints.SlotFor(block, TryTopSoilColor(block, out _, out LodUntintedShare share)
            ? share
            : LodUntintedShare.None);
    }

    readonly Dictionary<int, Block> appearanceByBlockId = new();

    /// <summary>
    /// The block whose colour, tint and texture this one is drawn with at distance - itself,
    /// except for branchy leaves, which are drawn as the plain leaves of the same tree.
    ///
    /// Branchy leaves name no colour map on the block: vanilla tints them per element inside
    /// their shape, which a cube cannot follow, and their textures are grey until tinted. So
    /// at distance they drew as dark grey-brown cubes scattered through every redwood and
    /// pine crown, where vanilla shows ordinary green foliage (owner screenshots, 2026-10-13).
    /// The owner's call was to draw them as normal leaves. Both blocks share their variant
    /// names, so the twin is the same code with "leaves" in place of the branchy part; a
    /// block without one - a mod's tree, say - keeps its own look.
    /// </summary>
    Block AppearanceOf(Block block)
    {
        if (appearanceByBlockId.TryGetValue(block.BlockId, out Block? known)) return known;

        Block appearance = block;
        string? twin = LodBlockPolicy.PlainLeavesCodeFor(block.Code?.Path);
        if (twin != null)
        {
            Block? plain = capi.World.GetBlock(new AssetLocation(block.Code!.Domain, twin));
            if (plain != null && plain.BlockId > 0) appearance = plain;
        }
        appearanceByBlockId[block.BlockId] = appearance;
        return appearance;
    }

    /// <summary>
    /// Reproduce vanilla's top-soil compositing for a block that has one, from the atlas.
    ///
    /// `chunktopsoil.fsh` draws these as
    /// <c>brownSoil * (1 - grass.a) + grass * grass.a</c>, where the grass overlay is the
    /// block's `specialSecondTexture` and only the overlay is colour-mapped. Two things
    /// follow, and the mod had both of them wrong.
    ///
    /// The bare dirt showing through is a THIRD of the face at full coverage and more at
    /// the sparse ones, and it is never tinted. Dropping it is what made distant ground
    /// read as flat green where vanilla reads olive, and it removed essentially all of the
    /// blue: the seasonal tint's blue channel is near zero, so anything multiplied by it
    /// loses its blue entirely, and in vanilla the dirt is not multiplied by it.
    ///
    /// And the engine's own `GetColorWithoutTint` cannot be used for these blocks at all -
    /// see G48. `BlockWithGrassOverlay` answers with `GetRandomColor`, whose values are raw
    /// `ToArgb` with red at bits 16-23, while `GetAverageColor` is byte-reversed with red
    /// in the low byte. The mod reads one convention, so every grass-covered block came out
    /// with red and blue exchanged. Both texture reads here go through `GetAverageColor`,
    /// so both are in the same order as everything else the mod stores.
    /// </summary>
    /// <summary>
    /// Where a block's colour texture sits in the atlas, and what that texture averages to.
    ///
    /// `TextureSubIdForBlockColor` is the engine's own answer for which of a block's textures
    /// represents it, so this asks rather than guessing at names like "up" or "side" - guessing
    /// left 976 blocks with no texture at all.
    ///
    /// The mean travels with it because the texture is applied as variation around the block's
    /// colour rather than as the colour. Grass-covered ground is the case that forces it: its
    /// base texture is brown soil and the green is an overlay composited on top, so a shader
    /// that took the texture at face value drew a brown band across the world.
    /// </summary>
    LodBlockTextureRegistry.BlockTexture ResolveBlockTexture(Block block)
    {
        block = AppearanceOf(block);
        resolvingTextureFor = block;
        try
        {
            return ResolveBlockTextureFor(block);
        }
        finally
        {
            resolvingTextureFor = null;
        }
    }

    LodBlockTextureRegistry.BlockTexture ResolveBlockTextureFor(Block block)
    {
        // The UP face, explicitly. `TextureSubIdForBlockColor` is the engine's generic "what
        // colour is this block" texture and for grass-covered soil that is the SIDE - which is
        // grass across its top half and bare dirt across its bottom half. Tiled onto a top
        // face that draws every block split diagonally between grass and dirt, which is
        // exactly what the owner reported. Asking by face name is right; falling back to the
        // generic texture is what keeps coverage high for blocks that name their faces
        // differently.
        CompositeTexture? face = null;
        int subId = -1;
        if (block.Textures != null)
        {
            foreach (string name in UpFaceTextureNames)
            {
                if (!block.Textures.TryGetValue(name, out CompositeTexture? candidate)) continue;
                int candidateId = candidate?.Baked?.TextureSubId ?? -1;
                if (!IsUsableFaceTexture(candidateId)) continue;
                face = candidate;
                subId = candidateId;
                break;
            }
        }
        if (subId < 0)
        {
            subId = block.TextureSubIdForBlockColor;
            if (!IsUsableAtlasTexture(subId)) return default;
            face = TextureFor(block, subId);
        }

        TextureAtlasPosition position = capi.BlockTextureAtlas.Positions[subId];
        TextureMean mean = MeanOf(face, subId);

        // Grass-covered ground is soil under a grass texture composited by the grass's own
        // per-pixel alpha - the same arithmetic the colour already uses. Without the overlay
        // the shader draws correctly coloured ground wearing dirt's pattern, which close up
        // reads as dirt.
        // The TOP overlay, not the side fringe. Most grassy soil names a separate fully-opaque
        // `particletop` texture. Forest floor does not: its specialSecondTexture is one 2:1
        // strip with the side in the left half and the top in the right. Vanilla's
        // TopsoilTesselator supplies only the left-half UVs, then chunktopsoil.fsh shifts one
        // tile right when normal.y is 1. Sampling the whole strip as one LOD tile painted the
        // proper side fringe across every forest-floor top.
        TextureAtlasPosition? overlayPosition = null;
        TextureMean composite = mean;
        if (block.RenderPass == EnumChunkRenderPass.TopSoil
            && block.Textures != null
            && TryTopSoilOverlay(block, out CompositeTexture? overlay, out bool rightHalf))
        {
            int overlayId = overlay?.Baked?.TextureSubId ?? -1;
            if (IsUsableFaceTexture(overlayId))
            {
                // One decision drives both the rectangle the shader samples and the mean it
                // normalises by. Deriving it twice is how the colour path and this one came
                // to disagree about the same texture in the first place.
                TextureAtlasPosition fullPosition = capi.BlockTextureAtlas.Positions[overlayId];
                bool topTile = rightHalf && IsTwoTileStrip(fullPosition);
                TextureMean grass = MeanOf(overlay, overlayId, topTile);
                if (grass.Coverage > 0f)
                {
                    overlayPosition = topTile
                        ? LodBlockTextureRegistry.RightHalf(fullPosition)
                        : fullPosition;
                    // Offered, not imposed: the registry refuses an overlay on another atlas
                    // page, and normalising by a composite the shader never samples is what
                    // turned grassy ground pale.
                    composite = new TextureMean(
                        LodTopSoil.Composite(mean.R, grass.R, grass.Coverage),
                        LodTopSoil.Composite(mean.G, grass.G, grass.Coverage),
                        LodTopSoil.Composite(mean.B, grass.B, grass.Coverage),
                        1f);
                }
            }
        }

        // The walls. The engine draws a cube's sides from the textures named for those faces,
        // and for a standing log that is bark while its top and bottom are the cut end, so one
        // texture for every face put rings up the whole trunk. A log lying on its side names
        // the cut end for two of its walls, which is why the two wall axes are asked apart.
        //
        // TopSoil keeps the top's texture and overlay on its walls, exactly as before: the
        // engine composites those sides in its own top-soil pass, and the look of distant
        // grass was tuned against what this draws today.
        LodBlockTextureRegistry.FaceTexture? wallX = null;
        LodBlockTextureRegistry.FaceTexture? wallZ = null;
        if (block.RenderPass != EnumChunkRenderPass.TopSoil && block.Textures != null)
        {
            wallX = WallTexture(block.Textures, XWallTextureNames, subId);
            wallZ = WallTexture(block.Textures, ZWallTextureNames, subId);
        }

        return new LodBlockTextureRegistry.BlockTexture(
            position, overlayPosition, PackMean(mean), PackMean(composite), wallX, wallZ);
    }

    /// <summary>
    /// The texture the engine draws on one wall axis, or null when it is the top's own - the
    /// registry then copies the top's rows, so nothing about that block changes.
    /// </summary>
    LodBlockTextureRegistry.FaceTexture? WallTexture(
        IDictionary<string, CompositeTexture> textures, string[] names, int topSubId)
    {
        int wallId = PickFaceTexture(textures, names, IsUsableFaceTexture,
            out CompositeTexture? wall, skipId: topSubId);
        if (wallId < 0 || wallId == topSubId) return null;
        return new LodBlockTextureRegistry.FaceTexture(
            capi.BlockTextureAtlas.Positions[wallId], PackMean(MeanOf(wall, wallId)));
    }

    /// <summary>
    /// The first of <paramref name="names"/> the block defines with a texture on the atlas,
    /// or -1. Face names come first, then the shorthands a block file groups them under, so a
    /// log's sides resolve to its bark whether or not the loader expanded "all" into faces.
    ///
    /// <paramref name="skipId"/> is passed over, and a wall passes over the top's own texture.
    /// Redwood trunks are why: the quarter-trunk block is drawn from its own shape, bark on
    /// the outside and the cut end on top, and the face names in its block file - which that
    /// shape does not use - say "north" is the cut end. Taking that at its word left rings on
    /// the north and south walls of every redwood. A log lying on its side is unaffected,
    /// because its cut end is not also its top.
    /// </summary>
    internal static int PickFaceTexture(IDictionary<string, CompositeTexture> textures,
        string[] names, System.Func<int, bool> usable, out CompositeTexture? face,
        int skipId = -1)
    {
        foreach (string name in names)
        {
            if (!textures.TryGetValue(name, out CompositeTexture? candidate)) continue;
            int id = candidate?.Baked?.TextureSubId ?? -1;
            if (!usable(id) || id == skipId) continue;
            face = candidate;
            return id;
        }
        face = null;
        return -1;
    }

    /// <summary>
    /// The overlay texture region that belongs on a TopSoil block's TOP face.
    ///
    /// `specialSecondTexture` first, because that is what the engine actually draws:
    /// `chunktopsoil.fsh` samples it and shifts one tile right for an upward normal, so the top
    /// face comes from the right half of that strip and from nothing else.
    ///
    /// It used to prefer `particletop`, which is a PARTICLE texture - what the game throws when
    /// you walk on grass - and for every `soil-*` block that is `normal-top` whatever the
    /// coverage is. So sparse and very sparse ground drew the full-grass pattern near the seam
    /// while vanilla drew its own thinner one. The colour was unaffected, because the mean the
    /// shader normalises by is the mean of whatever is actually sampled, so this only ever cost
    /// pattern accuracy - which is exactly why it survived: nothing about it looks wrong until
    /// you compare a sparse patch against the real thing.
    ///
    /// Safe for full grass too: `normal-top.png` and the right tile of `normal.png` are
    /// byte-identical, checked pixel by pixel, so this changes nothing where coverage is full.
    /// `particletop` remains the fallback for a block that names no strip.
    /// </summary>
    /// <summary>
    /// Whether an atlas rectangle holds vanilla's two-tile TopSoil strip: the side fringe in
    /// the left half, the top face in the right.
    ///
    /// Asked rather than assumed, because taking the right half of a texture that is not a
    /// strip would read a neighbouring block's tile. The engine's own rule is the same test
    /// by implication - `chunktopsoil.fsh` shifts the top face one tile right, which is only
    /// meaningful when there is a second tile to shift into.
    ///
    /// Compared in atlas UV, which is why this is a ratio and not a pixel count; the block
    /// atlas is square, so the two axes are the same scale. 1.5 rather than 2.0 leaves room
    /// for a rectangle trimmed by padding without letting a square texture through.
    /// </summary>
    internal static bool IsTwoTileStrip(TextureAtlasPosition position) =>
        position.x2 - position.x1 >= (position.y2 - position.y1) * 1.5f;

    static bool TryTopSoilOverlay(
        Block block, out CompositeTexture? texture, out bool rightHalf)
    {
        if (block.Textures!.TryGetValue("specialSecondTexture", out texture)
            && texture?.Baked != null)
        {
            rightHalf = true;
            return true;
        }
        if (block.Textures.TryGetValue("particletop", out texture) && texture?.Baked != null)
        {
            rightHalf = false;
            return true;
        }
        texture = null;
        rightHalf = false;
        return false;
    }

    /// <summary>
    /// Face names to try for the up face, in order. Ground seen from above is nearly all top
    /// faces, and a block that names none of these falls back to the generic block colour.
    /// </summary>
    internal static readonly string[] UpFaceTextureNames = { "up", "all", "top" };

    /// <summary>
    /// Face names for the two wall axes: faces along X are east and west, faces along Z are
    /// north and south, matching the shader's choice of rows by normal.
    /// </summary>
    internal static readonly string[] XWallTextureNames =
        { "east", "west", "horizontals", "sides", "all" };
    internal static readonly string[] ZWallTextureNames =
        { "north", "south", "horizontals", "sides", "all" };

    static int PackMean(TextureMean mean) => unchecked((int)0xFF000000)
        | Math.Clamp((int)(mean.B + 0.5f), 0, 255) << 16
        | Math.Clamp((int)(mean.G + 0.5f), 0, 255) << 8
        | Math.Clamp((int)(mean.R + 0.5f), 0, 255);

    bool TryTopSoilColor(Block block, out int composite, out LodUntintedShare share)
    {
        composite = 0;
        share = LodUntintedShare.None;

        if (block.RenderPass != EnumChunkRenderPass.TopSoil) return false;
        if (block.Textures == null
            || !block.Textures.TryGetValue("specialSecondTexture", out CompositeTexture? overlay)) return false;

        int overlayId = overlay?.Baked?.TextureSubId ?? -1;
        if (!IsUsableAtlasTexture(overlayId) || !IsUsableAtlasTexture(block.TextureSubIdForBlockColor)) return false;

        TextureMean soil = MeanOf(TextureFor(block, block.TextureSubIdForBlockColor),
            block.TextureSubIdForBlockColor);

        // The TOP tile, not the whole strip. `specialSecondTexture` is two tiles wide - the
        // side fringe on the left, the top on the right - and `chunktopsoil.fsh` shifts one
        // tile right for an upward normal, so a top face is drawn entirely from the right
        // half. This averaged both halves, and the fringe is mostly transparent, so the
        // COVERAGE came out far too low: 0.687 for full grass where the top tile is 1.000,
        // 0.537 against 0.843 for sparse, 0.400 against 0.720 for very sparse.
        //
        // Coverage is what decides how much bare dirt shows through, and that dirt is the
        // share the seasonal tint must not touch. So the mod believed roughly a third of
        // every fully grassy block was untinted brown when none of it is, and since the
        // seasonal tint carries almost no blue, that untinted third is where nearly all the
        // blue in distant grass was coming from. Measured on the owner's own cache before
        // this change: soil-medium-normal stored 128,120,98 against a true 152,154,127, and
        // grass at the seam carried 1.9x vanilla's blue.
        //
        // The registered tint slot follows from the share, and both are re-derived from the
        // live block when a cached section loads, so an existing cache corrects itself
        // without re-exploring anything.
        bool topTile = IsTwoTileStrip(capi.BlockTextureAtlas.Positions[overlayId]);
        TextureMean grass = MeanOf(overlay, overlayId, topTile);
        if (grass.Coverage <= 0f) return false;

        float a = grass.Coverage;
        composite = unchecked((int)0xFF000000)
            | Channel(soil.B, grass.B, a) << 16 | Channel(soil.G, grass.G, a) << 8 | Channel(soil.R, grass.R, a);
        share = new LodUntintedShare(
            LodTopSoil.UntintedShare(soil.R, grass.R, a),
            LodTopSoil.UntintedShare(soil.G, grass.G, a),
            LodTopSoil.UntintedShare(soil.B, grass.B, a));
        return true;
    }

    static int Channel(float soil, float grass, float a) =>
        Math.Clamp((int)(LodTopSoil.Composite(soil, grass, a) + 0.5f), 0, 255);

    /// <summary>The block's own texture behind a given atlas sub-id, if it has one.</summary>
    static CompositeTexture? TextureFor(Block block, int subId)
    {
        if (block.Textures == null) return null;
        foreach (CompositeTexture tex in block.Textures.Values)
        {
            if ((tex?.Baked?.TextureSubId ?? -1) == subId) return tex;
        }
        return null;
    }

    /// <summary>
    /// A texture's colour and coverage, over every pixel of it.
    ///
    /// <c>GetAverageColor</c> is not an average: the engine samples exactly four pixels, at
    /// 35% and 65% of each axis, and calls that the texture's colour. For an opaque texture
    /// that is close enough. For a partly transparent OVERLAY it is not, because those same
    /// four pixels also decide how much of the block below shows through - measured at 146
    /// against a true 175 for full grass coverage, a fifth too much bare dirt, which was the
    /// whole of the residual error left after 0.3.20 composited the two layers at all.
    ///
    /// So read the texture. The colour is alpha-weighted, because a pixel that is barely
    /// there should barely count, and the pair (alpha-weighted colour, mean alpha) is exactly
    /// what averaging vanilla's per-pixel blend over the whole face reduces to.
    /// </summary>
    readonly record struct TextureMean(float R, float G, float B, float Coverage);

    readonly Dictionary<(AssetLocation Location, bool RightHalf), TextureMean> textureMeans = new();

    TextureMean MeanOf(CompositeTexture? tex, int atlasSubId, bool rightHalf = false)
    {
        AssetLocation? loc = tex?.Base?.Clone().WithPathPrefixOnce("textures/").WithPathAppendixOnce(".png");
        if (loc != null && textureMeans.TryGetValue((loc, rightHalf), out TextureMean cached)) return cached;

        TextureMean mean = loc != null && TryReadTextureMean(loc, rightHalf, out TextureMean read)
            ? read
            : AtlasMean(atlasSubId);

        if (loc != null) textureMeans[(loc, rightHalf)] = mean;
        return mean;
    }

    /// <summary>The four-pixel atlas answer, for a texture whose own file cannot be read.</summary>
    TextureMean AtlasMean(int subId)
    {
        int c = capi.BlockTextureAtlas.GetAverageColor(subId);
        return new TextureMean(c & 0xFF, (c >> 8) & 0xFF, (c >> 16) & 0xFF, ((c >> 24) & 0xFF) / 255f);
    }

    bool TryReadTextureMean(AssetLocation loc, bool rightHalf, out TextureMean mean)
    {
        mean = default;
        try
        {
            IAsset? asset = capi.Assets.TryGet(loc);
            if (asset?.Data == null) return false;

            using BitmapExternal bmp = capi.Render.BitmapCreateFromPng(asset.Data);

            // Read through Pixels, not GetPixel: the latter returns an SKColor, which would
            // put SkiaSharp on the mod's reference list for no gain. Both are 0xAARRGGBB -
            // red at bits 16-23, the order the atlas reads pixels in before it reverses the
            // bytes for AvgColor and does not for RndColors (G48). The caller puts red back
            // in the low byte. The decoder asks for unpremultiplied alpha, so weighting the
            // colour by it below is a weighting and not a second application of it.
            int[] argb = bmp.Pixels;
            if (argb == null || argb.Length == 0) return false;
            int width = bmp.Width;
            int height = bmp.Height;
            if (width <= 0 || height <= 0 || argb.Length != width * height) return false;
            int firstX = rightHalf ? width / 2 : 0;
            long pixels = (long)(width - firstX) * height;

            double r = 0, g = 0, b = 0, alpha = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = firstX; x < width; x++)
                {
                    int p = argb[y * width + x];
                    double aPixel = ((p >> 24) & 0xFF) / 255.0;
                    r += ((p >> 16) & 0xFF) * aPixel;
                    g += ((p >> 8) & 0xFF) * aPixel;
                    b += (p & 0xFF) * aPixel;
                    alpha += aPixel;
                }
            }

            if (alpha <= 0) return false;

            mean = new TextureMean((float)(r / alpha), (float)(g / alpha), (float)(b / alpha),
                (float)(alpha / pixels));
            return true;
        }
        catch (Exception e)
        {
            Mod.Logger.Notification(
                "Could not read texture '{0}' for its true average; using the atlas estimate instead. {1}",
                loc, e.Message);
            return false;
        }
    }

    /// <summary>
    /// A colour texture that resolved to the unknown.png placeholder is not a colour, it
    /// is the absence of one; so is a block-colour texture the atlas never assigned. Both
    /// fall back to another of the block's own textures, and to the caller's stand-in if
    /// the block has none - wrong-but-plausible beats a placeholder or a hole in the world.
    /// </summary>
    int RepairPlaceholder(Block block, int color, int fallback) =>
        IsPlaceholderColor(block, color) ? ColorFromAnyTexture(block, fallback) : color;

    /// <summary>
    /// Whether a block's colour texture is the absence of a colour rather than a colour:
    /// no texture assigned, or one that resolved to the missing-texture placeholder.
    ///
    /// Named once because two callers have to agree about it. The repair above acts on it,
    /// and <see cref="StableColorOf"/> reads it to decide whether it may replace the sampled
    /// colour with the texture's exact average - which it must not do first, because this
    /// test inspects the sampled value.
    /// </summary>
    bool IsPlaceholderColor(Block block, int color) =>
        !IsUsableAtlasTexture(block.TextureSubIdForBlockColor) || IsPlaceholderRgb(color);

    /// <summary>
    /// The placeholder in any form it reaches a palette - see LodPaletteRepair.IsPlaceholder.
    /// Until 0.4.62 this compared the atlas's alpha-free value exactly, which the averaged
    /// colour in StableColorOf (always opaque) could never equal, so the repair never fired
    /// there and every chiselled block a server captured was stored near-white.
    /// </summary>
    bool IsPlaceholderRgb(int color) =>
        LodPaletteRepair.IsPlaceholder(color, unknownTextureColor, unknownTextureMean);

    /// <summary>Average colour of unknown.png (near-white, not magenta - measured).</summary>
    int unknownTextureColor;

    /// <summary>
    /// unknown.png's own mean (255,249,249): what a colour read from a texture file comes to
    /// when that file resolved to the placeholder. 0 if the file could not be read.
    /// </summary>
    int unknownTextureMean;

    /// <summary>Stored placeholder colours replaced on load. See RepairPlaceholderEntries.</summary>
    int placeholderEntriesRepaired;

    /// <summary>Foreign palette entries drawn as grey because no block matched.</summary>
    int uncoloredForeignEntries;

    /// <summary>
    /// Colour for one block id, for repairing a cache saved without any. Separate from
    /// DescribePalette because that one also answers the tint slot and needs a world
    /// position; here the block is all there is to go on.
    /// </summary>
    public int AtlasColorOf(int blockId)
    {
        if (blockId <= 0) return LodPaletteRepair.UnknownBlockColor;

        // Deliberately the same answer capture gives, so a repaired entry cannot become
        // a colour step against the section next to it. The repair has only a block id
        // to go on, so a chiselled block repairs to neutral grey here and gets its real
        // materials on the next capture.
        return StableColorOf(capi.World.Blocks[blockId]);
    }

    /// <summary>
    /// Stored placeholder colours on blocks whose look lives in a block entity. The load path
    /// keeps those colours as stored, because they are per position (G46), so a near-white
    /// one written before 0.4.62 would draw white for ever. The replacement is what capture
    /// gives the same block today when its entity cannot be read. A plain block is left to
    /// Reclassify, which has already re-derived it from the live block.
    /// </summary>
    int RepairPlaceholderEntries(LodSection section)
    {
        int repaired = LodPaletteRepair.ReplacePlaceholders(section, IsPlaceholderRgb, blockId =>
        {
            Block? block = blockId < capi.World.Blocks.Count ? capi.World.Blocks[blockId] : null;
            return block != null && block.EntityClass != null
                ? ColorFromAnyTexture(block, LodPaletteRepair.UnknownBlockColor)
                : 0;
        });
        placeholderEntriesRepaired += repaired;
        return repaired;
    }

    /// <summary>
    /// Whether an atlas sub-id actually names a texture. `GetAverageColor` on an unassigned
    /// or out-of-range sub-id reads whatever the atlas holds there, which is where a
    /// nonsense LOD colour comes from - and unlike the unknown.png case, that does not
    /// require knowing what the placeholder looks like to detect.
    /// </summary>
    bool IsUsableAtlasTexture(int subId)
    {
        if (subId < 0) return false;

        TextureAtlasPosition[] positions = capi.BlockTextureAtlas.Positions;
        return subId < positions.Length && positions[subId] != null;
    }

    /// <summary>
    /// A texture asked for BY FACE NAME, usable only if what it holds is not the game's
    /// missing-texture image ("?" on grey).
    ///
    /// The owner reported that image on the walls of nearby cached terrain on 2026-10-13, and
    /// had seen it for a long time; vanilla draws the same blocks correctly. The 0.4.141 log
    /// named the route: ordinary cube blocks - granite, basalt, peridotite, ores, soil, water -
    /// carry an "all" entry whose texture is game:unknown, at atlas slot 0. The engine never
    /// draws it, because every face has its own entry. But a wall passes over the top's own
    /// texture (the redwood rule in <see cref="PickFaceTexture"/>), and on a block whose faces
    /// all share one texture that skips every face name and lands on "all". Such a face is
    /// passed over, so the wall copies the top - which for these blocks is their side texture.
    /// The test is on the CONTENT (slot 0, or an exact match of its average colour), so a
    /// missing file copied into its own slot is caught too. The first is logged, the rest counted.
    ///
    /// Kept apart from <see cref="IsUsableAtlasTexture"/> on purpose: the block-colour path
    /// still accepts that image, because a block the engine itself draws as the "?" box (from
    /// a mod since removed) keeps that look at distance too.
    /// </summary>
    bool IsUsableFaceTexture(int subId)
    {
        if (!IsUsableAtlasTexture(subId)) return false;
        if (!IsMissingTextureImage(subId)) return true;
        NoteUnloadedFaceTexture(subId);
        return false;
    }

    int? missingTextureImageColor;

    bool IsMissingTextureImage(int subId)
    {
        ITextureAtlasAPI atlas = capi.BlockTextureAtlas;
        if (ReferenceEquals(atlas.Positions[subId], atlas.UnknownTexturePosition)) return true;
        missingTextureImageColor ??= atlas.GetAverageColor(0);
        return atlas.GetAverageColor(subId) == missingTextureImageColor.Value;
    }

    /// <summary>The block whose face textures are being resolved, for the log line below.</summary>
    Block? resolvingTextureFor;
    readonly HashSet<int> unloadedFaceTextureBlocks = new();

    /// <summary>
    /// One log line, on the first block found, then only a count in the texture status line.
    /// The 0.4.141 build named every block (twelve at every start, all ordinary rock and soil),
    /// which answered the question and then became noise for every player (owner, 2026-10-13).
    /// </summary>
    void NoteUnloadedFaceTexture(int subId)
    {
        Block? block = resolvingTextureFor;
        if (block == null || !unloadedFaceTextureBlocks.Add(block.BlockId)) return;
        blockTextures.RefusedMissingImageBlocks = unloadedFaceTextureBlocks.Count;
        if (unloadedFaceTextureBlocks.Count > 1) return;
        string faces = block.Textures == null ? "?" : string.Join(", ", block.Textures
            .Where(entry => entry.Value?.Baked?.TextureSubId == subId)
            .Select(entry => $"{entry.Key}={entry.Value.Base}"));
        Mod.Logger.Notification(
            "[Landscapes] Some blocks carry a face texture that is the game's missing-texture image "
            + "(first: '{0}', {1}); the game never draws it, and distant terrain uses the block's own "
            + "texture there instead. The texture status line counts them.", block.Code, faces);
    }

    readonly Dictionary<int, int> missingTextureColorFallback = new();
    int missingTextureBlocks;
    bool loggedMissingTexture;

    /// <summary>
    /// Salvage a colour for a block whose block-colour texture did not resolve, so it draws
    /// as itself instead of as a placeholder or as whatever the atlas holds at a bogus id.
    ///
    /// Vanilla picks that texture in Block.LoadTextureSubIdForBlockColor: the
    /// 'textureCodeForBlockColor' attribute, else "up", else `Textures.First()` - and that
    /// last step ends in `?? 0`, so a block whose first texture in dictionary order has no
    /// Baked entry silently resolves to atlas subid 0, which is unknown.png. The block's
    /// other faces are baked fine, which is why it looks correct up close and magenta only
    /// in LOD. Measured firing on vanilla 'fruitingbush-wild-blackberry-free', so this is
    /// not a modded-content problem -- content-heavy block packs just hit it more often.
    ///
    /// So: use any of the block's own baked textures instead of the first one. Cached per
    /// block id - the answer cannot change within a session, and a palette entry is
    /// registered once per section, which is thousands of times per world.
    /// </summary>
    int ColorFromAnyTexture(Block block, int fallback)
    {
        // The cache holds only the block's own answer, with 0 for "no usable texture"
        // (no real block averages to 0, the same invariant colour-0 repair rests on).
        // The fallback is the caller's and is applied per call: the capture path passes
        // the probe colour, the foreign path passes grey, and caching whichever caller
        // came first hands one caller's stand-in to the others.
        if (missingTextureColorFallback.TryGetValue(block.BlockId, out int cached))
        {
            return cached != 0 ? cached : fallback;
        }

        int found = 0;
        if (block.Textures != null)
        {
            foreach (CompositeTexture tex in block.Textures.Values)
            {
                int subId = tex?.Baked?.TextureSubId ?? -1;
                if (!IsUsableAtlasTexture(subId)) continue;

                int candidate = capi.BlockTextureAtlas.GetAverageColor(subId);
                if (IsPlaceholderRgb(candidate)) continue;

                found = candidate;
                break;
            }
        }

        missingTextureColorFallback[block.BlockId] = found;
        missingTextureBlocks++;
        if (!loggedMissingTexture)
        {
            loggedMissingTexture = true;
            Mod.Logger.Notification(found != 0
                ? "Block '{0}' has no usable block-colour texture (vanilla resolved it to unknown.png). "
                    + "The mod uses another of its own textures instead, so it does not render wrong at distance."
                : "Block '{0}' has no usable texture of its own (vanilla draws it as the missing-texture box). "
                    + "At distance it keeps that look, or neutral grey if its appearance lives in a block entity.",
                block.Code);
        }
        return found != 0 ? found : fallback;
    }

    /// <summary>
    /// Find a block using the standard plant tint, so plants that declare no colour map
    /// (ferns) can borrow it instead of rendering as their greyscale texture.
    /// </summary>
    void ResolvePlantTintFallback()
    {
        foreach (Block block in capi.World.Blocks)
        {
            if (block?.Code == null) continue;
            if (block.SeasonColorMapResolved != null) continue;
            if (block.ClimateColorMapResolved == null) continue;
            if (block.ClimateColorMap != "climatePlantTint") continue;

            tints.PlantTintFallback = block;
            return;
        }
    }

    readonly System.Diagnostics.Stopwatch joinClock = new();
    static readonly int[] FillInMilestones = { 1, 100, 300, 600, 1200 };
    int nextMilestone;
    bool joinStallReported;

    /// <summary>
    /// A join is only ever seen from the outside as "the horizon took a while". Six joins
    /// on record land between 2.3 and 9.1 seconds to the first hundred meshes; one landed
    /// at 60.2, with no meshes at all after thirty seconds and every queue in the mod
    /// empty. Nothing in the log says which stage was not running, so the difference
    /// between "slow" and "not started" cannot be told apart afterwards.
    ///
    /// This reports the bootstrap state once, if the first mesh has not appeared within
    /// ten seconds. The renderer skips its selection walk entirely until a mesh exists,
    /// and the walk is what asks for sections, so the first mesh has to come from the
    /// dirty set instead - which makes "how many sections were dirty, how many loads were
    /// asked for, how many frames were skipped" the three numbers that name the stall.
    /// </summary>
    const double JoinStallSeconds = 10;

    void ReportFillIn()
    {
        while (nextMilestone < FillInMilestones.Length && renderer.MeshCount >= FillInMilestones[nextMilestone])
        {
            Mod.Logger.Notification(
                nextMilestone == 0 ? "Fill-in: first mesh after {1:0.0}s" : "Fill-in: {0} meshes after {1:0.0}s",
                FillInMilestones[nextMilestone], joinClock.Elapsed.TotalSeconds);
            nextMilestone++;
        }

        if (joinStallReported || renderer.MeshCount > 0) return;
        if (joinClock.Elapsed.TotalSeconds < JoinStallSeconds) return;

        joinStallReported = true;
        Mod.Logger.Notification(
            "Join: no cached terrain built after {0:0.0}s. {1} sections known, {2} resident, "
            + "{3} render-dirty, {4} loads in flight, {5} columns captured ({6} pending), "
            + "{7} mesh jobs queued, {8} render frames skipped for want of a mesh. "
            + "Radial demand: {9}.",
            joinClock.Elapsed.TotalSeconds,
            pipeline.CachedSectionsLoaded,
            pipeline.World.Sections.Count,
            pipeline.World.RenderDirty.Count,
            pipeline.World.LoadsInFlight.Count,
            pipeline.ColumnsCaptured,
            pipeline.PendingColumns,
            pipeline.Worker.PendingMeshes,
            renderer.FramesWithoutMeshes,
            renderer.DescribeRadialDemand());
    }

    void OnLevelFinalize()
    {
        ResolvePlantTintFallback();
        // Read once here rather than per palette entry: the atlas exists by now, and a
        // reload would change the position object but not what magenta looks like.
        unknownTextureColor = capi.BlockTextureAtlas.UnknownTexturePosition.AvgColor;
        // The same placeholder read from its file: a colour taken from a texture's own pixels
        // (PackMean) comes to this rather than to the atlas estimate, and both reached caches.
        unknownTextureMean = TryReadTextureMean(new AssetLocation("game", "textures/unknown.png"),
            false, out TextureMean placeholderMean) ? PackMean(placeholderMean) : 0;
        Mod.Logger.Debug("Missing-texture colour is {0:X8}, {1:X8} from its file{2}",
            unknownTextureColor, unknownTextureMean,
            unknownTextureColor == 0 ? " (zero: magenta-block salvage disabled)" : "");
        ResolveWaterSurfaceTexture();
        renderer.ApplyZFar();
        pipeline.Open("ModData/dodenslandscapes");
        SetSilhouetteWorldStore();

        // AFTER Open, which is what creates the writer. Taking this at startup instead read a
        // null - the pipeline builds its storage thread when a world opens, not in its
        // constructor - so remembered climate never reached disk at all, and the in-memory
        // learning that still worked within a session hid it completely.
        renderer.ClimateStorage = pipeline.StorageThread;
        renderer.TextureSlotSourceReady = pipeline.TextureSlotsPrepared;
        joinClock.Restart();
        joinStallReported = false;
        nextMilestone = 0;

        // A singleplayer world whose server side has swept the savegame leaves its results
        // in a sibling cache. Nothing to open on a dedicated server, where the same
        // sections arrive over the network instead.
        if (pipeline.DbPath is string dbPath)
        {
            localOffers = LodLocalOfferSource.TryOpen(dbPath, Mod.Logger);
        }

        // The record of the server's cache this client caught up with, read beside the key scan.
        // Integrated play does not take part: the sibling cache opened above already says which
        // rows changed, and a second account of the same fact is how the two come to disagree.
        serverSync.Load(pipeline.LoadAssistSync());
        LodAssistSyncRequest? sync = serverSync.Begin(participate: localOffers == null);

        // Last, and after the pipeline is live. An exception in a LevelFinalize handler
        // skips everything the handler has left to do, so an optional extra must not sit
        // upstream of the mod's actual job -- it did, and it broke exactly the
        // vanilla-server case it exists to stay out of the way of.
        assist?.Greet(sync);

        Mod.Logger.Notification(
            "Level finalized. LOD capture active (render distance: unlimited, {0} sections from cache{1}).",
            pipeline.CachedSectionsLoaded, renderer.AutoUnpause ? ", auto-unpause on" : "");

        capi.Event.RegisterCallback(_ => LogStats("Stats after 30s"), 30000);

        // Continuous telemetry. Was tied to AutoUnpause, which meant an *attended* session
        // - the only kind where someone can say "this looks wrong" - was the one case with
        // no ongoing numbers to explain it. Its own switch now, so watching and driving are
        // independent.
        if (allocationTelemetryEnabled)
        {
            capi.Event.RegisterGameTickListener(_ => LogStats("Stats"), 15000);
        }

        RegisterAutoCommand();

        autoExplore = Environment.GetEnvironmentVariable("VINTAGEHORIZONS_AUTOEXPLORE") == "1";

        // Creative for any unattended run, not only an exploring one.
        //
        // A survival player is moved by things the test did not ask for: knockback,
        // drowning, hunger, a mob, and at worst a death that respawns them somewhere
        // else entirely. Player position is an INPUT to most of what these scenarios
        // measure - which chunks stream in, where /dlgen centres, and which positions
        // the absence verifier excludes as explainable by a nearby player. A scenario
        // whose subject wanders is measuring the wander.
        //
        // Sent before the auto-command below fires, so a /dlgen run is already centred
        // on a player who will stay put.
        if (autoExplore || Environment.GetEnvironmentVariable("VINTAGEHORIZONS_CREATIVE") == "1")
        {
            capi.Event.RegisterCallback(_ =>
            {
                Mod.Logger.Notification("Test run: entering creative so the player cannot be moved");
                capi.SendChatMessage("/gamemode creative");
            }, 10000);
        }

        if (autoExplore)
        {
            exploreX = capi.World.Player.Entity.Pos.X;
            exploreZ = capi.World.Player.Entity.Pos.Z;
            capi.Event.RegisterGameTickListener(_ => ExploreHop(), 60000);
            Mod.Logger.Notification("Auto-explore active (spiral teleports every 60s)");
        }
    }

    /// <summary>
    /// Resolve the one ordinary-water atlas tile used by the first dedicated texture
    /// comparison. The translucent mesh also contains lava and ice, so its tint slot travels
    /// beside the texture slot and prevents the shader from applying water detail to them.
    /// </summary>
    void ResolveWaterSurfaceTexture()
    {
        try
        {
            Block? water = capi.World.Blocks.FirstOrDefault(block =>
                block?.Code?.Domain == "game" && block.Code.Path == "water-still-7")
                ?? capi.World.Blocks.FirstOrDefault(block =>
                    block?.Code?.Domain == "game"
                    && block.Code.Path.StartsWith("water-still-", StringComparison.Ordinal));
            if (water == null)
            {
                renderer.WaterSurfaceTextureSlot = 0;
                renderer.WaterSurfaceTintSlot = -1;
                Mod.Logger.Warning(
                    "[Landscapes] standard water texture unavailable; cached water keeps its averaged surface colour.");
                return;
            }

            renderer.WaterSurfaceTextureSlot = blockTextures.SlotFor(water, StableColorOf(water));
            renderer.WaterSurfaceTintSlot = TintSlotOf(water);
            Mod.Logger.Notification(
                "[Landscapes] standard water surface texture: {0}, atlas slot {1}, tint slot {2}.",
                water.Code, renderer.WaterSurfaceTextureSlot, renderer.WaterSurfaceTintSlot);
        }
        catch (Exception e)
        {
            renderer.WaterSurfaceTextureSlot = 0;
            renderer.WaterSurfaceTintSlot = -1;
            Mod.Logger.Warning(
                "[Landscapes] standard water texture could not be resolved; cached water keeps its averaged surface colour: {0}",
                e.Message);
        }
    }

    static readonly int ExploreHopBlocks =
        int.TryParse(Environment.GetEnvironmentVariable("VINTAGEHORIZONS_EXPLORE_HOP"), out int h) && h > 0 ? h : 350;

    void ExploreHop()
    {
        int hop = ExploreHopBlocks;

        exploreX += exploreDirX * hop;
        exploreZ += exploreDirZ * hop;

        // Square spiral: legs lengthen every second turn.
        if (++exploreStep >= exploreLeg / 2 + 1)
        {
            exploreStep = 0;
            exploreLeg++;
            (exploreDirX, exploreDirZ) = (-exploreDirZ, exploreDirX);
        }

        int y = capi.World.SeaLevel + 140;
        capi.SendChatMessage($"/tp ={(int)exploreX} {y} ={(int)exploreZ}");
    }

    bool loggedFirstCaptureError, loggedFirstMeshError, loggedFirstMipError, loggedFirstSaveError;
    bool allocationTelemetryEnabled;
    int gen0AtLastReport, gen1AtLastReport, gen2AtLastReport;
    LodPhaseCost totalTickCost, assistTickCost, localOfferTickCost, pipelineTickCost,
        residentEvictTickCost;

    void LogStats(string prefix)
    {
        LodWorld world = pipeline.World;
        LodWorker worker = pipeline.Worker;
        LodStorageThread? storageThread = pipeline.StorageThread;

        if (!loggedFirstCaptureError && worker.FirstCaptureError != null)
        {
            loggedFirstCaptureError = true;
            Mod.Logger.Warning("First capture error was: {0}", worker.FirstCaptureError);
        }
        if (!loggedFirstMeshError && worker.FirstMeshError != null)
        {
            loggedFirstMeshError = true;
            Mod.Logger.Warning("First mesh error was: {0}", worker.FirstMeshError);
        }
        if (!loggedFirstMipError && worker.FirstMipError != null)
        {
            loggedFirstMipError = true;
            Mod.Logger.Warning("First mip error was: {0}", worker.FirstMipError);
        }

        Mod.Logger.Notification(
            "{0}: {1} sections resident [{2}] ({3} RAM-evicted, {4} from cache), {5} meshes ({6} evicted, {23} seam repairs), " +
            "{7} selected [{8}] minus {9} draw-culled ({22} subtrees traversal-culled), {10} columns captured, {11} pending " +
            "({24} unloaded-before-capture drops), " +
            "worker: {12} captures / {13} meshes / {14} mips queued / {15}+{16}+{17} errors, " +
            "{18} awaiting mip ({19} in flight), {20} render-dirty, {21} unsaved",
            prefix, world.Sections.Count, world.DescribeLevels(), world.EvictedSectionsTotal, pipeline.CachedSectionsLoaded,
            renderer.MeshCount, renderer.EvictedTotal, renderer.LastDrawCount, renderer.DescribeDrawnLevels(),
            renderer.LastCulledCount, pipeline.ColumnsCaptured, pipeline.PendingColumns,
            worker.PendingCaptures, worker.PendingMeshes, worker.PendingMips,
            worker.CaptureErrors, worker.MeshErrors, worker.MipErrors,
            world.MipDirty.Count, world.MipInFlightCount, world.RenderDirty.Count, world.SaveDirty.Count,
            renderer.LastTraversalCulledCount, renderer.SeamRepairsQueued,
            pipeline.CaptureDropsUnloaded);

        // The amplification between real changes and mesh rebuilds, counted rather than
        // inferred. A stationary run previously had to estimate it from 64 changed
        // sections against 5,057 mesh replacements with nothing recording the middle term.
        Mod.Logger.Notification(
            "  change locality: {0} content changes made {1} meshes stale ({2:0.00} per change); " +
            "{3} neighbour rebuilds skipped as interior",
            world.MarkChangedCalls, world.MarkChangedRenderDirtied,
            world.MarkChangedCalls > 0 ? (double)world.MarkChangedRenderDirtied / world.MarkChangedCalls : 0,
            world.MarkChangedNeighborsSkipped);

        // Chiselled blocks and placeholder colours (0.4.62): whether distant structures are
        // drawn from what they are made of, or from the missing-texture image.
        Mod.Logger.Notification(
            "  chiselled blocks: {0} captured as their most visible material, {1} with no material "
            + "that can stand in, {2} column reads failed | {3} stored placeholder colours repaired on load",
            pipeline.ChiselStandIns, pipeline.ChiselsWithoutMaterial, pipeline.ChiselCollectErrors,
            placeholderEntriesRepaired);

        Mod.Logger.Notification(
            "  storage on main thread since last report: snapshot {0} calls, {1:0.00}ms avg, {2:0.00}ms max | " +
            "inline loads {3} calls, {4:0.00}ms avg, {5:0.00}ms max | storage thread: {6} write backlog, " +
            "{7} written, {8} write errors, {11} read, {9} async loads in flight, {10} read errors",
            pipeline.SaveCalls, pipeline.SaveCalls > 0 ? pipeline.SaveMsTotal / pipeline.SaveCalls : 0, pipeline.SaveMsMax,
            pipeline.LoadCalls, pipeline.LoadCalls > 0 ? pipeline.LoadMsTotal / pipeline.LoadCalls : 0, pipeline.LoadMsMax,
            storageThread?.Backlog ?? 0, storageThread?.SectionsWritten ?? 0, storageThread?.SaveErrors ?? 0,
            world.LoadsInFlight.Count, storageThread?.LoadErrors ?? 0, storageThread?.SectionsRead ?? 0);

        if (assist != null && assist.RemoteKeys.Count > 0)
        {
            Mod.Logger.Notification(
                "  server assist: {0} offered, {1} remote-only, {2} wanted by view, {3} requested, " +
                "{4} received, {5} installed, {6} in flight, peak {7}, {8} declined",
                assist.RemoteKeys.Count, pipeline.RemoteOnly.Count, pipeline.RemoteWanted().Length,
                assist.SectionsRequested, assist.SectionsReceived, pipeline.ForeignSectionsInstalled,
                assist.InFlight, assist.PeakInFlight, assist.SectionsRefused);
        }

        if (localOffers != null || localOfferKeysDiscovered > 0)
        {
            Mod.Logger.Notification(
                "  local sibling offers: {0} rows scanned, {1} offered, {2} fills queued, "
                + "{3} retryable misses, {4} accepted, {5} installed, {6} remote-only, {7} wanted",
                localOfferRowsScanned, localOfferKeysDiscovered, localOfferFillsQueued,
                localOfferRetryableMisses, localOfferSectionsAccepted, localOfferSectionsInstalled,
                pipeline.RemoteOnly.Count, pipeline.RemoteWanted().Length);
        }

        if (localOffers != null || pipeline.PendingFills > 0 || pipeline.ForeignMerges > 0)
        {
            Mod.Logger.Notification("  held sections: {0}; {1}",
                pipeline.DescribeFills(), DescribeSiblingScanner());
        }
        if (serverSync.Active) Mod.Logger.Notification("  server sync: {0}", serverSync.Describe());

        // Repairing means the cache on disk was written without colours, which drew as
        // black ground. Worth saying out loud, and worth being able to watch go to zero.
        if (pipeline.PaletteEntriesRepaired > 0)
        {
            Mod.Logger.Notification(
                "  repaired {0} palette entries that were cached with no colour at all. "
                + "They drew as black terrain, and the repair is written back as they load.",
                pipeline.PaletteEntriesRepaired);
        }

        // Warning, not notification: this is terrain drawn as a guess. Naming the codes is
        // the whole point - the previous symptom was black ground and nothing to grep for.
        if (uncoloredForeignEntries > 0)
        {
            string[] codes = pipeline.UnresolvedBlockCodes();
            Mod.Logger.Warning(
                "  {0} palette entries came from blocks this game does not have, and are drawn as "
                + "plain grey. {1}",
                uncoloredForeignEntries,
                codes.Length > 0
                    ? "Codes: " + string.Join(", ", codes.Take(12))
                      + (codes.Length > 12 ? $" and {codes.Length - 12} more" : "")
                    : "No unresolved codes were recorded, so these arrived over the network rather "
                      + "than from the cache.");
        }

        if (renderer != null && renderer.WalkCost.Calls > 0)
        {
            // Microseconds, because that is the scale these are at, and because rounding
            // them to milliseconds would print four zeroes and teach nobody anything.
            // A frame-rate comparison cannot see any of these: the benchmark's own
            // run-to-run spread is wider than all four added together.
            Mod.Logger.Notification(
                "  render thread per frame over {0} frames: prune {9:0.0}us avg / {10:0.0}us max | " +
                "schedule {1:0.0}/{2:0.0} | " +
                "far distance {3:0.0}/{4:0.0} | readiness shadow {11:0.0}/{12:0.0} | "
                + "quadtree walk {5:0.0}/{6:0.0} | draw submit {7:0.0}/{8:0.0}",
                renderer.WalkCost.Calls,
                renderer.ScheduleCost.AvgUs, renderer.ScheduleCost.MaxUs,
                renderer.FarDistanceCost.AvgUs, renderer.FarDistanceCost.MaxUs,
                renderer.WalkCost.AvgUs, renderer.WalkCost.MaxUs,
                renderer.DrawCost.AvgUs, renderer.DrawCost.MaxUs,
                renderer.PruneCost.AvgUs, renderer.PruneCost.MaxUs,
                renderer.ReadinessCost.AvgUs, renderer.ReadinessCost.MaxUs);

            Mod.Logger.Notification(
                "  render p95/p99/max us: prune {0:0}/{1:0}/{2:0} | schedule {3:0}/{4:0}/{5:0} | "
                + "upload {6:0}/{7:0}/{8:0} | evict {9:0}/{10:0}/{11:0} | seasonal {12:0}/{13:0}/{14:0} | "
                + "far {15:0}/{16:0}/{17:0} | readiness {24:0}/{25:0}/{26:0} | "
                + "walk {18:0}/{19:0}/{20:0} | draw {21:0}/{22:0}/{23:0}",
                renderer.PruneCost.P95Us, renderer.PruneCost.P99Us, renderer.PruneCost.MaxUs,
                renderer.ScheduleCost.P95Us, renderer.ScheduleCost.P99Us, renderer.ScheduleCost.MaxUs,
                renderer.UploadCost.P95Us, renderer.UploadCost.P99Us, renderer.UploadCost.MaxUs,
                renderer.EvictCost.P95Us, renderer.EvictCost.P99Us, renderer.EvictCost.MaxUs,
                renderer.SeasonalCost.P95Us, renderer.SeasonalCost.P99Us, renderer.SeasonalCost.MaxUs,
                renderer.FarDistanceCost.P95Us, renderer.FarDistanceCost.P99Us, renderer.FarDistanceCost.MaxUs,
                renderer.WalkCost.P95Us, renderer.WalkCost.P99Us, renderer.WalkCost.MaxUs,
                renderer.DrawCost.P95Us, renderer.DrawCost.P99Us, renderer.DrawCost.MaxUs,
                renderer.ReadinessCost.P95Us, renderer.ReadinessCost.P99Us, renderer.ReadinessCost.MaxUs);

            // The one line that can see the reported micro-hitches at all. Everything
            // above it measures our own phases; this measures the frame the player
            // actually experiences, and how much of it was us.
            Mod.Logger.Notification("  frame timeline: {0}", renderer.FrameTimeline.Describe());

            // How tall sections actually are, and what that already buys. The distribution
            // is the evidence Phase 4 of the GPU plan has to be argued from: a pyramid can
            // only reject a section that does not span the world, and until this line
            // nobody knew whether an ordinary section does.
            Mod.Logger.Notification(
                "  section heights: {0} | vertical cull: {1} section-draws skipped this interval "
                + "that a full-height box would have kept, worst frame {2}",
                renderer.SectionHeights.Describe(renderer.WorldHeight),
                renderer.VerticalCulledSections,
                renderer.VerticalCulledMax);

            // The same question one level up the quadtree. A section-draw skipped is one
            // draw; a SUBTREE skipped is a walk that never happened, so this line counts
            // the meshes inside the rejected subtrees rather than the nodes alone - the
            // nodes are what stopped being visited, the meshes are what stopped being
            // considered. Zero here with a healthy count above means the aggregate is
            // correct and simply never tight enough to reject, which is a real answer.
            Mod.Logger.Notification(
                "  subtree cull: {0} nodes rejected this interval that a full-height box "
                + "would have traversed, holding {1} resident meshes, worst frame {2} | "
                + "{3} nodes carry an aggregate",
                renderer.SubtreeCulledNodes,
                renderer.SubtreeCulledMeshes,
                renderer.SubtreeCulledMax,
                renderer.SubtreeBoundsNodes);

            // Whether depth verdicts actually stopped anything being drawn. In the log and
            // not only in chat, because chat cannot be copied out of the game and this is
            // the line that says whether a playtest was testing the thing it was meant to.
            // Unconditional: the case worth catching is the switch being on while nothing
            // ran, and a line that only appears when culling works cannot show that.
            Mod.Logger.Notification("  cull: {0}", renderer.DescribeGpuCull());

            // The pyramid's own cost, apart from everything else. Phase 4 lives or dies on
            // whether the GPU figure here is smaller than the drawing a depth test could
            // remove, and CPU time cannot see it because none of the work is on the CPU.
            if (renderer.HzbCost.Calls > 0)
            {
                Mod.Logger.Notification(
                    "  hzb: {0} | cpu {1:0.0}us avg / {2:0.0}us max, p95 {3:0}/p99 {4:0} | "
                    + "gpu {5:0.0}us avg / {6:0.0}us max over {7} timed builds | "
                    + "mid-frame gpu {8:0.0}us avg / {9:0.0}us max over {10} builds | "
                    + "classify gpu {11:0.0}us avg / {12:0.0}us max over {13} dispatches",
                    renderer.DescribeDepthPyramid(),
                    renderer.HzbCost.AvgUs, renderer.HzbCost.MaxUs,
                    renderer.HzbCost.P95Us, renderer.HzbCost.P99Us,
                    renderer.GpuHzbCost.AvgUs, renderer.GpuHzbCost.MaxUs,
                    renderer.GpuHzbCost.Calls,
                    renderer.GpuSplitHzbCost.AvgUs, renderer.GpuSplitHzbCost.MaxUs,
                    renderer.GpuSplitHzbCost.Calls,
                    renderer.GpuClassifyCost.AvgUs, renderer.GpuClassifyCost.MaxUs,
                    renderer.GpuClassifyCost.Calls);

                // Per distance band, because the case for this phase is that the amount of
                // hidden terrain scales with draw distance. One overall average mixes 500
                // blocks with 32,000 and cannot show that either way.
                string byDistance = renderer.DescribeDepthPyramidByDistance();
                if (byDistance.Length > 0)
                    Mod.Logger.Notification("  hzb hidden by distance: {0}", byDistance);

                string subdivision = renderer.DescribeDepthPyramidSubdivision();
                if (subdivision.Length > 0)
                    Mod.Logger.Notification("  hzb headroom: {0}", subdivision);

                // The correctness gate. The left-hand figure must be zero: it counts
                // sections the pyramid called hidden that a query had actually seen pixels
                // of, and every one of those is terrain a player could see. Warning rather
                // than notification when it is not zero, so it cannot scroll past.
                if (renderer.HzbVerdictsChecked > 0)
                {
                    string line = "  hzb against occlusion queries: {0} checked | "
                        + "{1} called hidden that a query SAW (must be zero) | "
                        + "{2} the query hid and the pyramid did not";
                    if (renderer.HzbHiddenButQuerySawIt > 0)
                    {
                        Mod.Logger.Warning(line, renderer.HzbVerdictsChecked,
                            renderer.HzbHiddenButQuerySawIt, renderer.HzbMissedWhatQueryHid);
                    }
                    else
                    {
                        Mod.Logger.Notification(line, renderer.HzbVerdictsChecked,
                            renderer.HzbHiddenButQuerySawIt, renderer.HzbMissedWhatQueryHid);
                    }
                }
            }

            Mod.Logger.Notification("  vanilla readiness: {0}", renderer.DescribeReadiness());

            // Why a coarser parent is covering ground its children could cover. Each cause
            // wants a different fix, so they are reported apart rather than as one number.
            if (renderer.CoarseWaitingLoad + renderer.CoarseWaitingMesh
                + renderer.CoarseWaitingSchedule + renderer.CoarseWaitingOther > 0)
            {
                Mod.Logger.Notification(
                    "  coarse cover waits: {0} on storage, {1} on a mesh worker, {2} on a schedule slot, "
                    + "{3} with nothing pending | backlog {4} render-dirty, {5} meshes queued, {6} loads in flight",
                    renderer.CoarseWaitingLoad, renderer.CoarseWaitingMesh,
                    renderer.CoarseWaitingSchedule, renderer.CoarseWaitingOther,
                    pipeline.World.RenderDirty.Count, pipeline.Worker.PendingMeshes,
                    pipeline.World.LoadsInFlight.Count);
            }

            Mod.Logger.Notification(
                "  render interval: {0} projection resets, {1:0.00} MiB uploaded; phase hitches >=25/50/100ms: {2}/{3}/{4}",
                renderer.ProjectionResetCount, renderer.MeshUploadBytes / (1024.0 * 1024.0),
                renderer.PruneCost.Over25Ms + renderer.ScheduleCost.Over25Ms + renderer.UploadCost.Over25Ms
                    + renderer.EvictCost.Over25Ms + renderer.SeasonalCost.Over25Ms + renderer.FarDistanceCost.Over25Ms
                    + renderer.ReadinessCost.Over25Ms + renderer.WalkCost.Over25Ms + renderer.DrawCost.Over25Ms,
                renderer.PruneCost.Over50Ms + renderer.ScheduleCost.Over50Ms + renderer.UploadCost.Over50Ms
                    + renderer.EvictCost.Over50Ms + renderer.SeasonalCost.Over50Ms + renderer.FarDistanceCost.Over50Ms
                    + renderer.ReadinessCost.Over50Ms + renderer.WalkCost.Over50Ms + renderer.DrawCost.Over50Ms,
                renderer.PruneCost.Over100Ms + renderer.ScheduleCost.Over100Ms + renderer.UploadCost.Over100Ms
                    + renderer.EvictCost.Over100Ms + renderer.SeasonalCost.Over100Ms + renderer.FarDistanceCost.Over100Ms
                    + renderer.ReadinessCost.Over100Ms + renderer.WalkCost.Over100Ms + renderer.DrawCost.Over100Ms);

            Mod.Logger.Notification(
                "  render budgets: snapshots {0} items/{1:0.00} MiB, {2} queued/{3:0.00} MiB, oldest {4}ms | "
                + "uploads {5} items/{6:0.00} MiB, {7} queued/{8:0.00} MiB, oldest {9}ms | "
                + "{10} stale results rejected",
                renderer.MeshSnapshotItems, renderer.MeshSnapshotBytes / (1024.0 * 1024.0),
                worker.PendingMeshes, worker.PendingMeshBytes / (1024.0 * 1024.0), worker.OldestMeshAgeMs,
                renderer.MeshUploadItems, renderer.MeshUploadBytes / (1024.0 * 1024.0),
                worker.PendingMeshResults, worker.PendingMeshResultBytes / (1024.0 * 1024.0),
                worker.OldestMeshResultAgeMs, renderer.StaleMeshResultsRejected);

            Mod.Logger.Notification(
                "  render gpu calls p95/p99/max us: upload {0:0}/{1:0}/{2:0} | dispose {3:0}/{4:0}/{5:0}",
                renderer.GlUploadCost.P95Us, renderer.GlUploadCost.P99Us, renderer.GlUploadCost.MaxUs,
                renderer.MeshDisposeCost.P95Us, renderer.MeshDisposeCost.P99Us, renderer.MeshDisposeCost.MaxUs);

            Mod.Logger.Notification(
                "  render draw interval: opaque {0} calls, {1} vertices/{2} indices | "
                + "water {3} calls, {4} vertices/{5} indices | live geometry {6:0.00} MiB, "
                + "opaque {7}/{8}, water {9}/{10}",
                renderer.OpaqueDrawCalls, renderer.OpaqueDrawVertices, renderer.OpaqueDrawIndices,
                renderer.WaterDrawCalls, renderer.WaterDrawVertices, renderer.WaterDrawIndices,
                renderer.LiveGpuMeshBytes / (1024.0 * 1024.0),
                renderer.LiveOpaqueVertices, renderer.LiveOpaqueIndices,
                renderer.LiveWaterVertices, renderer.LiveWaterIndices);

            string? arena = renderer.DescribeGpuArena();
            if (arena != null)
            {
                Mod.Logger.Notification("  gpu arena shadow: {0}", arena);
                Mod.Logger.Notification("  packed opaque: {0}", renderer.DescribePackedDraw());
                Mod.Logger.Notification("  packed clusters: {0}", renderer.DescribeClusterDraw());
                // Phase 8 may emit several commands per section, so both counts are named.
                // Coverage stays section-based: a missing clustered section falls back as
                // one complete legacy draw rather than being flattered by command count.
                Mod.Logger.Notification(
                    "  gpu indirect shadow: last frame {0} commands for {1} of {2} drawn "
                    + "sections would be {3} multi-draw batches, coverage {4:P0}, {5} spans stale",
                    renderer.ShadowIndirectCommands,
                    renderer.ShadowIndirectSections,
                    renderer.ShadowIndirectSections + renderer.ShadowIndirectMissing,
                    renderer.ShadowIndirectBatches,
                    renderer.ShadowIndirectCoverage,
                    renderer.ShadowIndirectDropped);
            }

            if (renderer.GpuTimingRequested)
            {
                LodPhaseCost opaqueGpu = renderer.GpuOpaqueCost;
                LodPhaseCost splitNearGpu = renderer.GpuSplitNearCost;
                LodPhaseCost splitFarGpu = renderer.GpuSplitFarCost;
                LodPhaseCost waterGpu = renderer.GpuWaterCost;
                Mod.Logger.Notification(
                    "  delayed GPU pass p95/p99/max us: opaque {0:0}/{1:0}/{2:0} over {3} samples | "
                    + "split near {4:0}/{5:0}/{6:0} over {7} | split far {8:0}/{9:0}/{10:0} over {11} | "
                    + "water {12:0}/{13:0}/{14:0} over {15}; {16} pending, {17} ring-full skips, "
                    + "{18} time-query target conflicts, timing {19}",
                    opaqueGpu.P95Us, opaqueGpu.P99Us, opaqueGpu.MaxUs, opaqueGpu.Calls,
                    splitNearGpu.P95Us, splitNearGpu.P99Us, splitNearGpu.MaxUs, splitNearGpu.Calls,
                    splitFarGpu.P95Us, splitFarGpu.P99Us, splitFarGpu.MaxUs, splitFarGpu.Calls,
                    waterGpu.P95Us, waterGpu.P99Us, waterGpu.MaxUs, waterGpu.Calls,
                    renderer.GpuTimerPendingResults, renderer.GpuTimerUnavailableSlots,
                    renderer.GpuTimerTargetBusy, renderer.GpuTimingActive ? "active" : "inactive");

                LodPhaseCost ordinaryCullGpu = renderer.GpuOrdinaryCullCost;
                LodPhaseCost splitNearCullGpu = renderer.GpuSplitNearCullCost;
                LodPhaseCost splitFarCullGpu = renderer.GpuSplitFarCullCost;
                Mod.Logger.Notification(
                    "  delayed GPU cull p95/p99/max us: ordinary {0:0}/{1:0}/{2:0} over {3} samples | "
                    + "split near {4:0}/{5:0}/{6:0} over {7} | "
                    + "split far {8:0}/{9:0}/{10:0} over {11}",
                    ordinaryCullGpu.P95Us, ordinaryCullGpu.P99Us, ordinaryCullGpu.MaxUs,
                    ordinaryCullGpu.Calls,
                    splitNearCullGpu.P95Us, splitNearCullGpu.P99Us, splitNearCullGpu.MaxUs,
                    splitNearCullGpu.Calls,
                    splitFarCullGpu.P95Us, splitFarCullGpu.P99Us, splitFarCullGpu.MaxUs,
                    splitFarCullGpu.Calls);

                LodPhaseCost taaGpu = renderer.GpuTaaCost;
                LodPhaseCost taaCopyGpu = renderer.GpuTaaCopyCost;
                Mod.Logger.Notification(
                    "  delayed GPU distant anti-aliasing p95/p99/max us: resolve {0:0}/{1:0}/{2:0} "
                    + "over {3} samples | depth copy {4:0}/{5:0}/{6:0} over {7}",
                    taaGpu.P95Us, taaGpu.P99Us, taaGpu.MaxUs, taaGpu.Calls,
                    taaCopyGpu.P95Us, taaCopyGpu.P99Us, taaCopyGpu.MaxUs, taaCopyGpu.Calls);
            }

            // Collections since the last report, beside the phase maxima, because the
            // two are related and the relationship is easy to get backwards. A phase
            // maximum is not a measurement of that phase: the far-distance scan averages
            // 2us over a few hundred meshes and has reported a 1624us maximum, which
            // nothing inside a loop that multiplies and compares can account for. What a
            // maximum records is whatever interrupted the frame, charged to whichever
            // phase was running. These counters say how much of that was collection.
            Mod.Logger.Notification(
                "  gc since last report: {0} gen0, {1} gen1, {2} gen2, {3} MB managed",
                GC.CollectionCount(0) - gen0AtLastReport,
                GC.CollectionCount(1) - gen1AtLastReport,
                GC.CollectionCount(2) - gen2AtLastReport,
                GC.GetTotalMemory(false) / (1024 * 1024));

            gen0AtLastReport = GC.CollectionCount(0);
            gen1AtLastReport = GC.CollectionCount(1);
            gen2AtLastReport = GC.CollectionCount(2);
        }

        if (totalTickCost.Calls > 0)
        {
            Mod.Logger.Notification(
                "  game tick p95/p99/max us over {0} ticks: total {1:0}/{2:0}/{3:0} | assist {4:0}/{5:0}/{6:0} | "
                + "local offers {7:0}/{8:0}/{9:0} | pipeline {10:0}/{11:0}/{12:0} | resident evict {13:0}/{14:0}/{15:0}; "
                + "total hitches >=25/50/100ms: {16}/{17}/{18}",
                totalTickCost.Calls,
                totalTickCost.P95Us, totalTickCost.P99Us, totalTickCost.MaxUs,
                assistTickCost.P95Us, assistTickCost.P99Us, assistTickCost.MaxUs,
                localOfferTickCost.P95Us, localOfferTickCost.P99Us, localOfferTickCost.MaxUs,
                pipelineTickCost.P95Us, pipelineTickCost.P99Us, pipelineTickCost.MaxUs,
                residentEvictTickCost.P95Us, residentEvictTickCost.P99Us, residentEvictTickCost.MaxUs,
                totalTickCost.Over25Ms, totalTickCost.Over50Ms, totalTickCost.Over100Ms);

            Mod.Logger.Notification(
                "  pipeline p95/p99/max us: foreign install {0:0}/{1:0}/{2:0} | load install {3:0}/{4:0}/{5:0} | "
                + "capture schedule {6:0}/{7:0}/{8:0} | capture apply {9:0}/{10:0}/{11:0} | "
                + "mip apply {12:0}/{13:0}/{14:0} | mip schedule {15:0}/{16:0}/{17:0} | "
                + "save snapshot {18:0}/{19:0}/{20:0}",
                pipeline.ForeignInstallCost.P95Us, pipeline.ForeignInstallCost.P99Us, pipeline.ForeignInstallCost.MaxUs,
                pipeline.LoadInstallCost.P95Us, pipeline.LoadInstallCost.P99Us, pipeline.LoadInstallCost.MaxUs,
                pipeline.CaptureScheduleCost.P95Us, pipeline.CaptureScheduleCost.P99Us, pipeline.CaptureScheduleCost.MaxUs,
                pipeline.CaptureApplyCost.P95Us, pipeline.CaptureApplyCost.P99Us, pipeline.CaptureApplyCost.MaxUs,
                pipeline.MipApplyCost.P95Us, pipeline.MipApplyCost.P99Us, pipeline.MipApplyCost.MaxUs,
                pipeline.MipScheduleCost.P95Us, pipeline.MipScheduleCost.P99Us, pipeline.MipScheduleCost.MaxUs,
                pipeline.SaveSnapshotCost.P95Us, pipeline.SaveSnapshotCost.P99Us, pipeline.SaveSnapshotCost.MaxUs);

            Mod.Logger.Notification(
                "  capture publish budget: {0} items/{1:0.00} MiB, {2} queued/{3:0.00} MiB, oldest {4}ms",
                pipeline.CaptureApplyItems, pipeline.CaptureApplyBytes / (1024.0 * 1024.0),
                pipeline.PendingCaptureResults, pipeline.PendingCaptureResultBytes / (1024.0 * 1024.0),
                pipeline.OldestCaptureResultAgeMs);

            Mod.Logger.Notification(
                "  install budgets: assist input {0} items/{1:0.00} MiB, {2} queued/{3:0.00} MiB, oldest {4}ms | "
                + "local input {5} items/{6:0.00} MiB | foreign publish {7} items/{8:0.00} MiB, "
                + "{9} queued/{10:0.00} MiB, oldest {11}ms | background {12} items/{13:0.00} MiB, "
                + "{14} queued/{15:0.00} MiB, oldest {16}ms",
                assist?.ArrivalItemsProcessed ?? 0,
                (assist?.ArrivalBytesProcessed ?? 0) / (1024.0 * 1024.0),
                assist?.PendingArrivals ?? 0,
                (assist?.PendingArrivalBytes ?? 0) / (1024.0 * 1024.0),
                assist?.OldestArrivalAgeMs ?? 0,
                localOfferItemsProcessed, localOfferBytesProcessed / (1024.0 * 1024.0),
                pipeline.ForeignInstallItems, pipeline.ForeignInstallBytes / (1024.0 * 1024.0),
                pipeline.PendingForeignResults, pipeline.PendingForeignResultBytes / (1024.0 * 1024.0),
                pipeline.OldestForeignResultAgeMs,
                pipeline.LoadInstallItems, pipeline.LoadInstallBytes / (1024.0 * 1024.0),
                pipeline.PendingLoadResults, pipeline.PendingLoadResultBytes / (1024.0 * 1024.0),
                pipeline.OldestLoadResultAgeMs);

            if (allocationTelemetryEnabled) LogAllocationStats();
        }

        if (storageThread?.FirstSaveError != null && !loggedFirstSaveError)
        {
            loggedFirstSaveError = true;
            Mod.Logger.Warning("First storage-write error was: {0}", storageThread.FirstSaveError);
        }
        pipeline.ResetStorageStats();
        pipeline.ResetPhaseCosts();
        assist?.ResetPumpStats();
        localOfferItemsProcessed = 0;
        localOfferBytesProcessed = 0;
        totalTickCost.Reset();
        assistTickCost.Reset();
        localOfferTickCost.Reset();
        pipelineTickCost.Reset();
        residentEvictTickCost.Reset();
        renderer?.ResetPhaseCosts();
    }

    void LogAllocationStats()
    {
        static double MiB(long bytes) => bytes / (1024.0 * 1024.0);
        static double KiB(long bytes) => bytes / 1024.0;

        Mod.Logger.Notification(
            "  tick allocation interval MiB/max KiB: total {0:0.00}/{1:0.0} | assist {2:0.00}/{3:0.0} | "
            + "local {4:0.00}/{5:0.0} | pipeline {6:0.00}/{7:0.0} | evict {8:0.00}/{9:0.0}",
            MiB(totalTickCost.AllocatedBytes), KiB(totalTickCost.MaxAllocatedBytes),
            MiB(assistTickCost.AllocatedBytes), KiB(assistTickCost.MaxAllocatedBytes),
            MiB(localOfferTickCost.AllocatedBytes), KiB(localOfferTickCost.MaxAllocatedBytes),
            MiB(pipelineTickCost.AllocatedBytes), KiB(pipelineTickCost.MaxAllocatedBytes),
            MiB(residentEvictTickCost.AllocatedBytes), KiB(residentEvictTickCost.MaxAllocatedBytes));

        Mod.Logger.Notification(
            "  pipeline allocation interval MiB/max KiB: foreign {0:0.00}/{1:0.0} | load {2:0.00}/{3:0.0} | "
            + "capture schedule {4:0.00}/{5:0.0} | capture apply {6:0.00}/{7:0.0} | "
            + "mip apply {8:0.00}/{9:0.0} | mip schedule {10:0.00}/{11:0.0} | save {12:0.00}/{13:0.0}",
            MiB(pipeline.ForeignInstallCost.AllocatedBytes), KiB(pipeline.ForeignInstallCost.MaxAllocatedBytes),
            MiB(pipeline.LoadInstallCost.AllocatedBytes), KiB(pipeline.LoadInstallCost.MaxAllocatedBytes),
            MiB(pipeline.CaptureScheduleCost.AllocatedBytes), KiB(pipeline.CaptureScheduleCost.MaxAllocatedBytes),
            MiB(pipeline.CaptureApplyCost.AllocatedBytes), KiB(pipeline.CaptureApplyCost.MaxAllocatedBytes),
            MiB(pipeline.MipApplyCost.AllocatedBytes), KiB(pipeline.MipApplyCost.MaxAllocatedBytes),
            MiB(pipeline.MipScheduleCost.AllocatedBytes), KiB(pipeline.MipScheduleCost.MaxAllocatedBytes),
            MiB(pipeline.SaveSnapshotCost.AllocatedBytes), KiB(pipeline.SaveSnapshotCost.MaxAllocatedBytes));

        if (renderer == null) return;
        Mod.Logger.Notification(
            "  render allocation interval MiB/max KiB: prune {0:0.00}/{1:0.0} | schedule {2:0.00}/{3:0.0} | "
            + "upload {4:0.00}/{5:0.0} | evict {6:0.00}/{7:0.0} | seasonal {8:0.00}/{9:0.0} | "
            + "far {10:0.00}/{11:0.0} | readiness {16:0.00}/{17:0.0} | "
            + "walk {12:0.00}/{13:0.0} | draw {14:0.00}/{15:0.0}",
            MiB(renderer.PruneCost.AllocatedBytes), KiB(renderer.PruneCost.MaxAllocatedBytes),
            MiB(renderer.ScheduleCost.AllocatedBytes), KiB(renderer.ScheduleCost.MaxAllocatedBytes),
            MiB(renderer.UploadCost.AllocatedBytes), KiB(renderer.UploadCost.MaxAllocatedBytes),
            MiB(renderer.EvictCost.AllocatedBytes), KiB(renderer.EvictCost.MaxAllocatedBytes),
            MiB(renderer.SeasonalCost.AllocatedBytes), KiB(renderer.SeasonalCost.MaxAllocatedBytes),
            MiB(renderer.FarDistanceCost.AllocatedBytes), KiB(renderer.FarDistanceCost.MaxAllocatedBytes),
            MiB(renderer.WalkCost.AllocatedBytes), KiB(renderer.WalkCost.MaxAllocatedBytes),
            MiB(renderer.DrawCost.AllocatedBytes), KiB(renderer.DrawCost.MaxAllocatedBytes),
            MiB(renderer.ReadinessCost.AllocatedBytes), KiB(renderer.ReadinessCost.MaxAllocatedBytes));
    }

    void OnLeaveWorld()
    {
        hitches?.Flush("leaving the world");
        settingsIntegration?.CancelActivePage();
        configDialog?.TryClose();
        assist?.Reset();
        ResetSilhouettes();
        // Belongs to the world being left: the next one is a different savegame with a
        // different sibling cache, and holding this open would keep a file handle on a
        // database the server side may want to delete or replace.
        localOffers?.Dispose();
        localOffers = null;
        loggedLocalOffers = false;
        localOfferProbeTicks = 0;
        localOfferKeysDiscovered = 0;
        localOfferRowsScanned = 0;
        localOfferFillsQueued = 0;
        fillMergesAtLastLog = 0;
        localOfferRetryableMisses = 0;
        localOfferSectionsAccepted = 0;
        localOfferSectionsInstalled = 0;
        // The server-sync record's last changes go to the same worker before Close drains it.
        if (serverSync.TakeWrites() is { } syncWrites) pipeline.StorageThread?.SaveAssistSync(syncWrites);
        serverSync.Reset();
        // Order matters and is stated here because only this method knows it: the flush hands
        // patches to the storage worker, and Close disposes that worker. Afterwards the
        // reference is dropped, because the next world's Open builds a new one and a stale
        // handle would be written to.
        renderer.SaveClimateMemory();
        pipeline.Close();
        renderer.ClimateStorage = null;
        renderer.ClearMeshes();
        renderer.ResetBlockTextureStateForWorld();
        blockTextures.Clear();
        textureMeans.Clear();
    }

    /// <summary>
    /// Writes a chat report into the log as well, one line at a time so each carries its
    /// own timestamp and stays greppable. Game chat cannot be selected or copied, so
    /// without this every diagnostic has to be read off the screen and retyped.
    /// </summary>
    /// <summary>
    /// Writes a report to the client log and hands the same text back for chat.
    ///
    /// <para>
    /// The status line is thousands of characters and the chat window shows the tail of it,
    /// which cannot be scrolled back or copied out. Until now the only complete copy reached
    /// the log ONCE, thirty seconds after joining - so every question about steady-state
    /// behaviour was answered with a snapshot taken while the world was still streaming in,
    /// including the one that produced this method (G72).
    /// </para>
    /// </summary>
    string LogAndReturn(string label, string report)
    {
        LogReportLines(label, report);
        return report;
    }

    void LogReportLines(string label, string report)
    {
        foreach (string line in report.Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            Mod.Logger.Notification("  {0}: {1}", label, line.Trim());
        }
    }

    VintageHorizonsConfigDialog CreateStandaloneConfigDialog() =>
        BuildConfigDialog(null, null);

    bool OpenStandaloneConfigDialog()
    {
        if (configDialog?.IsOpened() == true)
        {
            configDialog.Focus();
            return true;
        }

        configDialog?.Dispose();
        configDialog = CreateStandaloneConfigDialog();
        return configDialog.TryOpen();
    }

    VintageHorizonsConfigDialog CreateEmbeddedConfigDialog(
        Action requestRecompose, Action requestClose) =>
        BuildConfigDialog(requestRecompose, requestClose);

    /// <summary>
    /// One editor-state factory for both entry points. Keeping the snapshot and save callback
    /// here prevents the Settings page from becoming a subtly different configuration system
    /// from .dlconfig.
    /// </summary>
    VintageHorizonsConfigDialog BuildConfigDialog(
        Action? requestRecompose, Action? requestClose)
    {
        // The player's own distances, which automatic mode leaves untouched in the file,
        // and what automatic mode would use right now, which the scale shows while it is on.
        bool automatic = renderer.AutoLodThresholds;
        int[] thresholds = !automatic
            ? LodWorld.GetLevelThresholds()
            : LodWorld.NormalizeLevelThresholds(config.LodThresholds, config.DetailDistance);
        int[] automaticThresholds = renderer.PreviewAutoThresholds();
        int farCap = renderer.FarViewDistanceCap;

        // The Anti-Aliasing tab shows each slider's effect at once and puts back what was
        // in force if the editor closes without Save.
        var taaTuning = new TaaTuning(
            renderer.TaaDials,
            renderer.TaaShowWeight,
            PreviewTaaTuning,
            () => renderer.ScreenBlockScale);
        var silhouetteTuning = new SilhouetteTuning(
            CurrentSilhouetteSettings(), PreviewSilhouetteSettings, DescribeSilhouettes);
        var vanilla = new VanillaDistanceTuning(
            Math.Max(1, ClientSettings.ViewDistance), ApplyVanillaViewDistance);
        bool loadWhilePaused = renderer.ContinueLoadingWhilePaused;
        Action<LodWorldGenPreferences>? openLiveWorldGen = requestClose == null
            ? null
            : value =>
            {
                SaveWorldGenPreferences(value);
                requestClose();
                capi.Event.EnqueueMainThreadTask(() =>
                {
                    if (!OpenStandaloneConfigDialog())
                        capi.ShowChatMessage(
                            "[Landscapes] The live configuration window could not open; use .dlconfig.");
                }, "dodenslandscapes-open-live-worldgen");
            };
        var worldGen = new WorldGenTuning(
            ConfiguredWorldGenPreferences(), assist, SaveWorldGenPreferences,
            openLiveWorldGen);
        var collector = new CollectorTuning(
            config.AllCoresGarbageCollection, () => GameCollectorSetting.InEffect,
            SaveAllCoresGarbageCollection);
        var advanced = new AdvancedTuning(config.ShowAdvancedSettings, on =>
        {
            config.ShowAdvancedSettings = on;
            SaveConfig();
        });

        return requestRecompose != null && requestClose != null
            ? new VintageHorizonsConfigDialog(
                capi, thresholds, automaticThresholds, automatic, farCap,
                loadWhilePaused, CurrentVisualSettings(), vanilla, taaTuning, silhouetteTuning, worldGen,
                collector, advanced, requestRecompose, requestClose, ApplyGuiConfig)
            : new VintageHorizonsConfigDialog(
                capi, thresholds, automaticThresholds, automatic, farCap,
                loadWhilePaused, CurrentVisualSettings(), vanilla, taaTuning, silhouetteTuning, worldGen,
                collector, advanced, ApplyGuiConfig);
    }

    LodWorldGenPreferences ConfiguredWorldGenPreferences()
    {
        EnumWorldGenShape shape = Enum.TryParse(config.WorldGenShape, true,
            out EnumWorldGenShape parsedShape) ? parsedShape : LodWorldGenPlan.DefaultShape;
        EnumWorldGenCentreMode centre = Enum.TryParse(config.WorldGenCentre, true,
            out EnumWorldGenCentreMode parsedCentre) ? parsedCentre : EnumWorldGenCentreMode.Here;
        int maxRadius = assist?.ServerSettings?.WorldGenMaxRadiusChunks ?? 256;
        return new LodWorldGenPreferences(config.WorldGenRadiusChunks, shape, centre,
            config.WorldGenCentreX, config.WorldGenCentreZ, "Balanced").Normalize(maxRadius);
    }

    /// <summary>
    /// Keeps the game's start-up file in step with the all-cores collector setting and says in
    /// the log what this session runs. A game update replaces the file, so a setting that is on
    /// is set again here; like any change to it, that applies from the next start.
    /// </summary>
    void SyncGameCollector()
    {
        string? path = GameCollectorSetting.RuntimeConfigPath();
        string file;
        if (path == null)
        {
            file = "no start-up file was found beside the game";
        }
        else
        {
            GameCollectorSetting.FileState state;
            try
            {
                state = GameCollectorSetting.ReadState(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                state = GameCollectorSetting.FileState.Unavailable;
                Mod.Logger.Warning("[Landscapes] The game's start-up file could not be read: {0}", e.Message);
            }
            bool fileOn = state is GameCollectorSetting.FileState.OnByThisMod
                or GameCollectorSetting.FileState.OnElsewhere;
            if (config.AllCoresGarbageCollection && !fileOn && state != GameCollectorSetting.FileState.Unavailable)
            {
                GameCollectorSetting.Apply(path, on: true, out string message);
                file = message + " (the start-up file had lost it, as a game update does)";
            }
            else if (!config.AllCoresGarbageCollection && state == GameCollectorSetting.FileState.OnByThisMod)
            {
                GameCollectorSetting.Apply(path, on: false, out string message);
                file = message;
            }
            else
            {
                file = "start-up file " + state switch
                {
                    GameCollectorSetting.FileState.OnByThisMod => "set by Doden's Landscapes",
                    GameCollectorSetting.FileState.OnElsewhere => "set outside Doden's Landscapes",
                    GameCollectorSetting.FileState.Off => "at the game's default",
                    _ => "unreadable",
                };
            }
        }
        Mod.Logger.Notification(
            "[Landscapes] All-cores garbage collection: {0} in this mod's settings, {1} in this session; {2}.",
            config.AllCoresGarbageCollection ? "on" : "off",
            GameCollectorSetting.InEffect ? "running" : "not running", file);
    }

    /// <summary>
    /// The Performance tab's Save: edits the game's start-up file and says what happened, in
    /// chat and in the log. A file that could not be changed leaves the setting as it was.
    /// </summary>
    string SaveAllCoresGarbageCollection(bool on)
    {
        if (on == config.AllCoresGarbageCollection) return "";
        string? path = GameCollectorSetting.RuntimeConfigPath();
        string message;
        bool changed;
        if (path == null)
        {
            message = "no start-up file was found beside the game, so nothing changed";
            changed = false;
        }
        else
        {
            changed = GameCollectorSetting.Apply(path, on, out message);
        }
        if (changed) config.AllCoresGarbageCollection = on;
        Mod.Logger.Notification("[Landscapes] {0}", message);
        capi.ShowChatMessage("[Landscapes] " + char.ToUpperInvariant(message[0]) + message[1..] + ".");
        return message;
    }

    void SaveWorldGenPreferences(LodWorldGenPreferences value)
    {
        config.WorldGenRadiusChunks = value.RadiusChunks;
        config.WorldGenShape = value.Shape.ToString().ToLowerInvariant();
        config.WorldGenCentre = value.CentreMode.ToString().ToLowerInvariant();
        config.WorldGenCentreX = value.X;
        config.WorldGenCentreZ = value.Z;
    }

    /// <summary>
    /// Mirror the vanilla Graphics slider's exact setting and Custom-preset side effect. Its own
    /// control commits only on mouse-up too; returning a new automatic sequence keeps the map in
    /// step with the new vanilla edge immediately.
    /// </summary>
    int[] ApplyVanillaViewDistance(int blocks)
    {
        int value = LodThresholdScaleLayout.SnapVanillaDistance(blocks);
        ClientSettings.ViewDistance = value;
        ClientSettings.GraphicsPresetId = GraphicsPreset.Custom.PresetId;
        return renderer.PreviewAutoThresholds();
    }

    void RegisterCommands()
    {
        // Every answer also goes to the client log (G72): the owner cannot copy game chat.
        capi.ChatCommands.Create("dlhitch")
            .WithDescription("Hitch recorder: once a minute it writes the frame-time lows and what was "
                + "unusually slow in the slowest frames to the client log. on | off; with nothing, "
                + "writes the current minute now.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("onoff"))
            .HandleWith(args =>
            {
                if (hitches == null)
                    return TextCommandResult.Success("[Landscapes] no hitch recorder: the distant-terrain renderer did not start.");
                string word = args.Parsers[0].IsMissing ? "" : (string)args[0];
                string reply = word.Equals("on", StringComparison.OrdinalIgnoreCase)
                    ? hitches.SetEnabled(true)
                    : word.Equals("off", StringComparison.OrdinalIgnoreCase)
                        ? hitches.SetEnabled(false)
                        : hitches.ReportNow();
                Mod.Logger.Notification("[Landscapes] .dlhitch {0}: {1}", word, reply);
                return TextCommandResult.Success("[Landscapes] " + reply);
            });

        capi.ChatCommands.Create("dldiag")
            .WithDescription("Show or hide the Doden's Landscapes diagnostics window for this session. Default off.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (args.Parsers[0].IsMissing)
                {
                    return TextCommandResult.Success(
                        $"[Landscapes] diagnostics window: {(diagnosticsEnabled ? "on" : "off")}");
                }

                bool enabled = (bool)args[0];
                bool opened = SetDiagnosticsEnabled(enabled);
                return TextCommandResult.Success(!enabled
                    ? "[Landscapes] diagnostics window off."
                    : opened
                        ? "[Landscapes] diagnostics window on."
                        : "[Landscapes] diagnostics window is on but could not open right now.");
            });

        capi.ChatCommands.Create("dlinfo")
            .WithDescription("Doden's Landscapes status. Also written to the client log, which is the only "
                + "copy long enough to read.")
            .HandleWith(_ => TextCommandResult.Success(
                LogAndReturn("status",
                $"[Landscapes] sections: {pipeline.World.Sections.Count} [{pipeline.World.DescribeLevels()}] " +
                $"({pipeline.CachedSectionsLoaded} from cache), meshes: {renderer.MeshCount}, " +
                $"drawn: {renderer.LastDrawCount} [{renderer.DescribeDrawnLevels()}] " +
                $"({renderer.LastTraversalCulledCount} subtrees traversal-culled, " +
                $"{renderer.LastCulledCount} selected nodes draw-culled), " +
                $"columns captured: {pipeline.ColumnsCaptured}, pending: {pipeline.PendingColumns}, " +
                $"unloaded-before-capture drops: {pipeline.CaptureDropsUnloaded}, " +
                $"capture errors: {pipeline.CaptureErrors}, " +
                $"worker: {pipeline.Worker.PendingCaptures}c/{pipeline.Worker.PendingMeshes}m/" +
                $"{pipeline.Worker.PendingMeshResults}u/{pipeline.Worker.PendingMips}p, " +
                $"awaiting mip: {pipeline.World.MipDirty.Count} ({pipeline.World.MipInFlightCount} in flight), " +
                $"unsaved: {pipeline.World.SaveDirty.Count}, persistence: {(pipeline.Persisting ? "on" : "off")}, " +
                $"render distance: {(renderer.FarViewDistanceCap > 0 ? renderer.FarViewDistanceCap + " (capped)" : "unlimited")}, " +
                $"current far edge: {(int)renderer.EffectiveFarDistance}, " +
                $"radial demand: {renderer.DescribeRadialDemand()}, " +
                $"tints: {renderer.DescribeTintReadiness()}, " +
                $"LOD thresholds: {string.Join('/', LodWorld.GetLevelThresholds())} (.dlconfig to change), " +
                $"occlusion order: {renderer.DescribeOcclusionCulling()}, " +
                $"delayed occlusion: {renderer.DescribeTemporalOcclusion()}, " +
                $"readiness: {renderer.DescribeReadiness()}, " +
                $"server assist: {assist?.Status ?? "off"}" +
                (assist != null && assist.RemoteKeys.Count > 0
                    ? $", server offers {assist.RemoteKeys.Count} sections " +
                      $"({pipeline.RemoteOnly.Count} not held locally, {pipeline.ForeignSectionsInstalled} fetched, " +
                      $"{assist.InFlight} in flight, {assist.SectionsRefused} declined)" +
                      (assist.ManifestComplete ? "" : " (manifest still arriving)")
                    : "") +
                (localOffers != null || pipeline.PendingFills > 0 || pipeline.ForeignMerges > 0
                    ? $", held sections: {pipeline.DescribeFills()}; {DescribeSiblingScanner()}"
                    : "") +
                (serverSync.Active ? $", server sync: {serverSync.Describe()}" : ""))));

        capi.ChatCommands.Create("dlconfig")
            .WithDescription("Open the Doden's Landscapes settings window, optionally on one tab: "
                + "general, farseer, generate, performance, or antialiasing (with Advanced settings on)")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("tab"))
            .HandleWith(args =>
            {
                bool alreadyOpen = configDialog?.IsOpened() == true;
                if (!OpenStandaloneConfigDialog())
                    return TextCommandResult.Success("[Landscapes] configuration window could not open right now.");
                if (!args.Parsers[0].IsMissing)
                {
                    string name = (string)args[0];
                    if (VintageHorizonsConfigDialog.TabNamed(name) is not int tab)
                        return TextCommandResult.Error("[Landscapes] no tab called '" + name
                            + "': use general, farseer, generate, performance or antialiasing.");
                    if (!configDialog!.ShowTab(tab))
                        return TextCommandResult.Error(
                            "[Landscapes] the Anti-Aliasing tab shows with Advanced settings on.");
                }
                return TextCommandResult.Success(alreadyOpen
                    ? "[Landscapes] configuration window focused."
                    : "[Landscapes] configuration window opened.");
            });

        capi.ChatCommands.Create("dlblend")
            .WithDescription("Turn seam blending and L0 block textures off or on. Saved; also available in .dlconfig.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (args.Parsers[0].IsMissing)
                {
                    return TextCommandResult.Success("[Landscapes] " + renderer.DescribeBlendPipeline());
                }

                bool enabled = (bool)args[0];
                config.BlendPipeline = enabled;
                ApplyBlendPipeline(enabled);
                SaveConfig();
                return TextCommandResult.Success("[Landscapes] " + renderer.DescribeBlendPipeline()
                    + (!enabled ? " Live L0 terrain will finish changing over the next few seconds." : ""));
            });

        RegisterSilhouetteCommand();

        // Named apart from ".dlwhy" on purpose. This one answers "why is that coarse",
        // the other answers "why is that missing", and registering both under one name
        // threw at startup and cost every command declared after it.
        capi.ChatCommands.Create("dlcoarse")
            .WithDescription("Explain why nearby LOD terrain draws coarser than the detail setting allows")
            .HandleWith(_ =>
            {
                var at = capi.World.Player.Entity.Pos;
                return TextCommandResult.Success(
                    "[Landscapes] coarse draws:" + renderer.ExplainCoarseDraws(at.X, at.Z));
            });

        capi.ChatCommands.Create("dlfar")
            .WithDescription("Cap Doden's Landscapes render distance in blocks (0 = unlimited)")
            .WithArgs(capi.ChatCommands.Parsers.Int("blocks"))
            .HandleWith(args =>
            {
                int blocks = (int)args[0];
                renderer.FarViewDistanceCap = blocks <= 0 ? 0 : GameMath.Clamp(blocks, 1024, 262144);
                SaveConfig();
                return TextCommandResult.Success(renderer.FarViewDistanceCap > 0
                    ? $"[Landscapes] render distance capped at {renderer.FarViewDistanceCap} (saved)"
                    : "[Landscapes] render distance unlimited (saved)");
            });

        capi.ChatCommands.Create("dlmask")
            .WithDescription("Hand each vanilla chunk its own ground instead of using one distance. On by default; turn it off to fall back to a single measured distance.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");
                if (renderer.ChunkMaskFailed)
                    return TextCommandResult.Success(
                        "[Landscapes] the chunk mask hit an error this session and stays off. " +
                        "The distance handoff is drawing.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[Landscapes] chunk mask {(renderer.ChunkMaskEnabled ? "on" : "off")}. " +
                        "On gives each loaded vanilla chunk its own ground and skips cached terrain " +
                        "that is entirely replaced. Off uses a single measured distance.");

                renderer.ChunkMaskEnabled = (bool)args[0];
                SaveConfig();
                return TextCommandResult.Success(
                    $"[Landscapes] chunk mask {(renderer.ChunkMaskEnabled ? "on" : "off")} (saved). " +
                    "The change applies on the next frame.");
            });

        capi.ChatCommands.Create("dlwhy")
            .WithDescription("Look at a hole and run this. Searches your line of sight for the first ground that nothing is drawing and says which part of the mod is responsible. Optional argument is how far to search, 512 blocks by default.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalInt("blocksAhead"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");

                // Searching the whole line of sight rather than one chosen distance: the
                // hole a player is looking at is wherever it is, and a confident report
                // about the wrong chunk reads exactly like a report about the right one.
                // 512 blocks was a guess and it was too short: the cached band runs out to
                // the far distance, so a hole in its outer half sat past the end of the
                // search and the command answered "nothing wrong" about ground it never
                // looked at. Default to the distance the mod actually draws to.
                int range = args.Parsers[0].IsMissing
                    ? GameMath.Clamp((int)renderer.EffectiveFarDistance, 512, 16384)
                    : GameMath.Clamp((int)args[0], 16, 16384);
                Vec3f look = capi.World.Player.Entity.Pos.GetViewVector();
                var camera = capi.World.Player.Entity.CameraPos;

                // Logged as well as shown: game chat cannot be copied out, and this answer is
                // only useful in the hands of whoever reads the log (G72).
                string why = renderer.ExplainViewRay(
                    camera.X, camera.Y, camera.Z, look.X, look.Y, look.Z, range);
                LogReportLines("why", why);
                return TextCommandResult.Success("[Landscapes] " + why);
            });

        capi.ChatCommands.Create("dltint")
            .WithDescription("Take one block's distant colour apart: the stored colour, how much of "
                + "it the live tint is not allowed to touch, and the tint itself against the game's "
                + "own answer for the same block. Aim at a block, or name one, or stand on it. "
                + "'.dltint field on|off' switches per-area distant climate. "
                + "'.dltint leaves on|off' switches per-tree seasonal variation. "
                + "'.dltint leafwide on|off' spends it out to level 2 instead of level 0. "
                + "'.dltint leafcheap on|off' prices the noise alone - WRONG COLOURS, for a "
                + "measurement only. "
                + "Written to the client log, which is the only copy long enough to read.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("blockCode"),
                      capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                // Two switches rather than blocks, and no block is called either. Kept on this
                // command instead of their own because this report is how they get judged, and a
                // diagnostic and the thing it measures belong together.
                if (!args.Parsers[0].IsMissing
                    && ((string)args[0]).Equals("field", StringComparison.OrdinalIgnoreCase))
                {
                    if (renderer == null)
                        return TextCommandResult.Success(
                            "[Landscapes] the distant-terrain renderer did not start; the client log says why.");
                    if (!args.Parsers[1].IsMissing)
                    {
                        renderer.ClimateFieldTint = (bool)args[1];
                        SaveConfig();
                    }
                    return TextCommandResult.Success(
                        $"[Landscapes] climate field "
                        + $"{(renderer.ClimateFieldTint ? "on" : "off")}. "
                        + "On tints distant ground from the climate it stands in, cell by cell. "
                        + "Off is one climate for the whole view, taken where you are standing, "
                        + "which is what every build before this one drew. Where the client has "
                        + "no climate the nearest known one is used either way.");
                }

                // Per-position seasonal variation on leaves. The whole question is whether a
                // forest turning unevenly reads better than one flat colour, which is judged by
                // looking at trees rather than at numbers.
                if (!args.Parsers[0].IsMissing
                    && ((string)args[0]).Equals("leaves", StringComparison.OrdinalIgnoreCase))
                {
                    if (renderer == null)
                        return TextCommandResult.Success(
                            "[Landscapes] the distant-terrain renderer did not start; the client log says why.");
                    if (!args.Parsers[1].IsMissing)
                    {
                        renderer.LeafSeasonVariation = (bool)args[1] ? 1f : 0f;
                        SaveConfig();
                    }
                    return TextCommandResult.Success(
                        $"[Landscapes] leaf season variation "
                        + $"{(renderer.LeafSeasonVariation > 0f ? "on" : "off")}. "
                        + "On picks each patch of distant leaves its own row of the seasonal "
                        + "colour table, the way the game does, so a forest turns unevenly "
                        + "instead of all at once. It fades out as one cached voxel grows past "
                        + "a block or two, because below that the variation is finer than the "
                        + "terrain can carry. Off draws the average of all sixteen rows, which "
                        + "is what every build before this one did.");
                }

                // How far out the CLIMATE lookup itself is paid for. The same question the
                // leaf switch below asks, about the more expensive half: leaves pick a row of
                // one biome's season table, this picks the biome.
                if (!args.Parsers[0].IsMissing
                    && ((string)args[0]).Equals("climatewide", StringComparison.OrdinalIgnoreCase))
                {
                    if (renderer == null)
                        return TextCommandResult.Success(
                            "[Landscapes] the distant-terrain renderer did not start; the client log says why.");
                    if (!args.Parsers[1].IsMissing)
                    {
                        renderer.ClimateFadeBlocks = (bool)args[1]
                            ? LodVisualSettings.FullClimateTintRange
                            : LodVisualSettings.CloseClimateTintRange;
                        SaveConfig();
                    }
                    bool everywhere = renderer.ClimateFadeBlocks <= 0f;
                    return TextCommandResult.Success(
                        "[Landscapes] distant ground works out its own climate "
                        + (everywhere
                            ? "at every level (zero fade)."
                            : $"out to {renderer.ClimateFadeBlocks:0} blocks per voxel.")
                        + " Past that it wears the flat colour taken where you stand, which is "
                        + "what every build before 0.3.179 drew everywhere. The lookup is eight "
                        + "table reads per vertex and a section costs the same per vertex at "
                        + "any level, so this is where most of the feature's frame cost lives. "
                        + "On spends it everywhere. What to watch for is a biome edge on the "
                        + "horizon changing colour as it crosses the cut-off.");
                }

                // How far out that variation is paid for. Separate from whether it is on at all
                // because they are different questions: one is whether a forest turning
                // unevenly looks better, the other is how many LOD levels that is worth. A
                // section costs the same per vertex whatever level it holds, and the near
                // levels are not the small ones - so this switch is where nearly all of the
                // feature's frame cost lives.
                if (!args.Parsers[0].IsMissing
                    && ((string)args[0]).Equals("leafwide", StringComparison.OrdinalIgnoreCase))
                {
                    if (renderer == null)
                        return TextCommandResult.Success(
                            "[Landscapes] the distant-terrain renderer did not start; the client log says why.");
                    if (!args.Parsers[1].IsMissing)
                        renderer.LeafSeasonFadeBlocks = (bool)args[1] ? 8f : 2f;
                    bool wide = renderer.LeafSeasonFadeBlocks > 2f;
                    return TextCommandResult.Success(
                        $"[Landscapes] leaf season variation reaches "
                        + (wide ? "level 2" : "level 0 only")
                        + $" (zero at {renderer.LeafSeasonFadeBlocks:0} blocks per voxel). "
                        + "Off is level 0 alone, where one voxel is one block and the variation "
                        + "is drawn at the scale vanilla draws it, next to the vanilla trees it "
                        + "has to match. On spends it across levels 0 to 2 as well, which in a "
                        + "measured view meant 97 of 110 drawn sections paying for it instead "
                        + "of 31 - level 1 alone had more sections in view than level 0. "
                        + "Needs .dltint leaves on to show at all.");
                }

                // The instrument for whether baking the season row is worth building. Not
                // saved and not restored: it draws rows the game would never pick, so the only
                // safe lifetime for it is the session it was typed in.
                if (!args.Parsers[0].IsMissing
                    && ((string)args[0]).Equals("leafcheap", StringComparison.OrdinalIgnoreCase))
                {
                    if (renderer == null)
                        return TextCommandResult.Success(
                            "[Landscapes] the distant-terrain renderer did not start; the client log says why.");
                    if (!args.Parsers[1].IsMissing)
                        renderer.LeafSeasonCheapRow = (bool)args[1];
                    return TextCommandResult.Success(
                        "[Landscapes] leaf season row "
                        + (renderer.LeafSeasonCheapRow
                            ? "CHEAP HASH - the colours are wrong on purpose"
                            : "the engine's own noise")
                        + ". The variation costs two value noises per vertex plus one scattered "
                        + "fetch into the seasonal map. On keeps the fetch and drops the noise, "
                        + "so the difference between the two is what baking the row into the "
                        + "mesh could win, and the rest is what only a baked COLOUR could. "
                        + "Session-only: it is never saved and a restart clears it.");
                }

                // Aim first, feet second. A player comparing cached ground against the game's
                // own is looking at the seam, so the block under the crosshair is the one they
                // mean; standing on it is the fallback for ground too far to select.
                BlockPos? at = capi.World.Player.CurrentBlockSelection?.Position;
                if (at == null)
                {
                    var feet = capi.World.Player.Entity.Pos;
                    at = new BlockPos((int)feet.X, (int)feet.Y - 1, (int)feet.Z);
                }
                Block? block = capi.World.BlockAccessor.GetBlock(at);

                if (!args.Parsers[0].IsMissing)
                {
                    string code = (string)args[0];
                    Block? named = capi.World.GetBlock(new AssetLocation(code));
                    if (named == null || named.BlockId <= 0)
                        return TextCommandResult.Error(
                            $"[Landscapes] no block called '{code}'. Give a full code such as "
                            + "soil-medium-normal, or leave it off and aim at one.");
                    // The named block is described where the player is standing: climate and
                    // season are functions of position, so it has to be asked somewhere.
                    block = named;
                }

                if (block == null || block.BlockId <= 0)
                    return TextCommandResult.Error("[Landscapes] no block there to describe.");

                bool topSoil = TryTopSoilColor(block, out int composite, out LodUntintedShare share);
                int slot = tints.PeekSlot(block, topSoil ? share : LodUntintedShare.None);
                int stored = StableColorOf(block);
                (float sr, float sg, float sb) = LodBlockTextureRegistry.Channels(stored);

                var lines = new List<string>
                {
                    $"stored colour R{(int)(sr * 255)} G{(int)(sg * 255)} B{(int)(sb * 255)}"
                    + (topSoil
                        ? $" (grass-over-soil composite {composite & 0xFF},{(composite >> 8) & 0xFF},"
                          + $"{(composite >> 16) & 0xFF})"
                        : " (no grass overlay)"),
                };
                lines.AddRange(tints.Describe(
                    capi.World, block, slot, topSoil ? share : LodUntintedShare.None, at.X, at.Y, at.Z));
                if (renderer != null)
                    lines.AddRange(renderer.DescribeClimateTint(at.X, at.Y, at.Z, slot));

                // The log gets the report as written; the chat copy gets its angle brackets
                // taken out first. Chat text is markup, an unmatched bracket wedges the panel
                // for as long as the message stays in the scrollback, and a diagnostic that
                // breaks the window you read it in is worse than no diagnostic.
                string report = LogAndReturn("tint", string.Join('\n', lines));
                return TextCommandResult.Success(
                    "[Landscapes] " + report.Replace('<', '(').Replace('>', ')'));
            });

        capi.ChatCommands.Create("dlclimate")
            .WithDescription("How far the client actually has climate data, and how fast climate "
                + "changes once it does. Decides whether distant terrain can be given its own "
                + "biome colour or only the one under your feet. Written to the client log.")
            .HandleWith(_ =>
            {
                var camera = capi.World.Player.Entity.CameraPos;
                float far = renderer?.EffectiveFarDistance ?? 0f;
                string report = LogAndReturn("climate", LodClimateProbe.Describe(
                    capi.World, (int)camera.X, (int)camera.Z, far, renderer?.ClimateMemory));
                // Chat gets the headline only. The full table is dozens of lines and the
                // panel cannot hold it; the log is the copy that is meant to be read.
                string[] lines = report.Split('\n');
                string reach = Array.Find(lines, l => l.Contains("fully covered")) ?? "";
                return TextCommandResult.Success(
                    "[Landscapes] climate probe written to the client log. "
                    + reach.Trim().Replace('<', '(').Replace('>', ')'));
            });

        capi.ChatCommands.Create("dlpaint")
            .WithDescription("Paint the terrain the mask is hiding magenta instead of hiding it. Diagnostic.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[Landscapes] paint mode {(renderer.MaskDebugPaint ? "on" : "off")}.");

                renderer.MaskDebugPaint = (bool)args[0];
                return TextCommandResult.Success(
                    $"[Landscapes] paint mode {(renderer.MaskDebugPaint ? "on" : "off")}. " +
                    "Magenta is terrain the mask is hiding. A gap that stays empty is not the mask.");
            });

        // Session-only, like .dlpaint. The flash this exists for lasts a frame or two, too short
        // to read on screen, and chat cannot be copied, so every switch and every finding goes to
        // the client log (G72).
        capi.ChatCommands.Create("dlgap")
            .WithDescription("Log every chunk the mask gives to vanilla while vanilla is not drawing it: "
                + "one client-log line per gap, with the reason and how many frames it lasted. "
                + "on | off. '.dlgap release on|off' also hands such chunks to the cache in the "
                + "frame they are found, as a comparison.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("part"),
                      capi.ChatCommands.Parsers.OptionalWord("value"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");

                string part = args.Parsers[0].IsMissing ? "" : (string)args[0];
                string value = args.Parsers[1].IsMissing ? "" : (string)args[1];

                if (part.Equals("on", StringComparison.OrdinalIgnoreCase))
                    return TextCommandResult.Success(renderer.SetHandoffGapLog(true));
                if (part.Equals("off", StringComparison.OrdinalIgnoreCase))
                    return TextCommandResult.Success(renderer.SetHandoffGapLog(false));
                if (part.Equals("release", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.Equals("on", StringComparison.OrdinalIgnoreCase))
                        return TextCommandResult.Success(renderer.SetHandoffGapRelease(true));
                    if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
                        return TextCommandResult.Success(renderer.SetHandoffGapRelease(false));
                }
                return TextCommandResult.Success(renderer.DescribeHandoffGaps());
            });

        // Session-only. The first measurement of what the graphics card actually drew at the
        // handoff: every earlier diagnostic compared CPU beliefs with each other. Every switch
        // and every finding goes to the client log (G72).
        capi.ChatCommands.Create("dlsky")
            .WithDescription("Watch for sky showing through the ground. After distant terrain is drawn, "
                + "count sky below the horizon every frame and write each flash to the client log with "
                + "where it was and how the chunk mask stood there. on | off. '.dlsky verify on|off' "
                + "also reads the mask back from the graphics card after every upload.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("part"),
                      capi.ChatCommands.Parsers.OptionalWord("value"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");

                string part = args.Parsers[0].IsMissing ? "" : (string)args[0];
                string value = args.Parsers[1].IsMissing ? "" : (string)args[1];

                if (part.Equals("on", StringComparison.OrdinalIgnoreCase))
                    return TextCommandResult.Success(renderer.SetSkyWitness(true));
                if (part.Equals("off", StringComparison.OrdinalIgnoreCase))
                    return TextCommandResult.Success(renderer.SetSkyWitness(false));
                if (part.Equals("verify", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.Equals("on", StringComparison.OrdinalIgnoreCase))
                        return TextCommandResult.Success(renderer.SetSkyMaskVerify(true));
                    if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
                        return TextCommandResult.Success(renderer.SetSkyMaskVerify(false));
                }
                return TextCommandResult.Success("[Landscapes] " + renderer.DescribeSkyWitness());
            });

        // Session-only. The fix for the handoff flash, switchable only so the old timing can be
        // compared against it with .dlsky. Every switch goes to the client log (G72).
        capi.ChatCommands.Create("dllaterange")
            .WithDescription("Check vanilla's draw range again after the game moves the player each frame, "
                + "so a chunk leaving it goes to distant terrain in that same frame. On by default. "
                + "on | off. Off restores the timing of 0.4.13 to 0.4.52, for comparison only.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("value"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");

                string value = args.Parsers[0].IsMissing ? "" : (string)args[0];
                if (value.Equals("on", StringComparison.OrdinalIgnoreCase))
                    return TextCommandResult.Success(renderer.SetLateRangeCheck(true));
                if (value.Equals("off", StringComparison.OrdinalIgnoreCase))
                    return TextCommandResult.Success(renderer.SetLateRangeCheck(false));
                return TextCommandResult.Success("[Landscapes] " + renderer.DescribeLateRangeCheck());
            });

        // Session-only, like the other comparison switches. The point of shipping these is
        // that the owner stands at the seam and turns them until it disappears; the values
        // that survive that become defaults in source, so persisting them would only make
        // the next build disagree with what it says it does.
        capi.ChatCommands.Create("dlband")
            .WithDescription("Blend the seam between game terrain and distant terrain. "
                + "No argument reports the settings. on | off | shape | map | speckle | width | step | inner | strength | darken | saturate | sink | overlap.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("part"),
                      capi.ChatCommands.Parsers.OptionalWord("value"),
                      capi.ChatCommands.Parsers.OptionalWord("green"),
                      capi.ChatCommands.Parsers.OptionalWord("blue"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");

                string part = args.Parsers[0].IsMissing ? "" : ((string)args[0]).ToLowerInvariant();
                string value = args.Parsers[1].IsMissing ? "" : (string)args[1];

                switch (part)
                {
                    case "":
                        return TextCommandResult.Success(renderer.DescribeBlendBand());

                    case "on":
                    case "off":
                        renderer.BandEnabled = part == "on";
                        SaveConfig();
                        return TextCommandResult.Success(
                            $"[Landscapes] blending {part}. " + renderer.DescribeBlendBand());

                    case "map":
                        // The picture cannot answer this one. A ramp seen from altitude is a
                        // few pixels tall, so a gradient and a cliff look identical, and the
                        // number that separates them is not on screen at all.
                        return TextCommandResult.Success(
                            "[Landscapes] " + renderer.LogBlendBandMap());

                    case "shape":
                        // Judging the ring's outline is a different question from judging its
                        // colour, and answering them separately is what stops a bad shape and
                        // a bad correction being mistaken for one another.
                        if (value is not ("on" or "off"))
                            return TextCommandResult.Success(
                                $"[Landscapes] shape view {(renderer.BandDebugPaint ? "on" : "off")}. "
                                + "On paints the blending band by strength - red at the seam, "
                                + "through yellow, to blue at its outer edge - so you can look down "
                                + "at its shape without any colour change being involved.");
                        renderer.BandDebugPaint = value == "on";
                        return TextCommandResult.Success(
                            $"[Landscapes] shape view {(renderer.BandDebugPaint ? "on" : "off")}.");

                    case "width":
                        if (!float.TryParse(value, out float width))
                            return TextCommandResult.Success(
                                $"[Landscapes] band width {renderer.BandWidthBlocks:0} blocks. "
                                + "Give a number of blocks. Wider hides a bigger colour step but "
                                + "covers more ground.");
                        renderer.BandWidthBlocks = GameMath.Clamp(width, 8f, 2048f);
                        return TextCommandResult.Success(
                            $"[Landscapes] band width {renderer.BandWidthBlocks:0} blocks.");

                    case "step":
                        if (!float.TryParse(value, out float step))
                            return TextCommandResult.Success(
                                $"[Landscapes] step {renderer.BandStepBlocks:0} blocks. "
                                + "8, 16 or 32. Bigger steps read as chunkier terrain; there is "
                                + "deliberately no smooth setting, because a ramp finer than one "
                                + "block stops looking like ground.");
                        renderer.BandStepBlocks = GameMath.Clamp(step, 8f, 32f);
                        return TextCommandResult.Success(
                            $"[Landscapes] step {renderer.BandStepBlocks:0} blocks.");

                    case "inner":
                        if (value is "auto")
                        {
                            renderer.BandInnerBlocks = -1f;
                            return TextCommandResult.Success(
                                $"[Landscapes] inner edge follows the overlap: "
                                + $"{renderer.BandInnerBlocks:0} blocks.");
                        }
                        if (!float.TryParse(value, out float inner))
                            return TextCommandResult.Success(
                                $"[Landscapes] inner edge {renderer.BandInnerBlocks:0} blocks"
                                + (renderer.BandInnerDerived ? " (following the overlap)" : " (pinned)")
                                + ". Where distant terrain overlaps the game's own, the blending has "
                                + "to start that far inside the seam you can see. It follows the "
                                + "overlap on its own, so it is zero while .dlexact is on. Give a "
                                + "number to pin it, or 'auto' to hand it back.");
                        renderer.BandInnerBlocks = GameMath.Clamp(inner, 0f, 256f);
                        return TextCommandResult.Success(
                            $"[Landscapes] inner edge pinned at {renderer.BandInnerBlocks:0} blocks.");

                    case "strength":
                        if (!float.TryParse(value, out float strength))
                            return TextCommandResult.Success(
                                $"[Landscapes] strength {renderer.BandStrength:0.00}. "
                                + "0 to 1. How much of the correction lands right at the seam.");
                        renderer.BandStrength = GameMath.Clamp(strength, 0f, 1f);
                        return TextCommandResult.Success(
                            $"[Landscapes] strength {renderer.BandStrength:0.00}.");

                    case "speckle":
                        if (!float.TryParse(value, out float speckle))
                            return TextCommandResult.Success(
                                $"[Landscapes] speckle {renderer.BandSpeckle:0.00}. "
                                + "How much block-to-block variation distant terrain gets at the "
                                + "seam, fading out across the band. The game's ground is never one "
                                + "flat colour; this is what stops ours meeting it as a flat sheet. "
                                + "0 turns it off.");
                        renderer.BandSpeckle = GameMath.Clamp(speckle, 0f, 1f);
                        return TextCommandResult.Success(
                            $"[Landscapes] speckle {renderer.BandSpeckle:0.00}.");

                    case "darken":
                        if (!float.TryParse(value, out float darken))
                            return TextCommandResult.Success(
                                $"[Landscapes] darken {renderer.BandDarken:0.00}. "
                                + "How much dimmer distant terrain is made at the seam. Below 1 "
                                + "darkens it. This works on whatever colour is already there, so "
                                + "it does not carry one area's colour into another.");
                        renderer.BandDarken = GameMath.Clamp(darken, 0.5f, 1.5f);
                        return TextCommandResult.Success(
                            $"[Landscapes] darken {renderer.BandDarken:0.00}.");

                    case "saturate":
                        if (!float.TryParse(value, out float saturate))
                            return TextCommandResult.Success(
                                $"[Landscapes] saturate {renderer.BandSaturate:0.00}. "
                                + "How much more colourful, less washed out, distant terrain is "
                                + "made at the seam. Above 1 pushes it further from grey without "
                                + "deciding which colour it should be.");
                        renderer.BandSaturate = GameMath.Clamp(saturate, 0.5f, 2.5f);
                        return TextCommandResult.Success(
                            $"[Landscapes] saturate {renderer.BandSaturate:0.00}.");

                    case "overlap":
                        if (!float.TryParse(value, out float overlap))
                            return TextCommandResult.Success(
                                $"[Landscapes] overlap +{renderer.OverlapMarginBlocks:0} blocks. "
                                + "How early distant terrain takes back ground from the game's own. "
                                + "Zero by default since 0.3.163: it used to be 16, to hide a hole "
                                + "at the seam that turned out to have two other causes, both now "
                                + "fixed. Raise it if ground ever goes missing where the two meet - "
                                + "and say so, because that means something else is wrong.");
                        renderer.OverlapMarginBlocks = GameMath.Clamp(overlap, 0f, 256f);
                        return TextCommandResult.Success(
                            $"[Landscapes] overlap +{renderer.OverlapMarginBlocks:0} blocks. "
                            + "Applies as the game re-checks chunks, within a second or so.");

                    case "sink":
                        if (!int.TryParse(value, out int sink))
                            return TextCommandResult.Success(
                                $"[Landscapes] sink {renderer.BandSinkVoxels} of 16ths of a block. "
                                + "Lowers distant terrain slightly so it stops fighting the game's own "
                                + "terrain where the two overlap. Most visible underwater, on lake and "
                                + "river floors. Raise it if the flickering is still there.");
                        renderer.BandSinkVoxels = GameMath.Clamp(sink, 0, 16);
                        return TextCommandResult.Success(
                            $"[Landscapes] sink {renderer.BandSinkVoxels} of 16ths of a block.");

                    default:
                        return TextCommandResult.Success(
                            "[Landscapes] on | off | shape | speckle | width | step | inner | strength | darken | saturate | sink | overlap.");
                }
            });

        capi.ChatCommands.Create("dlnear")
            .WithDescription("How close to you distant terrain stops being drawn, in blocks. "
                + "0 draws it right up to the camera, which is what you want when the game's own "
                + "view distance is turned down far enough to see it.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("blocks"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");
                if (args.Parsers[0].IsMissing
                    || !float.TryParse((string)args[0], out float blocks))
                    return TextCommandResult.Success(
                        $"[Landscapes] near suppression {renderer.NearSuppressionBlocks:0} blocks. "
                        + "0 is off. It used to be 48, to stop rough distant terrain appearing at "
                        + "arm's length where the game has not confirmed it is drawing - most "
                        + "likely underground. Raise it again if that comes back.");

                renderer.NearSuppressionBlocks = GameMath.Clamp(blocks, 0f, 512f);
                return TextCommandResult.Success(
                    $"[Landscapes] near suppression {renderer.NearSuppressionBlocks:0} blocks.");
            });

        capi.ChatCommands.Create("dlstandin")
            .WithDescription("The 0.4.135 fix for rough distant terrain briefly appearing over the "
                + "game's own ground. On, a coarse piece standing in for finer terrain that is not "
                + "ready covers only the part that is waiting, and chunks the game has just loaded "
                + "are handed to it within a frame or two. Off restores the old behaviour so the two "
                + "can be compared. Not saved; every session starts on.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[Landscapes] stand-in fix {(renderer.StandInFix ? "on" : "off")}.");

                renderer.StandInFix = (bool)args[0];
                // Logged as well as shown, so a report can be matched to the side of the switch it
                // was seen on without anyone having to remember when it was flipped (G72).
                string state = renderer.StandInFix
                    ? "on: coarse stand-ins cover only the part that is waiting, and freshly loaded "
                        + "chunks are handed to the game within a frame or two"
                    : "off: coarse stand-ins cover their whole area and freshly loaded chunks wait for "
                        + "the background sweep, as before 0.4.135";
                Mod.Logger.Notification("[Landscapes] stand-in fix {0}", state);
                return TextCommandResult.Success("[Landscapes] stand-in fix " + state + ".");
            });

        capi.ChatCommands.Create("dltexture")
            .WithDescription("Draw distant ground with the game's own block textures inside the "
                + "blending band, instead of one flat colour per block. "
                + "on | off | strength | detail | mips.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("part"),
                      capi.ChatCommands.Parsers.OptionalWord("value"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");

                string part = args.Parsers[0].IsMissing ? "" : ((string)args[0]).ToLowerInvariant();
                switch (part)
                {
                    case "on":
                    case "off":
                        renderer.BlockTexturesEnabled = part == "on";
                        SaveConfig();
                        return TextCommandResult.Success(
                            $"[Landscapes] block textures {part}. "
                            + renderer.DescribeBlockTextures());

                    case "detail":
                        if (!float.TryParse(
                                args.Parsers[1].IsMissing ? "" : (string)args[1], out float detail))
                            return TextCommandResult.Success(
                                $"[Landscapes] texture detail {renderer.BlockTextureDetail:0.0}. "
                                + "How much sharper than the distance warrants to draw the texture. "
                                + "0 is technically correct and invisible - at that range the game "
                                + "shrinks a texture down to a single flat colour, which is the "
                                + "problem. Raise it until you can see the texture; too high and "
                                + "distant ground will sparkle as you move.");
                        renderer.BlockTextureDetail = GameMath.Clamp(detail, 0f, 8f);
                        return TextCommandResult.Success(
                            $"[Landscapes] texture detail {renderer.BlockTextureDetail:0.0}.");

                    case "mips":
                        if (!float.TryParse(
                                args.Parsers[1].IsMissing ? "" : (string)args[1], out float mips))
                            return TextCommandResult.Success(
                                $"[Landscapes] texture detail limit {renderer.BlockTextureMipLimit:0.0}. "
                                + "How far away the real texture keeps being drawn before it hands "
                                + "back to flat colour. Raise it if the texture stops before the "
                                + "seam; lower it if distant ground picks up colours from the "
                                + "wrong blocks.");
                        renderer.BlockTextureMipLimit = GameMath.Clamp(mips, 0f, 8f);
                        return TextCommandResult.Success(
                            $"[Landscapes] texture detail limit {renderer.BlockTextureMipLimit:0.0}.");

                    case "strength":
                        if (!float.TryParse(
                                args.Parsers[1].IsMissing ? "" : (string)args[1], out float strength))
                            return TextCommandResult.Success(
                                $"[Landscapes] texture strength {renderer.BlockTextureStrength:0.00}. "
                                + "0 to 1. How much real texture shows where the band is strongest.");
                        renderer.BlockTextureStrength = GameMath.Clamp(strength, 0f, 1f);
                        return TextCommandResult.Success(
                            $"[Landscapes] texture strength {renderer.BlockTextureStrength:0.00}.");

                    default:
                        return TextCommandResult.Success(
                            "[Landscapes] " + renderer.DescribeBlockTextures());
                }
            });

        capi.ChatCommands.Create("dltoplight")
            .WithDescription("Light flat cached ground the way the game lights flat ground, instead of by sun angle. On by default.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[Landscapes] flat-top lighting {(renderer.FlatTopLight ? "on" : "off")}.");

                renderer.FlatTopLight = (bool)args[0];
                return TextCommandResult.Success(
                    $"[Landscapes] flat-top lighting {(renderer.FlatTopLight ? "on" : "off")}. " +
                    "The game never darkens flat ground as the sun drops; off shades it by sun angle " +
                    "as before. Worth comparing at dawn or dusk, not at midday.");
            });

        // Separate switches rather than one, because the point of shipping them is
        // that a single dusk-to-night sweep can attribute what it sees. Reading both
        // shaders settles what vanilla computes; it cannot settle whether copying it onto
        // terrain kilometres away looks right, and only the owner flipping these while
        // watching can.
        capi.ChatCommands.Create("dllight")
            .WithDescription("Match cached terrain lighting to the game's. sun | moondir | ramp | ambient | boost | sky | all, on or off. All on by default.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("part"),
                      capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");

                string DescribeLight() =>
                    $"sun {(renderer.LightSunLevel ? "on" : "off")}, " +
                    $"moondir {(renderer.LightMoonDirection ? "on" : "off")}, " +
                    $"ramp {(renderer.LightVanillaRamp ? "on" : "off")}, " +
                    $"ambient {(renderer.LightAmbientColor ? "on" : "off")}, " +
                    $"boost {(renderer.LightDayBoost ? "on" : "off")}, " +
                    $"sky {(renderer.LightSkyDayLight ? "on" : "off")}";

                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success($"[Landscapes] lighting: {DescribeLight()}.");

                if (args.Parsers[1].IsMissing)
                    return TextCommandResult.Error(
                        "[Landscapes] use: .dllight sun | moondir | ramp | ambient | boost | sky | all, then on or off.");

                string part = ((string)args[0]).ToLowerInvariant();
                bool on = (bool)args[1];
                string note;
                switch (part)
                {
                    case "sun":
                        renderer.LightSunLevel = on;
                        note = "Multiply by the sun light level the game bakes into its own terrain. " +
                               "The biggest one: off is 24% brighter at every hour, on every block.";
                        break;
                    case "moondir":
                        renderer.LightMoonDirection = on;
                        note = "Shade by the game's light vector, which follows the moon at night. " +
                               "Changes nothing while the sun is up.";
                        break;
                    case "ramp":
                        renderer.LightVanillaRamp = on;
                        note = "Use the game's shade ramp and its darker floor on slopes facing away " +
                               "from the light. A constant difference, visible at any time of day.";
                        break;
                    case "ambient":
                        renderer.LightAmbientColor = on;
                        note = "Light by the game's own ambient colour instead of the sun colour. " +
                               "This is the one to watch through sunset into night.";
                        break;
                    case "boost":
                        renderer.LightDayBoost = on;
                        note = "Apply the game's daylight brightening. This is the only one that " +
                               "changes broad daylight; off is the older, dimmer look.";
                        break;
                    case "sky":
                        renderer.LightSkyDayLight = on;
                        note = "Fade the far edge of the cache into the sky the game actually " +
                               "draws. Look at the horizon band, not the ground.";
                        break;
                    case "all":
                        renderer.LightSunLevel = on;
                        renderer.LightMoonDirection = on;
                        renderer.LightVanillaRamp = on;
                        renderer.LightAmbientColor = on;
                        renderer.LightDayBoost = on;
                        renderer.LightSkyDayLight = on;
                        note = "Off is the lighting every build before this one used.";
                        break;
                    default:
                        return TextCommandResult.Error(
                            "[Landscapes] use: .dllight sun | moondir | ramp | ambient | boost | sky | all, then on or off.");
                }

                return TextCommandResult.Success($"[Landscapes] lighting: {DescribeLight()}. {note}");
            });

        // The one in-game switch for the whole GPU terrain path, and since 0.3.103 that path
        // is the default rather than a measurement. Every other stage is nested under the
        // regional arenas this controls - batching draws from them, culling zeroes the
        // commands batching writes, the split needs something to cull. Turning this off is
        // now a deliberate fail-loud test: cached opaque terrain disappears and no retired
        // renderer substitutes for it.
        //
        // It is deliberately the ONLY remaining switch. The staging commands it used to sit
        // beside could each select a half-built configuration, which is a way to collect a
        // bug report against something nobody ships.
        capi.ChatCommands.Create("dlgpu")
            .WithDescription("Turn the GPU terrain renderer off or on. off | on | verify. On by default; remembered between sessions.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("mode"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        "[Landscapes] GPU terrain renderer " + renderer.DescribeGpuShadow());

                string mode = (string)args[0];
                if (!renderer.RequestGpuShadow(mode))
                    return TextCommandResult.Error("[Landscapes] use: .dlgpu off | on | verify");

                SaveConfig();
                bool off = mode.Equals("off", StringComparison.OrdinalIgnoreCase);
                return TextCommandResult.Success(off
                    ? "[Landscapes] GPU terrain renderer switching off; buffers release next frame and cached opaque terrain will disappear. No legacy fallback exists."
                    : "[Landscapes] GPU terrain renderer switching on"
                        + (mode.Equals("verify", StringComparison.OrdinalIgnoreCase)
                            ? " with content verification" : "")
                        + ". Live sections are re-meshed into it first, so wait a few seconds, "
                        + "then run .dlgpu again for the numbers.");
            });

        capi.ChatCommands.Create("dlskip")
            .WithDescription("While the chunk mask is on: allow dropping a cached piece entirely when the game covers all of it. "
                + "On by default. 'why' writes to the client log what is stopping it.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("part"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");

                string part = args.Parsers[0].IsMissing ? "" : ((string)args[0]).ToLowerInvariant();
                switch (part)
                {
                    // "true"/"false"/"1"/"0" as well as on/off: this command took an OptionalBool
                    // before it grew a 'why', and silently ignoring what used to work would turn
                    // a comparison toggle into a no-op with a success message.
                    case "on":
                    case "off":
                    case "true":
                    case "false":
                    case "1":
                    case "0":
                        renderer.WholeMeshSkip = part is "on" or "true" or "1";
                        return TextCommandResult.Success(
                            $"[Landscapes] whole-piece skipping "
                            + $"{(renderer.WholeMeshSkip ? "on" : "off")}. Applies on the next frame.");

                    // The report that answers "why is nothing being dropped". It is a
                    // measurement over the pieces resident right now, so it has to be asked
                    // for while standing where the problem is - and it goes to the log,
                    // because it is far too long for the chat window to hold (G72).
                    case "why":
                        return TextCommandResult.Success(
                            "[Landscapes] " + renderer.LogSectionOwnershipMap());

                    default:
                        return TextCommandResult.Success(
                            $"[Landscapes] whole-piece skipping {(renderer.WholeMeshSkip ? "on" : "off")}, "
                            + $"{renderer.VanillaOwnedDrawsSkipped} pieces dropped this interval. "
                            + "Off keeps the per-pixel mask working but never drops a whole cached "
                            + "piece. '.dlskip why' writes to the client log what is stopping it.");
                }
            });

        capi.ChatCommands.Create("dlbackface")
            .WithDescription("Reject back-facing cached opaque terrain. On by default and not saved.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[Landscapes] opaque back-face culling {(renderer.OpaqueBackfaceCulling ? "on" : "off")} (on by default, not saved).");

                renderer.OpaqueBackfaceCulling = (bool)args[0];
                return TextCommandResult.Success(
                    $"[Landscapes] opaque back-face culling {(renderer.OpaqueBackfaceCulling ? "on" : "off")} (on by default, not saved). " +
                    "Applies on the next frame; water and thin cover remain two-sided.");
            });

        // The one switch that makes this phase's gate answerable by looking: with it off,
        // sections are bounded bedrock-to-sky exactly as before, so anything that vanishes
        // while looking up or down comes straight back.
        // Phase 4 shadow work. It builds the depth pyramid and measures it, and hides
        // nothing at all - so the only thing to look for while it is on is whether the
        // frame rate moves, which is the number that decides whether the phase continues.
        capi.ChatCommands.Create("dlhzb")
            .WithDescription("Build the depth pyramid used to find terrain hidden behind hills. Measurement only; hides nothing. Off by default and not saved. `.dlhzb why` explains the piece you are looking at.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");
                if (args.Parsers[0].IsMissing)
                {
                    // Logged as well as shown. Game chat cannot be copied out, so a report
                    // that exists only on screen has to be transcribed by hand or
                    // photographed - and these are long lines of digits, which is the worst
                    // possible thing to ask someone to retype. The log is the copy that can
                    // actually be handed to somebody.
                    string report = renderer.ReportDepthPyramid();
                    LogReportLines("hzb", report);
                    return TextCommandResult.Success("[Landscapes] " + report);
                }

                string word = ((string)args[0]).ToLowerInvariant();

                // Look at a piece of far terrain and ask about that one. A counter says how
                // often something happens; only this can be checked against what is on the
                // screen in front of the person running it.
                if (word == "why")
                {
                    Vec3f look = capi.World.Player.Entity.Pos.GetViewVector();
                    var camera = capi.World.Player.Entity.CameraPos;
                    int range = GameMath.Clamp((int)renderer.EffectiveFarDistance, 512, 32768);
                    string why = renderer.ExplainDepthPyramid(
                        camera.X, camera.Y, camera.Z, look.X, look.Y, look.Z, range);
                    LogReportLines("hzb why", why);
                    return TextCommandResult.Success("[Landscapes] " + why);
                }

                if (word != "on" && word != "off")
                    return TextCommandResult.Error("[Landscapes] use: .dlhzb on | off | why");

                renderer.DepthPyramidEnabled = word == "on";
                renderer.ResetDepthPyramidInterval();

                return TextCommandResult.Success(
                    $"[Landscapes] depth pyramid {(renderer.DepthPyramidEnabled ? "on" : "off")} "
                    + "(off by default, not saved). It copies the depth buffer and reduces it every "
                    + "frame, and nothing reads the result yet. Turn it on, play, and read the "
                    + "hzb line in the log: the question is what it costs, not what it hides. "
                    + "GPU timing " + renderer.DescribeGpuTiming() + ".");
            });

        capi.ChatCommands.Create("dlcavecull")
            .WithDescription("Stop building cavities outside the surface-light and straight-sight envelope. On by default. Changing it rebuilds distant terrain, which takes a few seconds.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");

                if (args.Parsers[0].IsMissing)
                {
                    // The live totals, because "the caves look the same" and "the rule did
                    // nothing" are different claims and only a number separates them. Read
                    // it once with the switch on and once with it off, after each rebuild
                    // has settled: if the geometry does not move, the rule is not running,
                    // and if it moves by a third then it is running and a third is simply
                    // not as much as it looks like it should be.
                    string live = $"cave culling {(renderer.CaveCulling ? "on" : "off")}, "
                        + $"daylight reach {renderer.CaveCullReach} blocks | live geometry "
                        + $"{renderer.LiveGpuMeshBytes / (1024.0 * 1024.0):0.0} MiB, "
                        + $"{renderer.LiveOpaqueVertices:n0} opaque vertices over "
                        + $"{renderer.MeshCount} meshes | {LodMesher.DescribeCaveCullTiming()}";
                    if (LodMesher.AuditSections > 0)
                    {
                        long with = LodMesher.AuditVerticesWithCulling;
                        long without = LodMesher.AuditVerticesWithout;
                        live += $" | AUDIT over {LodMesher.AuditSections} rebuilt sections: "
                            + $"{without:n0} vertices without culling, {with:n0} with "
                            + $"({(without > 0 ? (without - with) * 100.0 / without : 0):0.0}% removed)";
                    }
                    // The reason breakdown goes to the LOG only. It is a block of lines,
                    // chat cannot be copied out of the game, and its whole purpose is to
                    // say which safety rule kept the caves that are still standing - which
                    // is a thing to read afterwards, not a thing to squint at in chat.
                    LogReportLines("caves", live + "\n" + LodCaveCull.DescribeRetention());
                    return TextCommandResult.Success("[Landscapes] " + live
                        + ". Flip it, wait for the rebuild, and run this again - the geometry"
                        + " total is the answer, not the picture. The per-reason breakdown of"
                        + " what survived is in the client log under 'caves:'.");
                }

                string word = ((string)args[0]).ToLowerInvariant();

                if (word != "on" && word != "off")
                    return TextCommandResult.Error("[Landscapes] use: .dlcavecull on | off");

                bool wanted = word == "on";
                if (wanted == renderer.CaveCulling)
                {
                    return TextCommandResult.Success(
                        $"[Landscapes] cave culling is already {word}.");
                }

                renderer.CaveCulling = wanted;
                LodMesher.ResetCaveCullTiming();
                LodCaveCull.ResetRetention();

                // The switch decides what geometry EXISTS, not what is drawn, so nothing
                // changes until the meshes are built again. Doing that here is what makes
                // the comparison something a person can actually make: flip, wait, look.
                int queued = renderer.RemeshAll();

                return TextCommandResult.Success(
                    $"[Landscapes] cave culling {word}. Rebuilding {queued} distant "
                    + "sections - give it a few seconds. On, cavities outside both the daylight "
                    + "envelope and a straight surface sightline are not built. Straight mountain "
                    + "tunnels remain open without preserving hidden networks around bends. What to watch for is "
                    + "terrain that is missing or a cave mouth that has closed up - if you "
                    + "see either, run .dlcavecull off and say where you were looking.");
            });

        capi.ChatCommands.Create("dlwater")
            .WithDescription("How distant water meets the game's own. No argument reports the settings. "
                + "off | a number of chunks | edge <chunks> | fresnel on|off | glint <0-1> | "
                + "foam <0-2> | depth on|off | alpha <0.05-2> | clarity <blocks> | wave <blocks> | "
                + "walls on|off | murk on|off | surface on|off | coverage on|off | "
                + "texture on|off | pipeline oit|opaque | "
                + "junction on|off | log | capture.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("part"),
                      capi.ChatCommands.Parsers.OptionalWord("value"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");

                string part = args.Parsers[0].IsMissing ? "" : ((string)args[0]).ToLowerInvariant();
                string value = args.Parsers[1].IsMissing ? "" : (string)args[1];

                switch (part)
                {
                    case "":
                        // Written to the log as well as to chat. Chat cannot be copied out of
                        // the game, so a setting that only ever reported to chat could not be
                        // read back when a screenshot disagreed with what the code should do.
                        capi.Logger.Notification(
                            "[Landscapes] water settings: " + DescribeWater(renderer));
                        capi.Logger.Notification(
                            "[Landscapes] water depth shading: "
                            + renderer.DescribeWaterAbsorption());
                        return TextCommandResult.Success(
                            $"[Landscapes] {DescribeWater(renderer)}. Use: .dlwater off |"
                            + " a number of CHUNKS | edge <chunks> | fresnel on/off |"
                            + " glint 0-1 | foam 0-2 | murk on/off."
                            + " Distant water hands over to the game's water at the game's own"
                            + " chunk edges and nowhere else - there is no radius any more, in"
                            + " either direction. The chunk count is an off-by-default fallback"
                            + " for water right at your feet, and it steps along chunk edges."
                            + " Glint and foam are the game's own sun reflection and surface"
                            + " noise, carried everywhere distant water is drawn.");

                    case "log":
                        string waterLogReply = "[Landscapes] " + DescribeWater(renderer)
                            + "; absorption " + renderer.DescribeWaterAbsorption()
                            + "; shader " + renderer.WaterShaderStatus
                            + "; " + (horizonEffects?.DescribeLiquidBoundary()
                                ?? "vanilla missing-neighbour water-wall guard unavailable")
                            + "; cached depth draws " + renderer.WaterDepthDraws
                            + "; depth skip " + renderer.WaterDepthSkipReason
                            + "; " + renderer.DescribeWaterPipeline();
                        capi.Logger.Notification(waterLogReply);
                        return TextCommandResult.Success(waterLogReply);

                    case "pipeline":
                        if (value == "oit" || value == "opaque")
                        {
                            renderer.WaterOitEnabled = value == "oit";
                        }
                        string pipelineReply = "[Landscapes] requested water pipeline "
                            + (renderer.WaterOitEnabled ? "oit" : "opaque")
                            + "; " + renderer.DescribeWaterPipeline()
                            + ". Changes next frame, no rebuild. Use .dlwater pipeline oit|opaque; .dlwater log after a short comparison.";
                        capi.Logger.Notification(pipelineReply);
                        return TextCommandResult.Success(pipelineReply);

                    case "shader":
                        return TextCommandResult.Error(
                            "[Landscapes] the legacy water shader is retired. Dedicated "
                            + "water is required; use .dlwater pipeline oit|opaque to compare its stages.");

                    case "junction":
                        if (value != "on" && value != "off")
                            return TextCommandResult.Success($"[Landscapes] junction depth {(renderer.WaterJunctionDepth ? "on" : "off")}, last frame {renderer.WaterJunctionStatus}. Use .dlwater junction on/off. Fills missing boundary depth from cached water geometry. Needs murk, coverage and surface on. Session only.");
                        renderer.WaterJunctionDepth = value == "on";
                        string junctionReply = "[Landscapes] junction depth "
                            + (renderer.WaterJunctionDepth ? "on" : "off")
                            + ". Changes next frame; no rebuild. Depth timing restarted. Session only.";
                        capi.Logger.Notification(junctionReply);
                        return TextCommandResult.Success(junctionReply);

                    case "repair":
                        if (value != "on" && value != "off")
                            return TextCommandResult.Success($"[Landscapes] water-depth hole repair {(renderer.WaterDepthRepair ? "on" : "off")}. Use .dlwater repair on/off. Needs murk and coverage on.");
                        renderer.WaterDepthRepair = value == "on";
                        string repairReply = "[Landscapes] water-depth hole repair "
                            + (renderer.WaterDepthRepair ? "on" : "off") + ". Immediate, no rebuild. Session only.";
                        capi.Logger.Notification(repairReply);
                        return TextCommandResult.Success(repairReply);

                    case "capture":
                        renderer.RequestWaterCapture();
                        return TextCommandResult.Success("[Landscapes] Capturing the next frame's water depth and rendering stages. A brief pause is expected. The saved folder will be written to the client log.");

                    case "murk":
                        if (value != "on" && value != "off")
                            return TextCommandResult.Success(
                                $"[Landscapes] ground murk {(renderer.WaterGroundMurk ? "on" : "off")}. "
                                + "Use .dlwater murk on/off to compare the game's underwater "
                                + "colour and fog on cached ground. Immediate; "
                                + "independent of depth, clarity and water effects. Session only.");
                        renderer.WaterGroundMurk = value == "on";
                        string murkReply = "[Landscapes] ground murk "
                            + (renderer.WaterGroundMurk ? "on" : "off")
                            + ". Look at the seam now; no rebuild needed. Session only.";
                        capi.Logger.Notification(murkReply);
                        return TextCommandResult.Success(murkReply);

                    case "coverage":
                        if (value != "on" && value != "off")
                            return TextCommandResult.Success(
                                $"[Landscapes] cached water depth coverage {(renderer.WaterDepthCoverage ? "on" : "off")}. "
                                + "Use .dlwater coverage on/off. On extends the game's underwater "
                                + "depth image through cached water; off uses vanilla water only. "
                                + $"Last frame: {renderer.WaterDepthDraws} water sections. Needs murk on. Session only.");
                        renderer.WaterDepthCoverage = value == "on";
                        string coverageReply = "[Landscapes] cached water depth coverage "
                            + (renderer.WaterDepthCoverage ? "on" : "off")
                            + ". Takes effect next frame. Needs murk on. Session only.";
                        capi.Logger.Notification(coverageReply);
                        return TextCommandResult.Success(coverageReply);

                    case "surface":
                        if (value != "on" && value != "off")
                            return TextCommandResult.Success(
                                $"[Landscapes] cached water surface {(renderer.WaterSurfaceVisible ? "on" : "off")}. "
                                + "Use .dlwater surface on/off to remove only Horizons water. "
                                + "Ground, plants and the game's water keep drawing. Session only.");
                        renderer.WaterSurfaceVisible = value == "on";
                        string surfaceReply = "[Landscapes] cached water surface "
                            + (renderer.WaterSurfaceVisible ? "on" : "off")
                            + ". Look at the band now; no rebuild needed. Session only.";
                        capi.Logger.Notification(surfaceReply);
                        return TextCommandResult.Success(surfaceReply);

                    case "texture":
                        if (value != "on" && value != "off")
                            return TextCommandResult.Success(
                                $"[Landscapes] cached water texture {renderer.WaterSurfaceTextureStatus}. "
                                + "Use .dlwater texture on/off. This applies the game's live "
                                + "water tile and its alpha to ordinary cached water. Immediate; "
                                + "dedicated shader only; session only.");
                        renderer.WaterSurfaceTexture = value == "on";
                        string textureReply = "[Landscapes] cached water texture "
                            + renderer.WaterSurfaceTextureStatus
                            + ". Look at the broad boundary band now; no rebuild needed. "
                            + "Dedicated shader only; session only.";
                        capi.Logger.Notification(textureReply);
                        return TextCommandResult.Success(textureReply);

                    case "edge":
                        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out int edge))
                        {
                            return TextCommandResult.Success(
                                $"[Landscapes] water edge {renderer.WaterEdgeChunks} "
                                + "(-1 derives it). How many whole chunks further out than "
                                + "distant GROUND distant water stops. Ground deliberately "
                                + "overlaps the game's terrain to guard against holes; water "
                                + "does not need that and gives it back. Raise it by one if "
                                + "distant water reaches into the game's own water, lower it "
                                + "by one if there is a strip with no water in it at all.");
                        }
                        renderer.WaterEdgeChunks = edge;
                        return TextCommandResult.Success(
                            $"[Landscapes] water edge {renderer.WaterEdgeChunks}. "
                            + "Takes a moment to settle as ownership moves - look at a "
                            + "shoreline at distance.");

                    case "fresnel":
                        if (value is not ("on" or "off"))
                            return TextCommandResult.Success(
                                $"[Landscapes] opacity {(renderer.WaterFresnel ? "on" : "off")}. "
                                + "On gives distant water the game's own opacity, which changes with "
                                + "the angle you look at it: see-through underfoot, solid further "
                                + "out. Off makes it one flat value at every distance, which is what "
                                + "let the lake bottom show through where the game's water is solid.");
                        renderer.WaterFresnel = value == "on";
                        SaveConfig();
                        return TextCommandResult.Success(
                            $"[Landscapes] {DescribeWater(renderer)}. Look now.");

                    case "glint":
                        if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float glint))
                        {
                            return TextCommandResult.Success(
                                $"[Landscapes] glint {renderer.WaterGlint:0.00}. 0 to 1. How much "
                                + "of the game's sun reflection distant water carries. The game's "
                                + "water is shiny out to its own horizon, so this is carried "
                                + "everywhere rather than faded out - a term that ends somewhere "
                                + "draws a line where it ends. 0 turns it off.");
                        }
                        renderer.WaterGlint = glint;
                        return TextCommandResult.Success(
                            $"[Landscapes] glint {renderer.WaterGlint:0.00}. Look now.");

                    case "foam":
                        if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float foam))
                        {
                            return TextCommandResult.Success(
                                $"[Landscapes] foam {renderer.WaterFoam:0.00}. 0 to 2. One is the game's "
                                + "own surface noise on open water, carried everywhere distant water "
                                + "is drawn; values above one amplify it to compare a small residual "
                                + "handoff mismatch. 0 turns it off.");
                        }
                        renderer.WaterFoam = foam;
                        return TextCommandResult.Success(
                            $"[Landscapes] foam {renderer.WaterFoam:0.00}. Look now.");

                    case "depth":
                        if (value != "on" && value != "off")
                            return TextCommandResult.Success(
                                $"[Landscapes] depth shading "
                                + $"{(renderer.WaterDepthShading ? "on" : "off")}. "
                                + "On darkens ground under water by how deep it is, the way the "
                                + "game does: water swallows one light level a block, so a bed "
                                + "under eight blocks is at 70% of the shore beside it and one "
                                + "under twenty is at 26%. Off draws a lakebed as brightly as an "
                                + "open field, which is what every build before this one did. "
                                + "Rebuilds every loaded section.");
                        renderer.WaterDepthShading = value == "on";
                        SaveConfig();
                        // The resolved table goes to the log every time, because "no effect"
                        // and "applying 1.00 at every depth" are the same picture.
                        capi.Logger.Notification("[Landscapes] water depth shading: "
                            + renderer.DescribeWaterAbsorption());
                        return TextCommandResult.Success(
                            $"[Landscapes] depth shading "
                            + $"{(renderer.WaterDepthShading ? "on" : "off")}. Rebuilding. "
                            + renderer.DescribeWaterAbsorption()
                            + ". Written to the log too.");

                    case "walls":
                        if (value != "on" && value != "off")
                            return TextCommandResult.Success(
                                $"[Landscapes] water walls "
                                + $"{(renderer.SuppressUnknownWaterWalls ? "OFF at unknown edges" : "on")}. "
                                + "Where the cache has no record of what is beside a body of "
                                + "water, the mesher draws the SIDE of that water - a sheet "
                                + "from the surface down to the seabed. It is meant for the "
                                + "real edge of explored water, and it is also drawn against "
                                + "any single column that was never captured, including inside "
                                + "a loaded section. Off drops those. This is a diagnostic: it "
                                + "is here to find out whether that is what the band at the "
                                + "seam is. Rebuilds every loaded section.");
                        renderer.SuppressUnknownWaterWalls = value == "off";
                        SaveConfig();
                        return TextCommandResult.Success(
                            $"[Landscapes] water walls "
                            + $"{(renderer.SuppressUnknownWaterWalls ? "OFF at unknown edges" : "on")}. "
                            + "Rebuilding - give it a few seconds, then look at the seam.");

                    case "wave":
                        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out int strip))
                        {
                            return TextCommandResult.Success(
                                $"[Landscapes] wave strip {renderer.WaterSeamStripBlocks} "
                                + "blocks. The game does not draw water flat - it pushes every "
                                + "liquid vertex up and down, a tenth of a block on a pond and "
                                + "up to a whole one on the open sea - and ours was flat, so "
                                + "where they meet the game's surface dips below ours and leaves "
                                + "an opening. This is how wide a strip of water keeps one quad "
                                + "per block so it can carry the same wave. The seam can only "
                                + "land on a 32-block chunk edge, so 1 covers it for about 1% of "
                                + "the mesh where doing it everywhere costs 16%. 0 turns the "
                                + "wave off. Rebuilds every loaded section.");
                        }
                        renderer.WaterSeamStripBlocks = strip;
                        SaveConfig();
                        return TextCommandResult.Success(
                            $"[Landscapes] wave strip {renderer.WaterSeamStripBlocks} blocks. "
                            + "Rebuilding.");

                    case "clarity":
                        if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float clarity))
                        {
                            return TextCommandResult.Success(
                                $"[Landscapes] clarity {renderer.WaterClarity:0} blocks. "
                                + "How far light gets back UP through the water to your eye. "
                                + "The game's own light only models the sunlight going DOWN, one "
                                + "level a block; the trip back is what its see-through surface "
                                + "does per pixel and a shaded bed cannot. Smaller is murkier and "
                                + "goes dark sooner. Rebuilds every loaded section.");
                        }
                        renderer.WaterClarity = clarity;
                        SaveConfig();
                        capi.Logger.Notification("[Landscapes] water depth shading: "
                            + renderer.DescribeWaterAbsorption());
                        return TextCommandResult.Success(
                            $"[Landscapes] clarity {renderer.WaterClarity:0} blocks. "
                            + "Rebuilding. " + renderer.DescribeWaterAbsorption());

                    case "alpha":
                        if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float maxAlpha))
                        {
                            return TextCommandResult.Success(
                                $"[Landscapes] alpha {renderer.WaterMaxAlpha:0.00}. 0.05 to 2. "
                                + "This caps the pre-texture Fresnel alpha. 2 is vanilla's own "
                                + "ceiling; lower values deliberately leave more of the darkened "
                                + "distant bed visible.");
                        }
                        renderer.WaterMaxAlpha = GameMath.Clamp(maxAlpha, 0.05f, 2f);
                        SaveConfig();
                        return TextCommandResult.Success(
                            $"[Landscapes] alpha {renderer.WaterMaxAlpha:0.00}. Look now.");

                    default:
                        int chunks;
                        if (part == "off") chunks = 0;
                        else if (!int.TryParse(part, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out chunks))
                        {
                            return TextCommandResult.Error(
                                "[Landscapes] use: .dlwater off | a number of chunks | "
                                + "edge <chunks> | fresnel on/off | glint 0-1 | foam 0-2 | "
                                + "depth on/off | alpha 0.05-2 | clarity <blocks> | wave <blocks> | "
                                + "walls on/off");
                        }

                        renderer.WaterNearChunks = chunks;
                        return TextCommandResult.Success(
                            $"[Landscapes] {DescribeWater(renderer)}. No rebuild needed - look now.");
                }
            });

        capi.ChatCommands.Create("dlslabs")
            .WithDescription("Draw terrain slabs at half height. 'surface' is the default low-cost mode throughout level 0, independent of the blend band; 'near' also includes caves. Changing it rebuilds distant terrain.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("levels"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");

                if (args.Parsers[0].IsMissing)
                {
                    return TextCommandResult.Success(
                        $"[Landscapes] half-block terrain: {DescribeSlabs(renderer)} "
                        + $"| live geometry {renderer.LiveOpaqueVertices:n0} opaque vertices over "
                        + $"{renderer.MeshCount} meshes. Use: .dlslabs off | surface | near | all. "
                        + "Surface and near cover all of level 0, not just the blend band. "
                        + "Read the vertex total on each setting after the rebuild settles - "
                        + "that number is the cost, and the picture is the benefit.");
                }

                string word = ((string)args[0]).ToLowerInvariant();
                bool surfaceOnly = word == "surface";
                int wanted = word switch
                {
                    "off" => 0,
                    "surface" => LodTerrainRenderer.NearHalfBlockLevels,
                    "near" => LodTerrainRenderer.NearHalfBlockLevels,
                    "all" => 8,
                    _ => int.TryParse(word, out int levels) ? levels : -1,
                };
                if (wanted < 0)
                    return TextCommandResult.Error(
                        "[Landscapes] use: .dlslabs off | surface | near | all");

                renderer.HalfBlockLevels = wanted;
                renderer.SurfaceHalfBlocksOnly = surfaceOnly;
                SaveConfig();
                int queued = renderer.RemeshAll();

                return TextCommandResult.Success(
                    $"[Landscapes] half-block terrain: {DescribeSlabs(renderer)}. "
                    + $"Rebuilding {queued} distant sections - give it a few seconds. Mods that "
                    + "smooth the landscape with half blocks are otherwise rebuilt as full cubes "
                    + "out here, which puts the steps they removed back. What to watch for is the "
                    + "far ground looking chunkier than the ground at your feet, and whether the "
                    + "vertex total above is worth the difference.");
            });

        // A geometry cost with no picture attached, so it needs a switch that reports the
        // vertex total the same way .dlslabs does. The fix is invisible except at the seam it
        // exists for; the cost is not, and only a paired reading can weigh them.
        capi.ChatCommands.Create("dlcutout")
            .WithDescription("Let crops, tall grass and vines hide the terrain behind them again. "
                + "Off by default, which is correct. Changing it rebuilds distant terrain.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");

                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[Landscapes] plants hiding what is behind them: "
                        + $"{(renderer.CutoutBlocksCover ? "ON - the old behaviour" : "off - correct")} "
                        + $"| live geometry {renderer.LiveOpaqueVertices:n0} opaque vertices over "
                        + $"{renderer.MeshCount} meshes. The game draws a crop as a see-through "
                        + "tuft, so a crop hides nothing; distant terrain draws it as a solid "
                        + "block, and letting that block hide its neighbours opened a hole you "
                        + "could see the sky through where the two terrains meet. Read the vertex "
                        + "total on each setting after the rebuild settles - that is the whole "
                        + "cost, and it buys a seam with no holes in it.");

                renderer.CutoutBlocksCover = (bool)args[0];
                int queued = renderer.RemeshAll();
                return TextCommandResult.Success(
                    $"[Landscapes] plants hiding what is behind them: "
                    + $"{(renderer.CutoutBlocksCover ? "ON - the old behaviour" : "off - correct")}. "
                    + $"Rebuilding {queued} distant sections - give it a few seconds, then read "
                    + ".dlcutout again for the vertex total.");
            });

        // The live toggle the aggregate bound was funded with. Unlike the retired staging
        // switches this selects nothing half-built: both sides are shipping renderers, and
        // the whole point of the item is a comparison somebody has to be able to make from
        // inside the view that shows it.
        capi.ChatCommands.Create("dlsubtree")
            .WithDescription("Cull whole quadtree branches by how tall the terrain under them really is. Default on. `.dlsubtree` reports what it rejected; `heights` reports how tall resident terrain claims to be; `reset` zeroes the count so one view can be measured on its own.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");

                if (args.Parsers[0].IsMissing)
                {
                    // Into the log as well as the screen, for the same reason .dlhzb does
                    // it: chat cannot be copied out of the game, and these are lines of
                    // digits that would otherwise have to be retyped or photographed.
                    string report = renderer.ReportSubtreeCulling();
                    LogReportLines("subtree", report);
                    return TextCommandResult.Success("[Landscapes] " + report);
                }

                string word = ((string)args[0]).ToLowerInvariant();

                // The measurement that decides whether tightening these boxes further is
                // worth anything. Read it once after a world has settled, not on the way in.
                if (word == "heights")
                {
                    string heights = renderer.ReportLiveSectionHeights();
                    LogReportLines("subtree heights", heights);
                    return TextCommandResult.Success("[Landscapes] " + heights);
                }

                if (word == "reset")
                {
                    renderer.ResetSubtreeCullingInterval();
                    return TextCommandResult.Success(
                        "[Landscapes] subtree cull counters zeroed. Hold a view for a few "
                        + "seconds, then run .dlsubtree to see what that view alone rejected.");
                }

                if (word != "on" && word != "off")
                    return TextCommandResult.Error(
                        "[Landscapes] use: .dlsubtree on | off | reset | heights");

                renderer.SubtreeHeightCulling = word == "on";
                renderer.ResetSubtreeCullingInterval();

                return TextCommandResult.Success(
                    $"[Landscapes] subtree height culling {(word == "on" ? "on" : "off")} "
                    + "(default on, not saved). On, a branch of the quadtree is tested against how "
                    + "tall the terrain under it actually is; off, against a box running from "
                    + "bedrock to sky, which is what every branch used before this. The failure "
                    + "to watch for is terrain missing when you look up, look down, or fly high - "
                    + "if you see that, run .dlsubtree off and say whether it comes back.");
            });

        capi.ChatCommands.Create("dlfront")
            .WithDescription("Submit cached opaque terrain front-to-back. On by default and not saved.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[Landscapes] opaque front-to-back submission {(renderer.OpaqueFrontToBack ? "on" : "off")} (on by default, not saved).");

                renderer.OpaqueFrontToBack = (bool)args[0];
                return TextCommandResult.Success(
                    $"[Landscapes] opaque front-to-back submission {(renderer.OpaqueFrontToBack ? "on" : "off")} (on by default, not saved). " +
                    "Applies on the next frame; water keeps its existing order.");
            });

        capi.ChatCommands.Create("dlocclusion")
            .WithDescription("Draw cached terrain after vanilla so ordinary depth testing rejects hidden pixels. On by default.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[Landscapes] post-vanilla depth culling {renderer.DescribeOcclusionCulling()}. " +
                        "On by default and not saved.");

                renderer.OcclusionCullingEnabled = (bool)args[0];
                return TextCommandResult.Success(
                    $"[Landscapes] post-vanilla depth culling {renderer.DescribeOcclusionCulling()}. " +
                    "Applies on the next frame; on by default and not saved.");
            });

        capi.ChatCommands.Create("dltemporal")
            .WithDescription("Skip opaque cached meshes proven hidden on later frames. Aggressive profile on by default.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[Landscapes] delayed exact-geometry occlusion {renderer.DescribeTemporalOcclusion()}. " +
                        "Aggressive profile on by default; not saved.");

                renderer.TemporalOcclusionEnabled = (bool)args[0];
                return TextCommandResult.Success(
                    $"[Landscapes] delayed exact-geometry occlusion {renderer.DescribeTemporalOcclusion()}. " +
                    "Applies on the next frame; aggressive profile is the default. Use it with .dlocclusion on. " +
                    "Tune live with .dltemporalprofile safe|aggressive|extreme.");
            });

        capi.ChatCommands.Create("dltemporalprofile")
            .WithDescription("Tune delayed occlusion: safe invalidates on turns; aggressive/extreme persist while turning.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWordRange(
                "profile", ["safe", "aggressive", "extreme"]))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[Landscapes] delayed occlusion profile: {renderer.TemporalOcclusionProfileName}. " +
                        "Safe invalidates on turns; aggressive persists through turns and rechecks quickly; " +
                        "extreme persists through all camera motion. Changes are not saved.");

                string profile = (string)args[0];
                if (!renderer.SetTemporalOcclusionProfile(profile))
                    return TextCommandResult.Error(
                        "[Landscapes] profile must be safe, aggressive, or extreme.");
                return TextCommandResult.Success(
                    $"[Landscapes] delayed occlusion profile: {renderer.TemporalOcclusionProfileName}. " +
                    "Previous visibility was invalidated; the new profile applies immediately.");
            });

        capi.ChatCommands.Create("dlholes")
            .WithDescription("Find ground the mod has handed to the game that the game is not drawing. No need to look at anything.")
            .HandleWith(_ =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");

                var at = capi.World.Player.Entity.Pos;
                // Logged as well as shown, like .dlwhy (G72).
                string holes = renderer.ExplainOwnedButNotDrawn(at.X, at.Z, 4);
                LogReportLines("holes", holes);
                return TextCommandResult.Success("[Landscapes] " + holes);
            });

        capi.ChatCommands.Create("dlgeom")
            .WithDescription("While the chunk mask is on: wait for an uploaded vanilla mesh before first handing it terrain. On by default.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalBool("on"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");
                if (args.Parsers[0].IsMissing)
                    return TextCommandResult.Success(
                        $"[Landscapes] geometry rule {(renderer.GeometryOwnershipRule ? "on" : "off")}. " +
                        "On delays a new handoff until vanilla holds uploaded terrain geometry; " +
                        "it never revokes an established handoff. Only has an effect with .dlmask on.");

                renderer.GeometryOwnershipRule = (bool)args[0];
                return TextCommandResult.Success(
                    $"[Landscapes] geometry rule {(renderer.GeometryOwnershipRule ? "on" : "off")}. " +
                    "The change applies on the next frame; new ownership settles over the next second or two.");
            });

        // Session-only. The two rules produce a visibly different seam, and the only honest
        // comparison is standing at one and switching, so this has to be live; persisting it
        // would let a saved setting silently disagree with what the build says it does.
        capi.ChatCommands.Create("dlexact")
            .WithDescription("Use the game's own measurement of how far it draws each chunk, "
                + "instead of a safe estimate. No argument reports it; 'map' writes the "
                + "measurement to the client log; 'audit' checks the mask against what the game "
                + "is actually drawing, chunk by chunk. on | off | map | audit.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("part"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");

                string part = args.Parsers[0].IsMissing ? "" : ((string)args[0]).ToLowerInvariant();
                switch (part)
                {
                    case "on":
                    case "off":
                        renderer.ExactDrawRange = part == "on";
                        return TextCommandResult.Success(
                            $"[Landscapes] exact draw range {part}. "
                            + (part == "on"
                                ? "Distant terrain now stops exactly where the game's own terrain "
                                + "starts, instead of overlapping the last two chunks of it."
                                : "Back to the safe estimate: distant terrain overlaps the last "
                                + "two chunks of the game's own. Settles over the next second.")
                            + " " + renderer.DescribeExactRange());

                    case "map":
                        // The chat window cannot hold this and the owner cannot copy from it,
                        // so it goes to the log and chat gets the one-line answer (G72).
                        return TextCommandResult.Success("[Landscapes] " + renderer.LogExactRangeMap());

                    // The only report that asks the GAME what it is drawing rather than asking
                    // the mod what it believes. Everything else here can agree with itself and
                    // still be wrong about the picture.
                    case "audit":
                        return TextCommandResult.Success("[Landscapes] " + renderer.LogCoverageAudit());

                    default:
                        return TextCommandResult.Success(
                            "[Landscapes] " + renderer.DescribeExactRange()
                            + " On means ownership is decided from the bounding sphere the game "
                            + "itself measures each chunk by, so distant terrain gives ground back "
                            + "at the exact chunk the game stops drawing. Off estimates that "
                            + "boundary and keeps a two-chunk overlap as a hole guard. "
                            + "'.dlexact map' writes the measurement to the client log.");
                }
            });

        capi.ChatCommands.Create("dldetail")
            .WithDescription("LOD thresholds. auto (the default) chooses them from your field of view "
                + "and render resolution so every level hands over at the same size on screen; "
                + "<blocks> sets a fixed doubling sequence from that distance and switches automatic "
                + "off; manual goes back to your own distances; target <pixels> sets the on-screen "
                + "size automatic mode aims for. Saved; automatic is also a switch in .dlconfig.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("mode"),
                      capi.ChatCommands.Parsers.OptionalWord("value"))
            .HandleWith(args =>
            {
                string mode = args.Parsers[0].IsMissing ? "" : ((string)args[0]).Trim();
                string reply;
                if (mode.Length == 0)
                {
                    reply = "[Landscapes] " + DescribeLodThresholds();
                }
                else if (mode.Equals("auto", StringComparison.OrdinalIgnoreCase))
                {
                    renderer.AutoLodThresholds = true;
                    SaveConfig();
                    reply = "[Landscapes] automatic LOD thresholds on (saved); terrain "
                        + "re-selects over the next few seconds. " + DescribeLodThresholds();
                }
                else if (mode.Equals("manual", StringComparison.OrdinalIgnoreCase))
                {
                    renderer.AutoLodThresholds = false;
                    LodWorld.SetLevelThresholds(LodWorld.NormalizeLevelThresholds(
                        config.LodThresholds, config.DetailDistance));
                    SaveConfig();
                    reply = "[Landscapes] automatic LOD thresholds off; back to your own "
                        + "distances (saved). " + DescribeLodThresholds();
                }
                else if (mode.Equals("target", StringComparison.OrdinalIgnoreCase))
                {
                    string value = args.Parsers[1].IsMissing ? "" : (string)args[1];
                    if (TryParseSetting(value, out float pixels))
                    {
                        renderer.LodTargetPixels = pixels;
                        SaveConfig();
                        reply = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                            $"[Landscapes] automatic LOD target {renderer.LodTargetPixels:0.00} px (saved). ")
                            + DescribeLodThresholds();
                    }
                    else
                    {
                        reply = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                            $"[Landscapes] automatic LOD target {renderer.LodTargetPixels:0.00} px. "
                            + $"Give {LodAutoThresholds.MinTargetPixels:0.#} to "
                            + $"{LodAutoThresholds.MaxTargetPixels:0.#}: smaller keeps finer detail "
                            + $"further out, larger saves GPU time.");
                    }
                }
                else if (int.TryParse(mode, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int blocks))
                {
                    renderer.AutoLodThresholds = false;
                    LodWorld.DetailDistance = GameMath.Clamp(blocks,
                        (int)LodWorld.MinDetailDistance, (int)LodWorld.MaxDetailDistance);
                    SaveConfig();
                    reply = "[Landscapes] LOD thresholds reset to "
                        + $"{string.Join(", ", LodWorld.GetLevelThresholds())} (saved; automatic "
                        + "off). Terrain re-selects over the next few seconds.";
                }
                else
                {
                    reply = "[Landscapes] .dldetail auto | manual | target <pixels> | <blocks>. "
                        + DescribeLodThresholds();
                }
                capi.Logger.Notification(reply);
                return TextCommandResult.Success(reply);
            });

        capi.ChatCommands.Create("dltaa")
            .WithDescription("Distant anti-aliasing: blends distant cached terrain over several frames "
                + "so it stops shimmering as you move. on | off | show | reach <1-32> | "
                + "nudge <0-1> | sharpen <0-1> | blend <0-0.97> | still <0-0.99> | clip <0.5-4> | "
                + "reset. reach is the on-screen block width, in pixels, where it begins (larger "
                + "reaches closer); nudge is how far terrain is moved each frame (lower is crisper "
                + "but shimmers more); sharpen restores contrast on the way to the screen; blend "
                + "and still are the memory while turning and once the camera is still (higher is "
                + "steadier but smears more); clip is the safety check's width (lower smears less "
                + "but flickers more). On by default and everything is saved; also a switch in "
                + ".dlconfig; reset puts the dials back. With no argument, reports what it is doing "
                + "and what it costs, in chat and in the client log.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("mode"),
                      capi.ChatCommands.Parsers.OptionalWord("value"))
            .HandleWith(args =>
            {
                string mode = args.Parsers[0].IsMissing ? "" : ((string)args[0]).Trim();
                string value = args.Parsers[1].IsMissing ? "" : ((string)args[1]).Trim();
                string reply;

                // One tuning dial: set it when a value was given, save, and reply with them all.
                string Dial(string name, string range, Action<float> set)
                {
                    if (!TryParseSetting(value, out float setting))
                        return $"[Landscapes] give .dltaa {name} a value from {range}. "
                            + renderer.DescribeTaaDials() + ".";
                    set(setting);
                    SaveConfig();
                    return "[Landscapes] " + renderer.DescribeTaaDials() + ". Saved.";
                }

                if (mode.Equals("on", StringComparison.OrdinalIgnoreCase)
                    || mode.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    renderer.DistantAntiAliasing = mode.Equals("on", StringComparison.OrdinalIgnoreCase);
                    SaveConfig();
                    reply = "[Landscapes] " + renderer.DescribeDistantAntiAliasing() + " Saved.";
                }
                else if (mode.Equals("show", StringComparison.OrdinalIgnoreCase))
                {
                    renderer.TaaShowWeight = !renderer.TaaShowWeight;
                    reply = "[Landscapes] anti-aliased pixels "
                        + (renderer.TaaShowWeight
                            ? "tinted magenta by strength; nothing else is touched. "
                            : "shown normally. ")
                        + "Session only.";
                }
                else if (mode.Equals("nudge", StringComparison.OrdinalIgnoreCase))
                    reply = Dial("nudge", "0 to 1", v => renderer.TaaNudge = v);
                else if (mode.Equals("sharpen", StringComparison.OrdinalIgnoreCase))
                    reply = Dial("sharpen", "0 to 1", v => renderer.TaaSharpen = v);
                else if (mode.Equals("blend", StringComparison.OrdinalIgnoreCase))
                    reply = Dial("blend", "0 to 0.97", v => renderer.TaaFeedback = v);
                else if (mode.Equals("still", StringComparison.OrdinalIgnoreCase))
                    reply = Dial("still", "0 to 0.99", v => renderer.TaaStillFeedback = v);
                else if (mode.Equals("clip", StringComparison.OrdinalIgnoreCase))
                    reply = Dial("clip", "0.5 to 4", v => renderer.TaaClip = v);
                else if (mode.Equals("reset", StringComparison.OrdinalIgnoreCase))
                {
                    renderer.ResetTaaDials();
                    SaveConfig();
                    reply = "[Landscapes] anti-aliasing back to its defaults: "
                        + renderer.DescribeTaaDials() + "; " + renderer.DescribeTaaReach() + ". Saved.";
                }
                else if (mode.Equals("reach", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseSetting(value, out float reach))
                    {
                        renderer.TaaReachPixels = reach;
                        SaveConfig();
                        reply = "[Landscapes] " + renderer.DescribeTaaReach() + ". Saved.";
                    }
                    else
                    {
                        reply = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                            $"[Landscapes] {renderer.DescribeTaaReach()}. Give "
                            + $"{LodTaaMath.MinReachPixels:0} to {LodTaaMath.MaxReachPixels:0} px: larger "
                            + $"reaches closer (default {LodTaaMath.DefaultReachPixels:0.#}).");
                    }
                }
                else
                {
                    reply = "[Landscapes] " + renderer.DescribeDistantAntiAliasing();
                }
                capi.Logger.Notification(reply);
                return TextCommandResult.Success(reply);
            });
    }

    /// <summary>The thresholds in force, and where they come from.</summary>
    string DescribeLodThresholds()
    {
        string live = string.Join(", ", LodWorld.GetLevelThresholds());
        return renderer.AutoLodThresholds
            ? $"LOD thresholds {live}; automatic: {renderer.DescribeAutoThresholds()}."
            : $"LOD thresholds {live}; fixed. '.dldetail auto' chooses them from your field of "
                + "view and resolution instead.";
    }

    /// <summary>A number typed in chat, with either decimal separator.</summary>
    static bool TryParseSetting(string text, out float value) =>
        float.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value)
        || float.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.CurrentCulture, out value);

    void ApplyGuiConfig(int[] thresholds, bool autoThresholds, int farViewDistanceCap,
        bool loadWhilePaused, LodVisualSettings visuals, LodTaaDials taaDials,
        LodSilhouetteSettings silhouetteSettings)
    {
        // The fixed list is the player's own and is kept whichever mode is chosen; automatic mode
        // only stops it being the one in force.
        int[] normalized = LodWorld.NormalizeLevelThresholds(thresholds, config.DetailDistance);
        config.LodThresholds = normalized;
        config.DetailDistance = normalized[0];
        config.AutoLodThresholds = autoThresholds;
        config.FarViewDistanceCap = farViewDistanceCap;
        config.LoadWhilePaused = loadWhilePaused;
        WriteSilhouetteSettings(silhouetteSettings);

        if (renderer != null)
        {
            renderer.AutoLodThresholds = autoThresholds;
            if (!autoThresholds) LodWorld.SetLevelThresholds(normalized);
            renderer.FarViewDistanceCap = farViewDistanceCap;
            renderer.ContinueLoadingWhilePaused = loadWhilePaused;
            ApplyVisualSettings(visuals);
            renderer.TaaDials = taaDials;
            ApplySilhouetteSettings(silhouetteSettings);
        }
        else
        {
            // No renderer to set, so the file is the only place the choice can live until one
            // exists. SaveConfig leaves every renderer-derived key alone without a renderer, so
            // writing them here is what makes the window work at all in that state.
            WriteVisualSettings(visuals);
            WriteTaaDials(taaDials);
        }

        SaveConfig();
    }

    /// <summary>
    /// The settings window's Anti-Aliasing tab, showing a slider's effect as it moves. Nothing is
    /// saved here; Save goes through <see cref="ApplyGuiConfig"/>, and closing without it calls
    /// this again with what was in force when the window opened. Every dial it carries is on
    /// the side panel under its own `.dltaa` name.
    /// </summary>
    void PreviewTaaTuning(LodTaaDials dials, bool show)
    {
        if (renderer == null) return;
        renderer.TaaDials = dials;
        renderer.TaaShowWeight = show;
    }

    /// <summary>The renderer's current visual settings, as the window shows them.</summary>
    LodVisualSettings CurrentVisualSettings() => renderer == null
        ? ConfiguredVisualSettings()
        : new LodVisualSettings(
            BlendPipeline: renderer.BlendPipelineEnabled,
            SeamBand: renderer.BandEnabled,
            BlockTextures: renderer.BlockTexturesEnabled,
            ClimateTint: renderer.ClimateFieldTint,
            ClimateTintRange: renderer.ClimateFadeBlocks,
            LeafSeasonColours: renderer.LeafSeasonVariation > 0f,
            HalfBlockTerrain: renderer.HalfBlockLevels > 0,
            WaterEffects: renderer.WaterFresnel,
            DistantAntiAliasing: renderer.DistantAntiAliasing);

    /// <summary>What the config file says, for startup and for when no renderer exists.</summary>
    LodVisualSettings ConfiguredVisualSettings() => new(
        BlendPipeline: config.BlendPipeline,
        SeamBand: config.SeamBand,
        BlockTextures: config.BlockTextures,
        ClimateTint: config.ClimateTint,
        ClimateTintRange: Math.Max(0f, config.ClimateTintRange),
        LeafSeasonColours: config.LeafSeasonColours,
        HalfBlockTerrain: config.HalfBlockTerrain,
        WaterEffects: config.WaterEffects,
        DistantAntiAliasing: config.DistantAntiAliasing);

    /// <summary>What the config file says for the anti-aliasing dials, for when no renderer exists.</summary>
    LodTaaDials ConfiguredTaaDials() => new(config.TaaReachPixels, config.TaaNudge,
        config.TaaSharpen, config.TaaBlend, config.TaaStillBlend, config.TaaClip);

    /// <summary>The anti-aliasing dials straight into the file, when no renderer exists to hold them.</summary>
    void WriteTaaDials(LodTaaDials dials)
    {
        config.TaaReachPixels = dials.Reach;
        config.TaaNudge = dials.Nudge;
        config.TaaSharpen = dials.Sharpen;
        config.TaaBlend = dials.Blend;
        config.TaaStillBlend = dials.Still;
        config.TaaClip = dials.Clip;
    }

    void WriteVisualSettings(LodVisualSettings v)
    {
        config.BlendPipeline = v.BlendPipeline;
        config.SeamBand = v.SeamBand;
        config.BlockTextures = v.BlockTextures;
        config.ClimateTint = v.ClimateTint;
        config.ClimateTintRange = v.ClimateTintRange;
        config.LeafSeasonColours = v.LeafSeasonColours;
        config.HalfBlockTerrain = v.HalfBlockTerrain;
        config.WaterEffects = v.WaterEffects;
        config.DistantAntiAliasing = v.DistantAntiAliasing;
    }

    /// <summary>
    /// Push a whole visual settings value into the renderer, re-meshing once if the terrain's
    /// SHAPE changed.
    ///
    /// <para>
    /// Only two of these change geometry - the blend pipeline, because a textured quad carries
    /// a slot instead of a colour, and half-block terrain, because a slab's surface sits half a
    /// block lower and needs the walls around it. The rest are shader uniforms and take effect
    /// on the next frame. Re-meshing for those too would stall the client for a second on a
    /// setting that did not need it.
    /// </para>
    /// </summary>
    void ApplyVisualSettings(LodVisualSettings v)
    {
        bool shapeChanged = renderer.HalfBlockLevels > 0 != v.HalfBlockTerrain;

        ApplyBlendPipeline(v.BlendPipeline);
        renderer.BandEnabled = v.SeamBand;
        renderer.BlockTexturesEnabled = v.BlockTextures;
        renderer.ClimateFieldTint = v.ClimateTint;
        renderer.ClimateFadeBlocks = Math.Max(0f, v.ClimateTintRange);
        renderer.LeafSeasonVariation = v.LeafSeasonColours ? 1f : 0f;
        renderer.HalfBlockLevels =
            v.HalfBlockTerrain ? LodTerrainRenderer.NearHalfBlockLevels : 0;
        renderer.SurfaceHalfBlocksOnly = true;
        renderer.WaterFresnel = v.WaterEffects;
        renderer.WaterGlint = v.WaterEffects ? 1f : 0f;
        renderer.WaterFoam = v.WaterEffects ? 1.6f : 0f;
        // A pass after the terrain, not a change to it: no re-mesh.
        renderer.DistantAntiAliasing = v.DistantAntiAliasing;

        if (shapeChanged) renderer.RemeshAll();
    }

    /// <summary>
    /// Keep the owner-thread palette source and render-side packed-quad interpretation in
    /// lockstep. Turning on may require a bounded resident-palette repair before textured
    /// jobs are safe to publish; turning off immediately stops producing texture slots.
    /// </summary>
    void ApplyBlendPipeline(bool enabled)
    {
        if (!enabled)
        {
            renderer.BlendPipelineEnabled = false;
            pipeline.SetTextureSlotsEnabled(false);
            renderer.TextureSlotSourceReady = false;
            return;
        }

        pipeline.SetTextureSlotsEnabled(true);
        renderer.TextureSlotSourceReady = pipeline.TextureSlotsPrepared;
        renderer.BlendPipelineEnabled = true;
    }

    /// <summary>
    /// Put the seam-fog dials back to the current model's defaults once, when the file was
    /// written for an older fog. The on/off switch is kept. A file that could not be read is
    /// never written here, and a fresh install only records the model. From models 2 and 3
    /// only the densities change: they are the dials whose meaning moved, and the rest are the
    /// player's tuning of the same bank's shape.
    /// </summary>
    void ResetOutdatedFogDials(bool fromFile)
    {
        if (config.SilhouetteFogModel >= LodSilhouetteFogVolume.FogModel) return;
        LodSilhouetteSettings defaults = LodSilhouetteSettings.Default;
        int previous = config.SilhouetteFogModel;
        config.SilhouetteFogStrength = defaults.FogStrength;
        config.SilhouetteFogSeamStrength = defaults.FogSeamStrength;
        config.SilhouetteFogModel = LodSilhouetteFogVolume.FogModel;
        if (previous is 2 or 3)
        {
            if (!fromFile) return;
            capi.StoreModConfig(config, "dodenslandscapes.json");
            Mod.Logger.Notification(
                "[Landscapes] the seam fog now hides the seam with a shallow strip and "
                + "leaves the bank above it see-through. Its two densities, the strip's and the "
                + "bank's, were set once to {0:0}% and {1:0}%; your other fog dials are unchanged.",
                defaults.FogSeamStrength * 100f, defaults.FogStrength * 100f);
            return;
        }
        config.SilhouetteFogCoverage = defaults.FogCoverage;
        config.SilhouetteFogSpillBlocks = defaults.FogSpillBlocks;
        config.SilhouetteFogHeightBlocks = defaults.FogHeightBlocks;
        config.SilhouetteFogFloorDepthBlocks = defaults.FogFloorDepthBlocks;
        config.SilhouetteFogBillowSizeBlocks = defaults.FogBillowSizeBlocks;
        config.SilhouetteFogDriftSpeed = defaults.FogDriftSpeed;
        config.SilhouetteFogLayerDetail = defaults.FogLayerDetail;
        config.SilhouetteFogEdgeSoftness = defaults.FogEdgeSoftness;
        config.SilhouetteFogStripHeightBlocks = defaults.FogStripHeightBlocks;
        config.SilhouetteFogFadeSeconds = defaults.FogFadeSeconds;
        config.SilhouetteFogModel = LodSilhouetteFogVolume.FogModel;
        if (!fromFile) return;
        capi.StoreModConfig(config, "dodenslandscapes.json");
        Mod.Logger.Notification(
            "[Landscapes] the seam fog was redesigned, so its dials were reset once to the "
            + "new defaults. The values you had were tuned for the previous fog.");
    }

    /// <summary>Writes every setting: a partial write would silently reset the others.</summary>
    void SaveConfig()
    {
        // The renderer and the LodWorld statics may not exist yet, and what was
        // loaded from the file is still the right thing to write back.
        if (renderer != null)
        {
            config.FarViewDistanceCap = renderer.FarViewDistanceCap;
            // Automatic thresholds are re-derived from the screen every session. Writing them over
            // the fixed list would lose the player's own distances for when automatic is off.
            if (!renderer.AutoLodThresholds)
            {
                config.DetailDistance = (int)LodWorld.DetailDistance;
                config.LodThresholds = LodWorld.GetLevelThresholds();
            }
            config.AutoLodThresholds = renderer.AutoLodThresholds;
            config.LoadWhilePaused = renderer.ContinueLoadingWhilePaused;
            config.LodTargetPixels = renderer.LodTargetPixels;
            config.TaaReachPixels = renderer.TaaReachPixels;
            config.TaaBlend = renderer.TaaFeedback;
            config.TaaStillBlend = renderer.TaaStillFeedback;
            config.TaaNudge = renderer.TaaNudge;
            config.TaaSharpen = renderer.TaaSharpen;
            config.TaaClip = renderer.TaaClip;
            // The request, never the effective state: a mask that failed this session
            // reports itself disabled, and writing that would turn it off permanently.
            config.ChunkMask = renderer.ChunkMaskRequested;
            config.WaterDepthShading = renderer.WaterDepthShading;
            config.WaterMaxAlpha = renderer.WaterMaxAlpha;
            config.WaterClarity = renderer.WaterClarity;
            config.WaterSeamStripBlocks = renderer.WaterSeamStripBlocks;
            config.SuppressUnknownWaterWalls = renderer.SuppressUnknownWaterWalls;
            WriteVisualSettings(CurrentVisualSettings());

            // The requested state, never the effective one, for the same reason the mask
            // uses the request: a path that refused to start this session reports itself
            // off, and writing that back would turn it off permanently on a machine where
            // the next driver update might have fixed it.
            config.GpuArenas = renderer.GpuShadowRequested;
            config.IndirectDraw = renderer.IndirectDrawEnabled;
            config.DepthCull = renderer.GpuCullEnabled;
            config.LateDepthPicture = renderer.LateDepthPyramid;
        }

        capi.StoreModConfig(config, "dodenslandscapes.json");
    }

    public override void Dispose()
    {
        if (capi == null) return;

        // The engine runs this on whichever thread is tearing the game down, and on the
        // vanilla shutdown crash path ("Can't use a disposed shader" out of a render
        // stage) that is not the main thread. Every engine call below refuses to run off
        // it, and an exception that escapes one step skips every step behind it. Two
        // rounds of this were needed to learn the general rule: the first version lost
        // the storage writer's shutdown, and the second, which guarded only the events,
        // lost the renderer's. So each step stands alone and none of them propagates.
        Quietly(() =>
        {
            capi.Event.ChunkDirty -= OnChunkDirty;
            capi.Event.LevelFinalize -= OnLevelFinalize;
            capi.Event.LevelFinalize -= RegisterAutoCommand;
            capi.Event.LevelFinalize -= ShowLodModNotice;
            capi.Event.LeaveWorld -= OnLeaveWorld;

            // Nothing to unregister if the renderer never started: no listener was registered.
            if (renderer != null) capi.Event.UnregisterGameTickListener(tickListenerId);
        });

        // The sibling-cache reader owns a background connection and must stop before
        // process teardown even if the engine skipped the ordinary LeaveWorld event.
        Quietly(() =>
        {
            localOffers?.Dispose();
            localOffers = null;
        });

        // Editors restore any unsaved live AA preview while the renderer still exists.
        Quietly(() => settingsIntegration?.Dispose());
        Quietly(() => configDialog?.Dispose());

        // Stop the client silhouette store even if the engine skipped LeaveWorld. It owns a
        // background SQLite connection just like the sibling-cache reader above.
        Quietly(() => silhouettes?.Dispose());

        // Stops the storage writer before the connection it writes through.
        Quietly(() => horizonEffects?.Dispose());
        Quietly(() => pipeline?.Dispose());
        Quietly(() =>
        {
            if (hitches == null) return;
            capi.Event.UnregisterRenderer(hitches, EnumRenderStage.Before);
            hitches.Dispose();
            hitches = null;
        });
        Quietly(() => renderer?.Dispose());
        Quietly(DisposeDiagnostics);
    }

    /// <summary>
    /// One shutdown step, and whatever it throws stays here. Only ever called from
    /// <see cref="Dispose"/>, where the game is already going down and the alternative is
    /// abandoning the steps behind this one.
    /// </summary>
    void Quietly(Action step)
    {
        try
        {
            step();
        }
        catch (Exception e)
        {
            Mod.Logger.Debug("A shutdown step was refused, which changes nothing by now: {0}",
                e.Message);
        }
    }
}
