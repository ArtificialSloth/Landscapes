using ProtoBuf;

namespace VintageHorizons.Net;

/// <summary>
/// Wire contract for the optional server assist (DESIGN.md §10), shared by both sides.
///
/// The assist is strictly additive: a client with the mod must behave exactly as it did
/// before this existed when the server has no assist, and a server with the assist must
/// not change anything for players who do not have the mod. Every message here is
/// therefore either a request that may go unanswered or an answer that may be ignored.
/// </summary>
public static class LodAssist
{
    /// <summary>
    /// Channel name, identical on both sides or they do not link up. Registering it is
    /// safe against a vanilla server: the channel simply never reaches Connected.
    /// </summary>
    public const string ChannelName = "dodenslandscapes";

    /// <summary>
    /// Wire protocol version, bumped whenever a message changes meaning rather than
    /// gaining a field -- protobuf already ignores fields it does not know, so adding
    /// one is not a break. Both sides run <c>min(mine, theirs)</c> so a new server keeps
    /// working with the 0.1.1-era clients that are already in the wild.
    /// </summary>
    public const int Protocol = 1;

    /// <summary>
    /// Keys per manifest chunk. 2048 keys is ~16 KB, small enough not to stall a join
    /// that is already loading a world and large enough that a 5581-key world takes
    /// three messages rather than dozens.
    /// </summary>
    public const int ManifestKeysPerMessage = 2048;

    /// <summary>
    /// Most sections a client may have outstanding. At a mean 45.9 KB a section, 16 in
    /// flight is roughly 730 KB - enough to keep a join filling in, small enough that a
    /// player who sprints across unexplored land cannot ask for a whole world at once.
    /// </summary>
    public const int MaxSectionsInFlight = 16;

    /// <summary>
    /// Sections a server will serve one player per second. The cap exists so an admin can
    /// reason about the cost: 8/s is ~370 KB/s per player at the measured mean.
    ///
    /// Enforced server-side rather than trusted to <see cref="MaxSectionsInFlight"/>: a
    /// modified client ignores its own limit, so the client's is a courtesy and this is
    /// the actual bound.
    /// </summary>
    public const int MaxSectionsPerSecondPerPlayer = 8;

    /// <summary>
    /// Sections a server will serve per second in total, across every player. The
    /// per-player cap alone does not bound what the server pays: each section served is a
    /// main-thread SQLite blob read, so twenty players at 8/s each would be 160 reads a
    /// second of tick time. This is the number that protects the server, and the
    /// per-player cap only decides how it is shared.
    /// </summary>
    public const int MaxSectionsPerSecondTotal = 32;

    /// <summary>
    /// Silhouette tiles are much smaller than terrain sections, but decoding and remeshing still
    /// need an explicit client-side bound. Requests are batched at this size and retain their
    /// slots until success, refusal, or a retryable answer arrives.
    /// </summary>
    public const int MaxSilhouetteTilesInFlight = 16;

    /// <summary>Per-player tile replies per second, enforced by the server.</summary>
    public const int MaxSilhouetteTilesPerSecondPerPlayer = 20;

    /// <summary>Total tile replies per second across all players, enforced by the server.</summary>
    public const int MaxSilhouetteTilesPerSecondTotal = 80;

    /// <summary>
    /// Append-only silhouette delivery generation. Zero is the 0.4.77 direct-push prototype;
    /// one is the manifest/request path. Carrying this in the request preserves old-client
    /// interoperability without changing the channel's message order or protocol number.
    /// </summary>
    public const int SilhouetteTransportVersion = 1;

    /// <summary>
    /// Append-only section-sync generation a client advertises in <see cref="AssistHello.SectionSync"/>.
    /// Zero, which every older client sends, keeps the unversioned refresh behaviour; one means the
    /// client records the server cache's section versions and is owed every change past its record
    /// (terrain-change plan Phase B).
    /// </summary>
    public const int SectionSyncVersion = 1;

    /// <summary>
    /// Append-only capabilities advertised by <see cref="AssistWelcome.Features"/>. An older
    /// server sends zero; an older client ignores the field. Capability and terrain serving are
    /// separate so an admin may disable map sharing while still allowing explicit generation.
    /// </summary>
    public const int FeatureWorldGeneration = 1 << 0;

    // Reserved now so independently developed follow-up plans cannot accidentally reuse a
    // shipped meaning. A server advertises these only after the corresponding feature lands.
    public const int FeatureFrontierGeneration = 1 << 1;
    public const int FeatureSilhouetteTiles = 1 << 2;

    /// <summary>
    /// Silhouette messages flow only when both sides agree: a usable protocol and the server's
    /// feature bit. A client that sends without the bit would be talking to a server that never
    /// registered the handler.
    /// </summary>
    public static bool SilhouettesAvailable(int serverProtocol, int features) =>
        serverProtocol > 0 && (features & FeatureSilhouetteTiles) != 0;
}

/// <summary>Client -> server, once per join, only when the channel is Connected.</summary>
[ProtoContract]
public class AssistHello
{
    [ProtoMember(1)] public int Protocol;
    [ProtoMember(2)] public string ModVersion = "";

    /// <summary><see cref="LodAssist.SectionSyncVersion"/>, or 0 from a client that does not sync.</summary>
    [ProtoMember(3)] public int SectionSync;

    /// <summary>
    /// The server cache generation this client caught up with, or empty for none. A server whose
    /// generation differs answers with a span from <see cref="LodAssistSync.NoRecord"/>.
    /// </summary>
    [ProtoMember(4)] public string SyncGeneration = "";

    /// <summary>The version this client caught up to in that generation.</summary>
    [ProtoMember(5)] public long SyncVersion;
}

/// <summary>
/// Server -> client, in reply to <see cref="AssistHello"/>. <see cref="Enabled"/> is
/// separate from the protocol check on purpose: an admin who has turned the assist off
/// still gets a well-formed answer saying so, which is a diagnosable state, rather than
/// silence that looks identical to a vanilla server.
/// </summary>
[ProtoContract]
public class AssistWelcome
{
    [ProtoMember(1)] public int Protocol;
    [ProtoMember(2)] public string ModVersion = "";
    [ProtoMember(3)] public bool Enabled;

    /// <summary>Human-readable reason, surfaced verbatim by .dlinfo. Not parsed.</summary>
    [ProtoMember(4)] public string Status = "";

    /// <summary>
    /// How many section keys the server is about to send, so a client can report
    /// progress and size its set once instead of rehashing as chunks arrive. Zero when
    /// no manifest follows.
    /// </summary>
    [ProtoMember(5)] public int ManifestKeyCount;

    /// <summary>
    /// Diagnostic-only copy of every server config value. Optional so a new client can
    /// still join an older server; protobuf leaves it null when that peer does not know
    /// about the field.
    /// </summary>
    [ProtoMember(6)] public AssistServerSettings? ServerSettings;

    /// <summary>Bit field of append-only optional server features.</summary>
    [ProtoMember(7)] public int Features;

    /// <summary>Permission snapshot for this player; status remains readable without it.</summary>
    [ProtoMember(8)] public bool CanControlWorldGeneration;

    /// <summary>
    /// The server cache's generation when it versions its sections and this client asked to sync;
    /// empty otherwise, including from every older server. Empty means no versioned span follows.
    /// </summary>
    [ProtoMember(9)] public string CacheGeneration = "";
}

/// <summary>
/// The complete server configuration as it was sanitized and applied. This travels only
/// in the welcome packet so the development diagnostics window can report server-only
/// settings truthfully. It does not grant control over them.
/// </summary>
[ProtoContract]
public class AssistServerSettings
{
    [ProtoMember(1)] public bool EnableCapture;
    [ProtoMember(2)] public bool EnableServing;
    [ProtoMember(3)] public int ServeRadiusBlocks;
    [ProtoMember(4)] public int MaxSectionsPerSecondPerPlayer;
    [ProtoMember(5)] public int MaxSectionsPerSecondTotal;
    [ProtoMember(6)] public bool SweepSavegame;
    [ProtoMember(7)] public int SweepRadiusChunks;
    [ProtoMember(8)] public int SweepColumnsPerSecond;
    [ProtoMember(9)] public bool EnableGenerateCommand;
    [ProtoMember(10)] public int GenerateMaxRadiusChunks;
    [ProtoMember(11)] public int GenerateDefaultRadiusChunks;
    [ProtoMember(12)] public int GenerateColumnsPerSecond;
    [ProtoMember(13)] public int GenerateMaxInFlight;
    [ProtoMember(14)] public int PregenRadiusChunks;
    [ProtoMember(15)] public int PregenColumnsPerSecond;
    [ProtoMember(16)] public int RefreshColumnsPerSecond;
    [ProtoMember(17)] public bool EnableWorldGeneration;
    [ProtoMember(18)] public int WorldGenMaxRadiusChunks;
    [ProtoMember(19)] public int WorldGenDefaultRadiusChunks;
    [ProtoMember(20)] public int WorldGenQueueTarget;
    [ProtoMember(21)] public int WorldGenMaxInFlight;
    [ProtoMember(22)] public int WorldGenDirtySectionLimit;
    [ProtoMember(23)] public int WorldGenOldestDirtySeconds;
    [ProtoMember(24)] public bool SilhouetteUseAccelerators;
    [ProtoMember(25)] public int SilhouetteReachBlocks;
    [ProtoMember(26)] public int SilhouettePreviewsPerSecond;

    public static AssistServerSettings From(LodServerConfig config) => new()
    {
        EnableCapture = config.EnableCapture,
        EnableServing = config.EnableServing,
        ServeRadiusBlocks = config.ServeRadiusBlocks,
        MaxSectionsPerSecondPerPlayer = config.MaxSectionsPerSecondPerPlayer,
        MaxSectionsPerSecondTotal = config.MaxSectionsPerSecondTotal,
        SweepSavegame = config.SweepSavegame,
        SweepRadiusChunks = config.SweepRadiusChunks,
        SweepColumnsPerSecond = config.SweepColumnsPerSecond,
        EnableGenerateCommand = config.EnableGenerateCommand,
        GenerateMaxRadiusChunks = config.GenerateMaxRadiusChunks,
        GenerateDefaultRadiusChunks = config.GenerateDefaultRadiusChunks,
        GenerateColumnsPerSecond = config.GenerateColumnsPerSecond,
        GenerateMaxInFlight = config.GenerateMaxInFlight,
        PregenRadiusChunks = config.PregenRadiusChunks,
        PregenColumnsPerSecond = config.PregenColumnsPerSecond,
        RefreshColumnsPerSecond = config.RefreshColumnsPerSecond,
        EnableWorldGeneration = config.EnableWorldGeneration,
        WorldGenMaxRadiusChunks = config.WorldGenMaxRadiusChunks,
        WorldGenDefaultRadiusChunks = config.WorldGenDefaultRadiusChunks,
        WorldGenQueueTarget = config.WorldGenQueueTarget,
        WorldGenMaxInFlight = config.WorldGenMaxInFlight,
        WorldGenDirtySectionLimit = config.WorldGenDirtySectionLimit,
        WorldGenOldestDirtySeconds = config.WorldGenOldestDirtySeconds,
        SilhouetteUseAccelerators = config.SilhouetteUseAccelerators,
        SilhouetteReachBlocks = config.SilhouetteReachBlocks,
        SilhouettePreviewsPerSecond = config.SilhouettePreviewsPerSecond,
    };
}

/// <summary>Player action sent by the Generate page.</summary>
public enum EnumWorldGenControlAction
{
    Status,
    Prepare,
    Confirm,
    Pause,
    Resume,
    Stop,
}

/// <summary>How the Generate page resolves the centre of a new job.</summary>
public enum EnumWorldGenCentreMode
{
    Here,
    Spawn,
    Coordinates,
}

/// <summary>Stable states rendered by the client without parsing a human status sentence.</summary>
public enum EnumWorldGenRemoteState
{
    Unavailable,
    Idle,
    Prepared,
    Running,
    Paused,
    Interrupted,
    Faulted,
    Complete,
}

/// <summary>
/// Client -> server control request. RequestId correlates the result with the click that caused
/// it. Every request receives an <see cref="AssistWorldGenStatus"/>, including refusals.
/// </summary>
[ProtoContract]
public class AssistWorldGenRequest
{
    [ProtoMember(1)] public long RequestId;
    [ProtoMember(2)] public EnumWorldGenControlAction Action;
    [ProtoMember(3)] public int RadiusChunks;
    [ProtoMember(4)] public EnumWorldGenShape Shape;
    [ProtoMember(5)] public EnumWorldGenCentreMode CentreMode;
    [ProtoMember(6)] public int X;
    [ProtoMember(7)] public int Z;
    [ProtoMember(8)] public string Preset = "Balanced";

    /// <summary>
    /// X and Z are block coordinates measured from the world spawn, as the game's coordinate
    /// display shows them, and may be negative. False, which every client before 0.4.160 sends,
    /// keeps their old meaning: the game's internal block position, with -1 for "not given".
    /// </summary>
    [ProtoMember(9)] public bool SpawnRelative;
}

/// <summary>
/// Server -> client result plus the current structured status. Human text explains the latest
/// action; numeric fields drive the progress bar and estimates without parsing that text.
/// </summary>
[ProtoContract]
public class AssistWorldGenStatus
{
    [ProtoMember(1)] public long RequestId;
    [ProtoMember(2)] public bool Success;
    [ProtoMember(3)] public string Message = "";
    [ProtoMember(4)] public bool Available;
    [ProtoMember(5)] public bool CanControl;
    [ProtoMember(6)] public EnumWorldGenRemoteState State;
    [ProtoMember(7)] public bool PendingConfirmation;
    [ProtoMember(8)] public int DoneColumns;
    [ProtoMember(9)] public int TotalColumns;
    [ProtoMember(10)] public double ColumnsPerSecond;
    [ProtoMember(11)] public long EtaSeconds = -1;
    [ProtoMember(12)] public long SavegameBytes;
    [ProtoMember(13)] public long CacheBytes;
    [ProtoMember(14)] public string Gate = "";
    [ProtoMember(15)] public int EngineQueue;
    [ProtoMember(16)] public int QueueTarget;
    [ProtoMember(17)] public int InFlight;
    [ProtoMember(18)] public int Retries;
    [ProtoMember(19)] public int Failures;
    [ProtoMember(20)] public int RadiusChunks;
    [ProtoMember(21)] public EnumWorldGenShape Shape;
    [ProtoMember(22)] public int CentreCx;
    [ProtoMember(23)] public int CentreCz;
    [ProtoMember(24)] public int MaxRadiusChunks;
    [ProtoMember(25)] public int DefaultRadiusChunks;
    [ProtoMember(26)] public string Preset = "Balanced";
    [ProtoMember(27)] public int MaxInFlight;
    [ProtoMember(28)] public EnumWorldGenControlAction Action;
}

/// <summary>
/// Server -> client: which sections the server holds, as packed keys and nothing else.
/// Measured at 8 bytes a key and 5581 keys for a well-travelled world, so ~44 KB total -
/// cheap enough to send in full at join, which is why there is no spatial query here.
///
/// Chunked because one 44 KB message is a needless latency spike on a join that is
/// already busy, not because the reliable channel has a size limit (§10.7).
/// </summary>
[ProtoContract]
public class AssistKeyManifest
{
    [ProtoMember(1)] public int Sequence;

    /// <summary>Set on the final chunk, so the client knows the set is complete.</summary>
    [ProtoMember(2)] public bool Last;

    [ProtoMember(3)] public long[] Keys = Array.Empty<long>();

    /// <summary>
    /// These keys already existed when the client joined but now have newer durable L0
    /// contents. Older clients ignore this append-only field and retain their previous
    /// one-shot behavior; newer clients fetch the replacements through the same bounded
    /// request path as ordinary sections.
    ///
    /// Since section sync, a refresh with <see cref="VersionTo"/> above zero is a versioned span:
    /// the keys are every level-0 section whose version lies in (<see cref="VersionFrom"/>,
    /// <see cref="VersionTo"/>], the span may be empty, and its Last chunk marks where it ends.
    /// The flag stays set on spans so a client that predates versions still reads them as the
    /// refresh they are.
    /// </summary>
    [ProtoMember(4)] public bool Refresh;

    /// <summary>A versioned span's start, exclusive; <see cref="LodAssistSync.NoRecord"/> re-checks everything.</summary>
    [ProtoMember(5)] public long VersionFrom;

    /// <summary>A versioned span's end, inclusive; zero on an unversioned message.</summary>
    [ProtoMember(6)] public long VersionTo;

    /// <summary>Each key's version, parallel to <see cref="Keys"/>, on a versioned span.</summary>
    [ProtoMember(7)] public long[] Versions = Array.Empty<long>();
}

/// <summary>
/// Client -> server: send me these sections. Batched, and the client asks only for keys
/// the manifest offered that it has no local data for, so a request is never a duplicate
/// of something on disk.
///
/// The server decides what it is willing to send, but it MUST answer every key one way
/// or the other. An empty <see cref="AssistSection.Blob"/> is the refusal. A key left
/// unanswered is not retried later: the client holds it in flight waiting for a reply,
/// and once the in-flight cap fills with such keys it never asks for anything again.
/// This comment used to claim the opposite, and that belief is how the silent-drop paths
/// in LodAssistServerSystem survived as long as they did.
/// </summary>
[ProtoContract]
public class AssistSectionRequest
{
    [ProtoMember(1)] public long[] Keys = Array.Empty<long>();
}

/// <summary>
/// Server -> client: one section, as the stored blob verbatim.
///
/// Not chunked. Sections measure a mean 45.9 KB and a max 154.5 KB on a real world, and
/// the reliable channel has no size cap (the 508-byte warning is UDP-only, §10.7), so one
/// message per section is the simpler thing that works. If large messages turn out to
/// stall a join, chunking goes here - with the sequence/last pattern the manifest already
/// uses - rather than anywhere else.
///
/// <see cref="Blob"/> empty means "I am not sending this one": either it is gone or the
/// server declined. The client marks the key so it stops asking every few seconds.
/// </summary>
[ProtoContract]
public class AssistSection
{
    [ProtoMember(1)] public long Key;
    [ProtoMember(2)] public byte[] Blob = Array.Empty<byte>();

    /// <summary>
    /// Set on an empty blob when the server expects to have this section later, rather
    /// than not at all. The manifest is built from the section tree, which includes mip
    /// parents the pipeline has created in memory but not yet written, so "no row yet" is
    /// an ordinary state on a server that is still sweeping or generating.
    ///
    /// Without this a client cannot tell "not yet" from "never", and treats both as
    /// never: the key is refused permanently and the player loses that section for the
    /// whole session, even though the server writes it seconds later. The local sibling
    /// cache path already distinguishes the two and treats a miss as "not yet"; this is
    /// the same rule reaching the network path.
    ///
    /// A new field rather than a protocol bump, per the note on Protocol above: an older
    /// client never reads it and keeps its existing behaviour, which is the old bug and
    /// not a new one.
    /// </summary>
    [ProtoMember(3)] public bool Retryable;

    /// <summary>
    /// The version of the row this blob was read from, from a server that versions its cache;
    /// zero otherwise. A client owed a newer version than this keeps asking.
    /// </summary>
    [ProtoMember(4)] public long Version;
}

// Horizon silhouettes (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER 4.6). The four types are appended
// after AssistWorldGenStatus, in this order, on both sides; the engine numbers message types by
// registration order and checks nothing at handshake.

/// <summary>
/// Client -> server: this player wants silhouettes out to this reach, or no longer wants them.
/// Sent only when the welcome advertised <see cref="LodAssist.FeatureSilhouetteTiles"/>, after
/// the welcome and on change. An off request is the last silhouette message a client sends until
/// the player switches silhouettes back on.
/// </summary>
[ProtoContract]
public class AssistSilhouetteRequest
{
    [ProtoMember(1)] public bool Enabled;
    [ProtoMember(2)] public int ReachBlocks;
    [ProtoMember(3)] public string ModVersion = "";
    [ProtoMember(4)] public int TransportVersion;
    [ProtoMember(5)] public long SessionId;
}

/// <summary>
/// Server -> client: silhouette tile keys with their revisions, chunked with sequence/last like
/// <see cref="AssistKeyManifest"/>. A direct-push fallback remains only for older clients that
/// predate the final manifest/request transport.
/// </summary>
[ProtoContract]
public class AssistSilhouetteManifest
{
    [ProtoMember(1)] public int Sequence;
    [ProtoMember(2)] public bool Last;
    [ProtoMember(3)] public long[] Keys = Array.Empty<long>();
    [ProtoMember(4)] public int[] Revisions = Array.Empty<int>();
    [ProtoMember(5)] public long SessionId;
}

/// <summary>Client -> server: send these silhouette tiles. Every key is answered.</summary>
[ProtoContract]
public class AssistSilhouetteTileRequest
{
    [ProtoMember(1)] public long[] Keys = Array.Empty<long>();
    [ProtoMember(2)] public long SessionId;
}

/// <summary>
/// Server -> client: one silhouette tile in LodSilhouetteTile's format. An empty payload is a
/// refusal; <see cref="Retryable"/> marks "not yet".
/// </summary>
[ProtoContract]
public class AssistSilhouetteTile
{
    [ProtoMember(1)] public int TileX;
    [ProtoMember(2)] public int TileZ;
    [ProtoMember(3)] public int Revision;
    [ProtoMember(4)] public byte[] Payload = Array.Empty<byte>();
    [ProtoMember(5)] public bool Retryable;
    [ProtoMember(6)] public long SessionId;

    /// <summary>
    /// Exact requested key, including malformed/refused keys whose coordinates cannot encode the
    /// same value. A legacy reply is identified by SessionId zero and derives its key from the
    /// coordinates instead.
    /// </summary>
    [ProtoMember(7)] public long Key;
}
