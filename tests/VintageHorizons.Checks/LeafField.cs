using Microsoft.Data.Sqlite;

namespace VintageHorizons.Checks;

/// <summary>
/// What each leaf shape costs, measured on a real cache with no game process.
///
/// A leaf block is not six faces, and a plane is not two: faces between touching blocks are never
/// drawn, flat runs of them merge, a plane merges up its column, and a leaf that stops covering
/// what is beside it gives the mesh a log's or the ground's face back. So each section is meshed
/// by the shipping mesher in every shape, and the whole mesh is compared rather than the leaves.
///
/// Faces only. A cube's back faces are rejected before shading and a plane has none, which the
/// "reach pixels" column allows for by counting half of the ordinary quads; what a see-through
/// plane costs per pixel can only be read off a frame counter.
/// </summary>
public static class LeafField
{
    // One palette block id for every leaf, one for a fruit tree's wood and one for everything else,
    // standing in for the tables a running game builds from its block registry.
    const int LeafBlockId = 1;
    const int FruitWoodBlockId = 2;
    static readonly bool[] LeafTable = { false, true, false };
    static readonly bool[] FruitWoodTable = { false, false, true };

    public static int Run(string[] args)
    {
        string? cache = null;
        int samples = 60, seed = 20261101;
        bool under = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--cache") cache = args[++i];
            else if (args[i] == "--samples") samples = int.Parse(args[++i]);
            else if (args[i] == "--seed") seed = int.Parse(args[++i]);
            else if (args[i] == "--under") under = true;
        }
        if (cache == null || !File.Exists(cache))
        {
            Console.Error.WriteLine("  pass a cache database with --cache <path>");
            return 2;
        }

        string dir = Path.Combine(Path.GetTempPath(), "vh-leaffield-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string copy = Path.Combine(dir, Path.GetFileName(cache));
            File.Copy(cache, copy);
            using var conn = new SqliteConnection($"Data Source={copy};Mode=ReadOnly");
            conn.Open();
            return Measure(conn, cache, samples, seed, under);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// What stands between the canopy and the ground: every run under a column's first leaf down to
    /// its soil or stone, by block code. Which side of a vine holds it up is read from the blocks
    /// around it, since a vine's name gives the direction it faces rather than where it hangs.
    /// </summary>
    static int Under(SqliteConnection conn, LodStore decoder, long[] sample)
    {
        using SqliteCommand get = conn.CreateCommand();
        get.CommandText = "SELECT Data FROM Section WHERE Detail=0 AND SX=@x AND SZ=@z";
        get.Parameters.Add("@x", SqliteType.Integer);
        get.Parameters.Add("@z", SqliteType.Integer);
        get.Prepare();

        var blocks = new Dictionary<string, long>();
        var supports = new Dictionary<string, long>();
        int gs = LodSection.GridSize;
        foreach (long key in sample)
        {
            get.Parameters["@x"].Value = LodWorld.KeySx(key);
            get.Parameters["@z"].Value = LodWorld.KeySz(key);
            if (get.ExecuteScalar() is not byte[] blob) continue;
            LodSection? section = decoder.DeserializeForeign(blob, null);
            string[]? codes = section?.PendingPaletteCodes;
            if (section == null || codes == null) continue;
            string Path(ulong run)
            {
                string code = codes[LodSection.RunPaletteId(run)];
                return code.Substring(code.IndexOf(':') + 1);
            }
            bool Solid(int x, int y, int z)
            {
                if (x < 0 || z < 0 || x >= gs || z >= gs) return false;
                foreach (ulong run in section.ColumnRuns(LodSection.ColumnIndex(x, z)))
                {
                    if (LodSection.RunYTop(run) <= y || LodSection.RunYBottom(run) > y) continue;
                    string path = Path(run);
                    return !path.StartsWith("wildvine", StringComparison.Ordinal)
                        && !path.StartsWith("tallgrass", StringComparison.Ordinal);
                }
                return false;
            }

            for (int cz = 0; cz < gs; cz++)
            {
                for (int cx = 0; cx < gs; cx++)
                {
                    bool canopy = false;
                    foreach (ulong run in section.ColumnRuns(LodSection.ColumnIndex(cx, cz)))
                    {
                        string path = Path(run);
                        long height = LodSection.RunYTop(run) - LodSection.RunYBottom(run);
                        if (path.StartsWith("wildvine", StringComparison.Ordinal))
                        {
                            string side = path.Substring(path.LastIndexOf('-') + 1);
                            int y = LodSection.RunYBottom(run);
                            if (Solid(cx, y, cz + 1)) Add(supports, side + " held from south", 1);
                            if (Solid(cx, y, cz - 1)) Add(supports, side + " held from north", 1);
                            if (Solid(cx + 1, y, cz)) Add(supports, side + " held from east", 1);
                            if (Solid(cx - 1, y, cz)) Add(supports, side + " held from west", 1);
                        }
                        if (path.StartsWith("leaves", StringComparison.Ordinal)
                            || path.StartsWith("fruittree-foliage", StringComparison.Ordinal))
                        {
                            canopy = true;
                            continue;
                        }
                        if (!canopy) continue;
                        byte flags = section.Palette[LodSection.RunPaletteId(run)].Flags;
                        Add(blocks, $"{Trim(path)} flags {flags}", height);
                        if (path.StartsWith("soil", StringComparison.Ordinal)
                            || path.StartsWith("rock", StringComparison.Ordinal)
                            || path.StartsWith("sand", StringComparison.Ordinal)
                            || path.StartsWith("gravel", StringComparison.Ordinal)
                            || path.StartsWith("forestfloor", StringComparison.Ordinal))
                        {
                            break;
                        }
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("  under the canopy, in blocks");
        foreach ((string code, long count) in blocks.OrderByDescending(pair => pair.Value).Take(30))
            Console.WriteLine($"  {count,10:n0}  {code}");
        Console.WriteLine();
        Console.WriteLine("  what holds a vine up");
        foreach ((string side, long count) in supports.OrderBy(pair => pair.Key))
            Console.WriteLine($"  {count,10:n0}  {side}");
        return 0;
    }

    static void Add(Dictionary<string, long> tally, string key, long amount) =>
        tally[key] = tally.GetValueOrDefault(key) + amount;

    /// <summary>A code without its last two parts, so one plant's sizes and sides add up.</summary>
    static string Trim(string path)
    {
        string[] parts = path.Split('-');
        return parts.Length > 2 ? string.Join('-', parts.Take(2)) : path;
    }

    static int Measure(SqliteConnection conn, string cache, int samples, int seed, bool under)
    {
        var keys = new List<long>();
        using (SqliteCommand list = conn.CreateCommand())
        {
            list.CommandText = "SELECT SX,SZ FROM Section WHERE Detail=0";
            using SqliteDataReader reader = list.ExecuteReader();
            while (reader.Read())
                keys.Add(LodWorld.SectionKey(0, reader.GetInt32(0), reader.GetInt32(1)));
        }

        var random = new Random(seed);
        long[] sample = keys.OrderBy(_ => random.Next()).Take(samples).ToArray();
        var decoder = new LodStore(new CaptureLogger());
        if (under) return Under(conn, decoder, sample);
        using SqliteCommand get = conn.CreateCommand();
        get.CommandText = "SELECT Data FROM Section WHERE Detail=0 AND SX=@x AND SZ=@z";
        get.Parameters.Add("@x", SqliteType.Integer);
        get.Parameters.Add("@z", SqliteType.Integer);
        get.Prepare();

        LodSection? Load(int sx, int sz)
        {
            get.Parameters["@x"].Value = sx;
            get.Parameters["@z"].Value = sz;
            if (get.ExecuteScalar() is not byte[] blob) return null;
            LodSection? section = decoder.DeserializeForeign(blob, null);
            if (section != null) MarkLeaves(section);
            return section;
        }

        LodLeafShape[] shapes = Enum.GetValues<LodLeafShape>();
        var quads = new long[shapes.Length];
        var planes = new long[shapes.Length];
        var meshTicks = new long[shapes.Length];
        var shadeArea = new double[shapes.Length];
        var shadeSum = new double[shapes.Length];
        var buriedArea = new double[shapes.Length, LodPackedQuadFormat.MaximumLeafShade + 1];
        long leafBlocks = 0;
        int meshed = 0, withLeaves = 0;

        foreach (long key in sample)
        {
            int sx = LodWorld.KeySx(key), sz = LodWorld.KeySz(key);
            LodSection? self = Load(sx, sz);
            if (self == null) continue;

            var sides = new LodSection?[4];
            (int dx, int dz)[] steps = { (-1, 0), (1, 0), (0, -1), (0, 1) };
            for (int side = 0; side < 4; side++)
                sides[side] = Load(sx + steps[side].dx, sz + steps[side].dz);

            long leaves = CountLeaves(self);
            leafBlocks += leaves;
            if (leaves > 0) withLeaves++;
            meshed++;

            for (int s = 0; s < shapes.Length; s++)
            {
                bool[]? leafPlanes = shapes[s] == LodLeafShape.Cubes ? null : LeafTable;
                var neighbours = new SectionSnapshot?[4];
                for (int side = 0; side < 4; side++)
                {
                    if (sides[side] != null)
                        neighbours[side] = SectionSnapshot.Of(sides[side]!, leafPlanes: leafPlanes);
                }

                var job = new MeshJob
                {
                    Key = key,
                    Self = SectionSnapshot.Of(self, crossedShapes: _ => LodBlockPolicy.CrossedShapeByFlags,
                        leafPlanes: leafPlanes, canopyWood: FruitWoodTable),
                    Neighbors = neighbours,
                    CaveCullReach = 64,
                    EmitTextureSlots = true,
                    LeafShape = shapes[s],
                };
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                MeshResult mesh = LodMesher.BuildMesh(job);
                meshTicks[s] += System.Diagnostics.Stopwatch.GetTimestamp() - started;
                quads[s] += mesh.PackedOpaqueQuadCount;
                for (int q = 0; q < mesh.PackedOpaqueQuadCount; q++)
                {
                    if (LodPackedQuadFormat.FaceOf(mesh.PackedOpaqueQuads, q) < LodPackedFace.DiagonalRising) continue;
                    planes[s]++;
                    if (!LodPackedQuadFormat.IsTextured(mesh.PackedOpaqueQuads[q * 3])) continue;

                    int buried = LodPackedQuadFormat.LeafShade(mesh.PackedOpaqueQuads[q * 3 + 2]);
                    uint heights = mesh.PackedOpaqueQuads[q * 3 + 1];
                    float height = ((heights >> 16) - (heights & 0xFFFFu)) * 0.25f;
                    // A flat plane is two blocks by two, and a stack's foot is shaded down its height.
                    float area = height > 0f ? height : 2f;
                    float foot = 1f - (0.16f + 0.2f * Math.Clamp(height / 12f, 0f, 1f));
                    float shade = (1f - 0.03f * buried) * (height > 0f ? (1f + foot) / 2f : 1f);
                    shadeArea[s] += area;
                    shadeSum[s] += area * shade;
                    buriedArea[s, buried] += area;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("  leaf shapes, whole level-0 mesh, real mesher, real cache");
        Console.WriteLine("  cache:   " + cache);
        Console.WriteLine($"  sample:  {meshed} level-0 sections, {withLeaves} holding leaves, seed {seed}");
        Console.WriteLine($"  leaf blocks: {leafBlocks:n0}");
        Console.WriteLine();
        Console.WriteLine("  shape                  all quads   vs cubes     planes   reach pixels   vs cubes");
        long Reach(int s) => (quads[s] - planes[s]) / 2 + planes[s];
        for (int s = 0; s < shapes.Length; s++)
        {
            Console.WriteLine($"  {shapes[s],-20}{quads[s],12:n0}{quads[s] * 100.0 / Math.Max(1, quads[0]),10:0.0}%"
                + $"{planes[s],11:n0}{Reach(s),15:n0}{Reach(s) * 100.0 / Math.Max(1, Reach(0)),10:0.0}%");
        }
        Console.WriteLine();
        Console.WriteLine("  shape                  mesh ms   mean leaf shade   share of leaf area by buried 0-2 / 3-5 / 6-8 / 9-11");
        for (int s = 0; s < shapes.Length; s++)
        {
            double Share(int from, int to)
            {
                double area = 0;
                for (int b = from; b <= to; b++) area += buriedArea[s, b];
                return area * 100.0 / Math.Max(1, shadeArea[s]);
            }
            double ms = meshTicks[s] * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Console.WriteLine($"  {shapes[s],-20}{ms,10:0.0}{shadeSum[s] / Math.Max(1, shadeArea[s]),18:0.000}"
                + $"{Share(0, 2),12:0}%{Share(3, 5),6:0}%{Share(6, 8),6:0}%{Share(9, 11),6:0}%");
        }
        Console.WriteLine();
        return 0;
    }

    /// <summary>Every leaf one block id with a texture slot, so the snapshots read it as a running game would.</summary>
    static void MarkLeaves(LodSection section)
    {
        string[]? codes = section.PendingPaletteCodes;
        for (int i = 0; i < section.Palette.Count; i++)
        {
            string? code = codes != null && i < codes.Length ? codes[i] : null;
            string path = code?.Substring(code.IndexOf(':') + 1) ?? "";
            bool leaf = path.StartsWith("leaves", StringComparison.Ordinal)
                || path.StartsWith("fruittree-foliage", StringComparison.Ordinal);
            bool wood = !leaf && path.StartsWith("fruittree-", StringComparison.Ordinal);
            LodPaletteEntry e = section.Palette[i];
            e.BlockId = leaf ? LeafBlockId : wood ? FruitWoodBlockId : 0;
            e.TextureSlot = leaf ? (ushort)1 : (ushort)0;
            section.Palette[i] = e;
        }
    }

    static long CountLeaves(LodSection section)
    {
        long blocks = 0;
        foreach (ulong run in section.Runs)
        {
            if (section.Palette[LodSection.RunPaletteId(run)].BlockId == LeafBlockId)
                blocks += LodSection.RunYTop(run) - LodSection.RunYBottom(run);
        }
        return blocks;
    }
}
