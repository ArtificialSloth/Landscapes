namespace VintageHorizons.Checks;

/// <summary>
/// The climate field: cell addressing, and the spread that fills what the client does not know.
///
/// Every failure this can have looks like a colour bug rather than an indexing one, which is
/// the most expensive shape of defect this codebase has. A cell address that disagrees with the
/// shader's misplaces every climate at once; a spread that leaks the previous window's values
/// paints one place with another place's climate, which is the exact thing the field exists to
/// stop.
/// </summary>
public static class ClimateFieldChecks
{
    public static void Run(Check c)
    {
        CellsAreFlooredNotTruncated(c);
        AddressingWrapsAndStaysUnique(c);
        KnownCellsSurviveExactly(c);
        OneKnownCellFillsTheWholeWindow(c);
        TheNearerClimateWins(c);
        NothingKnownPublishesNothing(c);
        ARebuiltWindowCannotLeakTheOldOne(c);
        ThePublishedFieldSurvivesTheNextRefresh(c);
        ColourMapPixelsAreRepackedRedHigh(c);
    }

    /// <summary>
    /// A refresh takes hundreds of frames, and the field on screen has to keep describing
    /// itself for all of them.
    ///
    /// The defect: one counter of known cells, reset when a refresh began. Active went false
    /// the instant a rebuild started, the renderer read that as "no climate anywhere", and
    /// every fragment fell back to the old whole-world tint until the rebuild landed - a
    /// visible flip to the previous system and back, once per cadence. Nothing about it looks
    /// like a counter; it looks like the new tint intermittently not working.
    /// </summary>
    static void ThePublishedFieldSurvivesTheNextRefresh(Check c)
    {
        LodClimateField.Sampler warm = (int x, int z, out byte t, out byte r) =>
        {
            t = 190; r = 60;
            return true;
        };

        var field = new LodClimateField(32);
        c.False(field.Active, "a field that has never been built is not usable");

        field.BeginRefresh(0, 0);
        for (int row = 0; row < field.RefreshRowCount; row++) field.RefreshRow(row, warm);
        field.CompleteRefresh();
        c.True(field.Active, "once published it is usable");

        // The moment that matters: a new refresh has started and nothing has landed yet.
        field.BeginRefresh(4096, 4096);
        c.True(field.Active, "and it stays usable while the next refresh is still being built");
        c.Eq(190, Read(field, 0, 0).Temperature,
            "still publishing the climate it published before");

        field.RefreshRow(0, warm);
        c.True(field.Active, "still usable part way through");

        for (int row = 1; row < field.RefreshRowCount; row++) field.RefreshRow(row, warm);
        field.CompleteRefresh();
        c.True(field.Active, "and after the new one lands");
    }

    /// <summary>
    /// The colour maps are copied out of engine memory, where red sits in the LOW byte, into
    /// the layout every texture this renderer uploads uses, where it sits at bits 16-23.
    ///
    /// Copying them verbatim drew an autumn forest deep blue. Orange is high red and low blue,
    /// so this particular mistake is maximally visible - but it is still only visible in game,
    /// which is why the packing is arithmetic that can be checked here.
    /// </summary>
    static void ColourMapPixelsAreRepackedRedHigh(Check c)
    {
        // A stored pixel with red in the low byte, as ColorMap.Pixels holds it.
        const int red = 200, green = 120, blue = 30, alpha = 111;
        int stored = (alpha << 24) | (blue << 16) | (green << 8) | red;

        int packed = LodClimateMapPages.PackTexel(stored);
        c.Eq(red, (packed >> 16) & 0xFF, "red comes out at bits 16-23, where the shader reads it");
        c.Eq(green, (packed >> 8) & 0xFF, "green does not move");
        c.Eq(blue, packed & 0xFF, "and blue takes red's old place");
        c.Eq(alpha, (packed >> 24) & 0xFF,
            "alpha stays intact for vanilla liquid color mapping");

        // Swapping twice is the identity, which is what makes the direction the whole point.
        c.Eq(stored,
            LodClimateMapPages.PackTexel(LodClimateMapPages.PackTexel(stored))
                & unchecked((int)0xFFFFFFFF),
            "the swap is its own inverse, so the direction is the only thing that can be wrong");

        // Grey survives either way, so a grey test would have passed with the bug in place.
        int grey = (alpha << 24) | (99 << 16) | (99 << 8) | 99;
        c.Eq(grey, LodClimateMapPages.PackTexel(grey),
            "a grey pixel is unchanged, which is why the bug needed a coloured one to show");
    }

    /// <summary>Temperature, rainfall and spread distance out of one published texel.</summary>
    static (int Temperature, int Rainfall, int Spread) Read(
        LodClimateField field, int cellX, int cellZ)
    {
        int v = field.Texels[LodClimateField.TexelIndex(cellX, cellZ, field.Capacity)];
        return ((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
    }

    /// <summary>Build a whole window in one go, the way the renderer does across frames.</summary>
    static LodClimateField Build(int capacity, int camX, int camZ, LodClimateField.Sampler sample)
    {
        var field = new LodClimateField(capacity);
        field.BeginRefresh(camX, camZ);
        for (int row = 0; row < field.RefreshRowCount; row++) field.RefreshRow(row, sample);
        field.CompleteRefresh();
        return field;
    }

    static void CellsAreFlooredNotTruncated(Check c)
    {
        int n = LodClimateField.CellBlocks;
        c.Eq(0, LodClimateField.CellOf(0), "block 0 is cell 0");
        c.Eq(0, LodClimateField.CellOf(n - 1), "the last block of cell 0 is still cell 0");
        c.Eq(1, LodClimateField.CellOf(n), "the next block starts cell 1");

        // Truncation would fold block -1 into cell 0 and make one cell twice as wide across
        // the origin, which is a seam in the climate exactly at the world's centre.
        c.Eq(-1, LodClimateField.CellOf(-1), "block -1 is cell -1");
        c.Eq(-1, LodClimateField.CellOf(-n), "the first block of cell -1 is cell -1");
        c.Eq(-2, LodClimateField.CellOf(-n - 1), "one block further back is cell -2");

        // Every cell holds exactly CellBlocks blocks, on both sides of the origin.
        for (int cell = -4; cell <= 4; cell++)
        {
            int count = 0;
            for (int b = cell * n - 2; b < (cell + 1) * n + 2; b++)
                if (LodClimateField.CellOf(b) == cell) count++;
            c.Eq(n, count, $"cell {cell} holds {n} blocks");
        }
    }

    static void AddressingWrapsAndStaysUnique(Check c)
    {
        const int capacity = 32;
        var seen = new HashSet<int>();
        for (int z = 0; z < capacity; z++)
        {
            for (int x = 0; x < capacity; x++)
            {
                int i = LodClimateField.TexelIndex(x, z, capacity);
                c.True(i >= 0 && i < capacity * capacity, $"cell {x},{z} lands inside the buffer");
                c.True(seen.Add(i), $"cell {x},{z} has its own texel");
            }
        }
        c.Eq(capacity * capacity, seen.Count, "a full window uses every texel exactly once");

        // The ring wraps, so a cell one window away shares a slot. That is the property the
        // whole-window rewrite exists to make safe, and the shader relies on it too.
        c.Eq(LodClimateField.TexelIndex(3, 5, capacity),
            LodClimateField.TexelIndex(3 + capacity, 5 - capacity, capacity),
            "cells a whole window apart share a slot");
        c.Eq(LodClimateField.TexelIndex(-1, -1, capacity),
            LodClimateField.TexelIndex(capacity - 1, capacity - 1, capacity),
            "negative cells wrap to the far corner");
    }

    static void KnownCellsSurviveExactly(Check c)
    {
        // A climate that varies with position, known everywhere, so nothing is spread.
        LodClimateField.Sampler everywhere = (int x, int z, out byte t, out byte r) =>
        {
            t = (byte)(100 + (LodClimateField.CellOf(x) & 15));
            r = (byte)(30 + (LodClimateField.CellOf(z) & 15));
            return true;
        };

        LodClimateField field = Build(32, 0, 0, everywhere);
        c.True(field.Active, "a fully known field is usable");
        c.Eq(32 * 32, field.KnownCells, "every cell was answered");

        for (int z = -3; z <= 3; z++)
        {
            for (int x = -3; x <= 3; x++)
            {
                var got = Read(field, x, z);
                c.Eq(100 + (x & 15), got.Temperature, $"cell {x},{z} keeps its temperature");
                c.Eq(30 + (z & 15), got.Rainfall, $"cell {x},{z} keeps its rainfall");
                c.Eq(0, got.Spread, $"cell {x},{z} was not spread into");
            }
        }
    }

    static void OneKnownCellFillsTheWholeWindow(Check c)
    {
        // The worst real case: the player has just loaded in and the client holds almost
        // nothing. Every cell must still carry a climate, and it must be the one real climate
        // there is rather than a default.
        LodClimateField.Sampler onlyOrigin = (int x, int z, out byte t, out byte r) =>
        {
            t = 77; r = 123;
            return LodClimateField.CellOf(x) == 0 && LodClimateField.CellOf(z) == 0;
        };

        LodClimateField field = Build(32, 0, 0, onlyOrigin);
        c.Eq(1, field.KnownCells, "exactly one cell was answered");
        c.True(field.Active, "one known cell is enough to publish");

        for (int z = -16; z < 16; z++)
        {
            for (int x = -16; x < 16; x++)
            {
                var got = Read(field, x, z);
                c.Eq(77, got.Temperature, $"cell {x},{z} took the one known temperature");
                c.Eq(123, got.Rainfall, $"cell {x},{z} took the one known rainfall");
            }
        }
        c.Eq(0, Read(field, 0, 0).Spread, "the known cell was not spread into");
        c.True(Read(field, 10, 0).Spread > 0, "a distant cell records that it was spread");
    }

    static void TheNearerClimateWins(Check c)
    {
        // Two known places with very different climates and unknown ground between them. A
        // cell must take the nearer one - the spread is a nearest-neighbour fill, and if it
        // ran as a plain flood from one side the whole gap would take whichever it met first.
        LodClimateField.Sampler twoPlaces = (int x, int z, out byte t, out byte r) =>
        {
            int cx = LodClimateField.CellOf(x);
            int cz = LodClimateField.CellOf(z);
            t = 0; r = 0;
            if (cz != 0) return false;
            if (cx == -10) { t = 20; r = 200; return true; }
            if (cx == 10) { t = 220; r = 40; return true; }
            return false;
        };

        LodClimateField field = Build(32, 0, 0, twoPlaces);
        c.Eq(2, field.KnownCells, "two cells were answered");

        for (int x = -9; x <= -1; x++)
            c.Eq(20, Read(field, x, 0).Temperature, $"cell {x} is nearer the cold place");
        for (int x = 1; x <= 9; x++)
            c.Eq(220, Read(field, x, 0).Temperature, $"cell {x} is nearer the warm place");

        // Off-axis too: the chamfer must spread in a circle, not along rows only.
        c.Eq(20, Read(field, -8, 4).Temperature, "a cell off the axis still takes the nearer");
        c.Eq(220, Read(field, 8, -4).Temperature, "and so does one on the other side");
    }

    static void NothingKnownPublishesNothing(Check c)
    {
        LodClimateField.Sampler blind = (int x, int z, out byte t, out byte r) =>
        {
            t = 0; r = 0;
            return false;
        };

        LodClimateField field = Build(32, 0, 0, blind);
        c.Eq(0, field.KnownCells, "nothing was answered");
        c.False(field.Active, "a field that knows nothing refuses to be used");
        for (int i = 0; i < field.Texels.Length; i++)
        {
            c.Eq(0, (field.Texels[i] >> 16) & 0xFF, "no temperature is claimed");
            c.Eq(0, (field.Texels[i] >> 8) & 0xFF, "no rainfall is claimed");
            c.Eq(255, field.Texels[i] & 0xFF, "and the cell says it was never reached");
        }
    }

    static void ARebuiltWindowCannotLeakTheOldOne(Check c)
    {
        // The defect this is written against: the buffers are reused between refreshes, so a
        // window rebuilt somewhere the client knows nothing about could publish the climate of
        // the place the player came from. That is the same shape as the stale season pixel
        // that made distant colour disappear and reappear, and it is invisible in a diff.
        LodClimateField.Sampler warm = (int x, int z, out byte t, out byte r) =>
        {
            t = 210; r = 15;
            return true;
        };
        LodClimateField.Sampler blind = (int x, int z, out byte t, out byte r) =>
        {
            t = 0; r = 0;
            return false;
        };

        var field = new LodClimateField(32);
        field.BeginRefresh(0, 0);
        for (int row = 0; row < field.RefreshRowCount; row++) field.RefreshRow(row, warm);
        field.CompleteRefresh();
        c.Eq(210, Read(field, 0, 0).Temperature, "the first window published its climate");

        // Somewhere else entirely, and nothing is known there.
        field.BeginRefresh(1_000_000, 1_000_000);
        for (int row = 0; row < field.RefreshRowCount; row++) field.RefreshRow(row, blind);
        field.CompleteRefresh();

        c.False(field.Active, "the new window knows nothing and says so");
        for (int i = 0; i < field.Texels.Length; i++)
        {
            c.Eq(0, (field.Texels[i] >> 16) & 0xFF,
                "no texel carries the previous window's temperature");
            c.Eq(0, (field.Texels[i] >> 8) & 0xFF,
                "no texel carries the previous window's rainfall");
        }
    }
}
