using Microsoft.Data.Sqlite;

namespace VintageHorizons.Checks;

/// <summary>
/// What half-block terrain costs, measured against a real cache with no game process.
///
/// WHY THIS EXISTS. Terrain-slab mods replace the top block of a smoothed step with a block
/// half a cell tall. Capture stores whole-block runs, so the LOD used to rebuild every one
/// of them as a full cube - putting back exactly the stair-steps the mod exists to remove,
/// and standing the ground half a block too high wherever one sits. Lowering that surface
/// is a rendering gain and a geometry cost in the same change: a slab beside a full block
/// exposes the top half of that block's wall, and those walls did not exist before.
///
/// HOW IT MEASURES. Not by counting faces itself - that would measure a reimplementation.
/// Each sampled section is meshed TWICE by the shipping mesher, once with the half-block
/// flag off and once with it on, with the same four neighbours flagged the same way so no
/// false wall appears along a section edge. The difference is the answer and both sides of
/// it come from shipping code.
///
/// HOW IT KNOWS WHAT A SLAB IS. In the game this is the block's own collision shape (see
/// LodBlockPolicy). There is no block registry here, so the harness matches stored palette
/// CODES by domain prefix instead - "terrainslabs:" by default. That is a stand-in for the
/// shape test, not the same rule: exact for the mod this was built for and blind to any
/// other, which is why the shape test is what ships.
/// </summary>
public static class SlabField
{
    sealed class Options
    {
        public string? Cache;
        public int Samples = 60;
        public int Seed = 20260828;
        public int Level;
        public int Cull = 64;
        public int HalfLevels = 99;
        public bool SurfaceOnly;
        public string Prefix = "terrainslabs:";
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

        long baseVerts = 0, slabVerts = 0, baseWater = 0, slabWater = 0;
        long baseClusterQuads = 0, slabClusterQuads = 0;
        long baseClusters = 0, slabClusters = 0;
        long baseEarlyBackfaces = 0, slabEarlyBackfaces = 0;
        long baseFlat = 0, slabFlat = 0, baseWall = 0, slabWall = 0;
        var baseFaces = new FaceCounts();
        var slabFaces = new FaceCounts();
        long buriedSlabs = 0, exposedSlabs = 0;
        long columns = 0, slabTopped = 0;
        int meshed = 0, withSlabs = 0;

        foreach (long key in sample)
        {
            LodSection? self = sections.Get(key);
            if (self == null) continue;

            SectionSnapshot plainSelf = SectionSnapshot.Of(self);
            SectionSnapshot halfSelf = Flagged(self, o.Prefix, out bool anyHere);
            var plainNeighbours = new SectionSnapshot?[4];
            var halfNeighbours = new SectionSnapshot?[4];
            for (int side = 0; side < 4; side++)
            {
                LodSection? nb = sections.Get(Neighbour(key, side));
                if (nb == null) continue;
                plainNeighbours[side] = SectionSnapshot.Of(nb);
                halfNeighbours[side] = Flagged(nb, o.Prefix, out _);
            }

            MeshResult before = LodMesher.BuildMesh(new MeshJob
            {
                Key = key,
                Self = plainSelf,
                Neighbors = plainNeighbours,
                CaveCullReach = o.Cull,
            });
            MeshResult after = LodMesher.BuildMesh(new MeshJob
            {
                Key = key,
                Self = halfSelf,
                Neighbors = halfNeighbours,
                CaveCullReach = o.Cull,
                // Every level by default: the question this harness answers is what the
                // half-block surface costs at the level being sampled, not what the
                // shipping level gate chooses to pay for.
                HalfBlockLevels = o.HalfLevels,
                SurfaceHalfBlocksOnly = o.SurfaceOnly,
            });

            meshed++;
            if (anyHere) withSlabs++;
            baseVerts += before.VertexCount;
            slabVerts += after.VertexCount;
            baseClusterQuads += before.ClusteredPackedOpaqueQuads.Length
                / LodPackedQuadFormat.WordsPerQuad;
            slabClusterQuads += after.ClusteredPackedOpaqueQuads.Length
                / LodPackedQuadFormat.WordsPerQuad;
            baseClusters += before.PackedOpaqueClusters.Length;
            slabClusters += after.PackedOpaqueClusters.Length;
            baseEarlyBackfaces += PackedVerticalBackfacesFromSectionCenter(
                before.ClusteredPackedOpaqueQuads);
            slabEarlyBackfaces += PackedVerticalBackfacesFromSectionCenter(
                after.ClusteredPackedOpaqueQuads);
            Split(before.Xyz, ref baseFlat, ref baseWall);
            Split(after.Xyz, ref slabFlat, ref slabWall);
            Classify(before.Xyz, baseFaces);
            Classify(after.Xyz, slabFaces);
            CountBuried(self, halfSelf, ref buriedSlabs, ref exposedSlabs);
            baseWater += before.WaterVertexCount;
            slabWater += after.WaterVertexCount;
            CountSurface(self, halfSelf, ref columns, ref slabTopped);
            sections.Forget(key);
        }

        Console.WriteLine();
        Console.WriteLine("  half-block terrain, measured against a real cache");
        Console.WriteLine("  cache:   " + cache);
        Console.WriteLine($"  sample:  {meshed} level-{o.Level} sections, "
            + $"{withSlabs} holding a '{o.Prefix}' block, cave reach {o.Cull}, "
            + (o.SurfaceOnly ? "surface slabs only" : "all slab runs"));
        Console.WriteLine();
        Console.WriteLine($"  captured columns        {columns,12:n0}");
        Console.WriteLine($"  ending in a half block  {slabTopped,12:n0}   "
            + $"{Share(slabTopped, columns):0.00}% of the surface");
        Console.WriteLine();
        Console.WriteLine($"  opaque vertices  {baseVerts,12:n0} -> {slabVerts,12:n0}   "
            + Delta(baseVerts, slabVerts));
        Console.WriteLine($"  opaque quads     {baseVerts / 4,12:n0} -> {slabVerts / 4,12:n0}   "
            + Delta(baseVerts / 4, slabVerts / 4));
        Console.WriteLine($"  clustered quads  {baseClusterQuads,12:n0} -> {slabClusterQuads,12:n0}   "
            + Delta(baseClusterQuads, slabClusterQuads));
        Console.WriteLine($"  draw clusters    {baseClusters,12:n0} -> {slabClusters,12:n0}   "
            + Delta(baseClusters, slabClusters));
        Console.WriteLine($"  early wall exits {baseEarlyBackfaces,12:n0} -> {slabEarlyBackfaces,12:n0}   "
            + Delta(baseEarlyBackfaces, slabEarlyBackfaces));
        Console.WriteLine($"  of which flat    {baseFlat,12:n0} -> {slabFlat,12:n0}   "
            + Delta(baseFlat, slabFlat));
        Console.WriteLine($"  of which walls   {baseWall,12:n0} -> {slabWall,12:n0}   "
            + Delta(baseWall, slabWall));
        Console.WriteLine($"  top quads        {baseFaces.Top,12:n0} -> {slabFaces.Top,12:n0}   "
            + Delta(baseFaces.Top, slabFaces.Top));
        Console.WriteLine($"  bottom quads     {baseFaces.Bottom,12:n0} -> {slabFaces.Bottom,12:n0}   "
            + Delta(baseFaces.Bottom, slabFaces.Bottom));
        Console.WriteLine($"  X-wall quads     {baseFaces.XWall,12:n0} -> {slabFaces.XWall,12:n0}   "
            + Delta(baseFaces.XWall, slabFaces.XWall));
        Console.WriteLine($"  Z-wall quads     {baseFaces.ZWall,12:n0} -> {slabFaces.ZWall,12:n0}   "
            + Delta(baseFaces.ZWall, slabFaces.ZWall));
        Console.WriteLine($"  water vertices   {baseWater,12:n0} -> {slabWater,12:n0}   "
            + Delta(baseWater, slabWater));
        Console.WriteLine();
        Console.WriteLine($"  slab runs with open sky above  {exposedSlabs,12:n0}");
        Console.WriteLine($"  slab runs buried under a block {buriedSlabs,12:n0}");
        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// A snapshot whose palette carries the half-block flag wherever the stored code names
    /// a slab. The snapshot owns its own flags array, so the original section is untouched
    /// and the two meshes differ in exactly this one byte per palette entry.
    /// </summary>
    static SectionSnapshot Flagged(LodSection section, string prefix, out bool any)
    {
        SectionSnapshot snap = SectionSnapshot.Of(section);
        string[]? codes = section.PendingPaletteCodes;
        any = false;
        if (codes == null) return snap;

        const byte NotTerrain = LodPaletteEntry.FlagWater
            | LodPaletteEntry.FlagThin
            | LodPaletteEntry.FlagSkip;

        for (int i = 0; i < snap.PaletteFlags.Length && i < codes.Length; i++)
        {
            string? code = codes[i];
            if (code == null || !code.StartsWith(prefix, StringComparison.Ordinal)) continue;

            // Water, cover and skipped blocks are classified before the shape test in the
            // shipping policy, so they can never carry this flag; mirror that here.
            if ((snap.PaletteFlags[i] & NotTerrain) != 0) continue;

            snap.PaletteFlags[i] |= LodPaletteEntry.FlagHalf;
            any = true;
        }
        return snap;
    }

    static void CountSurface(LodSection section, SectionSnapshot flagged,
        ref long columns, ref long slabTopped)
    {
        int total = LodSection.GridSize * LodSection.GridSize;
        for (int col = 0; col < total; col++)
        {
            Span<ulong> runs = section.ColumnRuns(col);
            if (runs.Length == 0) continue;
            columns++;
            int pid = LodSection.RunPaletteId(runs[0]);
            if (pid < flagged.PaletteFlags.Length
                && (flagged.PaletteFlags[pid] & LodPaletteEntry.FlagHalf) != 0)
            {
                slabTopped++;
            }
        }
    }

    /// <summary>Quads whose four corners share a Y are surfaces; the rest are walls.</summary>
    static void Split(float[]? xyz, ref long flat, ref long wall)
    {
        if (xyz == null) return;
        for (int v = 0; v + 12 <= xyz.Length; v += 12)
        {
            float y = xyz[v + 1];
            bool level = Math.Abs(xyz[v + 4] - y) < 0.0001f
                && Math.Abs(xyz[v + 7] - y) < 0.0001f
                && Math.Abs(xyz[v + 10] - y) < 0.0001f;
            if (level) flat += 4; else wall += 4;
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

    /// <summary>
    /// Packed vertical quads whose complete shader tail the new camera-side test avoids,
    /// with the camera placed at the centre of each section. This is an estimate rather
    /// than an FPS claim, but it counts the shipping clustered stream the GPU consumes.
    /// A distant section moves the dividing plane; it does not change the test itself.
    /// </summary>
    static long PackedVerticalBackfacesFromSectionCenter(uint[] packed)
    {
        const float camera = LodSection.GridSize * 0.5f;
        int quads = packed.Length / LodPackedQuadFormat.WordsPerQuad;
        long rejected = 0;
        for (int quad = 0; quad < quads; quad++)
        {
            LodPackedFace face = LodPackedQuadFormat.FaceOf(packed, quad);
            if (face is LodPackedFace.Bottom or LodPackedFace.Top) continue;
            LodPackedQuadFormat.DecodeVertex(packed, quad, 0, 1,
                out float x, out _, out float z, out _);
            bool back = face switch
            {
                LodPackedFace.West => x <= camera,
                LodPackedFace.East => x >= camera,
                LodPackedFace.North => z <= camera,
                LodPackedFace.South => z >= camera,
                _ => false,
            };
            if (back) rejected++;
        }
        return rejected;
    }

    /// <summary>
    /// Slab runs with something resting directly on top of them, against those with open
    /// space above. A buried one's half-block gap is sealed inside solid ground, so every
    /// face the shave adds around it is invisible - which makes it the obvious thing to cut
    /// if the cost needs to come down.
    /// </summary>
    static void CountBuried(LodSection section, SectionSnapshot flagged,
        ref long buried, ref long exposed)
    {
        int total = LodSection.GridSize * LodSection.GridSize;
        for (int col = 0; col < total; col++)
        {
            Span<ulong> runs = section.ColumnRuns(col);
            for (int r = 0; r < runs.Length; r++)
            {
                int pid = LodSection.RunPaletteId(runs[r]);
                if (pid >= flagged.PaletteFlags.Length
                    || (flagged.PaletteFlags[pid] & LodPaletteEntry.FlagHalf) == 0) continue;

                bool covered = r > 0
                    && LodSection.RunYBottom(runs[r - 1]) == LodSection.RunYTop(runs[r]);
                if (covered) buried++; else exposed++;
            }
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
            Path.GetTempPath(), "vh-slabfield-" + Guid.NewGuid().ToString("N"));
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
                case "slabfield": break;
                case "--help":
                case "-h": o.Help = true; break;
                case "--cache": o.Cache = Next(args, ref i); break;
                case "--prefix": o.Prefix = Next(args, ref i); break;
                case "--samples": o.Samples = int.Parse(Next(args, ref i)); break;
                case "--seed": o.Seed = int.Parse(Next(args, ref i)); break;
                case "--level": o.Level = int.Parse(Next(args, ref i)); break;
                case "--cull": o.Cull = int.Parse(Next(args, ref i)); break;
                case "--half-levels": o.HalfLevels = int.Parse(Next(args, ref i)); break;
                case "--surface-only": o.SurfaceOnly = true; break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        throw new ArgumentException("unknown option " + args[i]);
                    }
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
        Console.WriteLine("  slabfield - what half-block terrain costs, over a real cache");
        Console.WriteLine("    --cache <path>   cache database (default: newest client cache)");
        Console.WriteLine("    --prefix <text>  palette-code prefix that names a slab");
        Console.WriteLine("    --samples <n>    sections to mesh twice (default 60)");
        Console.WriteLine("    --seed <n>       sample seed");
        Console.WriteLine("    --level <n>      detail level to sample (default 0)");
        Console.WriteLine("    --cull <blocks>  cave-cull reach used for both meshes (default 64)");
        Console.WriteLine("    --half-levels <n>  levels that draw half blocks (default: all)");
        Console.WriteLine("    --surface-only     shorten only the highest solid run per column");
        Console.WriteLine();
    }
}
