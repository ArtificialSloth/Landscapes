namespace VintageHorizons;

/// <summary>
/// Climate the client has told us once, kept so it does not have to tell us again.
///
/// The climate field can only sample where the client holds a map region - about a thousand
/// blocks on arrival, growing toward three thousand as the player explores, against eight
/// thousand of drawn terrain - and it relearns that from nothing every session. This is the
/// memory that makes exploration cumulative: a cell answered once is answered forever, so
/// coverage grows toward everywhere the player has ever been rather than resetting on login.
///
/// It is keyed on WORLD cells, not on LOD sections, so it is independent of the terrain cache
/// and needs nothing from the blob format. A section can be evicted, re-captured or arrive from
/// another player's cache without touching any of this.
///
/// Only REAL answers are stored. Cells the field filled by spreading outward from a neighbour
/// are derived, and storing a derivation would let one guess become the seed of the next.
/// </summary>
public sealed class LodClimateMemory
{
    /// <summary>
    /// Cells per patch edge. 32 x 32 cells is 2,048 blocks square and 2 KiB of payload, so a
    /// window of the field is a couple of dozen rows rather than sixteen thousand. Large enough
    /// that row overhead disappears, small enough that a player crossing a patch boundary does
    /// not drag a megabyte in.
    /// </summary>
    public const int PatchCells = 32;

    /// <summary>Cells in one patch.</summary>
    public const int CellsPerPatch = PatchCells * PatchCells;

    /// <summary>One bit per cell saying whether it was ever answered, then two bytes per cell.</summary>
    public const int MaskBytes = CellsPerPatch / 8;
    public const int BlobBytes = 1 + MaskBytes + CellsPerPatch * 2;

    /// <summary>Bumped if the layout changes. A blob that does not match is ignored, not read.</summary>
    public const byte BlobFormat = 1;

    sealed class Patch
    {
        public readonly byte[] Mask = new byte[MaskBytes];
        public readonly byte[] Temperature = new byte[CellsPerPatch];
        public readonly byte[] Rainfall = new byte[CellsPerPatch];
        public bool Dirty;
    }

    readonly Dictionary<long, Patch> patches = new();
    readonly HashSet<long> requested = new();

    public int PatchCount => patches.Count;
    public int PendingRequests => requested.Count;

    /// <summary>Which patch a cell belongs to. Floor division, so it works either side of zero.</summary>
    public static int PatchOf(int cell) =>
        cell >= 0 ? cell / PatchCells : -(((-cell) + PatchCells - 1) / PatchCells);

    /// <summary>One key for a patch coordinate pair, stable across sessions.</summary>
    public static long PatchKey(int patchX, int patchZ) =>
        ((long)(uint)patchX << 32) | (uint)patchZ;

    public static int PatchXOf(long key) => (int)(key >> 32);
    public static int PatchZOf(long key) => (int)(key & 0xFFFFFFFF);

    /// <summary>Where a cell sits inside its own patch.</summary>
    public static int IndexIn(int cellX, int cellZ)
    {
        int lx = cellX - PatchOf(cellX) * PatchCells;
        int lz = cellZ - PatchOf(cellZ) * PatchCells;
        return lz * PatchCells + lx;
    }

    /// <summary>The remembered climate for a cell, if it was ever answered.</summary>
    public bool TryGet(int cellX, int cellZ, out byte temperature, out byte rainfall)
    {
        temperature = rainfall = 0;
        if (!patches.TryGetValue(PatchKey(PatchOf(cellX), PatchOf(cellZ)), out Patch? patch))
            return false;

        int i = IndexIn(cellX, cellZ);
        if ((patch.Mask[i >> 3] & (1 << (i & 7))) == 0) return false;
        temperature = patch.Temperature[i];
        rainfall = patch.Rainfall[i];
        return true;
    }

    /// <summary>
    /// Record a real answer. Rewriting a cell with the same values leaves the patch clean, so a
    /// refresh that learns nothing new writes nothing to disk - which is the common case once an
    /// area has been visited.
    /// </summary>
    public void Learn(int cellX, int cellZ, byte temperature, byte rainfall)
    {
        long key = PatchKey(PatchOf(cellX), PatchOf(cellZ));
        if (!patches.TryGetValue(key, out Patch? patch))
        {
            patch = new Patch();
            patches[key] = patch;
        }

        int i = IndexIn(cellX, cellZ);
        bool known = (patch.Mask[i >> 3] & (1 << (i & 7))) != 0;
        if (known && patch.Temperature[i] == temperature && patch.Rainfall[i] == rainfall) return;

        patch.Mask[i >> 3] |= (byte)(1 << (i & 7));
        patch.Temperature[i] = temperature;
        patch.Rainfall[i] = rainfall;
        patch.Dirty = true;
        requested.Remove(key);
    }

    /// <summary>
    /// True when this cell's patch has never been loaded or learned, so it is worth asking the
    /// store for. Answers false the second time for the same patch: a load is in flight and
    /// asking again would queue the same read behind itself every frame.
    /// </summary>
    public bool ShouldRequest(int cellX, int cellZ, out long key)
    {
        key = PatchKey(PatchOf(cellX), PatchOf(cellZ));
        if (patches.ContainsKey(key)) return false;
        return requested.Add(key);
    }

    /// <summary>A load came back empty. The patch exists now, so nothing asks for it again.</summary>
    public void AcceptEmpty(long key)
    {
        requested.Remove(key);
        patches.TryAdd(key, new Patch());
    }

    /// <summary>
    /// Take a blob from the store. A blob that is not the length or format this build writes is
    /// ignored rather than parsed: climate is cheap to relearn and a misread one would be a
    /// confident wrong colour over a whole 2,048-block square.
    /// </summary>
    public bool Accept(long key, byte[]? blob)
    {
        requested.Remove(key);
        if (blob == null || blob.Length != BlobBytes || blob[0] != BlobFormat)
        {
            patches.TryAdd(key, new Patch());
            return false;
        }

        var patch = new Patch();
        Array.Copy(blob, 1, patch.Mask, 0, MaskBytes);
        Array.Copy(blob, 1 + MaskBytes, patch.Temperature, 0, CellsPerPatch);
        Array.Copy(blob, 1 + MaskBytes + CellsPerPatch, patch.Rainfall, 0, CellsPerPatch);
        patches[key] = patch;
        return true;
    }

    /// <summary>Serialise one patch, or null when nothing has been learned in it.</summary>
    public byte[]? Encode(long key)
    {
        if (!patches.TryGetValue(key, out Patch? patch)) return null;
        var blob = new byte[BlobBytes];
        blob[0] = BlobFormat;
        Array.Copy(patch.Mask, 0, blob, 1, MaskBytes);
        Array.Copy(patch.Temperature, 0, blob, 1 + MaskBytes, CellsPerPatch);
        Array.Copy(patch.Rainfall, 0, blob, 1 + MaskBytes + CellsPerPatch, CellsPerPatch);
        return blob;
    }

    /// <summary>
    /// Patches changed since the last call, and clears their dirty flags. The caller writes
    /// them; a failed write simply means they are learned again next time the player is there,
    /// which is why this does not track acknowledgements the way the section store does.
    /// </summary>
    public List<long> TakeDirtyKeys()
    {
        var dirty = new List<long>();
        foreach ((long key, Patch patch) in patches)
        {
            if (!patch.Dirty) continue;
            patch.Dirty = false;
            dirty.Add(key);
        }
        return dirty;
    }

    /// <summary>How many cells in a patch carry a real answer. For diagnostics.</summary>
    public int KnownCellsIn(long key)
    {
        if (!patches.TryGetValue(key, out Patch? patch)) return 0;
        int known = 0;
        foreach (byte b in patch.Mask) known += System.Numerics.BitOperations.PopCount(b);
        return known;
    }

    /// <summary>Total cells remembered across every patch held in memory.</summary>
    public int KnownCells()
    {
        int known = 0;
        foreach (Patch patch in patches.Values)
        {
            foreach (byte b in patch.Mask) known += System.Numerics.BitOperations.PopCount(b);
        }
        return known;
    }

    /// <summary>Leaving a world. Nothing here is valid in the next one.</summary>
    public void Clear()
    {
        patches.Clear();
        requested.Clear();
    }
}
