using System.Buffers.Binary;
using System.IO.Compression;

namespace VintageHorizons.Silhouette;

/// <summary>
/// Where a known silhouette cell's height came from. A better source replaces a worse one in
/// the same cell, never the reverse (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER 4.2). The numeric
/// value is also the 2-bit wire code, so it must not be renumbered.
/// </summary>
public enum EnumSilhouetteOrigin : byte
{
    Unknown = 0,
    /// <summary>Real terrain the server captured.</summary>
    Captured = 1,
    /// <summary>The engine's own Terrain-pass preview; follows every installed worldgen mod.</summary>
    Preview = 2,
    /// <summary>Another mod's height sampler: fast, but a reimplementation.</summary>
    Accelerator = 3,
}

/// <summary>
/// One silhouette tile: the heights of a 512x512-block square at 4-block cells. That square is
/// one L3 section footprint and one Generate World tile, so the tile is keyed by the packed L3
/// section key (plan 4.3).
///
/// Pure data with no game types, so the fast tier can prove the chunk mapping, the source order
/// and the wire format. Cells are stored per chunk and allocated only when a chunk gets data:
/// sparse sampling fills four chunks of a tile's 256, and a dense 128x128 array per tile would
/// hold tens of megabytes of nothing across an 8 km reach.
/// </summary>
public sealed class LodSilhouetteTile
{
    public const int TileBlocks = 512;
    public const int CellBlocks = 4;
    public const int CellsPerEdge = TileBlocks / CellBlocks;
    public const int CellCount = CellsPerEdge * CellsPerEdge;
    public const int ChunkBlocks = 32;
    public const int ChunksPerEdge = TileBlocks / ChunkBlocks;
    public const int CellsPerChunk = ChunkBlocks / CellBlocks;
    public const int ChunkCells = CellsPerChunk * CellsPerChunk;
    public const int HeightMapLength = ChunkBlocks * ChunkBlocks;

    /// <summary>The L3 footprint: 64 blocks at level 0, doubled three times.</summary>
    public const int Level = 3;

    /// <summary>First byte of every payload. A reader refuses any other value.</summary>
    public const byte FormatVersion = 1;

    const int StateBytes = CellCount / 4;

    sealed class ChunkData
    {
        public readonly byte[] Origins = new byte[ChunkCells];
        public readonly ushort[] Heights = new ushort[ChunkCells];
    }

    readonly ChunkData?[] chunks = new ChunkData?[ChunksPerEdge * ChunksPerEdge];

    public LodSilhouetteTile(int tileX, int tileZ, int revision = 0)
    {
        TileX = tileX;
        TileZ = tileZ;
        Revision = revision;
    }

    /// <summary>L3 section coordinates: world block / 512, chunk / 16.</summary>
    public int TileX { get; }
    public int TileZ { get; }

    public long Key => KeyOf(TileX, TileZ);

    /// <summary>
    /// Server-owned. Incremented whenever a known cell is added or replaced, and carried by every
    /// message so a receiver can reject an older copy arriving late.
    /// </summary>
    public int Revision { get; private set; }

    public int KnownCells { get; private set; }

    public int BlockX => TileX * TileBlocks;
    public int BlockZ => TileZ * TileBlocks;

    public static long KeyOf(int tileX, int tileZ) => LodWorld.SectionKey(Level, tileX, tileZ);

    public static (int TileX, int TileZ) TileOfChunk(int cx, int cz) => (cx >> 4, cz >> 4);

    public static (int TileX, int TileZ) TileOfBlock(int x, int z) => (x >> 9, z >> 9);

    /// <summary>Better sources rank lower. Unknown ranks worst so any data replaces it.</summary>
    public static int Rank(EnumSilhouetteOrigin origin) =>
        origin == EnumSilhouetteOrigin.Unknown ? int.MaxValue : (int)origin;

    ChunkData? ChunkOfCell(int cellX, int cellZ) =>
        chunks[(cellZ / CellsPerChunk) * ChunksPerEdge + cellX / CellsPerChunk];

    static int IndexInChunk(int cellX, int cellZ) =>
        (cellZ % CellsPerChunk) * CellsPerChunk + cellX % CellsPerChunk;

    /// <summary>
    /// Copy one chunk column's 32x32 height map into its 8x8 cells, sampling each cell at its
    /// minimum corner, which is Farseer's sampling. A cell changes only when the new source is at
    /// least as good as the one it holds, and only a real change bumps the revision.
    /// </summary>
    public bool SetChunk(int cx, int cz, ReadOnlySpan<ushort> heightMap, EnumSilhouetteOrigin origin)
    {
        if (origin == EnumSilhouetteOrigin.Unknown)
            throw new ArgumentOutOfRangeException(nameof(origin));
        if (heightMap.Length < HeightMapLength)
            throw new ArgumentException("A chunk height map has 1,024 entries.", nameof(heightMap));
        int localX = cx - TileX * ChunksPerEdge;
        int localZ = cz - TileZ * ChunksPerEdge;
        if (localX < 0 || localX >= ChunksPerEdge || localZ < 0 || localZ >= ChunksPerEdge)
            throw new ArgumentOutOfRangeException(nameof(cx), "The chunk is outside this tile.");

        int slot = localZ * ChunksPerEdge + localX;
        ChunkData cells = chunks[slot] ??= new ChunkData();
        int rank = Rank(origin);
        bool changed = false;
        for (int j = 0; j < CellsPerChunk; j++)
        {
            for (int i = 0; i < CellsPerChunk; i++)
            {
                int index = j * CellsPerChunk + i;
                var held = (EnumSilhouetteOrigin)cells.Origins[index];
                if (rank > Rank(held)) continue;
                ushort height = heightMap[j * CellBlocks * ChunkBlocks + i * CellBlocks];
                if (held == origin && cells.Heights[index] == height) continue;
                if (held == EnumSilhouetteOrigin.Unknown) KnownCells++;
                cells.Origins[index] = (byte)origin;
                cells.Heights[index] = height;
                changed = true;
            }
        }
        if (changed) Revision++;
        return changed;
    }

    public EnumSilhouetteOrigin OriginAt(int cellX, int cellZ)
    {
        ChunkData? cells = ChunkOfCell(cellX, cellZ);
        return cells == null
            ? EnumSilhouetteOrigin.Unknown
            : (EnumSilhouetteOrigin)cells.Origins[IndexInChunk(cellX, cellZ)];
    }

    public bool TryGetHeight(int cellX, int cellZ, out ushort height)
    {
        ChunkData? cells = ChunkOfCell(cellX, cellZ);
        int index = IndexInChunk(cellX, cellZ);
        if (cells == null || cells.Origins[index] == 0)
        {
            height = 0;
            return false;
        }
        height = cells.Heights[index];
        return true;
    }

    /// <summary>Mean height of the known cells of one chunk of this tile, if it has any.</summary>
    public bool TryChunkMean(int chunkLocalX, int chunkLocalZ, out double mean)
    {
        ChunkData? cells = chunks[chunkLocalZ * ChunksPerEdge + chunkLocalX];
        mean = 0;
        if (cells == null) return false;
        int count = 0;
        long sum = 0;
        for (int i = 0; i < ChunkCells; i++)
        {
            if (cells.Origins[i] == 0) continue;
            sum += cells.Heights[i];
            count++;
        }
        if (count == 0) return false;
        mean = (double)sum / count;
        return true;
    }

    /// <summary>True when the chunk has known cells. A chunk's cells are only allocated with data.</summary>
    public bool HasChunk(int chunkLocalX, int chunkLocalZ) =>
        chunks[chunkLocalZ * ChunksPerEdge + chunkLocalX] != null;

    /// <summary>
    /// Write every known cell's height into a row-major grid, with this tile's cell (0,0) at
    /// (<paramref name="x0"/>, <paramref name="z0"/>). Unknown cells are left as they are.
    /// On the client a tile is never changed after <see cref="Decode"/> (a newer revision
    /// replaces it whole), so mesh jobs may call this from worker threads.
    /// </summary>
    public void WriteKnownHeights(float[] grid, int stride, int x0, int z0)
    {
        for (int slot = 0; slot < chunks.Length; slot++)
        {
            ChunkData? cells = chunks[slot];
            if (cells == null) continue;
            int baseX = x0 + slot % ChunksPerEdge * CellsPerChunk;
            int baseZ = z0 + slot / ChunksPerEdge * CellsPerChunk;
            for (int j = 0; j < CellsPerChunk; j++)
            {
                int row = (baseZ + j) * stride + baseX;
                for (int i = 0; i < CellsPerChunk; i++)
                {
                    int index = j * CellsPerChunk + i;
                    if (cells.Origins[index] != 0) grid[row + i] = cells.Heights[index];
                }
            }
        }
    }

    /// <summary>
    /// Format byte, then deflated: the 2-bit state of all 16,384 cells in row-major order (four
    /// to a byte, lowest bits first), then the height of every known cell in the same order,
    /// little-endian. A fully known tile is 36 KiB before compression.
    /// </summary>
    public byte[] Encode()
    {
        byte[] body = new byte[StateBytes + 2 * KnownCells];
        int heightOffset = StateBytes;
        for (int cellZ = 0; cellZ < CellsPerEdge; cellZ++)
        {
            for (int cellX = 0; cellX < CellsPerEdge; cellX++)
            {
                ChunkData? cells = ChunkOfCell(cellX, cellZ);
                if (cells == null) continue;
                int index = IndexInChunk(cellX, cellZ);
                byte state = cells.Origins[index];
                if (state == 0) continue;
                int cell = cellZ * CellsPerEdge + cellX;
                body[cell >> 2] |= (byte)(state << ((cell & 3) * 2));
                BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(heightOffset), cells.Heights[index]);
                heightOffset += 2;
            }
        }

        using var output = new MemoryStream();
        output.WriteByte(FormatVersion);
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(body, 0, body.Length);
        }
        return output.ToArray();
    }

    /// <summary>
    /// The inverse of <see cref="Encode"/>, or null for anything that is not exactly one
    /// well-formed tile: a wrong format byte, a truncated or over-long body, or a stream that
    /// does not inflate. Never throws on bad input, because it runs on data from the network.
    /// </summary>
    public static LodSilhouetteTile? Decode(int tileX, int tileZ, int revision, byte[]? payload)
    {
        if (payload == null || payload.Length < 2 || payload[0] != FormatVersion) return null;
        byte[] body = new byte[StateBytes + 2 * CellCount + 1];
        int read;
        try
        {
            using var input = new MemoryStream(payload, 1, payload.Length - 1, writable: false);
            using var inflate = new DeflateStream(input, CompressionMode.Decompress);
            read = 0;
            while (read < body.Length)
            {
                int got = inflate.Read(body, read, body.Length - read);
                if (got <= 0) break;
                read += got;
            }
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        if (read < StateBytes || read == body.Length) return null;

        int known = 0;
        for (int i = 0; i < StateBytes; i++)
        {
            byte packed = body[i];
            for (int k = 0; k < 4; k++)
                if (((packed >> (k * 2)) & 3) != 0) known++;
        }
        if (read != StateBytes + 2 * known) return null;

        var tile = new LodSilhouetteTile(tileX, tileZ, revision);
        int heightOffset = StateBytes;
        for (int cell = 0; cell < CellCount; cell++)
        {
            int state = (body[cell >> 2] >> ((cell & 3) * 2)) & 3;
            if (state == 0) continue;
            int cellX = cell % CellsPerEdge, cellZ = cell / CellsPerEdge;
            int slot = (cellZ / CellsPerChunk) * ChunksPerEdge + cellX / CellsPerChunk;
            ChunkData cells = tile.chunks[slot] ??= new ChunkData();
            int index = IndexInChunk(cellX, cellZ);
            cells.Origins[index] = (byte)state;
            cells.Heights[index] = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(heightOffset));
            heightOffset += 2;
        }
        tile.KnownCells = known;
        return tile;
    }
}
