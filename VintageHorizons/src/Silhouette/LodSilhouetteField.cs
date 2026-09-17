namespace VintageHorizons.Silhouette;

/// <summary>A tile's mesh as plain arrays: built on a worker, uploaded on the render thread.</summary>
public sealed class LodSilhouetteMeshArrays
{
    public required float[] Xyz;
    public required float[] EdgeDistanceUv;
    public required int[] Indices;
    public required int VertexCount;
    public required int IndexCount;
    public required int QuadsPerEdge;
    public required float MinY;
    public required float MaxY;

    public long ByteCount => ((long)Xyz.Length + EdgeDistanceUv.Length) * sizeof(float)
        + (long)Indices.Length * sizeof(int);
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

    /// <summary>Every cell covered by <paramref name="other"/> is also covered here.</summary>
    public bool Contains(LodSilhouetteCoverage other) =>
        (Word0 & other.Word0) == other.Word0
        && (Word1 & other.Word1) == other.Word1
        && (Word2 & other.Word2) == other.Word2
        && (Word3 & other.Word3) == other.Word3;

    public LodSilhouetteCoverage Union(LodSilhouetteCoverage other) => new(
        Word0 | other.Word0, Word1 | other.Word1,
        Word2 | other.Word2, Word3 | other.Word3);
}

/// <summary>The three P4 meshes published atomically for one 512-block tile.</summary>
public sealed class LodSilhouetteMeshSet
{
    public required LodSilhouetteMeshArrays High;
    public required LodSilhouetteMeshArrays Medium;
    public required LodSilhouetteMeshArrays Low;
    public required LodSilhouetteCoverage CoveredCells;

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
/// Everything below is engine-free. Farseer keeps its own broad sampled shape; a short apron at
/// the cached-terrain frontier slopes underneath it instead of trying to reproduce its exact edge.
/// </summary>
public static class LodSilhouetteField
{
    public const int LatticeChunks = 8;
    public const int LatticeOffsetChunks = 4;
    public const int LatticeBlocks = LatticeChunks * LodSilhouetteTile.ChunkBlocks;
    public const double NodeOriginBlocks =
        LatticeOffsetChunks * LodSilhouetteTile.ChunkBlocks + LodSilhouetteTile.ChunkBlocks / 2.0;

    // Thirty-two-, sixty-four- and 128-block quads. Farseer is a broad horizon, not voxel
    // terrain: this is one quarter of 0.4.92's already reduced triangle count.
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
    // Near partial tiles are only a hole-filling underlay. The player can trade gap coverage for
    // a less sunken transition without rebuilding a mesh; the vertex shader removes the chosen
    // depth before the distant fade begins.
    public const float MinUnderlayDepthBlocks = 0f;
    public const float MaxUnderlayDepthBlocks = 128f;
    public const float DefaultUnderlayDepthBlocks = 64f;
    public const float MinUnderlayWidthBlocks = 32f;
    public const float MaxUnderlayWidthBlocks = 1024f;
    public const float DefaultUnderlayWidthBlocks = 384f;

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
    /// Builds all three meshes from the same filled field. Every tile stays complete even while
    /// cached data claims its whole footprint: render readiness, not data availability, performs
    /// the live handoff. Keeping the fallback geometry resident means a failed or lost
    /// replacement immediately reveals Farseer rather than sky.
    /// </summary>
    public static LodSilhouetteMeshSet? BuildMeshes(LodSilhouetteTile?[,] neighbourhood,
        LodSilhouetteCoverage coveredCells, int seaLevel,
        float hillSoftness = DefaultHillSoftness) => BuildMeshes(neighbourhood,
            CentreCoverageNeighbourhood(coveredCells), seaLevel, hillSoftness);

    /// <summary>
    /// Builds a tile with the surrounding cached-terrain masks that shape its edge apron. One
    /// neighbouring tile is enough because the apron is narrower than a silhouette tile.
    /// </summary>
    public static LodSilhouetteMeshSet? BuildMeshes(LodSilhouetteTile?[,] neighbourhood,
        LodSilhouetteCoverage[,] coverageNeighbourhood, int seaLevel,
        float hillSoftness = DefaultHillSoftness)
    {
        ValidateCoverageNeighbourhood(coverageNeighbourhood);
        LodSilhouetteCoverage coveredCells = coverageNeighbourhood[1, 1];

        float[] filled = LodSilhouetteFill.FillCentreFeathered(neighbourhood,
            FeatherRadiusFor(hillSoftness), FeatherStrengthFor(hillSoftness));
        LodSilhouetteMeshArrays? high = BuildMeshFromFilled(
            filled, coveredCells, coverageNeighbourhood, seaLevel, HighQuadsPerEdge);
        LodSilhouetteMeshArrays? medium = BuildMeshFromFilled(
            filled, coveredCells, coverageNeighbourhood, seaLevel, MediumQuadsPerEdge);
        LodSilhouetteMeshArrays? low = BuildMeshFromFilled(
            filled, coveredCells, coverageNeighbourhood, seaLevel, LowQuadsPerEdge);
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
        LodSilhouetteCoverage coveredCells, int seaLevel,
        int quadsPerEdge = DefaultQuadsPerEdge,
        float hillSoftness = DefaultHillSoftness) => BuildMeshFromFilled(
            LodSilhouetteFill.FillCentreFeathered(
                neighbourhood, FeatherRadiusFor(hillSoftness),
                FeatherStrengthFor(hillSoftness)),
            coveredCells, CentreCoverageNeighbourhood(coveredCells), seaLevel, quadsPerEdge);

    static LodSilhouetteMeshArrays? BuildMeshFromFilled(float[] filled,
        LodSilhouetteCoverage coveredCells, LodSilhouetteCoverage[,] coverageNeighbourhood,
        int seaLevel, int quadsPerEdge)
    {
        if (quadsPerEdge < 1 || LodSilhouetteTile.CellsPerEdge % quadsPerEdge != 0)
            throw new ArgumentOutOfRangeException(nameof(quadsPerEdge));

        int sourceStride = LodSilhouetteTile.CellsPerEdge + 1;
        int cellStep = LodSilhouetteTile.CellsPerEdge / quadsPerEdge;
        int spacing = cellStep * LodSilhouetteTile.CellBlocks;
        int verts = quadsPerEdge + 1;
        var xyz = new float[verts * verts * 3];
        var edgeDistanceUv = new float[verts * verts * 2];
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
                float y = raw;
                if (float.IsNaN(y)) y = seaLevel;
                // Coverage can arrive before a renderable replacement. Keep fallback geometry at
                // Farseer's ordinary flat-sea height everywhere; the live readiness mask decides
                // which fragments may disappear and when the edge apron may sink.
                y = Math.Max(y, seaLevel);

                xyz[v * 3] = blockX;
                xyz[v * 3 + 1] = y;
                xyz[v * 3 + 2] = blockZ;
                edgeDistanceUv[v * 2] = EdgeDistance(
                    coverageNeighbourhood, blockX, blockZ);
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
            EdgeDistanceUv = edgeDistanceUv,
            Indices = indices,
            VertexCount = verts * verts,
            IndexCount = indices.Length,
            QuadsPerEdge = quadsPerEdge,
            MinY = minY,
            MaxY = maxY,
        };
    }

    static LodSilhouetteCoverage[,] CentreCoverageNeighbourhood(
        LodSilhouetteCoverage coveredCells)
    {
        var result = new LodSilhouetteCoverage[3, 3];
        result[1, 1] = coveredCells;
        return result;
    }

    static void ValidateCoverageNeighbourhood(LodSilhouetteCoverage[,] masks)
    {
        if (masks.GetLength(0) != 3 || masks.GetLength(1) != 3)
            throw new ArgumentException("Coverage neighbourhood must be [3,3].", nameof(masks));
    }

    /// <summary>
    /// Distance from the cached frontier, capped at the widest permitted apron. The shader turns
    /// it into a slope using the player's live width and depth dials, so neither one remeshes.
    /// </summary>
    static float EdgeDistance(LodSilhouetteCoverage[,] masks, int blockX, int blockZ)
    {
        float nearestSquared = float.MaxValue;
        for (int tileZ = 0; tileZ < 3; tileZ++)
        {
            for (int tileX = 0; tileX < 3; tileX++)
            {
                LodSilhouetteCoverage mask = masks[tileZ, tileX];
                if (mask.IsEmpty) continue;
                int tileMinX = (tileX - 1) * LodSilhouetteTile.TileBlocks;
                int tileMinZ = (tileZ - 1) * LodSilhouetteTile.TileBlocks;
                for (int cellZ = 0; cellZ < CoverageCellsPerEdge; cellZ++)
                {
                    for (int cellX = 0; cellX < CoverageCellsPerEdge; cellX++)
                    {
                        if (!mask.Covered(cellX, cellZ)) continue;
                        int minX = tileMinX + cellX * CoverageCellBlocks;
                        int minZ = tileMinZ + cellZ * CoverageCellBlocks;
                        int maxX = minX + CoverageCellBlocks;
                        int maxZ = minZ + CoverageCellBlocks;
                        int dx = blockX < minX ? minX - blockX
                            : blockX > maxX ? blockX - maxX : 0;
                        int dz = blockZ < minZ ? minZ - blockZ
                            : blockZ > maxZ ? blockZ - maxZ : 0;
                        float squared = (float)dx * dx + (float)dz * dz;
                        if (squared < nearestSquared) nearestSquared = squared;
                    }
                }
            }
        }

        if (nearestSquared == float.MaxValue) return MaxUnderlayWidthBlocks;
        return Math.Min(MaxUnderlayWidthBlocks, MathF.Sqrt(nearestSquared));
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
