namespace VintageHorizons;

/// <summary>
/// Exact 32-block ground coverage emitted by one resident opaque LOD mesh.
///
/// The coverage belongs to the mesh result, not to the mutable section that produced it. That
/// makes it safe to use for the Farseer handoff: a cell is never hidden because newer terrain
/// data arrived before its replacement mesh did. At L0 a section has 2x2 cells; each coarser
/// level doubles that edge. The largest L6 mask is still only 2 KiB.
/// </summary>
internal readonly struct LodMeshGroundCoverage
{
    public const int CellBlocks = 32;

    readonly ulong[]? words;

    public int CellsPerEdge { get; }
    public bool IsEmpty => words == null || words.Length == 0;

    LodMeshGroundCoverage(int cellsPerEdge, ulong[] words)
    {
        CellsPerEdge = cellsPerEdge;
        this.words = words;
    }

    public bool Covered(int localCellX, int localCellZ)
    {
        if (words == null || (uint)localCellX >= (uint)CellsPerEdge
            || (uint)localCellZ >= (uint)CellsPerEdge) return false;
        int bit = localCellZ * CellsPerEdge + localCellX;
        return (words[bit >> 6] & (1UL << (bit & 63))) != 0;
    }

    /// <summary>
    /// Builds coverage from the opaque top surfaces the mesher really emitted. A 32-block cell
    /// is covered only when those surfaces span its whole footprint. This deliberately declines
    /// partial cells: keeping Farseer there can overlap a little terrain, but claiming empty sky
    /// would reopen the much worse handoff hole.
    /// </summary>
    public static LodMeshGroundCoverage Build(int level, ReadOnlySpan<ulong> topColumns)
    {
        if (level < 0 || level > LodWorld.MaxLevel)
            throw new ArgumentOutOfRangeException(nameof(level));
        if (topColumns.Length < LodSection.GridSize * LodSection.GridSize / 64)
            throw new ArgumentException("A complete 64x64 column mask is required.",
                nameof(topColumns));

        int step = LodSection.ColumnStepBlocks << level;
        int cellsPerEdge = (LodSection.GridSize * step) / CellBlocks;
        var covered = new ulong[(cellsPerEdge * cellsPerEdge + 63) / 64];

        if (step <= CellBlocks)
        {
            int columnsPerCell = CellBlocks / step;
            for (int cellZ = 0; cellZ < cellsPerEdge; cellZ++)
            {
                for (int cellX = 0; cellX < cellsPerEdge; cellX++)
                {
                    bool full = true;
                    int startX = cellX * columnsPerCell;
                    int startZ = cellZ * columnsPerCell;
                    for (int z = startZ; full && z < startZ + columnsPerCell; z++)
                    {
                        for (int x = startX; x < startX + columnsPerCell; x++)
                        {
                            if (ColumnCovered(topColumns, x, z)) continue;
                            full = false;
                            break;
                        }
                    }
                    if (full) SetCovered(covered, cellsPerEdge, cellX, cellZ);
                }
            }
        }
        else
        {
            int cellsPerColumn = step / CellBlocks;
            for (int columnZ = 0; columnZ < LodSection.GridSize; columnZ++)
            {
                for (int columnX = 0; columnX < LodSection.GridSize; columnX++)
                {
                    if (!ColumnCovered(topColumns, columnX, columnZ)) continue;
                    int startX = columnX * cellsPerColumn;
                    int startZ = columnZ * cellsPerColumn;
                    for (int dz = 0; dz < cellsPerColumn; dz++)
                        for (int dx = 0; dx < cellsPerColumn; dx++)
                            SetCovered(covered, cellsPerEdge, startX + dx, startZ + dz);
                }
            }
        }

        return new LodMeshGroundCoverage(cellsPerEdge, covered);
    }

    static bool ColumnCovered(ReadOnlySpan<ulong> columns, int x, int z)
    {
        int bit = z * LodSection.GridSize + x;
        return (columns[bit >> 6] & (1UL << (bit & 63))) != 0;
    }

    static void SetCovered(ulong[] destination, int cellsPerEdge, int x, int z)
    {
        int bit = z * cellsPerEdge + x;
        destination[bit >> 6] |= 1UL << (bit & 63);
    }
}
