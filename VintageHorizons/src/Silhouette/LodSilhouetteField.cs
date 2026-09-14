namespace VintageHorizons.Silhouette;

/// <summary>A tile's mesh as plain arrays: built on a worker, uploaded on the render thread.</summary>
public sealed class LodSilhouetteMeshArrays
{
    public required float[] Xyz;
    public required int[] Indices;
    public required int VertexCount;
    public required int IndexCount;
    public required float MinY;
    public required float MaxY;
}

/// <summary>
/// Where the server samples, and how the client turns a tile into a mesh.
///
/// The server previews one chunk in every 8x8, on a global lattice (chunks 4 and 12 of every
/// 16-chunk tile), and also keeps the heights of every column it loads or previews for its
/// cache. The client fills the unknown cells from the known ones across the tile's
/// neighbourhood (<see cref="LodSilhouetteFill"/>) and meshes the result at one resolution.
/// P4's mesher adds more resolutions, the anchoring and the handoff.
/// </summary>
public static class LodSilhouetteField
{
    /// <summary>One sampled chunk in every 8x8.</summary>
    public const int LatticeChunks = 8;

    /// <summary>Offset of the sampled chunk inside each 8x8 block, so samples sit mid-block.</summary>
    public const int LatticeOffsetChunks = 4;

    public const int LatticeBlocks = LatticeChunks * LodSilhouetteTile.ChunkBlocks;

    /// <summary>World block coordinate of node 0: the centre of lattice chunk 4.</summary>
    public const double NodeOriginBlocks =
        LatticeOffsetChunks * LodSilhouetteTile.ChunkBlocks + LodSilhouetteTile.ChunkBlocks / 2.0;

    /// <summary>Quads per tile edge at the one resolution drawn so far: 16-block quads.</summary>
    public const int DefaultQuadsPerEdge = 32;

    /// <summary>
    /// How far a vertex next to cached terrain dips, so real terrain wins the depth test at the
    /// join. Farseer sinks by 5 near its start; a whole quad is 16 blocks here, so a little more.
    /// </summary>
    public const float DefaultSinkBlocks = 8f;

    public static bool IsLatticeChunk(int cx, int cz) =>
        Mod(cx, LatticeChunks) == LatticeOffsetChunks && Mod(cz, LatticeChunks) == LatticeOffsetChunks;

    public static int NodeOfLatticeChunk(int c) => (c - LatticeOffsetChunks) / LatticeChunks;

    public static int LatticeChunkOfNode(int n) => n * LatticeChunks + LatticeOffsetChunks;

    public static double NodeBlockX(int i) => NodeOriginBlocks + (double)i * LatticeBlocks;

    /// <summary>Tiles whose mesh reads a given tile's heights: the tile and its eight neighbours.</summary>
    public static IEnumerable<(int TileX, int TileZ)> TilesReading(int tileX, int tileZ)
    {
        int radius = LodSilhouetteFill.NeighbourhoodTiles / 2;
        for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
                yield return (tileX + dx, tileZ + dz);
    }

    /// <summary>
    /// Bit for one 64-block level-0 cell inside a tile: 8x8 of them, bit = localZ * 8 + localX.
    /// </summary>
    public static int CoverageBit(int localCellX, int localCellZ) => localCellZ * 8 + localCellX;

    /// <summary>
    /// Build the centre tile's mesh from its neighbourhood (see <see cref="LodSilhouetteFill"/>
    /// for the layout). A quad is drawn when all four corners have heights and its level-0 cell
    /// is not covered by cached terrain; a vertex touching a covered cell sinks. Heights are
    /// clamped to sea level, which is Farseer's flat sea. Null when no quad is drawn.
    /// </summary>
    public static LodSilhouetteMeshArrays? BuildMesh(LodSilhouetteTile?[,] neighbourhood,
        ulong coveredCells, int seaLevel, int quadsPerEdge = DefaultQuadsPerEdge,
        float sinkBlocks = DefaultSinkBlocks)
    {
        if (quadsPerEdge < 1 || LodSilhouetteTile.CellsPerEdge % quadsPerEdge != 0)
            throw new ArgumentOutOfRangeException(nameof(quadsPerEdge));
        if (coveredCells == ulong.MaxValue) return null;

        float[] filled = LodSilhouetteFill.FillCentre(neighbourhood);
        int stride = LodSilhouetteTile.CellsPerEdge + 1;
        int cellStep = LodSilhouetteTile.CellsPerEdge / quadsPerEdge;
        int spacing = cellStep * LodSilhouetteTile.CellBlocks;
        int verts = quadsPerEdge + 1;

        var heights = new float[verts * verts];
        for (int b = 0; b < verts; b++)
            for (int a = 0; a < verts; a++)
                heights[b * verts + a] = filled[b * cellStep * stride + a * cellStep];

        // A quad belongs to the level-0 cell under its centre.
        bool QuadCovered(int a, int b)
        {
            int cellX = (a * spacing + spacing / 2) / LodSection.SectionBlocks;
            int cellZ = (b * spacing + spacing / 2) / LodSection.SectionBlocks;
            return (coveredCells & (1UL << CoverageBit(cellX, cellZ))) != 0;
        }

        var drawn = new bool[quadsPerEdge * quadsPerEdge];
        var sunk = new bool[verts * verts];
        int quads = 0;
        for (int b = 0; b < quadsPerEdge; b++)
        {
            for (int a = 0; a < quadsPerEdge; a++)
            {
                if (QuadCovered(a, b))
                {
                    sunk[b * verts + a] = sunk[b * verts + a + 1] = true;
                    sunk[(b + 1) * verts + a] = sunk[(b + 1) * verts + a + 1] = true;
                    continue;
                }
                if (float.IsNaN(heights[b * verts + a]) || float.IsNaN(heights[b * verts + a + 1])
                    || float.IsNaN(heights[(b + 1) * verts + a])
                    || float.IsNaN(heights[(b + 1) * verts + a + 1])) continue;
                drawn[b * quadsPerEdge + a] = true;
                quads++;
            }
        }
        if (quads == 0) return null;

        var xyz = new float[verts * verts * 3];
        float minY = float.MaxValue, maxY = float.MinValue;
        for (int b = 0; b < verts; b++)
        {
            for (int a = 0; a < verts; a++)
            {
                int v = b * verts + a;
                float h = heights[v];
                float y = float.IsNaN(h) ? seaLevel : Math.Max(h, seaLevel);
                if (sunk[v]) y -= sinkBlocks;
                xyz[v * 3] = a * spacing;
                xyz[v * 3 + 1] = y;
                xyz[v * 3 + 2] = b * spacing;
            }
        }

        var indices = new int[quads * 6];
        int n = 0;
        for (int b = 0; b < quadsPerEdge; b++)
        {
            for (int a = 0; a < quadsPerEdge; a++)
            {
                if (!drawn[b * quadsPerEdge + a]) continue;
                int topLeft = b * verts + a;
                int bottomLeft = (b + 1) * verts + a;
                indices[n++] = topLeft;
                indices[n++] = bottomLeft;
                indices[n++] = topLeft + 1;
                indices[n++] = topLeft + 1;
                indices[n++] = bottomLeft;
                indices[n++] = bottomLeft + 1;
                foreach (int v in new[] { topLeft, topLeft + 1, bottomLeft, bottomLeft + 1 })
                {
                    float y = xyz[v * 3 + 1];
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        return new LodSilhouetteMeshArrays
        {
            Xyz = xyz,
            Indices = indices,
            VertexCount = verts * verts,
            IndexCount = indices.Length,
            MinY = minY,
            MaxY = maxY,
        };
    }

    static int Mod(int value, int m)
    {
        int r = value % m;
        return r < 0 ? r + m : r;
    }
}
