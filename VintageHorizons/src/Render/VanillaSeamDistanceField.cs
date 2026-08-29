namespace VintageHorizons;

/// <summary>
/// How far each 8x8 block cell lies from the nearest ground vanilla owns, in cells.
///
/// This is the only new data the blend band needs. The band's shape is not a radius and is
/// never derived from one: it is whatever shape vanilla's own loaded footprint has, so it
/// inherits vanilla's chunk-stepped outline for free and stays a ring only because the
/// loaded area is a ring.
///
/// 8 blocks is a deliberate choice rather than a resolution ceiling. It is a quarter of a
/// vanilla chunk - fine enough to read as a smaller continuation of the game's blocky idiom,
/// coarse enough to still read as steps at 200+ blocks. Finer cells cost the square, are
/// rebuilt on every window move, and shrink the float precision margin that
/// <see cref="VanillaReadinessMask"/> already works around; and the ramp they would buy is
/// finer than one world block, which is the exact thing that made an earlier smooth radial
/// transition read as a GPU effect painted over the terrain instead of as terrain.
///
/// Storage mirrors the mask's wrapped ring so the shader can reconstruct the same address
/// from an integer section origin plus a section-local offset. Unlike the mask this is 2D:
/// vanilla's footprint is a horizontal question, and a column counts as vanilla's the moment
/// any cell in it does.
/// </summary>
internal sealed class VanillaSeamDistanceField
{
    /// <summary>Blocks per axis in one field cell.</summary>
    public const int CellBlocks = 8;

    /// <summary>Field cells across one vanilla ownership chunk.</summary>
    public const int CellsPerChunk = VanillaReadinessMask.ChunkBlocks / CellBlocks;

    /// <summary>
    /// Chamfer weights. Distances accumulate in thirds of a cell so a diagonal step can cost
    /// 4/3 rather than 1, which keeps the contours within about 2% of true Euclidean instead
    /// of the diamond a plain Manhattan sweep would produce.
    /// </summary>
    const int Orthogonal = 3;
    const int Diagonal = 4;

    /// <summary>Larger than any reachable accumulation, and its own saturating ceiling.</summary>
    const int Unreached = ushort.MaxValue;

    /// <summary>The furthest distance a texel can carry. Beyond it every band weight is zero.</summary>
    public const int MaxDistanceCells = 255;

    readonly int capacity;
    readonly int capacityMask;
    readonly int[] texels;
    readonly ushort[] work;

    int minCellX;
    int minCellZ;
    int width;
    int depth;

    public VanillaSeamDistanceField(int cellCapacity)
    {
        if (cellCapacity <= 0 || (cellCapacity & (cellCapacity - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(cellCapacity),
                "Seam field capacity must be a positive power of two.");

        capacity = cellCapacity;
        capacityMask = cellCapacity - 1;
        int cells = checked(cellCapacity * cellCapacity);
        texels = new int[cells];
        work = new ushort[cells];
        Clear();
    }

    public int Capacity => capacity;
    public int Width => capacity;
    public int Height => capacity;
    public int[] Texels => texels;
    public int MinCellX => minCellX;
    public int MinCellZ => minCellZ;
    public int WidthCells => width;
    public int DepthCells => depth;
    public long Bytes => texels.LongLength * sizeof(int) + work.LongLength * sizeof(ushort);

    /// <summary>True when the buffer differs from what the GPU last accepted.</summary>
    public bool Dirty { get; private set; }

    public long Builds { get; private set; }
    public long Uploads { get; private set; }

    /// <summary>
    /// Wrapped atlas address for one field cell, matching the addressing the fragment shader
    /// reconstructs. A static check holds the two together.
    /// </summary>
    public static int TexelIndex(int cellX, int cellZ, int capacity)
    {
        int mask = capacity - 1;
        return (cellZ & mask) * capacity + (cellX & mask);
    }

    /// <summary>
    /// Everything maximally far from vanilla, which is the same as no band at all. Used for
    /// construction, world teardown, and every failure path, because a field that claims
    /// nothing is near vanilla leaves cached terrain exactly as it draws today.
    /// </summary>
    public void Clear()
    {
        int far = Texel(MaxDistanceCells);
        for (int i = 0; i < texels.Length; i++) texels[i] = far;
        Array.Fill(work, (ushort)Unreached);
        width = depth = 0;
        Dirty = true;
    }

    /// <summary>
    /// Recomputes the whole window from committed ownership.
    ///
    /// The window is the mask's own window grown by <paramref name="marginCells"/> on every
    /// side, because the band lives entirely OUTSIDE vanilla's footprint and the mask only
    /// tracks a couple of chunks past it. Rebuilding wholesale rather than incrementally is
    /// what makes a moved window safe: the wrapped ring hands a reused slot to different
    /// world coordinates, and a full rewrite of the window is what stops the shader reading
    /// another place's distance.
    /// </summary>
    public void Build(VanillaReadinessMask mask,
        int maskMinChunkX, int maskMinChunkZ, int maskWidth, int maskDepth, int marginCells)
    {
        ArgumentNullException.ThrowIfNull(mask);
        if (maskWidth <= 0 || maskDepth <= 0) throw new ArgumentOutOfRangeException(nameof(maskWidth));
        if (marginCells < 0) throw new ArgumentOutOfRangeException(nameof(marginCells));

        int seedMinCellX = checked(maskMinChunkX * CellsPerChunk);
        int seedMinCellZ = checked(maskMinChunkZ * CellsPerChunk);
        int newWidth = checked(maskWidth * CellsPerChunk + marginCells * 2);
        int newDepth = checked(maskDepth * CellsPerChunk + marginCells * 2);
        if (newWidth > capacity || newDepth > capacity)
            throw new ArgumentOutOfRangeException(nameof(marginCells),
                "Seam field window is larger than its capacity.");

        minCellX = seedMinCellX - marginCells;
        minCellZ = seedMinCellZ - marginCells;
        width = newWidth;
        depth = newDepth;

        for (int z = 0; z < depth; z++)
        {
            int row = ((minCellZ + z) & capacityMask) * capacity;
            for (int x = 0; x < width; x++) work[row + ((minCellX + x) & capacityMask)] = Unreached;
        }

        SeedOwnedColumns(mask, maskMinChunkX, maskMinChunkZ, maskWidth, maskDepth,
            seedMinCellX, seedMinCellZ);
        SweepForward();
        SweepBackward();
        WriteTexels();

        Builds++;
        Dirty = true;
    }

    void SeedOwnedColumns(VanillaReadinessMask mask,
        int maskMinChunkX, int maskMinChunkZ, int maskWidth, int maskDepth,
        int seedMinCellX, int seedMinCellZ)
    {
        for (int cz = 0; cz < maskDepth; cz++)
        {
            for (int cx = 0; cx < maskWidth; cx++)
            {
                if (!mask.ColumnHasReady(maskMinChunkX + cx, maskMinChunkZ + cz)) continue;

                for (int sz = 0; sz < CellsPerChunk; sz++)
                {
                    int row = ((seedMinCellZ + cz * CellsPerChunk + sz) & capacityMask) * capacity;
                    for (int sx = 0; sx < CellsPerChunk; sx++)
                    {
                        work[row + ((seedMinCellX + cx * CellsPerChunk + sx) & capacityMask)] = 0;
                    }
                }
            }
        }
    }

    void SweepForward()
    {
        for (int z = 0; z < depth; z++)
        {
            for (int x = 0; x < width; x++)
            {
                int best = Read(x, z);
                if (best == 0) continue;
                best = Math.Min(best, Step(Read(x - 1, z), Orthogonal));
                best = Math.Min(best, Step(Read(x, z - 1), Orthogonal));
                best = Math.Min(best, Step(Read(x - 1, z - 1), Diagonal));
                best = Math.Min(best, Step(Read(x + 1, z - 1), Diagonal));
                Write(x, z, best);
            }
        }
    }

    void SweepBackward()
    {
        for (int z = depth - 1; z >= 0; z--)
        {
            for (int x = width - 1; x >= 0; x--)
            {
                int best = Read(x, z);
                if (best == 0) continue;
                best = Math.Min(best, Step(Read(x + 1, z), Orthogonal));
                best = Math.Min(best, Step(Read(x, z + 1), Orthogonal));
                best = Math.Min(best, Step(Read(x + 1, z + 1), Diagonal));
                best = Math.Min(best, Step(Read(x - 1, z + 1), Diagonal));
                Write(x, z, best);
            }
        }
    }

    void WriteTexels()
    {
        for (int z = 0; z < depth; z++)
        {
            int row = ((minCellZ + z) & capacityMask) * capacity;
            for (int x = 0; x < width; x++)
            {
                int index = row + ((minCellX + x) & capacityMask);
                int accumulated = work[index];
                int cells = accumulated >= Unreached
                    ? MaxDistanceCells
                    : Math.Min(MaxDistanceCells, (accumulated + Orthogonal / 2) / Orthogonal);
                texels[index] = Texel(cells);
            }
        }
    }

    /// <summary>
    /// Distance in cells, in the red channel of a BGRA texel. Opaque so the value cannot be
    /// mistaken for an unwritten texel, and only red carries meaning; the shader reads .r.
    /// </summary>
    static int Texel(int cells) => unchecked((int)0xFF000000) | (cells << 16);

    /// <summary>
    /// Outside the window is unreachable rather than near, so a cell at the window's edge
    /// cannot inherit a distance from ground the field does not describe.
    /// </summary>
    int Read(int x, int z)
    {
        if (x < 0 || x >= width || z < 0 || z >= depth) return Unreached;
        return work[((minCellZ + z) & capacityMask) * capacity + ((minCellX + x) & capacityMask)];
    }

    void Write(int x, int z, int value) =>
        work[((minCellZ + z) & capacityMask) * capacity + ((minCellX + x) & capacityMask)] =
            (ushort)Math.Min(Unreached, value);

    /// <summary>Saturating, so an unreachable neighbour stays unreachable instead of wrapping.</summary>
    static int Step(int value, int cost) =>
        value >= Unreached ? Unreached : Math.Min(Unreached, value + cost);

    /// <summary>Records that the GPU accepted the current buffer.</summary>
    public void MarkUploaded()
    {
        Dirty = false;
        Uploads++;
    }

    public void ResetTelemetry()
    {
        Builds = 0;
        Uploads = 0;
    }
}
