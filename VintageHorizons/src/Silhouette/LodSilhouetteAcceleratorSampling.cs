namespace VintageHorizons.Silhouette;

/// <summary>
/// Pure geometry for the live P3 accelerator path. A coarse tile is sampled every 64 blocks,
/// matching the accepted plan. Engine previews remain the higher-quality fallback and refine one
/// low/coastal chunk where the coarse answers say the waterline may matter.
/// </summary>
internal static class LodSilhouetteAcceleratorSampling
{
    public const int CoarseSpacingBlocks = 64;
    public const int CoarseCellStep = CoarseSpacingBlocks / LodSilhouetteTile.CellBlocks;
    public const int CoastBandBlocks = 4;
    public const int SamplesPerEdge = LodSilhouetteTile.TileBlocks / CoarseSpacingBlocks;
    public const int SamplesPerTile = SamplesPerEdge * SamplesPerEdge;

    public static (int X, int Z)[] Positions(int tileX, int tileZ)
    {
        var positions = new (int X, int Z)[SamplesPerTile];
        int x0 = tileX * LodSilhouetteTile.TileBlocks;
        int z0 = tileZ * LodSilhouetteTile.TileBlocks;
        int n = 0;
        for (int z = 0; z < SamplesPerEdge; z++)
            for (int x = 0; x < SamplesPerEdge; x++)
                positions[n++] = (x0 + x * CoarseSpacingBlocks, z0 + z * CoarseSpacingBlocks);
        return positions;
    }

    /// <summary>True while at least one coarse point is not already known from any source.</summary>
    public static bool NeedsSamples(LodSilhouetteTile? tile)
    {
        if (tile == null) return true;
        for (int z = 0; z < SamplesPerEdge; z++)
            for (int x = 0; x < SamplesPerEdge; x++)
                if (tile.OriginAt(x * CoarseCellStep, z * CoarseCellStep)
                    == EnumSilhouetteOrigin.Unknown) return true;
        return false;
    }

    /// <summary>Apply one complete coarse result. Invalid heights fail the whole result.</summary>
    public static bool Apply(LodSilhouetteTile tile, IReadOnlyList<int> heights)
    {
        if (heights.Count != SamplesPerTile)
            throw new ArgumentException($"A coarse tile needs {SamplesPerTile} heights.", nameof(heights));
        for (int i = 0; i < heights.Count; i++)
            if ((uint)heights[i] > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(heights), "An accelerator height is outside ushort range.");

        bool changed = false;
        int n = 0;
        for (int z = 0; z < SamplesPerEdge; z++)
        {
            for (int x = 0; x < SamplesPerEdge; x++)
            {
                changed |= tile.SetCell(x * CoarseCellStep, z * CoarseCellStep,
                    (ushort)heights[n++], EnumSilhouetteOrigin.Accelerator);
            }
        }
        return changed;
    }

    /// <summary>
    /// Pick one chunk for an engine preview when the coarse tile crosses the waterline, or is low
    /// dry ground close enough to hide a lake. Pure ocean and confidently inland terrain need no
    /// preview. The preview outranks these samples cell by cell.
    /// </summary>
    public static bool TryCoastPreview(int tileX, int tileZ, IReadOnlyList<int> heights, int seaLevel,
        out int chunkX, out int chunkZ)
    {
        chunkX = chunkZ = 0;
        if (heights.Count != SamplesPerTile) return false;

        int x0 = tileX * LodSilhouetteTile.TileBlocks;
        int z0 = tileZ * LodSilhouetteTile.TileBlocks;
        for (int z = 0; z < SamplesPerEdge; z++)
        {
            for (int x = 0; x < SamplesPerEdge; x++)
            {
                int here = heights[z * SamplesPerEdge + x];
                if (x + 1 < SamplesPerEdge
                    && (here <= seaLevel) != (heights[z * SamplesPerEdge + x + 1] <= seaLevel))
                {
                    chunkX = (x0 + x * CoarseSpacingBlocks + CoarseSpacingBlocks / 2) >> 5;
                    chunkZ = (z0 + z * CoarseSpacingBlocks) >> 5;
                    return true;
                }
                if (z + 1 < SamplesPerEdge
                    && (here <= seaLevel) != (heights[(z + 1) * SamplesPerEdge + x] <= seaLevel))
                {
                    chunkX = (x0 + x * CoarseSpacingBlocks) >> 5;
                    chunkZ = (z0 + z * CoarseSpacingBlocks + CoarseSpacingBlocks / 2) >> 5;
                    return true;
                }
            }
        }

        // A sampler can turn a real lake into low land without a sampled wet neighbour. Spend one
        // preview on the lowest dry point near sea level; confidently inland tiles stay cheap.
        int best = -1, bestDistance = int.MaxValue;
        for (int i = 0; i < heights.Count; i++)
        {
            int height = heights[i];
            if (height <= seaLevel || height > seaLevel + CoastBandBlocks) continue;
            int distance = Math.Abs(height - seaLevel);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }
        if (best < 0) return false;

        int sampleX = best % SamplesPerEdge;
        int sampleZ = best / SamplesPerEdge;
        int blockX = x0 + sampleX * CoarseSpacingBlocks;
        int blockZ = z0 + sampleZ * CoarseSpacingBlocks;
        chunkX = blockX >> 5;
        chunkZ = blockZ >> 5;
        return true;
    }
}
