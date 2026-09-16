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

/// <summary>
/// Exact 32-block cached-terrain coverage for one 512-block silhouette tile. Four words hold the
/// 16x16 chunk footprints without allocating an array per tile or per mesh job.
/// </summary>
public readonly record struct LodSilhouetteCoverage(
    ulong Word0, ulong Word1, ulong Word2, ulong Word3)
{
    public static LodSilhouetteCoverage Empty => default;
    public static LodSilhouetteCoverage Full => new(
        ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue);

    public bool IsEmpty => (Word0 | Word1 | Word2 | Word3) == 0;
    public bool IsFull => Word0 == ulong.MaxValue && Word1 == ulong.MaxValue
        && Word2 == ulong.MaxValue && Word3 == ulong.MaxValue;

    public ulong Word(int index) => index switch
    {
        0 => Word0,
        1 => Word1,
        2 => Word2,
        3 => Word3,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public uint IntWord(int index)
    {
        ulong word = Word(index >> 1);
        return (uint)(index % 2 == 0 ? word : word >> 32);
    }

    public ushort ShortWord(int index)
    {
        ulong word = Word(index >> 2);
        return (ushort)(word >> ((index & 3) * 16));
    }

    public bool Covered(int localCellX, int localCellZ)
    {
        if ((uint)localCellX >= LodSilhouetteField.CoverageCellsPerEdge
            || (uint)localCellZ >= LodSilhouetteField.CoverageCellsPerEdge) return false;
        int bit = localCellZ * LodSilhouetteField.CoverageCellsPerEdge + localCellX;
        return (Word(bit >> 6) & (1UL << (bit & 63))) != 0;
    }

    public LodSilhouetteCoverage WithCovered(int localCellX, int localCellZ)
    {
        int bit = localCellZ * LodSilhouetteField.CoverageCellsPerEdge + localCellX;
        ulong flag = 1UL << (bit & 63);
        return (bit >> 6) switch
        {
            0 => this with { Word0 = Word0 | flag },
            1 => this with { Word1 = Word1 | flag },
            2 => this with { Word2 = Word2 | flag },
            3 => this with { Word3 = Word3 | flag },
            _ => throw new ArgumentOutOfRangeException(nameof(localCellX)),
        };
    }
}

/// <summary>The three P4 meshes published atomically for one 512-block tile.</summary>
public sealed class LodSilhouetteMeshSet
{
    public required LodSilhouetteMeshArrays High;
    public required LodSilhouetteMeshArrays Medium;
    public required LodSilhouetteMeshArrays Low;
    public required LodSilhouetteCoverage CoveredCells;
    public required bool HasAnchors;

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

    // Thirty-two-, sixty-four- and 128-block quads. Farseer is a broad horizon, not voxel
    // terrain: this is one quarter of 0.4.92's already reduced triangle count. Join tiles use
    // the 32-block tier so every real chunk boundary still has an exact cached-terrain vertex.
    public const int HighQuadsPerEdge = 16;
    public const int MediumQuadsPerEdge = 8;
    public const int LowQuadsPerEdge = 4;
    public const int DefaultQuadsPerEdge = MediumQuadsPerEdge;
    public const float MinOutlineDetail = 0f;
    public const float MaxOutlineDetail = 1f;
    public const float DefaultOutlineDetail = 0.35f;
    public const float SoftTargetPixels = 24f;
    public const float SharpTargetPixels = 2f;
    public const int CoverageCellBlocks = LodSilhouetteTile.ChunkBlocks;
    public const int CoverageCellsPerEdge = LodSilhouetteTile.TileBlocks / CoverageCellBlocks;
    public const float MinHillSoftness = 0f;
    public const float MaxHillSoftness = 1f;
    public const float DefaultHillSoftness = 0.75f;
    public const int MaxFeatherRadiusCells = 32;
    // The ordinary Farseer surface sits well below the cached-terrain edge. A lightweight join
    // skirt bridges back to the exact live height, so the cache can overlap the silhouette without
    // either z-fighting or exposing sky through the height difference.
    public const float DefaultSinkBlocks = 32f;
    public const float JoinOverlapBlocks = 4f;
    public const float JoinBlendBlocks = CoverageCellBlocks;
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

    /// <summary>
    /// Selects the cheapest mesh allowed by the player's geometry-detail dial. The decision is
    /// screen-size driven, so changing resolution or field of view still moves the transition.
    /// Low detail tolerates wider on-screen quads and therefore draws much less geometry.
    /// </summary>
    public static int ResolutionFor(float blockScale, double viewDepth,
        float outlineDetail = DefaultOutlineDetail)
    {
        if (!(blockScale > 0f) || !float.IsFinite(blockScale) || !(viewDepth > 0))
            return LowQuadsPerEdge;
        float target = TargetPixelsForDetail(outlineDetail);
        double blockPixels = blockScale / viewDepth;
        int mediumSpacing = LodSilhouetteTile.TileBlocks / MediumQuadsPerEdge;
        int lowSpacing = LodSilhouetteTile.TileBlocks / LowQuadsPerEdge;
        if (blockPixels * mediumSpacing > target) return HighQuadsPerEdge;
        if (blockPixels * lowSpacing > target) return MediumQuadsPerEdge;
        return LowQuadsPerEdge;
    }

    public static float NormalizeOutlineDetail(float value) => Math.Clamp(
        float.IsFinite(value) ? value : DefaultOutlineDetail,
        MinOutlineDetail, MaxOutlineDetail);

    public static float TargetPixelsForDetail(float outlineDetail)
    {
        float detail = NormalizeOutlineDetail(outlineDetail);
        return SoftTargetPixels * MathF.Pow(SharpTargetPixels / SoftTargetPixels, detail);
    }

    public static float NormalizeHillSoftness(float value) => Math.Clamp(
        float.IsFinite(value) ? value : DefaultHillSoftness,
        MinHillSoftness, MaxHillSoftness);

    public static int FeatherRadiusFor(float hillSoftness) => (int)MathF.Round(
        NormalizeHillSoftness(hillSoftness) * MaxFeatherRadiusCells);

    public static float FeatherStrengthFor(float hillSoftness) =>
        NormalizeHillSoftness(hillSoftness);

    /// <summary>
    /// Builds all three meshes from the same filled field. A fully covered tile needs no GPU
    /// object. Partial coverage stays in the mesh and is discarded exactly per 32-block chunk by
    /// the fragment shader; boundary vertices are pulled below copied cached heights and the high
    /// mesh receives a narrow curtain back to the exact edge so real terrain owns the depth test.
    /// </summary>
    public static LodSilhouetteMeshSet? BuildMeshes(LodSilhouetteTile?[,] neighbourhood,
        LodSilhouetteCoverage coveredCells, int seaLevel, float[]? anchors = null,
        float sinkBlocks = DefaultSinkBlocks, float hillSoftness = DefaultHillSoftness)
    {
        if (coveredCells.IsFull) return null;
        if (anchors != null && anchors.Length != AnchorVerticesPerEdge * AnchorVerticesPerEdge)
            throw new ArgumentException(
                $"Silhouette anchors must be a {AnchorVerticesPerEdge}x{AnchorVerticesPerEdge} high-resolution grid.",
                nameof(anchors));

        float[] filled = LodSilhouetteFill.FillCentreFeathered(neighbourhood,
            FeatherRadiusFor(hillSoftness), FeatherStrengthFor(hillSoftness));
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
            HasAnchors = anchors?.Any(value => !float.IsNaN(value)) == true,
        };
    }

    /// <summary>Compatibility helper retained for focused checks and diagnostics.</summary>
    public static LodSilhouetteMeshArrays? BuildMesh(LodSilhouetteTile?[,] neighbourhood,
        LodSilhouetteCoverage coveredCells, int seaLevel,
        int quadsPerEdge = DefaultQuadsPerEdge, float sinkBlocks = DefaultSinkBlocks,
        float hillSoftness = DefaultHillSoftness) => coveredCells.IsFull
            ? null
            : BuildMeshFromFilled(LodSilhouetteFill.FillCentreFeathered(
                    neighbourhood, FeatherRadiusFor(hillSoftness),
                    FeatherStrengthFor(hillSoftness)),
                coveredCells, seaLevel, quadsPerEdge, null, sinkBlocks);

    static LodSilhouetteMeshArrays? BuildMeshFromFilled(float[] filled,
        LodSilhouetteCoverage coveredCells,
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
                    // tile. The surface deliberately runs below it; AppendJoinSkirts supplies the
                    // missing vertical face so this cannot reopen the old sky slit.
                    y = exactAnchor - Math.Max(0f, sinkBlocks);
                }
                else if (anchors != null && (!touchesCovered || touchesUncovered)
                    && TryJoinAnchor(anchors, blockX, blockZ,
                        out float joinAnchor, out float joinInfluence))
                {
                    // One anchored vertex followed by an unrelated sampled vertex made a hard
                    // cliff in the high mesh. Ease across the adjoining 32-block coverage
                    // cell instead. The outermost vertex still sits just under cached terrain;
                    // the field regains its sampled height smoothly by the opposite cell edge.
                    float target = joinAnchor - Math.Max(0f, sinkBlocks);
                    y += (target - y) * joinInfluence;
                }
                xyz[v * 3] = blockX;
                xyz[v * 3 + 1] = y;
                xyz[v * 3 + 2] = blockZ;
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        var indices = new List<int>(quadsPerEdge * quadsPerEdge * 6);
        for (int b = 0; b < quadsPerEdge; b++)
        {
            for (int a = 0; a < quadsPerEdge; a++)
            {
                int topLeft = b * verts + a;
                int bottomLeft = (b + 1) * verts + a;
                if (!valid[topLeft] || !valid[topLeft + 1]
                    || !valid[bottomLeft] || !valid[bottomLeft + 1]) continue;
                indices.Add(topLeft);
                indices.Add(bottomLeft);
                indices.Add(topLeft + 1);
                indices.Add(topLeft + 1);
                indices.Add(bottomLeft);
                indices.Add(bottomLeft + 1);
            }
        }
        if (indices.Count == 0) return null;

        var allXyz = new List<float>(xyz.Length + AnchorVerticesPerEdge * AnchorVerticesPerEdge * 3);
        allXyz.AddRange(xyz);
        if (quadsPerEdge == HighQuadsPerEdge && anchors != null && sinkBlocks > 0f)
            AppendJoinSkirts(allXyz, indices, valid, coveredCells, anchors,
                Math.Max(0f, sinkBlocks), ref minY, ref maxY);

        return new LodSilhouetteMeshArrays
        {
            Xyz = allXyz.ToArray(),
            Indices = indices.ToArray(),
            VertexCount = allXyz.Count / 3,
            IndexCount = indices.Count,
            QuadsPerEdge = quadsPerEdge,
            MinY = minY,
            MaxY = maxY,
        };
    }

    /// <summary>
    /// Adds only the cached/uncovered boundary faces. Their lower edge reuses the sunk terrain
    /// grid and their upper edge follows live cached heights. Ordinary joins add two triangles per
    /// 32-block edge; even pathological coverage remains tiny beside real terrain geometry.
    /// </summary>
    static void AppendJoinSkirts(List<float> xyz, List<int> indices, bool[] valid,
        LodSilhouetteCoverage coveredCells, float[] anchors, float sinkBlocks,
        ref float minY, ref float maxY)
    {
        int edge = AnchorVerticesPerEdge;
        var topVertices = new int[anchors.Length];
        Array.Fill(topVertices, -1);
        float skirtMinY = minY, skirtMaxY = maxY;

        int TopVertex(int x, int z)
        {
            int anchorIndex = z * edge + x;
            int held = topVertices[anchorIndex];
            if (held >= 0) return held;
            int vertex = xyz.Count / 3;
            float height = anchors[anchorIndex];
            xyz.Add(x * CoverageCellBlocks);
            xyz.Add(height);
            xyz.Add(z * CoverageCellBlocks);
            topVertices[anchorIndex] = vertex;
            skirtMinY = Math.Min(skirtMinY, height - sinkBlocks);
            skirtMaxY = Math.Max(skirtMaxY, height);
            return vertex;
        }

        bool Anchored(int x, int z) => valid[z * edge + x]
            && !float.IsNaN(anchors[z * edge + x]);

        void AddVertical(int x, int z, bool uncoveredPositive)
        {
            if (!Anchored(x, z) || !Anchored(x, z + 1)) return;
            int bottom0 = z * edge + x;
            int bottom1 = (z + 1) * edge + x;
            int top0 = TopVertex(x, z);
            int top1 = TopVertex(x, z + 1);
            if (uncoveredPositive)
            {
                indices.Add(top0); indices.Add(top1); indices.Add(bottom0);
                indices.Add(top1); indices.Add(bottom1); indices.Add(bottom0);
            }
            else
            {
                indices.Add(top0); indices.Add(bottom0); indices.Add(top1);
                indices.Add(top1); indices.Add(bottom0); indices.Add(bottom1);
            }
        }

        void AddHorizontal(int x, int z, bool uncoveredPositive)
        {
            if (!Anchored(x, z) || !Anchored(x + 1, z)) return;
            int bottom0 = z * edge + x;
            int bottom1 = z * edge + x + 1;
            int top0 = TopVertex(x, z);
            int top1 = TopVertex(x + 1, z);
            if (uncoveredPositive)
            {
                indices.Add(top0); indices.Add(bottom0); indices.Add(top1);
                indices.Add(top1); indices.Add(bottom0); indices.Add(bottom1);
            }
            else
            {
                indices.Add(top0); indices.Add(top1); indices.Add(bottom0);
                indices.Add(top1); indices.Add(bottom1); indices.Add(bottom0);
            }
        }

        for (int z = 0; z < CoverageCellsPerEdge; z++)
        {
            for (int x = 0; x <= CoverageCellsPerEdge; x++)
            {
                bool left = x > 0 && coveredCells.Covered(x - 1, z);
                bool right = x < CoverageCellsPerEdge && coveredCells.Covered(x, z);
                if (x > 0 && x < CoverageCellsPerEdge && left == right) continue;
                // An outer anchor proves that the unseen neighbouring cell differs from the local
                // one. Inside the tile the mask directly identifies the uncovered viewing side.
                bool uncoveredPositive = x == 0 ? !right : left;
                AddVertical(x, z, uncoveredPositive);
            }
        }

        for (int z = 0; z <= CoverageCellsPerEdge; z++)
        {
            for (int x = 0; x < CoverageCellsPerEdge; x++)
            {
                bool north = z > 0 && coveredCells.Covered(x, z - 1);
                bool south = z < CoverageCellsPerEdge && coveredCells.Covered(x, z);
                if (z > 0 && z < CoverageCellsPerEdge && north == south) continue;
                bool uncoveredPositive = z == 0 ? !south : north;
                AddHorizontal(x, z, uncoveredPositive);
            }
        }
        minY = skirtMinY;
        maxY = skirtMaxY;
    }

    static bool TryJoinAnchor(float[] anchors, int blockX, int blockZ,
        out float height, out float influence)
    {
        int cellX = Math.Min(CoverageCellsPerEdge - 1, blockX / CoverageCellBlocks);
        int cellZ = Math.Min(CoverageCellsPerEdge - 1, blockZ / CoverageCellBlocks);
        int minX = cellX * CoverageCellBlocks;
        int minZ = cellZ * CoverageCellBlocks;
        int maxX = minX + CoverageCellBlocks;
        int maxZ = minZ + CoverageCellBlocks;
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

    static void VertexCoverage(LodSilhouetteCoverage mask, int blockX, int blockZ,
        out bool touchesCovered, out bool touchesUncovered)
    {
        touchesCovered = false;
        touchesUncovered = false;
        int cellX = Math.Min(CoverageCellsPerEdge - 1, blockX / CoverageCellBlocks);
        int cellZ = Math.Min(CoverageCellsPerEdge - 1, blockZ / CoverageCellBlocks);
        int minDx = blockX > 0 && blockX < LodSilhouetteTile.TileBlocks
            && blockX % CoverageCellBlocks == 0 ? -1 : 0;
        int minDz = blockZ > 0 && blockZ < LodSilhouetteTile.TileBlocks
            && blockZ % CoverageCellBlocks == 0 ? -1 : 0;
        for (int dz = minDz; dz <= 0; dz++)
        {
            for (int dx = minDx; dx <= 0; dx++)
            {
                int x = cellX + dx, z = cellZ + dz;
                if (x < 0 || x >= CoverageCellsPerEdge
                    || z < 0 || z >= CoverageCellsPerEdge) continue;
                if (mask.Covered(x, z)) touchesCovered = true;
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
