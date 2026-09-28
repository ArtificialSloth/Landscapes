using System.Buffers;

namespace VintageHorizons;

/// <summary>
/// Arrays that carry a job's output from a worker to the owning thread and are dead once it
/// has been consumed, rented from the shared array pool instead of allocated (0.4.131).
///
/// The owner's 0.4.130 log put this mod's mesh threads at 40-85 MB/s of garbage, nearly all
/// of it the finished mesh arrays: built, uploaded once, then dropped. Rented, the same few
/// arrays carry mesh after mesh. A rented array is at least as long as asked for and usually
/// longer, so everything that reads one goes by the job's counts, never by the array's length.
/// </summary>
internal static class LodMeshArrays
{
    /// <summary>A rented copy of the list's contents, or an empty array for an empty list.</summary>
    public static T[] Rent<T>(List<T> source)
    {
        int count = source.Count;
        if (count == 0) return Array.Empty<T>();
        T[] array = ArrayPool<T>.Shared.Rent(count);
        source.CopyTo(array);
        return array;
    }

    /// <summary>Hands a rented array back. Empty and null arrays were never rented.</summary>
    public static void Return<T>(T[]? array)
    {
        if (array == null || array.Length == 0) return;
        ArrayPool<T>.Shared.Return(array);
    }

    /// <summary>
    /// The size to give a reused array that must hold <paramref name="count"/>: an eighth of
    /// headroom, in 4,096-element steps, so a job a little larger than the last still fits.
    /// </summary>
    public static int PooledLength(int count) =>
        checked((count + (count >> 3) + 4095) & ~4095);

    /// <summary>
    /// A capture's per-column batch: one slot per column of a section, all null. Cleared on
    /// rent as well as on return, because the shared pool is the whole process's.
    /// </summary>
    public static ulong[]?[] RentColumnBatch()
    {
        ulong[]?[] batch = ArrayPool<ulong[]?>.Shared.Rent(LodSection.GridSize * LodSection.GridSize);
        Array.Clear(batch);
        return batch;
    }

    public static void ReturnColumnBatch(ulong[]?[] batch)
    {
        if (batch.Length == 0) return;
        ArrayPool<ulong[]?>.Shared.Return(batch, clearArray: true);
    }
}
