using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using VintageHorizons.Net;

namespace VintageHorizons;

/// <summary>What the savegame currently holds for one chunk column.</summary>
/// <param name="Fingerprint">Content hash over the column's chunk rows. Never zero when rows exist.</param>
/// <param name="Rows">How many vertical chunks the column holds.</param>
public readonly record struct LodColumnFingerprint(ulong Fingerprint, int Rows);

/// <summary>
/// Reads the savegame's own answers to "which columns exist?" and "has this column
/// changed?" through the private read path (terrain-change plan phase A2).
///
/// A fingerprint is sound here because <c>ServerSystemLoadAndSaveGame</c> writes only
/// chunks whose <c>DirtyForSaving</c> flag is set: an untouched chunk is never rewritten,
/// so its stored bytes stay byte-identical. That means a fingerprint can only produce a
/// false *positive* - reporting change when volatile state such as light, snow or block
/// entities moved but terrain did not - which costs an unnecessary load and never a wrong
/// answer. Nothing here may cause a claim about content; it may only cause work to be
/// skipped, and only once that column's capture has durably succeeded.
/// </summary>
public sealed class LodSavegameScan
{
    /// <summary>
    /// Chunk-position layout, shared by every terrain table. Verified against the engine's
    /// own saves: x in bits 0-21, dimension low in 22-26, z in 27-48, dimension high in
    /// 49-53, y in 54-62. <c>mapchunk</c> and <c>mapregion</c> use the same encoding with
    /// y = 0, which is why one decoder serves all three.
    /// </summary>
    const long ChunkMask = 0x3FFFFF;
    const long YMask = 0x1FF;
    const long DimensionLowMask = 0x1F;
    const long DimensionHighMask = 0x3E0;

    /// <summary>Blob bytes read at a time. Large enough to amortise the call, small enough to reuse.</summary>
    const int BlobBufferBytes = 64 * 1024;

    readonly LodSavegameReader reader;

    public LodSavegameScan(LodSavegameReader reader) => this.reader = reader;

    public static int PositionX(long position) => (int)(position & ChunkMask);
    public static int PositionZ(long position) => (int)((position >> 27) & ChunkMask);
    public static int PositionY(long position) => (int)((position >> 54) & YMask);

    /// <summary>
    /// Which dimension a row belongs to. Only zero is the ordinary world; the sweep must
    /// never treat another dimension's rows as overworld columns.
    /// </summary>
    public static int PositionDimension(long position) =>
        (int)(((position >> 22) & DimensionLowMask) + ((position >> 44) & DimensionHighMask));

    /// <summary>
    /// Fills <paramref name="into"/> with every column the savegame holds, replacing what
    /// 70,225 paced engine probes answer today. Returns false when the read path is
    /// unavailable or the read failed, and the caller must then fall back to probing.
    /// </summary>
    public bool ReadExistence(LodColumnMap into, Func<bool>? shouldStop = null) =>
        reader.Read("SELECT position FROM mapchunk;", row =>
        {
            long position = row.GetInt64(0);
            if (PositionDimension(position) != 0) return;
            into.Add(PositionX(position), PositionZ(position));
        }, shouldStop);

    /// <summary>
    /// Accumulates one fingerprint per chunk column over the whole <c>chunk</c> table.
    ///
    /// Each row's hash is mixed with its own y before being combined into the column, and
    /// the rows are counted, so the result is sensitive to which layers are present without
    /// needing them in order. That matters: ordering by position would sort y-major and
    /// scatter each column's rows across the whole table, while an unordered sequential
    /// scan is the access pattern the file is fastest at.
    /// </summary>
    public bool ReadFingerprints(Dictionary<long, LodColumnFingerprint> into,
        Func<bool>? shouldStop = null)
    {
        byte[] buffer = new byte[BlobBufferBytes];
        return reader.Read("SELECT position, data FROM chunk;", row =>
        {
            long position = row.GetInt64(0);
            if (PositionDimension(position) != 0) return;

            ulong rowHash = HashBlob(row, buffer);
            // Mixing y in makes the per-column combination order-independent while still
            // distinguishing a column that gained or lost a layer at a particular height.
            rowHash = Mix(rowHash ^ Mix((ulong)PositionY(position) + 0x9E3779B97F4A7C15UL));

            long key = LodColumnMap.Key(PositionX(position), PositionZ(position));
            if (into.TryGetValue(key, out LodColumnFingerprint existing))
            {
                into[key] = new LodColumnFingerprint(
                    existing.Fingerprint ^ rowHash, existing.Rows + 1);
            }
            else
            {
                into[key] = new LodColumnFingerprint(rowHash, 1);
            }
        }, shouldStop);
    }

    /// <summary>
    /// Fingerprints one column by asking for its rows directly, for the background audit.
    ///
    /// A column's chunk rows are scattered through the table because y occupies the high
    /// bits of the position, so this asks for each of its vertical positions by primary key
    /// rather than scanning. Random point lookups measured 13,990/s against a sequential
    /// scan's 12,536/s, so a handful of keyed lookups is the cheap way to re-examine a few
    /// columns without re-reading the world.
    ///
    /// The accumulation is identical to <see cref="ReadFingerprints"/>, so the two are
    /// directly comparable - which is the entire point of an audit.
    /// </summary>
    public bool FingerprintColumn(int cx, int cz, int chunksY, out LodColumnFingerprint print)
    {
        print = default;
        if (chunksY <= 0) return false;

        var positions = new List<string>(chunksY);
        var parameters = new List<(string Name, long Value)>(chunksY);
        for (int y = 0; y < chunksY; y++)
        {
            string name = "$p" + y;
            positions.Add(name);
            parameters.Add((name, Index(cx, y, cz)));
        }

        ulong accumulated = 0;
        int rows = 0;
        byte[] buffer = new byte[BlobBufferBytes];
        bool ok = reader.Read(
            "SELECT position, data FROM chunk WHERE position IN (" + string.Join(",", positions) + ");",
            row =>
            {
                long position = row.GetInt64(0);
                if (PositionDimension(position) != 0) return;
                ulong rowHash = HashBlob(row, buffer);
                rowHash = Mix(rowHash ^ Mix((ulong)PositionY(position) + 0x9E3779B97F4A7C15UL));
                accumulated ^= rowHash;
                rows++;
            },
            parameters: parameters);

        if (!ok) return false;
        print = new LodColumnFingerprint(accumulated, rows);
        return true;
    }

    /// <summary>The engine's packed chunk position for dimension zero.</summary>
    public static long Index(int x, int y, int z) =>
        ((long)x & ChunkMask) | (((long)z & ChunkMask) << 27) | (((long)y & YMask) << 54);

    /// <summary>
    /// Folds the row count into the accumulated hash. Callers compare this, not the raw
    /// accumulator, so two columns whose rows happened to cancel under XOR still differ
    /// when their layer counts do.
    /// </summary>
    public static ulong Seal(LodColumnFingerprint column) =>
        Mix(column.Fingerprint ^ Mix((ulong)column.Rows + 0xD6E8FEB86659FD93UL));

    static ulong HashBlob(SqliteDataReader row, byte[] buffer)
    {
        if (row.IsDBNull(1)) return Mix(0xC2B2AE3D27D4EB4FUL);

        var hash = new LodBlobHash();
        using Stream blob = row.GetStream(1);
        int read;
        while ((read = blob.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.Add(buffer.AsSpan(0, read));
        }
        return hash.Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong Mix(ulong value)
    {
        // splitmix64's finaliser. Change detection, not security: what it has to do is
        // spread a one-bit input difference across the whole word, which it does.
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}

/// <summary>
/// A 64-bit content hash fed in arbitrary pieces, so a blob can be hashed while it streams
/// rather than after it has been copied into one array. Carries up to seven bytes between
/// calls so the result never depends on how the reads happened to split.
/// </summary>
internal struct LodBlobHash
{
    const ulong Prime = 0x9E3779B97F4A7C15UL;

    ulong state = 0xCBF29CE484222325UL;
    ulong carry;
    int carried;
    long length;

    public LodBlobHash() { }

    public void Add(ReadOnlySpan<byte> bytes)
    {
        length += bytes.Length;

        // Finish any partial word left by the previous piece first.
        while (carried > 0 && bytes.Length > 0)
        {
            carry |= (ulong)bytes[0] << (carried * 8);
            carried++;
            bytes = bytes[1..];
            if (carried == 8)
            {
                Absorb(carry);
                carry = 0;
                carried = 0;
            }
        }

        // The loop above only leaves a carry when this piece ran out mid-word. Falling
        // through would reset `carried` to the empty remainder below and silently drop
        // those bytes, which is exactly what feeding one byte at a time used to do.
        if (carried > 0) return;

        while (bytes.Length >= 8)
        {
            Absorb(BinaryPrimitives.ReadUInt64LittleEndian(bytes));
            bytes = bytes[8..];
        }

        for (int i = 0; i < bytes.Length; i++)
        {
            carry |= (ulong)bytes[i] << (i * 8);
        }
        carried = bytes.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Absorb(ulong word)
    {
        state ^= LodSavegameScan.Mix(word);
        state = BitOperations.RotateLeft(state, 27) * Prime + 0xFF51AFD7ED558CCDUL;
    }

    /// <summary>The hash of everything added so far, including a trailing partial word.</summary>
    public readonly ulong Value
    {
        get
        {
            ulong final = state;
            if (carried > 0)
            {
                // The partial word carries its own length, so trailing zero bytes cannot be
                // confused with a shorter blob.
                final ^= LodSavegameScan.Mix(carry ^ (ulong)carried);
            }
            return LodSavegameScan.Mix(final ^ (ulong)length);
        }
    }
}
