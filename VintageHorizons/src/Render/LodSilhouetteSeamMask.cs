using VintageHorizons.Silhouette;

namespace VintageHorizons;

/// <summary>One unique 32-block edge on the Farseer/real-terrain ownership frontier.</summary>
internal readonly record struct LodSilhouetteBoundaryEdge(
    int StartGridX, int StartGridZ, int EndGridX, int EndGridZ);

/// <summary>
/// One byte per 32-block Farseer ownership cell, packed four cells into each integer.
///
/// Bit zero says a drawable Farseer tile describes the cell, bit one says ready real terrain
/// replaces it, and bits two through five mark a west/east/north/south edge against the other
/// owner. The unique edge list is the centreline for the continuous fog field; ownership remains
/// exact while the visible medium can spill softly and irregularly beyond the chunk grid.
/// </summary>
internal sealed class LodSilhouetteSeamMask
{
    public const int CellBlocks = LodSilhouetteField.CoverageCellBlocks;
    public const int CellsPerTexel = 4;
    public const byte Available = 1 << 0;
    public const byte Replaced = 1 << 1;
    public const byte WestEdge = 1 << 2;
    public const byte EastEdge = 1 << 3;
    public const byte NorthEdge = 1 << 4;
    public const byte SouthEdge = 1 << 5;
    public const byte EdgeMask = WestEdge | EastEdge | NorthEdge | SouthEdge;

    int[] texels = Array.Empty<int>();
    readonly List<(int CellX, int CellZ)> tiles = new();
    readonly List<LodSilhouetteBoundaryEdge> boundaries = new();
    ulong signature;
    bool hasSignature;

    public int[] Texels => texels;
    public int MinCellX { get; private set; }
    public int MinCellZ { get; private set; }
    public int WidthCells { get; private set; }
    public int DepthCells { get; private set; }
    public int TextureWidth { get; private set; }
    public int TextureHeight { get; private set; }
    public bool Active { get; private set; }
    public bool Dirty { get; private set; }
    public bool TextureSizeChanged { get; private set; }
    public int BoundaryCells { get; private set; }
    public IReadOnlyList<LodSilhouetteBoundaryEdge> Boundaries => boundaries;
    public bool HasEdges => boundaries.Count > 0;

    /// <summary>
    /// Starts a complete visible-window rebuild. False means the exact same tile set and
    /// replacement coverage are already live, so neither the CPU table nor the GPU texture needs
    /// touching this frame.
    /// </summary>
    public bool BeginBuild(int minTileX, int minTileZ, int maxTileX, int maxTileZ,
        ulong contentSignature)
    {
        if (maxTileX < minTileX || maxTileZ < minTileZ)
            throw new ArgumentOutOfRangeException(nameof(maxTileX));

        int cellsPerTile = LodSilhouetteField.CoverageCellsPerEdge;
        int minCellX = checked(minTileX * cellsPerTile);
        int minCellZ = checked(minTileZ * cellsPerTile);
        int widthCells = checked((maxTileX - minTileX + 1) * cellsPerTile);
        int depthCells = checked((maxTileZ - minTileZ + 1) * cellsPerTile);
        int textureWidth = NextPowerOfTwo((widthCells + CellsPerTexel - 1) / CellsPerTexel);
        int textureHeight = NextPowerOfTwo(depthCells);

        bool unchanged = Active && hasSignature && signature == contentSignature
            && MinCellX == minCellX && MinCellZ == minCellZ
            && WidthCells == widthCells && DepthCells == depthCells;
        if (unchanged) return false;

        bool sizeChanged = TextureWidth != textureWidth || TextureHeight != textureHeight;
        if (sizeChanged)
        {
            texels = new int[checked(textureWidth * textureHeight)];
            TextureWidth = textureWidth;
            TextureHeight = textureHeight;
            TextureSizeChanged = true;
        }
        else
        {
            Array.Clear(texels);
        }

        MinCellX = minCellX;
        MinCellZ = minCellZ;
        WidthCells = widthCells;
        DepthCells = depthCells;
        signature = contentSignature;
        hasSignature = true;
        tiles.Clear();
        boundaries.Clear();
        BoundaryCells = 0;
        Active = true;
        Dirty = true;
        return true;
    }

    /// <summary>
    /// Adds one 512-block tile after <see cref="BeginBuild"/>. <paramref name="available"/>
    /// limits it to the cells inside the fog's reach - the cached-terrain fog's disc ends partway
    /// through a tile - and a cell outside it is no owner at all, so no edge forms against it.
    /// A Farseer tile is available everywhere it describes.
    /// </summary>
    public void SetTile(int tileX, int tileZ, LodSilhouetteCoverage replacements,
        LodSilhouetteCoverage? available = null)
    {
        if (!Active) throw new InvalidOperationException("The Farseer seam mask has no active build.");
        int cellsPerTile = LodSilhouetteField.CoverageCellsPerEdge;
        int tileCellX = checked(tileX * cellsPerTile);
        int tileCellZ = checked(tileZ * cellsPerTile);
        tiles.Add((tileCellX, tileCellZ));

        for (int z = 0; z < cellsPerTile; z++)
        {
            for (int x = 0; x < cellsPerTile; x++)
            {
                if (available is { } within && !within.Covered(x, z)) continue;
                byte state = (byte)(Available
                    | (replacements.Covered(x, z) ? Replaced : 0));
                WriteState(tileCellX + x, tileCellZ + z, state);
            }
        }
    }

    /// <summary>
    /// Marks only shared edges whose two drawable Farseer cells have opposite live owners.
    /// Missing neighbouring tiles are not a Farseer/LOD seam and deliberately create no fog.
    /// </summary>
    public void CompleteBuild()
    {
        if (!Active) return;
        int cellsPerTile = LodSilhouetteField.CoverageCellsPerEdge;
        foreach ((int tileCellX, int tileCellZ) in tiles)
        {
            for (int z = 0; z < cellsPerTile; z++)
            {
                for (int x = 0; x < cellsPerTile; x++)
                {
                    int cellX = tileCellX + x;
                    int cellZ = tileCellZ + z;
                    byte state = StateAt(cellX, cellZ);
                    if ((state & Available) == 0) continue;
                    bool replaced = (state & Replaced) != 0;
                    if (OppositeOwner(cellX - 1, cellZ, replaced)) state |= WestEdge;
                    if (OppositeOwner(cellX + 1, cellZ, replaced)) state |= EastEdge;
                    if (OppositeOwner(cellX, cellZ - 1, replaced)) state |= NorthEdge;
                    if (OppositeOwner(cellX, cellZ + 1, replaced)) state |= SouthEdge;
                    if ((state & EdgeMask) != 0) BoundaryCells++;
                    WriteState(cellX, cellZ, state);
                    // Keep one copy of each shared edge. Its opposite cell carries the matching
                    // west/north flag, so east and south are sufficient for the complete set.
                    // Direct every edge so its right-hand normal points into Farseer terrain.
                    // The fog path builder can then widen that side without re-reading ownership.
                    if ((state & EastEdge) != 0)
                        boundaries.Add(replaced
                            ? new LodSilhouetteBoundaryEdge(
                                cellX + 1, cellZ + 1, cellX + 1, cellZ)
                            : new LodSilhouetteBoundaryEdge(
                                cellX + 1, cellZ, cellX + 1, cellZ + 1));
                    if ((state & SouthEdge) != 0)
                        boundaries.Add(replaced
                            ? new LodSilhouetteBoundaryEdge(
                                cellX, cellZ + 1, cellX + 1, cellZ + 1)
                            : new LodSilhouetteBoundaryEdge(
                                cellX + 1, cellZ + 1, cellX, cellZ + 1));
                }
            }
        }
    }

    bool OppositeOwner(int cellX, int cellZ, bool replaced)
    {
        byte neighbour = StateAt(cellX, cellZ);
        return (neighbour & Available) != 0
            && ((neighbour & Replaced) != 0) != replaced;
    }

    public byte StateAt(int cellX, int cellZ)
    {
        int x = cellX - MinCellX;
        int z = cellZ - MinCellZ;
        if ((uint)x >= (uint)WidthCells || (uint)z >= (uint)DepthCells) return 0;
        int packed = texels[z * TextureWidth + x / CellsPerTexel];
        return (byte)(packed >> ((x & (CellsPerTexel - 1)) * 8));
    }

    void WriteState(int cellX, int cellZ, byte state)
    {
        int x = cellX - MinCellX;
        int z = cellZ - MinCellZ;
        if ((uint)x >= (uint)WidthCells || (uint)z >= (uint)DepthCells)
            throw new ArgumentOutOfRangeException(nameof(cellX));
        int index = z * TextureWidth + x / CellsPerTexel;
        int shift = (x & (CellsPerTexel - 1)) * 8;
        texels[index] = (texels[index] & ~(0xFF << shift)) | (state << shift);
    }

    public void MarkUploaded()
    {
        Dirty = false;
        TextureSizeChanged = false;
    }

    public void Deactivate()
    {
        Active = false;
        Dirty = false;
        hasSignature = false;
        tiles.Clear();
        boundaries.Clear();
    }

    public void Clear()
    {
        Deactivate();
        texels = Array.Empty<int>();
        MinCellX = MinCellZ = WidthCells = DepthCells = 0;
        TextureWidth = TextureHeight = 0;
        BoundaryCells = 0;
        TextureSizeChanged = false;
    }

    static int NextPowerOfTwo(int value)
    {
        int result = 1;
        while (result < value) result = checked(result << 1);
        return result;
    }
}
