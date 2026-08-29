namespace VintageHorizons.Checks;

/// <summary>
/// The band's ramp must finish INSIDE the field's window.
///
/// The field describes a square window centred on the camera, and every cell outside it reads
/// no band at all. So if the ramp is still carrying weight when the window ends, the
/// correction falls to zero in one step along a straight line - a hard edge across the world,
/// in the one place the whole design exists to avoid having one. That is exactly what shipped:
/// the margin was sized from the band's width alone while the shader ramps on
/// <c>seamCells * 8 - bandInnerBlocks</c>, so the ramp needed 174 blocks and got 136, and the
/// field's edge still carried a weight of 0.31.
///
/// Screenshots could not settle this. A ramp seen at a shallow angle from altitude is a few
/// pixels tall, so a gradient and a cliff look the same; the deciding number is the weight the
/// outermost ring ends on, and nothing on screen shows it. This computes it.
/// </summary>
public static class BandShapeChecks
{
    const int MaskCapacity = 32;
    const int VerticalChunks = 12;
    const int WindowChunks = 17;

    public static void Run(Check c)
    {
        RampFinishesInsideTheWindow(c);
        SaturatesWhenOwnershipIsScattered(c);
    }

    /// <summary>
    /// Across every band width and inner offset the commands accept, the field's outermost
    /// ring must have reached zero. The margin is what buys that, and it has to be sized from
    /// the reach the SHADER uses, not from the width alone.
    /// </summary>
    static void RampFinishesInsideTheWindow(Check c)
    {
        foreach (float width in new[] { 32f, 64f, 128f, 256f })
        {
            foreach (float inner in new[] { 0f, 46f, 96f })
            {
                int margin = MarginCells(width, inner);
                var field = BuildField(margin, (x, z) =>
                {
                    int dx = x - WindowChunks / 2, dz = z - WindowChunks / 2;
                    return dx * dx + dz * dz <= 36;
                });

                float edge = OuterEdgeWeight(field, width, inner);
                c.Eq(0f, edge,
                    $"the ramp reaches zero before the window ends at width {width:0}, inner {inner:0}");
            }
        }

        // The exact regression, stated as the arithmetic rather than as an outcome: a margin
        // sized from the width alone cannot cover a ramp that starts an inner offset later.
        int shortMargin = (int)Math.Ceiling(128f / VanillaSeamDistanceField.CellBlocks) + 1;
        c.True(MarginCells(128f, 46f) > shortMargin,
            "the margin accounts for the inner offset the shader subtracts, not the width alone");
    }

    /// <summary>
    /// What the owner's screenshot showed. Ownership commits as a scatter rather than as a
    /// solid footprint, and distance-to-the-nearest-owned-column collapses to zero between
    /// scattered columns - so the band's full-strength region is the whole tracked window
    /// rather than a ring at the seam, and its only visible boundary is the window's own
    /// straight edge.
    ///
    /// Recorded rather than asserted away: this is a property of how ownership commits, not a
    /// defect in the field, and the fix for it is a separate decision. What this pins is that
    /// the saturation is real and measurable offline, so nobody has to re-derive it from a
    /// screenshot.
    /// </summary>
    static void SaturatesWhenOwnershipIsScattered(Check c)
    {
        var rng = new Random(1);
        var scattered = new HashSet<(int, int)>();
        while (scattered.Count < 43)
        {
            int x = rng.Next(WindowChunks), z = rng.Next(WindowChunks);
            int dx = x - WindowChunks / 2, dz = z - WindowChunks / 2;
            if (dx * dx + dz * dz > 64) continue;
            scattered.Add((x, z));
        }

        int margin = MarginCells(128f, 46f);
        var solid = BuildField(margin, (x, z) =>
        {
            int dx = x - WindowChunks / 2, dz = z - WindowChunks / 2;
            return dx * dx + dz * dz <= 36;
        });
        var sparse = BuildField(margin, (x, z) => scattered.Contains((x, z)));

        // Measured over the MASK window alone, so the share is comparable with the share of
        // columns vanilla actually owns. 43 of 289 columns is 15%; everything the band paints
        // at full strength past that is ground vanilla does not own being treated as if it sat
        // on the seam. That is the saturation, as a ratio rather than as an adjective.
        double solidFull = FullWeightShareOverMaskWindow(solid, 128f, 46f);
        double sparseFull = FullWeightShareOverMaskWindow(sparse, 128f, 46f);
        double sparseOwned = 43.0 / (WindowChunks * WindowChunks);

        Console.WriteLine($"      full weight over the mask window: solid {solidFull:0.000}, "
            + $"scattered {sparseFull:0.000} against {sparseOwned:0.000} actually owned");

        c.True(sparseFull > solidFull,
            "scattered ownership saturates more of the window than a solid footprint does");
        // A ratio rather than an absolute share, deliberately: the absolute number moves with
        // the fixture's scatter and would be a threshold tuned to this seed, while "the band
        // paints several times more ground at full strength than vanilla owns" is the defect
        // itself and holds for any scatter.
        c.True(sparseFull > sparseOwned * 4,
            "painting several times more ground at full strength than vanilla actually owns, "
            + "so the band fills the tracked window instead of ringing the seam");
    }

    static int MarginCells(float width, float inner)
    {
        float reach = Math.Max(0f, width) + Math.Max(0f, inner);
        return Math.Clamp(
            (int)Math.Ceiling(reach / VanillaSeamDistanceField.CellBlocks) + 1,
            1, 512 / 4);
    }

    static VanillaSeamDistanceField BuildField(int marginCells, Func<int, int, bool> isOwned)
    {
        var mask = new VanillaReadinessMask(MaskCapacity, VerticalChunks);
        for (int z = 0; z < WindowChunks; z++)
        {
            for (int x = 0; x < WindowChunks; x++)
            {
                if (isOwned(x, z)) mask.Set(new VanillaChunkCell(x, 3, z), true);
            }
        }

        int cells = WindowChunks * VanillaSeamDistanceField.CellsPerChunk + marginCells * 2;
        int capacity = 1;
        while (capacity < cells) capacity <<= 1;
        var field = new VanillaSeamDistanceField(capacity);
        field.Build(mask, 0, 0, WindowChunks, WindowChunks, marginCells);
        return field;
    }

    /// <summary>Exactly what the fragment shader computes from the same texel.</summary>
    static float Weight(VanillaSeamDistanceField field, int cellX, int cellZ,
        float width, float inner)
    {
        int index = VanillaSeamDistanceField.TexelIndex(cellX, cellZ, field.Capacity);
        float cells = (field.Texels[index] >> 16) & 0xFF;
        float blocks = Math.Max(0f, cells * VanillaSeamDistanceField.CellBlocks - inner);
        float step = VanillaSeamDistanceField.CellBlocks;
        return Math.Clamp(1f - MathF.Floor(blocks / step) * step / Math.Max(1f, width), 0f, 1f);
    }

    static float OuterEdgeWeight(VanillaSeamDistanceField field, float width, float inner)
    {
        float worst = 0f;
        for (int x = 0; x < field.WidthCells; x++)
        {
            worst = Math.Max(worst, Weight(field, field.MinCellX + x, field.MinCellZ, width, inner));
            worst = Math.Max(worst, Weight(field,
                field.MinCellX + x, field.MinCellZ + field.DepthCells - 1, width, inner));
        }
        for (int z = 0; z < field.DepthCells; z++)
        {
            worst = Math.Max(worst, Weight(field, field.MinCellX, field.MinCellZ + z, width, inner));
            worst = Math.Max(worst, Weight(field,
                field.MinCellX + field.WidthCells - 1, field.MinCellZ + z, width, inner));
        }
        return worst;
    }

    /// <summary>
    /// The share of the MASK window - not the whole field, which carries a wide empty margin -
    /// that the band paints at full strength.
    /// </summary>
    static double FullWeightShareOverMaskWindow(
        VanillaSeamDistanceField field, float width, float inner)
    {
        int margin = (field.WidthCells - WindowChunks * VanillaSeamDistanceField.CellsPerChunk) / 2;
        int full = 0, total = 0;
        for (int z = margin; z < field.DepthCells - margin; z++)
        {
            for (int x = margin; x < field.WidthCells - margin; x++)
            {
                total++;
                if (Weight(field, field.MinCellX + x, field.MinCellZ + z, width, inner) >= 1f) full++;
            }
        }
        return total == 0 ? 0 : full / (double)total;
    }
}

/// <summary>
/// Water's own ownership footprint: the ground footprint grown by whole chunk columns.
///
/// Ground gives up a 62-block ring inside vanilla's real edge so it can cover any chunk the
/// engine has quietly stopped submitting - a hole guard worth its cost. Water does not need
/// it, and paying it anyway put cached water inside live terrain, which is what the owner has
/// been looking at for several builds. These hold the two footprints apart, and hold water's
/// boundary on the chunk grid.
/// </summary>
public static class WaterFootprintChecks
{
    const int Capacity = 32;
    const int VerticalChunks = 4;

    public static void Run(Check c)
    {
        var mask = new VanillaReadinessMask(Capacity, VerticalChunks);
        mask.Set(new VanillaChunkCell(8, 1, 8), true);

        // Ground ownership carries the water bit too: anywhere vanilla owns the ground, it
        // owns the water above it, and the shader must not need two lookups to learn that.
        c.Eq(VanillaReadinessMask.ReadyTexel,
            mask.Texels[VanillaReadinessMask.TexelIndex(8, 1, 8, Capacity, VerticalChunks)],
            "a ground-owned cell sets both the ground and the water bit");
        c.Eq(1L, mask.ReadyTexels, "and the owned count still counts ground cells only");

        mask.RebuildWaterFootprint(0, 0, 17, 17, 2);

        // Chebyshev, not Euclidean. The diagonal corner at exactly (2,2) is 2.83 chunks away
        // by distance and would be excluded by any circular reach - and excluding it is what
        // rounds the footprint's corners onto a smooth curve.
        c.True(WaterOwned(mask, 10, 10), "the corner two chunks out on both axes is water-owned");
        c.True(WaterOwned(mask, 10, 8), "and so is the column two chunks out on one axis");
        c.False(WaterOwned(mask, 11, 8), "three chunks out is outside the dilation");
        c.False(WaterOwned(mask, 11, 11), "and so is the corner three out on both axes");

        // A square of exactly (2*2+1)^2 columns. Any other count means the reach is not
        // uniform, which shows up in game as a footprint that is not chunk-shaped.
        c.Eq(25L, mask.WaterColumns, "the dilation covers a 5x5 square of chunk columns");

        // The ground footprint is untouched. This is the whole safety property: the owner
        // asked for water's overlap to go without disturbing terrain's.
        c.True(mask.ColumnHasReady(8, 8), "the ground footprint still holds its own column");
        c.False(mask.ColumnHasReady(10, 10),
            "and has NOT grown into water's - the band reads this and must not move");
        c.Eq(1L, mask.ReadyTexels, "nor has the ground-owned count changed");

        // Ground ownership can be lost without taking water's footprint with it: the two are
        // written by different passes, and a cell that drops to zero here would punch a hole
        // in water's footprint until the next rebuild happened to run.
        mask.Set(new VanillaChunkCell(8, 1, 8), false);
        c.Eq(0L, mask.ReadyTexels, "losing ground ownership drops the ground count");
        c.True(WaterOwned(mask, 8, 8), "but leaves water's own bit standing");
    }

    static bool WaterOwned(VanillaReadinessMask mask, int chunkX, int chunkZ)
    {
        for (int y = 0; y < VerticalChunks; y++)
        {
            int texel = mask.Texels[
                VanillaReadinessMask.TexelIndex(chunkX, y, chunkZ, Capacity, VerticalChunks)];
            if (texel == VanillaReadinessMask.WaterOnlyTexel
                || texel == VanillaReadinessMask.ReadyTexel)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// How far water's footprint grows past ground's, derived rather than guessed.
///
/// 0.3.151 grew it by the whole refused ring and overshot by exactly one chunk, which showed
/// up in play as a chunk-wide strip with no water in it between the two. The error was in
/// which face the refusal measures from, so this pins the reasoning and not just the number.
/// </summary>
public static class WaterEdgeChecks
{
    const int ChunkBlocks = 32;

    public static void Run(Check c)
    {
        // The refusal compares a column's NEAREST face against the threshold, so the outermost
        // column that still owns anything reaches a full chunk further out than the threshold
        // does. That chunk is already inside the ground footprint and must not be grown again.
        c.Eq(1, Derive(46f, 16f), "the default ring needs one chunk of growth, not two");
        c.Eq(2, Derive(46f, 48f), "a wider early handover needs one more");
        c.Eq(1, Derive(46f, 0f), "the slack alone still spills past one chunk, so still one");
        c.Eq(0, Derive(30f, 0f), "a ring that fits inside a single chunk needs no growth");
        c.Eq(0, Derive(0f, 0f), "no ring, no growth");

        // Monotonic: a wider ring can never ask for less growth. A non-monotonic derivation
        // would make the seam jump the wrong way when the overlap is tuned.
        int previous = -1;
        for (float overlap = 0f; overlap <= 256f; overlap += 8f)
        {
            int growth = Derive(46f, overlap);
            c.True(growth >= previous, $"growth never decreases as the ring widens, at {overlap:0}");
            previous = growth;
        }

        // Never short. Falling short is the failure this replaced - cached water standing
        // inside live terrain - so the growth plus the chunk already inside the footprint must
        // always cover the whole refused ring.
        foreach (float overlap in new[] { 0f, 16f, 30f, 32f, 33f, 64f, 100f })
        {
            float ring = 46f + overlap;
            c.True(Derive(46f, overlap) * ChunkBlocks + ChunkBlocks >= ring,
                $"the growth plus the outermost owned chunk covers the ring, at {overlap:0}");
        }
    }

    /// <summary>The renderer's own arithmetic, kept here so a change to it fails loudly.</summary>
    static int Derive(float slack, float overlap)
    {
        float remaining = slack + Math.Max(0f, overlap) - ChunkBlocks;
        return Math.Clamp((int)Math.Ceiling(Math.Max(0f, remaining) / ChunkBlocks), 0, 8);
    }
}
