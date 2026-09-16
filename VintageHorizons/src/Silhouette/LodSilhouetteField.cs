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
/// Everything below is engine-free. Farseer keeps its own broad sampled shape; the renderer buries
/// partial tiles beneath nearby cached terrain instead of trying to reproduce its exact edge.
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
    // Near partial tiles are only a hole-filling underlay. This is deliberately much deeper than
    // ordinary sampling error, and the vertex shader removes it before the distant fade begins.
    public const float UnderlaySinkBlocks = 96f;

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
    /// object. A partial tile stays complete so its buried surface can fill any raster gap behind
    /// cached terrain without needing a height-perfect join.
    /// </summary>
    public static LodSilhouetteMeshSet? BuildMeshes(LodSilhouetteTile?[,] neighbourhood,
        LodSilhouetteCoverage coveredCells, int seaLevel,
        float hillSoftness = DefaultHillSoftness)
    {
        if (coveredCells.IsFull) return null;

        float[] filled = LodSilhouetteFill.FillCentreFeathered(neighbourhood,
            FeatherRadiusFor(hillSoftness), FeatherStrengthFor(hillSoftness));
        LodSilhouetteMeshArrays? high = BuildMeshFromFilled(
            filled, coveredCells, seaLevel, HighQuadsPerEdge);
        LodSilhouetteMeshArrays? medium = BuildMeshFromFilled(
            filled, coveredCells, seaLevel, MediumQuadsPerEdge);
        LodSilhouetteMeshArrays? low = BuildMeshFromFilled(
            filled, coveredCells, seaLevel, LowQuadsPerEdge);
        if (high == null || medium == null || low == null) return null;
        return new LodSilhouetteMeshSet
        {
            High = high,
            Medium = medium,
            Low = low,
        };
    }

    /// <summary>Compatibility helper retained for focused checks and diagnostics.</summary>
    public static LodSilhouetteMeshArrays? BuildMesh(LodSilhouetteTile?[,] neighbourhood,
        LodSilhouetteCoverage coveredCells, int seaLevel,
        int quadsPerEdge = DefaultQuadsPerEdge,
        float hillSoftness = DefaultHillSoftness) => coveredCells.IsFull
            ? null
            : BuildMeshFromFilled(LodSilhouetteFill.FillCentreFeathered(
                    neighbourhood, FeatherRadiusFor(hillSoftness),
                    FeatherStrengthFor(hillSoftness)),
                coveredCells, seaLevel, quadsPerEdge);

    static LodSilhouetteMeshArrays? BuildMeshFromFilled(float[] filled,
        LodSilhouetteCoverage coveredCells,
        int seaLevel, int quadsPerEdge)
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
