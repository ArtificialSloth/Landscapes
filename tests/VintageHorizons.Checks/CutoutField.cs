using Microsoft.Data.Sqlite;

namespace VintageHorizons.Checks;

/// <summary>
/// What it costs to stop a crop hiding the terrain behind it, measured against a real cache
/// with no game process.
///
/// WHY THIS EXISTS. The game draws a crop, tall grass or a vine as a see-through cutout -
/// its own block data says <c>sideopaque: {all: false}</c> - while the LOD draws it as a
/// solid cube, because a meadow of mats reads as a flat green sheet at distance. Treating
/// that cube as COVER claims an occlusion the game does not provide. Inside our own terrain
/// that claim is harmless and free: the crop really does hide the soil's top face and its
/// neighbour's wall, and nobody can see either. At the vanilla seam the game draws where we
/// do not, its crop is a transparent tuft, and those two dropped faces become a window
/// through the hollow cached terrain and out to the sky. Reported in play at a seam running
/// through a flax field.
///
/// The fix is to stop the cutout counting as cover. It buys a seam with no holes in it and
/// it pays in geometry that is sealed inside solid ground everywhere else - so the number
/// this harness reports is the ENTIRE cost, and the benefit is not in it at all.
///
/// HOW IT MEASURES. Not by counting faces itself - that would measure a reimplementation.
/// Each sampled section is meshed TWICE by the shipping mesher, once with cutouts covering
/// and once without, with the same four neighbours flagged the same way so no false wall
/// appears along a section edge. The difference is the answer and both sides of it come from
/// shipping code. Split by face orientation, because G127: a coverage change spends its bill
/// on walls and the vertex total will not say so.
///
/// HOW IT KNOWS WHAT A CUTOUT IS. In the game this is the block's own
/// <c>Block.AllSidesOpaque</c> under a Plant material guard (see LodBlockPolicy). There is no
/// block registry here, so the harness matches stored palette CODES by family prefix instead.
/// That is a stand-in for the engine's answer, not the same rule: it is blind to any family
/// not named below, which makes the reported cost a FLOOR rather than a ceiling.
/// </summary>
public static class CutoutField
{
    /// <summary>
    /// Families the game draws as see-through cutouts, from a palette scan of a real world:
    /// crops 4,855 palette entries, tall grass 7,294, vines 3,841, berry bushes 2,617,
    /// mushrooms 986 - every one of them stored as a solid cube today. Flowers and ferns are
    /// absent on purpose: they already carry FlagThin and have never counted as cover.
    /// </summary>
    static readonly string[] DefaultFamilies =
    {
        "crop-", "tallgrass-", "fruitingbush-", "wildvine-", "mushroom-",
    };

    sealed class Options
    {
        public string? Cache;
        public int Samples = 60;
        public int Seed = 20260830;
        public int Level;
        public int Cull = 64;
        public string[] Families = DefaultFamilies;
        public bool Help;
    }

    public static int Run(string[] args)
    {
        Options o;
        try { o = Parse(args); }
        catch (Exception e) { Console.Error.WriteLine("  " + e.Message); return 2; }
        if (o.Help) { PrintHelp(); return 0; }

        string? cache = o.Cache ?? NewestClientCache();
        if (cache == null || !File.Exists(cache))
        {
            Console.Error.WriteLine("  no cache database found - pass one with --cache <path>");
            return 2;
        }

        string copy = CopyForReading(cache);
        try
        {
            using var conn = new SqliteConnection($"Data Source={copy};Mode=ReadOnly");
            conn.Open();
            return Measure(conn, cache, o);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(copy)!, true); } catch { }
        }
    }

    static int Measure(SqliteConnection conn, string cache, Options o)
    {
        long[] keys = KeysAtLevel(conn, o.Level);
        if (keys.Length == 0)
        {
            Console.Error.WriteLine($"  no level-{o.Level} sections in {cache}");
            return 2;
        }

        var random = new Random(o.Seed);
        long[] sample = keys.OrderBy(_ => random.Next()).Take(o.Samples).ToArray();
        var sections = new CacheSections(conn);

        long coverVerts = 0, freeVerts = 0;
        long coverClusterQuads = 0, freeClusterQuads = 0;
        long coverClusters = 0, freeClusters = 0;
        var coverFaces = new FaceCounts();
        var freeFaces = new FaceCounts();
        long columns = 0, cutoutTopped = 0, cutoutRuns = 0;
        int meshed = 0, withCutouts = 0;

        foreach (long key in sample)
        {
            LodSection? self = sections.Get(key);
            if (self == null) continue;

            SectionSnapshot flaggedSelf = Flagged(self, o.Families, out bool anyHere);
            var neighbours = new SectionSnapshot?[4];
            for (int side = 0; side < 4; side++)
            {
                LodSection? nb = sections.Get(Neighbour(key, side));
                if (nb == null) continue;
                neighbours[side] = Flagged(nb, o.Families, out _);
            }

            // ONE flagged input for both meshes. The flag says what a block IS; the job knob
            // says what the mesher does about it. Flagging only one side would measure the
            // flag rather than the rule, and would put a false wall down every section edge.
            MeshResult covering = LodMesher.BuildMesh(new MeshJob
            {
                Key = key,
                Self = flaggedSelf,
                Neighbors = neighbours,
                CaveCullReach = o.Cull,
                CutoutBlocksCover = true,
            });
            MeshResult free = LodMesher.BuildMesh(new MeshJob
            {
                Key = key,
                Self = flaggedSelf,
                Neighbors = neighbours,
                CaveCullReach = o.Cull,
                CutoutBlocksCover = false,
            });

            meshed++;
            if (anyHere) withCutouts++;
            coverVerts += covering.VertexCount;
            freeVerts += free.VertexCount;
            coverClusterQuads += covering.ClusteredPackedOpaqueQuads.Length
                / LodPackedQuadFormat.WordsPerQuad;
            freeClusterQuads += free.ClusteredPackedOpaqueQuads.Length
                / LodPackedQuadFormat.WordsPerQuad;
            coverClusters += covering.PackedOpaqueClusters.Length;
            freeClusters += free.PackedOpaqueClusters.Length;
            Classify(covering.Xyz, coverFaces);
            Classify(free.Xyz, freeFaces);
            CountCutouts(self, flaggedSelf, ref columns, ref cutoutTopped, ref cutoutRuns);
            sections.Forget(key);
        }

        Console.WriteLine();
        Console.WriteLine("  crops and grass as non-cover, measured against a real cache");
        Console.WriteLine("  cache:   " + cache);
        Console.WriteLine($"  sample:  {meshed} level-{o.Level} sections, "
            + $"{withCutouts} holding a cutout block, cave reach {o.Cull}");
        Console.WriteLine("  family:  " + string.Join(" ", o.Families));
        Console.WriteLine();
        Console.WriteLine($"  captured columns        {columns,12:n0}");
        Console.WriteLine($"  ending in a cutout      {cutoutTopped,12:n0}   "
            + $"{Share(cutoutTopped, columns):0.00}% of the surface");
        Console.WriteLine($"  cutout runs anywhere    {cutoutRuns,12:n0}");
        Console.WriteLine();
        Console.WriteLine("  covering -> not covering");
        Console.WriteLine($"  opaque vertices  {coverVerts,12:n0} -> {freeVerts,12:n0}   "
            + Delta(coverVerts, freeVerts));
        Console.WriteLine($"  opaque quads     {coverVerts / 4,12:n0} -> {freeVerts / 4,12:n0}   "
            + Delta(coverVerts / 4, freeVerts / 4));
        Console.WriteLine($"  clustered quads  {coverClusterQuads,12:n0} -> {freeClusterQuads,12:n0}   "
            + Delta(coverClusterQuads, freeClusterQuads));
        Console.WriteLine($"  draw clusters    {coverClusters,12:n0} -> {freeClusters,12:n0}   "
            + Delta(coverClusters, freeClusters));
        Console.WriteLine($"  top quads        {coverFaces.Top,12:n0} -> {freeFaces.Top,12:n0}   "
            + Delta(coverFaces.Top, freeFaces.Top));
        Console.WriteLine($"  bottom quads     {coverFaces.Bottom,12:n0} -> {freeFaces.Bottom,12:n0}   "
            + Delta(coverFaces.Bottom, freeFaces.Bottom));
        Console.WriteLine($"  X-wall quads     {coverFaces.XWall,12:n0} -> {freeFaces.XWall,12:n0}   "
            + Delta(coverFaces.XWall, freeFaces.XWall));
        Console.WriteLine($"  Z-wall quads     {coverFaces.ZWall,12:n0} -> {freeFaces.ZWall,12:n0}   "
            + Delta(coverFaces.ZWall, freeFaces.ZWall));
        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// A snapshot whose palette carries the cutout flag wherever the stored code names one of
    /// the families. The snapshot owns its own flags array, so the original section is
    /// untouched and the two meshes differ in exactly the job knob.
    /// </summary>
    static SectionSnapshot Flagged(LodSection section, string[] families, out bool any)
    {
        SectionSnapshot snap = SectionSnapshot.Of(section);
        string[]? codes = section.PendingPaletteCodes;
        any = false;
        if (codes == null) return snap;

        // Water, thin cover and skipped blocks are classified before the cutout test in the
        // shipping policy, so they can never carry this flag; mirror that here.
        const byte NotTerrain = LodPaletteEntry.FlagWater
            | LodPaletteEntry.FlagThin
            | LodPaletteEntry.FlagSkip;

        for (int i = 0; i < snap.PaletteFlags.Length && i < codes.Length; i++)
        {
            string? code = codes[i];
            if (code == null) continue;
            // Domain-qualified: "game:crop-flax-4". Match on the path, as the shipping
            // thin-cover stand-in does, so a modded domain is not missed for its prefix.
            int colon = code.IndexOf(':');
            ReadOnlySpan<char> path = colon >= 0 ? code.AsSpan(colon + 1) : code.AsSpan();
            bool hit = false;
            foreach (string family in families)
            {
                if (!path.StartsWith(family, StringComparison.Ordinal)) continue;
                hit = true;
                break;
            }
            if (!hit) continue;
            if ((snap.PaletteFlags[i] & NotTerrain) != 0) continue;

            snap.PaletteFlags[i] |= LodPaletteEntry.FlagCutout;
            any = true;
        }
        return snap;
    }

    static void CountCutouts(LodSection section, SectionSnapshot flagged,
        ref long columns, ref long cutoutTopped, ref long cutoutRuns)
    {
        int total = LodSection.GridSize * LodSection.GridSize;
        for (int col = 0; col < total; col++)
        {
            Span<ulong> runs = section.ColumnRuns(col);
            if (runs.Length == 0) continue;
            columns++;
            for (int r = 0; r < runs.Length; r++)
            {
                int pid = LodSection.RunPaletteId(runs[r]);
                if (pid >= flagged.PaletteFlags.Length
                    || (flagged.PaletteFlags[pid] & LodPaletteEntry.FlagCutout) == 0) continue;
                cutoutRuns++;
                if (r == 0) cutoutTopped++;
            }
        }
    }

    sealed class FaceCounts
    {
        public long Top;
        public long Bottom;
        public long XWall;
        public long ZWall;
    }

    /// <summary>Count emitted quads by their geometric normal.</summary>
    static void Classify(float[]? xyz, FaceCounts counts)
    {
        if (xyz == null) return;
        for (int v = 0; v + 12 <= xyz.Length; v += 12)
        {
            float ax = xyz[v + 3] - xyz[v];
            float ay = xyz[v + 4] - xyz[v + 1];
            float az = xyz[v + 5] - xyz[v + 2];
            float bx = xyz[v + 6] - xyz[v];
            float by = xyz[v + 7] - xyz[v + 1];
            float bz = xyz[v + 8] - xyz[v + 2];
            float nx = ay * bz - az * by;
            float ny = az * bx - ax * bz;
            float nz = ax * by - ay * bx;

            float absX = Math.Abs(nx);
            float absY = Math.Abs(ny);
            float absZ = Math.Abs(nz);
            if (absY >= absX && absY >= absZ)
            {
                if (ny >= 0) counts.Top++; else counts.Bottom++;
            }
            else if (absX >= absZ) counts.XWall++;
            else counts.ZWall++;
        }
    }

    static long Neighbour(long key, int side)
    {
        int level = LodWorld.KeyLevel(key);
        int sx = LodWorld.KeySx(key);
        int sz = LodWorld.KeySz(key);
        return side switch
        {
            0 => LodWorld.SectionKey(level, sx - 1, sz),
            1 => LodWorld.SectionKey(level, sx + 1, sz),
            2 => LodWorld.SectionKey(level, sx, sz - 1),
            _ => LodWorld.SectionKey(level, sx, sz + 1),
        };
    }

    sealed class CacheSections
    {
        readonly SqliteCommand cmd;
        readonly LodStore decoder = new(new CaptureLogger());
        readonly Dictionary<long, LodSection?> loaded = new();

        public CacheSections(SqliteConnection conn)
        {
            cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Data FROM Section WHERE Detail=@d AND SX=@x AND SZ=@z";
            cmd.Parameters.Add("@d", SqliteType.Integer);
            cmd.Parameters.Add("@x", SqliteType.Integer);
            cmd.Parameters.Add("@z", SqliteType.Integer);
            cmd.Prepare();
        }

        public LodSection? Get(long key)
        {
            if (loaded.TryGetValue(key, out LodSection? cached)) return cached;
            cmd.Parameters["@d"].Value = LodWorld.KeyLevel(key);
            cmd.Parameters["@x"].Value = LodWorld.KeySx(key);
            cmd.Parameters["@z"].Value = LodWorld.KeySz(key);
            LodSection? section = cmd.ExecuteScalar() is byte[] blob
                ? decoder.DeserializeForeign(blob, null)
                : null;
            loaded[key] = section;
            return section;
        }

        public void Forget(long key) => loaded.Remove(key);
    }

    static long[] KeysAtLevel(SqliteConnection conn, int level)
    {
        var keys = new List<long>();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT SX,SZ FROM Section WHERE Detail={level}";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            keys.Add(LodWorld.SectionKey(level, reader.GetInt32(0), reader.GetInt32(1)));
        }
        return keys.ToArray();
    }

    static string? NewestClientCache()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VintagestoryData", "ModData", "dodenslandscapes");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateFiles(root, "*.db")
            .Where(p => !p.EndsWith("-server.db", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// The owner's cache is not a test fixture. A snapshot beside it costs a file copy and
    /// removes any possibility of this tool writing to the only copy.
    /// </summary>
    static string CopyForReading(string cache)
    {
        string dir = Path.Combine(
            Path.GetTempPath(), "vh-cutoutfield-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string copy = Path.Combine(dir, Path.GetFileName(cache));
        File.Copy(cache, copy);
        foreach (string side in new[] { "-wal", "-shm" })
        {
            if (File.Exists(cache + side)) File.Copy(cache + side, copy + side);
        }
        return copy;
    }

    static double Share(long part, long whole) => whole > 0 ? part * 100.0 / whole : 0.0;

    static string Delta(long before, long after)
    {
        if (before == 0) return "no baseline";
        long change = after - before;
        string sign = change >= 0 ? "+" : "";
        return $"{sign}{change:n0}, {change * 100.0 / before:+0.00;-0.00;0.00}%";
    }

    static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "cutoutfield": break;
                case "--help":
                case "-h": o.Help = true; break;
                case "--cache": o.Cache = Next(args, ref i); break;
                case "--samples": o.Samples = int.Parse(Next(args, ref i)); break;
                case "--seed": o.Seed = int.Parse(Next(args, ref i)); break;
                case "--level": o.Level = int.Parse(Next(args, ref i)); break;
                case "--cull": o.Cull = int.Parse(Next(args, ref i)); break;
                case "--families":
                    o.Families = Next(args, ref i)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries
                            | StringSplitOptions.TrimEntries);
                    break;
                default:
                    if (args[i].StartsWith('-')) throw new ArgumentException("unknown option " + args[i]);
                    break;
            }
        }
        return o;
    }

    static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException("missing value after " + args[i]);
        return args[++i];
    }

    static void PrintHelp()
    {
        Console.WriteLine();
        Console.WriteLine("  cutoutfield - what it costs to stop a crop hiding the terrain behind it");
        Console.WriteLine();
        Console.WriteLine("    --cache <path>     cache database (default: newest client cache)");
        Console.WriteLine("    --samples <n>      sections to mesh (default 60)");
        Console.WriteLine("    --seed <n>         sample seed");
        Console.WriteLine("    --level <n>        detail level to sample (default 0)");
        Console.WriteLine("    --cull <blocks>    cave cull reach (default 64)");
        Console.WriteLine("    --families <list>  comma-separated code prefixes to treat as cutouts");
        Console.WriteLine();
        Console.WriteLine("  Reports the ENTIRE cost. The benefit - a seam with no holes in it -");
        Console.WriteLine("  is not in these numbers and cannot be.");
        Console.WriteLine();
    }
}
