namespace VintageHorizons.Silhouette;

/// <summary>A tile's mesh as plain arrays: built on a worker, uploaded on the render thread.</summary>
public sealed class LodSilhouetteMeshArrays
{
    public required float[] Xyz;
    public required int[] Indices;
    public required int VertexCount;
    public required int IndexCount;
    public required int QuadsPerEdge;
    public required float MinY;
    public required float MaxY;

    public long ByteCount => (long)Xyz.Length * sizeof(float) + (long)Indices.Length * sizeof(int);
}

/// <summary>The three P4 meshes published atomically for one 512-block tile.</summary>
public sealed class LodSilhouetteMeshSet
{
    public required LodSilhouetteMeshArrays High;
    public required LodSilhouetteMeshArrays Medium;
    public required LodSilhouetteMeshArrays Low;
    public required ulong CoveredCells;

    public long ByteCount => High.ByteCount + Medium.ByteCount + Low.ByteCount;
    public float MinY => Math.Min(High.MinY, Math.Min(Medium.MinY, Low.MinY));
    public float MaxY => Math.Max(High.MaxY, Math.Max(Medium.MaxY, Low.MaxY));

    public LodSilhouetteMeshArrays At(int quadsPerEdge) => quadsPerEdge switch
    {
        LodSilhouetteField.HighQuadsPerEdge => High,
        LodSilhouetteField.MediumQuadsPerEdge => Medium,
        LodSilhouetteField.LowQuadsPerEdge => Low,
        _ => throw new ArgumentOutOfRangeException(nameof(quadsPerEdge)),
    };
}

/// <summary>
/// Where the server samples, and how the client turns a tile into the three meshes P4 draws.
/// Everything below is engine-free: live cached terrain is copied into the optional anchor
/// array on the main thread before a worker enters this code.
/// </summary>
public static class LodSilhouetteField
{
    public const int LatticeChunks = 8;
    public const int LatticeOffsetChunks = 4;
    public const int LatticeBlocks = LatticeChunks * LodSilhouetteTile.ChunkBlocks;
    public const double NodeOriginBlocks =
        LatticeOffsetChunks * LodSilhouetteTile.ChunkBlocks + LodSilhouetteTile.ChunkBlocks / 2.0;

    // Four-, sixteen- and sixty-four-block quads. Every edge is a divisor of the 512-block tile,
    // and the fine meshes pin their outer edge to the low mesh's 64-block line so mixed-resolution
    // neighbours cannot open a crack.
    public const int HighQuadsPerEdge = 128;
    public const int MediumQuadsPerEdge = 32;
    public const int LowQuadsPerEdge = 8;
    public const int DefaultQuadsPerEdge = MediumQuadsPerEdge;
    public const float DefaultSinkBlocks = 2f;
    public const float JoinBlendBlocks = LodSection.SectionBlocks;
    public const int AnchorVerticesPerEdge = HighQuadsPerEdge + 1;

    public static bool IsLatticeChunk(int cx, int cz) =>
        Mod(cx, LatticeChunks) == LatticeOffsetChunks && Mod(cz, LatticeChunks) == LatticeOffsetChunks;

    public static int NodeOfLatticeChunk(int c) => (c - LatticeOffsetChunks) / LatticeChunks;
    public static int LatticeChunkOfNode(int n) => n * LatticeChunks + LatticeOffsetChunks;
    public static double NodeBlockX(int i) => NodeOriginBlocks + (double)i * LatticeBlocks;

    public static IEnumerable<(int TileX, int TileZ)> TilesReading(int tileX, int tileZ)
    {
        int radius = LodSilhouetteFill.NeighbourhoodTiles / 2;
        for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
                yield return (tileX + dx, tileZ + dz);
    }

    public static int CoverageBit(int localCellX, int localCellZ) => localCellZ * 8 + localCellX;

    public static bool Covered(ulong coveredCells, int localCellX, int localCellZ) =>
        localCellX >= 0 && localCellX < 8 && localCellZ >= 0 && localCellZ < 8
        && (coveredCells & (1UL << CoverageBit(localCellX, localCellZ))) != 0;

    /// <summary>
    /// Selects the cheapest mesh whose quads remain at most two pixels wide. The decision is
    /// screen-size driven, so changing resolution or field of view moves the transition rather
    /// than silently changing silhouette quality.
    /// </summary>
    public static int ResolutionFor(float blockScale, double viewDepth,
        float targetPixels = LodAutoThresholds.DefaultTargetPixels)
    {
        if (!(blockScale > 0f) || !float.IsFinite(blockScale) || !(viewDepth > 0))
            return LowQuadsPerEdge;
        float target = Math.Clamp(float.IsFinite(targetPixels)
            ? targetPixels : LodAutoThresholds.DefaultTargetPixels,
            LodAutoThresholds.MinTargetPixels, LodAutoThresholds.MaxTargetPixels);
        double blockPixels = blockScale / viewDepth;
        if (blockPixels * 16 > target) return HighQuadsPerEdge;
        if (blockPixels * 64 > target) return MediumQuadsPerEdge;
        return LowQuadsPerEdge;
    }

    /// <summary>
    /// Builds all three meshes from the same filled field. A fully covered tile needs no GPU
    /// object. Partial coverage stays in the mesh and is discarded exactly per 64-block cell by
    /// the fragment shader; boundary vertices are pulled to copied cached heights and sunk a
    /// little so real terrain owns the depth test.
    /// </summary>
    public static LodSilhouetteMeshSet? BuildMeshes(LodSilhouetteTile?[,] neighbourhood,
        ulong coveredCells, int seaLevel, float[]? anchors = null,
        float sinkBlocks = DefaultSinkBlocks)
    {
        if (coveredCells == ulong.MaxValue) return null;
        if (anchors != null && anchors.Length != AnchorVerticesPerEdge * AnchorVerticesPerEdge)
            throw new ArgumentException("Silhouette anchors must be a 129x129 high-resolution grid.",
                nameof(anchors));

        float[] filled = LodSilhouetteFill.FillCentre(neighbourhood);
        LodSilhouetteMeshArrays? high = BuildMeshFromFilled(
            filled, coveredCells, seaLevel, HighQuadsPerEdge, anchors, sinkBlocks);
        LodSilhouetteMeshArrays? medium = BuildMeshFromFilled(
            filled, coveredCells, seaLevel, MediumQuadsPerEdge, anchors, sinkBlocks);
        LodSilhouetteMeshArrays? low = BuildMeshFromFilled(
            filled, coveredCells, seaLevel, LowQuadsPerEdge, anchors, sinkBlocks);
        if (high == null || medium == null || low == null) return null;
        return new LodSilhouetteMeshSet
        {
            High = high,
            Medium = medium,
            Low = low,
            CoveredCells = coveredCells,
        };
    }

    /// <summary>Compatibility helper retained for focused checks and diagnostics.</summary>
    public static LodSilhouetteMeshArrays? BuildMesh(LodSilhouetteTile?[,] neighbourhood,
        ulong coveredCells, int seaLevel, int quadsPerEdge = DefaultQuadsPerEdge,
        float sinkBlocks = DefaultSinkBlocks) => coveredCells == ulong.MaxValue
            ? null
            : BuildMeshFromFilled(LodSilhouetteFill.FillCentre(neighbourhood), coveredCells,
                seaLevel, quadsPerEdge, null, sinkBlocks);

    static LodSilhouetteMeshArrays? BuildMeshFromFilled(float[] filled, ulong coveredCells,
        int seaLevel, int quadsPerEdge, float[]? anchors, float sinkBlocks)
    {
        if (quadsPerEdge < 1 || LodSilhouetteTile.CellsPerEdge % quadsPerEdge != 0)
            throw new ArgumentOutOfRangeException(nameof(quadsPerEdge));

        int sourceStride = LodSilhouetteTile.CellsPerEdge + 1;
        int cellStep = LodSilhouetteTile.CellsPerEdge / quadsPerEdge;
        int spacing = cellStep * LodSilhouetteTile.CellBlocks;
        int verts = quadsPerEdge + 1;
        var xyz = new float[verts * verts * 3];
        var valid = new bool[verts * verts];

        float RawHeight(int a, int b)
        {
            int sourceX = a * cellStep;
            int sourceZ = b * cellStep;
            float height = filled[sourceZ * sourceStride + sourceX];

            // All resolutions share one straight outer edge between the low mesh's vertices.
            // This is the stitch: a fine tile and a coarse neighbour describe the same line.
            if (a != 0 && a != quadsPerEdge && b != 0 && b != quadsPerEdge) return height;
            int lowStep = LodSilhouetteTile.CellsPerEdge / LowQuadsPerEdge;
            if (a == 0 || a == quadsPerEdge)
            {
                int z0 = sourceZ / lowStep * lowStep;
                int z1 = Math.Min(LodSilhouetteTile.CellsPerEdge, z0 + lowStep);
                float h0 = filled[z0 * sourceStride + sourceX];
                float h1 = filled[z1 * sourceStride + sourceX];
                return LerpKnown(h0, h1, (sourceZ - z0) / (float)Math.Max(1, z1 - z0));
            }
            int x0 = sourceX / lowStep * lowStep;
            int x1 = Math.Min(LodSilhouetteTile.CellsPerEdge, x0 + lowStep);
            float left = filled[sourceZ * sourceStride + x0];
            float right = filled[sourceZ * sourceStride + x1];
            return LerpKnown(left, right, (sourceX - x0) / (float)Math.Max(1, x1 - x0));
        }

        float minY = float.MaxValue, maxY = float.MinValue;
        for (int b = 0; b < verts; b++)
        {
            for (int a = 0; a < verts; a++)
            {
                int v = b * verts + a;
                float raw = RawHeight(a, b);
                valid[v] = !float.IsNaN(raw);

                int blockX = a * spacing;
                int blockZ = b * spacing;
                VertexCoverage(coveredCells, blockX, blockZ,
                    out bool touchesCovered, out bool touchesUncovered);
                bool boundary = touchesCovered && touchesUncovered;
                float y = raw;
                if (float.IsNaN(y)) y = seaLevel;
                // Covered interiors retain raw land/seabed height. Everywhere the silhouette can
                // be seen uses Farseer's flat-sea rule.
                if (!touchesCovered || touchesUncovered) y = Math.Max(y, seaLevel);

                int highX = blockX / (LodSilhouetteTile.TileBlocks / HighQuadsPerEdge);
                int highZ = blockZ / (LodSilhouetteTile.TileBlocks / HighQuadsPerEdge);
                float exactAnchor = anchors?[highZ * AnchorVerticesPerEdge + highX] ?? float.NaN;
                if (!float.IsNaN(exactAnchor))
                {
                    // CaptureAnchors marks the real cached edge, including an edge on the next
                    // tile. Honour that evidence even when this tile's own mask cannot describe
                    // the neighbour on the other side.
                    y = exactAnchor - Math.Max(0f, sinkBlocks);
                }
                else if (anchors != null && (!touchesCovered || touchesUncovered)
                    && TryJoinAnchor(anchors, blockX, blockZ,
                        out float joinAnchor, out float joinInfluence))
                {
                    // One anchored vertex followed by an unrelated sampled vertex made a four-
                    // block cliff in the high mesh. Ease across the adjoining 64-block coverage
                    // cell instead. The outermost vertex still sits just under cached terrain;
                    // the field regains its sampled height smoothly by the opposite cell edge.
                    float target = joinAnchor - Math.Max(0f, sinkBlocks);
                    y += (target - y) * joinInfluence;
                }
                else if (boundary)
                {
                    y -= Math.Max(0f, sinkBlocks);
                }

                xyz[v * 3] = blockX;
                xyz[v * 3 + 1] = y;
                xyz[v * 3 + 2] = blockZ;
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        var indices = new int[quadsPerEdge * quadsPerEdge * 6];
        int n = 0;
        for (int b = 0; b < quadsPerEdge; b++)
        {
            for (int a = 0; a < quadsPerEdge; a++)
            {
                int topLeft = b * verts + a;
                int bottomLeft = (b + 1) * verts + a;
                if (!valid[topLeft] || !valid[topLeft + 1]
                    || !valid[bottomLeft] || !valid[bottomLeft + 1]) continue;
                indices[n++] = topLeft;
                indices[n++] = bottomLeft;
                indices[n++] = topLeft + 1;
                indices[n++] = topLeft + 1;
                indices[n++] = bottomLeft;
                indices[n++] = bottomLeft + 1;
            }
        }
        if (n == 0) return null;
        if (n != indices.Length) Array.Resize(ref indices, n);
        return new LodSilhouetteMeshArrays
        {
            Xyz = xyz,
            Indices = indices,
            VertexCount = verts * verts,
            IndexCount = indices.Length,
            QuadsPerEdge = quadsPerEdge,
            MinY = minY,
            MaxY = maxY,
        };
    }

    static bool TryJoinAnchor(float[] anchors, int blockX, int blockZ,
        out float height, out float influence)
    {
        int cellX = Math.Min(7, blockX / LodSection.SectionBlocks);
        int cellZ = Math.Min(7, blockZ / LodSection.SectionBlocks);
        int minX = cellX * LodSection.SectionBlocks;
        int minZ = cellZ * LodSection.SectionBlocks;
        int maxX = minX + LodSection.SectionBlocks;
        int maxZ = minZ + LodSection.SectionBlocks;
        int highSpacing = LodSilhouetteTile.TileBlocks / HighQuadsPerEdge;
        float weightedHeight = 0f;
        float totalWeight = 0f;

        void Add(int anchorX, int anchorZ, int distance)
        {
            if (distance < 0 || distance >= JoinBlendBlocks) return;
            int highX = anchorX / highSpacing;
            int highZ = anchorZ / highSpacing;
            float anchor = anchors[highZ * AnchorVerticesPerEdge + highX];
            if (float.IsNaN(anchor)) return;
            float t = Math.Clamp(distance / JoinBlendBlocks, 0f, 1f);
            float smooth = t * t * (3f - 2f * t);
            float weight = 1f - smooth;
            weightedHeight += anchor * weight;
            totalWeight += weight;
        }

        Add(minX, blockZ, blockX - minX);
        Add(maxX, blockZ, maxX - blockX);
        Add(blockX, minZ, blockZ - minZ);
        Add(blockX, maxZ, maxZ - blockZ);

        height = totalWeight > 0f ? weightedHeight / totalWeight : float.NaN;
        // Two cached sides of a one-cell gap should bridge between both live edges instead of
        // reintroducing the unrelated sampled height at the centre.
        influence = Math.Min(1f, totalWeight);
        return totalWeight > 0f;
    }

    static void VertexCoverage(ulong mask, int blockX, int blockZ,
        out bool touchesCovered, out bool touchesUncovered)
    {
        touchesCovered = false;
        touchesUncovered = false;
        int cellX = Math.Min(7, blockX / LodSection.SectionBlocks);
        int cellZ = Math.Min(7, blockZ / LodSection.SectionBlocks);
        int minDx = blockX > 0 && blockX < LodSilhouetteTile.TileBlocks
            && blockX % LodSection.SectionBlocks == 0 ? -1 : 0;
        int minDz = blockZ > 0 && blockZ < LodSilhouetteTile.TileBlocks
            && blockZ % LodSection.SectionBlocks == 0 ? -1 : 0;
        for (int dz = minDz; dz <= 0; dz++)
        {
            for (int dx = minDx; dx <= 0; dx++)
            {
                int x = cellX + dx, z = cellZ + dz;
                if (x < 0 || x >= 8 || z < 0 || z >= 8) continue;
                if (Covered(mask, x, z)) touchesCovered = true;
                else touchesUncovered = true;
            }
        }
    }

    static float LerpKnown(float a, float b, float t) => float.IsNaN(a) ? b
        : float.IsNaN(b) ? a
        : a + (b - a) * t;

    static int Mod(int value, int m)
    {
        int r = value % m;
        return r < 0 ? r + m : r;
    }
}
