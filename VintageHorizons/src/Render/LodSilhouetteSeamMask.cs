using VintageHorizons.Silhouette;

namespace VintageHorizons;

/// <summary>
/// One byte per 32-block Farseer ownership cell, packed four cells into each BGRA texel.
///
/// Bit zero says a drawable Farseer tile describes the cell, bit one says ready real terrain
/// replaces it, and bits two through five mark a west/east/north/south edge against the other
/// owner. The fragment shader therefore needs one point sample to find the exact handoff and can
/// fade fog through the whole cell on either side without walking neighbouring texels per pixel.
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
    public bool HasEdges => BoundaryCells > 0;

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
        BoundaryCells = 0;
        Active = true;
        Dirty = true;
        return true;
    }

    /// <summary>Adds one complete 512-block tile after <see cref="BeginBuild"/>.</summary>
    public void SetTile(int tileX, int tileZ, LodSilhouetteCoverage replacements)
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
                    bool replaced = (state & Replaced) != 0;
                    if (OppositeOwner(cellX - 1, cellZ, replaced)) state |= WestEdge;
                    if (OppositeOwner(cellX + 1, cellZ, replaced)) state |= EastEdge;
                    if (OppositeOwner(cellX, cellZ - 1, replaced)) state |= NorthEdge;
                    if (OppositeOwner(cellX, cellZ + 1, replaced)) state |= SouthEdge;
                    if ((state & EdgeMask) != 0) BoundaryCells++;
                    WriteState(cellX, cellZ, state);
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

    /// <summary>CPU mirror of the GLSL ramp, used by focused checks.</summary>
    public static float FogWeight(byte state, float localX, float localZ,
        float widthBlocks = CellBlocks)
    {
        if ((state & Available) == 0 || (state & EdgeMask) == 0) return 0f;
        float width = Math.Max(1f, widthBlocks);
        float distance = width;
        if ((state & WestEdge) != 0) distance = Math.Min(distance, localX);
        if ((state & EastEdge) != 0) distance = Math.Min(distance, CellBlocks - localX);
        if ((state & NorthEdge) != 0) distance = Math.Min(distance, localZ);
        if ((state & SouthEdge) != 0) distance = Math.Min(distance, CellBlocks - localZ);
        float t = Math.Clamp(distance / width, 0f, 1f);
        float smooth = t * t * (3f - 2f * t);
        return MathF.Pow(Math.Clamp(1f - smooth, 0f, 1f), 0.60f);
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
