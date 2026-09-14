using System.Buffers;

namespace VintageHorizons.Silhouette;

/// <summary>
/// Heights everywhere from heights somewhere (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER 4.3): a
/// deterministic push-pull over the known cells of a tile and its eight neighbours.
///
/// Push halves the resolution level by level, from 4-block cells up to one value per tile; a
/// coarse cell is the mean of its known children. Pull then works back down: an unknown cell
/// takes the bilinear blend of the coarser cells around it, known ones only. Known cells keep
/// their own heights exactly, and filled values are never stored.
///
/// Every level is aligned to the world grid, and a cell only ever reads the coarser cells that
/// bracket it, which never reach past the tile next to it. So a tile's heights depend on its own
/// neighbourhood alone, and two tiles filled from the same data agree exactly along their shared
/// edge. SilhouetteChecks proves both on fixtures.
/// </summary>
public static class LodSilhouetteFill
{
    /// <summary>Tiles per side of the neighbourhood: the tile and one ring around it.</summary>
    public const int NeighbourhoodTiles = 3;

    /// <summary>Levels from 4-block cells up to one cell per tile: 128, 64, ..., 1 per edge.</summary>
    public const int Levels = 8;

    const int Cells = LodSilhouetteTile.CellsPerEdge;
    const int GridCells = NeighbourhoodTiles * Cells;

    /// <summary>
    /// Filled heights for the centre tile's cells plus one more row and column (its east and
    /// south neighbours' first cells), so a mesh can close its far edges: (128 + 1)^2 values,
    /// row-major, NaN where nothing is known within reach. <paramref name="tiles"/> is
    /// [<see cref="NeighbourhoodTiles"/>, <see cref="NeighbourhoodTiles"/>], indexed [z, x] with
    /// the centre tile in the middle, and null where no tile is held.
    /// </summary>
    public static float[] FillCentre(LodSilhouetteTile?[,] tiles)
    {
        if (tiles.GetLength(0) != NeighbourhoodTiles || tiles.GetLength(1) != NeighbourhoodTiles)
            throw new ArgumentException("A fill reads a 3x3 neighbourhood of tiles.", nameof(tiles));

        // Pooled: the full-resolution grid is past the large-object threshold, and a fresh one
        // per mesh would feed full collections, which show as hitches.
        var grids = new float[Levels][];
        var sizes = new int[Levels];
        for (int level = 0; level < Levels; level++)
        {
            sizes[level] = GridCells >> level;
            grids[level] = ArrayPool<float>.Shared.Rent(sizes[level] * sizes[level]);
        }

        try
        {
            float[] cells = grids[0];
            Array.Fill(cells, float.NaN, 0, GridCells * GridCells);
            for (int tz = 0; tz < NeighbourhoodTiles; tz++)
                for (int tx = 0; tx < NeighbourhoodTiles; tx++)
                    tiles[tz, tx]?.WriteKnownHeights(cells, GridCells, tx * Cells, tz * Cells);

            for (int level = 1; level < Levels; level++)
                Push(grids[level - 1], sizes[level - 1], grids[level], sizes[level]);
            for (int level = Levels - 2; level >= 0; level--)
                Pull(grids[level], sizes[level], grids[level + 1], sizes[level + 1]);

            var result = new float[(Cells + 1) * (Cells + 1)];
            for (int z = 0; z <= Cells; z++)
                Array.Copy(cells, (Cells + z) * GridCells + Cells, result, z * (Cells + 1), Cells + 1);
            return result;
        }
        finally
        {
            foreach (float[] grid in grids) ArrayPool<float>.Shared.Return(grid);
        }
    }

    /// <summary>Each coarse cell is the mean of its known children, NaN when none is known.</summary>
    static void Push(float[] fine, int fineSize, float[] coarse, int size)
    {
        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                int f = 2 * z * fineSize + 2 * x;
                float sum = 0;
                int count = 0;
                Add(fine[f], ref sum, ref count);
                Add(fine[f + 1], ref sum, ref count);
                Add(fine[f + fineSize], ref sum, ref count);
                Add(fine[f + fineSize + 1], ref sum, ref count);
                coarse[z * size + x] = count > 0 ? sum / count : float.NaN;
            }
        }
    }

    /// <summary>
    /// An unknown fine cell blends the four coarse cells that bracket its centre, known ones
    /// only. Coarse cell c's centre sits at fine position 2c + 0.5, so fine cell x's centre is
    /// at coarse position x / 2 - 0.25.
    /// </summary>
    static void Pull(float[] fine, int size, float[] coarse, int coarseSize)
    {
        for (int z = 0; z < size; z++)
        {
            float cz = z * 0.5f - 0.25f;
            int z0 = (int)MathF.Floor(cz);
            float wz = cz - z0;
            for (int x = 0; x < size; x++)
            {
                int i = z * size + x;
                if (!float.IsNaN(fine[i])) continue;
                float cx = x * 0.5f - 0.25f;
                int x0 = (int)MathF.Floor(cx);
                float wx = cx - x0;
                float sum = 0, weight = 0;
                Blend(coarse, coarseSize, x0, z0, (1 - wx) * (1 - wz), ref sum, ref weight);
                Blend(coarse, coarseSize, x0 + 1, z0, wx * (1 - wz), ref sum, ref weight);
                Blend(coarse, coarseSize, x0, z0 + 1, (1 - wx) * wz, ref sum, ref weight);
                Blend(coarse, coarseSize, x0 + 1, z0 + 1, wx * wz, ref sum, ref weight);
                if (weight > 0) fine[i] = sum / weight;
            }
        }
    }

    static void Add(float value, ref float sum, ref int count)
    {
        if (float.IsNaN(value)) return;
        sum += value;
        count++;
    }

    static void Blend(float[] grid, int size, int x, int z, float w, ref float sum, ref float weight)
    {
        if (x < 0 || z < 0 || x >= size || z >= size) return;
        float value = grid[z * size + x];
        if (float.IsNaN(value)) return;
        sum += value * w;
        weight += w;
    }
}
