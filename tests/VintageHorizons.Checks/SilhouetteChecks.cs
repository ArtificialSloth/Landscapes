using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using VintageHorizons.Net;
using VintageHorizons.Silhouette;
using Vintagestory.Client.NoObf;

namespace VintageHorizons.Checks;

/// <summary>
/// Horizon silhouettes, first look (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER P1). First the tile
/// format, the source order, the lattice interpolation and the mesh, as pure functions; then the
/// wiring no compiler sees: message order on both sides, declared uniforms, the sampler layout.
/// </summary>
public static class SilhouetteChecks
{
    // Positive, like real world coordinates.
    const int TileX = 1000, TileZ = 2000;

    public static void Run(Check c)
    {
        TileCellMapping(c);
        TileSourceOrder(c);
        TileRoundTrip(c);
        TileRejectsBadPayloads(c);
        FillFromKnownCells(c);
        FeatherSoftensPeaks(c);
        MeshMatchesAcrossTileBorders(c);
        MeshSeaAndCoverage(c);
        MeshResolutionsAndAnchoring(c);
        ClientSettingsArithmetic(c);
        FeatureGate(c);
        GeneratorStallWatch(c);
        TileMergeAndSnapshot(c);
        WorldgenErrorBrake(c);
        HeightComparisonArithmetic(c);
        PresampleAndMapFiles(c);
        AcceleratorSelectionAndSampling(c);
        TileTransferState(c);
        BuilderYieldRules(c);
    }

    public static void RunWiring(Check c)
    {
        MessageOrderMatches(c);
        PreviewsNeverReachCapture(c);
        GeneratorSilenceStopsTheBuilder(c);
        CapturedHeightsReachTiles(c);
        StoreKeepsTilesAcrossSessions(c);
        ClientTilesPersistAcrossRejoin(c);
        StoredTilesAreReadBeforePreviewing(c);
        PreviewsStayFewPerRegion(c);
        AcceleratorComparisonWiring(c);
        TileTransportWiring(c);
        UploadedUniformsAreDeclared(c);
        SamplerLayout(c);
        OffStateIsTheOldBehaviour(c);
        P4RenderingAndSettingsWiring(c);
    }

    /// <summary>The bounded manifest/request lifecycle, without a live network channel.</summary>
    static void TileTransferState(Check c)
    {
        const long session = 47;
        var state = new LodSilhouetteTransferState();
        state.Reset(session);
        long[] keys = Enumerable.Range(0, LodAssist.ManifestKeysPerMessage + 1)
            .Select(i => LodSilhouetteTile.KeyOf(i - 1024, i % 7 - 3)).ToArray();
        int[] revisions = Enumerable.Repeat(1, LodAssist.ManifestKeysPerMessage).ToArray();
        c.True(state.Apply(new AssistSilhouetteManifest
        {
            Sequence = 0, Last = false, SessionId = session,
            Keys = keys[..LodAssist.ManifestKeysPerMessage], Revisions = revisions,
        }), "the first protocol-sized silhouette manifest chunk is accepted");
        c.True(state.Apply(new AssistSilhouetteManifest
        {
            Sequence = 1, Last = true, SessionId = session,
            Keys = keys[^1..], Revisions = new[] { 2 },
        }), "the final silhouette manifest chunk is accepted");
        c.Eq(LodAssist.ManifestKeysPerMessage + 1, state.Offered,
            "manifest chunking retains every offered key");
        c.True(state.ManifestComplete, "the final chunk completes the silhouette manifest");
        c.False(state.Apply(new AssistSilhouetteManifest
        {
            Sequence = 2, Last = true, SessionId = session - 1,
            Keys = new[] { LodSilhouetteTile.KeyOf(9999, 9999) }, Revisions = new[] { 1 },
        }), "a previous world's manifest is rejected");
        c.False(state.Apply(new AssistSilhouetteManifest
        {
            Sequence = 2, Last = true, SessionId = session,
            Keys = new[] { keys[0] }, Revisions = Array.Empty<int>(),
        }), "a manifest whose key and revision arrays disagree is rejected");

        long[] requested = state.Select(0, 0, _ => 0, now: 1000);
        c.Eq(LodAssist.MaxSilhouetteTilesInFlight, requested.Length,
            "one request batch fills but never exceeds the silhouette in-flight cap");
        c.Eq(LodAssist.MaxSilhouetteTilesInFlight, state.InFlight,
            "request slots remain held until replies arrive");
        c.Eq(0, state.Select(0, 0, _ => 0, now: 1000).Length,
            "a full in-flight set admits no more requests");

        long retry = requested[0];
        c.False(state.BeginReply(session - 1, retry),
            "an old-session reply cannot release a current-world slot");
        c.True(state.BeginReply(session, retry), "a current reply releases its exact slot");
        c.True(state.Failed(retry, retryable: true, now: 1000),
            "a retryable answer leaves the offer alive");
        c.False(state.Select(0, 0, _ => 0, now: 1000).Contains(retry),
            "retry backoff prevents an immediate request loop");
        foreach (long key in requested.Skip(1))
        {
            c.True(state.BeginReply(session, key), "each current reply releases its slot");
            state.Succeeded(key);
        }
        c.True(state.Select(0, 0, key => key == retry ? 0 : 1,
                now: 1000 + LodSilhouetteTransferState.RetryDelayMs).Contains(retry),
            "the retryable key becomes eligible again after its bounded delay");
        c.True(state.BeginReply(session, retry), "the retried key owns a new slot");
        c.False(state.Failed(retry, retryable: false, now: 9999),
            "an explicit refusal is terminal");
        c.Eq(1, state.Refused, "terminal refusals are remembered");
        state.Prune(key => key == keys[1]);
        c.Eq(1, state.Offered, "moving reach prunes old manifest offers from client memory");
        c.True(LodSilhouetteClient.WithinReach(LodSilhouetteTile.KeyOf(3, 0), 0, 0, 1536),
            "the one-tile margin keeps the tile whose near edge meets it");
        c.False(LodSilhouetteClient.WithinReach(LodSilhouetteTile.KeyOf(4, 0), 0, 0, 1536),
            "a tile beyond the reach and margin is not retained");
    }

    static void BuilderYieldRules(Check c)
    {
        c.False(LodSilhouetteBuilder.MustYield(EnumWorldGenAuthority.None, EnumWorldGenGate.None, 0),
            "an idle generator leaves silhouette work runnable");
        foreach (EnumWorldGenAuthority authority in Enum.GetValues<EnumWorldGenAuthority>())
            if (authority != EnumWorldGenAuthority.None)
                c.True(LodSilhouetteBuilder.MustYield(authority, EnumWorldGenGate.None, 0),
                    "silhouettes yield to authority " + authority);
        foreach (EnumWorldGenGate gate in Enum.GetValues<EnumWorldGenGate>())
            if (gate != EnumWorldGenGate.None)
                c.True(LodSilhouetteBuilder.MustYield(EnumWorldGenAuthority.None, gate, 0),
                    "silhouettes yield to gate " + gate);
        c.True(LodSilhouetteBuilder.MustYield(EnumWorldGenAuthority.None, EnumWorldGenGate.None,
                LodSilhouetteBuilder.YieldGeneratingChunks),
            "the measured real-chunk threshold independently yields silhouette work");
    }

    static void TileTransportWiring(Check c)
    {
        string client = Source("src", "Net", "LodAssistClient.cs");
        string server = Source("src", "Net", "LodAssistServerSystem.cs");
        string builder = Source("src", "Silhouette", "LodSilhouetteBuilder.cs");
        string silhouette = Source("src", "Silhouette", "LodSilhouetteClient.cs");
        string transfer = Source("src", "Silhouette", "LodSilhouetteTransferState.cs");
        c.True(client.Contains("SetMessageHandler<AssistSilhouetteManifest>(OnSilhouetteManifest)",
                StringComparison.Ordinal)
                && server.Contains("SetMessageHandler<AssistSilhouetteTileRequest>(OnSilhouetteTileRequest)",
                    StringComparison.Ordinal),
            "both halves install the final manifest/request handlers");
        c.True(builder.Contains("Take(LodAssist.ManifestKeysPerMessage)", StringComparison.Ordinal)
                && builder.Contains("Sequence = demand.ManifestSequence", StringComparison.Ordinal)
                && builder.Contains("demand.ManifestSequence++", StringComparison.Ordinal),
            "the server chunks silhouette manifests with an ordered sequence");
        c.True(builder.Contains("MaxSilhouetteTilesPerSecondTotal", StringComparison.Ordinal)
                && builder.Contains("demand.SendAllowance", StringComparison.Ordinal),
            "tile replies have independent total and per-player limits");
        c.True(builder.Contains("SendRefusal", StringComparison.Ordinal)
                && builder.Contains("Retryable = retryable", StringComparison.Ordinal),
            "server tile requests end in data, refusal, or retryable state");
        c.True(builder.Contains("demand.Requests.Peek()", StringComparison.Ordinal)
                && builder.Contains("if (!sendTile", StringComparison.Ordinal),
            "a failed channel send retains the request instead of silently consuming it");
        c.True(transfer.Contains("MaxSilhouetteTilesInFlight", StringComparison.Ordinal)
                && silhouette.Contains("message.SessionId", StringComparison.Ordinal)
                && silhouette.Contains("message.Revision < wantedRevision", StringComparison.Ordinal),
            "the client caps requests and rejects old sessions and stale revisions");
        c.True(builder.Contains("TransportVersion < LodAssist.SilhouetteTransportVersion", StringComparison.Ordinal)
                && silhouette.Contains("message.SessionId == 0", StringComparison.Ordinal),
            "the direct-push prototype remains usable only as a mixed-version fallback");
    }

    /// <summary>
    /// The accelerator comparison's arithmetic on a hand-computed set: nearest-rank percentiles,
    /// bias, land/sea disagreement overall and on the coast, and quiet sea-level answers.
    /// </summary>
    static void HeightComparisonArithmetic(Check c)
    {
        const int sea = 110;
        var points = new List<LodHeightPoint>
        {
            new(0, 0, 120, 121, EnumSilhouetteOrigin.Captured),
            new(4, 0, 130, 127, EnumSilhouetteOrigin.Captured),
            new(8, 0, 112, 108, EnumSilhouetteOrigin.Preview),
            new(12, 0, 100, 100, EnumSilhouetteOrigin.Captured),
            new(16, 0, 150, 170, EnumSilhouetteOrigin.Preview),
            new(20, 0, 109, 110, EnumSilhouetteOrigin.Captured),
        };
        LodHeightComparison r = LodHeightComparison.Of(points, sea, new[] { 1.0, 2.0, 3.0 });
        c.Eq(6, r.Count, "every point is compared");
        c.Eq((4, 2), (r.Captured, r.Previewed), "points are counted by where their real height came from");
        c.Eq((1, 20, 20, 20), (r.MedianAbs, r.P90Abs, r.P95Abs, r.MaxAbs),
            "errors 0,1,1,3,4,20 give median 1 and 90%, 95% and max 20 by nearest rank");
        c.Near(2.5, r.MeanSigned, 1e-9, "the bias is the mean signed error (+15 over 6)");
        c.Eq(2, r.LandSeaDisagreements, "land read as sea and sea read as land both count");
        c.Eq((2, 1, 2), (r.CoastCount, r.CoastMedianAbs, r.CoastLandSeaDisagreements),
            "coast points are those within 4 blocks of sea level, with their own figures");
        c.Eq((1, 0), (r.SampledAtSeaLevel, r.RealAtSeaLevel), "answers exactly at sea level are counted");
        c.Eq((2.0, 3.0, 3.0), (r.MeanMs, r.P95Ms, r.MaxMs), "sample times give mean, 95% and max");
        c.Eq(16, r.Worst[0].X, "the worst point comes first");
        c.True(r.Describe("Test sampler").Contains("land/sea disagrees at 2 points (33.3%)", StringComparison.Ordinal),
            "the report states the land/sea disagreement plainly");
        c.Eq(0, LodHeightComparison.Of(new List<LodHeightPoint>(), sea, Array.Empty<double>()).Count,
            "an empty comparison reports no points");
    }

    /// <summary>
    /// The comparison's square and files (P0.3; Session 94's side-by-side and unexplored-land test):
    /// the cells of a chunk square, and presample and map files that read back what was written.
    /// </summary>
    static void PresampleAndMapFiles(Check c)
    {
        List<(int X, int Z)> cells = LodAcceleratorPresampleRun.SquareCells(100, -3, 1);
        c.Eq(9 * LodSilhouetteTile.ChunkCells, cells.Count, "a square of radius 1 is 3x3 chunks of 8x8 cells");
        c.True(cells.Contains((99 * 32, -4 * 32)) && cells.Contains((101 * 32 + 28, -2 * 32 + 28))
                && cells.All(p => p.X % 4 == 0 && p.Z % 4 == 0),
            "cells are the 4-block corners of every chunk in the square, negative coordinates included");

        string dir = Path.Combine(Path.GetTempPath(), "vh-silhouette-compare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var presample = new LodPresample
            {
                Accelerators = new[] { "A", "B" }, SeaLevel = 110, TakenUtc = "2026-09-15T00:00:00Z",
            };
            presample.Cells[(0, 4)] = new LodPresampleCell(false, new int?[] { 112, null }, new[] { 1.5, 0 });
            presample.Cells[(-8, 12)] = new LodPresampleCell(true, new int?[] { 90, 91 }, new[] { 0.25, 0.5 });
            string path = Path.Combine(dir, "w-presample.csv.gz");
            LodHeightSampleFile.WritePresample(path, presample);
            LodPresample? back = LodHeightSampleFile.ReadPresample(path);
            c.True(back != null && back.Accelerators.SequenceEqual(new[] { "A", "B" }) && back.SeaLevel == 110
                    && back.TakenUtc == presample.TakenUtc,
                "a presample reads back its accelerators, sea level and time");
            c.True(back != null && back.Cells.Count == 2
                    && back.Cells[(0, 4)] is { Held: false } first && first.Heights[0] == 112 && first.Heights[1] == null
                    && first.Ms[0] == 1.5 && back.Cells[(-8, 12)].Held && back.Cells[(-8, 12)].Heights[1] == 91,
                "every cell reads back, with a failed answer as none");
            c.True(LodHeightSampleFile.ReadPresample(Path.Combine(dir, "missing.csv.gz")) == null,
                "a missing presample reads as none");

            string map = Path.Combine(dir, "w-map.csv.gz");
            LodHeightSampleFile.WriteMap(map, new[] { "A" }, 110, "t", new[]
            {
                new LodMapRow(0, 4, 111, EnumSilhouetteOrigin.Captured, new int?[] { 112 }, new int?[] { 113 }),
                new LodMapRow(4, 4, 90, EnumSilhouetteOrigin.Preview, new int?[] { null }, null),
            });
            string[] rows;
            using (var reader = new StreamReader(new GZipStream(File.OpenRead(map), CompressionMode.Decompress)))
                rows = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
            c.True(rows[0] == "# vintagehorizons silhouette map 1" && rows.Contains("x,z,real,origin,a0now,a0before")
                    && rows.Contains("0,4,111,C,112,113") && rows.Contains("4,4,90,P,,"),
                "the map has a header and one row per cell, empty where nothing was answered");
            c.True(LodHeightSampleFile.ReadPresample(map) == null, "a map is never read as a presample");
            c.False(File.Exists(path + ".tmp") || File.Exists(map + ".tmp"),
                "files are moved into place, leaving no temporary copy");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    static void AcceleratorComparisonWiring(Check c)
    {
        string assist = Source("src", "Net", "LodAssistServerSystem.cs");
        string run = Source("src", "Silhouette", "LodAcceleratorComparisonRun.cs");
        string builder = Source("src", "Silhouette", "LodSilhouetteBuilder.cs");
        string presampleRun = Source("src", "Silhouette", "LodAcceleratorPresampleRun.cs");
        c.True(Regex.IsMatch(assist, @"BeginSubCommand\(""presample""\)[\s\S]*?HandleWith\(StartAcceleratorPresample\)"),
            "the presample is a subcommand of the same admin command");
        c.True(presampleRun.Contains("IsBackground = true", StringComparison.Ordinal)
                && presampleRun.Contains("accelerators[a].Sample(", StringComparison.Ordinal),
            "the presample calls accelerators on its own background thread");
        c.True(assist.Contains("sapi.WorldManager.MapSizeX / Vintagestory.API.Config.GlobalConstants.ChunkSize / 2",
                StringComparison.Ordinal),
            "from the console both commands centre where /wgen pregen does");
        c.True(run.Contains("LodHeightSampleFile.ReadPresample(presamplePath)", StringComparison.Ordinal)
                && run.Contains("LodHeightSampleFile.WriteMap(", StringComparison.Ordinal),
            "the comparison reads the presample and writes the map");
        c.False(Source("src", "Silhouette", "LodHeightAccelerators.cs").Contains("before rivers", StringComparison.Ordinal),
            "no accelerator is labelled as before rivers: both include river valleys (G205)");
        c.True(Regex.IsMatch(assist,
                @"Create\(""vhsilhouetteserver""\)[\s\S]*?RequiresPrivilege\(Privilege\.controlserver\)[\s\S]*?BeginSubCommand\(""compare""\)"),
            "the accelerator comparison is an admin subcommand of the server's silhouette command");
        c.True(run.Contains("IsBackground = true", StringComparison.Ordinal)
                && run.Contains("accelerator.Sample(", StringComparison.Ordinal),
            "accelerators are called on the comparison's own background thread");
        c.True(builder.Contains("ILodHeightAccelerator? accelerator", StringComparison.Ordinal)
                && builder.Contains("LodSilhouetteAcceleratorSampling.Apply", StringComparison.Ordinal)
                && builder.Contains("engine previews take over", StringComparison.Ordinal),
            "the accepted accelerator runs in the builder and a named failure falls back to previews");
        c.True(assist.Contains("LodHeightAccelerators.Preferred(LodHeightAccelerators.Find(sapi))",
                StringComparison.Ordinal),
            "the live builder selects one accepted accelerator through the explicit preference rule");
    }

    sealed class FakeAccelerator(EnumLodHeightAccelerator kind, string name) : ILodHeightAccelerator
    {
        public EnumLodHeightAccelerator Kind => kind;
        public string Name => name;
        public int[] Sample(IReadOnlyList<(int X, int Z)> positions) => new int[positions.Count];
    }

    /// <summary>The owner's accepted P0.3 choice and the coarse tile geometry it unlocks.</summary>
    static void AcceleratorSelectionAndSampling(Check c)
    {
        var algernon = new FakeAccelerator(EnumLodHeightAccelerator.AlgernonsTerrainSampler, "Algernon");
        var watersheds = new FakeAccelerator(EnumLodHeightAccelerator.Watersheds, "Watersheds");
        c.True(ReferenceEquals(watersheds, LodHeightAccelerators.Preferred(new[] { algernon, watersheds })),
            "Watersheds is preferred when both accepted accelerators are installed");
        c.True(ReferenceEquals(algernon, LodHeightAccelerators.Preferred(new[] { algernon })),
            "Algernon's Terrain Sampler is accepted when Watersheds is absent");
        c.True(LodHeightAccelerators.Preferred(Array.Empty<ILodHeightAccelerator>()) == null,
            "no installed accelerator leaves the standalone preview path");

        (int X, int Z)[] positions = LodSilhouetteAcceleratorSampling.Positions(TileX, TileZ);
        c.Eq(64, positions.Length, "a tile gets one accepted sample every 64 blocks");
        c.Eq((TileX * 512, TileZ * 512), positions[0], "the coarse grid starts at the tile's minimum corner");
        c.Eq((TileX * 512 + 448, TileZ * 512 + 448), positions[^1],
            "the coarse grid ends one spacing before the next tile");

        var tile = new LodSilhouetteTile(TileX, TileZ);
        c.True(LodSilhouetteAcceleratorSampling.NeedsSamples(tile), "an empty tile needs its coarse samples");
        int[] heights = Enumerable.Repeat(120, positions.Length).ToArray();
        c.True(LodSilhouetteAcceleratorSampling.Apply(tile, heights), "the coarse result changes the tile");
        c.Eq(64, tile.KnownCells, "all coarse points are stored as sparse cells");
        c.Eq(EnumSilhouetteOrigin.Accelerator, tile.OriginAt(0, 0), "coarse cells retain accelerator provenance");
        c.False(LodSilhouetteAcceleratorSampling.NeedsSamples(tile), "a complete coarse tile is not sampled again");

        tile.SetCell(0, 0, 111, EnumSilhouetteOrigin.Preview);
        c.Eq((EnumSilhouetteOrigin.Preview, (ushort)111),
            (tile.OriginAt(0, 0), tile.TryGetHeight(0, 0, out ushort refined) ? refined : (ushort)0),
            "an engine preview replaces an accelerator answer at the same cell");
        c.False(tile.SetCell(0, 0, 130, EnumSilhouetteOrigin.Accelerator),
            "an accelerator never replaces the better preview");

        int[] ocean = Enumerable.Repeat(82, 64).ToArray();
        c.False(LodSilhouetteAcceleratorSampling.TryCoastPreview(TileX, TileZ, ocean, 82,
                out _, out _),
            "pure ocean does not spend a world-generator preview");
        int[] inland = Enumerable.Repeat(120, 64).ToArray();
        c.False(LodSilhouetteAcceleratorSampling.TryCoastPreview(TileX, TileZ, inland, 82,
                out _, out _),
            "confident inland terrain does not spend a preview");
        int[] coast = Enumerable.Repeat(82, 64).ToArray();
        coast[9] = 84;
        c.True(LodSilhouetteAcceleratorSampling.TryCoastPreview(TileX, TileZ, coast, 82,
                out int coastCx, out int coastCz)
                && coastCx >= TileX * 16 && coastCx < TileX * 16 + 16
                && coastCz >= TileZ * 16 && coastCz < TileZ * 16 + 16,
            "a coast or low dry lake candidate receives one in-tile engine preview");
    }

    /// <summary>
    /// The error brake counts only errors logged while previews wait on the generator, never the
    /// mod's own lines, and trips once at its threshold (Session 93: bodanboys logged 99 and 144
    /// such errors before its stalls; test2 none).
    /// </summary>
    static void WorldgenErrorBrake(Check c)
    {
        var brake = new LodWorldgenErrorBrake("[vintagehorizons]");
        brake.Observe(Vintagestory.API.Common.EnumLogType.Error, "Exception: {0}", "before any preview");
        c.Eq(0, brake.ErrorsDuringPreviews, "an error with no previews outstanding does not count");

        brake.SetPreviewsOutstanding(4);
        brake.Observe(Vintagestory.API.Common.EnumLogType.Warning, "a warning");
        brake.Observe(Vintagestory.API.Common.EnumLogType.Error, "[vintagehorizons] one of our own lines");
        c.Eq(0, brake.ErrorsDuringPreviews, "warnings and the mod's own errors do not count");

        brake.Observe(Vintagestory.API.Common.EnumLogType.Error, "Exception: {0}",
            "System.IndexOutOfRangeException: Index was outside the bounds of the array.\n   at TerraPrety.Rivers.CoastMap.GenLayer");
        brake.Observe(Vintagestory.API.Common.EnumLogType.Fatal, "a fatal line");
        c.False(brake.Tick(), "below the threshold the brake holds off");
        brake.Observe(Vintagestory.API.Common.EnumLogType.Error, "a third error");
        c.True(brake.Tick(), "the brake trips on the tick the threshold is reached");
        c.True(brake.Tripped && !brake.Tick(), "and stays tripped, reporting it once");
        c.True(brake.FirstError != null
                && brake.FirstError.StartsWith("Exception: System.IndexOutOfRangeException", StringComparison.Ordinal)
                && !brake.FirstError.Contains('\n'),
            "the first counted error is kept, formatted, on one line");
    }

    static void PreviewsStayFewPerRegion(Check c)
    {
        string builder = Source("src", "Silhouette", "LodSilhouetteBuilder.cs");
        c.Eq(4, LodSilhouetteBuilder.MaxPeeksInFlight,
            "at most four previews wait on the generator, so a stop leaves little behind");
        c.True(builder.Contains("if (!useAccelerator && TileHasHeights(tileKey)) continue;",
                StringComparison.Ordinal)
                && builder.Contains("!claimedTiles.Add(tileKey)", StringComparison.Ordinal),
            "the preview-only path keeps one preview per unexplored region and never duplicates claimed work");
        c.True(Regex.IsMatch(builder, @"void EnsureListening\(\)[\s\S]*?sapi\.Logger\.EntryAdded \+= logWatcher;"),
            "the error brake reads the server log only once someone asks for silhouettes");
        c.True(Regex.IsMatch(builder, @"void StopListening\(\)[\s\S]*?sapi\.Logger\.EntryAdded -= logWatcher;")
                && Regex.IsMatch(builder, @"public void Dispose\(\)[\s\S]*?sapi\.Logger\.EntryAdded -= logWatcher;"),
            "and stops reading it when nobody asks, and at shutdown");
        c.True(builder.Contains("bool previewsAvailable = !generatorWatch.Stalled && !errorBrake.Tripped;",
                StringComparison.Ordinal)
                && builder.Contains("while (previewsAvailable && peeks < allowance", StringComparison.Ordinal),
            "a tripped brake stops probes and previews while the independent accelerator may continue");
    }

    /// <summary>
    /// An older copy of a tile (the stored one) fills what a newer copy lacks and never
    /// overrides it from the same source; a snapshot is independent of later changes.
    /// </summary>
    static void TileMergeAndSnapshot(Check c)
    {
        int cx = TileX * 16, cz = TileZ * 16;
        var older = new LodSilhouetteTile(TileX, TileZ, revision: 40);
        older.SetChunk(cx, cz, HeightMap((_, _) => 70), EnumSilhouetteOrigin.Captured);
        older.SetChunk(cx + 1, cz, HeightMap((_, _) => 71), EnumSilhouetteOrigin.Preview);
        older.SetChunk(cx + 2, cz, HeightMap((_, _) => 72), EnumSilhouetteOrigin.Preview);

        var newer = new LodSilhouetteTile(TileX, TileZ);
        newer.SetChunk(cx, cz, HeightMap((_, _) => 90), EnumSilhouetteOrigin.Preview);
        newer.SetChunk(cx + 1, cz, HeightMap((_, _) => 91), EnumSilhouetteOrigin.Preview);
        newer.SetChunk(cx + 3, cz, HeightMap((_, _) => 93), EnumSilhouetteOrigin.Captured);

        c.True(newer.MergeFrom(older), "merging an older copy that knows more changes the tile");
        c.True(newer.TryGetHeight(0, 0, out ushort better) && better == 70
                && newer.OriginAt(0, 0) == EnumSilhouetteOrigin.Captured,
            "a cell the older copy knows from a better source takes it");
        c.True(newer.TryGetHeight(8, 0, out ushort same) && same == 91,
            "where both know a cell from the same source, the newer height stands");
        c.True(newer.TryGetHeight(16, 0, out ushort onlyOlder) && onlyOlder == 72,
            "a cell only the older copy knows is taken");
        c.True(newer.TryGetHeight(24, 0, out ushort onlyNewer) && onlyNewer == 93,
            "a cell only the newer copy knows is kept");
        c.Eq(4 * 64, newer.KnownCells, "merged cells are counted once");
        c.Eq(older.Revision + 1, newer.Revision, "the revision passes the older copy's, so it never goes back");
        c.False(newer.MergeFrom(older), "merging the same copy again changes nothing");
        c.Eq(older.Revision + 1, newer.Revision, "no change, no new revision");
        c.Throws<ArgumentException>(() => newer.MergeFrom(new LodSilhouetteTile(TileX + 1, TileZ)),
            "only copies of the same tile merge");

        LodSilhouetteTile copy = newer.Snapshot();
        newer.SetChunk(cx + 5, cz, HeightMap((_, _) => 95), EnumSilhouetteOrigin.Preview);
        c.False(copy.TryGetHeight(40, 0, out _), "a snapshot does not see later changes");
        c.Eq(4 * 64, copy.KnownCells, "a snapshot keeps the count it was taken with");
        c.Eq(older.Revision + 1, copy.Revision, "and the revision");
    }

    static bool WaitForIndex(LodSilhouetteStore store, out HashSet<long> keys)
    {
        long deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            if (store.TryTakeIndex(out keys)) return true;
            Thread.Sleep(2);
        }
        keys = new HashSet<long>();
        return false;
    }

    static LodSilhouetteStoredTile? WaitForLoad(LodSilhouetteStore store)
    {
        long deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            if (store.TryTakeLoaded(out LodSilhouetteStoredTile result)) return result;
            Thread.Sleep(2);
        }
        return null;
    }

    static void RunSql(string path, string sql)
    {
        var options = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(options.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The tile store keeps tiles across sessions. A tile saved before a close is listed and
    /// read back after reopening; a later save merges into the stored row rather than replacing
    /// it; a row that does not decode reads as nothing; a store of another format is emptied.
    /// </summary>
    static void StoreKeepsTilesAcrossSessions(Check c)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-silhouette-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "world-silhouette-server.db");
        int cx = TileX * 16, cz = TileZ * 16;
        var logger = new CaptureLogger();
        try
        {
            using (var store = new LodSilhouetteStore(path, logger))
            {
                c.True(WaitForIndex(store, out HashSet<long> keys) && keys.Count == 0, "a new store lists no tiles");
                var first = new LodSilhouetteTile(TileX, TileZ);
                first.SetChunk(cx, cz, HeightMap((_, _) => 80), EnumSilhouetteOrigin.Preview);
                c.True(store.Save(first.Snapshot()), "a save is accepted");
            }

            using (var store = new LodSilhouetteStore(path, logger))
            {
                c.True(WaitForIndex(store, out HashSet<long> keys)
                        && keys.SetEquals(new[] { LodSilhouetteTile.KeyOf(TileX, TileZ) }),
                    "a reopened store lists the tile saved before it closed");

                // Rebuilt this session without reading the stored copy first: the write must merge.
                var second = new LodSilhouetteTile(TileX, TileZ);
                second.SetChunk(cx + 1, cz, HeightMap((_, _) => 81), EnumSilhouetteOrigin.Captured);
                store.Save(second.Snapshot());
                long deadline = Environment.TickCount64 + 5000;
                while (store.Saved < 1 && Environment.TickCount64 < deadline) Thread.Sleep(2);

                c.True(store.TryRequestLoad(TileX, TileZ), "a load is accepted");
                LodSilhouetteTile? merged = WaitForLoad(store)?.Tile;
                c.True(merged != null && merged.TryGetHeight(0, 0, out ushort kept) && kept == 80
                        && merged.TryGetHeight(8, 0, out ushort added) && added == 81,
                    "a later save merges into the stored row instead of replacing it");

                c.True(store.TryRequestLoad(TileX + 5, TileZ), "a load of a tile never stored is accepted");
                LodSilhouetteStoredTile? none = WaitForLoad(store);
                c.True(none != null && none.Value.Tile == null, "a tile the store never held reads as nothing");
            }

            RunSql(path, "INSERT OR REPLACE INTO Tile (TX, TZ, Revision, Data, ModifiedMs) VALUES (1, 2, 1, x'0102', 0);");
            using (var store = new LodSilhouetteStore(path, logger))
            {
                WaitForIndex(store, out _);
                store.TryRequestLoad(1, 2);
                LodSilhouetteStoredTile? broken = WaitForLoad(store);
                c.True(broken != null && broken.Value.Tile == null && store.Undecodable == 1,
                    "a row that does not decode reads as nothing, and is counted");
            }

            RunSql(path, "UPDATE Meta SET Value = '0' WHERE Key = 'SchemaVersion';");
            using (var store = new LodSilhouetteStore(path, logger))
            {
                c.True(WaitForIndex(store, out HashSet<long> keys) && keys.Count == 0,
                    "a store written in another format is emptied when opened");
                c.False(store.OpenFailed, "and still opens");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// P5's client cache is lazy, supplies a drawable tile before a network answer, and yields to
    /// the exact server reply even if the server rebuilt its store and restarted at revision one.
    /// Closing the switch flushes and closes the cache; merely configuring an off client creates
    /// no file.
    /// </summary>
    static void ClientTilesPersistAcrossRejoin(Check c)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-silhouette-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string coldPath = Path.Combine(dir, "off-silhouette.db");
        string path = Path.Combine(dir, "world-silhouette.db");
        int cx = TileX * 16, cz = TileZ * 16;
        long key = LodSilhouetteTile.KeyOf(TileX, TileZ);
        int earlyTileX = TileX + 1, earlyCx = earlyTileX * 16;
        long earlyKey = LodSilhouetteTile.KeyOf(earlyTileX, TileZ);
        var logger = new CaptureLogger();
        try
        {
            var oldTile = new LodSilhouetteTile(TileX, TileZ);
            oldTile.SetChunk(cx, cz, HeightMap((_, _) => 140), EnumSilhouetteOrigin.Preview);
            oldTile = LodSilhouetteTile.Decode(TileX, TileZ, 7, oldTile.Encode())!;
            using (var seed = new LodSilhouetteStore(path, logger, mergeStoredRows: false))
            {
                WaitForIndex(seed, out _);
                c.True(seed.Save(oldTile.Snapshot()), "the earlier client's tile is queued for persistence");
            }

            bool available = true;
            long session = 0;
            var requested = new List<long>();
            using (var client = new LodSilhouetteClient(logger, () => available,
                (on, _, id) =>
                {
                    if (on) session = id;
                    return true;
                },
                (keys, _) =>
                {
                    requested.AddRange(keys);
                    return true;
                }))
            {
                client.SetStorePath(coldPath);
                client.Pump(new LodWorld(), TileX * LodSilhouetteTile.TileBlocks,
                    TileZ * LodSilhouetteTile.TileBlocks, 110);
                c.False(File.Exists(coldPath),
                    "an off client does not create or open its configured silhouette store");

                available = false;
                client.SetEnabled(true);
                client.Pump(new LodWorld(), TileX * LodSilhouetteTile.TileBlocks,
                    TileZ * LodSilhouetteTile.TileBlocks, 110);
                c.False(client.StoreOpen || File.Exists(coldPath),
                    "an enabled client does not open a store when the server does not advertise silhouettes");
                client.SetEnabled(false);

                client.SetStorePath(null);
                available = true;
                client.SetEnabled(true);
                var world = new LodWorld();
                client.Pump(world, TileX * LodSilhouetteTile.TileBlocks,
                    TileZ * LodSilhouetteTile.TileBlocks, 110);

                var earlyTile = new LodSilhouetteTile(earlyTileX, TileZ);
                earlyTile.SetChunk(earlyCx, cz, HeightMap((_, _) => 95), EnumSilhouetteOrigin.Captured);
                client.OnTileArrived(new AssistSilhouetteTile
                {
                    TileX = earlyTileX,
                    TileZ = TileZ,
                    Key = earlyKey,
                    Revision = 3,
                    Payload = earlyTile.Encode(),
                    SessionId = 0,
                });
                long deadline = Environment.TickCount64 + 5000;
                while (client.TilesReceived == 0 && Environment.TickCount64 < deadline)
                {
                    client.Pump(world, TileX * LodSilhouetteTile.TileBlocks,
                        TileZ * LodSilhouetteTile.TileBlocks, 110);
                    Thread.Sleep(2);
                }
                c.Eq(1, client.TileCount,
                    "a server tile may arrive before LevelFinalize supplies the client store path");

                client.SetStorePath(path);
                deadline = Environment.TickCount64 + 5000;
                while (client.TileCount < 2 && Environment.TickCount64 < deadline)
                {
                    client.Pump(world, TileX * LodSilhouetteTile.TileBlocks,
                        TileZ * LodSilhouetteTile.TileBlocks, 110);
                    Thread.Sleep(2);
                }
                c.Eq(2, client.TileCount, "a persisted client tile is held before the server sends it again");
                c.Eq(1, client.TilesLoadedFromStore, "the instant-rejoin tile is identified as a local read");
                c.True(client.StoreOpen, "the enabled, advertised feature owns an open client store");

                client.OnManifestArrived(new AssistSilhouetteManifest
                {
                    Sequence = 0,
                    Last = true,
                    SessionId = session,
                    Keys = new[] { key },
                    Revisions = new[] { 1 },
                });
                deadline = Environment.TickCount64 + 5000;
                while (!requested.Contains(key) && Environment.TickCount64 < deadline)
                {
                    client.Pump(world, TileX * LodSilhouetteTile.TileBlocks,
                        TileZ * LodSilhouetteTile.TileBlocks, 110);
                    Thread.Sleep(2);
                }
                c.True(requested.Contains(key),
                    "a higher persisted revision does not hide a server whose revision sequence restarted");

                var current = new LodSilhouetteTile(TileX, TileZ);
                current.SetChunk(cx, cz, HeightMap((_, _) => 80), EnumSilhouetteOrigin.Captured);
                client.OnTileArrived(new AssistSilhouetteTile
                {
                    TileX = TileX,
                    TileZ = TileZ,
                    Key = key,
                    Revision = 1,
                    Payload = current.Encode(),
                    SessionId = session,
                });
                int receivedBeforeReplacement = client.TilesReceived;
                deadline = Environment.TickCount64 + 5000;
                while (client.TilesReceived == receivedBeforeReplacement && Environment.TickCount64 < deadline)
                {
                    client.Pump(world, TileX * LodSilhouetteTile.TileBlocks,
                        TileZ * LodSilhouetteTile.TileBlocks, 110);
                    Thread.Sleep(2);
                }
                c.Eq(receivedBeforeReplacement + 1, client.TilesReceived,
                    "the current server answer replaces the persisted copy");

                client.SetEnabled(false);
                c.False(client.StoreOpen, "switching silhouettes off closes the client store");
                c.Eq(0, client.TileCount, "switching silhouettes off drops persisted tiles from RAM too");
            }

            using (var reopened = new LodSilhouetteStore(path, logger, mergeStoredRows: false))
            {
                c.True(WaitForIndex(reopened, out HashSet<long> keys) && keys.Contains(key),
                    "the server replacement remains indexed after the client closes");
                c.True(reopened.TryRequestLoad(TileX, TileZ), "the replaced client tile can be read back");
                LodSilhouetteTile? stored = WaitForLoad(reopened)?.Tile;
                c.True(stored != null && stored.Revision == 1
                        && stored.TryGetHeight(0, 0, out ushort height) && height == 80,
                    "client persistence keeps the server's exact lower-revision answer, not a merge");
                c.True(reopened.TryRequestLoad(earlyTileX, TileZ),
                    "a tile received before the store path existed can be read back");
                LodSilhouetteTile? earlyStored = WaitForLoad(reopened)?.Tile;
                c.True(earlyStored != null && earlyStored.Revision == 3
                        && earlyStored.TryGetHeight(0, 0, out ushort earlyHeight) && earlyHeight == 95,
                    "opening the client store backfills authoritative tiles already held in memory");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    static void StoredTilesAreReadBeforePreviewing(Check c)
    {
        string builder = Source("src", "Silhouette", "LodSilhouetteBuilder.cs");
        string assist = Source("src", "Net", "LodAssistServerSystem.cs");
        c.True(Regex.IsMatch(builder,
                @"if \(!TileReady\(tileKey\)\)[\s\S]*?return EnumProbeStep\.Paused;[\s\S]*?Probe\(cx, cz\);"),
            "a tile's stored copy is read back before anything in it is previewed");
        c.True(builder.Contains("if (store != null && storedBeforeSession == null) return;", StringComparison.Ordinal),
            "nothing is previewed until the store has said which tiles it holds");
        c.True(assist.Contains("GameWorldSave += OnSilhouettesWorldSave", StringComparison.Ordinal)
                && assist.Contains("void OnSilhouettesWorldSave() => silhouettes?.SaveDirty();", StringComparison.Ordinal),
            "changed tiles are written at every world save");
        c.True(Regex.IsMatch(builder, @"public void Dispose\(\)[\s\S]*?SaveDirty\(\);\s*store\?\.Dispose\(\);"),
            "shutdown saves what changed before it closes the store");
        c.True(Regex.IsMatch(assist, @"storePath != null && File\.Exists\(storePath\)\s*\?\s*OpenSilhouetteStore\(storePath\)")
                && Regex.IsMatch(builder, @"if \(store == null && openStore != null\)\s*\{[^}]*store = openStore\(\);"),
            "a world that has never used silhouettes gets no tile store file until a player asks for them");
    }

    /// <summary>
    /// The 0.4.77 hang: the world-generation thread stopped answering previews and never came
    /// back. The watch must declare that once, after 20 s, and never charge the generator for
    /// silence it did not owe: nothing outstanding, or the builder itself not ticking.
    /// </summary>
    static void GeneratorStallWatch(Check c)
    {
        long t;
        var idle = new LodGenerationStallWatch();
        for (t = 0; t < 60_000; t += 50) idle.Tick(t, workOutstanding: false);
        c.False(idle.Stalled, "silence with nothing asked of the generator is not a stall");

        var answering = new LodGenerationStallWatch();
        for (t = 0; t < 120_000; t += 50)
        {
            if (t % 15_000 == 0) answering.Answered(t);
            answering.Tick(t, workOutstanding: true);
        }
        c.False(answering.Stalled, "a generator that answers every 15 s is never declared stuck");

        var silent = new LodGenerationStallWatch();
        int declared = 0;
        for (t = 0; t <= 60_000; t += 50)
            if (silent.Tick(t, workOutstanding: true)) declared++;
        c.Eq(1, declared, "a stall is declared exactly once");
        c.Eq(LodGenerationStallWatch.DefaultSilenceMs, silent.StalledAtMs,
            "after 20 s of silence with work outstanding, and not before");
        c.Eq(0L, silent.SilentSinceMs, "the silence counts from when the generator last owed nothing");
        silent.Answered(t);
        c.Eq(1, silent.LateAnswers, "an answer after the declaration counts as late");
        c.True(silent.Stalled, "a late answer does not lift the stall: previews stay off for the session");

        var gap = new LodGenerationStallWatch();
        for (t = 0; t <= 15_000; t += 50) gap.Tick(t, workOutstanding: true);
        t += 6_000; // a save or a hitch: the builder itself did not run
        for (; t <= 40_000; t += 50) gap.Tick(t, workOutstanding: true);
        c.False(gap.Stalled, "a gap between builder ticks restarts the count instead of charging the generator");
        for (; t <= 42_000; t += 50) gap.Tick(t, workOutstanding: true);
        c.True(gap.Stalled, "silence after the gap still counts");
    }

    static void GeneratorSilenceStopsTheBuilder(Check c)
    {
        string builder = Source("src", "Silhouette", "LodSilhouetteBuilder.cs");
        c.Eq(2, Regex.Matches(builder, @"\bGeneratorAnswered\(\);").Count,
            "both the probe callback and the preview landing tell the watch the generator answered");
        c.True(builder.Contains("bool previewsAvailable = !generatorWatch.Stalled && !errorBrake.Tripped;",
                StringComparison.Ordinal)
                && builder.Contains("ProbeNext(demand, previewsAvailable)", StringComparison.Ordinal),
            "a stuck generator stops probes and previews while the independent accelerator may continue");
        c.True(Regex.IsMatch(builder, @"void Tick\(\)\s*\{[^}]*WatchGenerator\(now\);"),
            "the builder watches the generator on every tick");
    }

    static ushort[] HeightMap(Func<int, int, int> height)
    {
        var map = new ushort[LodSilhouetteTile.HeightMapLength];
        for (int z = 0; z < LodSilhouetteTile.ChunkBlocks; z++)
            for (int x = 0; x < LodSilhouetteTile.ChunkBlocks; x++)
                map[z * LodSilhouetteTile.ChunkBlocks + x] = (ushort)height(x, z);
        return map;
    }

    static void TileCellMapping(Check c)
    {
        var tile = new LodSilhouetteTile(TileX, TileZ);
        int cx = TileX * 16 + 5, cz = TileZ * 16 + 12;
        ushort[] map = HeightMap((x, z) => 1000 + x + 32 * z);
        c.True(tile.SetChunk(cx, cz, map, EnumSilhouetteOrigin.Preview), "a new chunk changes the tile");
        c.Eq(64, tile.KnownCells, "one chunk fills its 8x8 cells");
        c.Eq(1, tile.Revision, "the first change is revision 1");

        bool allMatch = true;
        for (int j = 0; j < 8; j++)
        {
            for (int i = 0; i < 8; i++)
            {
                bool known = tile.TryGetHeight(5 * 8 + i, 12 * 8 + j, out ushort h);
                allMatch &= known && h == map[j * 4 * 32 + i * 4]
                    && tile.OriginAt(5 * 8 + i, 12 * 8 + j) == EnumSilhouetteOrigin.Preview;
            }
        }
        c.True(allMatch, "each cell takes the height at its minimum corner (Farseer's sampling)");
        c.False(tile.TryGetHeight(6 * 8, 12 * 8, out _), "the neighbouring chunk's cells stay unknown");
        c.Eq(LodWorld.SectionKey(3, TileX, TileZ), tile.Key, "a tile is keyed by its L3 section key");
        c.Eq((TileX, TileZ), LodSilhouetteTile.TileOfChunk(cx, cz), "a chunk maps to the tile that holds it");
        c.Throws<ArgumentOutOfRangeException>(
            () => tile.SetChunk(TileX * 16 + 16, cz, map, EnumSilhouetteOrigin.Preview),
            "a chunk outside the tile is refused");

        double expected = 0;
        for (int k = 0; k < 64; k++) expected += map[k / 8 * 4 * 32 + k % 8 * 4];
        expected /= 64;
        c.True(tile.TryChunkMean(5, 12, out double mean) && Math.Abs(mean - expected) < 1e-9,
            "a chunk mean is the mean of its known cells");
        c.False(tile.TryChunkMean(4, 12, out _), "a chunk with no data has no mean");
    }

    static void TileSourceOrder(Check c)
    {
        int cx = TileX * 16, cz = TileZ * 16;
        ushort[] low = HeightMap((_, _) => 70), mid = HeightMap((_, _) => 80), high = HeightMap((_, _) => 90);
        var tile = new LodSilhouetteTile(TileX, TileZ);
        tile.SetChunk(cx, cz, low, EnumSilhouetteOrigin.Accelerator);
        c.True(tile.SetChunk(cx, cz, high, EnumSilhouetteOrigin.Preview),
            "a preview replaces an accelerator sample");
        c.Eq(EnumSilhouetteOrigin.Preview, tile.OriginAt(0, 0), "the cell now holds the preview");
        int revision = tile.Revision;
        c.False(tile.SetChunk(cx, cz, low, EnumSilhouetteOrigin.Accelerator),
            "an accelerator sample never replaces a preview");
        c.False(tile.SetChunk(cx, cz, high, EnumSilhouetteOrigin.Preview),
            "the same preview again changes nothing");
        c.Eq(revision, tile.Revision, "no change, no new revision");
        c.True(tile.SetChunk(cx, cz, mid, EnumSilhouetteOrigin.Captured), "captured terrain replaces a preview");
        c.False(tile.SetChunk(cx, cz, high, EnumSilhouetteOrigin.Preview),
            "a preview never replaces captured terrain");
        c.True(tile.TryGetHeight(0, 0, out ushort h) && h == 80, "captured heights are kept");
        c.Eq(64, tile.KnownCells, "replacing cells does not count them twice");
    }

    static void TileRoundTrip(Check c)
    {
        var tile = new LodSilhouetteTile(TileX, TileZ);
        tile.SetChunk(TileX * 16 + 4, TileZ * 16 + 4,
            HeightMap((x, z) => 60 + (x * 7 + z * 3) % 200), EnumSilhouetteOrigin.Preview);
        tile.SetChunk(TileX * 16 + 15, TileZ * 16,
            HeightMap((x, z) => 300 + x - z), EnumSilhouetteOrigin.Captured);
        byte[] payload = tile.Encode();
        LodSilhouetteTile? back = LodSilhouetteTile.Decode(TileX, TileZ, 7, payload);
        c.True(back != null, "an encoded tile decodes");
        if (back == null) return;
        c.Eq(tile.KnownCells, back.KnownCells, "known cells survive the round trip");
        c.Eq(7, back.Revision, "the decoded revision is the message's");

        bool same = true;
        for (int z = 0; z < LodSilhouetteTile.CellsPerEdge; z++)
        {
            for (int x = 0; x < LodSilhouetteTile.CellsPerEdge; x++)
            {
                bool a = tile.TryGetHeight(x, z, out ushort ha), b = back.TryGetHeight(x, z, out ushort hb);
                same &= a == b && ha == hb && tile.OriginAt(x, z) == back.OriginAt(x, z);
            }
        }
        c.True(same, "every cell's origin and height survive the round trip");

        LodSilhouetteTile? empty = LodSilhouetteTile.Decode(1, 2, 0, new LodSilhouetteTile(1, 2).Encode());
        c.True(empty != null && empty.KnownCells == 0, "an empty tile round-trips");
        c.True(payload.Length < 2048, $"a sparse tile stays small on the wire ({payload.Length} bytes)");
    }

    static byte[] Deflated(byte format, byte[] body)
    {
        using var output = new MemoryStream();
        output.WriteByte(format);
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(body, 0, body.Length);
        return output.ToArray();
    }

    static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    static byte[] StatesWithKnown(int known)
    {
        var states = new byte[LodSilhouetteTile.CellCount / 4];
        for (int cell = 0; cell < known; cell++)
            states[cell >> 2] |= (byte)(2 << ((cell & 3) * 2));
        return states;
    }

    static void TileRejectsBadPayloads(Check c)
    {
        var tile = new LodSilhouetteTile(TileX, TileZ);
        tile.SetChunk(TileX * 16, TileZ * 16, HeightMap((_, _) => 100), EnumSilhouetteOrigin.Preview);
        byte[] good = tile.Encode();
        byte v = LodSilhouetteTile.FormatVersion;

        var cases = new List<(string Name, byte[]? Payload)>
        {
            ("null", null),
            ("empty", Array.Empty<byte>()),
            ("format byte only", new[] { v }),
            ("unknown format", Concat(new byte[] { 2 }, good[1..])),
            ("truncated", good[..(good.Length / 2)]),
            ("not deflate", new byte[] { v, 1, 2, 3, 4, 5, 6, 7, 8 }),
            ("states only, heights missing", Deflated(v, StatesWithKnown(3))),
            ("one height short", Deflated(v, Concat(StatesWithKnown(3), new byte[] { 1, 0, 2, 0 }))),
            ("trailing byte", Deflated(v, Concat(StatesWithKnown(1), new byte[] { 1, 0, 9 }))),
            ("oversized body", Deflated(v,
                new byte[LodSilhouetteTile.CellCount / 4 + 2 * LodSilhouetteTile.CellCount + 64])),
        };
        foreach ((string name, byte[]? payload) in cases)
        {
            LodSilhouetteTile? decoded = null;
            c.NoThrow(() => decoded = LodSilhouetteTile.Decode(TileX, TileZ, 1, payload),
                "decoding " + name + " does not throw");
            c.True(decoded == null, "decoding " + name + " is refused");
        }
        c.True(LodSilhouetteTile.Decode(TileX, TileZ, 1,
                Deflated(v, Concat(StatesWithKnown(1), new byte[] { 1, 0 }))) is { KnownCells: 1 },
            "a hand-built one-cell tile is accepted, so the refusals above are not refusing everything");
    }

    /// <summary>A tile's 3x3 neighbourhood as the fill and the mesher read it, indexed [z, x].</summary>
    static LodSilhouetteTile?[,] Neighbourhood(Func<int, int, LodSilhouetteTile?> tileAt, int centreX, int centreZ)
    {
        var hood = new LodSilhouetteTile?[LodSilhouetteFill.NeighbourhoodTiles, LodSilhouetteFill.NeighbourhoodTiles];
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                hood[dz + 1, dx + 1] = tileAt(centreX + dx, centreZ + dz);
        return hood;
    }

    static LodSilhouetteTile FullTile(int tileX, int tileZ, int height)
    {
        var tile = new LodSilhouetteTile(tileX, tileZ);
        ushort[] map = HeightMap((_, _) => height);
        for (int cz = 0; cz < LodSilhouetteTile.ChunksPerEdge; cz++)
            for (int cx = 0; cx < LodSilhouetteTile.ChunksPerEdge; cx++)
                tile.SetChunk(tileX * 16 + cx, tileZ * 16 + cz, map, EnumSilhouetteOrigin.Captured);
        return tile;
    }

    /// <summary>
    /// A deterministic scatter over the 4x4 tiles around the fixture tile: about one chunk in
    /// eight known, and one tile missing entirely.
    /// </summary>
    static Dictionary<(int, int), LodSilhouetteTile> SparseWorld()
    {
        var world = new Dictionary<(int, int), LodSilhouetteTile>();
        for (int tz = TileZ - 1; tz <= TileZ + 2; tz++)
        {
            for (int tx = TileX - 1; tx <= TileX + 2; tx++)
            {
                if ((tx * 7 + tz * 3) % 5 == 1) continue;
                var tile = new LodSilhouetteTile(tx, tz);
                for (int cz = 0; cz < LodSilhouetteTile.ChunksPerEdge; cz++)
                {
                    for (int cx = 0; cx < LodSilhouetteTile.ChunksPerEdge; cx++)
                    {
                        int hash = tx * 73856093 ^ tz * 19349663 ^ cx * 83492791 ^ cz * 2654435;
                        if ((hash & 7) != 0) continue;
                        int ground = 100 + (hash >> 3 & 63);
                        tile.SetChunk(tx * 16 + cx, tz * 16 + cz, HeightMap((x, z) => ground + (x ^ z) % 5),
                            EnumSilhouetteOrigin.Preview);
                    }
                }
                world[(tx, tz)] = tile;
            }
        }
        return world;
    }

    static void FillFromKnownCells(Check c)
    {
        int stride = LodSilhouetteTile.CellsPerEdge + 1;
        c.Eq(LodSilhouetteTile.CellsPerEdge, 1 << (LodSilhouetteFill.Levels - 1),
            "the fill's top level holds one cell per tile");

        var one = new LodSilhouetteTile(TileX, TileZ);
        one.SetChunk(TileX * 16 + 4, TileZ * 16 + 4, HeightMap((x, z) => 100 + x + z), EnumSilhouetteOrigin.Preview);
        LodSilhouetteTile?[,] alone = Neighbourhood((x, z) => x == TileX && z == TileZ ? one : null, TileX, TileZ);
        float[] filled = LodSilhouetteFill.FillCentre(alone);
        bool kept = true;
        for (int j = 0; j < 8; j++)
        {
            for (int i = 0; i < 8; i++)
            {
                one.TryGetHeight(32 + i, 32 + j, out ushort h);
                kept &= filled[(32 + j) * stride + 32 + i] == h;
            }
        }
        c.True(kept, "known cells keep their own heights exactly");
        c.True(filled.All(h => !float.IsNaN(h)), "one known chunk fills its whole tile and the closing edges");
        c.True(filled.All(h => h >= 100 && h <= 100 + 28 + 28), "filled heights stay within the known ones");
        c.SeqEq(filled, LodSilhouetteFill.FillCentre(alone), "the same data fills the same way twice");
        c.True(LodSilhouetteFill.FillCentre(new LodSilhouetteTile?[3, 3]).All(float.IsNaN),
            "with nothing known, nothing is filled");

        var west = new LodSilhouetteTile(TileX - 1, TileZ);
        west.SetChunk((TileX - 1) * 16 + 15, TileZ * 16 + 8, HeightMap((_, _) => 150), EnumSilhouetteOrigin.Captured);
        float[] beside = LodSilhouetteFill.FillCentre(
            Neighbourhood((x, z) => x == TileX - 1 && z == TileZ ? west : null, TileX, TileZ));
        c.Near(150, beside[64 * stride], 1e-3, "an empty tile takes a known neighbour's heights at their border");
        c.True(float.IsNaN(beside[64 * stride + LodSilhouetteTile.CellsPerEdge]),
            "and they stop before the tile beyond, rather than spreading without limit");

        c.True(LodSilhouetteField.IsLatticeChunk(LodSilhouetteField.LatticeChunkOfNode(625),
                LodSilhouetteField.LatticeChunkOfNode(2000))
            && !LodSilhouetteField.IsLatticeChunk(16000, 16004), "lattice chunks sit at 4 mod 8");
        c.Eq(7, LodSilhouetteField.NodeOfLatticeChunk(LodSilhouetteField.LatticeChunkOfNode(7)),
            "a node and its lattice chunk convert both ways");
        c.Eq(9, LodSilhouetteField.TilesReading(TileX, TileZ).Distinct().Count(),
            "a tile's heights reach its own mesh and its eight neighbours'");
    }

    static void MeshMatchesAcrossTileBorders(Check c)
    {
        const int sea = 110;
        Dictionary<(int, int), LodSilhouetteTile> world = SparseWorld();
        LodSilhouetteTile? At(int x, int z) => world.GetValueOrDefault((x, z));
        LodSilhouetteMeshArrays? left = LodSilhouetteField.BuildMesh(Neighbourhood(At, TileX, TileZ), 0, sea);
        LodSilhouetteMeshArrays? right = LodSilhouetteField.BuildMesh(Neighbourhood(At, TileX + 1, TileZ), 0, sea);
        LodSilhouetteMeshArrays? below = LodSilhouetteField.BuildMesh(Neighbourhood(At, TileX, TileZ + 1), 0, sea);
        c.True(left != null && right != null && below != null, "tiles with known cells around them mesh");
        if (left == null || right == null || below == null) return;

        int verts = LodSilhouetteField.DefaultQuadsPerEdge + 1;
        bool eastWest = true, northSouth = true;
        for (int k = 0; k < verts; k++)
        {
            eastWest &= left.Xyz[(k * verts + verts - 1) * 3 + 1] == right.Xyz[k * verts * 3 + 1];
            northSouth &= left.Xyz[((verts - 1) * verts + k) * 3 + 1] == below.Xyz[k * 3 + 1];
        }
        c.True(eastWest, "neighbouring tiles agree on every vertex of their shared east-west border");
        c.True(northSouth, "neighbouring tiles agree on every vertex of their shared north-south border");

        float[] filled = LodSilhouetteFill.FillCentreFeathered(
            Neighbourhood(At, TileX, TileZ),
            LodSilhouetteField.FeatherRadiusCells, LodSilhouetteField.FeatherStrength);
        int stride = LodSilhouetteTile.CellsPerEdge + 1;
        int step = LodSilhouetteTile.CellsPerEdge / LodSilhouetteField.DefaultQuadsPerEdge;
        bool direct = true;
        for (int b = 1; b < verts - 1; b++)
        {
            for (int a = 1; a < verts - 1; a++)
            {
                float h = filled[b * step * stride + a * step];
                float expected = float.IsNaN(h) ? sea : Math.Max(h, sea);
                direct &= left.Xyz[(b * verts + a) * 3 + 1] == expected;
            }
        }
        c.True(direct, "an interior mesh vertex takes the gently feathered height of its cell, clamped to sea level");
    }

    static void FeatherSoftensPeaks(Check c)
    {
        var world = new Dictionary<(int, int), LodSilhouetteTile>();
        for (int tz = TileZ - 1; tz <= TileZ + 1; tz++)
            for (int tx = TileX - 1; tx <= TileX + 1; tx++)
                world[(tx, tz)] = FullTile(tx, tz, 100);
        world[(TileX, TileZ)].SetCell(64, 64, 220, EnumSilhouetteOrigin.Captured);
        LodSilhouetteTile?[,] neighbourhood = Neighbourhood(
            (x, z) => world.GetValueOrDefault((x, z)), TileX, TileZ);
        float[] raw = LodSilhouetteFill.FillCentre(neighbourhood);
        float[] feathered = LodSilhouetteFill.FillCentreFeathered(neighbourhood,
            LodSilhouetteField.FeatherRadiusCells, LodSilhouetteField.FeatherStrength);
        int stride = LodSilhouetteTile.CellsPerEdge + 1;
        int peak = 64 * stride + 64;
        c.True(feathered[peak] < raw[peak] && feathered[peak] > 100,
            "render-only feathering rounds an isolated hard peak without flattening it");
        c.True(feathered[peak + 1] > raw[peak + 1],
            "the removed peak height feathers into its immediate skyline instead of disappearing");
    }

    static void CapturedHeightsReachTiles(Check c)
    {
        string pipeline = Source("src", "Lod", "LodPipeline.cs");
        string capture = Source("src", "Net", "LodServerCaptureSystem.cs");
        string assist = Source("src", "Net", "LodAssistServerSystem.cs");
        string builder = Source("src", "Silhouette", "LodSilhouetteBuilder.cs");
        c.True(Regex.IsMatch(pipeline,
                @"public bool CaptureColumn\([\s\S]*?PreviewHeightsCaptured[\s\S]*?WorldGenTerrainHeightMap[\s\S]*?return true;"),
            "a previewed column's worldgen heights are offered as its capture job is created");
        c.True(Regex.IsMatch(capture,
                @"if \(Config\.SilhouettesEnabled\(sapi\.Server\.IsDedicated\)\)\s*\{[^}]*PreviewHeightsCaptured ="),
            "the pipeline's height hook is attached only while the server offers silhouettes");
        c.True(Regex.IsMatch(assist,
                @"if \(!config\.SilhouetteHeightsKept\(sapi\.Server\.IsDedicated, measuring\)\)[\s\S]*?return;[\s\S]*?ChunkColumnLoaded \+= OnColumnLoadedForSilhouettes"),
            "loaded columns' heights are read only while the server keeps silhouette heights");
        c.True(assist.Contains("silhouettesOffered = config.SilhouettesEnabled(sapi.Server.IsDedicated);", StringComparison.Ordinal)
                && assist.Contains("(silhouettesOffered ? LodAssist.FeatureSilhouetteTiles : 0)", StringComparison.Ordinal)
                && Regex.IsMatch(assist, @"if \(silhouettes == null \|\| !silhouettesOffered \|\|"),
            "a server that only measures silhouette heights neither advertises silhouettes nor answers a request");
        c.Eq(1, Regex.Matches(assist, @"GetEnvironmentVariable\(LodServerConfig\.MeasureSilhouettesVariable\)").Count,
            "the measuring switch is read in one place");
        string script = File.ReadAllText(Path.Combine(GameAssemblies.RepoRoot, "scripts", "silhouette-compare.ps1"));
        c.True(script.Contains($"$psi.Environment['{LodServerConfig.MeasureSilhouettesVariable}'] = '1'", StringComparison.Ordinal),
            "the comparison script starts its test server with the measuring switch the server reads");
        c.Eq(1, Regex.Matches(assist, @"ChunkColumnLoaded \+=").Count,
            "the silhouette side subscribes to column loads in one place");
        c.True(builder.Contains("SetChunk(cx, cz, heights, EnumSilhouetteOrigin.Captured)", StringComparison.Ordinal),
            "captured heights enter tiles as captured, the best source");
        c.True(builder.Contains("TileHasHeights(tileKey)", StringComparison.Ordinal),
            "the builder never previews in a tile whose heights it holds");
    }

    static void MeshSeaAndCoverage(Check c)
    {
        const int sea = 110;
        LodSilhouetteTile?[,] deep = Neighbourhood((x, z) => FullTile(x, z, sea - 20), TileX, TileZ);
        LodSilhouetteMeshArrays? flat = LodSilhouetteField.BuildMesh(deep, 0, sea);
        c.True(flat != null && Enumerable.Range(0, flat.VertexCount).All(v => flat.Xyz[v * 3 + 1] == sea),
            "ground below sea level draws as a flat sea");
        c.True(LodSilhouetteField.BuildMesh(deep, ulong.MaxValue, sea) == null,
            "a tile cached terrain fully covers has no mesh");
        c.True(LodSilhouetteField.BuildMesh(new LodSilhouetteTile?[3, 3], 0, sea) == null,
            "a tile with nothing known around it has no mesh");

        ulong oneCell = 1UL << LodSilhouetteField.CoverageBit(0, 0);
        LodSilhouetteMeshArrays? cut = LodSilhouetteField.BuildMesh(deep, oneCell, sea);
        int quads = LodSilhouetteField.DefaultQuadsPerEdge * LodSilhouetteField.DefaultQuadsPerEdge;
        int quadsPerCellEdge = LodSection.SectionBlocks
            / (LodSilhouetteTile.TileBlocks / LodSilhouetteField.DefaultQuadsPerEdge);
        c.Eq(quads * 6, cut?.IndexCount ?? -1,
            "partial coverage keeps a complete mesh for exact per-fragment ownership");
        c.True(LodSilhouetteField.Covered(oneCell, 0, 0)
                && !LodSilhouetteField.Covered(oneCell, 1, 0)
                && !LodSilhouetteField.Covered(oneCell, 0, 1),
            "the coverage predicate selects exactly its own 64-block cell");
        if (cut == null) return;
        int verts = LodSilhouetteField.DefaultQuadsPerEdge + 1;
        c.Eq((float)sea, cut.Xyz[quadsPerCellEdge * 3 + 1],
            "a boundary without a live anchor no longer opens a lowered sky slit");
        c.Eq((float)sea, cut.Xyz[(quadsPerCellEdge + 1) * 3 + 1], "a vertex away from cached terrain does not");
        c.Eq((float)sea, cut.Xyz[(verts * verts - 1) * 3 + 1], "the far corner is untouched");
    }

    static void MeshResolutionsAndAnchoring(Check c)
    {
        const int sea = 110;
        LodSilhouetteTile?[,] deep = Neighbourhood((x, z) => FullTile(x, z, sea - 20), TileX, TileZ);
        ulong covered = 1UL << LodSilhouetteField.CoverageBit(0, 0);
        var anchors = new float[LodSilhouetteField.AnchorVerticesPerEdge
            * LodSilhouetteField.AnchorVerticesPerEdge];
        Array.Fill(anchors, float.NaN);
        int highX = LodSection.SectionBlocks
            / (LodSilhouetteTile.TileBlocks / LodSilhouetteField.HighQuadsPerEdge);
        anchors[highX] = 137;

        LodSilhouetteMeshSet? set = LodSilhouetteField.BuildMeshes(deep, covered, sea, anchors);
        c.True(set != null, "a partially covered tile publishes its three resolutions together");
        if (set == null) return;
        c.Eq((64, 16, 8), (set.High.QuadsPerEdge, set.Medium.QuadsPerEdge, set.Low.QuadsPerEdge),
            "the simplified mesh set contains 8-, 32- and 64-block quads");
        c.True(set.ByteCount > set.High.ByteCount,
            "the upload budget accounts for all three arrays rather than only the selected one");

        foreach (LodSilhouetteMeshArrays mesh in new[] { set.High, set.Medium, set.Low })
        {
            int boundaryX = LodSection.SectionBlocks
                / (LodSilhouetteTile.TileBlocks / mesh.QuadsPerEdge);
            c.Eq(137f,
                mesh.Xyz[boundaryX * 3 + 1],
                $"the {mesh.QuadsPerEdge}-quad mesh meets the cached edge anchor exactly");

            c.Eq((float)(sea - 20), mesh.Xyz[1],
                $"the covered interior of the {mesh.QuadsPerEdge}-quad mesh keeps raw seabed height");
        }

        // A whole anchor line is what CaptureAnchors supplies for an ordinary cached edge. The
        // next vertex must ease toward the sampled field instead of forming a four-block cliff.
        for (int z = 0; z < LodSilhouetteField.AnchorVerticesPerEdge; z++)
            anchors[z * LodSilhouetteField.AnchorVerticesPerEdge + highX] = 137;
        set = LodSilhouetteField.BuildMeshes(deep, covered, sea, anchors);
        if (set == null) return;
        int highZ = 32 / (LodSilhouetteTile.TileBlocks / LodSilhouetteField.HighQuadsPerEdge);
        int easedX = 80 / (LodSilhouetteTile.TileBlocks / LodSilhouetteField.HighQuadsPerEdge);
        float smoothQuarter = 0.25f * 0.25f * (3f - 2f * 0.25f);
        float expectedEase = sea + (137 - sea)
            * (1f - smoothQuarter);
        c.Eq(expectedEase,
            set.High.Xyz[(highZ * (LodSilhouetteField.HighQuadsPerEdge + 1) + easedX) * 3 + 1],
            "the high mesh eases away from a cached edge across the adjoining 64-block cell");

        // An anchor at x=0 came from a cached section in the neighbouring tile. It must still
        // own this tile's outer edge even though this tile's 64-bit mask is entirely uncovered.
        var outerAnchors = new float[anchors.Length];
        Array.Fill(outerAnchors, float.NaN);
        for (int z = 0; z < LodSilhouetteField.AnchorVerticesPerEdge; z++)
            outerAnchors[z * LodSilhouetteField.AnchorVerticesPerEdge] = 145;
        LodSilhouetteMeshSet? outer = LodSilhouetteField.BuildMeshes(deep, 0, sea, outerAnchors);
        c.Eq(145f, outer?.High.Xyz[1] ?? float.NaN,
            "a cached neighbour across a tile border anchors the tile's outer edge");

        c.Eq(LodSilhouetteField.HighQuadsPerEdge,
            LodSilhouetteField.ResolutionFor(1000, 1000, 0.35f),
            "a large on-screen tile selects the high silhouette mesh");
        c.Eq(LodSilhouetteField.MediumQuadsPerEdge,
            LodSilhouetteField.ResolutionFor(1000, 2500, 0.35f),
            "a medium on-screen tile selects the medium silhouette mesh");
        c.Eq(LodSilhouetteField.LowQuadsPerEdge,
            LodSilhouetteField.ResolutionFor(1000, 8000, 0.35f),
            "a small on-screen tile selects the low silhouette mesh");
        c.Eq(LodSilhouetteField.HighQuadsPerEdge,
            LodSilhouetteField.ResolutionFor(1000, 8000, 1f),
            "the sharp end of the dial retains fine distant peaks");
        c.Eq(LodSilhouetteField.LowQuadsPerEdge,
            LodSilhouetteField.ResolutionFor(1000, 8000, 0f),
            "the soft end of the dial chooses the broadest distant samples");
        c.Eq(LodSilhouetteField.LowQuadsPerEdge,
            LodSilhouetteField.ResolutionFor(0, 1),
            "an unavailable screen scale fails to the cheapest mesh");
    }

    static void FeatureGate(Check c)
    {
        c.True(LodAssist.SilhouettesAvailable(1, LodAssist.FeatureSilhouetteTiles),
            "an advertised feature on a usable protocol is available");
        c.False(LodAssist.SilhouettesAvailable(0, LodAssist.FeatureSilhouetteTiles),
            "without a usable protocol there are no silhouettes");
        c.False(LodAssist.SilhouettesAvailable(1, LodAssist.FeatureWorldGeneration),
            "a server without the bit is never sent a silhouette request");
    }

    static void ClientSettingsArithmetic(Check c)
    {
        LodSilhouetteSettings defaults = LodSilhouetteSettings.Default;
        c.True(defaults.Enabled && defaults.ReachBlocks == 8192,
            "a fresh client asks for the accepted 8,192-block silhouette reach");
        c.Eq(1024, LodSilhouetteSettings.ClampReach(-1),
            "silhouette reach clamps to the 1,024-block floor");
        c.Eq(32768, LodSilhouetteSettings.ClampReach(int.MaxValue),
            "silhouette reach clamps to the shared 32,768-block scale endpoint");
        c.Eq(2560, LodSilhouetteSettings.ClampReach(2700),
            "silhouette reach snaps to 512-block increments");
        c.Eq(4096, LodThresholdScaleLayout.SnapSilhouetteReach(4096),
            "Farseer reach remains independently adjustable inside the cached draw edge");

        LodSilhouetteSettings repaired = (defaults with
        {
            ReachBlocks = int.MaxValue,
            SkyTint = float.NaN,
            TintRed = -1,
            TintStrength = 2,
            LightLevelBias = 0,
            FadeBias = 2,
            GlobeEffect = float.PositiveInfinity,
            OutlineDetail = float.PositiveInfinity,
        }).Normalize();
        c.Eq(32768, repaired.ReachBlocks, "a hand-edited reach is normalized");
        c.Near(defaults.SkyTint, repaired.SkyTint, 1e-6, "a non-finite dial returns to its default");
        c.Eq((0f, 1f), (repaired.TintRed, repaired.TintStrength),
            "colour settings stay inside their shader range");
        c.Eq((0.51f, 0.95f, defaults.GlobeEffect, defaults.OutlineDetail),
            (repaired.LightLevelBias, repaired.FadeBias, repaired.GlobeEffect,
                repaired.OutlineDetail),
            "bias and globe settings avoid invalid pow inputs and non-finite curves");
    }

    static string Source(params string[] parts) => File.ReadAllText(
        Path.Combine(new[] { GameAssemblies.RepoRoot, "VintageHorizons" }.Concat(parts).ToArray()));

    static void MessageOrderMatches(Check c)
    {
        static List<string> Order(string source) => Regex.Matches(source, @"RegisterMessageType<(\w+)>\(\)")
            .Select(m => m.Groups[1].Value).ToList();
        List<string> client = Order(Source("src", "Net", "LodAssistClient.cs"));
        List<string> server = Order(Source("src", "Net", "LodAssistServerSystem.cs"));
        c.SeqEq(client, server, "client and server register the assist messages in the same order");
        c.SeqEq(new[]
        {
            "AssistHello", "AssistWelcome", "AssistKeyManifest", "AssistSectionRequest", "AssistSection",
            "AssistWorldGenRequest", "AssistWorldGenStatus", "AssistSilhouetteRequest",
            "AssistSilhouetteManifest", "AssistSilhouetteTileRequest", "AssistSilhouetteTile",
        }, client, "the silhouette messages are appended after the world-generation pair, in the plan's order");
    }

    static void PreviewsNeverReachCapture(Check c)
    {
        string builder = Source("src", "Silhouette", "LodSilhouetteBuilder.cs");
        foreach (string call in new[] { "CaptureColumn(", "QueueColumn(", "LoadChunkColumn", "TryAcquire(" })
            c.False(builder.Contains(call, StringComparison.Ordinal),
                "the silhouette builder never calls " + call.TrimEnd('('));
        c.True(builder.Contains("UntilPass = EnumWorldGenPass.Terrain", StringComparison.Ordinal),
            "silhouette previews stop at the Terrain pass");
    }

    static void UploadedUniformsAreDeclared(Check c)
    {
        string pass = Source("src", "Render", "LodTerrainRenderer.Silhouette.cs");
        List<string> names = Regex.Matches(pass, @"\bp\.Uniform(?:Matrix)?\(""(\w+)""")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        string engine = Path.Combine(GameAssemblies.GamePath, "assets", "game", "shaderincludes");
        string declared = Source("assets", "vintagehorizons", "shaders", "lodsilhouette.vsh")
            + Source("assets", "vintagehorizons", "shaders", "lodsilhouette.fsh")
            + File.ReadAllText(Path.Combine(engine, "fogandlight.vsh"))
            + File.ReadAllText(Path.Combine(engine, "fogandlight.fsh"));
        c.True(names.Count >= 15, $"found the silhouette pass's uniform uploads ({names.Count})");
        List<string> missing = names.Where(name => !Regex.IsMatch(declared, @"uniform\s+\w+\s+" + name + @"\b")).ToList();
        c.SeqEq(Array.Empty<string>(), missing,
            "every uniform the silhouette pass uploads is declared (the engine throws otherwise, G174)");
    }

    static void SamplerLayout(Check c)
    {
        // The installed engine's own include expansion and sampler collector, as in
        // TerrainSamplerChecks: no compile and no GL call is needed to see a unit collision.
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        FieldInfo includes = typeof(ShaderRegistry).GetField("includes", flags)!;
        MethodInfo expand = typeof(ShaderRegistry).GetMethod("HandleIncludes", flags)!;
        MethodInfo collect = typeof(ShaderProgram).GetMethod("collectUniformNames",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        object? previous = includes.GetValue(null);
        var sources = new Dictionary<string, string>();
        foreach (string directory in new[]
        {
            Path.Combine(GameAssemblies.GamePath, "assets", "game", "shaderincludes"),
            Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "assets", "vintagehorizons", "shaderincludes"),
        })
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                sources[Path.GetFileName(file)] = File.ReadAllText(file);

        try
        {
            includes.SetValue(null, sources);
            var program = new ShaderProgram();
            var names = new HashSet<string>();
            foreach (string stage in new[] { "vsh", "fsh" })
            {
                string code = (string)expand.Invoke(null,
                    [program, Source("assets", "vintagehorizons", "shaders", "lodsilhouette." + stage), null])!;
                collect.Invoke(program, [code, names]);
            }
            c.True(program.textureLocations.ContainsKey("sky") && program.textureLocations.ContainsKey("glow"),
                "the silhouette program samples the engine's sky and glow");
            LodTerrainSamplerLayout.Configure(program);
            c.Eq(program.textureLocations.Count, program.textureLocations.Values.Distinct().Count(),
                "the silhouette program has no sampler aliases after layout configuration");
            c.True(program.textureLocations.Values.All(unit => unit < 16),
                "the silhouette program stays within the baseline fragment-unit limit");
            foreach (var binding in LodTerrainSamplerLayout.Bindings)
                if (program.textureLocations.TryGetValue(binding.Name, out int unit))
                    c.Eq(binding.Unit, unit, "the silhouette program binds " + binding.Name + " where the terrain programs do");
        }
        finally
        {
            includes.SetValue(null, previous);
        }

        string pass = Source("src", "Render", "LodTerrainRenderer.Silhouette.cs").Replace("\r\n", "\n");
        c.True(pass.Contains("silhouetteShaderOk = silhouetteProg.Compile();\n        if (silhouetteShaderOk) "
                + "LodTerrainSamplerLayout.Configure(silhouetteProg);", StringComparison.Ordinal),
            "the silhouette program is configured before its first use");
    }

    static void OffStateIsTheOldBehaviour(Check c)
    {
        string renderer = Source("src", "Render", "LodTerrainRenderer.cs");
        string pass = Source("src", "Render", "LodTerrainRenderer.Silhouette.cs");
        c.True(Regex.IsMatch(renderer,
                @"Math\.Max\(LodFarDistance\.RequiredProjection\(EffectiveFarDistance\),\s*SilhouetteProjectionDistance\)"),
            "the far plane takes the larger of the cached and the silhouette extents");
        c.True(Regex.IsMatch(pass, @"SilhouetteProjectionDistance\s*=>[^;]*Enabled: true[^;]*: 0f;"),
            "the silhouette extent is zero unless silhouettes are on and drawing");
        c.True(pass.Contains("if (!silhouetteShaderWanted) return;", StringComparison.Ordinal),
            "the silhouette program is never compiled until silhouettes are first switched on");
    }

    static void P4RenderingAndSettingsWiring(Check c)
    {
        string client = Source("src", "Silhouette", "LodSilhouetteClient.cs");
        string field = Source("src", "Silhouette", "LodSilhouetteField.cs");
        string pass = Source("src", "Render", "LodTerrainRenderer.Silhouette.cs");
        string renderer = Source("src", "Render", "LodTerrainRenderer.cs");
        string fragment = Source("assets", "vintagehorizons", "shaders", "lodsilhouette.fsh");
        string inputs = Source("assets", "vintagehorizons", "shaderincludes", "lodfragmentinputs.fsh");
        string finish = Source("assets", "vintagehorizons", "shaderincludes", "lodsurfacefinish.fsh");
        string color = Source("assets", "vintagehorizons", "shaderincludes", "lodsilhouettecolor.fsh");
        string dialog = Source("src", "Gui", "VintageHorizonsConfigDialog.cs");
        string scale = Source("src", "Gui", "LodThresholdScaleElement.cs");
        string mod = Source("src", "VintageHorizonsModSystem.cs")
            + Source("src", "VintageHorizonsModSystem.Silhouettes.cs");

        c.True(field.Contains("BuildMeshes", StringComparison.Ordinal)
                && field.Contains("HighQuadsPerEdge = 64", StringComparison.Ordinal)
                && field.Contains("MediumQuadsPerEdge = 16", StringComparison.Ordinal)
                && field.Contains("LowQuadsPerEdge = 8", StringComparison.Ordinal),
            "Farseer builds simplified 64, 16 and 8-quad tile meshes");
        c.True(client.IndexOf("CaptureAnchors(world", StringComparison.Ordinal)
                < client.IndexOf("Task.Run", StringComparison.Ordinal)
                && client.Contains("source.ColumnRuns(col)", StringComparison.Ordinal)
                && client.Contains("baseSectionX", StringComparison.Ordinal)
                && client.Contains("DirtyAnchorNeighbours", StringComparison.Ordinal)
                && client.Contains("blockX % LodSection.SectionBlocks == 0 ? -1 : 0",
                    StringComparison.Ordinal),
            "cached edge heights, including neighbours across tile borders, are copied on the owner thread and invalidate both sides before a worker starts");
        c.True(pass.Contains("LodSilhouetteField.ResolutionFor(", StringComparison.Ordinal)
                && pass.Contains("ScreenBlockScale", StringComparison.Ordinal)
                && pass.Contains("SilhouetteOutlineDetail", StringComparison.Ordinal)
                && pass.Contains("mesh.At(resolution)", StringComparison.Ordinal)
                && pass.Contains("silhouetteFrustum.BoxInView", StringComparison.Ordinal),
            "the draw pass frustum-culls and chooses one of three meshes from screen scale and the outline dial");

        c.True(fragment.Contains("floor(tilePos / 64.0)", StringComparison.Ordinal)
                && fragment.Contains("coveredCellsLow", StringComparison.Ordinal)
                && fragment.Contains("coveredCellsHigh", StringComparison.Ordinal)
                && fragment.Contains("length(worldPos.xz) < cacheFadeStart", StringComparison.Ordinal)
                && fragment.Contains("discard;", StringComparison.Ordinal),
            "cached cells discard exactly inside the cached fade and resume beyond its far edge");
        c.True(pass.Contains("SilhouetteUploadBytesPerFrame", StringComparison.Ordinal)
                && pass.Contains("SilhouetteUploadMillisecondsPerFrame", StringComparison.Ordinal)
                && pass.IndexOf("silhouetteMeshes[result.Key] = next;", StringComparison.Ordinal)
                    < pass.IndexOf("old?.Dispose();", StringComparison.Ordinal),
            "silhouette uploads have count, byte and time bounds and publish before disposal");
        c.True(pass.Contains("if (silhouetteMeshes.Count > 0) ReleaseSilhouetteMeshes();",
                    StringComparison.Ordinal)
                && client.Contains("Clear();", StringComparison.Ordinal),
            "switching silhouettes off releases GPU meshes and drops client tile state");

        c.True(color.Contains("vhSilhouetteTerrainColor", StringComparison.Ordinal)
                && fragment.Contains("#include lodsilhouettecolor.fsh", StringComparison.Ordinal)
                && inputs.Contains("#include lodsilhouettecolor.fsh", StringComparison.Ordinal),
            "the pass and cached fade use one shared silhouette colour function");
        c.True(finish.Contains("if (silhouetteEnabled == 1)", StringComparison.Ordinal)
                && renderer.Contains("SilhouetteFadeTargetActive ? 1 : 0", StringComparison.Ordinal),
            "cached terrain changes fade target only while complete silhouette GPU tiles can draw");
        c.True(pass.IndexOf("if (silhouetteMeshes.Count == 0) return;", StringComparison.Ordinal)
                < pass.IndexOf("silhouetteShaderWanted = true;", StringComparison.Ordinal),
            "an unavailable server never causes even the lazy silhouette shader compile");

        c.True(dialog.Contains("Name = \"Farseer\"", StringComparison.Ordinal)
                && dialog.Contains("ComposeSilhouettes", StringComparison.Ordinal)
                && dialog.Contains("SilhouetteRows", StringComparison.Ordinal)
                && dialog.Contains("Outline detail", StringComparison.Ordinal),
            "the settings editor names the feature for Farseer and exposes its appearance and outline dials");
        c.True(scale.Contains("DrawMarker", StringComparison.Ordinal)
                && scale.Contains("SilhouetteMarker", StringComparison.Ordinal)
                && scale.Contains("silhouettesEnabled", StringComparison.Ordinal)
                && scale.Contains("MarkerText(index)", StringComparison.Ordinal)
                && scale.Contains("MarkerText(index), CairoFont.WhiteDetailText()", StringComparison.Ordinal)
                && scale.Contains("$\"L{index + 1}\\n({values[index]})\"", StringComparison.Ordinal),
            "General's taller distance scale uses the Vanilla label font and stacks each LOD distance below its short name");
        c.True(dialog.Contains("int reach = LodSilhouetteSettings.ClampReach(blocks);",
                    StringComparison.Ordinal)
                && dialog.Contains("silhouettes.Preview(pendingSilhouettes);", StringComparison.Ordinal),
            "both Reach controls apply an independent Farseer distance immediately");
        c.True(dialog.Contains("(\"Performance\", false)", StringComparison.Ordinal)
                && dialog.Contains("(\"Balanced\", true)", StringComparison.Ordinal)
                && dialog.Contains("(\"Quality\", true)", StringComparison.Ordinal),
            "Performance disables silhouettes while Balanced and Quality enable them");
        foreach (string name in new[]
        {
            "HorizonSilhouettes", "SilhouetteReachBlocks", "SilhouetteSkyTint",
            "SilhouetteTintRed", "SilhouetteTintGreen", "SilhouetteTintBlue",
            "SilhouetteTintStrength", "SilhouetteLightLevelBias", "SilhouetteFadeBias",
            "SilhouetteGlobeEffect", "SilhouetteOutlineDetail",
        })
            c.True(mod.Contains(name, StringComparison.Ordinal), name + " is persisted in client config");
    }
}
