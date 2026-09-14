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
        FieldInterpolation(c);
        MeshMatchesAcrossTileBorders(c);
        MeshSeaAndCoverage(c);
        FeatureGate(c);
    }

    public static void RunWiring(Check c)
    {
        MessageOrderMatches(c);
        PreviewsNeverReachCapture(c);
        UploadedUniformsAreDeclared(c);
        SamplerLayout(c);
        OffStateIsTheOldBehaviour(c);
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

    static void FieldInterpolation(Check c)
    {
        double x = LodSilhouetteField.NodeBlockX(10), z = LodSilhouetteField.NodeBlockX(20);
        double Only(int i, int j) => i == 10 && j == 20 ? 100 : double.NaN;
        c.Near(100, LodSilhouetteField.HeightAt(x, z, Only), 1e-9, "at a known node the height is the node's");
        c.Near(100, LodSilhouetteField.HeightAt(x + 100, z + 50, Only), 1e-9,
            "unknown nodes drop out: one known node fills its surroundings");
        c.True(double.IsNaN(LodSilhouetteField.HeightAt(x, z, (_, _) => double.NaN)), "no known node, no height");

        double Pair(int i, int j) => j != 20 ? double.NaN : i == 10 ? 100 : i == 11 ? 200 : double.NaN;
        c.Near(150, LodSilhouetteField.HeightAt(x + LodSilhouetteField.LatticeBlocks / 2.0, z, Pair), 1e-9,
            "halfway between two known nodes is their mean");

        c.True(LodSilhouetteField.IsLatticeChunk(LodSilhouetteField.LatticeChunkOfNode(625),
                LodSilhouetteField.LatticeChunkOfNode(2000))
            && !LodSilhouetteField.IsLatticeChunk(16000, 16004), "lattice chunks sit at 4 mod 8");
        c.Eq(7, LodSilhouetteField.NodeOfLatticeChunk(LodSilhouetteField.LatticeChunkOfNode(7)),
            "a node and its lattice chunk convert both ways");
        var touched = LodSilhouetteField.TilesTouchedByNode(10, 20).ToList();
        c.True(touched.Count is >= 1 and <= 9 && touched.Contains(LodSilhouetteTile.TileOfBlock((int)x, (int)z)),
            "a node touches its own tile and at most its eight neighbours");
    }

    static double[,] Snapshot(int tileX, int tileZ, Func<int, int, double> node)
    {
        int firstX = LodSilhouetteField.FirstNodeOfTile(tileX), firstZ = LodSilhouetteField.FirstNodeOfTile(tileZ);
        var snapshot = new double[LodSilhouetteField.TileNodeSpan, LodSilhouetteField.TileNodeSpan];
        for (int b = 0; b < LodSilhouetteField.TileNodeSpan; b++)
            for (int a = 0; a < LodSilhouetteField.TileNodeSpan; a++)
                snapshot[b, a] = node(firstX + a, firstZ + b);
        return snapshot;
    }

    static void MeshMatchesAcrossTileBorders(Check c)
    {
        const int sea = 110;
        double Node(int i, int j) => (i * 37 + j * 91) % 7 == 0 ? double.NaN : 120 + (i * 131 + j * 71) % 97;
        LodSilhouetteMeshArrays? left = LodSilhouetteField.BuildMesh(TileX, TileZ, Snapshot(TileX, TileZ, Node), 0, sea);
        LodSilhouetteMeshArrays? right = LodSilhouetteField.BuildMesh(TileX + 1, TileZ,
            Snapshot(TileX + 1, TileZ, Node), 0, sea);
        LodSilhouetteMeshArrays? below = LodSilhouetteField.BuildMesh(TileX, TileZ + 1,
            Snapshot(TileX, TileZ + 1, Node), 0, sea);
        c.True(left != null && right != null && below != null, "tiles with known nodes around them mesh");
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

        bool direct = true;
        int spacing = LodSilhouetteTile.TileBlocks / LodSilhouetteField.DefaultQuadsPerEdge;
        for (int b = 0; b < verts; b++)
        {
            for (int a = 0; a < verts; a++)
            {
                double h = LodSilhouetteField.HeightAt(TileX * (double)LodSilhouetteTile.TileBlocks + a * spacing,
                    TileZ * (double)LodSilhouetteTile.TileBlocks + b * spacing, Node);
                float expected = double.IsNaN(h) ? sea : (float)Math.Max(h, sea);
                direct &= left.Xyz[(b * verts + a) * 3 + 1] == expected;
            }
        }
        c.True(direct, "a tile's four-by-four node snapshot holds every node its heights read");
    }

    static void MeshSeaAndCoverage(Check c)
    {
        const int sea = 110;
        double Deep(int i, int j) => sea - 20;
        LodSilhouetteMeshArrays? flat = LodSilhouetteField.BuildMesh(TileX, TileZ, Snapshot(TileX, TileZ, Deep), 0, sea);
        c.True(flat != null && Enumerable.Range(0, flat.VertexCount).All(v => flat.Xyz[v * 3 + 1] == sea),
            "ground below sea level draws as a flat sea");
        c.True(LodSilhouetteField.BuildMesh(TileX, TileZ, Snapshot(TileX, TileZ, Deep), ulong.MaxValue, sea) == null,
            "a tile cached terrain fully covers has no mesh");
        c.True(LodSilhouetteField.BuildMesh(TileX, TileZ, Snapshot(TileX, TileZ, (_, _) => double.NaN), 0, sea) == null,
            "a tile with no known node has no mesh");

        ulong oneCell = 1UL << LodSilhouetteField.CoverageBit(0, 0);
        LodSilhouetteMeshArrays? cut = LodSilhouetteField.BuildMesh(TileX, TileZ,
            Snapshot(TileX, TileZ, Deep), oneCell, sea);
        int quads = LodSilhouetteField.DefaultQuadsPerEdge * LodSilhouetteField.DefaultQuadsPerEdge;
        int quadsPerCellEdge = LodSection.SectionBlocks
            / (LodSilhouetteTile.TileBlocks / LodSilhouetteField.DefaultQuadsPerEdge);
        c.Eq((quads - quadsPerCellEdge * quadsPerCellEdge) * 6, cut?.IndexCount ?? -1,
            "a covered level-0 cell drops exactly its own quads");
        if (cut == null) return;
        int verts = LodSilhouetteField.DefaultQuadsPerEdge + 1;
        c.Eq(sea - LodSilhouetteField.DefaultSinkBlocks, cut.Xyz[quadsPerCellEdge * 3 + 1],
            "a vertex on the covered cell's edge sinks under cached terrain");
        c.Eq((float)sea, cut.Xyz[(quadsPerCellEdge + 1) * 3 + 1], "a vertex away from cached terrain does not");
        c.Eq((float)sea, cut.Xyz[(verts * verts - 1) * 3 + 1], "the far corner is untouched");
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
}
