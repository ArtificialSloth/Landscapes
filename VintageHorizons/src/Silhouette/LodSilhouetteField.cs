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
/// How the first-look slice (P1) turns sparse samples into a continuous surface.
///
/// The server samples one chunk in every 8x8, on a global lattice (chunks 4 and 12 of every
/// 16-chunk tile). Each sampled chunk becomes one node: the mean of its known cells, at the chunk
/// centre. A height anywhere is the bilinear blend of the four surrounding nodes, with the
/// weights of unknown nodes dropped and the rest renormalized.
///
/// It is a pure function of world position and node values. Two tiles that share a border
/// therefore compute identical heights along it, and no crack can open between them as long as
/// both were meshed with the same nodes. When a node changes, every tile it can reach is
/// re-meshed. Later phases replace this with a fill over known cells at any spacing (plan 4.3);
/// only this class changes when they do.
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

    /// <summary>Nodes per axis a tile's heights can read: its own two plus one either side.</summary>
    public const int TileNodeSpan = 4;

    /// <summary>Quads per tile edge in P1's single resolution: 16-block quads.</summary>
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

    public static long NodeKey(int i, int j) => ((long)j << 32) | (uint)i;

    public static double NodeBlockX(int i) => NodeOriginBlocks + (double)i * LatticeBlocks;

    /// <summary>First node index a tile's heights read, on either axis.</summary>
    public static int FirstNodeOfTile(int tile) =>
        (int)Math.Floor((tile * (double)LodSilhouetteTile.TileBlocks - NodeOriginBlocks) / LatticeBlocks);

    /// <summary>
    /// Tiles whose heights a node can change: every tile within one lattice step of the node on
    /// both axes. A node sits inside one tile and influences at most 256 blocks around it, which
    /// never reaches past the eight neighbours.
    /// </summary>
    public static IEnumerable<(int TileX, int TileZ)> TilesTouchedByNode(int i, int j)
    {
        double x = NodeBlockX(i), z = NodeBlockX(j);
        int minTx = (int)Math.Floor((x - LatticeBlocks) / LodSilhouetteTile.TileBlocks);
        int maxTx = (int)Math.Floor((x + LatticeBlocks) / LodSilhouetteTile.TileBlocks);
        int minTz = (int)Math.Floor((z - LatticeBlocks) / LodSilhouetteTile.TileBlocks);
        int maxTz = (int)Math.Floor((z + LatticeBlocks) / LodSilhouetteTile.TileBlocks);
        for (int tz = minTz; tz <= maxTz; tz++)
            for (int tx = minTx; tx <= maxTx; tx++)
                yield return (tx, tz);
    }

    /// <summary>
    /// Height at a world block position, or NaN where none of the four surrounding nodes is
    /// known. <paramref name="node"/> answers NaN for an unknown node.
    /// </summary>
    public static double HeightAt(double x, double z, Func<int, int, double> node)
    {
        double fx = (x - NodeOriginBlocks) / LatticeBlocks;
        double fz = (z - NodeOriginBlocks) / LatticeBlocks;
        int i0 = (int)Math.Floor(fx), j0 = (int)Math.Floor(fz);
        double tx = fx - i0, tz = fz - j0;

        double sum = 0, weight = 0;
        Accumulate(node(i0, j0), (1 - tx) * (1 - tz), ref sum, ref weight);
        Accumulate(node(i0 + 1, j0), tx * (1 - tz), ref sum, ref weight);
        Accumulate(node(i0, j0 + 1), (1 - tx) * tz, ref sum, ref weight);
        Accumulate(node(i0 + 1, j0 + 1), tx * tz, ref sum, ref weight);
        return weight > 1e-6 ? sum / weight : double.NaN;
    }

    static void Accumulate(double value, double w, ref double sum, ref double weight)
    {
        if (double.IsNaN(value) || w <= 0) return;
        sum += value * w;
        weight += w;
    }

    /// <summary>
    /// Bit for one 64-block level-0 cell inside a tile: 8x8 of them, bit = localZ * 8 + localX.
    /// </summary>
    public static int CoverageBit(int localCellX, int localCellZ) => localCellZ * 8 + localCellX;

    /// <summary>
    /// Build one tile's mesh from the nodes it can read. <paramref name="nodes"/> is
    /// <see cref="TileNodeSpan"/> square, indexed [j - firstNodeZ, i - firstNodeX], NaN where
    /// unknown. A quad is drawn when all four corners are known and its level-0 cell is not
    /// covered by cached terrain; a vertex touching a covered cell sinks. Heights are clamped to
    /// sea level, which is Farseer's flat sea. Null when no quad is drawn.
    /// </summary>
    public static LodSilhouetteMeshArrays? BuildMesh(int tileX, int tileZ, double[,] nodes,
        ulong coveredCells, int seaLevel, int quadsPerEdge = DefaultQuadsPerEdge,
        float sinkBlocks = DefaultSinkBlocks)
    {
        if (quadsPerEdge < 1 || LodSilhouetteTile.TileBlocks % quadsPerEdge != 0)
            throw new ArgumentOutOfRangeException(nameof(quadsPerEdge));
        if (coveredCells == ulong.MaxValue) return null;

        int firstX = FirstNodeOfTile(tileX), firstZ = FirstNodeOfTile(tileZ);
        double Node(int i, int j)
        {
            int a = i - firstX, b = j - firstZ;
            return a < 0 || b < 0 || a >= nodes.GetLength(1) || b >= nodes.GetLength(0)
                ? double.NaN
                : nodes[b, a];
        }

        int spacing = LodSilhouetteTile.TileBlocks / quadsPerEdge;
        int verts = quadsPerEdge + 1;
        double originX = tileX * (double)LodSilhouetteTile.TileBlocks;
        double originZ = tileZ * (double)LodSilhouetteTile.TileBlocks;

        var heights = new double[verts * verts];
        for (int b = 0; b < verts; b++)
            for (int a = 0; a < verts; a++)
                heights[b * verts + a] = HeightAt(originX + a * spacing, originZ + b * spacing, Node);

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
                if (double.IsNaN(heights[b * verts + a]) || double.IsNaN(heights[b * verts + a + 1])
                    || double.IsNaN(heights[(b + 1) * verts + a])
                    || double.IsNaN(heights[(b + 1) * verts + a + 1])) continue;
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
                double h = heights[v];
                float y = double.IsNaN(h) ? seaLevel : (float)Math.Max(h, seaLevel);
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
