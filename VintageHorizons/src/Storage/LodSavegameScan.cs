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
/// false *positive* - reporting change when something the capture cannot see moved -
/// which costs an unnecessary load and never a wrong answer. Nothing here may cause a
/// claim about content; it may only cause work to be skipped, and only once that column's
/// capture has durably succeeded.
///
/// The fingerprint covers the landscape rather than the whole stored row. A chunk row also
/// carries light, creatures, other mods' chunk data and every block entity's private state,
/// none of which reaches a captured section: hashing them made a berry bush's growth timer
/// cost a reload. Measured offline on the owner's world over 25 days of play, dropping them
/// removes 17.8% of all flagged columns (13,754 to 11,307) and 36.7% of the flags a single
/// session produced, with no column flagged that a whole-row hash did not already flag.
///
/// What stays in: every block in the column, fluids, the contents of chiselled blocks - a
/// chisel has no colour of its own, so distant ruins are drawn from its materials - the
/// layer count and the structural markers, and anything the exclusion list does not name.
/// The list is exclusion rather than inclusion on purpose: a field a future game version
/// adds counts as a change until someone decides otherwise.
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

    /// <summary>
    /// Protobuf fields of a stored chunk row, as the engine writes them. Only the ones this
    /// file names are interpreted; every other number is hashed without being understood.
    /// </summary>
    const int BlockEntitiesField = 8;
    const int MaxKnownField = 32;

    /// <summary>
    /// Fields dropped from the fingerprint because a captured section cannot show them:
    /// 2 block light, 3 light saturation, 5 entity count, 6 entities, 7 block-entity count,
    /// 9 mod data, 10 light positions, 11 server mod data, 14 decors, 17 blocks placed,
    /// 18 blocks removed.
    ///
    /// Decors are attached to blocks and the capture does not read them; the owner's call on
    /// 2026-09-20 was that ground gaining decor is being worked on for other reasons anyway,
    /// so a decor change alone is not worth a reload. Everything else here is state the
    /// distant view has never drawn.
    /// </summary>
    static readonly bool[] DroppedFields = BuildDroppedFields(2, 3, 5, 6, 7, 9, 10, 11, 14, 17, 18);

    readonly LodSavegameReader reader;

    /// <summary>
    /// Block-entity class codes whose contents the capture reads, as UTF-8 for comparison
    /// against the name each stored entry starts with. Empty means "keep every block entity",
    /// which is the pre-0.4.118 behaviour: without the registry this cannot tell a chiselled
    /// block from a chest, and silently dropping chisels would grey out distant ruins.
    /// </summary>
    readonly byte[][] captureBlockEntities;

    byte[] blobBuffer = new byte[BlobBufferBytes];

    public LodSavegameScan(LodSavegameReader reader,
        IReadOnlyCollection<string>? captureBlockEntityClasses = null)
    {
        this.reader = reader;
        captureBlockEntities = captureBlockEntityClasses == null
            ? Array.Empty<byte[]>()
            : captureBlockEntityClasses
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(System.Text.Encoding.UTF8.GetBytes)
                .ToArray();
    }

    static bool[] BuildDroppedFields(params int[] numbers)
    {
        var dropped = new bool[MaxKnownField];
        foreach (int number in numbers) dropped[number] = true;
        return dropped;
    }

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
        return reader.Read("SELECT position, data FROM chunk;", row =>
        {
            long position = row.GetInt64(0);
            if (PositionDimension(position) != 0) return;

            ulong rowHash = HashRow(row);
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
        bool ok = reader.Read(
            "SELECT position, data FROM chunk WHERE position IN (" + string.Join(",", positions) + ");",
            row =>
            {
                long position = row.GetInt64(0);
                if (PositionDimension(position) != 0) return;
                ulong rowHash = HashRow(row);
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

    /// <summary>
    /// Hashes one stored chunk row's landscape: every field except the ones a captured
    /// section cannot show, with the block-entity list reduced to the classes the capture
    /// actually reads.
    ///
    /// A row that does not parse as the shape described here is hashed whole. That is both
    /// the old behaviour and the safe direction: an unreadable row then reports change on
    /// every pass, which costs loads rather than correctness.
    /// </summary>
    ulong HashRow(SqliteDataReader row)
    {
        if (row.IsDBNull(1)) return Mix(0xC2B2AE3D27D4EB4FUL);

        int length = ReadBlob(row);
        ReadOnlySpan<byte> blob = blobBuffer.AsSpan(0, length);

        var hash = new LodBlobHash();
        if (TryHashLandscape(blob, ref hash)) return hash.Value;

        var whole = new LodBlobHash();
        whole.Add(blob);
        return whole.Value;
    }

    /// <summary>Reads the whole blob into the reusable buffer and returns its length.</summary>
    int ReadBlob(SqliteDataReader row)
    {
        using Stream blob = row.GetStream(1);
        int length = 0;
        while (true)
        {
            if (length == blobBuffer.Length) Array.Resize(ref blobBuffer, blobBuffer.Length * 2);
            int read = blob.Read(blobBuffer, length, blobBuffer.Length - length);
            if (read <= 0) return length;
            length += read;
        }
    }

    /// <summary>
    /// Walks the row's top-level fields, adding the ones that describe the landscape. Returns
    /// false the moment the bytes stop making sense, leaving the caller to hash them whole;
    /// a partially fed hash is discarded rather than trusted.
    /// </summary>
    bool TryHashLandscape(ReadOnlySpan<byte> blob, ref LodBlobHash hash)
    {
        int offset = 0;
        while (offset < blob.Length)
        {
            int start = offset;
            if (!TryReadVarint(blob, ref offset, out ulong tag)) return false;

            int number = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            if (number == 0) return false;

            int payloadStart = offset;
            int payloadLength = 0;
            switch (wire)
            {
                case 0:
                    if (!TryReadVarint(blob, ref offset, out _)) return false;
                    break;
                case 1:
                    if (offset + 8 > blob.Length) return false;
                    offset += 8;
                    break;
                case 2:
                    if (!TryReadVarint(blob, ref offset, out ulong size)) return false;
                    if (size > (ulong)(blob.Length - offset)) return false;
                    payloadStart = offset;
                    payloadLength = (int)size;
                    offset += payloadLength;
                    break;
                case 5:
                    if (offset + 4 > blob.Length) return false;
                    offset += 4;
                    break;
                default:
                    // Groups and unassigned wire types. The engine writes none of them, and
                    // guessing at a length here would silently skip real content.
                    return false;
            }

            if (number < MaxKnownField && DroppedFields[number]) continue;

            if (number == BlockEntitiesField && wire == 2
                && !CaptureReads(blob.Slice(payloadStart, payloadLength)))
            {
                continue;
            }

            hash.Add(blob[start..offset]);
        }
        return true;
    }

    /// <summary>
    /// True when this stored block entity is one whose contents reach a captured section.
    ///
    /// The engine writes a block entity as its class code - a 7-bit encoded length then
    /// UTF-8, the shape <c>BinaryWriter.Write(string)</c> produces - followed by its tree
    /// attributes. Only the name is read here. An entry whose name cannot be read is kept,
    /// because the alternative is dropping something the capture may depend on.
    /// </summary>
    bool CaptureReads(ReadOnlySpan<byte> entry)
    {
        if (captureBlockEntities.Length == 0) return true;
        if (!TryReadSevenBitLength(entry, out int nameStart, out int nameLength)) return true;

        ReadOnlySpan<byte> name = entry.Slice(nameStart, nameLength);
        foreach (byte[] candidate in captureBlockEntities)
        {
            if (name.SequenceEqual(candidate)) return true;
        }
        return false;
    }

    /// <summary>The length prefix .NET's BinaryWriter puts in front of a string.</summary>
    static bool TryReadSevenBitLength(ReadOnlySpan<byte> entry, out int start, out int length)
    {
        start = 0;
        length = 0;
        int value = 0, shift = 0, offset = 0;
        while (offset < entry.Length && shift <= 28)
        {
            byte piece = entry[offset++];
            value |= (piece & 0x7F) << shift;
            if (piece < 0x80)
            {
                if (value < 0 || value > entry.Length - offset) return false;
                start = offset;
                length = value;
                return true;
            }
            shift += 7;
        }
        return false;
    }

    static bool TryReadVarint(ReadOnlySpan<byte> blob, ref int offset, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (offset < blob.Length && shift <= 63)
        {
            byte piece = blob[offset++];
            value |= (ulong)(piece & 0x7F) << shift;
            if (piece < 0x80) return true;
            shift += 7;
        }
        return false;
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
