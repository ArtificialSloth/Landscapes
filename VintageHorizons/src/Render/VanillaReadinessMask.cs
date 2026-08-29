namespace VintageHorizons;

/// <summary>
/// The CPU side of the per-cell ownership mask: one texel per 32x32x32 vanilla chunk, laid
/// out as Y slices stacked vertically in a single 2D texture. The public client API exposes
/// 2D texture binding and whole-texture upload only - there is no integer 3D texture and no
/// subregion update - so the atlas is the portable representation and every change costs a
/// full upload. That is cheap at ordinary view distances (32 KiB for a 256-block window)
/// and is why upload is coalesced to at most once per frame.
///
/// Addressing matches <see cref="VanillaRenderReadiness"/>'s tagged ring exactly, because
/// the shader reconstructs the same wrapped address from world coordinates. Any change to
/// one must change the other; <c>StaticAssetChecks</c> holds them together.
/// </summary>
internal sealed class VanillaReadinessMask
{
    /// <summary>
    /// Vanilla owns this cell's GROUND, and cached ground must not draw. The shader reads the
    /// red channel for that; the green channel carries water's own footprint, so a
    /// ground-owned cell sets both and this stays opaque white.
    /// </summary>
    public const int ReadyTexel = unchecked((int)0xFFFFFFFF);

    /// <summary>
    /// Vanilla owns this column for WATER but not for ground. Green channel, red clear.
    ///
    /// The two footprints differ on purpose. Ownership is refused for a 62-block ring inside
    /// vanilla's real edge - 46 blocks because the engine range-culls a mesh on its
    /// bounding-sphere centre rather than its near face, plus a 16-block early handover - so
    /// cached GROUND keeps drawing there and covers any chunk vanilla has quietly stopped
    /// submitting. That ring is a hole guard and it earns its cost: a hole in the ground is the
    /// worst thing this renderer can produce.
    ///
    /// Water has no such stake. Cached water missing where vanilla draws none is a river with
    /// no water in it - bad, but local and obvious - while cached water drawn where vanilla is
    /// drawing its own is a flat sheet standing inside live terrain. So water takes the ring
    /// back, and this bit is the columns ground gives up that water does not.
    /// </summary>
    public const int WaterOnlyTexel = unchecked((int)0xFF00FF00);

    /// <summary>Zero: cache owns this cell. Every failure path leaves texels at this value.</summary>
    public const int CacheTexel = 0;

    /// <summary>Blocks per ownership cell on every axis.</summary>
    public const int ChunkBlocks = 32;

    readonly int capacity;
    readonly int capacityMask;
    readonly int verticalChunks;
    readonly int[] texels;

    public VanillaReadinessMask(int horizontalCapacity, int verticalChunkCount)
    {
        if (horizontalCapacity <= 0 || (horizontalCapacity & (horizontalCapacity - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(horizontalCapacity),
                "Mask capacity must be a positive power of two.");
        if (verticalChunkCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(verticalChunkCount));

        capacity = horizontalCapacity;
        capacityMask = horizontalCapacity - 1;
        verticalChunks = verticalChunkCount;
        texels = new int[checked(horizontalCapacity * horizontalCapacity * verticalChunkCount)];
        Dirty = true;
    }

    public int Width => capacity;
    public int Height => checked(capacity * verticalChunks);
    public int VerticalChunks => verticalChunks;
    public int[] Texels => texels;
    public long Bytes => texels.LongLength * sizeof(int);

    /// <summary>True when the buffer differs from what the GPU last accepted.</summary>
    public bool Dirty { get; private set; }

    public long Uploads { get; private set; }
    public long ReadyTexels { get; private set; }

    /// <summary>
    /// Bumped by every change to any texel. The seam distance field is derived from this
    /// mask and is far more expensive to rebuild than to upload, so it needs to know that
    /// ownership moved rather than that the atlas happens to be dirty - <see cref="Dirty"/>
    /// is cleared by upload and would hide a change the field had not yet consumed.
    /// </summary>
    public long Revision { get; private set; }

    /// <summary>
    /// True when any cell in this column is owned by vanilla FOR GROUND.
    ///
    /// Ground only, deliberately. The seam band is derived from this, and water's dilated
    /// footprint must not be able to move the band the ground uses.
    ///
    /// "Any" rather than "all" on purpose: the band needs vanilla's horizontal footprint,
    /// and a column's ground cell is owned while the air chunks stacked above it are
    /// deliberately excluded from the mask (G40). Asking for all of them would report solid
    /// ground as being outside vanilla entirely.
    /// </summary>
    public bool ColumnHasReady(int chunkX, int chunkZ)
    {
        for (int y = 0; y < verticalChunks; y++)
        {
            if (texels[TexelIndex(chunkX, y, chunkZ, capacity, verticalChunks)] == ReadyTexel)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Wrapped atlas address for one ownership cell. The row is the ring's Z slot offset by
    /// whole capacity-sized blocks per Y level, so one 2D texture holds the full column
    /// stack without a 3D sampler.
    /// </summary>
    public static int TexelIndex(int chunkX, int chunkY, int chunkZ, int capacity, int verticalChunks)
    {
        int mask = capacity - 1;
        int row = (chunkZ & mask) + chunkY * capacity;
        return row * capacity + (chunkX & mask);
    }

    public bool Set(VanillaChunkCell cell, bool ready)
    {
        if (cell.Y < 0 || cell.Y >= verticalChunks) return false;

        int index = TexelIndex(cell.X, cell.Y, cell.Z, capacity, verticalChunks);
        // Ground ownership implies water ownership, so a ready cell writes both bits. Losing
        // ground ownership must NOT clear water's: the two footprints are maintained by
        // different passes, and dropping to zero here would punch a hole in water's until the
        // next rebuild.
        int value = ready ? ReadyTexel
            : (texels[index] == ReadyTexel ? WaterOnlyTexel : texels[index]);
        if (texels[index] == value) return false;

        if (ready && texels[index] != ReadyTexel) ReadyTexels++;
        else if (!ready && texels[index] == ReadyTexel) ReadyTexels--;
        texels[index] = value;
        Dirty = true;
        Revision++;
        return true;
    }

    /// <summary>Columns carrying water's footprint, ground-owned or dilated into it.</summary>
    public long WaterColumns { get; private set; }

    /// <summary>
    /// Recomputes water's own footprint: every column within <paramref name="dilateChunks"/>
    /// of a column vanilla owns any ground in.
    ///
    /// A CHEBYSHEV dilation in whole chunk columns, so the result is the ground footprint
    /// grown by a whole number of chunks and its boundary still lands exactly on chunk edges.
    /// A Euclidean or chamfer dilation would round every corner onto a smooth curve, which is
    /// the one thing this seam must not do.
    ///
    /// A dilation rather than a distance test from the camera, for the same reason: a radius
    /// is a circle wherever it is measured from, and what is being reconstructed here is a
    /// footprint. Growing the footprint also closes the pockets inside it that ownership has
    /// not committed yet, which a range test would leave open.
    /// </summary>
    public void RebuildWaterFootprint(
        int minChunkX, int minChunkZ, int width, int depth, int dilateChunks)
    {
        if (width <= 0 || depth <= 0) return;
        int reach = Math.Max(0, dilateChunks);
        long columns = 0;

        for (int z = 0; z < depth; z++)
        {
            for (int x = 0; x < width; x++)
            {
                bool near = false;
                for (int dz = -reach; dz <= reach && !near; dz++)
                {
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        int sx = x + dx, sz = z + dz;
                        if (sx < 0 || sx >= width || sz < 0 || sz >= depth) continue;
                        if (!ColumnHasReady(minChunkX + sx, minChunkZ + sz)) continue;
                        near = true;
                        break;
                    }
                }

                if (near) columns++;
                int value = near ? WaterOnlyTexel : CacheTexel;
                for (int y = 0; y < verticalChunks; y++)
                {
                    int index = TexelIndex(minChunkX + x, y, minChunkZ + z, capacity, verticalChunks);
                    // Ground ownership always wins: it already carries the water bit, and
                    // rewriting it here would drop the red bit the ground path reads.
                    if (texels[index] == ReadyTexel || texels[index] == value) continue;
                    texels[index] = value;
                    Dirty = true;
                    Revision++;
                }
            }
        }

        WaterColumns = columns;
    }

    public bool IsReady(VanillaChunkCell cell) =>
        cell.Y >= 0 && cell.Y < verticalChunks
        && texels[TexelIndex(cell.X, cell.Y, cell.Z, capacity, verticalChunks)] == ReadyTexel;

    /// <summary>
    /// Drops all ownership. Used for world teardown, window resize, and every failure path,
    /// because an emptied mask means cached terrain covers everything rather than nothing.
    /// </summary>
    public void Clear()
    {
        if (ReadyTexels == 0 && WaterColumns == 0 && !Dirty) return;
        Array.Clear(texels);
        ReadyTexels = 0;
        WaterColumns = 0;
        Dirty = true;
        Revision++;
    }

    /// <summary>
    /// Clears one wrapped column. A column leaving the active window must lose ownership
    /// before its ring slot is reused by different world coordinates, or the shader would
    /// read another place's readiness.
    /// </summary>
    public void ClearColumn(int chunkX, int chunkZ)
    {
        for (int y = 0; y < verticalChunks; y++)
        {
            int index = TexelIndex(chunkX, y, chunkZ, capacity, verticalChunks);
            if (texels[index] == CacheTexel) continue;
            if (texels[index] == ReadyTexel) ReadyTexels--;
            texels[index] = CacheTexel;
            Dirty = true;
            Revision++;
        }
    }

    /// <summary>Records that the GPU accepted the current buffer.</summary>
    public void MarkUploaded()
    {
        Dirty = false;
        Uploads++;
    }

    public void ResetTelemetry() => Uploads = 0;
}
