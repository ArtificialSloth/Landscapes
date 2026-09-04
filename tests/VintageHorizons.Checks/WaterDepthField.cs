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
/// WHAT IT COSTS. The mesher merges neighbouring quads that share a palette entry, so giving
/// every column its own brightness stops quads at different depths merging and the bed grows
/// geometry. Bucketing the depth would bound that; it turned out not to be worth doing, because
/// a bed that changes depth is a bed that changes height and those quads were not merging
/// anyway. The exact per-block gradient measured +0.24%, so there is no ladder here any more.
///
/// HOW IT MEASURES. Not by counting faces itself - that would measure a reimplementation. Each
/// sampled section is meshed twice by the SHIPPING mesher, with and without the absorption
/// table the renderer supplies, and the difference is the answer. The depth histogram is walked
/// independently of the mesher's own walk, so the two disagreeing is a signal rather than a
/// coincidence.
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

    /// <summary>Water patch sizes to price, in blocks. 1 is one quad per block.</summary>
    static readonly int[] WaveCells = { 1, 2, 4, 8 };

    /// <summary>
    /// Half-widths, in blocks, of a per-block strip either side of every 32-block chunk plane.
    /// 1 means the two columns touching the plane; 3 means six columns.
    /// </summary>
    static readonly int[] SeamStrips = { 1, 2, 3 };

    sealed class Options
    {
        public string? Cache;
        public int Samples = 120;
        public int Seed = 20260903;
        public int Level;
        public int Cull = 64;
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
        // Water is a SEPARATE buffer from opaque and VertexCount does not include it, so a
        // water-only change is invisible unless both are counted. Learned the hard way: an
        // experiment that disabled the water surface split produced byte-identical totals.
        long shadedVerts = 0, shadedQuads = 0;
        long baseWaterVerts = 0, shadedWaterVerts = 0;
        var waveWaterVerts = new long[WaveCells.Length];
        var stripWaterVerts = new long[SeamStrips.Length];
        var meshClock = new System.Diagnostics.Stopwatch();
        int meshed = 0, withWater = 0;

        // The world's own ramp is not reachable without a client, so the harness uses the
        // survival config's published levels. LodTerrainRenderer reads the live world instead;
        // a server that replaces the ramp changes the shade, not the geometry this measures.
        var absorption = new float[LodMesher.MaxSubmergedDepth + 1];
        for (int d = 0; d < absorption.Length; d++) absorption[d] = (float)Brightness(d);

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

            meshClock.Start();
            MeshResult baseline = LodMesher.BuildMesh(new MeshJob
            {
                Key = key,
                Self = plain,
                Neighbors = neighbours,
                CaveCullReach = o.Cull,
            });

            meshClock.Stop();
            meshed++;
            if (anyWater) withWater++;
            baseVerts += baseline.VertexCount;
            baseWaterVerts += baseline.WaterVertexCount;
            baseQuads += baseline.ClusteredPackedOpaqueQuads.Length
                / LodPackedQuadFormat.WordsPerQuad;

            // The SHIPPING path, not a simulation of it: the same mesher with the same input
            // and the absorption table supplied, which is exactly what the renderer hands it.
            MeshResult shaded = LodMesher.BuildMesh(new MeshJob
            {
                Key = key,
                Self = plain,
                Neighbors = neighbours,
                CaveCullReach = o.Cull,
                WaterAbsorption = absorption,
            });
            // What it would cost to stop merging water, so the surface can carry vanilla's
            // wave per block instead of interpolating it across a quad tens of blocks wide.
            // Simulated by giving every column's water run a palette entry of its own, which
            // is precisely what stops the greedy pass joining two columns.
            for (int c = 0; c < WaveCells.Length; c++)
            {
                MeshResult unmerged = LodMesher.BuildMesh(new MeshJob
                {
                    Key = key,
                    Self = WaterPerColumn(plain, WaveCells[c], 0),
                    Neighbors = neighbours,
                    CaveCullReach = o.Cull,
                    WaterAbsorption = absorption,
                });
                waveWaterVerts[c] += unmerged.WaterVertexCount;
            }

            // The seam can only ever lie on a 32-block chunk plane, because ownership is
            // decided per vanilla chunk - so a narrow strip around that grid is a STATIC
            // property of the section, needing no view and no re-mesh.
            for (int c = 0; c < SeamStrips.Length; c++)
            {
                // The SHIPPING flag, not a simulation of it - this is the number the mod
                // actually pays, built by the mesher the game runs.
                MeshResult strip = LodMesher.BuildMesh(new MeshJob
                {
                    Key = key,
                    Self = plain,
                    Neighbors = neighbours,
                    CaveCullReach = o.Cull,
                    WaterAbsorption = absorption,
                    WaterSeamStripBlocks = SeamStrips[c],
                });
                stripWaterVerts[c] += strip.WaterVertexCount;
            }

            shadedVerts += shaded.VertexCount;
            shadedWaterVerts += shaded.WaterVertexCount;
            shadedQuads += shaded.ClusteredPackedOpaqueQuads.Length
                / LodPackedQuadFormat.WordsPerQuad;

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

        Console.WriteLine("  geometry cost of the exact per-block gradient, shipping mesher");
        Console.WriteLine($"  absorption off     {baseVerts,12:n0} vertices   "
            + $"{baseQuads,10:n0} clustered quads");
        Console.WriteLine($"  absorption on      {shadedVerts,12:n0} vertices   "
            + $"{shadedQuads,10:n0} quads   "
            + $"verts {Delta(baseVerts, shadedVerts)}");
        Console.WriteLine($"  water off          {baseWaterVerts,12:n0} water vertices");
        Console.WriteLine($"  water on           {shadedWaterVerts,12:n0} water vertices   "
            + $"{Delta(baseWaterVerts, shadedWaterVerts)}");
        Console.WriteLine();
        Console.WriteLine("  cost of splitting water so its surface can carry vanilla's wave");
        Console.WriteLine("  vanilla's liquid warp has a wavelength of about 3 blocks, so a patch");
        Console.WriteLine("  wider than 1-2 blocks samples it below Nyquist and flattens it.");
        Console.WriteLine();
        Console.WriteLine($"  merged (today)     {shadedWaterVerts,12:n0} water vertices");
        for (int c = 0; c < WaveCells.Length; c++)
        {
            Console.WriteLine($"  {WaveCells[c],2}x{WaveCells[c]} patches      {waveWaterVerts[c],12:n0} water vertices   "
                + $"{Delta(shadedWaterVerts, waveWaterVerts[c])}   "
                + $"whole mesh +{Share(waveWaterVerts[c] - shadedWaterVerts, shadedVerts + shadedWaterVerts):0.00}%");
        }
        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine("  per-block water only within N blocks of a 32-block chunk plane");
        Console.WriteLine("  (the seam can land nowhere else, so this needs no view and no re-mesh)");
        for (int c = 0; c < SeamStrips.Length; c++)
        {
            Console.WriteLine($"  strip +/-{SeamStrips[c]}         {stripWaterVerts[c],12:n0} water vertices   "
                + $"{Delta(shadedWaterVerts, stripWaterVerts[c])}   "
                + $"whole mesh +{Share(stripWaterVerts[c] - shadedWaterVerts, shadedVerts + shadedWaterVerts):0.00}%");
        }
        Console.WriteLine();
        Console.WriteLine($"  mesh build time    {meshClock.Elapsed.TotalMilliseconds / Math.Max(1, meshed):0.00} ms "
            + "per section, one pass (Release, no game)");
        Console.WriteLine();
        Console.WriteLine($"  combined           {baseVerts + baseWaterVerts,12:n0} -> "
            + $"{shadedVerts + shadedWaterVerts:n0}   "
            + $"{Delta(baseVerts + baseWaterVerts, shadedVerts + shadedWaterVerts)}");
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
    /// The same section with every water run on a palette entry unique to its column, so the
    /// greedy pass cannot join two columns and every water block emits its own quad.
    ///
    /// This is the shape a wave-carrying surface needs. Vanilla evaluates its liquid warp at
    /// every block corner; a merged quad spanning tens of blocks can only interpolate the wave
    /// straight across its middle, and the wave's own wavelength is about three blocks. Colours
    /// and flags are copied unchanged - this measures the merge, nothing else.
    /// </summary>
    static SectionSnapshot WaterPerColumn(SectionSnapshot s, int cell, int seamStrip)
    {
        var runs = (ulong[])s.Runs.Clone();
        var colors = new List<int>(s.PaletteColors);
        var flags = new List<byte>(s.PaletteFlags);
        var tints = new List<byte>(s.PaletteTintSlots);
        var textures = new List<ushort>(s.PaletteTextureSlots);
        int gs = LodSection.GridSize;
        var minted = new Dictionary<(int Pid, int Cell), int>();

        for (int col = 0; col < gs * gs; col++)
        {
            // Which NxN patch of the section this column falls in. cell 1 is one quad per
            // block; larger patches merge inside themselves and sample the wave that much
            // more coarsely.
            int localX = col % gs;
            int localZ = col / gs;

            // Sections are 64 blocks and 64-aligned, so the vanilla chunk planes inside one
            // land at local 0 and 32. Outside the strip, water keeps merging normally.
            if (seamStrip > 0)
            {
                int dx = Math.Min(localX % 32, 32 - localX % 32);
                int dz = Math.Min(localZ % 32, 32 - localZ % 32);
                if (dx >= seamStrip && dz >= seamStrip) continue;
            }

            int patch = (localZ / cell) * gs + (localX / cell);
            for (int r = s.ColumnStart[col]; r < s.ColumnStart[col + 1]; r++)
            {
                int pid = LodSection.RunPaletteId(runs[r]);
                if (pid < 0 || pid >= s.PaletteFlags.Length) continue;
                if ((s.PaletteFlags[pid] & LodPaletteEntry.FlagWater) == 0) continue;
                if (minted.TryGetValue((pid, patch), out int existing))
                {
                    runs[r] = LodSection.PackRun(
                        existing, LodSection.RunYTop(runs[r]), LodSection.RunYBottom(runs[r]));
                    continue;
                }

                int replacement = colors.Count;
                colors.Add(s.PaletteColors[pid]);
                flags.Add(s.PaletteFlags[pid]);
                tints.Add(s.PaletteTintSlots[pid]);
                if (textures.Count > 0) textures.Add(
                    pid < s.PaletteTextureSlots.Length ? s.PaletteTextureSlots[pid] : (ushort)0);
                minted[(pid, patch)] = replacement;
                runs[r] = LodSection.PackRun(
                    replacement, LodSection.RunYTop(runs[r]), LodSection.RunYBottom(runs[r]));
            }
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
    /// The bed's share of shore brightness at this depth, on the survival ramp. Water absorbs
    /// one light level per block and the ramp is 0.176 at level 1 rising 0.03 a level, so this
    /// is the factor LodMesher.SubmergedColor multiplies a stored colour by.
    /// </summary>
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
                default: throw new ArgumentException($"unknown argument '{args[i]}'");
            }
        }
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
        Console.WriteLine();
    }
}
