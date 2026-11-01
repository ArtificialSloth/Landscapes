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
    // One palette block id for every leaf, one for everything else, standing in for the table
    // a running game builds from its block registry.
    const int LeafBlockId = 1;
    static readonly bool[] LeafTable = { false, true };

    public static int Run(string[] args)
    {
        string? cache = null;
        int samples = 60, seed = 20261101;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--cache") cache = args[++i];
            else if (args[i] == "--samples") samples = int.Parse(args[++i]);
            else if (args[i] == "--seed") seed = int.Parse(args[++i]);
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
            return Measure(conn, cache, samples, seed);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    static int Measure(SqliteConnection conn, string cache, int samples, int seed)
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

                MeshResult mesh = LodMesher.BuildMesh(new MeshJob
                {
                    Key = key,
                    Self = SectionSnapshot.Of(self, crossedShapes: _ => LodBlockPolicy.CrossedShapeByFlags,
                        leafPlanes: leafPlanes),
                    Neighbors = neighbours,
                    CaveCullReach = 64,
                    EmitTextureSlots = true,
                    LeafShape = shapes[s],
                });
                quads[s] += mesh.PackedOpaqueQuadCount;
                for (int q = 0; q < mesh.PackedOpaqueQuadCount; q++)
                {
                    if (LodPackedQuadFormat.FaceOf(mesh.PackedOpaqueQuads, q) >= LodPackedFace.DiagonalRising)
                        planes[s]++;
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
        return 0;
    }

    /// <summary>Every leaf one block id with a texture slot, so the snapshots read it as a running game would.</summary>
    static void MarkLeaves(LodSection section)
    {
        string[]? codes = section.PendingPaletteCodes;
        for (int i = 0; i < section.Palette.Count; i++)
        {
            string? code = codes != null && i < codes.Length ? codes[i] : null;
            int colon = code?.IndexOf(':') ?? -1;
            bool leaf = code != null && code.Substring(colon + 1).StartsWith("leaves", StringComparison.Ordinal);
            LodPaletteEntry e = section.Palette[i];
            e.BlockId = leaf ? LeafBlockId : 0;
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
