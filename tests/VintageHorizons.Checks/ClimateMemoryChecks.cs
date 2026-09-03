namespace VintageHorizons.Checks;

/// <summary>
/// The climate the client has answered before, kept so exploration is cumulative.
///
/// Everything here is arithmetic over a byte array, and every way it can be wrong puts one
/// place's climate on another place's ground - the exact failure the climate field exists to
/// remove. A patch coordinate that truncates instead of flooring folds two patches into one
/// across the world origin; an index that transposes x and z mirrors every patch; a blob whose
/// layout drifts is read back as a confident wrong colour over a 2,048-block square.
/// </summary>
public static class ClimateMemoryChecks
{
    public static void Run(Check c)
    {
        PatchesAreFlooredNotTruncated(c);
        EveryCellHasItsOwnPlaceInItsPatch(c);
        LearnedCellsComeBackExactly(c);
        ABlobRoundTripsThroughTheStore(c);
        ABlobThatIsNotOursIsIgnoredNotParsed(c);
        OnlyChangedPatchesAreWritten(c);
        APatchIsAskedForOnce(c);
    }

    static void PatchesAreFlooredNotTruncated(Check c)
    {
        int n = LodClimateMemory.PatchCells;
        c.Eq(0, LodClimateMemory.PatchOf(0), "cell 0 is in patch 0");
        c.Eq(0, LodClimateMemory.PatchOf(n - 1), "the last cell of patch 0 is still patch 0");
        c.Eq(1, LodClimateMemory.PatchOf(n), "the next cell starts patch 1");
        c.Eq(-1, LodClimateMemory.PatchOf(-1), "cell -1 is in patch -1, not patch 0");
        c.Eq(-1, LodClimateMemory.PatchOf(-n), "the first cell of patch -1 is patch -1");
        c.Eq(-2, LodClimateMemory.PatchOf(-n - 1), "one further back is patch -2");

        // Keys survive a round trip, on both sides of the origin, because they are what a row
        // is addressed by on disk.
        foreach (int px in new[] { -70000, -1, 0, 1, 70000 })
        {
            foreach (int pz in new[] { -70000, -1, 0, 1, 70000 })
            {
                long key = LodClimateMemory.PatchKey(px, pz);
                c.Eq(px, LodClimateMemory.PatchXOf(key), $"patch x {px} survives its key");
                c.Eq(pz, LodClimateMemory.PatchZOf(key), $"patch z {pz} survives its key");
            }
        }
    }

    static void EveryCellHasItsOwnPlaceInItsPatch(Check c)
    {
        int n = LodClimateMemory.PatchCells;
        var seen = new HashSet<int>();
        for (int z = 0; z < n; z++)
        {
            for (int x = 0; x < n; x++)
            {
                int i = LodClimateMemory.IndexIn(x, z);
                c.True(i >= 0 && i < LodClimateMemory.CellsPerPatch,
                    $"cell {x},{z} lands inside its patch");
                c.True(seen.Add(i), $"cell {x},{z} has its own place");
            }
        }
        c.Eq(LodClimateMemory.CellsPerPatch, seen.Count, "a patch holds every cell exactly once");

        // x moves by one, z moves by a row. Transposing these mirrors every patch, which reads
        // as climate being subtly in the wrong place rather than as an indexing bug.
        c.Eq(LodClimateMemory.IndexIn(0, 0) + 1, LodClimateMemory.IndexIn(1, 0),
            "one cell east is the next place");
        c.Eq(LodClimateMemory.IndexIn(0, 0) + n, LodClimateMemory.IndexIn(0, 1),
            "one cell south is a whole row on");

        // And negative cells land in the same places within their own patch.
        c.Eq(LodClimateMemory.IndexIn(0, 0), LodClimateMemory.IndexIn(-n, -n),
            "the first cell of a negative patch is that patch's first place");
        c.Eq(LodClimateMemory.IndexIn(n - 1, n - 1), LodClimateMemory.IndexIn(-1, -1),
            "and the last cell before the origin is its patch's last place");
    }

    static void LearnedCellsComeBackExactly(Check c)
    {
        var memory = new LodClimateMemory();
        c.False(memory.TryGet(5, 7, out _, out _), "nothing is known before anything is learned");

        for (int i = 0; i < 40; i++)
        {
            memory.Learn(i * 3 - 50, i * 5 - 60, (byte)(i + 60), (byte)(i * 2 + 10));
        }
        for (int i = 0; i < 40; i++)
        {
            c.True(memory.TryGet(i * 3 - 50, i * 5 - 60, out byte t, out byte r),
                $"cell {i} is remembered");
            c.Eq((byte)(i + 60), t, $"cell {i} keeps its temperature");
            c.Eq((byte)(i * 2 + 10), r, $"cell {i} keeps its rainfall");
        }

        // A neighbour that was never learned stays unknown. Storing a derived value would let
        // one guess seed the next, which is the whole reason spread values are not kept.
        c.False(memory.TryGet(1000, 1000, out _, out _), "an unvisited cell stays unknown");

        // Zero is a legal climate, so it has to be distinguishable from "never answered".
        memory.Learn(4000, 4000, 0, 0);
        c.True(memory.TryGet(4000, 4000, out byte zt, out byte zr),
            "a cell learned as zero is still known");
        c.Eq(0, zt, "and reads back as zero");
        c.Eq(0, zr, "on both channels");
    }

    static void ABlobRoundTripsThroughTheStore(Check c)
    {
        var written = new LodClimateMemory();
        int n = LodClimateMemory.PatchCells;
        for (int z = 0; z < n; z += 3)
        {
            for (int x = 0; x < n; x += 2)
                written.Learn(x, z, (byte)(x * 7 + z), (byte)(z * 5 + x));
        }

        long key = LodClimateMemory.PatchKey(0, 0);
        byte[]? blob = written.Encode(key);
        c.True(blob != null, "a patch with content encodes");
        c.Eq(LodClimateMemory.BlobBytes, blob!.Length, "to the length the store expects");
        c.Eq(LodClimateMemory.BlobFormat, blob[0], "carrying its own format byte");

        var read = new LodClimateMemory();
        c.True(read.Accept(key, blob), "and is accepted back");
        for (int z = 0; z < n; z++)
        {
            for (int x = 0; x < n; x++)
            {
                bool expected = z % 3 == 0 && x % 2 == 0;
                bool got = read.TryGet(x, z, out byte t, out byte r);
                c.Eq(expected, got, $"cell {x},{z} is known exactly when it was learned");
                if (!expected) continue;
                c.Eq((byte)(x * 7 + z), t, $"cell {x},{z} keeps its temperature across the store");
                c.Eq((byte)(z * 5 + x), r, $"cell {x},{z} keeps its rainfall across the store");
            }
        }
        c.Eq(written.KnownCells(), read.KnownCells(), "and the same number of cells survive");
    }

    static void ABlobThatIsNotOursIsIgnoredNotParsed(Check c)
    {
        var memory = new LodClimateMemory();
        long key = LodClimateMemory.PatchKey(3, 4);

        c.False(memory.Accept(key, null), "a missing row is not a patch");
        c.False(memory.Accept(key, new byte[LodClimateMemory.BlobBytes - 1]),
            "and neither is one of the wrong length");

        var wrongFormat = new byte[LodClimateMemory.BlobBytes];
        wrongFormat[0] = (byte)(LodClimateMemory.BlobFormat + 1);
        for (int i = 1; i < wrongFormat.Length; i++) wrongFormat[i] = 0xFF;
        c.False(memory.Accept(key, wrongFormat), "nor one written by a different layout");

        // Refused, and NOT half-read: a blob of 0xFF would otherwise mark every cell known and
        // paint a whole 2,048-block square with an invented climate.
        for (int x = 3 * LodClimateMemory.PatchCells;
             x < 4 * LodClimateMemory.PatchCells; x += 7)
        {
            c.False(memory.TryGet(x, 4 * LodClimateMemory.PatchCells, out _, out _),
                $"cell {x} took nothing from the refused blob");
        }
        c.Eq(0, memory.KnownCells(), "and the memory learned nothing at all from it");
    }

    static void OnlyChangedPatchesAreWritten(Check c)
    {
        var memory = new LodClimateMemory();
        c.Eq(0, memory.TakeDirtyKeys().Count, "a fresh memory has nothing to write");

        memory.Learn(0, 0, 100, 50);
        c.Eq(1, memory.TakeDirtyKeys().Count, "learning something makes one patch dirty");
        c.Eq(0, memory.TakeDirtyKeys().Count, "and taking it clears the flag");

        // Re-learning the same answer must not queue a write. A refresh crosses the same cells
        // every thirty seconds, so a dirty flag on equal values would rewrite the whole visited
        // world forever.
        memory.Learn(0, 0, 100, 50);
        c.Eq(0, memory.TakeDirtyKeys().Count, "relearning the same value writes nothing");

        memory.Learn(0, 0, 101, 50);
        c.Eq(1, memory.TakeDirtyKeys().Count, "a changed value does write");

        memory.Learn(0, 0, 100, 50);
        memory.Learn(LodClimateMemory.PatchCells, 0, 7, 7);
        c.Eq(2, memory.TakeDirtyKeys().Count, "two patches touched, two writes");
    }

    static void APatchIsAskedForOnce(Check c)
    {
        var memory = new LodClimateMemory();
        c.True(memory.ShouldRequest(10, 10, out long key), "an unknown patch is worth asking for");
        c.False(memory.ShouldRequest(11, 11, out long same),
            "and the same patch is not asked for twice while the read is in flight");
        c.Eq(key, same, "because both cells are in the same patch");

        memory.AcceptEmpty(key);
        c.False(memory.ShouldRequest(10, 10, out _),
            "a patch the store had nothing for is not asked for again either");

        // A patch already in memory is never requested.
        var learned = new LodClimateMemory();
        learned.Learn(500, 500, 9, 9);
        c.False(learned.ShouldRequest(500, 500, out _), "a known patch is never requested");
    }
}
