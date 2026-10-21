using System.Reflection;
using ProtoBuf;
using VintageHorizons.Net;
using VintageHorizons.Silhouette;

namespace VintageHorizons.Checks;

/// <summary>
/// The packet layout on the wire, pinned.
///
/// One mod covers all four install combinations, so a 0.2.0 client can meet a 0.1.1
/// server and the reverse. Protobuf identifies a field by its number, not its name, so
/// renumbering a member or changing its type silently reinterprets an old peer's bytes.
/// The compiler cannot see it, the fast tier would not notice, and the symptom reaches a
/// player as garbled terrain or a handshake that fails for no stated reason.
///
/// So the numbers are asserted here rather than trusted. Adding a field with a NEW number
/// is safe and needs only a line below. Changing an existing number or type is a protocol
/// break, and this suite is where that decision gets made deliberately.
/// </summary>
public static class WireFormatChecks
{
    public static void Run(Check c)
    {
        FieldNumbersAreStable(c);
        PacketsRoundTrip(c);
    }

    /// <summary>Every [ProtoMember] on a type, as "number:name:type".</summary>
    static List<string> Layout(Type type)
    {
        var found = new List<string>();
        foreach (FieldInfo f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            var tag = f.GetCustomAttribute<ProtoMemberAttribute>();
            if (tag != null) found.Add($"{tag.Tag}:{f.Name}:{f.FieldType.Name}");
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    static void FieldNumbersAreStable(Check c)
    {
        // Section sync (terrain-change plan Phase B) appended 3-5 here, 9 on the welcome, 5-7 on
        // the manifest and 4 on the section. An older peer reads none of them.
        // 0.5.1 appended 6, the import-status generation; an older server ignores it.
        c.SeqEq(new[]
        {
            "1:Protocol:Int32", "2:ModVersion:String", "3:SectionSync:Int32",
            "4:SyncGeneration:String", "5:SyncVersion:Int64", "6:ImportStatus:Int32",
        }, Layout(typeof(AssistHello)), "AssistHello field numbers");
        c.SeqEq(new[]
        {
            "1:Active:Boolean", "2:Finished:Boolean", "3:BandBlocks:Int32",
            "4:PendingByBand:Int32[]", "5:ColumnsPerSecond:Double", "6:PendingTotal:Int32",
        }, Layout(typeof(AssistImportStatus)), "AssistImportStatus field numbers");

        // 10 tells the Farseer row whether a sampler mod answers; zero from older servers. Ordinal
        // sort puts it first.
        c.SeqEq(new[]
        {
            "10:SilhouetteHeightSource:Int32",
            "1:Protocol:Int32", "2:ModVersion:String", "3:Enabled:Boolean",
            "4:Status:String", "5:ManifestKeyCount:Int32",
            "6:ServerSettings:AssistServerSettings", "7:Features:Int32",
            "8:CanControlWorldGeneration:Boolean", "9:CacheGeneration:String",
        }, Layout(typeof(AssistWelcome)), "AssistWelcome field numbers");

        c.SeqEq(new[]
        {
            "10:GenerateMaxRadiusChunks:Int32", "11:GenerateDefaultRadiusChunks:Int32",
            "12:GenerateColumnsPerSecond:Int32", "13:GenerateMaxInFlight:Int32",
            "14:PregenRadiusChunks:Int32", "15:PregenColumnsPerSecond:Int32",
            "16:RefreshColumnsPerSecond:Int32", "17:EnableWorldGeneration:Boolean",
            "18:WorldGenMaxRadiusChunks:Int32", "19:WorldGenDefaultRadiusChunks:Int32",
            "1:EnableCapture:Boolean",
            "20:WorldGenQueueTarget:Int32", "21:WorldGenMaxInFlight:Int32",
            "22:WorldGenDirtySectionLimit:Int32", "23:WorldGenOldestDirtySeconds:Int32",
            "24:SilhouetteUseAccelerators:Boolean",
            "25:SilhouetteReachBlocks:Int32",
            "26:SilhouettePreviewsPerSecond:Int32",
            "2:EnableServing:Boolean",
            "3:ServeRadiusBlocks:Int32", "4:MaxSectionsPerSecondPerPlayer:Int32",
            "5:MaxSectionsPerSecondTotal:Int32", "6:SweepSavegame:Boolean",
            "7:SweepRadiusChunks:Int32", "8:SweepColumnsPerSecond:Int32",
            "9:EnableGenerateCommand:Boolean",
        }, Layout(typeof(AssistServerSettings)), "AssistServerSettings field numbers");

        c.SeqEq(new[]
        {
            "1:Sequence:Int32", "2:Last:Boolean", "3:Keys:Int64[]", "4:Refresh:Boolean",
            "5:VersionFrom:Int64", "6:VersionTo:Int64", "7:Versions:Int64[]",
        },
            Layout(typeof(AssistKeyManifest)), "AssistKeyManifest field numbers");

        c.SeqEq(new[] { "1:Keys:Int64[]" },
            Layout(typeof(AssistSectionRequest)), "AssistSectionRequest field numbers");

        c.SeqEq(new[]
        {
            "1:RequestId:Int64", "2:Action:EnumWorldGenControlAction",
            "3:RadiusChunks:Int32", "4:Shape:EnumWorldGenShape",
            "5:CentreMode:EnumWorldGenCentreMode", "6:X:Int32", "7:Z:Int32",
            "8:Preset:String", "9:SpawnRelative:Boolean",
        }, Layout(typeof(AssistWorldGenRequest)), "AssistWorldGenRequest field numbers");

        c.SeqEq(new[]
        {
            "10:ColumnsPerSecond:Double", "11:EtaSeconds:Int64",
            "12:SavegameBytes:Int64", "13:CacheBytes:Int64", "14:Gate:String",
            "15:EngineQueue:Int32", "16:QueueTarget:Int32", "17:InFlight:Int32",
            "18:Retries:Int32", "19:Failures:Int32", "1:RequestId:Int64",
            "20:RadiusChunks:Int32", "21:Shape:EnumWorldGenShape", "22:CentreCx:Int32",
            "23:CentreCz:Int32", "24:MaxRadiusChunks:Int32",
            "25:DefaultRadiusChunks:Int32", "26:Preset:String", "27:MaxInFlight:Int32",
            "28:Action:EnumWorldGenControlAction",
            "2:Success:Boolean",
            "3:Message:String", "4:Available:Boolean", "5:CanControl:Boolean",
            "6:State:EnumWorldGenRemoteState", "7:PendingConfirmation:Boolean",
            "8:DoneColumns:Int32", "9:TotalColumns:Int32",
        }, Layout(typeof(AssistWorldGenStatus)), "AssistWorldGenStatus field numbers");

        // Retryable was ADDED at 3, not slotted into an existing number. That is the one
        // shape of change this contract allows without a protocol bump: an older peer
        // ignores a field it does not know, and 1 and 2 still mean what they meant.
        c.SeqEq(new[] { "1:Key:Int64", "2:Blob:Byte[]", "3:Retryable:Boolean", "4:Version:Int64" },
            Layout(typeof(AssistSection)), "AssistSection field numbers");

        c.SeqEq(new[]
        {
            "1:Enabled:Boolean", "2:ReachBlocks:Int32", "3:ModVersion:String",
            "4:TransportVersion:Int32", "5:SessionId:Int64",
        }, Layout(typeof(AssistSilhouetteRequest)), "AssistSilhouetteRequest field numbers");
        c.SeqEq(new[]
        {
            "1:Sequence:Int32", "2:Last:Boolean", "3:Keys:Int64[]",
            "4:Revisions:Int32[]", "5:SessionId:Int64",
        }, Layout(typeof(AssistSilhouetteManifest)), "AssistSilhouetteManifest field numbers");
        c.SeqEq(new[] { "1:Keys:Int64[]", "2:SessionId:Int64" },
            Layout(typeof(AssistSilhouetteTileRequest)), "AssistSilhouetteTileRequest field numbers");
        c.SeqEq(new[]
        {
            "1:TileX:Int32", "2:TileZ:Int32", "3:Revision:Int32", "4:Payload:Byte[]",
            "5:Retryable:Boolean", "6:SessionId:Int64", "7:Key:Int64",
        }, Layout(typeof(AssistSilhouetteTile)), "AssistSilhouetteTile field numbers");

        // The negotiated protocol number itself. A client takes Math.Min of its own and
        // the server's, so bumping this is a deliberate compatibility decision.
        c.Eq(1, LodAssist.Protocol, "the protocol version is 1");
        c.Eq(1, LodAssist.FeatureWorldGeneration,
            "feature bit zero remains the Generate page");
        c.Eq(2, LodAssist.FeatureFrontierGeneration,
            "feature bit one is reserved for frontier generation");
        c.Eq(4, LodAssist.FeatureSilhouetteTiles,
            "feature bit two is reserved for silhouette tiles");
        c.Eq(1, LodAssist.SectionSyncVersion, "section sync is generation 1; older clients send 0");
    }

    /// <summary>
    /// Each packet survives a serialize and deserialize. The field numbers above say the
    /// contract has not moved. This says protobuf can actually carry it - including the
    /// empty blob that means "refused", which must not come back as null.
    /// </summary>
    static void PacketsRoundTrip(Check c)
    {
        var hello = Roundtrip(new AssistHello { Protocol = 1, ModVersion = "0.2.0" });
        c.Eq(1, hello.Protocol, "hello protocol survives");
        c.Eq("0.2.0", hello.ModVersion, "hello version survives");

        var welcome = Roundtrip(new AssistWelcome
        {
            Protocol = 1, ModVersion = "0.2.0", Enabled = true,
            Status = "serving from 5 cached sections", ManifestKeyCount = 5,
            Features = LodAssist.FeatureWorldGeneration,
            CanControlWorldGeneration = true,
            ServerSettings = new AssistServerSettings
            {
                EnableCapture = true,
                EnableServing = true,
                ServeRadiusBlocks = 8192,
                MaxSectionsPerSecondPerPlayer = 8,
                MaxSectionsPerSecondTotal = 32,
                SweepSavegame = true,
                SweepRadiusChunks = 128,
                SweepColumnsPerSecond = 16,
                EnableGenerateCommand = true,
                GenerateMaxRadiusChunks = 128,
                GenerateDefaultRadiusChunks = 32,
                GenerateColumnsPerSecond = 16,
                GenerateMaxInFlight = 64,
                PregenRadiusChunks = 0,
                PregenColumnsPerSecond = 8,
                RefreshColumnsPerSecond = 1,
                EnableWorldGeneration = true,
                WorldGenMaxRadiusChunks = 256,
                WorldGenDefaultRadiusChunks = 32,
                WorldGenQueueTarget = 120,
                WorldGenMaxInFlight = 160,
                WorldGenDirtySectionLimit = 512,
                WorldGenOldestDirtySeconds = 120,
                SilhouetteUseAccelerators = true,
                SilhouetteReachBlocks = 8192,
                SilhouettePreviewsPerSecond = 16,
            },
        });
        c.Eq(true, welcome.Enabled, "welcome enabled flag survives");
        c.Eq(5, welcome.ManifestKeyCount, "welcome manifest count survives");
        c.Eq("serving from 5 cached sections", welcome.Status, "welcome status survives");
        c.Eq(8192, welcome.ServerSettings?.ServeRadiusBlocks ?? -1,
            "welcome server settings survive");
        c.Eq(64, welcome.ServerSettings?.GenerateMaxInFlight ?? -1,
            "welcome generation settings survive");
        c.Eq(1, welcome.ServerSettings?.RefreshColumnsPerSecond ?? -1,
            "welcome rolling-refresh settings survive");
        c.Eq(LodAssist.FeatureWorldGeneration, welcome.Features,
            "welcome world-generation feature survives");
        c.True(welcome.CanControlWorldGeneration,
            "welcome world-generation permission survives");
        c.Eq(256, welcome.ServerSettings?.WorldGenMaxRadiusChunks ?? -1,
            "welcome real-world generation settings survive");
        c.True(welcome.ServerSettings?.SilhouetteUseAccelerators == true,
            "welcome silhouette accelerator setting survives");
        c.Eq(8192, welcome.ServerSettings?.SilhouetteReachBlocks ?? -1,
            "welcome dedicated silhouette reach survives");
        c.Eq(16, welcome.ServerSettings?.SilhouettePreviewsPerSecond ?? -1,
            "welcome silhouette preview rate survives");

        var worldGenRequest = Roundtrip(new AssistWorldGenRequest
        {
            RequestId = 17,
            Action = EnumWorldGenControlAction.Prepare,
            RadiusChunks = 48,
            Shape = EnumWorldGenShape.Circle,
            CentreMode = EnumWorldGenCentreMode.Coordinates,
            X = 1234,
            Z = 5678,
            Preset = "Balanced",
        });
        c.Eq(17L, worldGenRequest.RequestId, "world-generation request id survives");
        c.Eq(EnumWorldGenControlAction.Prepare, worldGenRequest.Action,
            "world-generation request action survives");
        c.Eq(EnumWorldGenCentreMode.Coordinates, worldGenRequest.CentreMode,
            "world-generation centre mode survives");
        c.Eq(1234, worldGenRequest.X, "world-generation x coordinate survives");

        var worldGenStatus = Roundtrip(new AssistWorldGenStatus
        {
            RequestId = 17,
            Success = true,
            Message = "prepared",
            Available = true,
            CanControl = true,
            State = EnumWorldGenRemoteState.Prepared,
            PendingConfirmation = true,
            DoneColumns = 12,
            TotalColumns = 120,
            ColumnsPerSecond = 9.5,
            EtaSeconds = 14,
            SavegameBytes = 1000,
            CacheBytes = 2000,
            Gate = "engine queue",
            EngineQueue = 80,
            QueueTarget = 120,
            InFlight = 16,
            Retries = 2,
            Failures = 1,
            RadiusChunks = 48,
            Shape = EnumWorldGenShape.Circle,
            CentreCx = 30,
            CentreCz = 40,
            MaxRadiusChunks = 256,
            DefaultRadiusChunks = 32,
            Preset = "Balanced",
            MaxInFlight = 160,
            Action = EnumWorldGenControlAction.Confirm,
        });
        c.Eq(EnumWorldGenRemoteState.Prepared, worldGenStatus.State,
            "world-generation structured state survives");
        c.True(worldGenStatus.PendingConfirmation,
            "world-generation confirmation state survives");
        c.Near(9.5, worldGenStatus.ColumnsPerSecond, 1e-9,
            "world-generation measured rate survives");
        c.Eq(2000L, worldGenStatus.CacheBytes,
            "world-generation cache growth survives");
        c.Eq(256, worldGenStatus.MaxRadiusChunks,
            "world-generation server radius limit survives");
        c.Eq(160, worldGenStatus.MaxInFlight,
            "world-generation kept-column limit survives");
        c.Eq(EnumWorldGenControlAction.Confirm, worldGenStatus.Action,
            "world-generation response action survives");

        var keys = new[] { 0L, 1L, -1L, long.MaxValue, long.MinValue };
        var manifest = Roundtrip(new AssistKeyManifest
        {
            Sequence = 2, Last = true, Keys = keys, Refresh = true,
        });
        c.SeqEq(keys, manifest.Keys, "manifest keys survive, including the extremes");
        c.Eq(true, manifest.Last, "the manifest last flag survives");
        c.True(manifest.Refresh, "the append-only refresh marker survives");

        var span = Roundtrip(new AssistKeyManifest
        {
            Sequence = 0, Last = true, Keys = new[] { 7L, 9L }, Refresh = true,
            VersionFrom = LodAssistSync.NoRecord, VersionTo = 12, Versions = new[] { 0L, 11L },
        });
        c.Eq((LodAssistSync.NoRecord, 12L), (span.VersionFrom, span.VersionTo),
            "a span's bounds survive, including the re-check-everything start");
        c.SeqEq(new[] { 0L, 11L }, span.Versions, "a span's per-key versions survive");

        // The compatibility the append-only rule promises, checked against the older shapes.
        var oldClientReads = Reshape<AssistKeyManifest, PreSyncKeyManifest>(span);
        c.SeqEq(new[] { 7L, 9L }, oldClientReads.Keys,
            "a client from before section sync reads a span's keys");
        c.True(oldClientReads.Refresh && oldClientReads.Last,
            "and reads it as the refresh it has always handled");

        var newServerReads = Reshape<PreSyncHello, AssistHello>(
            new PreSyncHello { Protocol = 1, ModVersion = "0.4.125" });
        c.Eq((0, ""), (newServerReads.SectionSync, newServerReads.SyncGeneration),
            "a greeting from before section sync reads as a client that does not sync");

        var hello2 = Roundtrip(new AssistHello
        {
            Protocol = 1, ModVersion = "0.4.126", SectionSync = LodAssist.SectionSyncVersion,
            SyncGeneration = "abc", SyncVersion = LodAssistSync.NoRecord,
        });
        c.Eq((LodAssist.SectionSyncVersion, "abc", LodAssistSync.NoRecord),
            (hello2.SectionSync, hello2.SyncGeneration, hello2.SyncVersion),
            "a syncing greeting survives, including the no-record version");

        var oldServerWelcome = Reshape<PreSyncWelcome, AssistWelcome>(
            new PreSyncWelcome { Protocol = 1, ModVersion = "0.4.125", Enabled = true });
        c.Eq("", oldServerWelcome.CacheGeneration ?? "",
            "a welcome from before section sync names no generation, so no span is expected");

        var versioned = Roundtrip(new AssistSection { Key = 5, Blob = new byte[] { 1 }, Version = 44 });
        c.Eq(44L, versioned.Version, "a section's version survives");

        var request = Roundtrip(new AssistSectionRequest { Keys = keys });
        c.SeqEq(keys, request.Keys, "request keys survive");

        var section = Roundtrip(new AssistSection { Key = 42, Blob = new byte[] { 9, 8, 7 } });
        c.Eq(42L, section.Key, "section key survives");
        c.SeqEq(new byte[] { 9, 8, 7 }, section.Blob, "section blob survives");

        // The refusal. An empty blob is the server saying no, and the client keys its
        // whole in-flight release on receiving one - so it must arrive as an empty array
        // or a null, never as something the handler mistakes for data.
        var refusal = Roundtrip(new AssistSection { Key = 42 });
        c.Eq(42L, refusal.Key, "a refusal carries its key");
        c.Eq(0, refusal.Blob?.Length ?? 0, "a refusal carries no blob bytes");

        const long silhouetteSession = 91234;
        long tileKey = LodSilhouetteTile.KeyOf(12, -7);
        var silhouetteRequest = Roundtrip(new AssistSilhouetteRequest
        {
            Enabled = true, ReachBlocks = 8192, ModVersion = "0.4.86",
            TransportVersion = LodAssist.SilhouetteTransportVersion, SessionId = silhouetteSession,
        });
        c.Eq((LodAssist.SilhouetteTransportVersion, silhouetteSession),
            (silhouetteRequest.TransportVersion, silhouetteRequest.SessionId),
            "silhouette transport and world session survive");
        var silhouetteManifest = Roundtrip(new AssistSilhouetteManifest
        {
            Sequence = 3, Last = true, Keys = new[] { tileKey }, Revisions = new[] { 9 },
            SessionId = silhouetteSession,
        });
        c.Eq((3, true, silhouetteSession),
            (silhouetteManifest.Sequence, silhouetteManifest.Last, silhouetteManifest.SessionId),
            "silhouette manifest framing survives");
        c.SeqEq(new[] { tileKey }, silhouetteManifest.Keys, "silhouette manifest keys survive");
        c.SeqEq(new[] { 9 }, silhouetteManifest.Revisions, "silhouette manifest revisions survive");
        var silhouetteTileRequest = Roundtrip(new AssistSilhouetteTileRequest
        {
            Keys = new[] { tileKey }, SessionId = silhouetteSession,
        });
        c.Eq(silhouetteSession, silhouetteTileRequest.SessionId,
            "silhouette tile request session survives");
        var silhouetteTile = Roundtrip(new AssistSilhouetteTile
        {
            TileX = 12, TileZ = -7, Key = tileKey, Revision = 9,
            Payload = new byte[] { 1, 2, 3 }, Retryable = true, SessionId = silhouetteSession,
        });
        c.Eq((tileKey, 9, silhouetteSession),
            (silhouetteTile.Key, silhouetteTile.Revision, silhouetteTile.SessionId),
            "silhouette tile identity survives");
        c.True(silhouetteTile.Retryable && silhouetteTile.Payload.SequenceEqual(new byte[] { 1, 2, 3 }),
            "silhouette tile payload and retry state survive");

        // 0.5.1's import status. A hello from an older client carries no field 6, which must read
        // as 0: the server then never sends a message that client did not register.
        c.Eq(0, Roundtrip(new AssistHello { Protocol = 1 }).ImportStatus,
            "an older client's hello asks for no import status");
        c.Eq(LodAssist.ImportStatusVersion,
            Roundtrip(new AssistHello { ImportStatus = LodAssist.ImportStatusVersion }).ImportStatus,
            "a 0.5.1 client's request for it survives");
        var import = Roundtrip(new AssistImportStatus
        {
            Active = true, BandBlocks = LodImportStatusBands.BandBlocks,
            PendingByBand = new[] { 5, 0, 1200, 7 }, ColumnsPerSecond = 7.5, PendingTotal = 1212,
        });
        c.True(import.Active && !import.Finished && import.ColumnsPerSecond == 7.5
                && import.PendingTotal == 1212 && import.BandBlocks == 512,
            "import status flags, pace and totals survive");
        c.True(import.PendingByBand.SequenceEqual(new[] { 5, 0, 1200, 7 }),
            "the per-band counts survive in order, zero bands included");
    }

    static T Roundtrip<T>(T value)
    {
        using var buffer = new MemoryStream();
        Serializer.Serialize(buffer, value);
        buffer.Position = 0;
        return Serializer.Deserialize<T>(buffer);
    }

    /// <summary>Write one shape and read it as another, as two builds on the wire do.</summary>
    static TTo Reshape<TFrom, TTo>(TFrom value)
    {
        using var buffer = new MemoryStream();
        Serializer.Serialize(buffer, value);
        buffer.Position = 0;
        return Serializer.Deserialize<TTo>(buffer);
    }

    // The shapes 0.4.125 shipped, frozen here so the older side of each pairing can be read.
    // Protobuf assigns their fields when reading; the compiler cannot see that.
#pragma warning disable CS0649

    [ProtoContract]
    sealed class PreSyncHello
    {
        [ProtoMember(1)] public int Protocol;
        [ProtoMember(2)] public string ModVersion = "";
    }

    [ProtoContract]
    sealed class PreSyncWelcome
    {
        [ProtoMember(1)] public int Protocol;
        [ProtoMember(2)] public string ModVersion = "";
        [ProtoMember(3)] public bool Enabled;
    }

    [ProtoContract]
    sealed class PreSyncKeyManifest
    {
        [ProtoMember(1)] public int Sequence;
        [ProtoMember(2)] public bool Last;
        [ProtoMember(3)] public long[] Keys = Array.Empty<long>();
        [ProtoMember(4)] public bool Refresh;
    }
#pragma warning restore CS0649
}
