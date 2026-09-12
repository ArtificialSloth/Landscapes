using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VintageHorizons;

/// <summary>
/// One chiselled block found in a captured column: where it is (column-local, see
/// <see cref="LodChiselStandIn.IndexOf"/>), the block id the column holds there, and a copy of
/// what it is made of. A material the distant terrain cannot draw as ground is already 0.
/// </summary>
public readonly record struct LodChiselRecord(int Index, int ChiselBlockId, uint[] Cuboids, int[] Materials);

/// <summary>
/// A chiselled block drawn at distance as the material that covers most of its outside.
///
/// Distant terrain is one colour per block, and a chiselled block has no colour of its own:
/// its look is the materials stored in its block entity, which only the world holding the
/// chunk can read. A server capture has no texture atlas, so its sections reached the client
/// as the bare chisel block type, whose own texture is the game's missing-texture image -
/// near-white. Ruins are built from thousands of them, so whole towers drew white. Measured on
/// the owner's cache in 0.4.61: 2,035 of 2,387 level-0 microblock entries near-white, and
/// 2,078 tall structure columns white against 62 correct.
///
/// So capture reads the block entity while the chunk is loaded, and the column records the
/// material instead of the chisel. Server and client then store the same ordinary block,
/// every existing colour, tint and texture path handles it, and nothing on disk or on the
/// wire changes shape - a palette code is a block code either way.
///
/// "Most visible" is exposed surface: voxel faces that touch air or the edge of the block,
/// counted per material and summed per block. A plank skin over a stone core draws as planks
/// although stone fills most of it; a stone block with a thin plank trim still draws as
/// stone. Neighbouring blocks are not consulted - the block is judged on its own.
/// </summary>
public static class LodChiselStandIn
{
    const int ChunkSize = GlobalConstants.ChunkSize;
    const int Size = 16;

    /// <summary>
    /// Column-local position of a block: absolute y, then z and x inside the chunk column.
    /// The same key <see cref="Collect"/> writes and the capture worker reads.
    /// </summary>
    public static int IndexOf(int lx, int y, int lz) => (y * ChunkSize + lz) * ChunkSize + lx;

    /// <summary>
    /// A run's top block in a capture result: section column index and top y (exclusive, as
    /// in <see cref="LodSection.RunYTop"/>). Fourteen bits of y below twelve of column.
    /// </summary>
    public static int RunKey(int col, int yTop) => (col << 14) | (yTop & 0x3FFF);

    /// <summary>
    /// Copy what every chiselled block in a column is made of. Call where the capture job is
    /// created, never on the worker: a chunk's block-entity dictionary belongs to the thread
    /// that owns the chunk, and the copy lets the worker do the arithmetic without it.
    /// <paramref name="isChisel"/> confirms the block at the entity's position is a chisel,
    /// so a stale entity over an ordinary block is ignored. <paramref name="standsIn"/> says
    /// whether a material block may stand in for a chisel; one that may not is recorded as 0,
    /// so the worker needs no block registry.
    /// </summary>
    public static List<LodChiselRecord>? Collect(IWorldChunk?[] chunks, int chunkX, int chunkZ,
        System.Func<int, bool> isChisel, System.Func<int, bool> standsIn)
    {
        List<LodChiselRecord>? found = null;
        int baseX = chunkX * ChunkSize;
        int baseZ = chunkZ * ChunkSize;

        for (int cy = 0; cy < chunks.Length; cy++)
        {
            IWorldChunk? chunk = chunks[cy];
            if (chunk == null || chunk.Disposed) continue;
            Dictionary<BlockPos, BlockEntity>? entities = chunk.BlockEntities;
            if (entities == null || entities.Count == 0) continue;

            foreach (BlockEntity entity in entities.Values)
            {
                if (entity is not BlockEntityMicroBlock chisel || chisel.Pos == null) continue;
                List<uint>? cuboids = chisel.VoxelCuboids;
                int[]? ids = chisel.BlockIds;
                if (cuboids == null || cuboids.Count == 0 || ids == null || ids.Length == 0) continue;

                int lx = chisel.Pos.X - baseX;
                int lz = chisel.Pos.Z - baseZ;
                int y = chisel.Pos.Y;
                if (lx < 0 || lx >= ChunkSize || lz < 0 || lz >= ChunkSize || y / ChunkSize != cy) continue;

                // The id the worker will read at this position, read the same way. A block
                // replaced between now and the worker's read then simply does not match.
                int blockId = chunk.UnpackAndReadBlock(
                    ((y % ChunkSize) * ChunkSize + lz) * ChunkSize + lx, BlockLayersAccess.FluidOrSolid);
                if (blockId <= 0 || !isChisel(blockId)) continue;

                var materials = new int[ids.Length];
                for (int i = 0; i < ids.Length; i++)
                {
                    materials[i] = ids[i] > 0 && standsIn(ids[i]) ? ids[i] : 0;
                }

                (found ??= new List<LodChiselRecord>()).Add(
                    new LodChiselRecord(IndexOf(lx, y, lz), blockId, cuboids.ToArray(), materials));
            }
        }

        return found;
    }

    /// <summary>
    /// The stand-in for each recorded chisel, by column-local index: the chisel's own block
    /// id, so the worker can confirm it is reading the same block, and the material to record
    /// instead. <paramref name="withoutMaterial"/> counts chisels with no eligible material
    /// showing; those stay chisels and take the placeholder repair's neutral grey.
    /// </summary>
    public static Dictionary<int, (int Chisel, int StandIn)>? Resolve(
        List<LodChiselRecord>? records, out int withoutMaterial)
    {
        withoutMaterial = 0;
        if (records == null || records.Count == 0) return null;

        var byIndex = new Dictionary<int, (int Chisel, int StandIn)>(records.Count);
        foreach (LodChiselRecord record in records)
        {
            int standIn = CachedStandIn(record.Cuboids, record.Materials);
            if (standIn > 0) byIndex[record.Index] = (record.ChiselBlockId, standIn);
            else withoutMaterial++;
        }
        return byIndex;
    }

    /// <summary>
    /// The block id to draw a chisel as: the eligible material with the most exposed faces,
    /// summed over every index that names the same block. Ties go to the earlier material,
    /// which is the one the game itself reports first. 0 when no eligible material shows.
    /// </summary>
    public static int StandInFor(ReadOnlySpan<uint> cuboids, ReadOnlySpan<int> materials)
    {
        if (cuboids.IsEmpty || materials.IsEmpty) return 0;
        long[] faces = ExposedFaces(cuboids, materials.Length);

        int best = 0;
        long bestFaces = 0;
        for (int i = 0; i < materials.Length; i++)
        {
            int id = materials[i];
            if (id <= 0) continue;

            // Each block once, at its first index, carrying every index that names it.
            bool counted = false;
            for (int j = 0; j < i && !counted; j++) counted = materials[j] == id;
            if (counted) continue;

            long total = 0;
            for (int j = i; j < materials.Length; j++)
            {
                if (materials[j] == id) total += faces[j];
            }
            if (total > bestFaces)
            {
                best = id;
                bestFaces = total;
            }
        }
        return best;
    }

    /// <summary>
    /// Exposed voxel faces per material index: faces that touch the edge of the block or a
    /// voxel nothing fills. Every cuboid occupies space, eligible or not, so an ineligible
    /// material still hides what is behind it. Cuboids are the engine's own encoding and do
    /// not overlap; a material index past <paramref name="materialCount"/> is ignored.
    /// </summary>
    public static long[] ExposedFaces(ReadOnlySpan<uint> cuboids, int materialCount)
    {
        var faces = new long[materialCount];
        Span<ulong> filled = stackalloc ulong[Size * Size * Size / 64];
        filled.Clear();

        foreach (uint packed in cuboids)
        {
            if (!Decode(packed, out int x1, out int y1, out int z1,
                out int x2, out int y2, out int z2, out _)) continue;
            for (int y = y1; y < y2; y++)
            {
                for (int z = z1; z < z2; z++)
                {
                    for (int x = x1; x < x2; x++) Fill(filled, x, y, z);
                }
            }
        }

        foreach (uint packed in cuboids)
        {
            if (!Decode(packed, out int x1, out int y1, out int z1,
                out int x2, out int y2, out int z2, out int material)) continue;
            if (material >= materialCount) continue;

            long exposed = 0;
            for (int y = y1; y < y2; y++)
            {
                for (int z = z1; z < z2; z++)
                {
                    if (x1 == 0 || !IsFilled(filled, x1 - 1, y, z)) exposed++;
                    if (x2 == Size || !IsFilled(filled, x2, y, z)) exposed++;
                }
            }
            for (int z = z1; z < z2; z++)
            {
                for (int x = x1; x < x2; x++)
                {
                    if (y1 == 0 || !IsFilled(filled, x, y1 - 1, z)) exposed++;
                    if (y2 == Size || !IsFilled(filled, x, y2, z)) exposed++;
                }
            }
            for (int y = y1; y < y2; y++)
            {
                for (int x = x1; x < x2; x++)
                {
                    if (z1 == 0 || !IsFilled(filled, x, y, z1 - 1)) exposed++;
                    if (z2 == Size || !IsFilled(filled, x, y, z2)) exposed++;
                }
            }
            faces[material] += exposed;
        }

        return faces;
    }

    /// <summary>
    /// The engine's cuboid encoding (BlockEntityMicroBlock.FromUint): four bits per corner
    /// coordinate, the far corner stored minus one, and the material index in the top byte.
    /// False for a cuboid with no volume, which a well-formed block never holds.
    /// </summary>
    public static bool Decode(uint packed, out int x1, out int y1, out int z1,
        out int x2, out int y2, out int z2, out int material)
    {
        x1 = (int)(packed & 0xF);
        y1 = (int)((packed >> 4) & 0xF);
        z1 = (int)((packed >> 8) & 0xF);
        x2 = (int)((packed >> 12) & 0xF) + 1;
        y2 = (int)((packed >> 16) & 0xF) + 1;
        z2 = (int)((packed >> 20) & 0xF) + 1;
        material = (int)((packed >> 24) & 0xFF);
        return x2 > x1 && y2 > y1 && z2 > z1;
    }

    static void Fill(Span<ulong> bits, int x, int y, int z)
    {
        int i = (y * Size + z) * Size + x;
        bits[i >> 6] |= 1UL << (i & 63);
    }

    static bool IsFilled(ReadOnlySpan<ulong> bits, int x, int y, int z)
    {
        int i = (y * Size + z) * Size + x;
        return (bits[i >> 6] & (1UL << (i & 63))) != 0;
    }

    /// <summary>
    /// Structures repeat the same chiselled shapes hundreds of times, so answers are kept by
    /// content. Per thread, because the production caller is the single capture thread and a
    /// check must not share its state; cleared rather than evicted when it fills.
    /// </summary>
    [ThreadStatic] static Dictionary<ulong, int>? standInByShape;
    const int ShapeCacheLimit = 16384;

    static int CachedStandIn(uint[] cuboids, int[] materials)
    {
        ulong key = ShapeKey(cuboids, materials);
        Dictionary<ulong, int> cache = standInByShape ??= new Dictionary<ulong, int>();
        if (cache.TryGetValue(key, out int known)) return known;

        int standIn = StandInFor(cuboids, materials);
        if (cache.Count >= ShapeCacheLimit) cache.Clear();
        cache[key] = standIn;
        return standIn;
    }

    /// <summary>FNV-1a over the cuboids and the materials, lengths included.</summary>
    static ulong ShapeKey(uint[] cuboids, int[] materials)
    {
        ulong hash = 14695981039346656037UL;
        hash = Mix(hash, (uint)cuboids.Length);
        foreach (uint cuboid in cuboids) hash = Mix(hash, cuboid);
        hash = Mix(hash, (uint)materials.Length);
        foreach (int material in materials) hash = Mix(hash, (uint)material);
        return hash;
    }

    static ulong Mix(ulong hash, uint value)
    {
        for (int shift = 0; shift < 32; shift += 8)
        {
            hash ^= (value >> shift) & 0xFF;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}
