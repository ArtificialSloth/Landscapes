using System.Diagnostics;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using VintageHorizons.Silhouette;

namespace VintageHorizons.Net;

/// <summary>
/// Server half of the optional assist (DESIGN.md §10). Stage 1 answers the handshake and
/// nothing else, which is the point: it proves the mod can go Universal without changing
/// anything for anyone, before any terrain is put on the wire.
///
/// This is a separate ModSystem rather than a branch inside the client one. The client
/// system casts World to ClientMain, compiles shaders and registers a renderer; the
/// robust guarantee that none of that runs on a server is that the code is not there,
/// not a side check that one refactor could get wrong.
/// </summary>
public class LodAssistServerSystem : ModSystem
{
    ICoreServerAPI sapi = null!;
    IServerNetworkChannel channel = null!;
    bool allocationTelemetryEnabled;

    /// <summary>Assist costs since the last server telemetry report.</summary>
    public LodPhaseCost ServeCost, ServeSetupCost, ServePublishCost, ServeAdmissionCost,
        ServeWithCollectionCost, ServeWithoutCollectionCost, BlobReadCost, SectionSendCost,
        SendWithCollectionCost, SendWithoutCollectionCost, OfferCost;
    LodAssistBlobReader? blobReader;
    string? blobReaderPath;

    /// <summary>
    /// Horizon silhouettes: created when the world runs, where the server config allows them
    /// (singleplayer and LAN-hosted worlds by default, dedicated when explicitly enabled), or on a dedicated test server that
    /// measures their heights (LodServerConfig.MeasureSilhouettesVariable).
    /// </summary>
    LodSilhouetteBuilder? silhouettes;

    /// <summary>
    /// Whether players are offered silhouettes. The feature bit is advertised, and requests are
    /// answered, only when this is set: a server that only measures heights offers nothing.
    /// </summary>
    bool silhouettesOffered;

    // Per-call attribution is populated only while the owning-thread serve callback is
    // active. Send cost is nested inside publication/admission; the other three costs
    // partition the outer callback. Collection counts are sampled only in explicit stats
    // sessions because three GC counter reads on every packet are diagnostics, not free.
    bool serveMeasurementActive;
    long currentServeSetupTicks, currentServePublishTicks, currentServeAdmissionTicks;
    long currentServeSendTicks, currentServeMaxSendTicks, currentServeSendBytes;
    int currentServeSendCount, currentServeGen0, currentServeGen1, currentServeGen2;
    long serveCallsWithCollection, serveCallsWithoutCollection;
    long serveGen0Collections, serveGen1Collections, serveGen2Collections;
    long sendCallsWithCollection, sendCallsWithoutCollection;
    long sendGen0Collections, sendGen1Collections, sendGen2Collections;
    const double AssistTailLogThresholdMs = ServeWorkBudgetMs;

    public override bool ShouldLoad(EnumAppSide forSide) => forSide == EnumAppSide.Server;

    public override void StartServerSide(ICoreServerAPI api)
    {
        sapi = api;
        allocationTelemetryEnabled =
            Environment.GetEnvironmentVariable("VINTAGEHORIZONS_STATS") == "1";
        channel = api.Network.RegisterChannel(LodAssist.ChannelName)
            .RegisterMessageType<AssistHello>()
            .RegisterMessageType<AssistWelcome>()
            .RegisterMessageType<AssistKeyManifest>()
            .RegisterMessageType<AssistSectionRequest>()
            .RegisterMessageType<AssistSection>()
            .RegisterMessageType<AssistWorldGenRequest>()
            .RegisterMessageType<AssistWorldGenStatus>()
            .RegisterMessageType<AssistSilhouetteRequest>()
            .RegisterMessageType<AssistSilhouetteManifest>()
            .RegisterMessageType<AssistSilhouetteTileRequest>()
            .RegisterMessageType<AssistSilhouetteTile>()
            .SetMessageHandler<AssistHello>(OnHello)
            .SetMessageHandler<AssistSectionRequest>(OnSectionRequest)
            .SetMessageHandler<AssistWorldGenRequest>(OnWorldGenRequest)
            .SetMessageHandler<AssistSilhouetteRequest>(OnSilhouetteRequest)
            .SetMessageHandler<AssistSilhouetteTileRequest>(OnSilhouetteTileRequest);

        // Decided when the world runs rather than here: the server config is loaded by the
        // capture system, and nothing orders its start before this one's.
        api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, StartSilhouettes);

        // Accumulate the configured per-second rates across normal ticks. Each allowance
        // is capped at one tick's share, so a slow tick cannot cause a catch-up burst.
        api.Event.RegisterGameTickListener(_ => MeasureServePending(), LodTickAllowance.TickMilliseconds);

        // Match the persistence checkpoint. New rows cannot become servable more often,
        // and this walks the complete key set on the server thread; scanning every five
        // seconds only created a periodic tick spike and advertised not-yet-durable rows.
        api.Event.RegisterGameTickListener(_ => MeasureOfferNewKeys(), 30000);
        if (allocationTelemetryEnabled)
            api.Event.RegisterGameTickListener(_ => LogStats(), 15000);

        api.Event.PlayerDisconnect += OnPlayerDisconnect;

        api.ChatCommands.Create("vhserver")
            .WithDescription("VintageHorizons server assist status")
            .RequiresPrivilege(Privilege.controlserver)
            .HandleWith(_ =>
            {
                LodServerCaptureSystem? capture = api.ModLoader.GetModSystem<LodServerCaptureSystem>();
                LodServerConfig config = capture?.Config ?? new LodServerConfig();
                var lines = new List<string>
                {
                    $"[VintageHorizons] {config.Describe()}. Cache: {capture?.SectionCount ?? 0} sections, "
                    + $"{capture?.ColumnsCaptured ?? 0} columns captured. Served {sectionsServed} sections "
                    + $"({bytesServed / 1e6:0.0} MB, {(sectionsServed > 0 ? blobReadMs / sectionsServed : 0):0.00}ms avg read), "
                    + $"{sectionsOutsideRadius} refused as out of radius, {sectionsRefused} refused unserved, "
                    + $"{pendingByPlayer.Count} players waiting, "
                    + $"{manifestDeltasSent} new-key and {refreshManifestsSent} refresh offers "
                    + "sent to connected players.",
                    capture?.CacheWorkStatusLine
                        ?? LodServerCaptureSystem.FormatCacheWorkStatus(0, 0, 0, 0, 0, 0, 0, 0),
                };
                if (capture?.SweepStatus is string sw) lines.Add("[VintageHorizons] " + sw + ".");
                if (capture?.PregenStatus is string pg) lines.Add("[VintageHorizons] " + pg + ".");
                if (capture?.GenerateStatus is string gn) lines.Add("[VintageHorizons] " + gn + ".");
                if (capture?.RefreshStatus is string rf) lines.Add("[VintageHorizons] " + rf + ".");
                if (capture?.WorldGenStatus is string wg) lines.Add(wg);
                lines.Add("[VintageHorizons] Settings live in "
                    + "ModConfig/vintagehorizons-server.json (restart to apply).");

                foreach (string line in lines) Mod.Logger.Notification("{0}", line);
                return TextCommandResult.Success(string.Join(Environment.NewLine, lines));
            });

        Mod.Logger.Notification(
            "VintageHorizons {0} server assist listening. Players without the mod are "
            + "unaffected and do not need to install anything.",
            Mod.Info.Version);
    }

    void OnHello(IServerPlayer fromPlayer, AssistHello msg)
    {
        Mod.Logger.Debug("VintageHorizons: assist hello from {0} (client {1}, protocol {2})",
            fromPlayer.PlayerName, msg.ModVersion, msg.Protocol);

        // Answered from the main thread, one tick later, rather than from here. Message
        // handlers do not run on the main thread, and both the key set and its count come
        // from a HashSet the capture pipeline mutates every tick - reading it here is a
        // torn read, and the count would disagree with the manifest that follows it
        // (observed: announced 5634, sent 5638, four sections captured in between).
        sapi.Event.EnqueueMainThreadTask(() => Answer(fromPlayer), "vintagehorizons-hello");
    }

    /// <summary>
    /// Welcome plus the key manifest, from one snapshot so the announced count is a fact
    /// rather than an estimate. Enabled stays false until sections can actually move:
    /// reporting true would leave a client waiting for terrain that is not coming.
    /// </summary>
    void Answer(IServerPlayer player)
    {
        LodServerCaptureSystem? capture = sapi.ModLoader.GetModSystem<LodServerCaptureSystem>();
        LodServerConfig config = capture?.Config ?? new LodServerConfig();
        bool capturing = capture?.Capturing == true;
        bool serving = capturing && config.EnableServing;
        long[] keys = serving ? capture!.SnapshotKeys() : Array.Empty<long>();

        (bool enabled, string status) = AssistGreeting.Describe(
            capturing, serving, keys.Length, config.ServeRadiusBlocks);

        channel.SendPacket(new AssistWelcome
        {
            Protocol = LodAssist.Protocol,
            ModVersion = Mod.Info.Version,
            Enabled = enabled,
            Status = status,
            ManifestKeyCount = keys.Length,
            ServerSettings = AssistServerSettings.From(config),
            Features = (capture != null && config.EnableCapture && config.WorldGenerationEnabled
                    ? LodAssist.FeatureWorldGeneration : 0)
                | (silhouettesOffered ? LodAssist.FeatureSilhouetteTiles : 0),
            CanControlWorldGeneration = player.HasPrivilege(Privilege.controlserver),
        }, player);

        if (keys.Length > 0) SendManifest(player, keys);

        // Recorded even when the cache was empty just now: that is the case that most
        // needs the follow-up offers.
        ledger.Greet(player.PlayerUID, keys);
    }

    void OnWorldGenRequest(IServerPlayer fromPlayer, AssistWorldGenRequest request)
    {
        // Network callbacks are not the server owner thread. The generator, its confirmation
        // ledger and every engine query below are owner-thread state.
        sapi.Event.EnqueueMainThreadTask(() => HandleWorldGenRequest(fromPlayer, request),
            "vintagehorizons-worldgen-request");
    }

    void OnSilhouetteRequest(IServerPlayer fromPlayer, AssistSilhouetteRequest request)
    {
        // Off the owner thread, like every network callback; the builder is owner-thread state.
        sapi.Event.EnqueueMainThreadTask(() =>
        {
            if (silhouettes == null || !silhouettesOffered || !ledger.HasGreeted(fromPlayer.PlayerUID)) return;
            silhouettes.Request(fromPlayer, request);
        }, "vintagehorizons-silhouette-request");
    }

    void OnSilhouetteTileRequest(IServerPlayer fromPlayer, AssistSilhouetteTileRequest request)
    {
        sapi.Event.EnqueueMainThreadTask(() =>
        {
            if (silhouettes == null || !silhouettesOffered || !ledger.HasGreeted(fromPlayer.PlayerUID))
                return;
            silhouettes.RequestTiles(fromPlayer, request);
        }, "vintagehorizons-silhouette-tile-request");
    }

    bool SendSilhouetteManifest(IServerPlayer player, AssistSilhouetteManifest manifest)
    {
        try
        {
            channel.SendPacket(manifest, player);
            return true;
        }
        catch (Exception e)
        {
            Mod.Logger.Warning("Horizon silhouettes: a manifest could not be sent to {0}: {1}",
                player.PlayerName, e.Message);
            return false;
        }
    }

    bool SendSilhouetteTile(IServerPlayer player, AssistSilhouetteTile tile)
    {
        try
        {
            channel.SendPacket(tile, player);
            return true;
        }
        catch (Exception e)
        {
            Mod.Logger.Warning("Horizon silhouettes: a tile reply could not be sent to {0}: {1}",
                player.PlayerName, e.Message);
            return false;
        }
    }

    void HandleWorldGenRequest(IServerPlayer player, AssistWorldGenRequest request)
    {
        LodServerCaptureSystem? capture = sapi.ModLoader.GetModSystem<LodServerCaptureSystem>();
        bool canControl = player.HasPrivilege(Privilege.controlserver);
        bool success = true;
        string message = "";

        string? refusal = WorldGenRequestRefusal(
            ledger.HasGreeted(player.PlayerUID), capture != null, canControl, request.Action);
        if (refusal != null)
        {
            success = false;
            message = refusal;
        }
        else
        {
            LodServerCaptureSystem owner = capture!;
            (success, message) = request.Action switch
            {
                EnumWorldGenControlAction.Status => (true, ""),
                EnumWorldGenControlAction.Prepare => owner.WorldGenPrepare(
                    player.PlayerUID, player.PlayerName,
                    player.Entity?.Pos.X, player.Entity?.Pos.Z,
                    request.RadiusChunks, request.Shape, request.CentreMode,
                    request.X, request.Z, request.Preset ?? "Balanced"),
                EnumWorldGenControlAction.Confirm => owner.WorldGenConfirm(player.PlayerUID),
                EnumWorldGenControlAction.Pause => owner.WorldGenPause(),
                EnumWorldGenControlAction.Resume => owner.WorldGenResume(),
                EnumWorldGenControlAction.Stop => owner.WorldGenStop(player.PlayerUID),
                _ => (false, "[VintageHorizons] The Generate page sent an unknown action."),
            };
        }

        AssistWorldGenStatus status = capture?.WorldGenPageStatus(
            player.PlayerUID, canControl, request.RequestId, success, message)
            ?? new AssistWorldGenStatus
            {
                RequestId = request.RequestId,
                Success = false,
                Message = message,
                State = EnumWorldGenRemoteState.Unavailable,
                CanControl = canControl,
            };
        status.Action = request.Action;
        if (!success)
        {
            Mod.Logger.Warning(
                "Generate World {0} request from {1} did not succeed: {2}",
                request.Action, player.PlayerName, message);
        }
        channel.SendPacket(status, player);
    }

    internal static string? WorldGenRequestRefusal(bool greeted, bool captureAvailable,
        bool canControl, EnumWorldGenControlAction action)
    {
        if (!greeted)
            return "[VintageHorizons] The Generate page has not completed its server handshake.";
        if (!captureAvailable)
            return "[VintageHorizons] Savegame world generation is unavailable on this server.";
        if (!Enum.IsDefined(action))
            return "[VintageHorizons] The Generate page sent an unknown action.";
        if (action != EnumWorldGenControlAction.Status && !canControl)
            return "[VintageHorizons] You may view progress, but controlserver permission is required to change it.";
        return null;
    }

    readonly ManifestLedger ledger = new();

    /// <summary>
    /// Tell connected players about sections the cache gained since they joined.
    ///
    /// The manifest used to be sent once, on the greeting, and that was the only one a
    /// client ever got. An admin running /vhgen while people were online therefore built
    /// terrain none of them could ask for: a client only requests keys it has been
    /// offered, so ".vhwhy" reported "no-data" for ground the server had held for hours,
    /// and relogging was the only cure. Reported from the field against 0.2.0. A sweep
    /// that finishes late, and ordinary exploring by other players, did the same.
    ///
    /// The client merges every manifest it receives and only ever adds keys, so a later
    /// message is a delta already and this needs no protocol change.
    /// </summary>
    void OfferNewKeys()
    {
        if (ledger.GreetedCount == 0) return;

        LodServerCaptureSystem? capture = sapi.ModLoader.GetModSystem<LodServerCaptureSystem>();
        if (capture?.Capturing != true || capture.Config.EnableServing != true) return;

        long[] delta = ledger.Delta(capture.SnapshotKeys());
        if (delta.Length == 0) return;

        int told = 0;
        foreach (IPlayer online in sapi.World.AllOnlinePlayers)
        {
            if (online is not IServerPlayer player || !ledger.HasGreeted(player.PlayerUID)) continue;
            SendManifest(player, delta);
            told++;
        }

        if (told == 0) return;
        manifestDeltasSent += told;
        Mod.Logger.Debug("VintageHorizons: offered {0} newly cached sections to {1} player(s)",
            delta.Length, told);
    }

    /// <summary>
    /// Tell current clients that durable L0 contents changed. This is separate from the
    /// monotone key ledger: the key is old, its blob is new. One append-only manifest flag
    /// lets old clients safely ignore the distinction while new clients request a replacement.
    /// </summary>
    void OfferUpdatedKeys()
    {
        LodServerCaptureSystem? capture = sapi.ModLoader.GetModSystem<LodServerCaptureSystem>();
        if (capture?.Capturing != true) return;

        // Drain even with no listeners or with serving disabled. A later greeting receives
        // the current rows through the ordinary full manifest and needs no history replay.
        long[] updated = capture.TakePersistedLevelZeroChanges();
        if (updated.Length == 0 || ledger.GreetedCount == 0
            || capture.Config.EnableServing != true) return;

        int told = 0;
        foreach (IPlayer online in sapi.World.AllOnlinePlayers)
        {
            if (online is not IServerPlayer player || !ledger.HasGreeted(player.PlayerUID)) continue;
            SendManifest(player, updated, refresh: true);
            told++;
        }

        refreshManifestsSent += told;
        if (told > 0)
            Mod.Logger.Debug("VintageHorizons: offered {0} refreshed L0 sections to {1} player(s)",
                updated.Length, told);
    }

    long manifestDeltasSent;
    long refreshManifestsSent;

    void OnPlayerDisconnect(IServerPlayer player)
    {
        ledger.Forget(player.PlayerUID);
        silhouettes?.Forget(player.PlayerUID);
        pendingByPlayer.Remove(player.PlayerUID);
        serveAllowanceByPlayer.Remove(player.PlayerUID);
        sessionByPlayer.Remove(player.PlayerUID);
        readInFlightByPlayer.Remove(player.PlayerUID);
    }

    /// <summary>
    /// Pending section requests, per player, oldest first. Held here rather than answered
    /// inline so the per-second cap has something to meter, and so a player who asks for a
    /// hundred sections gets them steadily instead of in one spike.
    /// </summary>
    readonly record struct PendingSection(long Key, long ReadyMs, long Session);
    readonly record struct InFlightReadBatch(long Session, int Count);
    readonly Dictionary<string, Queue<PendingSection>> pendingByPlayer = new();
    readonly Dictionary<string, LodTickAllowance> serveAllowanceByPlayer = new();
    readonly Dictionary<string, long> sessionByPlayer = new();
    readonly Dictionary<string, InFlightReadBatch> readInFlightByPlayer = new();
    readonly LodTickAllowance globalServeAllowance = new();
    const double ServeWorkBudgetMs = 2.0;
    long nextSession;

    void OnSectionRequest(IServerPlayer fromPlayer, AssistSectionRequest msg)
    {
        if (msg.Keys == null || msg.Keys.Length == 0) return;

        // Onto the main thread for the same reason as the manifest: this touches shared
        // state, and the blob read has to be ordered against the capture that writes it.
        long[] keys = msg.Keys;
        string uid = fromPlayer.PlayerUID;
        sapi.Event.EnqueueMainThreadTask(() =>
        {
            if (!sessionByPlayer.TryGetValue(uid, out long session))
                sessionByPlayer[uid] = session = ++nextSession;

            if (!pendingByPlayer.TryGetValue(uid, out Queue<PendingSection>? queue))
            {
                pendingByPlayer[uid] = queue = new Queue<PendingSection>();
            }

            // Bounded: the client is supposed to limit itself, but a server must not
            // depend on a client behaving.
            int held = queue.Count + (readInFlightByPlayer.TryGetValue(
                uid, out InFlightReadBatch batch) ? batch.Count : 0);
            int room = Math.Max(0, MaxQueuedPerPlayer - held);
            long readyMs = sapi.World.ElapsedMilliseconds;
            foreach (long key in keys.Take(room))
                queue.Enqueue(new PendingSection(key, readyMs, session));

            // Anything past the cap is refused OUT LOUD. This used to drop silently,
            // with a comment saying the client would re-ask - it cannot. A client marks
            // a key in flight when it sends it and only forgets it when a reply comes
            // back, so a dropped key is stranded for the session. With a 16-key in-flight
            // cap that is the whole client, permanently stuck. Same rule as M7 stage 4:
            // only keys actually answered may be forgotten.
            foreach (long key in keys.Skip(room)) Refuse(fromPlayer, key);
        }, "vintagehorizons-request");
    }

    const int MaxQueuedPerPlayer = 256;

    /// <summary>
    /// Tell a client we will not serve this key. An empty blob is the refusal, and the
    /// client needs it: silence is indistinguishable from a lost packet, and it leaves
    /// the key in flight forever.
    /// </summary>
    void Refuse(IServerPlayer player, long key)
    {
        SendSection(player, new AssistSection { Key = key });
        sectionsRefused++;
    }

    int sectionsRefused;

    /// <summary>
    /// Serve pending requests under continuously accrued per-player and global rates.
    /// Blob reads execute on their dedicated reader; this tick only publishes completed
    /// results and admits more FIFO work under a small elapsed-time ceiling.
    /// </summary>
    void MeasureServePending()
    {
        if (!allocationTelemetryEnabled)
        {
            LodPhaseStart phaseStart = LodPhaseCost.Start();
            ServePending();
            ServeCost.Add(phaseStart);
            return;
        }

        long started = Stopwatch.GetTimestamp();
        long allocationStart = GC.GetAllocatedBytesForCurrentThread();
        int gen0Start = GC.CollectionCount(0);
        int gen1Start = GC.CollectionCount(1);
        int gen2Start = GC.CollectionCount(2);

        currentServeSetupTicks = 0;
        currentServePublishTicks = 0;
        currentServeAdmissionTicks = 0;
        currentServeSendTicks = 0;
        currentServeMaxSendTicks = 0;
        currentServeSendBytes = 0;
        currentServeSendCount = 0;
        currentServeGen0 = 0;
        currentServeGen1 = 0;
        currentServeGen2 = 0;
        serveMeasurementActive = true;
        try
        {
            ServePending();
        }
        finally
        {
            serveMeasurementActive = false;
        }

        long elapsed = Stopwatch.GetTimestamp() - started;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        ServeCost.AddSample(elapsed, allocated);

        int gen0 = GC.CollectionCount(0) - gen0Start;
        int gen1 = GC.CollectionCount(1) - gen1Start;
        int gen2 = GC.CollectionCount(2) - gen2Start;
        if (gen0 + gen1 + gen2 > 0)
        {
            ServeWithCollectionCost.AddSample(elapsed, allocated);
            serveCallsWithCollection++;
        }
        else
        {
            ServeWithoutCollectionCost.AddSample(elapsed, allocated);
            serveCallsWithoutCollection++;
        }
        serveGen0Collections += gen0;
        serveGen1Collections += gen1;
        serveGen2Collections += gen2;

        double elapsedMs = elapsed * 1000.0 / Stopwatch.Frequency;
        if (elapsedMs < AssistTailLogThresholdMs) return;

        Mod.Logger.Notification(
            "Assist serve tail {0:0.000}ms: setup {1:0.000}, publish {2:0.000}, "
            + "admit {3:0.000}; {4} sends {5:0.00} MiB total/max {6:0.000}/{7:0.000}ms; "
            + "GC callback {8}/{9}/{10}, send {11}/{12}/{13}; {14:0.0} KiB allocated",
            elapsedMs,
            TicksToMilliseconds(currentServeSetupTicks),
            TicksToMilliseconds(currentServePublishTicks),
            TicksToMilliseconds(currentServeAdmissionTicks),
            currentServeSendCount, currentServeSendBytes / (1024.0 * 1024.0),
            TicksToMilliseconds(currentServeSendTicks),
            TicksToMilliseconds(currentServeMaxSendTicks),
            gen0, gen1, gen2,
            currentServeGen0, currentServeGen1, currentServeGen2,
            allocated / 1024.0);
    }

    void ServePending()
    {
        long phaseStarted = allocationTelemetryEnabled ? Stopwatch.GetTimestamp() : 0;
        LodServerCaptureSystem? capture = sapi.ModLoader.GetModSystem<LodServerCaptureSystem>();
        LodServerConfig config = capture?.Config ?? new LodServerConfig();
        LodAssistBlobReader? reader = EnsureBlobReader(capture);
        var tickBudget = new LodWorkBudget(ServeWorkBudgetMs);
        if (allocationTelemetryEnabled)
        {
            currentServeSetupTicks = Stopwatch.GetTimestamp() - phaseStarted;
            ServeSetupCost.AddElapsedTicks(currentServeSetupTicks);
        }

        if (allocationTelemetryEnabled) phaseStarted = Stopwatch.GetTimestamp();
        PublishBlobResults(capture, tickBudget);
        if (allocationTelemetryEnabled)
        {
            currentServePublishTicks = Stopwatch.GetTimestamp() - phaseStarted;
            ServePublishCost.AddElapsedTicks(currentServePublishTicks);
        }
        if (tickBudget.Expired) return;

        if (allocationTelemetryEnabled) phaseStarted = Stopwatch.GetTimestamp();
        AdmitPending(capture, config, reader, tickBudget);
        if (allocationTelemetryEnabled)
        {
            currentServeAdmissionTicks = Stopwatch.GetTimestamp() - phaseStarted;
            ServeAdmissionCost.AddElapsedTicks(currentServeAdmissionTicks);
        }
    }

    void AdmitPending(LodServerCaptureSystem? capture, LodServerConfig config,
        LodAssistBlobReader? reader, LodWorkBudget tickBudget)
    {

        long now = sapi.World.ElapsedMilliseconds;
        int globalBudget = globalServeAllowance.Available(now, config.MaxSectionsPerSecondTotal);
        if (pendingByPlayer.Count == 0 || globalBudget == 0) return;
        bool serving = capture?.Capturing == true && config.EnableServing && reader != null;

        // Round-robin from a rotating start, so the global budget below cannot be
        // monopolised by whichever player happens to sort first in the dictionary.
        List<string> uids = pendingByPlayer.Keys.ToList();
        uids.Sort(StringComparer.Ordinal);
        int start = uids.Count == 0 ? 0 : (int)(serveRound++ % (uint)uids.Count);

        int globalSpent = 0;
        List<string>? emptied = null;

        for (int n = 0; n < uids.Count && globalSpent < globalBudget && !tickBudget.Expired; n++)
        {
            string uid = uids[(start + n) % uids.Count];
            Queue<PendingSection> queue = pendingByPlayer[uid];

            if (sapi.World.PlayerByUid(uid) is not IServerPlayer player
                || player.ConnectionState != EnumClientState.Playing)
            {
                // A section request can arrive during join before PlayerByUid exposes the
                // player as Playing. The 50 ms serve cadence makes that race much easier to
                // hit than the old once-per-second callback. Dropping the queue here sends no
                // reply and strands every client in-flight slot. A real disconnect already
                // removes the bounded queue in OnPlayerDisconnect, so retain this transient
                // state and try it again on the next tick.
                continue;
            }

            // A player's responses stay in request order. Do not let a later refusal or
            // fast read overtake the one SQLite read already accepted for this player.
            if (readInFlightByPlayer.ContainsKey(uid)) continue;

            if (!serveAllowanceByPlayer.TryGetValue(uid, out LodTickAllowance? playerAllowance))
                serveAllowanceByPlayer[uid] = playerAllowance = new LodTickAllowance();
            int playerBudget = playerAllowance.Available(now, config.MaxSectionsPerSecondPerPlayer);
            int playerSpent = 0;
            int readsQueued = 0;
            while (playerSpent < playerBudget && globalSpent < globalBudget
                && queue.Count > 0 && !tickBudget.Expired)
            {
                PendingSection pending = queue.Peek();
                long key = pending.Key;

                if (!sessionByPlayer.TryGetValue(uid, out long session)
                    || session != pending.Session)
                {
                    // This belongs to a connection that has already gone away. Its
                    // disconnect owns cleanup, and a new player with the same uid must
                    // never receive the stale reply.
                    queue.Dequeue();
                    continue;
                }

                // Refuse gradually rather than clearing every queue in one callback. This
                // path is reachable during join before the cache opens and when serving is
                // disabled; every request still receives the explicit terminal response.
                if (!serving)
                {
                    if (readsQueued > 0) break;
                    queue.Dequeue();
                    Refuse(player, key);
                    playerSpent++;
                    globalSpent++;
                    continue;
                }

                // Radius is checked here, against where the player is NOW, rather than when
                // the request was queued: a request that waited in the queue must not be
                // honoured for somewhere the player has since left.
                if (!WithinServeRadius(key, player, config.ServeRadiusBlocks))
                {
                    if (readsQueued > 0) break;
                    queue.Dequeue();
                    SendSection(player, new AssistSection { Key = key });
                    sectionsOutsideRadius++;
                    playerSpent++;
                    globalSpent++;
                    continue;
                }

                if (!reader!.TryEnqueue(new LodAssistBlobReadRequest(
                    uid, session, key, Environment.TickCount64))) break;

                queue.Dequeue();
                readsQueued++;
                readInFlightByPlayer[uid] = new InFlightReadBatch(session, readsQueued);
                playerSpent++;
                globalSpent++;
            }
            playerAllowance.Spend(playerSpent);

            if (queue.Count == 0) (emptied ??= new List<string>()).Add(uid);
        }

        globalServeAllowance.Spend(globalSpent);

        if (emptied != null) foreach (string uid in emptied) pendingByPlayer.Remove(uid);

        // Cumulative progress is reported by the opt-in stats callback and /vhserver.
        // Do not write a synchronous notification from this 50 ms owning-thread path:
        // the 200-section progress line itself reproduced multi-millisecond serve tails.
    }

    LodAssistBlobReader? EnsureBlobReader(LodServerCaptureSystem? capture)
    {
        string? path = capture?.CachePath;
        if (string.IsNullOrEmpty(path))
        {
            blobReader?.Dispose();
            blobReader = null;
            blobReaderPath = null;
            return null;
        }
        if (blobReader != null && string.Equals(blobReaderPath, path, StringComparison.Ordinal))
            return blobReader;

        blobReader?.Dispose();
        blobReaderPath = path;
        blobReader = new LodAssistBlobReader(path, Mod.Logger, allocationTelemetryEnabled);
        return blobReader;
    }

    void PublishBlobResults(LodServerCaptureSystem? capture, LodWorkBudget tickBudget)
    {
        if (blobReader == null) return;

        int processed = 0;
        while (blobReader.TryPeekResult(out LodAssistBlobReadResult next)
            && (processed == 0 || !tickBudget.Expired))
        {
            bool currentSession = sessionByPlayer.TryGetValue(next.PlayerUid, out long session)
                && session == next.Session;
            IServerPlayer? player = sapi.World.PlayerByUid(next.PlayerUid) as IServerPlayer;

            // A valid connection can briefly be absent from the playing set during
            // transitions. Retain its oldest result until it can be sent. A disconnected
            // or superseded session is terminal and may be discarded immediately.
            if (currentSession
                && (player == null || player.ConnectionState != EnumClientState.Playing)) break;
            if (!blobReader.TryTakeResult(out LodAssistBlobReadResult result)) break;
            processed++;

            if (readInFlightByPlayer.TryGetValue(
                result.PlayerUid, out InFlightReadBatch inFlight)
                && inFlight.Session == result.Session)
            {
                if (inFlight.Count <= 1) readInFlightByPlayer.Remove(result.PlayerUid);
                else readInFlightByPlayer[result.PlayerUid] = inFlight with
                {
                    Count = inFlight.Count - 1,
                };
            }

            BlobReadCost.AddSample(result.ElapsedTicks, result.AllocatedBytes);
            blobReadMs += result.ElapsedTicks * 1000.0
                / System.Diagnostics.Stopwatch.Frequency;

            if (!currentSession || player == null) continue;

            byte[] blob = result.Blob;
            // Empty blob rather than silence for a miss: the client needs to know to
            // stop asking. A read error, or a row advertised before its save completed,
            // is retryable rather than a permanent loss.
            SendSection(player, new AssistSection
            {
                Key = result.Key,
                Blob = blob,
                Retryable = result.ReadFailed
                    || (blob.Length == 0 && capture?.ExpectsToHave(result.Key) == true),
            });
            sectionsServed++;
            bytesServed += blob.Length;
        }
    }

    /// <summary>
    /// Nearest-edge distance from the player to the section, not centre-to-centre: an L6
    /// section spans 4096 blocks, so centre distance would refuse sections the player is
    /// standing inside.
    /// </summary>
    /// <summary>
    /// Radius check for a player. Separate from the math below so the deref of a player
    /// who has no entity yet (mid-join) keeps its own answer: with an unlimited radius
    /// there is nothing to compare against, so the absent position does not matter.
    /// </summary>
    static bool WithinServeRadius(long key, IServerPlayer player, int radiusBlocks)
    {
        if (radiusBlocks <= 0) return true;

        var pos = player.Entity?.Pos;
        if (pos == null) return false;

        return WithinServeRadius(key, pos.X, pos.Z, radiusBlocks);
    }

    public static bool WithinServeRadius(long key, double x, double z, int radiusBlocks)
    {
        if (radiusBlocks <= 0) return true;

        int footprint = LodWorld.KeyFootprintBlocks(key);
        double minX = LodWorld.KeySx(key) * (double)footprint;
        double minZ = LodWorld.KeySz(key) * (double)footprint;

        double dx = Math.Max(0, Math.Max(minX - x, x - (minX + footprint)));
        double dz = Math.Max(0, Math.Max(minZ - z, z - (minZ + footprint)));
        return dx * dx + dz * dz <= (double)radiusBlocks * radiusBlocks;
    }

    long sectionsOutsideRadius;
    uint serveRound;
    long sectionsServed, bytesServed;
    double blobReadMs;

    void SendSection(IServerPlayer player, AssistSection packet)
    {
        long started = Stopwatch.GetTimestamp();
        long allocationStart = allocationTelemetryEnabled
            ? GC.GetAllocatedBytesForCurrentThread()
            : -1;
        int gen0Start = allocationTelemetryEnabled ? GC.CollectionCount(0) : 0;
        int gen1Start = allocationTelemetryEnabled ? GC.CollectionCount(1) : 0;
        int gen2Start = allocationTelemetryEnabled ? GC.CollectionCount(2) : 0;
        channel.SendPacket(packet, player);
        long elapsed = Stopwatch.GetTimestamp() - started;
        long allocated = allocationStart < 0
            ? -1
            : GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        SectionSendCost.AddSample(elapsed, allocated);

        int gen0 = allocationTelemetryEnabled ? GC.CollectionCount(0) - gen0Start : 0;
        int gen1 = allocationTelemetryEnabled ? GC.CollectionCount(1) - gen1Start : 0;
        int gen2 = allocationTelemetryEnabled ? GC.CollectionCount(2) - gen2Start : 0;
        if (allocationTelemetryEnabled)
        {
            if (gen0 + gen1 + gen2 > 0)
            {
                SendWithCollectionCost.AddSample(elapsed, allocated);
                sendCallsWithCollection++;
            }
            else
            {
                SendWithoutCollectionCost.AddSample(elapsed, allocated);
                sendCallsWithoutCollection++;
            }
            sendGen0Collections += gen0;
            sendGen1Collections += gen1;
            sendGen2Collections += gen2;
        }

        if (!serveMeasurementActive) return;
        currentServeSendTicks += elapsed;
        currentServeMaxSendTicks = Math.Max(currentServeMaxSendTicks, elapsed);
        currentServeSendBytes += packet.Blob?.LongLength ?? 0;
        currentServeSendCount++;
        currentServeGen0 += gen0;
        currentServeGen1 += gen1;
        currentServeGen2 += gen2;
    }

    static double TicksToMilliseconds(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;

    void MeasureOfferNewKeys()
    {
        LodPhaseStart phaseStart = LodPhaseCost.Start(allocationTelemetryEnabled);
        OfferNewKeys();
        OfferUpdatedKeys();
        OfferCost.Add(phaseStart);
    }

    void LogStats()
    {
        int pending = pendingByPlayer.Values.Sum(queue => queue.Count);
        int readerOutstanding = blobReader?.Outstanding ?? 0;
        long now = sapi.World.ElapsedMilliseconds;
        long oldestAgeMs = pendingByPlayer.Values
            .Where(queue => queue.Count > 0)
            .Select(queue => Math.Max(0, now - queue.Peek().ReadyMs))
            .DefaultIfEmpty(0)
            .Max();
        oldestAgeMs = Math.Max(oldestAgeMs, blobReader?.OldestAgeMilliseconds ?? 0);
        int waitingPlayers = pendingByPlayer.Keys
            .Concat(readInFlightByPlayer.Keys)
            .Distinct(StringComparer.Ordinal)
            .Count();
        Mod.Logger.Notification(
            "Server assist stats p95/p99/max us: serve {0:0}/{1:0}/{2:0} | blob read "
            + "{3:0}/{4:0}/{5:0} | section send {6:0}/{7:0}/{8:0} | offer scan "
            + "{9:0}/{10:0}/{11:0}; {12} requests queued, {13} reads outstanding "
            + "for {14} players, oldest {15}ms, {16:0.00} MiB results; {17} served, "
            + "{18:0.0} MB",
            ServeCost.P95Us, ServeCost.P99Us, ServeCost.MaxUs,
            BlobReadCost.P95Us, BlobReadCost.P99Us, BlobReadCost.MaxUs,
            SectionSendCost.P95Us, SectionSendCost.P99Us, SectionSendCost.MaxUs,
            OfferCost.P95Us, OfferCost.P99Us, OfferCost.MaxUs,
            pending, readerOutstanding, waitingPlayers, oldestAgeMs,
            (blobReader?.PendingResultBytes ?? 0) / (1024.0 * 1024.0),
            sectionsServed, bytesServed / 1e6);

        Mod.Logger.Notification(
            "  assist allocation interval MiB/max KiB: serve {0:0.00}/{1:0.0} | blob "
            + "{2:0.00}/{3:0.0} | send {4:0.00}/{5:0.0} | offer {6:0.00}/{7:0.0}",
            ServeCost.AllocatedBytes / (1024.0 * 1024.0), ServeCost.MaxAllocatedBytes / 1024.0,
            BlobReadCost.AllocatedBytes / (1024.0 * 1024.0), BlobReadCost.MaxAllocatedBytes / 1024.0,
            SectionSendCost.AllocatedBytes / (1024.0 * 1024.0), SectionSendCost.MaxAllocatedBytes / 1024.0,
            OfferCost.AllocatedBytes / (1024.0 * 1024.0), OfferCost.MaxAllocatedBytes / 1024.0);

        Mod.Logger.Notification(
            "  assist serve phase p95/p99/max us: setup {0:0}/{1:0}/{2:0} | publish "
            + "{3:0}/{4:0}/{5:0} | admit {6:0}/{7:0}/{8:0}",
            ServeSetupCost.P95Us, ServeSetupCost.P99Us, ServeSetupCost.MaxUs,
            ServePublishCost.P95Us, ServePublishCost.P99Us, ServePublishCost.MaxUs,
            ServeAdmissionCost.P95Us, ServeAdmissionCost.P99Us, ServeAdmissionCost.MaxUs);

        Mod.Logger.Notification(
            "  assist GC attribution: serve calls with/without {0}/{1}, collections "
            + "{2}/{3}/{4}, max with/without {5:0}/{6:0}us; send calls {7}/{8}, "
            + "collections {9}/{10}/{11}, max with/without {12:0}/{13:0}us",
            serveCallsWithCollection, serveCallsWithoutCollection,
            serveGen0Collections, serveGen1Collections, serveGen2Collections,
            ServeWithCollectionCost.MaxUs, ServeWithoutCollectionCost.MaxUs,
            sendCallsWithCollection, sendCallsWithoutCollection,
            sendGen0Collections, sendGen1Collections, sendGen2Collections,
            SendWithCollectionCost.MaxUs, SendWithoutCollectionCost.MaxUs);

        ServeCost.Reset();
        ServeSetupCost.Reset();
        ServePublishCost.Reset();
        ServeAdmissionCost.Reset();
        ServeWithCollectionCost.Reset();
        ServeWithoutCollectionCost.Reset();
        BlobReadCost.Reset();
        SectionSendCost.Reset();
        SendWithCollectionCost.Reset();
        SendWithoutCollectionCost.Reset();
        OfferCost.Reset();
        serveCallsWithCollection = 0;
        serveCallsWithoutCollection = 0;
        serveGen0Collections = 0;
        serveGen1Collections = 0;
        serveGen2Collections = 0;
        sendCallsWithCollection = 0;
        sendCallsWithoutCollection = 0;
        sendGen0Collections = 0;
        sendGen1Collections = 0;
        sendGen2Collections = 0;
    }

    /// <summary>Keys the server holds, in chunks. Main thread only.</summary>
    void SendManifest(IServerPlayer player, long[] keys, bool refresh = false)
    {
        int sent = 0, sequence = 0;
        while (sent < keys.Length)
        {
            int take = Math.Min(LodAssist.ManifestKeysPerMessage, keys.Length - sent);
            var chunk = new long[take];
            Array.Copy(keys, sent, chunk, 0, take);
            sent += take;

            channel.SendPacket(new AssistKeyManifest
            {
                Sequence = sequence++,
                Last = sent >= keys.Length,
                Keys = chunk,
                Refresh = refresh,
            }, player);
        }

        Mod.Logger.Debug("VintageHorizons: sent {0} keys to {1} in {2} chunks",
            keys.Length, player.PlayerName, sequence);
    }

    public override void Dispose()
    {
        if (sapi != null) sapi.Event.PlayerDisconnect -= OnPlayerDisconnect;
        if (sapi != null && silhouettes != null)
        {
            sapi.Event.ChunkColumnLoaded -= OnColumnLoadedForSilhouettes;
            sapi.Event.GameWorldSave -= OnSilhouettesWorldSave;
        }
        // Saves the tiles changed since the last world save, then closes the tile store.
        silhouettes?.Dispose();
        blobReader?.Dispose();
        blobReader = null;
        blobReaderPath = null;
    }

    /// <summary>
    /// Horizon silhouettes, when the world starts running and only if the server config allows
    /// them (plan 4.9). The builder then also keeps the worldgen heights of every column the
    /// server loads. That costs a copy of one height map, because the column is in memory
    /// anyway (plan 4.4). Tiles persist in their own file, written at every world save.
    /// </summary>
    void StartSilhouettes()
    {
        RegisterSilhouetteCommand();
        LodServerConfig config = sapi.ModLoader.GetModSystem<LodServerCaptureSystem>()?.Config
            ?? new LodServerConfig();
        bool measuring = Environment.GetEnvironmentVariable(LodServerConfig.MeasureSilhouettesVariable) == "1";
        silhouettesOffered = config.SilhouettesEnabled(sapi.Server.IsDedicated);
        if (!config.SilhouetteHeightsKept(sapi.Server.IsDedicated, measuring))
        {
            if (!sapi.Server.IsDedicated)
            {
                Mod.Logger.Notification("Horizon silhouettes are switched off in "
                    + "ModConfig/vintagehorizons-server.json: this world offers none and keeps no heights for them.");
            }
            return;
        }
        if (!silhouettesOffered)
        {
            Mod.Logger.Notification("Horizon silhouettes: measuring heights only ({0}). Players are offered "
                + "none; /vhsilhouetteserver compare can run.", LodServerConfig.MeasureSilhouettesVariable);
        }
        // A world that has never used silhouettes gets no file until a player asks for them: a
        // database opened in every singleplayer world "in case" is the cost the section cache's
        // startup guard exists to avoid.
        string? storePath = SilhouetteStorePath();
        LodSilhouetteStore? store = storePath != null && File.Exists(storePath)
            ? OpenSilhouetteStore(storePath)
            : null;
        ILodHeightAccelerator? accelerator = null;
        if (config.SilhouetteUseAccelerators)
        {
            accelerator = LodHeightAccelerators.Preferred(LodHeightAccelerators.Find(sapi));
            Mod.Logger.Notification(accelerator == null
                ? "Horizon silhouettes: no supported height accelerator is installed; using engine previews."
                : "Horizon silhouettes: selected {0}; Watersheds is preferred when both supported samplers are installed.",
                accelerator?.Name ?? "");
        }
        else
        {
            Mod.Logger.Notification("Horizon silhouettes: optional height accelerators are disabled in the server config; using engine previews.");
        }
        silhouettes = new LodSilhouetteBuilder(sapi, Mod.Logger,
            () => sapi.ModLoader.GetModSystem<LodServerCaptureSystem>()?.GenerationAuthority
                ?? EnumWorldGenAuthority.None,
            () => sapi.ModLoader.GetModSystem<LodServerCaptureSystem>()?.AutomaticGenerationGate
                ?? EnumWorldGenGate.None,
            SendSilhouetteManifest, SendSilhouetteTile, store,
            storePath == null ? null : () => OpenSilhouetteStore(storePath),
            // ModLogger's prefix: the mod's own log lines never count as worldgen errors.
            "[" + Mod.Info.ModID + "]", accelerator, sapi.World.SeaLevel,
            sapi.Server.IsDedicated ? config.SilhouetteReachBlocks : LodSilhouetteBuilder.MaxReachBlocks,
            config.SilhouettePreviewsPerSecond);
        sapi.Event.ChunkColumnLoaded += OnColumnLoadedForSilhouettes;
        sapi.Event.GameWorldSave += OnSilhouettesWorldSave;
    }

    /// <summary>
    /// Where the world's tile store lives: beside the section cache, named after the savegame the
    /// same way (LodPipeline.Open). Null if even the path cannot be made; silhouettes then work
    /// for the session only.
    /// </summary>
    string? SilhouetteStorePath()
    {
        try
        {
            string directory = sapi.GetOrCreateDataPath("ModData/vintagehorizons");
            return Path.Combine(directory, WorldFileKey() + "-silhouette-server.db");
        }
        catch (Exception e)
        {
            Mod.Logger.Warning("Horizon silhouettes: no tile store this session, so nothing is kept: {0}",
                e.Message);
            return null;
        }
    }

    LodSilhouetteStore? OpenSilhouetteStore(string path)
    {
        try
        {
            return new LodSilhouetteStore(path, Mod.Logger);
        }
        catch (Exception e)
        {
            Mod.Logger.Warning("Horizon silhouettes: no tile store this session, so nothing is kept: {0}",
                e.Message);
            return null;
        }
    }

    void OnSilhouettesWorldSave() => silhouettes?.SaveDirty();

    void RegisterSilhouetteCommand()
    {
        TextCommandResult Status()
        {
            string line = "[VintageHorizons] Horizon silhouettes: " + (silhouettes == null
                ? "off on this server (EnableSilhouettes in ModConfig/vintagehorizons-server.json)"
                : (silhouettesOffered ? "" : "measuring heights only, players are offered none; ")
                    + silhouettes.Describe()) + ".";
            // Chat cannot be copied, so the same line always reaches the log.
            Mod.Logger.Notification("{0}", line);
            return TextCommandResult.Success(line);
        }

        // Not "vhsilhouette": that is the client's switch, and every command name stays unique
        // across both sides (StaticAssetChecks.ChatCommandNamesAreUnique).
        sapi.ChatCommands.Create("vhsilhouetteserver")
            .WithDescription("Horizon silhouettes on this server: tiles, heights, previews and the tile store")
            .RequiresPrivilege(Privilege.controlserver)
            .HandleWith(_ => Status())
            .BeginSubCommand("status")
                .WithDescription("The same report")
                .HandleWith(_ => Status())
            .EndSubCommand()
            .BeginSubCommand("presample")
                .WithDescription("Ask each installed height accelerator for every cell of the chunk square "
                    + "around you (the world centre from the console) and keep the answers on disk, so a later "
                    + "session can compare them with the terrain once it is generated (silhouette plan P0.3)")
                .WithArgs(sapi.ChatCommands.Parsers.OptionalIntRange("radiusChunks", 1, MaxCompareRadiusChunks, 32))
                .HandleWith(StartAcceleratorPresample)
            .EndSubCommand()
            .BeginSubCommand("compare")
                .WithDescription("Measure each installed height accelerator against the real heights the "
                    + "server holds in the chunk square around you (the world centre from the console), and the "
                    + "presample if there is one; writes a map for the side-by-side (silhouette plan P0.3)")
                .WithArgs(sapi.ChatCommands.Parsers.OptionalIntRange("radiusChunks", 1, MaxCompareRadiusChunks, 32))
                .HandleWith(StartAcceleratorComparison)
            .EndSubCommand();
    }

    // One accelerator comparison or presample at a time; its lines are read on the main thread.
    ILodBackgroundReport? acceleratorRun;
    long acceleratorListener;

    /// <summary>Radius cap for compare and presample: 97 chunks a side, about 600,000 cells.</summary>
    const int MaxCompareRadiusChunks = 48;

    /// <summary>
    /// The chunk both commands centre their square on: the caller's, or from the console the world's
    /// centre, computed as the game's /wgen pregen does (it uses the map's X size for both axes), so a
    /// script can presample, pregenerate and compare the same square.
    /// </summary>
    (int Cx, int Cz) CompareCentre(TextCommandCallingArgs args)
    {
        int chunk = Vintagestory.API.Config.GlobalConstants.ChunkSize;
        if (args.Caller.Player is IServerPlayer player && player.Entity != null)
            return ((int)player.Entity.Pos.X / chunk, (int)player.Entity.Pos.Z / chunk);
        int centre = sapi.WorldManager.MapSizeX / Vintagestory.API.Config.GlobalConstants.ChunkSize / 2;
        return (centre, centre);
    }

    /// <summary>A comparison file for this world, beside the tile store and named after the savegame.</summary>
    string CompareFilePath(string suffix) =>
        Path.Combine(sapi.GetOrCreateDataPath("ModData/vintagehorizons/silhouette-compare"), WorldFileKey() + suffix);

    /// <summary>The savegame's name made safe for a file name, as the section cache names its files.</summary>
    string WorldFileKey()
    {
        string worldKey = sapi.World.SavegameIdentifier;
        if (string.IsNullOrEmpty(worldKey)) worldKey = "seed-" + sapi.World.Seed;
        return System.Text.RegularExpressions.Regex.Replace(worldKey, "[^A-Za-z0-9_-]", "_");
    }

    List<ILodHeightAccelerator>? AcceleratorsForRun(out TextCommandResult? refusal)
    {
        refusal = null;
        if (silhouettes == null)
        {
            refusal = TextCommandResult.Error("[VintageHorizons] Horizon silhouettes are off on this server, "
                + "so there are no real heights to compare against.");
        }
        else if (acceleratorRun != null)
        {
            refusal = TextCommandResult.Error("[VintageHorizons] A comparison or presample is already running; "
                + "its report will follow.");
        }
        else if (LodHeightAccelerators.Find(sapi) is { Count: > 0 } found)
        {
            return found;
        }
        else
        {
            refusal = TextCommandResult.Error("[VintageHorizons] No height accelerator is installed. This build "
                + "knows Algernon's Terrain Sampler and Watersheds' own sampler.");
        }
        return null;
    }

    TextCommandResult StartAcceleratorPresample(TextCommandCallingArgs args)
    {
        if (AcceleratorsForRun(out TextCommandResult? refusal) is not { } accelerators) return refusal!;
        (int cx, int cz) = CompareCentre(args);
        int radius = (int)args[0];
        List<(int X, int Z)> cells = LodAcceleratorPresampleRun.SquareCells(cx, cz, radius);
        var run = new LodAcceleratorPresampleRun(CompareFilePath("-presample.csv.gz"), cells,
            silhouettes!.HeldChunks(cx, cz, radius), accelerators, sapi.World.SeaLevel);
        return StartAcceleratorRun("presample", run, args, string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "[VintageHorizons] Accelerator presample of {0} cells around chunk {1},{2} (radius {3} chunks), asking "
            + "{4}. It runs in the background; each report follows in chat and the server log.",
            cells.Count, cx, cz, radius, string.Join(" and ", accelerators.Select(a => a.Name))));
    }

    TextCommandResult StartAcceleratorComparison(TextCommandCallingArgs args)
    {
        if (AcceleratorsForRun(out TextCommandResult? refusal) is not { } accelerators) return refusal!;
        (int cx, int cz) = CompareCentre(args);
        int radius = (int)args[0];
        List<LodRealHeight> cells = silhouettes!.RealHeightCells(cx, cz, radius);
        if (cells.Count == 0)
        {
            return TextCommandResult.Error("[VintageHorizons] The server holds no real heights in that square yet. "
                + "Load or explore some terrain, or switch silhouettes on, then try again.");
        }
        string map = CompareFilePath("-map-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss",
            System.Globalization.CultureInfo.InvariantCulture) + ".csv.gz");
        var run = new LodAcceleratorComparisonRun(cells, accelerators, sapi.World.SeaLevel,
            CompareFilePath("-presample.csv.gz"), map);
        return StartAcceleratorRun("comparison", run, args, string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "[VintageHorizons] Accelerator comparison at {0} cells where the server holds real heights ({1} captured, "
            + "{2} previewed) around chunk {3},{4} (radius {5} chunks), against {6}. It runs in the background; each "
            + "report follows in chat and the server log.", cells.Count,
            cells.Count(p => p.Origin == EnumSilhouetteOrigin.Captured),
            cells.Count(p => p.Origin == EnumSilhouetteOrigin.Preview), cx, cz, radius,
            string.Join(" and ", accelerators.Select(a => a.Name))));
    }

    TextCommandResult StartAcceleratorRun(string what, ILodBackgroundReport run, TextCommandCallingArgs args,
        string header)
    {
        acceleratorRun = run;
        IServerPlayer? caller = args.Caller.Player as IServerPlayer;
        Mod.Logger.Notification("{0}", header);
        acceleratorListener = sapi.Event.RegisterGameTickListener(_ => PumpAcceleratorRun(caller, what), 500);
        return TextCommandResult.Success(header);
    }

    void PumpAcceleratorRun(IServerPlayer? caller, string what)
    {
        ILodBackgroundReport? run = acceleratorRun;
        if (run == null) return;
        bool done = run.Finished;
        while (run.TryTakeLine(out string line))
        {
            string text = "[VintageHorizons] Accelerator " + what + ", " + line + ".";
            // Chat cannot be copied, so every line also reaches the log.
            Mod.Logger.Notification("{0}", text);
            caller?.SendMessage(Vintagestory.API.Config.GlobalConstants.GeneralChatGroup, text,
                EnumChatType.Notification);
        }
        if (!done) return;
        sapi.Event.UnregisterGameTickListener(acceleratorListener);
        acceleratorRun = null;
        Mod.Logger.Notification("[VintageHorizons] Accelerator " + what + " finished.");
    }

    /// <summary>Main thread: the engine raises this from a main-thread task.</summary>
    void OnColumnLoadedForSilhouettes(Vintagestory.API.MathTools.Vec2i chunkCoord, IWorldChunk[] chunks)
    {
        IMapChunk? map = chunks.Length > 0 ? chunks[0]?.MapChunk : null;
        ushort[]? heights = map?.WorldGenTerrainHeightMap;
        if (map == null || heights == null || heights.Length < LodSilhouetteTile.HeightMapLength) return;
        // An old-save column without worldgen heights carries a copy of its rain map (plan 2.1),
        // which reads water surfaces and treetops rather than the ground.
        bool rain = map.RainHeightMap is { } rainMap && heights.AsSpan().SequenceEqual(rainMap);
        silhouettes?.OfferCapturedHeights(chunkCoord.X, chunkCoord.Y, heights, rain); // Vec2i.Y is the Z chunk coord
    }

    /// <summary>Any thread: a previewed column's heights, handed over by the capture pipeline.</summary>
    public void OfferSilhouetteHeights(int cx, int cz, ushort[] heights)
    {
        if (silhouettes == null) return;
        sapi.Event.EnqueueMainThreadTask(() => silhouettes?.OfferCapturedHeights(cx, cz, heights, false),
            "vintagehorizons-silhouette-heights");
    }
}
