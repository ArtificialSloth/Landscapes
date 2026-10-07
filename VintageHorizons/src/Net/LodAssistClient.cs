using System.Collections.Concurrent;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageHorizons.Net;

/// <summary>
/// Adopts or accepts responsibility for a section that arrived from the server.
/// Returns false if it was not taken.
/// </summary>
public delegate bool LodForeignSectionInstaller(long key, byte[] blob);

/// <summary>
/// The same, with the server's version of the row the blob was read from (0 from a server that
/// does not version its cache), which a server-sync merge needs to settle what it owed.
/// </summary>
public delegate bool LodVersionedSectionInstaller(long key, byte[] blob, long version);

/// <summary>One chunk of a versioned refresh span, published on the owning thread.</summary>
public readonly record struct LodAssistSpanChunk(
    long From, long To, long[] Keys, long[] Versions, bool Last);

/// <summary>
/// Client half of the optional server assist (DESIGN.md §10). Stage 1 is handshake only:
/// it establishes whether an assisting server is on the other end and reports that
/// through .dlinfo. No terrain moves yet.
///
/// The failure path is the one that matters. Most servers will never have this mod, and
/// on those the whole class must amount to a channel that is registered and never used.
/// So nothing is ever sent before <see cref="EnumChannelState.Connected"/>, and a server
/// that answers with something unexpected leaves the assist off rather than half on.
/// </summary>
public sealed class LodAssistClient
{
    readonly ICoreClientAPI capi;
    readonly ILogger logger;
    readonly string modVersion;
    IClientNetworkChannel? channel;
    int serverProtocol;
    int serverFeatures;

    /// <summary>Negotiated protocol, 0 until a usable welcome arrives.</summary>
    public int NegotiatedProtocol { get; private set; }

    /// <summary>True once a server has confirmed it will serve terrain.</summary>
    public bool Available => NegotiatedProtocol > 0;

    /// <summary>One line for .dlinfo. Always set to something a player can act on.</summary>
    public string Status { get; private set; } = "not connected yet";

    /// <summary>
    /// Complete server-side settings from the welcome packet. Null means the server does
    /// not have VintageHorizons or predates the diagnostic snapshot field.
    /// </summary>
    public AssistServerSettings? ServerSettings { get; private set; }

    /// <summary>
    /// The server cache's generation from the welcome: set only when the server versions its
    /// sections and this client asked to sync, so a versioned join span follows. Empty otherwise.
    /// </summary>
    public string ServerCacheGeneration { get; private set; } = "";

    /// <summary>True only when this server explicitly advertised the append-only feature.</summary>
    public bool WorldGenerationAvailable => serverProtocol > 0
        && (serverFeatures & LodAssist.FeatureWorldGeneration) != 0;

    /// <summary>True only when this server advertised silhouette tiles (plan 4.6).</summary>
    public bool SilhouettesAvailable => LodAssist.SilhouettesAvailable(serverProtocol, serverFeatures);

    /// <summary>Called on the network thread for each silhouette manifest chunk.</summary>
    public Action<AssistSilhouetteManifest>? SilhouetteManifestArrived { get; set; }

    /// <summary>Called on the network thread for every silhouette tile reply.</summary>
    public Action<AssistSilhouetteTile>? SilhouetteTileArrived { get; set; }

    public bool CanControlWorldGeneration { get; private set; }
    public AssistWorldGenStatus? WorldGenStatus { get; private set; }
    public string LastWorldGenControlError { get; private set; } = "";
    public event Action<AssistWorldGenStatus>? WorldGenStatusChanged;

    readonly ConcurrentQueue<AssistWorldGenStatus> worldGenStatuses = new();
    bool worldGenPageOpen;
    long nextWorldGenPollMs;
    long nextWorldGenRequestId;
    long lastWorldGenStatusRequestId;
    long lastWorldGenControlResponseId;
    long worldGenResetFloor;

    public LodAssistClient(ICoreClientAPI capi, ILogger logger, string modVersion)
    {
        this.capi = capi;
        this.logger = logger;
        this.modVersion = modVersion;
    }

    /// <summary>
    /// Call once during StartClientSide -- channel registration has to happen before the
    /// connection handshake, which is long before a world exists.
    /// </summary>
    public void Register()
    {
        channel = capi.Network.RegisterChannel(LodAssist.ChannelName)
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
            .SetMessageHandler<AssistWelcome>(OnWelcome)
            .SetMessageHandler<AssistKeyManifest>(OnKeyManifest)
            .SetMessageHandler<AssistSection>(OnSection)
            .SetMessageHandler<AssistWorldGenStatus>(OnWorldGenStatus)
            .SetMessageHandler<AssistSilhouetteManifest>(OnSilhouetteManifest)
            .SetMessageHandler<AssistSilhouetteTile>(OnSilhouetteTile);
    }

    /// <summary>
    /// Call once the world is up, when the channel state has settled. Never throws: the
    /// caller is a LevelFinalize handler, and an exception there aborts every remaining
    /// step of it -- which on a vanilla server would mean the optional feature breaking
    /// the mod for exactly the players it is supposed to leave alone.
    /// </summary>
    /// <param name="sync">
    /// What this client caught up to with the server's section cache, or null when it does not
    /// sync by version (integrated play reads the server's cache directly).
    /// </param>
    public void Greet(LodAssistSyncRequest? sync = null)
    {
        if (channel == null) return;

        // IClientNetworkChannel.Connected, not INetworkAPI.GetChannelState: against a
        // vanilla server GetChannelState reports Connected while the channel is not,
        // and SendPacket then throws "Attempting to send data to a not connected
        // channel". Connected is what the engine's own error message says to test.
        if (!channel.Connected)
        {
            // Much the commonest outcome, and not a problem: it is a plain server.
            Status = "none (server does not have Doden's Landscapes)";
            logger.Debug("Doden's Landscapes: no server assist (channel state {0})",
                capi.Network.GetChannelState(LodAssist.ChannelName));
            return;
        }

        try
        {
            Status = "handshaking";
            channel.SendPacket(new AssistHello
            {
                Protocol = LodAssist.Protocol,
                ModVersion = modVersion,
                SectionSync = sync != null ? LodAssist.SectionSyncVersion : 0,
                SyncGeneration = sync?.Generation ?? "",
                SyncVersion = sync?.Version ?? LodAssistSync.NoRecord,
            });
        }
        catch (Exception e)
        {
            Status = "unavailable (handshake failed)";
            logger.Warning("Doden's Landscapes: server assist handshake failed, continuing without it: {0}", e);
        }
    }

    internal void OnWelcome(AssistWelcome msg)
    {
        // Deserialized, so every reference field is whatever the wire produced.
        ServerSettings = msg.ServerSettings;
        serverFeatures = msg.Features;
        ServerCacheGeneration = msg.CacheGeneration ?? "";
        CanControlWorldGeneration = msg.CanControlWorldGeneration;
        string reason = msg.Status ?? "";
        string version = msg.ModVersion ?? "unknown";
        ManifestExpected = msg.ManifestKeyCount;

        // Feature negotiation is valid even when terrain serving is disabled: an admin may
        // deliberately permit explicit savegame generation while sharing no cached map.
        serverProtocol = Math.Min(LodAssist.Protocol, msg.Protocol);
        if (serverProtocol < 1) serverProtocol = 0;

        if (!msg.Enabled)
        {
            NegotiatedProtocol = 0;
            Status = reason.Length > 0
                ? $"server has Doden's Landscapes but the assist is off ({reason})"
                : "server has Doden's Landscapes but the assist is off";
            logger.Notification("Doden's Landscapes: {0}", Status);
            return;
        }

        // Take the lower of the two so neither side has to know what the other added.
        int negotiated = serverProtocol;
        if (negotiated < 1)
        {
            NegotiatedProtocol = 0;
            Status = $"server assist unusable (its protocol {msg.Protocol}, ours {LodAssist.Protocol})";
            logger.Warning("Doden's Landscapes: {0}", Status);
            return;
        }

        NegotiatedProtocol = negotiated;
        Status = $"connected to server {version} (protocol {negotiated})";
        logger.Notification("Doden's Landscapes: server assist {0}", Status);
    }

    internal void OnWorldGenStatus(AssistWorldGenStatus status) =>
        worldGenStatuses.Enqueue(status);

    internal void OnSilhouetteManifest(AssistSilhouetteManifest manifest) =>
        SilhouetteManifestArrived?.Invoke(manifest);

    internal void OnSilhouetteTile(AssistSilhouetteTile tile) => SilhouetteTileArrived?.Invoke(tile);

    /// <summary>
    /// Main thread. Nothing is sent unless the server advertised the feature, so a server without
    /// it is never sent a message id it did not register.
    /// </summary>
    public bool SendSilhouetteRequest(bool enabled, int reachBlocks, long sessionId)
    {
        if (channel == null || !channel.Connected || !SilhouettesAvailable) return false;
        try
        {
            channel.SendPacket(new AssistSilhouetteRequest
            {
                Enabled = enabled,
                ReachBlocks = reachBlocks,
                ModVersion = modVersion,
                TransportVersion = LodAssist.SilhouetteTransportVersion,
                SessionId = sessionId,
            });
            return true;
        }
        catch (Exception e)
        {
            logger.Warning("Doden's Landscapes: the silhouette request failed, continuing without it: {0}",
                e.Message);
            return false;
        }
    }

    /// <summary>Send one bounded batch selected by the owning silhouette client.</summary>
    public bool SendSilhouetteTileRequest(long[] keys, long sessionId)
    {
        if (keys.Length == 0 || keys.Length > LodAssist.MaxSilhouetteTilesInFlight
            || channel == null || !channel.Connected || !SilhouettesAvailable) return false;
        try
        {
            channel.SendPacket(new AssistSilhouetteTileRequest { Keys = keys, SessionId = sessionId });
            return true;
        }
        catch (Exception e)
        {
            logger.Warning("Doden's Landscapes: the silhouette tile request failed: {0}", e.Message);
            return false;
        }
    }

    /// <summary>
    /// Publish packet-thread status on the owning client thread, then poll at one hertz only
    /// while the page is visible or a known job is still live.
    /// </summary>
    public void PumpWorldGen()
    {
        AssistWorldGenStatus? latest = null;
        long newestRequestId = lastWorldGenStatusRequestId;
        bool controlMessageChanged = false;
        while (worldGenStatuses.TryDequeue(out AssistWorldGenStatus? next))
        {
            if (next.RequestId <= worldGenResetFloor) continue;
            if (next.Action != EnumWorldGenControlAction.Status
                && next.RequestId > lastWorldGenControlResponseId)
            {
                lastWorldGenControlResponseId = next.RequestId;
                LastWorldGenControlError = next.Success ? "" : next.Message;
                controlMessageChanged = true;
            }
            if (next.RequestId <= lastWorldGenStatusRequestId
                || next.RequestId < newestRequestId) continue;
            newestRequestId = next.RequestId;
            latest = next;
        }
        if (latest != null)
        {
            lastWorldGenStatusRequestId = newestRequestId;
            WorldGenStatus = latest;
            CanControlWorldGeneration = latest.CanControl;
            WorldGenStatusChanged?.Invoke(latest);
        }
        else if (controlMessageChanged && WorldGenStatus != null)
        {
            // A newer passive poll may already own the structured state. A late control
            // refusal still has to repaint its durable error instead of disappearing.
            WorldGenStatusChanged?.Invoke(WorldGenStatus);
        }

        if (!ShouldPollWorldGen(worldGenPageOpen, WorldGenStatus?.State)) return;

        long now = NowMs();
        if (now < nextWorldGenPollMs) return;
        nextWorldGenPollMs = now + 1000;
        RequestWorldGen(EnumWorldGenControlAction.Status);
    }

    internal static bool ShouldPollWorldGen(bool pageOpen, EnumWorldGenRemoteState? state) =>
        pageOpen || state is EnumWorldGenRemoteState.Running
            or EnumWorldGenRemoteState.Paused or EnumWorldGenRemoteState.Faulted;

    public void SetWorldGenPageOpen(bool open)
    {
        worldGenPageOpen = open;
        if (open) nextWorldGenPollMs = 0;
    }

    public long RequestWorldGen(EnumWorldGenControlAction action,
        int radiusChunks = 0, EnumWorldGenShape shape = EnumWorldGenShape.Circle,
        EnumWorldGenCentreMode centreMode = EnumWorldGenCentreMode.Here,
        int x = -1, int z = -1, string preset = "Balanced")
    {
        long requestId = Interlocked.Increment(ref nextWorldGenRequestId);
        if (action != EnumWorldGenControlAction.Status)
            LastWorldGenControlError = "";
        if (!WorldGenerationAvailable || channel == null || !channel.Connected)
        {
            OnWorldGenStatus(new AssistWorldGenStatus
            {
                RequestId = requestId,
                Success = false,
                Message = "[Landscapes] Generate World is unavailable on this server.",
                State = EnumWorldGenRemoteState.Unavailable,
                CanControl = CanControlWorldGeneration,
                Action = action,
            });
            return requestId;
        }

        try
        {
            channel.SendPacket(new AssistWorldGenRequest
            {
                RequestId = requestId,
                Action = action,
                RadiusChunks = radiusChunks,
                Shape = shape,
                CentreMode = centreMode,
                X = x,
                Z = z,
                Preset = preset,
            });
        }
        catch (Exception e)
        {
            logger.Warning("Doden's Landscapes: Generate World request failed: {0}", e);
            OnWorldGenStatus(new AssistWorldGenStatus
            {
                RequestId = requestId,
                Success = false,
                Message = "[Landscapes] The Generate World request could not be sent.",
                State = WorldGenStatus?.State ?? EnumWorldGenRemoteState.Unavailable,
                Available = WorldGenerationAvailable,
                CanControl = CanControlWorldGeneration,
                Action = action,
            });
        }
        return requestId;
    }

    /// <summary>
    /// Keys the server holds. Read and mutated only from the game tick, via <see cref="Pump"/>
    /// - the handlers below run on whatever thread the engine delivers packets on, and a
    /// plain HashSet shared across those two would be a race whether or not it shows up in
    /// testing. Everything a handler learns goes into a concurrent queue and is applied on
    /// the tick, which also puts installs in the one place allowed to touch LodWorld.
    /// </summary>
    public readonly HashSet<long> RemoteKeys = new();

    readonly record struct QueuedManifest(
        long[] Keys, bool Last, bool Refresh, long VersionFrom, long VersionTo, long[] Versions);

    readonly ConcurrentQueue<QueuedManifest> manifestChunks = new();

    /// <summary>True once the final manifest chunk has been applied.</summary>
    public bool ManifestComplete { get; private set; }

    /// <summary>What the server said to expect, for comparison against what arrived.</summary>
    public int ManifestExpected { get; private set; }

    internal void OnKeyManifest(AssistKeyManifest msg) =>
        manifestChunks.Enqueue(new QueuedManifest(msg.Keys ?? Array.Empty<long>(), msg.Last, msg.Refresh,
            msg.VersionFrom, msg.VersionTo, msg.Versions ?? Array.Empty<long>()));

    /// <summary>
    /// Whether the server listed this key in any manifest this session, including keys it later
    /// declined. Owning thread only.
    /// </summary>
    public bool ServerListed(long key) => RemoteKeys.Contains(key) || refused.Contains(key);

    /// <summary>
    /// Make a key requestable again for a server-sync merge: an earlier refusal or exhausted
    /// retries belonged to an older copy, and the server announced it as changed. The same
    /// revival a refresh manifest gives the keys it lists. Owning thread only.
    /// </summary>
    public void ReviveForRefresh(long key)
    {
        refused.Remove(key);
        retriesByKey.Remove(key);
        retryNotBeforeByKey.Remove(key);
        RemoteKeys.Add(key);
    }

    // ---- Section transfer ----

    /// <summary>
    /// Arrivals awaiting the tick. An empty blob is a refusal; Retryable separates
    /// "not written yet" from "never", which are the same packet otherwise.
    /// </summary>
    readonly ConcurrentQueue<(long Key, byte[] Blob, bool Retryable, long Version, long ReadyAtMs)> Arrived = new();
    long arrivedBytes;

    internal long ArrivalMaxBytesPerTick = LodDrainBudget.DefaultMaxBytes;
    internal double ArrivalMaxMillisecondsPerTick = LodDrainBudget.DefaultMaxMilliseconds;

    readonly HashSet<long> inFlight = new();

    /// <summary>Keys the server declined or no longer has; never asked for again.</summary>
    readonly HashSet<long> refused = new();

    /// <summary>
    /// How many times each key has come back as "not yet". Bounded, because a server that
    /// keeps saying not-yet forever would otherwise have this client asking forever, and
    /// each ask costs a request slot the rest of the view is waiting for.
    /// </summary>
    readonly Dictionary<long, int> retriesByKey = new();
    readonly Dictionary<long, long> retryNotBeforeByKey = new();

    /// <summary>Monotonic clock seam for retry scheduling checks.</summary>
    internal Func<long> NowMs = static () => Environment.TickCount64;

    /// <summary>Eight attempts at this cadence span about a minute.</summary>
    internal const int RetryDelayMs = 7500;

    /// <summary>
    /// Roughly a minute of asking at the rate the request loop runs, which comfortably
    /// outlasts the gap between a section being registered and its row being written.
    /// Past this the key is treated as a flat refusal, so the failure mode is the old
    /// behaviour rather than an unbounded one.
    /// </summary>
    const int MaxRetriesPerKey = 8;

    public int InFlight => inFlight.Count;
    /// <summary>
    /// Largest number of request slots held at once in this world. This cumulative high-water
    /// mark lets an integration benchmark prove that it exercised the protocol's normal
    /// saturation point even when the server drains all slots before the next stats interval.
    /// </summary>
    public int PeakInFlight { get; private set; }
    public int SectionsReceived { get; private set; }
    public int SectionsRefused => refused.Count;
    public int PendingArrivals => Arrived.Count;
    public long PendingArrivalBytes => Math.Max(0, Interlocked.Read(ref arrivedBytes));
    public long OldestArrivalAgeMs => Arrived.TryPeek(out var oldest)
        ? Math.Max(0, NowMs() - oldest.ReadyAtMs)
        : 0;

    /// <summary>Arrival work since the last telemetry reset.</summary>
    public int ArrivalItemsProcessed { get; private set; }
    public long ArrivalBytesProcessed { get; private set; }

    /// <summary>Keys currently being retried because the server has not written them yet.</summary>
    public int SectionsPendingOnServer => retriesByKey.Count;

    /// <summary>
    /// Ask for sections the server has and we do not, up to the in-flight cap. Called from
    /// the game tick with keys the quadtree actually wants, so the fetch order follows what
    /// the player can see rather than the manifest's arbitrary order.
    /// </summary>
    public long[] Request(IEnumerable<long> wanted)
    {
        if (!Available || channel == null || !channel.Connected) return Array.Empty<long>();

        long[] sent = SelectRequestBatch(wanted);
        if (sent.Length == 0) return Array.Empty<long>();

        try
        {
            channel.SendPacket(new AssistSectionRequest { Keys = sent });
            SectionsRequested += sent.Length;
            return sent;
        }
        catch (Exception e)
        {
            foreach (long key in sent) inFlight.Remove(key);
            logger.Warning("Doden's Landscapes: section request failed: {0}", e);
            return Array.Empty<long>();
        }
    }

    /// <summary>
    /// Choose the next batch and mark it in flight. Split out of <see cref="Request"/>
    /// so the cap and the slot bookkeeping can be tested: Request needs a live channel,
    /// which meant the one rule the whole transfer rests on had no reachable check.
    ///
    /// That rule: a slot is held until a reply arrives. If the server ever answers a
    /// request with silence, the cap fills and this returns nothing forever. The server
    /// therefore replies to everything, even to refuse - see LodAssistServerSystem.
    /// </summary>
    internal long[] SelectRequestBatch(IEnumerable<long> wanted)
    {
        List<long>? batch = null;
        long now = NowMs();
        foreach (long key in wanted)
        {
            if (inFlight.Count >= LodAssist.MaxSectionsInFlight) break;
            if (retryNotBeforeByKey.TryGetValue(key, out long notBefore) && now < notBefore) continue;
            if (!RemoteKeys.Contains(key) || refused.Contains(key) || !inFlight.Add(key)) continue;
            (batch ??= new List<long>()).Add(key);
        }
        PeakInFlight = Math.Max(PeakInFlight, inFlight.Count);
        return batch?.ToArray() ?? Array.Empty<long>();
    }

    public int SectionsRequested { get; private set; }

    internal void OnSection(AssistSection msg)
    {
        byte[] blob = msg.Blob ?? Array.Empty<byte>();
        Interlocked.Add(ref arrivedBytes, blob.LongLength);
        Arrived.Enqueue((msg.Key, blob, msg.Retryable, msg.Version, NowMs()));
    }

    /// <summary>
    /// Apply everything the handlers have queued, on the game tick. <paramref name="install"/>
    /// is given each arrived section and returns whether it was adopted; a section the
    /// client already has locally is declined there, since local capture wins (§10.5).
    /// </summary>
    public void Pump(
        LodForeignSectionInstaller install,
        Action<long[]>? offerKeys = null,
        Action<long, bool>? transferFailed = null,
        Action<long[]>? refreshKeys = null,
        Action<LodAssistSpanChunk>? refreshSpan = null) =>
        PumpCore((key, blob, _) => install(key, blob), deferredInstall: false,
            offerKeys, transferFailed, refreshKeys, refreshSpan);

    /// <summary>
    /// Pump arrivals into an asynchronous decoder. A successful callback retains the
    /// transport in-flight slot until <see cref="CompleteInstall"/> publishes the result.
    /// </summary>
    public void PumpAsync(
        LodForeignSectionInstaller enqueue,
        Action<long[]>? offerKeys = null,
        Action<long, bool>? transferFailed = null,
        Action<long[]>? refreshKeys = null,
        Action<LodAssistSpanChunk>? refreshSpan = null) =>
        PumpCore((key, blob, _) => enqueue(key, blob), deferredInstall: true,
            offerKeys, transferFailed, refreshKeys, refreshSpan);

    /// <summary>
    /// <see cref="PumpAsync(LodForeignSectionInstaller, Action{long[]}?, Action{long, bool}?, Action{long[]}?, Action{LodAssistSpanChunk}?)"/>
    /// for a caller that needs each arrival's server version.
    /// </summary>
    public void PumpAsync(
        LodVersionedSectionInstaller enqueue,
        Action<long[]>? offerKeys = null,
        Action<long, bool>? transferFailed = null,
        Action<long[]>? refreshKeys = null,
        Action<LodAssistSpanChunk>? refreshSpan = null) =>
        PumpCore(enqueue, deferredInstall: true, offerKeys, transferFailed, refreshKeys, refreshSpan);

    void PumpCore(
        LodVersionedSectionInstaller install,
        bool deferredInstall,
        Action<long[]>? offerKeys,
        Action<long, bool>? transferFailed,
        Action<long[]>? refreshKeys,
        Action<LodAssistSpanChunk>? refreshSpan)
    {
        // One protocol-bounded chunk per game tick. A retained manifest can contain many
        // chunks, and applying all of them in one callback would recreate the join hitch
        // that chunking is meant to prevent.
        if (manifestChunks.TryDequeue(out QueuedManifest chunk))
        {
            List<long>? newlyOffered = null;
            foreach (long key in chunk.Keys)
            {
                if (chunk.Refresh)
                {
                    // A durable new version revives a key that an older request refused.
                    ReviveForRefresh(key);
                    (newlyOffered ??= new List<long>()).Add(key);
                }
                else if (!refused.Contains(key) && RemoteKeys.Add(key))
                {
                    (newlyOffered ??= new List<long>()).Add(key);
                }
            }

            // The callbacks run on this same owning-thread Pump. Each receives only this
            // chunk, so LodWorld registration happens once rather than by re-enumerating the
            // complete retained RemoteKeys set every game tick.
            //
            // A versioned span goes to its own callback whole, empty or not: an empty span
            // still moves the client's record forward, and its Last chunk is what ends it.
            // Without that callback it is read as the plain refresh it also is.
            if (chunk.Refresh && chunk.VersionTo > 0 && refreshSpan != null)
            {
                refreshSpan(new LodAssistSpanChunk(
                    chunk.VersionFrom, chunk.VersionTo, chunk.Keys, chunk.Versions, chunk.Last));
            }
            else if (newlyOffered != null)
            {
                if (chunk.Refresh) refreshKeys?.Invoke(newlyOffered.ToArray());
                else offerKeys?.Invoke(newlyOffered.ToArray());
            }

            if (chunk.Last && !chunk.Refresh)
            {
                // The first manifest is the join, and is worth a line. Everything after it is
                // a follow-up offer, and those now arrive every few seconds while a server
                // cache is growing. Repeating "complete" for each one would be noise, and the
                // count it announced at join is no longer the number to measure against.
                if (ManifestComplete)
                {
                    logger.Debug("Doden's Landscapes: server offered more sections, {0} now known",
                        RemoteKeys.Count);
                }
                else
                {
                    ManifestComplete = true;
                    // Announced vs applied: a mismatch means keys were captured or evicted
                    // mid-send, expected on a live server and worth seeing rather than silently
                    // tolerating once transfer starts trusting this set.
                    logger.Notification(
                        "Doden's Landscapes: server key manifest complete - {0} keys received{1}",
                        RemoteKeys.Count,
                        ManifestExpected > 0 && ManifestExpected != RemoteKeys.Count
                            ? $" (server announced {ManifestExpected})" : "");
                }
            }
        }

        var arrivalBudget = new LodDrainBudget(
            ArrivalMaxBytesPerTick, ArrivalMaxMillisecondsPerTick);
        while (Arrived.TryPeek(out var waiting)
            && arrivalBudget.TryStart(waiting.Blob.LongLength))
        {
            if (!Arrived.TryDequeue(out var got)) break;
            Interlocked.Add(ref arrivedBytes, -got.Blob.LongLength);

            // Call install even for an empty blob, then publish an explicit failure below.
            // Short-circuiting the reply path used to leave declined keys stuck in
            // LodWorld.LoadsInFlight for the session, pinning their parent coarse.
            if (install(got.Key, got.Blob, got.Version))
            {
                if (!deferredInstall) CompleteInstall(got.Key, installed: true);
                continue;
            }

            inFlight.Remove(got.Key);

            // "Not written yet" is not "never". The server says so explicitly, because
            // the two are the same empty packet otherwise, and treating not-yet as never
            // cost the player that section for the rest of the session even though the
            // row appeared seconds later. Left in RemoteKeys so the request loop picks it
            // up again; transferFailed restores the owning pipeline's retryable state.
            if (got.Retryable)
            {
                int tries = retriesByKey.TryGetValue(got.Key, out int n) ? n + 1 : 1;
                if (tries < MaxRetriesPerKey)
                {
                    retriesByKey[got.Key] = tries;
                    retryNotBeforeByKey[got.Key] = NowMs() + RetryDelayMs;
                    transferFailed?.Invoke(got.Key, true);
                    continue;
                }
                // Out of patience: fall through and refuse it, which is where a client
                // that never understood Retryable would have been on the first reply.
            }

            // Declined, gone, or already held locally. Remembering that is what stops us
            // asking every tick forever for something that will never arrive.
            retriesByKey.Remove(got.Key);
            retryNotBeforeByKey.Remove(got.Key);
            refused.Add(got.Key);
            RemoteKeys.Remove(got.Key);
            transferFailed?.Invoke(got.Key, false);
        }

        ArrivalItemsProcessed += arrivalBudget.Items;
        ArrivalBytesProcessed += arrivalBudget.Bytes;
    }

    /// <summary>
    /// Release a reply retained by <see cref="PumpAsync"/> after owning-thread
    /// publication or terminal rejection. <paramref name="retryable"/> is the third ending:
    /// the section arrived but could not be used yet (a merge whose target had left RAM), so
    /// the slot is released and the offer stays open for the pipeline to ask again.
    /// </summary>
    public void CompleteInstall(long key, bool installed, bool retryable = false)
    {
        inFlight.Remove(key);
        retriesByKey.Remove(key);
        retryNotBeforeByKey.Remove(key);
        if (installed)
        {
            SectionsReceived++;
            return;
        }
        if (retryable) return;

        refused.Add(key);
        RemoteKeys.Remove(key);
    }

    public void ResetPumpStats()
    {
        ArrivalItemsProcessed = 0;
        ArrivalBytesProcessed = 0;
    }

    /// <summary>Reset for the next world; the channel itself outlives the join.</summary>
    public void Reset()
    {
        NegotiatedProtocol = 0;
        serverProtocol = 0;
        serverFeatures = 0;
        Status = "not connected yet";
        ServerSettings = null;
        ServerCacheGeneration = "";
        CanControlWorldGeneration = false;
        WorldGenStatus = null;
        LastWorldGenControlError = "";
        worldGenPageOpen = false;
        nextWorldGenPollMs = 0;
        // IDs remain monotone across worlds. A late packet from the connection being torn
        // down must not become the first status of the next savegame.
        worldGenResetFloor = Math.Max(nextWorldGenRequestId, lastWorldGenStatusRequestId);
        nextWorldGenRequestId = worldGenResetFloor;
        lastWorldGenStatusRequestId = worldGenResetFloor;
        lastWorldGenControlResponseId = worldGenResetFloor;
        RemoteKeys.Clear();
        ManifestComplete = false;
        ManifestExpected = 0;
        inFlight.Clear();
        refused.Clear();
        retriesByKey.Clear();
        retryNotBeforeByKey.Clear();
        SectionsReceived = 0;
        SectionsRequested = 0;
        PeakInFlight = 0;
        ResetPumpStats();
        while (manifestChunks.TryDequeue(out _)) { }
        while (worldGenStatuses.TryDequeue(out _)) { }
        while (Arrived.TryDequeue(out var stale))
            Interlocked.Add(ref arrivedBytes, -stale.Blob.LongLength);
    }
}
