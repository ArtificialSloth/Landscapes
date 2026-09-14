using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace VintageHorizons.Net;

/// <summary>
/// Builds the server's own LOD cache (DESIGN.md §10, stage 2). Runs the same
/// <see cref="LodPipeline"/> the client does, driven by chunk columns the server loads or
/// generates rather than by ones a player receives, so the cache accumulates terrain from
/// everybody's travels instead of one player's.
///
/// Dedicated servers only. In singleplayer the client side of this same process already
/// captures every chunk that loads, so a second pipeline would duplicate the cache file,
/// the work and the memory for nothing - see StartServerSide.
///
/// Deliberately not merged into <see cref="LodAssistServerSystem"/>: the handshake has to
/// keep answering, and answer honestly, even when capture is off or skipped.
/// </summary>
public class LodServerCaptureSystem : ModSystem
{
    const string ConfigFile = "vintagehorizons-server.json";
    const long ActiveWorkStatusLogIntervalMs = 60_000;

    ICoreServerAPI sapi = null!;
    LodPipeline? pipeline;
    LodPlayerPregen? pregen;
    LodSavegameSweep? sweep;
    LodPlayerPregen? generate;
    LodPlayerPregen? refresh;
    LodWorldGenJob? worldGen;
    readonly LodWorldGenAuthority worldGenAuthority = new();
    readonly Dictionary<string, PendingWorldGen> pendingWorldGens = new();
    long tickListenerId;
    long statsListenerId;
    bool statsRegistered;
    bool allocationTelemetryEnabled;
    LodPhaseCost pipelineTickCost;
    readonly HashSet<long> persistedLevelZeroChanges = new();
    long nextActiveWorkStatusLogMs;
    long lastPipelineTickMs;
    long overloadedUntilMs;
    bool columnLoadedSubscribed;

    sealed record PendingWorldGen(
        string CallerKey, string StartedBy, long ExpiresAtMs,
        int CentreCx, int CentreCz, int RadiusChunks, EnumWorldGenShape Shape,
        string Preset, LodWorldGenPlan Plan);

    /// <summary>
    /// Whether chunk loads drive capture on this server. Dedicated: yes, every load.
    /// Singleplayer: only when a sweep needs it - the client side already captures
    /// every column this process shows the player, and capturing them again here
    /// doubles the work and the memory for no gain. Generation never needs this flag:
    /// it feeds the pipeline directly, so the guard has nothing to predict.
    /// </summary>
    bool captureDrivenByLoads;

    /// <summary>Set at RunGame. /vhgen cannot open a cache before the world has a name.</summary>
    bool worldReady;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    /// <summary>Set once the cache is open; the assist handshake reports it.</summary>
    public bool Capturing => pipeline?.Active == true;

    /// <summary>
    /// Keys the server can offer. HasDataSet, not Sections: a section evicted from RAM is
    /// still on disk and still servable, and the count a client is told has to match what
    /// the manifest will actually contain.
    /// </summary>
    public int SectionCount => pipeline?.World.HasDataSet.Count ?? 0;

    public int ColumnsCaptured => pipeline?.ColumnsCaptured ?? 0;

    /// <summary>Persistence and capture health for /vhserver and the active-work minute log.</summary>
    public string CacheWorkStatusLine => FormatCacheWorkStatus(
        pipeline?.DirtySections ?? 0,
        pipeline?.OldestDirtyAgeMs ?? 0,
        pipeline?.SavesPerSecond ?? 0,
        pipeline?.SectionsSaved ?? 0,
        pipeline?.CaptureBacklogTotal ?? 0,
        pipeline?.CaptureDropsUnloaded ?? 0,
        pipeline?.CaptureErrors ?? 0);

    internal static string FormatCacheWorkStatus(
        int dirtySections, long oldestDirtyAgeMs, double savesPerSecond,
        long sectionsSaved, int captureBacklog, long captureDropsUnloaded,
        int captureErrors) => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "[VintageHorizons] Cache work: {0} dirty, oldest dirty {1:0.0}s, "
            + "{2:0.00} saves/s ({3} saved); capture backlog {4}, "
            + "unloaded-before-capture drops {5}, capture errors {6}.",
            dirtySections, oldestDirtyAgeMs / 1000.0, savesPerSecond,
            sectionsSaved, captureBacklog, captureDropsUnloaded, captureErrors);

    /// <summary>Progress line for /vhserver, or null when no pre-generation is running.</summary>
    public string? PregenStatus => pregen?.Status;

    /// <summary>Progress line for /vhserver, or null when no sweep is running.</summary>
    public string? SweepStatus => sweep?.Status;

    /// <summary>Progress line for /vhserver, or null when /vhgen never ran.</summary>
    public string? GenerateStatus => generate?.Status;

    /// <summary>Progress line for /vhserver, or null when /vhrefresh never ran.</summary>
    public string? RefreshStatus => refresh?.Status;

    /// <summary>Progress line for /vhserver, or null when no real generation job ran.</summary>
    public string? WorldGenStatus => worldGen?.Status;

    /// <summary>Main thread only - the capture pipeline mutates this set every tick.</summary>
    public long[] SnapshotKeys() =>
        pipeline == null ? Array.Empty<long>() : pipeline.World.HasDataSet.ToArray();

    /// <summary>The active server-cache path, used to open the assist's read-only reader.</summary>
    public string? CachePath => pipeline?.DbPath;

    /// <summary>
    /// True when a key we advertised has no stored row YET, as opposed to having none at
    /// all. SnapshotKeys reports the whole section tree, and RegisterInTree puts every mip
    /// ancestor of a captured section in there the moment the child appears, well before
    /// propagation and the next save have written the parent. A sweep or a /vhgen run is
    /// in this state continuously.
    ///
    /// Answering "not yet" instead of a bare refusal is what stops a client giving that
    /// section up for the rest of the session.
    /// </summary>
    public bool ExpectsToHave(long key) =>
        pipeline != null && pipeline.World.HasDataSet.Contains(key);

    /// <summary>Admin settings, loaded once; both server systems read this copy.</summary>
    public LodServerConfig Config { get; private set; } = new();

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        allocationTelemetryEnabled =
            Environment.GetEnvironmentVariable("VINTAGEHORIZONS_STATS") == "1";

        bool configReadable = true;
        try
        {
            Config = api.LoadModConfig<LodServerConfig>(ConfigFile) ?? new LodServerConfig();
        }
        catch (Exception e)
        {
            // Defaults in memory, and the file stays exactly as the admin wrote it.
            // Writing defaults back here would destroy every hand-edited setting over
            // one bad comma.
            configReadable = false;
            Mod.Logger.Warning(
                "Could not parse {0}: {1}. Using defaults for this session. The file was "
                + "left untouched so you can fix it.", ConfigFile, e.Message);
            Config = new LodServerConfig();
        }
        Config.Sanitize();
        // Written back after every clean load so a new option appears in the file rather
        // than only in the source, and so a sanitised value is visible as the one in force.
        if (configReadable) api.StoreModConfig(Config, ConfigFile);

        // Registered whatever the config says: a command that refuses with the reason
        // beats "no such command", which reads as a broken mod.
        RegisterGenerateCommand();
        RegisterRefreshCommand();
        RegisterWorldGenCommand();

        if (!Config.EnableCapture)
        {
            Mod.Logger.Notification(
                "Server LOD capture disabled in {0}. Clients are unaffected and keep using "
                + "their own captures, exactly as on a server without this mod.", ConfigFile);
            return;
        }

        // Singleplayer (and LAN-hosted) worlds run client and integrated server in one
        // process. Capture from chunk loads has nothing to do there: the server loads
        // exactly the chunks the client is shown, and the client side already captures
        // those. Observed live before this was guarded: a manifest of 3851 keys the
        // client already had, every one redundant. A sweep is the exception - it loads
        // columns the client is never shown, and this event is the only way to see them.
        //
        // Generation is deliberately NOT an exception here. It hands peeked columns to
        // the pipeline itself and routes its loads through an OnLoaded callback, so it
        // needs the cache open but not this event - which is what lets /vhgen work in
        // singleplayer hours after startup without re-running this decision.
        captureDrivenByLoads = api.Server.IsDedicated || Config.SweepEnabled;

        // Not at StartServerSide: the savegame identifier that names the cache file is
        // not known until the world is up.
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, OnRunGame);
        api.Event.GameWorldSave += OnGameWorldSave;
    }

    void OnRunGame()
    {
        worldReady = true;

        OfferInterruptedWorldGen();

        if (!captureDrivenByLoads)
        {
            Mod.Logger.Notification(
                "Singleplayer or LAN-hosted world with the sweep off: the server side stays "
                + "idle. The client side already captures everything that this process loads. "
                + "Set SweepSavegame to index terrain from earlier sessions. /vhgen opens a "
                + "server-side cache on demand, for terrain that the client cannot reach itself.");
            return;
        }

        OpenPipeline();

        EnsureColumnLoadedSubscription();
        sapi.Event.ChunkDirty += OnChunkDirty;
        sapi.Event.DidBreakBlock += (_, _, blockSel) => QueueAt(blockSel?.Position);
        sapi.Event.DidPlaceBlock += (_, _, blockSel, _) => QueueAt(blockSel?.Position);

        // Sweep before pregen, and not only because it is cheaper. Sweeping loads terrain
        // that already exists; pregen makes more. Doing the free work first means an
        // interrupted startup has still indexed everything real before spending a second on
        // inventing anything.
        if (Config.SweepEnabled)
        {
            WeatherSystemBase? weather = null;
            try
            {
                weather = sapi.ModLoader.GetModSystem<WeatherSystemServer>();
            }
            catch (Exception e)
            {
                Mod.Logger.Warning(
                    "Vanilla weather system was unavailable; the startup sweep still runs, "
                    + "but automatic dormant-terrain snow refresh is disabled: {0}", e.Message);
            }
            sweep = new LodSavegameSweep(sapi, Mod.Logger,
                Config.SweepRadiusChunks, Config.SweepColumnsPerSecond,
                Config.RefreshColumnsPerSecond, weather, pipeline!.QueueColumn,
                () => pipeline?.CaptureBacklogTotal ?? 0)
            {
                TrackPhaseAllocations = allocationTelemetryEnabled,
            };
            sweep.Start();
        }

        if (Config.PregenRadiusChunks > 0)
        {
            // Pre-generation now runs the same peek generator /vhgen uses, rather than
            // loading columns into the world. The purpose is unchanged - build the LOD
            // cache around spawn without waiting for players - but it no longer writes
            // the terrain it generates to the savegame. That was never the point of the
            // setting, only how the first version happened to work, and it cost worldgen
            // time and disk for terrain nobody had visited.
            var spawn = sapi.World.DefaultSpawnPosition;
            int cs = GlobalConstants.ChunkSize;
            if (!worldGenAuthority.TryAcquire(EnumWorldGenAuthority.StartupPregen,
                "startup LOD pre-generation", out string refusal))
            {
                Mod.Logger.Warning("Startup LOD pre-generation did not start: {0}.", refusal);
            }
            else
            {
                pregen = new LodPlayerPregen(sapi, Mod.Logger, pipeline!,
                    (int)spawn.X / cs, (int)spawn.Z / cs,
                    Config.PregenRadiusChunks, Config, "startup pre-generation",
                    Config.PregenColumnsPerSecond)
                {
                    TrackPhaseAllocations = allocationTelemetryEnabled,
                };
                pregen.Start();
            }
        }
    }

    /// <summary>
    /// Open the cache and start ticking the pipeline. Idempotent; main thread only.
    ///
    /// Split out of OnRunGame because /vhgen can need it at any moment - hours into a
    /// singleplayer session where this side never opened anything - and because opening
    /// a second SQLite file in every singleplayer world, in case someone might type a
    /// command one day, is exactly the cost the startup guard exists to avoid.
    /// </summary>
    LodPipeline OpenPipeline()
    {
        if (pipeline != null) return pipeline;

        // A server has no texture atlas, so it cannot compute a palette colour at all
        // (Block.GetColorWithoutTint takes ICoreClientAPI). Sections are written
        // colour-unresolved and the receiving client fills colour in, which it can do
        // from the block code alone. Tint slots are likewise client-only and stay 0.
        pipeline = new LodPipeline(sapi, Mod.Logger, (_, _, _, _) => (0, 0));
        pipeline.TrackPhaseAllocations = allocationTelemetryEnabled;
        pipeline.SectionPersisted = key =>
        {
            if (LodWorld.KeyLevel(key) == 0) persistedLevelZeroChanges.Add(key);
            worldGen?.ObservePersisted(key);
        };
        pipeline.ColumnCaptured = (cx, cz) => worldGen?.ObserveCaptured(cx, cz);
        pipeline.SnowStateCaptured = (cx, cz, observed, covered) =>
            sweep?.ObserveSnowState(cx, cz, observed, covered);
        pipeline.Open("ModData/vintagehorizons", "-server");
        nextActiveWorkStatusLogMs = Environment.TickCount64 + ActiveWorkStatusLogIntervalMs;

        tickListenerId = sapi.Event.RegisterGameTickListener(_ => TickPipeline(), 50);
        if (allocationTelemetryEnabled && !statsRegistered)
        {
            statsRegistered = true;
            statsListenerId = sapi.Event.RegisterGameTickListener(_ => LogStats(), 15000);
        }

        Mod.Logger.Notification("Server LOD capture active ({0} sections from cache). {1}",
            pipeline.CachedSectionsLoaded, Config.Describe());
        return pipeline;
    }

    // ---- /vhgen: build the cache around a player, generating what nobody visited ----

    void RegisterGenerateCommand()
    {
        var parsers = sapi.ChatCommands.Parsers;
        sapi.ChatCommands.Create("vhgen")
            .WithDescription(
                "Build the VintageHorizons LOD cache around you. Terrain nobody has visited "
                + "is generated from the world seed and captured, and nothing is written to "
                + "the savegame.")
            .RequiresPrivilege(Privilege.controlserver)
            .HandleWith(_ => TextCommandResult.Success(GenerateStatusLine()))
            .BeginSubCommand("start")
                .WithDescription("Start a run centred on you, or on x z when given")
                .WithArgs(
                    parsers.OptionalIntRange("radius",
                        1, Math.Max(1, Config.GenerateMaxRadiusChunks), Config.GenerateDefaultRadiusChunks),
                    parsers.OptionalInt("x", -1),
                    parsers.OptionalInt("z", -1))
                .HandleWith(OnGenerateStart)
            .EndSubCommand()
            .BeginSubCommand("stop")
                .WithDescription("Cancel the run in progress")
                .HandleWith(OnGenerateStop)
            .EndSubCommand()
            .BeginSubCommand("status")
                .WithDescription("Progress of the current or last run")
                .HandleWith(_ => TextCommandResult.Success(GenerateStatusLine()))
            .EndSubCommand()
            .BeginSubCommand("diff")
                .WithDescription("Measure what a peek produces against a full generation")
                .WithArgs(parsers.OptionalInt("x", -1), parsers.OptionalInt("z", -1))
                .HandleWith(OnGenerateDiff)
            .EndSubCommand();

        // Places a block, so it exists only where someone asked for developer tools.
        // DESIGN.md 14 states the mod writes nothing to the world; that stays literally
        // true on every server that does not set this.
        if (Environment.GetEnvironmentVariable("VINTAGEHORIZONS_DEVTOOLS") != "1") return;

        sapi.ChatCommands.Get("vhgen")
            .BeginSubCommand("edittest")
                .WithDescription("Dev tool: place a marker, then show a peek cannot see it")
                .WithArgs(parsers.OptionalInt("x", -1), parsers.OptionalInt("z", -1))
                .HandleWith(OnGenerateEditTest)
            .EndSubCommand();

        Mod.Logger.Notification(
            "Developer tools on (VINTAGEHORIZONS_DEVTOOLS): /vhgen edittest is registered and "
            + "PLACES BLOCKS in the world when run. Do not set this on a server you care about.");
    }

    /// <summary>
    /// Centre for a diagnostic run: explicit x z, else the caller, else spawn. Null when
    /// only one coordinate was given, which the caller reports as an error.
    /// </summary>
    (int Cx, int Cz)? DiagnosticCentre(TextCommandCallingArgs args)
    {
        int argX = (int)args.Parsers[0].GetValue();
        int argZ = (int)args.Parsers[1].GetValue();
        var pos = args.Caller.Pos;
        var centre = LodPlayerPregen.ResolveCentre(argX, argZ, pos != null);
        if (centre == LodPlayerPregen.EnumCentre.Incomplete) return null;

        bool aimed = centre == LodPlayerPregen.EnumCentre.Argument;
        double bx = aimed ? argX : pos?.X ?? sapi.World.DefaultSpawnPosition.X;
        double bz = aimed ? argZ : pos?.Z ?? sapi.World.DefaultSpawnPosition.Z;
        return ((int)bx / GlobalConstants.ChunkSize, (int)bz / GlobalConstants.ChunkSize);
    }

    const string BothCoordinates =
        "[VintageHorizons] Give both coordinates or neither. With neither, the run "
        + "centres on you.";

    TextCommandResult OnGenerateDiff(TextCommandCallingArgs args)
    {
        if (!worldReady) return TextCommandResult.Error("[VintageHorizons] The world is still starting.");

        if (DiagnosticCentre(args) is not (int cx, int cz))
        {
            return TextCommandResult.Error(BothCoordinates);
        }
        new LodPeekDiff(sapi, Mod.Logger).RunDiff(cx, cz,
            line => Mod.Logger.Notification("Peek diff result: {0}", line));

        return TextCommandResult.Success(
            $"[VintageHorizons] Comparing a peek against a full generation at chunk {cx},{cz}. "
            + "This GENERATES terrain for the reference, so do not run it where that matters. "
            + "The report lands in the server log shortly.");
    }

    TextCommandResult OnGenerateEditTest(TextCommandCallingArgs args)
    {
        if (!worldReady) return TextCommandResult.Error("[VintageHorizons] The world is still starting.");

        if (DiagnosticCentre(args) is not (int cx, int cz))
        {
            return TextCommandResult.Error(BothCoordinates);
        }
        new LodPeekDiff(sapi, Mod.Logger).RunEditTest(cx, cz,
            line => Mod.Logger.Notification("Edit test result: {0}", line));

        return TextCommandResult.Success(
            $"[VintageHorizons] Placing a marker at chunk {cx},{cz} and peeking the same "
            + "coordinate. This WRITES BLOCKS to your world. The report lands in the server log.");
    }

    string GenerateStatusLine() => generate != null
        ? "[VintageHorizons] " + generate.Status
        : "[VintageHorizons] No generation running. /vhgen start [radius] - default "
          + $"{Config.GenerateDefaultRadiusChunks} chunks, max {Config.GenerateMaxRadiusChunks}.";

    TextCommandResult OnGenerateStart(TextCommandCallingArgs args)
    {
        if (!Config.EnableCapture)
        {
            return TextCommandResult.Error(
                "[VintageHorizons] Server LOD capture is switched off (EnableCapture in "
                + $"ModConfig/{ConfigFile}), so there is nothing to generate into.");
        }
        if (!Config.GenerateEnabled)
        {
            return TextCommandResult.Error(
                "[VintageHorizons] Generation is switched off on this server "
                + $"(EnableGenerateCommand or GenerateMaxRadiusChunks in ModConfig/{ConfigFile}).");
        }
        if (!worldReady)
        {
            return TextCommandResult.Error("[VintageHorizons] The world still starts up. Try again shortly.");
        }
        if (generate is { Done: false })
        {
            return TextCommandResult.Error(
                $"[VintageHorizons] A run started by {generate.StartedBy} is already going: "
                + generate.Status + ". Use /vhgen stop first.");
        }
        if (refresh is { Done: false })
        {
            return TextCommandResult.Error(
                "[VintageHorizons] /vhrefresh is already active. Let it finish or stop it "
                + "before starting /vhgen.");
        }
        RefreshWorldGenAuthority();
        if (!worldGenAuthority.TryAcquire(EnumWorldGenAuthority.TransientGeneration,
            "/vhgen transient generation", out string authorityRefusal))
        {
            return TextCommandResult.Error(
                "[VintageHorizons] Cannot start /vhgen: " + authorityRefusal + ".");
        }

        int radius = (int)args.Parsers[0].GetValue();
        int argX = (int)args.Parsers[1].GetValue();
        int argZ = (int)args.Parsers[2].GetValue();

        var pos = args.Caller.Pos;
        var centre = LodPlayerPregen.ResolveCentre(argX, argZ, pos != null);
        if (centre == LodPlayerPregen.EnumCentre.Incomplete)
        {
            worldGenAuthority.Release(EnumWorldGenAuthority.TransientGeneration);
            return TextCommandResult.Error(
                "[VintageHorizons] Give both coordinates or neither: /vhgen start "
                + $"{radius} <x> <z>. With neither, the run centres on you.");
        }

        bool aimed = centre == LodPlayerPregen.EnumCentre.Argument;
        bool fromSpawn = centre == LodPlayerPregen.EnumCentre.Spawn;
        double bx = aimed ? argX : pos?.X ?? sapi.World.DefaultSpawnPosition.X;
        double bz = aimed ? argZ : pos?.Z ?? sapi.World.DefaultSpawnPosition.Z;
        int cs = GlobalConstants.ChunkSize;
        int centreCx = (int)bx / cs;
        int centreCz = (int)bz / cs;
        string startedBy = args.Caller.Player?.PlayerName ?? "the console";

        try
        {
            generate = new LodPlayerPregen(sapi, Mod.Logger, OpenPipeline(),
                centreCx, centreCz, radius, Config, startedBy)
            {
                TrackPhaseAllocations = allocationTelemetryEnabled,
            };
            generate.Start();
        }
        catch (Exception e)
        {
            worldGenAuthority.Release(EnumWorldGenAuthority.TransientGeneration);
            return TextCommandResult.Error(
                "[VintageHorizons] Generation could not start: " + e.Message);
        }

        int columns = LodPlayerPregen.ColumnsFor(radius);
        int seconds = LodPlayerPregen.EstimateSeconds(columns, Config.GenerateColumnsPerSecond);
        string time = seconds >= 120 ? seconds / 60 + " minutes" : seconds + " seconds";
        return TextCommandResult.Success(
            $"[VintageHorizons] Generating LOD around {(fromSpawn ? "spawn " : "")}{(int)bx},{(int)bz} "
            + $"out to {radius} chunks ({radius * cs} blocks): {columns} columns, roughly {time} if "
            + "the server keeps up. Terrain that nobody visited is generated from the seed and "
            + "thrown away - nothing is written to the savegame, and no chunk is loaded for any "
            + "player. /vhgen status shows progress. /vhgen stop cancels.");
    }

    TextCommandResult OnGenerateStop(TextCommandCallingArgs args)
    {
        if (generate is not { Done: false })
        {
            return TextCommandResult.Success("[VintageHorizons] No generation is running.");
        }
        generate.Cancel();
        return TextCommandResult.Success(
            "[VintageHorizons] Generation stopping. Peeks already issued still get captured - "
            + "that worldgen time is spent either way. The finish line in the server log "
            + "reports what was done and verifies the savegame gained nothing.");
    }

    // ---- /vhworldgen: permanently generate a bounded part of the real savegame ----

    void RegisterWorldGenCommand()
    {
        var parsers = sapi.ChatCommands.Parsers;
        sapi.ChatCommands.Create("vhworldgen")
            .WithDescription(
                "Generate and save real terrain in a bounded square or circle, while "
                + "building the server distant-terrain cache. Requires confirmation.")
            .RequiresPrivilege(Privilege.controlserver)
            .HandleWith(OnWorldGenStatus)
            .BeginSubCommand("start")
                .WithDescription(
                    "Prepare a job: [radius] [circle|square] [here|spawn|coords] [x] [z]")
                .WithArgs(
                    parsers.OptionalIntRange("radius", 1,
                        Math.Max(1, Config.WorldGenMaxRadiusChunks),
                        Math.Max(1, Config.WorldGenDefaultRadiusChunks)),
                    parsers.OptionalWord("circle|square"),
                    parsers.OptionalWord("here|spawn|coords"),
                    parsers.OptionalInt("x", -1),
                    parsers.OptionalInt("z", -1))
                .HandleWith(OnWorldGenStart)
            .EndSubCommand()
            .BeginSubCommand("confirm")
                .WithDescription("Confirm your pending start after reading its permanent-world warning")
                .HandleWith(OnWorldGenConfirm)
            .EndSubCommand()
            .BeginSubCommand("pause")
                .WithDescription("Pause new generation requests; in-flight work may finish")
                .HandleWith(OnWorldGenPause)
            .EndSubCommand()
            .BeginSubCommand("resume")
                .WithDescription("Resume the paused or interrupted job")
                .HandleWith(OnWorldGenResume)
            .EndSubCommand()
            .BeginSubCommand("stop")
                .WithDescription("Stop the job, release kept columns, and discard resume state")
                .HandleWith(OnWorldGenStop)
            .EndSubCommand()
            .BeginSubCommand("status")
                .WithDescription("Show progress, rate, disk growth, queue depth, and active gate")
                .HandleWith(OnWorldGenStatus)
            .EndSubCommand();
    }

    TextCommandResult OnWorldGenStart(TextCommandCallingArgs args)
    {
        int radius = (int)args.Parsers[0].GetValue();
        string shapeText = Convert.ToString(args.Parsers[1].GetValue())?.Trim() ?? "";
        EnumWorldGenShape shape;
        if (shapeText.Length == 0)
            shape = LodWorldGenPlan.DefaultShape;
        else if (shapeText.Equals("circle", StringComparison.OrdinalIgnoreCase))
            shape = EnumWorldGenShape.Circle;
        else if (shapeText.Equals("square", StringComparison.OrdinalIgnoreCase))
            shape = EnumWorldGenShape.Square;
        else
            return TextCommandResult.Error(
                "[VintageHorizons] Shape must be circle or square.");

        string centreMode = Convert.ToString(args.Parsers[2].GetValue())?.Trim() ?? "";
        EnumWorldGenCentreMode centre;
        if (centreMode.Length == 0 || centreMode.Equals("here", StringComparison.OrdinalIgnoreCase))
            centre = EnumWorldGenCentreMode.Here;
        else if (centreMode.Equals("spawn", StringComparison.OrdinalIgnoreCase))
            centre = EnumWorldGenCentreMode.Spawn;
        else if (centreMode.Equals("coords", StringComparison.OrdinalIgnoreCase))
            centre = EnumWorldGenCentreMode.Coordinates;
        else
            return TextCommandResult.Error(
                "[VintageHorizons] Centre must be here, spawn, or coords followed by x z.");

        int argX = (int)args.Parsers[3].GetValue();
        int argZ = (int)args.Parsers[4].GetValue();
        var result = WorldGenPrepare(WorldGenCallerKey(args),
            args.Caller.Player?.PlayerName ?? "the console",
            args.Caller.Pos?.X, args.Caller.Pos?.Z, radius, shape, centre, argX, argZ,
            "Balanced");
        return WorldGenCommandResult(result);
    }

    TextCommandResult OnWorldGenConfirm(TextCommandCallingArgs args)
    {
        return WorldGenCommandResult(WorldGenConfirm(WorldGenCallerKey(args)));
    }

    TextCommandResult OnWorldGenPause(TextCommandCallingArgs args)
    {
        return WorldGenCommandResult(WorldGenPause());
    }

    TextCommandResult OnWorldGenResume(TextCommandCallingArgs args)
    {
        return WorldGenCommandResult(WorldGenResume());
    }

    TextCommandResult OnWorldGenStop(TextCommandCallingArgs args)
    {
        return WorldGenCommandResult(WorldGenStop(WorldGenCallerKey(args)));
    }

    TextCommandResult OnWorldGenStatus(TextCommandCallingArgs args)
    {
        string line = WorldGenStatusLine();
        Mod.Logger.Notification("{0}", line);
        return TextCommandResult.Success(line);
    }

    static TextCommandResult WorldGenCommandResult((bool Success, string Message) result) =>
        result.Success ? TextCommandResult.Success(result.Message) : TextCommandResult.Error(result.Message);

    /// <summary>Shared validation for the command and the Generate page.</summary>
    internal (bool Success, string Message) WorldGenPrepare(
        string callerKey, string startedBy, double? hereX, double? hereZ,
        int radius, EnumWorldGenShape shape, EnumWorldGenCentreMode centreMode,
        int argX, int argZ, string preset)
    {
        if (!Config.EnableCapture)
            return (false, "[VintageHorizons] Server LOD capture is switched off.");
        if (!Config.WorldGenerationEnabled)
            return (false, "[VintageHorizons] Savegame world generation is switched off "
                + $"(EnableWorldGeneration or WorldGenMaxRadiusChunks in ModConfig/{ConfigFile}).");
        if (!worldReady)
            return (false, "[VintageHorizons] The world is still starting.");
        if (radius < 1 || radius > Config.WorldGenMaxRadiusChunks)
            return (false, $"[VintageHorizons] Radius must be from 1 to {Config.WorldGenMaxRadiusChunks} chunks.");
        if (!Enum.IsDefined(shape))
            return (false, "[VintageHorizons] Shape must be circle or square.");
        if (!Enum.IsDefined(centreMode))
            return (false, "[VintageHorizons] Centre must be here, spawn, or coordinates.");
        if (!string.Equals(preset, "Balanced", StringComparison.OrdinalIgnoreCase))
            return (false, "[VintageHorizons] Only the measured Balanced preset is available until "
                + "the experimental worldgen-thread comparison is complete.");
        if (worldGen is { Done: false })
            return (false, "[VintageHorizons] A world-generation job is already active. " + worldGen.Status);
        if (refresh is { Done: false })
            return (false, "[VintageHorizons] /vhrefresh is active. Stop it or let it finish first.");

        RefreshWorldGenAuthority();
        if (worldGenAuthority.Current != EnumWorldGenAuthority.None)
            return (false, "[VintageHorizons] Cannot prepare the job: "
                + worldGenAuthority.Label + " already has world-generation authority.");

        var stateStore = new LodWorldGenStateStore(LodWorldGenJob.StatePath(sapi));
        if (stateStore.Exists)
            return (false, "[VintageHorizons] An interrupted job is saved. Resume it or stop it "
                + "to discard that resume state before preparing another area.");

        double bx, bz;
        if (centreMode == EnumWorldGenCentreMode.Here)
        {
            if (argX >= 0 || argZ >= 0)
                return (false, "[VintageHorizons] Typed coordinates require Coordinates as the centre.");
            bx = hereX ?? sapi.World.DefaultSpawnPosition.X;
            bz = hereZ ?? sapi.World.DefaultSpawnPosition.Z;
        }
        else if (centreMode == EnumWorldGenCentreMode.Spawn)
        {
            if (argX >= 0 || argZ >= 0)
                return (false, "[VintageHorizons] Spawn takes no x z coordinates.");
            bx = sapi.World.DefaultSpawnPosition.X;
            bz = sapi.World.DefaultSpawnPosition.Z;
        }
        else
        {
            if (argX < 0 || argZ < 0)
                return (false, "[VintageHorizons] Coordinates requires non-negative x and z block positions.");
            bx = argX;
            bz = argZ;
        }

        int cs = GlobalConstants.ChunkSize;
        int centreCx = (int)bx / cs;
        int centreCz = (int)bz / cs;
        int mapChunksX = sapi.WorldManager.MapSizeX / cs;
        int mapChunksZ = sapi.WorldManager.MapSizeZ / cs;
        if (centreCx < 0 || centreCz < 0 || centreCx >= mapChunksX || centreCz >= mapChunksZ)
            return (false, "[VintageHorizons] The requested centre is outside this world.");

        var plan = new LodWorldGenPlan(centreCx, centreCz, radius, shape, mapChunksX, mapChunksZ);
        if (plan.Count == 0)
            return (false, "[VintageHorizons] The requested area contains no world columns.");

        pendingWorldGens[callerKey] = new PendingWorldGen(
            callerKey, startedBy, Environment.TickCount64 + 120_000,
            centreCx, centreCz, radius, shape, "Balanced", plan);

        long estimatedBytes = (long)plan.Count * LodWorldGenJob.EstimatedTotalBytesPerColumn;
        long estimatedSeconds = (long)Math.Ceiling(plan.Count / LodWorldGenJob.BaselineColumnsPerSecond);
        return (true, $"[VintageHorizons] Ready to generate {plan.Count:N0} real columns in a "
            + $"{shape.ToString().ToLowerInvariant()} of radius {radius} chunks ({radius * cs:N0} blocks) "
            + $"around {centreCx * cs:N0},{centreCz * cs:N0}. Baseline estimate: "
            + $"{FormatWorldGenDuration(estimatedSeconds)} and up to {FormatWorldGenBytes(estimatedBytes)} "
            + "of savegame plus distant-terrain data. This permanently adds terrain to your "
            + "world; make a backup first if the world matters. Confirm within two minutes to begin.");
    }

    internal (bool Success, string Message) WorldGenConfirm(string callerKey)
    {
        if (!pendingWorldGens.Remove(callerKey, out PendingWorldGen? pending)
            || Environment.TickCount64 > pending.ExpiresAtMs)
            return (false, "[VintageHorizons] No unexpired start is waiting for your confirmation. "
                + "Prepare the area again.");

        int mapChunksX = sapi.WorldManager.MapSizeX / GlobalConstants.ChunkSize;
        int mapChunksZ = sapi.WorldManager.MapSizeZ / GlobalConstants.ChunkSize;
        var state = LodWorldGenState.Create(pending.Plan,
            pending.CentreCx, pending.CentreCz, pending.RadiusChunks, pending.Shape,
            mapChunksX, mapChunksZ, pending.StartedBy,
            LodWorldGenJob.FileLength(sapi.WorldManager.CurrentWorldFilepath), cacheBytesAtStart: 0);
        state.Preset = pending.Preset;

        if (!TryStartWorldGen(pending.Plan, state, resumed: false, out string refusal))
            return (false, "[VintageHorizons] World generation did not start: " + refusal);
        return (true, worldGen!.Status);
    }

    internal (bool Success, string Message) WorldGenPause()
    {
        if (worldGen is not { Done: false })
            return (true, "[VintageHorizons] No world-generation job is active.");
        worldGen.Pause();
        return (true, worldGen.Status);
    }

    internal (bool Success, string Message) WorldGenResume()
    {
        if (!Config.EnableCapture || !Config.WorldGenerationEnabled)
            return (false, "[VintageHorizons] Savegame world generation is disabled.");
        if (!worldReady)
            return (false, "[VintageHorizons] The world is still starting.");
        if (worldGen is { Done: false })
        {
            worldGen.Resume();
            return (true, worldGen.Status);
        }
        if (refresh is { Done: false })
            return (false, "[VintageHorizons] /vhrefresh is active. Stop it or let it finish first.");

        var stateStore = new LodWorldGenStateStore(LodWorldGenJob.StatePath(sapi));
        if (!stateStore.TryLoad(out LodWorldGenState? state, out string reason))
            return (false, "[VintageHorizons] Could not read the saved job: " + reason);
        if (state == null)
            return (true, "[VintageHorizons] There is no interrupted job to resume.");
        int currentMapChunksX = sapi.WorldManager.MapSizeX / GlobalConstants.ChunkSize;
        int currentMapChunksZ = sapi.WorldManager.MapSizeZ / GlobalConstants.ChunkSize;
        if (!Enum.IsDefined(state.Shape) || state.RadiusChunks < 0
            || state.RadiusChunks > 256 || state.MapChunksX != currentMapChunksX
            || state.MapChunksZ != currentMapChunksZ || state.CentreCx < 0
            || state.CentreCz < 0 || state.CentreCx >= currentMapChunksX
            || state.CentreCz >= currentMapChunksZ)
            return (false, "[VintageHorizons] The saved job has invalid area settings.");

        var plan = new LodWorldGenPlan(state.CentreCx, state.CentreCz, state.RadiusChunks,
            state.Shape, state.MapChunksX, state.MapChunksZ);
        if (!state.Matches(plan, currentMapChunksX, currentMapChunksZ))
            return (false, "[VintageHorizons] The saved job does not match this world's current bounds or plan format.");

        if (!TryStartWorldGen(plan, state, resumed: true, out string refusal))
            return (false, "[VintageHorizons] Saved world generation did not resume: " + refusal);
        return (true, worldGen!.Status);
    }

    internal (bool Success, string Message) WorldGenStop(string callerKey)
    {
        pendingWorldGens.Remove(callerKey);
        if (worldGen is { Done: false })
        {
            worldGen.Stop();
            return (true, worldGen.Status);
        }

        var store = new LodWorldGenStateStore(LodWorldGenJob.StatePath(sapi));
        if (!store.Exists)
            return (true, "[VintageHorizons] No world-generation job or resume state exists.");
        if (!store.TryDelete(out string reason))
            return (false, "[VintageHorizons] Could not discard the resume state: " + reason);
        if (worldGen is { Done: true }) worldGen = null;
        return (true,
            "[VintageHorizons] The interrupted job's resume state was discarded. Terrain it "
            + "already generated remains permanently in the world.");
    }

    bool TryStartWorldGen(LodWorldGenPlan plan, LodWorldGenState state, bool resumed,
        out string refusal)
    {
        refusal = "";
        RefreshWorldGenAuthority();
        if (worldGen is { Done: false })
        {
            refusal = "another /vhworldgen job is active";
            return false;
        }
        if (worldGenAuthority.Current != EnumWorldGenAuthority.None)
        {
            refusal = worldGenAuthority.Label + " already has world-generation authority";
            return false;
        }

        LodPipeline opened;
        try { opened = OpenPipeline(); }
        catch (Exception e)
        {
            refusal = "the server distant-terrain cache could not open: " + e.Message;
            return false;
        }
        if (!opened.Persisting || string.IsNullOrEmpty(opened.DbPath))
        {
            refusal = "the server distant-terrain cache is not persistent; no terrain was generated";
            return false;
        }
        if (!LodKeptColumnReleaser.TryCreate(sapi.WorldManager,
            out LodKeptColumnReleaser? releaser, out refusal) || releaser == null) return false;

        int remaining = Math.Max(0, plan.Count - state.DoneCount());
        if (!WorldGenDiskFits(sapi.WorldManager.CurrentWorldFilepath,
            opened.DbPath, remaining, out refusal)) return false;

        if (!worldGenAuthority.TryAcquire(EnumWorldGenAuthority.WorldGeneration,
            "/vhworldgen savegame generation", out refusal)) return false;

        // A new state's cache baseline cannot be known until OpenPipeline has selected
        // the exact world-keyed database.
        if (!resumed) state.CacheBytesAtStart = LodWorldGenJob.FileLength(opened.DbPath);
        try
        {
            EnsureColumnLoadedSubscription();
            worldGen = new LodWorldGenJob(sapi, Mod.Logger, opened, plan, state,
                LodWorldGenJob.StatePath(sapi), releaser, Config);
            worldGen.Start(resumed);
            if (sapi.WorldManager.CurrentGeneratingChunkCount >= Config.WorldGenQueueTarget)
            {
                Mod.Logger.Warning("/vhworldgen started while the engine already had {0} "
                    + "columns queued; it will wait at the named engine-queue gate.",
                    sapi.WorldManager.CurrentGeneratingChunkCount);
            }
            return true;
        }
        catch (Exception e)
        {
            worldGen?.Shutdown();
            worldGen = null;
            RemoveJobOnlyColumnLoadedSubscription();
            worldGenAuthority.Release(EnumWorldGenAuthority.WorldGeneration);
            refusal = e.Message;
            return false;
        }
    }

    string WorldGenStatusLine()
    {
        if (worldGen != null) return worldGen.Status;
        if (!worldReady)
            return "[VintageHorizons] The world is still starting.";

        var store = new LodWorldGenStateStore(LodWorldGenJob.StatePath(sapi));
        if (!store.TryLoad(out LodWorldGenState? state, out string reason))
            return "[VintageHorizons] An interrupted job state exists but cannot be read: " + reason;
        if (state != null)
            return $"[VintageHorizons] Interrupted world-generation job available: "
                + $"{state.DoneCount()}/{state.TotalColumns} durable columns around chunk "
                + $"{state.CentreCx},{state.CentreCz}, radius {state.RadiusChunks} "
                + $"{state.Shape.ToString().ToLowerInvariant()}. Use /vhworldgen resume or stop.";
        return "[VintageHorizons] No savegame world-generation job has run. Use "
            + $"/vhworldgen start [radius] [circle|square] [here|spawn|coords] [x] [z]; "
            + $"default circle, radius {Config.WorldGenDefaultRadiusChunks}, "
            + $"max radius {Config.WorldGenMaxRadiusChunks}.";
    }

    /// <summary>Structured owning-thread snapshot for the Generate page.</summary>
    internal AssistWorldGenStatus WorldGenPageStatus(
        string callerKey, bool canControl, long requestId = 0,
        bool success = true, string? message = null)
    {
        bool available = Config.EnableCapture && Config.WorldGenerationEnabled && worldReady;
        var status = new AssistWorldGenStatus
        {
            RequestId = requestId,
            Success = success,
            Message = message ?? "",
            Available = available,
            CanControl = canControl,
            State = available ? EnumWorldGenRemoteState.Idle : EnumWorldGenRemoteState.Unavailable,
            MaxRadiusChunks = Config.WorldGenMaxRadiusChunks,
            DefaultRadiusChunks = Config.WorldGenDefaultRadiusChunks,
            QueueTarget = Config.WorldGenQueueTarget,
            MaxInFlight = Config.WorldGenMaxInFlight,
            Shape = LodWorldGenPlan.DefaultShape,
        };

        if (pendingWorldGens.TryGetValue(callerKey, out PendingWorldGen? pending))
        {
            if (Environment.TickCount64 > pending.ExpiresAtMs)
            {
                pendingWorldGens.Remove(callerKey);
            }
            else
            {
                status.State = EnumWorldGenRemoteState.Prepared;
                status.PendingConfirmation = true;
                status.TotalColumns = pending.Plan.Count;
                status.RadiusChunks = pending.RadiusChunks;
                status.Shape = pending.Shape;
                status.CentreCx = pending.CentreCx;
                status.CentreCz = pending.CentreCz;
                status.Preset = pending.Preset;
            }
        }

        if (ShouldReportWorldGenJob(
            worldGen != null, worldGen?.Done == true, status.PendingConfirmation))
        {
            LodWorldGenJob job = worldGen!;
            LodWorldGenProgress progress = job.Snapshot();
            status.State = job.Done
                ? job.Failures > 0
                    ? EnumWorldGenRemoteState.Interrupted
                    : EnumWorldGenRemoteState.Complete
                : job.Faulted ? EnumWorldGenRemoteState.Faulted
                : job.Paused ? EnumWorldGenRemoteState.Paused
                : EnumWorldGenRemoteState.Running;
            status.PendingConfirmation = false;
            status.DoneColumns = progress.DoneColumns;
            status.TotalColumns = progress.TotalColumns;
            status.ColumnsPerSecond = progress.ColumnsPerSecond;
            status.EtaSeconds = progress.EtaSeconds;
            status.SavegameBytes = progress.SavegameBytes;
            status.CacheBytes = progress.CacheBytes;
            status.Gate = progress.Gate;
            status.EngineQueue = progress.EngineQueue;
            status.QueueTarget = progress.QueueTarget;
            status.InFlight = progress.InFlight;
            status.Retries = progress.Retries;
            status.Failures = progress.Failures;
            status.RadiusChunks = job.State.RadiusChunks;
            status.Shape = job.State.Shape;
            status.CentreCx = job.State.CentreCx;
            status.CentreCz = job.State.CentreCz;
            status.Preset = job.State.Preset;
        }
        else if (status.State != EnumWorldGenRemoteState.Prepared)
        {
            var store = new LodWorldGenStateStore(LodWorldGenJob.StatePath(sapi));
            if (store.TryLoad(out LodWorldGenState? saved, out _) && saved != null)
            {
                status.State = EnumWorldGenRemoteState.Interrupted;
                status.DoneColumns = saved.DoneCount();
                status.TotalColumns = saved.TotalColumns;
                status.RadiusChunks = saved.RadiusChunks;
                status.Shape = saved.Shape;
                status.CentreCx = saved.CentreCx;
                status.CentreCz = saved.CentreCz;
                status.Preset = saved.Preset;
            }
        }

        if (status.Message.Length == 0)
        {
            status.Message = status.State == EnumWorldGenRemoteState.Prepared
                ? $"[VintageHorizons] Ready to generate {status.TotalColumns:N0} real columns "
                    + $"around {status.CentreCx * GlobalConstants.ChunkSize:N0},"
                    + $"{status.CentreCz * GlobalConstants.ChunkSize:N0}. Confirm within two minutes."
                : WorldGenStatusLine();
        }
        return status;
    }

    /// <summary>
    /// An active job is authoritative. A finished job remains available for its final
    /// summary, but cannot hide a newer Prepare response and its Confirm button.
    /// </summary>
    internal static bool ShouldReportWorldGenJob(
        bool jobExists, bool jobDone, bool pendingConfirmation) =>
        jobExists && (!jobDone || !pendingConfirmation);

    void OfferInterruptedWorldGen()
    {
        var store = new LodWorldGenStateStore(LodWorldGenJob.StatePath(sapi));
        if (!store.Exists) return;
        Mod.Logger.Notification("{0}", WorldGenStatusLine());
    }

    static string WorldGenCallerKey(TextCommandCallingArgs args) =>
        args.Caller.Player?.PlayerUID ?? "console";

    static bool WorldGenDiskFits(string savegamePath, string cachePath, int remaining,
        out string reason)
    {
        long saveNeed = (long)remaining * LodWorldGenJob.EstimatedSavegameBytesPerColumn;
        long cacheNeed = (long)remaining * LodWorldGenJob.EstimatedCacheBytesPerColumn;
        try
        {
            string saveRoot = Path.GetPathRoot(Path.GetFullPath(savegamePath))!;
            string cacheRoot = Path.GetPathRoot(Path.GetFullPath(cachePath))!;
            if (string.Equals(saveRoot, cacheRoot, StringComparison.OrdinalIgnoreCase))
            {
                long free = new DriveInfo(saveRoot).AvailableFreeSpace;
                long need = saveNeed + cacheNeed;
                if (free < need)
                {
                    reason = $"the estimate needs {FormatWorldGenBytes(need)}, but {saveRoot} "
                        + $"has {FormatWorldGenBytes(free)} free";
                    return false;
                }
            }
            else
            {
                long saveFree = new DriveInfo(saveRoot).AvailableFreeSpace;
                long cacheFree = new DriveInfo(cacheRoot).AvailableFreeSpace;
                if (saveFree < saveNeed || cacheFree < cacheNeed)
                {
                    reason = "the savegame/cache drive does not have the estimated free space "
                        + $"({FormatWorldGenBytes(saveNeed)} savegame, "
                        + $"{FormatWorldGenBytes(cacheNeed)} cache)";
                    return false;
                }
            }
            reason = "";
            return true;
        }
        catch (Exception e)
        {
            reason = "free disk space could not be checked: " + e.Message;
            return false;
        }
    }

    static string FormatWorldGenBytes(long bytes) => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        bytes >= 1024L * 1024 * 1024 ? "{0:0.0} GiB" : "{0:0} MiB",
        bytes >= 1024L * 1024 * 1024
            ? bytes / (1024.0 * 1024 * 1024)
            : bytes / (1024.0 * 1024));

    static string FormatWorldGenDuration(long seconds)
    {
        if (seconds < 120) return seconds + " seconds";
        if (seconds < 7200) return (seconds + 30) / 60 + " minutes";
        return string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0:0.0} hours", seconds / 3600.0);
    }

    void OnChunkColumnLoaded(Vec2i chunkCoord, IWorldChunk[] chunks)
    {
        sweep?.ObserveLoadedColumn(chunkCoord.X, chunkCoord.Y);
        worldGen?.ObserveLoaded(chunkCoord.X, chunkCoord.Y);
        pipeline?.QueueColumn(chunkCoord.X, chunkCoord.Y); // Vec2i.Y is the Z chunk coord
    }

    void EnsureColumnLoadedSubscription()
    {
        if (columnLoadedSubscribed) return;
        sapi.Event.ChunkColumnLoaded += OnChunkColumnLoaded;
        columnLoadedSubscribed = true;
    }

    void RemoveJobOnlyColumnLoadedSubscription()
    {
        if (!columnLoadedSubscribed || captureDrivenByLoads) return;
        sapi.Event.ChunkColumnLoaded -= OnChunkColumnLoaded;
        columnLoadedSubscribed = false;
    }

    /// <summary>
    /// Vanilla snow commits mark the chunks they changed dirty. Capturing that event gives
    /// loaded terrain an exact, immediate path; the regional scheduler is only responsible
    /// for dormant savegame columns. QueueColumn coalesces the vertical chunks in a column.
    /// </summary>
    void OnChunkDirty(Vec3i chunkCoord, IWorldChunk chunk, EnumChunkDirtyReason reason)
    {
        pipeline?.QueueColumn(chunkCoord.X, chunkCoord.Z);
    }

    /// <summary>
    /// Player edits: the column has to be re-captured or the cache keeps showing terrain
    /// that is no longer there. ChunkColumnLoaded does not fire again for a live column.
    /// </summary>
    void QueueAt(BlockPos? pos)
    {
        if (pos == null) return;
        pipeline!.QueueColumn(pos.X / GlobalConstants.ChunkSize, pos.Z / GlobalConstants.ChunkSize);
    }

    /// <summary>
    /// Flush with the world save rather than only at shutdown: a server that is killed
    /// rather than stopped cleanly would otherwise lose everything since it started.
    /// </summary>
    void OnGameWorldSave()
    {
        worldGen?.Checkpoint();
        if (pipeline?.Active == true) TickPipeline();
    }

    // ---- /vhrefresh: recapture existing terrain without generating missing columns ----

    const int RefreshDefaultRadiusChunks = 32;
    const int RefreshMaxRadiusChunks = 128;

    void RegisterRefreshCommand()
    {
        sapi.ChatCommands.Create("vhrefresh")
            .WithDescription(
                "Reload and recapture existing VintageHorizons LOD terrain around you. "
                + "Missing vanilla terrain is never generated. Use a radius, status, or stop.")
            .RequiresPrivilege(Privilege.controlserver)
            .WithArgs(sapi.ChatCommands.Parsers.OptionalWord("radius|status|stop"))
            .HandleWith(OnRefreshCommand);
    }

    TextCommandResult OnRefreshCommand(TextCommandCallingArgs args)
    {
        string input = Convert.ToString(args.Parsers[0].GetValue())?.Trim() ?? "";
        if (input.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            return TextCommandResult.Success(RefreshStatusLine());
        }
        if (input.Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            if (refresh is not { Done: false })
                return TextCommandResult.Success("[VintageHorizons] No refresh is running.");

            refresh.Cancel();
            return TextCommandResult.Success(
                "[VintageHorizons] Existing-terrain refresh stopping. Already requested "
                + "loads may still complete and be captured.");
        }

        int radius = RefreshDefaultRadiusChunks;
        if (input.Length > 0
            && (!int.TryParse(input, out radius) || radius < 0 || radius > RefreshMaxRadiusChunks))
        {
            return TextCommandResult.Error(
                $"[VintageHorizons] Usage: /vhrefresh [radius 0-{RefreshMaxRadiusChunks}], "
                + "/vhrefresh status, or /vhrefresh stop.");
        }

        return StartRefresh(args, radius);
    }

    string RefreshStatusLine() => refresh != null
        ? "[VintageHorizons] " + refresh.Status
        : "[VintageHorizons] No refresh has run. /vhrefresh [radius] - default "
          + $"{RefreshDefaultRadiusChunks} chunks, max {RefreshMaxRadiusChunks}.";

    TextCommandResult StartRefresh(TextCommandCallingArgs args, int radius)
    {
        if (!Config.EnableCapture)
        {
            return TextCommandResult.Error(
                "[VintageHorizons] Server LOD capture is switched off (EnableCapture in "
                + $"ModConfig/{ConfigFile}), so there is nothing to refresh into.");
        }
        if (!worldReady)
        {
            return TextCommandResult.Error("[VintageHorizons] The world still starts up. Try again shortly.");
        }
        if (refresh is { Done: false })
        {
            return TextCommandResult.Error(
                $"[VintageHorizons] A refresh started by {refresh.StartedBy} is already going: "
                + refresh.Status + ". Use /vhrefresh stop first.");
        }
        if (generate is { Done: false })
        {
            return TextCommandResult.Error(
                "[VintageHorizons] /vhgen is already running. Let it finish or stop it before "
                + "starting a refresh, so both area walkers do not compete.");
        }
        if (worldGen is { Done: false })
        {
            return TextCommandResult.Error(
                "[VintageHorizons] /vhworldgen is already active. Let it finish or stop it "
                + "before starting a refresh, so both area walkers do not compete.");
        }

        var pos = args.Caller.Pos;
        double bx = pos?.X ?? sapi.World.DefaultSpawnPosition.X;
        double bz = pos?.Z ?? sapi.World.DefaultSpawnPosition.Z;
        bool fromSpawn = pos == null;
        int cs = GlobalConstants.ChunkSize;
        string startedBy = args.Caller.Player?.PlayerName ?? "the console";

        refresh = new LodPlayerPregen(sapi, Mod.Logger, OpenPipeline(),
            (int)bx / cs, (int)bz / cs, radius, Config, startedBy,
            Config.SweepColumnsPerSecond, LodPlayerPregen.EnumPurpose.RefreshExisting)
        {
            TrackPhaseAllocations = allocationTelemetryEnabled,
        };
        refresh.Start();

        int columns = LodPlayerPregen.ColumnsFor(radius);
        int seconds = Math.Max(1, LodPlayerPregen.EstimateSeconds(columns, Config.SweepColumnsPerSecond));
        string time = seconds >= 120 ? seconds / 60 + " minutes" : seconds + " seconds";
        return TextCommandResult.Success(
            $"[VintageHorizons] Refreshing existing LOD around {(fromSpawn ? "spawn " : "")}{(int)bx},{(int)bz} "
            + $"out to {radius} chunks ({radius * cs} blocks): examining {columns} columns, "
            + $"roughly {time} if they all exist and the server keeps up. Only safe columns "
            + "already present in the vanilla save are loaded and recaptured; missing terrain "
            + "is never generated. Updated LOD becomes visible after capture, persistence, and "
            + "the next server refresh offer. /vhrefresh status shows progress; /vhrefresh stop cancels.");
    }

    /// <summary>
    /// Durable L0 changes since the assist last checked. Parents are rebuilt by receiving
    /// clients from L0, so sending them too would multiply each snow campaign transfer.
    /// Main-thread producer and consumer; the returned snapshot owns its array.
    /// </summary>
    public long[] TakePersistedLevelZeroChanges()
    {
        if (persistedLevelZeroChanges.Count == 0) return Array.Empty<long>();
        long[] changes = persistedLevelZeroChanges.ToArray();
        persistedLevelZeroChanges.Clear();
        return changes;
    }

    void TickPipeline()
    {
        long now = Environment.TickCount64;
        if (lastPipelineTickMs != 0 && now - lastPipelineTickMs > 250)
            overloadedUntilMs = now + 1000;
        lastPipelineTickMs = now;

        LodPhaseStart phaseStart = LodPhaseCost.Start(allocationTelemetryEnabled);
        pipeline!.Tick();
        pipelineTickCost.Add(phaseStart);

        worldGen?.Tick(now < overloadedUntilMs);
        RefreshWorldGenAuthority();

        if (HasActiveCacheWork())
        {
            if (now < nextActiveWorkStatusLogMs) return;
            Mod.Logger.Notification("{0}", CacheWorkStatusLine);
            nextActiveWorkStatusLogMs = now + ActiveWorkStatusLogIntervalMs;
        }
        else
        {
            // A later job gets a full minute before its first coarse progress line.
            nextActiveWorkStatusLogMs = now + ActiveWorkStatusLogIntervalMs;
        }
    }

    bool HasActiveCacheWork() =>
        sweep is { Done: false }
        || pregen is { Done: false }
        || generate is { Done: false }
        || refresh is { Done: false }
        || worldGen is { Done: false };

    /// <summary>
    /// True while an explicit or bounded job holds the world-generation authority. Automatic
    /// producers read this and yield; they never acquire it (settled decision 39).
    /// </summary>
    public bool GenerationAuthorityBusy
    {
        get
        {
            RefreshWorldGenAuthority();
            return worldGenAuthority.Current != EnumWorldGenAuthority.None;
        }
    }

    void RefreshWorldGenAuthority()
    {
        if (pregen is { Done: true })
            worldGenAuthority.Release(EnumWorldGenAuthority.StartupPregen);
        if (generate is { Done: true })
            worldGenAuthority.Release(EnumWorldGenAuthority.TransientGeneration);
        if (worldGen is { Done: true })
        {
            worldGenAuthority.Release(EnumWorldGenAuthority.WorldGeneration);
            RemoveJobOnlyColumnLoadedSubscription();
        }
    }

    void LogStats()
    {
        if (pipeline == null) return;

        Mod.Logger.Notification(
            "Server stats: pipeline tick p95/p99/max {0:0}/{1:0}/{2:0}us over {3} ticks; "
            + "hitches >=25/50/100ms {4}/{5}/{6}",
            pipelineTickCost.P95Us, pipelineTickCost.P99Us, pipelineTickCost.MaxUs,
            pipelineTickCost.Calls, pipelineTickCost.Over25Ms, pipelineTickCost.Over50Ms,
            pipelineTickCost.Over100Ms);

        Mod.Logger.Notification(
            "  server pipeline p95/p99/max us: foreign {0:0}/{1:0}/{2:0} | load {3:0}/{4:0}/{5:0} | "
            + "capture schedule {6:0}/{7:0}/{8:0} | capture apply {9:0}/{10:0}/{11:0} | "
            + "mip apply {12:0}/{13:0}/{14:0} | mip schedule {15:0}/{16:0}/{17:0} | "
            + "save {18:0}/{19:0}/{20:0}",
            pipeline.ForeignInstallCost.P95Us, pipeline.ForeignInstallCost.P99Us, pipeline.ForeignInstallCost.MaxUs,
            pipeline.LoadInstallCost.P95Us, pipeline.LoadInstallCost.P99Us, pipeline.LoadInstallCost.MaxUs,
            pipeline.CaptureScheduleCost.P95Us, pipeline.CaptureScheduleCost.P99Us, pipeline.CaptureScheduleCost.MaxUs,
            pipeline.CaptureApplyCost.P95Us, pipeline.CaptureApplyCost.P99Us, pipeline.CaptureApplyCost.MaxUs,
            pipeline.MipApplyCost.P95Us, pipeline.MipApplyCost.P99Us, pipeline.MipApplyCost.MaxUs,
            pipeline.MipScheduleCost.P95Us, pipeline.MipScheduleCost.P99Us, pipeline.MipScheduleCost.MaxUs,
            pipeline.SaveSnapshotCost.P95Us, pipeline.SaveSnapshotCost.P99Us, pipeline.SaveSnapshotCost.MaxUs);

        LogSweepStats("sweep", sweep);
        LogGenerationStats("startup generation", pregen);
        LogGenerationStats("command generation", generate);
        LogGenerationStats("command refresh", refresh);

        Mod.Logger.Notification(
            "  server allocation interval MiB/max KiB: pipeline tick {0:0.00}/{1:0.0}",
            pipelineTickCost.AllocatedBytes / (1024.0 * 1024.0),
            pipelineTickCost.MaxAllocatedBytes / 1024.0);

        Mod.Logger.Notification(
            "  server pipeline allocation interval MiB/max KiB: foreign {0:0.00}/{1:0.0} | "
            + "load {2:0.00}/{3:0.0} | capture schedule {4:0.00}/{5:0.0} | capture apply "
            + "{6:0.00}/{7:0.0} | mip apply {8:0.00}/{9:0.0} | mip schedule "
            + "{10:0.00}/{11:0.0} | save {12:0.00}/{13:0.0}",
            MiB(pipeline.ForeignInstallCost.AllocatedBytes), KiB(pipeline.ForeignInstallCost.MaxAllocatedBytes),
            MiB(pipeline.LoadInstallCost.AllocatedBytes), KiB(pipeline.LoadInstallCost.MaxAllocatedBytes),
            MiB(pipeline.CaptureScheduleCost.AllocatedBytes), KiB(pipeline.CaptureScheduleCost.MaxAllocatedBytes),
            MiB(pipeline.CaptureApplyCost.AllocatedBytes), KiB(pipeline.CaptureApplyCost.MaxAllocatedBytes),
            MiB(pipeline.MipApplyCost.AllocatedBytes), KiB(pipeline.MipApplyCost.MaxAllocatedBytes),
            MiB(pipeline.MipScheduleCost.AllocatedBytes), KiB(pipeline.MipScheduleCost.MaxAllocatedBytes),
            MiB(pipeline.SaveSnapshotCost.AllocatedBytes), KiB(pipeline.SaveSnapshotCost.MaxAllocatedBytes));

        pipelineTickCost.Reset();
        pipeline.ResetPhaseCosts();
        sweep?.ResetPhaseCosts();
        pregen?.ResetPhaseCosts();
        generate?.ResetPhaseCosts();
        refresh?.ResetPhaseCosts();
    }

    void LogSweepStats(string label, LodSavegameSweep? work)
    {
        if (work == null || work.ProbeIssueCost.Calls + work.ProbePublishCost.Calls
            + work.LoadIssueCost.Calls + work.RefreshIssueCost.Calls == 0) return;

        Mod.Logger.Notification(
            "  {0} p95/p99/max us: probe issue {1:0}/{2:0}/{3:0} | probe publish "
            + "{4:0}/{5:0}/{6:0} | load issue {7:0}/{8:0}/{9:0} | refresh issue "
            + "{10:0}/{11:0}/{12:0}",
            label,
            work.ProbeIssueCost.P95Us, work.ProbeIssueCost.P99Us, work.ProbeIssueCost.MaxUs,
            work.ProbePublishCost.P95Us, work.ProbePublishCost.P99Us, work.ProbePublishCost.MaxUs,
            work.LoadIssueCost.P95Us, work.LoadIssueCost.P99Us, work.LoadIssueCost.MaxUs,
            work.RefreshIssueCost.P95Us, work.RefreshIssueCost.P99Us, work.RefreshIssueCost.MaxUs);

        Mod.Logger.Notification(
            "  {0} allocation interval MiB/max KiB: probe issue {1:0.00}/{2:0.0} | "
            + "probe publish {3:0.00}/{4:0.0} | load issue {5:0.00}/{6:0.0} | "
            + "refresh issue {7:0.00}/{8:0.0}",
            label,
            MiB(work.ProbeIssueCost.AllocatedBytes), KiB(work.ProbeIssueCost.MaxAllocatedBytes),
            MiB(work.ProbePublishCost.AllocatedBytes), KiB(work.ProbePublishCost.MaxAllocatedBytes),
            MiB(work.LoadIssueCost.AllocatedBytes), KiB(work.LoadIssueCost.MaxAllocatedBytes),
            MiB(work.RefreshIssueCost.AllocatedBytes), KiB(work.RefreshIssueCost.MaxAllocatedBytes));
    }

    void LogGenerationStats(string label, LodPlayerPregen? work)
    {
        if (work == null || work.ProbeIssueCost.Calls + work.ProbePublishCost.Calls
            + work.WorkIssueCost.Calls == 0) return;

        Mod.Logger.Notification(
            "  {0} p95/p99/max us: probe issue {1:0}/{2:0}/{3:0} | probe publish "
            + "{4:0}/{5:0}/{6:0} | work issue {7:0}/{8:0}/{9:0}",
            label,
            work.ProbeIssueCost.P95Us, work.ProbeIssueCost.P99Us, work.ProbeIssueCost.MaxUs,
            work.ProbePublishCost.P95Us, work.ProbePublishCost.P99Us, work.ProbePublishCost.MaxUs,
            work.WorkIssueCost.P95Us, work.WorkIssueCost.P99Us, work.WorkIssueCost.MaxUs);

        Mod.Logger.Notification(
            "  {0} allocation interval MiB/max KiB: probe issue {1:0.00}/{2:0.0} | "
            + "probe publish {3:0.00}/{4:0.0} | work issue {5:0.00}/{6:0.0}",
            label,
            MiB(work.ProbeIssueCost.AllocatedBytes), KiB(work.ProbeIssueCost.MaxAllocatedBytes),
            MiB(work.ProbePublishCost.AllocatedBytes), KiB(work.ProbePublishCost.MaxAllocatedBytes),
            MiB(work.WorkIssueCost.AllocatedBytes), KiB(work.WorkIssueCost.MaxAllocatedBytes));
    }

    static double MiB(long bytes) => bytes / (1024.0 * 1024.0);
    static double KiB(long bytes) => bytes / 1024.0;

    public override void Dispose()
    {
        // Before the pipeline closes, or a peek landing during shutdown would enqueue a
        // capture into a closed pipeline. CaptureColumn also refuses once Active drops,
        // as the second layer of the same guard.
        generate?.Cancel();
        refresh?.Cancel();
        sweep?.Stop();
        worldGen?.Shutdown();

        if (sapi != null)
        {
            if (columnLoadedSubscribed) sapi.Event.ChunkColumnLoaded -= OnChunkColumnLoaded;
            sapi.Event.ChunkDirty -= OnChunkDirty;
            sapi.Event.GameWorldSave -= OnGameWorldSave;
            sapi.Event.UnregisterGameTickListener(tickListenerId);
            if (statsRegistered) sapi.Event.UnregisterGameTickListener(statsListenerId);
        }
        pipeline?.Close();
        pipeline?.Dispose();
        pipeline = null;
        persistedLevelZeroChanges.Clear();
        pendingWorldGens.Clear();
    }
}
