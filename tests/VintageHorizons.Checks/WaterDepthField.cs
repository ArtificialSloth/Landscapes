using Microsoft.Data.Sqlite;

namespace VintageHorizons.Checks;

/// <summary>
/// What it costs to darken submerged ground by the water depth above it, measured against a
/// real cache with no game process.
///
/// WHY THIS EXISTS. Water in Vintage Story carries <c>lightAbsorption: 1</c>, and the engine's
/// illuminator settles a directly lit column at <c>defaultSunLight - (sum of absorption
/// above)</c> with no per-block travel penalty - so a lakebed under d blocks of water is
/// exactly d light levels darker than the shore beside it. On the survival ramp that is 0.03
/// of 0.806 per block: 85% of shore brightness at 4 blocks, 70% at 8, 55% at 12, 26% at 20.
/// Owner screenshots measure vanilla water spanning a 2.2x brightness range for exactly this
/// reason, against one flat value for cached water.
///
/// Cached terrain stores no light at all - one global sky-exposed level multiplies every
/// surface in the world - so this gradient is missing everywhere. For WATER specifically it
/// does not need to be captured or probed: the cache already holds the water runs sitting on
/// top of the bed, so the depth is arithmetic on data an existing cache already has.
///
/// WHAT IT WOULD COST. The mesher merges neighbouring quads that share a palette entry. Give
/// every column its own brightness and quads at different depths stop merging, so the bed
/// grows geometry. Rounding depth into a few buckets bounds that: fewer buckets, less growth,
/// coarser gradient. This harness measures the trade before anything is built.
///
/// HOW IT MEASURES. Not by counting faces itself - that would measure a reimplementation.
/// Each sampled section is meshed by the SHIPPING mesher, once unmodified and once per bucket
/// count with submerged runs re-keyed onto per-bucket palette entries. Re-keying is exactly
/// what a real depth-aware colour would do to the merge key, so the difference in vertices is
/// the whole cost. Colours are left alone: this measures merging, not appearance.
///
/// WHAT IT DOES NOT MEASURE. The benefit - a screenshot is the only judge of that. Nor
/// absorption from anything other than water: overhangs, cave mouths and foliage are invisible
/// to the runs. Nor the engine's lateral light spread, which lifts real shorelines a little
/// above what depth alone predicts.
/// </summary>
public static class WaterDepthField
{
    /// <summary>
    /// Blocks of water past which the bed is dark enough that further depth cannot be told
    /// apart. The survival ramp bottoms out at level 0 after 22 blocks; 24 is a round cap
    /// above that, so no real lake is clipped by it.
    /// </summary>
    const int MaxDepth = 24;

    sealed class Options
    {
        public string? Cache;
        public int Samples = 120;
        public int Seed = 20260903;
        public int Level;
        public int Cull = 64;
        public int[] Buckets = { 2, 4, 8, 16, MaxDepth };
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

        var depthHistogram = new long[MaxDepth + 1];
        long columns = 0, submerged = 0, waterColumns = 0;
        long baseVerts = 0, baseQuads = 0;
        var bucketVerts = new long[o.Buckets.Length];
        var bucketQuads = new long[o.Buckets.Length];
        var bucketSplitRuns = new long[o.Buckets.Length];
        int meshed = 0, withWater = 0;

        foreach (long key in sample)
        {
            LodSection? self = sections.Get(key);
            if (self == null) continue;

            var neighbours = new SectionSnapshot?[4];
            for (int side = 0; side < 4; side++)
            {
                LodSection? nb = sections.Get(Neighbour(key, side));
                neighbours[side] = nb == null ? null : SectionSnapshot.Of(nb);
            }

            SectionSnapshot plain = SectionSnapshot.Of(self);
            int[] depthOfRun = DepthPerRun(plain, out bool anyWater,
                ref columns, ref submerged, ref waterColumns, depthHistogram);

            MeshResult baseline = LodMesher.BuildMesh(new MeshJob
            {
                Key = key,
                Self = plain,
                Neighbors = neighbours,
                CaveCullReach = o.Cull,
            });

            meshed++;
            if (anyWater) withWater++;
            baseVerts += baseline.VertexCount;
            baseQuads += baseline.ClusteredPackedOpaqueQuads.Length
                / LodPackedQuadFormat.WordsPerQuad;

            for (int b = 0; b < o.Buckets.Length; b++)
            {
                SectionSnapshot split = Rekeyed(plain, depthOfRun, o.Buckets[b], out long moved);
                MeshResult mesh = LodMesher.BuildMesh(new MeshJob
                {
                    Key = key,
                    Self = split,
                    Neighbors = neighbours,
                    CaveCullReach = o.Cull,
                });
                bucketVerts[b] += mesh.VertexCount;
                bucketQuads[b] += mesh.ClusteredPackedOpaqueQuads.Length
                    / LodPackedQuadFormat.WordsPerQuad;
                bucketSplitRuns[b] += moved;
            }

            sections.Forget(key);
        }

        Console.WriteLine();
        Console.WriteLine("  darkening submerged ground by depth, measured against a real cache");
        Console.WriteLine("  cache:   " + cache);
        Console.WriteLine($"  sample:  {meshed} level-{o.Level} sections, "
            + $"{withWater} holding water, cave reach {o.Cull}");
        Console.WriteLine();
        Console.WriteLine($"  captured columns          {columns,12:n0}");
        Console.WriteLine($"  columns holding water     {waterColumns,12:n0}   "
            + $"{Share(waterColumns, columns):0.00}%");
        Console.WriteLine($"  submerged ground surfaces {submerged,12:n0}   "
            + $"{Share(submerged, columns):0.00}% of columns");
        Console.WriteLine();

        if (submerged > 0)
        {
            Console.WriteLine("  water depth over submerged ground");
            long cumulative = 0;
            for (int d = 1; d <= MaxDepth; d++)
            {
                if (depthHistogram[d] == 0) continue;
                cumulative += depthHistogram[d];
                Console.WriteLine($"    {d,2} block{(d == 1 ? " " : "s")}  {depthHistogram[d],10:n0}   "
                    + $"{Share(depthHistogram[d], submerged),5:0.0}%   "
                    + $"cumulative {Share(cumulative, submerged):0.0}%   "
                    + $"bed at {Brightness(d) * 100:0}% of shore");
            }
            Console.WriteLine();
        }

        Console.WriteLine("  geometry cost of splitting the merge by depth");
        Console.WriteLine($"  baseline           {baseVerts,12:n0} vertices   "
            + $"{baseQuads,10:n0} clustered quads");
        for (int b = 0; b < o.Buckets.Length; b++)
        {
            string label = o.Buckets[b] >= MaxDepth ? "exact (per block)" : $"{o.Buckets[b]} buckets";
            Console.WriteLine($"  {label,-18} {bucketVerts[b],12:n0} vertices   "
                + $"{bucketQuads[b],10:n0} quads   "
                + $"verts {Delta(baseVerts, bucketVerts[b])}");
        }
        Console.WriteLine();
        Console.WriteLine("  Bucket counts are the gradient's resolution: 2 buckets is shallow/deep,");
        Console.WriteLine("  'exact' is one step per block and is the ceiling on both cost and fidelity.");
        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// Blocks of water directly above each run's top face, or 0 where it is not submerged,
    /// indexed the same way as <see cref="SectionSnapshot.Runs"/>.
    ///
    /// Runs are stored top-down, so one descending walk per column carries the water above as
    /// it goes: a water run adds its own height, a solid run consumes what has accumulated as
    /// the depth over its top face, and anything else clears it. A solid run clears the
    /// accumulator too - what is under it is buried, not submerged.
    /// </summary>
    static int[] DepthPerRun(SectionSnapshot s, out bool anyWater,
        ref long columns, ref long submerged, ref long waterColumns, long[] histogram)
    {
        var depths = new int[s.Runs.Length];
        anyWater = false;
        int total = LodSection.GridSize * LodSection.GridSize;

        for (int col = 0; col < total; col++)
        {
            int start = s.ColumnStart[col];
            int end = s.ColumnStart[col + 1];
            if (end <= start) continue;
            columns++;

            int waterAbove = 0;
            bool columnHasWater = false;
            for (int r = start; r < end; r++)
            {
                ulong run = s.Runs[r];
                int pid = LodSection.RunPaletteId(run);
                if (pid < 0 || pid >= s.PaletteFlags.Length) { waterAbove = 0; continue; }
                byte flags = s.PaletteFlags[pid];
                // Runs are half-open, [yBottom, yTop) - see LodSection.PackRun. The
                // inclusive reading adds a phantom block of water to every column and
                // moves the whole depth histogram up by one.
                int height = LodSection.RunYTop(run) - LodSection.RunYBottom(run);

                if ((flags & LodPaletteEntry.FlagWater) != 0)
                {
                    waterAbove += height;
                    columnHasWater = true;
                    anyWater = true;
                    continue;
                }
                if ((flags & LodPaletteEntry.FlagSkip) != 0) continue;

                if (waterAbove > 0)
                {
                    int d = Math.Min(waterAbove, MaxDepth);
                    depths[r] = d;
                    histogram[d]++;
                    submerged++;
                }
                waterAbove = 0;
            }
            if (columnHasWater) waterColumns++;
        }
        return depths;
    }

    /// <summary>
    /// The same section with every submerged run moved onto a palette entry of its own depth
    /// bucket. Colours and flags are copied unchanged: a real implementation would darken the
    /// colour, but the darkening is free and only the SPLIT costs geometry, so leaving the
    /// colour alone keeps this measuring one thing.
    /// </summary>
    static SectionSnapshot Rekeyed(SectionSnapshot s, int[] depths, int buckets, out long moved)
    {
        moved = 0;
        var runs = (ulong[])s.Runs.Clone();
        var colors = new List<int>(s.PaletteColors);
        var flags = new List<byte>(s.PaletteFlags);
        var tints = new List<byte>(s.PaletteTintSlots);
        var textures = new List<ushort>(s.PaletteTextureSlots);
        var minted = new Dictionary<(int Pid, int Bucket), int>();

        for (int r = 0; r < runs.Length; r++)
        {
            int depth = depths[r];
            if (depth <= 0) continue;
            int pid = LodSection.RunPaletteId(runs[r]);
            if (pid < 0 || pid >= s.PaletteFlags.Length) continue;

            int bucket = BucketOf(depth, buckets);
            if (!minted.TryGetValue((pid, bucket), out int replacement))
            {
                replacement = colors.Count;
                colors.Add(s.PaletteColors[pid]);
                flags.Add(s.PaletteFlags[pid]);
                tints.Add(s.PaletteTintSlots[pid]);
                if (textures.Count > 0) textures.Add(
                    pid < s.PaletteTextureSlots.Length ? s.PaletteTextureSlots[pid] : (ushort)0);
                minted[(pid, bucket)] = replacement;
            }
            runs[r] = LodSection.PackRun(
                replacement, LodSection.RunYTop(runs[r]), LodSection.RunYBottom(runs[r]));
            moved++;
        }

        return new SectionSnapshot
        {
            Runs = runs,
            ColumnStart = s.ColumnStart,
            Captured = s.Captured,
            PaletteColors = colors.ToArray(),
            PaletteFlags = flags.ToArray(),
            PaletteTintSlots = tints.ToArray(),
            PaletteTextureSlots = textures.ToArray(),
        };
    }

    /// <summary>
    /// Which brightness step a depth falls in. Uniform over 1..MaxDepth because the survival
    /// light ramp is affine in the level, so equal depth steps are equal brightness steps.
    /// </summary>
    static int BucketOf(int depth, int buckets) =>
        Math.Clamp((depth - 1) * buckets / MaxDepth, 0, buckets - 1);

    /// <summary>The bed's share of shore brightness at this depth, on the survival ramp.</summary>
    static double Brightness(int depth)
    {
        const int SkyExposed = 22;
        double At(int level) => level <= 0 ? 0.03 : 0.176 + 0.03 * (level - 1);
        return At(Math.Max(0, SkyExposed - depth)) / At(SkyExposed);
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
            "VintagestoryData", "ModData", "vintagehorizons");
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
            Path.GetTempPath(), "vh-waterdepth-" + Guid.NewGuid().ToString("N"));
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
                case "waterdepthfield": break;
                case "--help":
                case "-h": o.Help = true; break;
                case "--cache": o.Cache = Next(args, ref i); break;
                case "--samples": o.Samples = int.Parse(Next(args, ref i)); break;
                case "--seed": o.Seed = int.Parse(Next(args, ref i)); break;
                case "--level": o.Level = int.Parse(Next(args, ref i)); break;
                case "--cull": o.Cull = int.Parse(Next(args, ref i)); break;
                case "--buckets":
                    o.Buckets = Next(args, ref i)
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(int.Parse)
                        .Where(n => n >= 1)
                        .ToArray();
                    break;
                default: throw new ArgumentException($"unknown argument '{args[i]}'");
            }
        }
        if (o.Buckets.Length == 0) throw new ArgumentException("--buckets needs at least one count");
        return o;
    }

    static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"'{args[i]}' needs a value");
        return args[++i];
    }

    static void PrintHelp()
    {
        Console.WriteLine();
        Console.WriteLine("  waterdepthfield - what it costs to darken submerged ground by depth");
        Console.WriteLine();
        Console.WriteLine("  --cache <path>     client cache database (default: newest)");
        Console.WriteLine("  --samples <n>      sections to mesh (default 120)");
        Console.WriteLine("  --seed <n>         sample seed");
        Console.WriteLine("  --level <n>        LOD level to sample (default 0)");
        Console.WriteLine("  --cull <blocks>    cave cull reach (default 64)");
        Console.WriteLine("  --buckets a,b,c    depth resolutions to price (default 2,4,8,16,24)");
        Console.WriteLine();
    }
}
