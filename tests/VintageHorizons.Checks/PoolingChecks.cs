using VintageHorizons.Silhouette;

namespace VintageHorizons.Checks;

/// <summary>
/// 0.4.131's reused storage must produce exactly what fresh storage does. Reuse fails quietly:
/// a value a previous job left in a buffer, read where the new job wrote nothing, is a wrong
/// texel or vertex that looks like ordinary terrain or fog. So each pooled path is compared
/// bit for bit with the allocating one, after the reused storage has been dirtied by a job of
/// a different shape and size.
/// </summary>
public static class PoolingChecks
{
    public static void Run(Check c)
    {
        FogBuildsMatchFreshStorage(c);
        PooledMeshesMatchExactOnes(c);
        BorrowedCaveFillMatchesTheCopy(c);
        ColumnBatchesComeBackClean(c);
    }

    const float Spill = LodSilhouetteFogVolume.DefaultSpillBlocks;

    static LodSilhouetteFogVolume.Prepared FogInput(int tilesWide, ulong signature, int shift)
    {
        var mask = new LodSilhouetteSeamMask();
        mask.BeginBuild(0, 0, tilesWide - 1, 0, signature);
        for (int tile = 0; tile < tilesWide; tile++)
        {
            var coverage = LodSilhouetteCoverage.Empty;
            for (int z = 0; z < LodSilhouetteField.CoverageCellsPerEdge; z++)
                for (int x = 0; x < 4 + z / 2 + shift; x++)
                    coverage = coverage.WithCovered(x, z);
            mask.SetTile(tile, 0, coverage);
        }
        mask.CompleteBuild();
        return LodSilhouetteFogVolume.Prepare(mask, (x, z) => 80f + x + z, Spill, 8f);
    }

    static void FogBuildsMatchFreshStorage(Check c)
    {
        LodSilhouetteFogVolume.Prepared before = FogInput(1, 41, 0);
        LodSilhouetteFogVolume.Prepared moved = FogInput(1, 42, 2);
        LodSilhouetteFogVolume.Prepared wide = FogInput(2, 43, 1);

        LodSilhouetteFogFieldArrays freshBefore = LodSilhouetteFogVolume.Build(before);
        LodSilhouetteFogFieldArrays freshMoved = LodSilhouetteFogVolume.Build(moved);
        LodSilhouetteFogFieldArrays freshWide = LodSilhouetteFogVolume.Build(wide);
        c.True(freshWide.Width * freshWide.Height != freshBefore.Width * freshBefore.Height,
            "the fixture really has fields of two sizes");

        var scratch = new LodSilhouetteFogVolume.Scratch();
        var halves = new LodSilhouetteFogVolume.HalfPool();

        LodSilhouetteFogFieldArrays pooled = LodSilhouetteFogVolume.Build(before, default, scratch, halves);
        SameField(c, freshBefore, pooled, "a pooled build matches a fresh one");
        halves.Return(pooled.HalfTexels);

        // A larger field through the same storage, then the first again: whatever the larger
        // one left behind must not show through.
        pooled = LodSilhouetteFogVolume.Build(wide, default, scratch, halves);
        SameField(c, freshWide, pooled, "a pooled build of a larger field matches a fresh one");
        halves.Return(pooled.HalfTexels);
        pooled = LodSilhouetteFogVolume.Build(before, default, scratch, halves);
        SameField(c, freshBefore, pooled,
            "a pooled build after a larger one leaves nothing of it behind");
        c.True(pooled.HalfTexels.Length >= freshBefore.HalfTexels.Length,
            "a pooled field's array can be longer than its texels");
        c.True(halves.Reused > 0, "the half-float arrays really are reused");

        // The residual: carried from the shown field onto the moved one, in pooled storage
        // that last held a different residual.
        LodSilhouetteFogResidual? fresh = LodSilhouetteFogVolume.BuildResidual(
            freshMoved, freshBefore.Layer, null, 0f, 0.1f, Spill);
        LodSilhouetteFogFieldArrays shown = pooled;
        // Junk where the moved field and its residual will be rented: a large half-float in
        // every element, so anything not written shows up as a mismatch.
        for (int i = 0; i < 2; i++)
        {
            ushort[] dirty = halves.Rent(freshMoved.Width * freshMoved.Height * 4);
            Array.Fill(dirty, (ushort)0x7BFF);
            halves.Return(dirty);
        }
        LodSilhouetteFogFieldArrays movedPooled = LodSilhouetteFogVolume.Build(moved, default, scratch, halves);
        SameField(c, freshMoved, movedPooled, "the moved field matches in pooled storage too");
        LodSilhouetteFogResidual? residual = LodSilhouetteFogVolume.BuildResidual(
            movedPooled, shown.Layer, null, 0f, 0.1f, Spill, default, halves);
        c.True(fresh != null && residual != null, "the moved seam leaves a residual both ways");
        if (fresh != null && residual != null)
        {
            int n = fresh.Layer.Width * fresh.Layer.Height * 4;
            c.True(fresh.Layer.Width == residual.Layer.Width
                    && fresh.Layer.Height == residual.Layer.Height
                    && fresh.MaxWeight == residual.MaxWeight
                    && fresh.LiveTexels == residual.LiveTexels
                    && fresh.Layer.HalfTexels.AsSpan(0, n).SequenceEqual(residual.Layer.HalfTexels.AsSpan(0, n)),
                "a pooled residual matches a fresh one, read through a pooled shown layer");
        }
    }

    static void SameField(Check c, LodSilhouetteFogFieldArrays expected,
        LodSilhouetteFogFieldArrays actual, string what)
    {
        int n = expected.Width * expected.Height * 4;
        bool same = expected.Width == actual.Width && expected.Height == actual.Height
            && expected.OriginBlockX == actual.OriginBlockX && expected.OriginBlockZ == actual.OriginBlockZ
            && expected.TexelBlocks == actual.TexelBlocks
            && expected.CoreBlocks == actual.CoreBlocks
            && expected.StaircaseDeviationBlocks == actual.StaircaseDeviationBlocks
            && expected.PathSamples == actual.PathSamples
            && actual.Texels.Length >= n && actual.HalfTexels.Length >= n
            && expected.Texels.AsSpan(0, n).SequenceEqual(actual.Texels.AsSpan(0, n))
            && expected.HalfTexels.AsSpan(0, n).SequenceEqual(actual.HalfTexels.AsSpan(0, n));
        c.True(same, what);
    }

    /// <summary>Uneven ground, with a lake over part of it, so both passes emit.</summary>
    static LodSection Terrain(int seed)
    {
        var s = new LodSection();
        s.FindOrAddPaletteEntry(blockId: 1, color: 0x00806040, flags: 0);
        s.FindOrAddPaletteEntry(blockId: 2, color: 0x00304080, flags: LodPaletteEntry.FlagWater);
        for (int col = 0; col < Fixtures.Total; col++)
        {
            int ground = 20 + (col * 7 + seed * 13) % 11;
            bool lake = (col / LodSection.GridSize + seed) % 5 == 0 && ground < 26;
            ulong[] runs = lake
                ? new[] { LodSection.PackRun(1, 28, ground), LodSection.PackRun(0, ground, 0) }
                : new[] { LodSection.PackRun(0, ground, 0) };
            s.SetColumn(col, runs);
        }
        return s;
    }

    static void PooledMeshesMatchExactOnes(Check c)
    {
        LodSection[] sections = { Terrain(1), Fixtures.SolidSection(yTop: 12), Terrain(4), Terrain(1) };
        bool allSame = true, anyWater = false;
        for (int i = 0; i < sections.Length; i++)
        {
            MeshResult exact = LodMesher.BuildMesh(Fixtures.Job(sections[i]));
            MeshResult pooled = LodMesher.BuildMesh(Fixtures.Job(sections[i]), pooledOutput: true);
            anyWater |= exact.WaterIndexCount > 0;
            allSame &= pooled.ArraysPooled
                && exact.VertexCount == pooled.VertexCount && exact.IndexCount == pooled.IndexCount
                && exact.WaterVertexCount == pooled.WaterVertexCount
                && exact.WaterIndexCount == pooled.WaterIndexCount
                && exact.PackedOpaqueQuadCount == pooled.PackedOpaqueQuadCount
                && Prefix(exact.Xyz, pooled.Xyz, exact.VertexCount * 3)
                && Prefix(exact.Rgba, pooled.Rgba, exact.VertexCount * 4)
                && Prefix(exact.Indices, pooled.Indices, exact.IndexCount)
                && Prefix(exact.PackedOpaqueQuads, pooled.PackedOpaqueQuads,
                    exact.PackedOpaqueQuadCount * LodPackedQuadFormat.WordsPerQuad)
                && Prefix(exact.WaterXyz, pooled.WaterXyz, exact.WaterVertexCount * 3)
                && Prefix(exact.WaterRgba, pooled.WaterRgba, exact.WaterVertexCount * 4)
                && Prefix(exact.WaterIndices, pooled.WaterIndices, exact.WaterIndexCount)
                && exact.ClusteredPackedOpaqueQuads.AsSpan().SequenceEqual(pooled.ClusteredPackedOpaqueQuads)
                && exact.EstimatedUploadBytes == pooled.EstimatedUploadBytes;
            // Handed back before the next build, so that build reuses them dirty.
            pooled.ReturnPooledArrays();
            allSame &= !pooled.ArraysPooled && pooled.Xyz.Length == 0 && pooled.WaterXyz == null;
            pooled.ReturnPooledArrays();
            c.False(exact.ArraysPooled, "an ordinary build keeps exact arrays a caller may hold");
        }
        c.True(anyWater, "the fixture really emits water");
        c.True(allSame,
            "pooled meshes match exact ones across sizes, water included, and hand back cleanly");
    }

    static bool Prefix<T>(T[]? expected, T[]? actual, int count) where T : IEquatable<T>
    {
        if (count == 0) return true;
        if (expected == null || actual == null || actual.Length < count) return false;
        return expected.AsSpan(0, count).SequenceEqual(actual.AsSpan(0, count));
    }

    const int Grid = LodSection.GridSize;

    static SectionSnapshot Ground(params (int X, int Z, int Bottom, int Top)[] cavities)
    {
        var runs = new List<ulong>();
        var starts = new int[Grid * Grid + 1];
        for (int cz = 0; cz < Grid; cz++)
        {
            for (int cx = 0; cx < Grid; cx++)
            {
                int col = LodSection.ColumnIndex(cx, cz);
                starts[col] = runs.Count;
                int top = 120;
                foreach (var gap in cavities.Where(v => v.X == cx && v.Z == cz).OrderByDescending(v => v.Top))
                {
                    if (gap.Top < top) runs.Add(LodSection.PackRun(1, top, gap.Top));
                    top = gap.Bottom;
                }
                if (top > 0) runs.Add(LodSection.PackRun(1, top, 0));
            }
        }
        starts[Grid * Grid] = runs.Count;
        return new SectionSnapshot
        {
            Runs = runs.ToArray(),
            ColumnStart = starts,
            Captured = Enumerable.Repeat(true, Grid * Grid).ToArray(),
            PaletteColors = new[] { 0, unchecked((int)0xFF808080) },
            PaletteFlags = new byte[] { 0, 0 },
            PaletteTintSlots = new byte[] { 0, 0 },
            PaletteTextureSlots = new ushort[] { 0, 0 },
        };
    }

    static void BorrowedCaveFillMatchesTheCopy(Check c)
    {
        var neighbours = new SectionSnapshot?[] { Ground(), Ground(), Ground(), Ground(),
            Ground(), Ground(), Ground(), Ground() };
        SectionSnapshot small = Ground((32, 32, 40, 48));
        SectionSnapshot large = Ground(Enumerable.Range(0, 40)
            .Select(i => (10 + i, 12 + i % 7, 30 + i % 5, 44 + i % 9)).ToArray());

        bool same = true;
        foreach (SectionSnapshot input in new[] { small, large, small })
        {
            SectionSnapshot copy = LodCaveCull.FillUnseen(input, neighbours, 0, LodCaveCull.DefaultReach);
            SectionSnapshot borrowed = LodCaveCull.Prepare(input, neighbours, 0, LodCaveCull.DefaultReach).Self;
            c.False(ReferenceEquals(copy, input), "the fixture really is filled");
            for (int col = 0; col < Grid * Grid && same; col++)
                same = copy.ColumnRuns(col).SequenceEqual(borrowed.ColumnRuns(col));
        }
        c.True(same, "a borrowed filled section matches the copied one, after a larger fill used the workspace");
    }

    static void ColumnBatchesComeBackClean(Check c)
    {
        ulong[]?[] batch = LodMeshArrays.RentColumnBatch();
        c.True(batch.Length >= Grid * Grid, "a column batch has a slot for every column");
        batch[0] = new ulong[] { 1 };
        batch[Grid * Grid - 1] = new ulong[] { 2 };
        LodMeshArrays.ReturnColumnBatch(batch);
        ulong[]?[] again = LodMeshArrays.RentColumnBatch();
        c.True(again.All(column => column == null),
            "a batch comes back with every column empty, whoever held it before");
        LodMeshArrays.ReturnColumnBatch(again);
    }
}
