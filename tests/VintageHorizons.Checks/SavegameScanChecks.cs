using Microsoft.Data.Sqlite;
using VintageHorizons;
using VintageHorizons.Net;

namespace VintageHorizons.Checks;

/// <summary>
/// Existence and change detection read from the savegame (terrain-change plan phase A2).
///
/// The properties worth proving are the ones whose failure is silent. A fingerprint that
/// missed a change would make the sweep skip a column forever; one that depended on how a
/// blob happened to arrive from SQLite would make every column look changed on some runs
/// and not others.
/// </summary>
public static class SavegameScanChecks
{
    public static void Run(Check c)
    {
        DecodesPositions(c);
        HashIgnoresHowBytesArrive(c);

        string dir = Path.Combine(Path.GetTempPath(),
            "vh-savegame-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            FingerprintsFollowTheSavegame(c, Path.Combine(dir, "world.vcdbs"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { /* a temp directory */ }
        }
    }

    static void DecodesPositions(Check c)
    {
        long position = Index(8053, 7, 7990);
        c.Eq(8053, LodSavegameScan.PositionX(position), "x comes out of a packed position");
        c.Eq(7, LodSavegameScan.PositionY(position), "y comes out of a packed position");
        c.Eq(7990, LodSavegameScan.PositionZ(position), "z comes out of a packed position");
        c.Eq(0, LodSavegameScan.PositionDimension(position),
            "an ordinary overworld row is dimension zero");

        // Another dimension's rows must never be mistaken for overworld columns; they would
        // add positions the overworld does not have and make the sweep load nothing there.
        long other = Index(10, 3, 20, dimension: 2);
        c.Eq(2, LodSavegameScan.PositionDimension(other), "a dimension is decoded from its split field");
        c.Eq(10, LodSavegameScan.PositionX(other), "x survives a non-zero dimension");
        c.Eq(20, LodSavegameScan.PositionZ(other), "z survives a non-zero dimension");
    }

    static void HashIgnoresHowBytesArrive(Check c)
    {
        byte[] payload = new byte[1000];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 31 + 7);

        ulong whole = HashInPieces(payload, payload.Length);
        c.Eq(whole, HashInPieces(payload, 1),
            "hashing one byte at a time matches hashing the whole blob");
        c.Eq(whole, HashInPieces(payload, 3),
            "a piece size that is not a multiple of eight matches");
        c.Eq(whole, HashInPieces(payload, 8), "an aligned piece size matches");
        c.Eq(whole, HashInPieces(payload, 999),
            "a piece size that leaves a one-byte tail matches");

        // Length is folded in, so a shorter blob and a zero-padded one differ. Without that,
        // a truncated chunk would fingerprint as unchanged.
        byte[] shorter = payload[..999];
        c.True(HashInPieces(shorter, 64) != whole,
            "a blob that lost its last byte hashes differently");

        byte[] flipped = (byte[])payload.Clone();
        flipped[500] ^= 0x01;
        c.True(HashInPieces(flipped, 64) != whole, "a single flipped bit changes the hash");
    }

    static void FingerprintsFollowTheSavegame(Check c, string path)
    {
        // Two columns of four layers each, plus one row in another dimension.
        CreateWorld(path, new[]
        {
            (Index(100, 0, 200), (byte)1), (Index(100, 1, 200), (byte)2),
            (Index(100, 2, 200), (byte)3), (Index(100, 3, 200), (byte)4),
            (Index(101, 0, 200), (byte)5), (Index(101, 1, 200), (byte)6),
            (Index(101, 2, 200), (byte)7), (Index(101, 3, 200), (byte)8),
            (Index(900, 0, 900, dimension: 1), (byte)9),
        }, new[] { Index(100, 0, 200), Index(101, 0, 200), Index(900, 0, 900, dimension: 1) });

        var logger = new CaptureLogger();
        using var reader = new LodSavegameReader(logger, path);
        c.True(reader.Open(), "the fingerprint fixture opens");
        var scan = new LodSavegameScan(reader);

        var exists = new LodColumnMap();
        c.True(scan.ReadExistence(exists), "the existence map reads");
        c.Eq(2, exists.Count, "only the two overworld columns are recorded");
        c.Eq(EnumColumnAction.Peek, exists.Classify(100, 900),
            "a position the savegame does not hold stays absent");

        var before = new Dictionary<long, LodColumnFingerprint>();
        c.True(scan.ReadFingerprints(before), "fingerprints read");
        c.Eq(2, before.Count, "one fingerprint per overworld column, and none for other dimensions");

        long first = LodColumnMap.Key(100, 200);
        long second = LodColumnMap.Key(101, 200);
        c.Eq(4, before[first].Rows, "the first column counts its four layers");
        c.Eq(4, before[second].Rows, "the second column counts its four layers");
        c.True(LodSavegameScan.Seal(before[first]) != LodSavegameScan.Seal(before[second]),
            "two columns holding different terrain have different fingerprints");

        // Re-reading an untouched savegame must give the same answer, or nothing could ever
        // be skipped.
        var again = new Dictionary<long, LodColumnFingerprint>();
        c.True(scan.ReadFingerprints(again), "fingerprints read a second time");
        c.Eq(LodSavegameScan.Seal(before[first]), LodSavegameScan.Seal(again[first]),
            "an unchanged column fingerprints identically on a second pass");
        c.Eq(LodSavegameScan.Seal(before[second]), LodSavegameScan.Seal(again[second]),
            "the second column is stable too");

        // A changed layer must move its own column and leave its neighbour alone.
        Write(path, "UPDATE chunk SET data = x'FF' WHERE position = $p;", Index(100, 2, 200));
        var edited = new Dictionary<long, LodColumnFingerprint>();
        c.True(scan.ReadFingerprints(edited), "fingerprints read after an edit");
        c.True(LodSavegameScan.Seal(edited[first]) != LodSavegameScan.Seal(before[first]),
            "editing one chunk changes its column's fingerprint");
        c.Eq(LodSavegameScan.Seal(before[second]), LodSavegameScan.Seal(edited[second]),
            "editing one column leaves the next column's fingerprint alone");

        // A lost layer must be visible even though every surviving layer is untouched. This
        // is what the row count is for.
        Write(path, "DELETE FROM chunk WHERE position = $p;", Index(101, 3, 200));
        var trimmed = new Dictionary<long, LodColumnFingerprint>();
        c.True(scan.ReadFingerprints(trimmed), "fingerprints read after a deletion");
        c.Eq(3, trimmed[second].Rows, "the shortened column counts one layer fewer");
        c.True(LodSavegameScan.Seal(trimmed[second]) != LodSavegameScan.Seal(before[second]),
            "a column that lost a layer fingerprints differently");
    }

    static ulong HashInPieces(byte[] payload, int piece)
    {
        var hash = new LodBlobHash();
        for (int offset = 0; offset < payload.Length; offset += piece)
        {
            int take = Math.Min(piece, payload.Length - offset);
            hash.Add(payload.AsSpan(offset, take));
        }
        return hash.Value;
    }

    /// <summary>The engine's packed chunk position; see <see cref="LodSavegameScan"/>.</summary>
    static long Index(int x, int y, int z, int dimension = 0)
    {
        long value = ((long)x & 0x3FFFFF) | (((long)z & 0x3FFFFF) << 27) | (((long)y & 0x1FF) << 54);
        if (dimension != 0)
        {
            value |= ((long)dimension & 0x1F) << 22;
            value |= ((long)dimension & 0x3E0) << 44;
        }
        return value;
    }

    static void CreateWorld(string path, (long Position, byte Fill)[] chunks, long[] mapChunks)
    {
        using var conn = Open(path);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE gamedata (savegameid INTEGER PRIMARY KEY, data BLOB);
                CREATE TABLE chunk (position INTEGER PRIMARY KEY, data BLOB);
                CREATE TABLE mapchunk (position INTEGER PRIMARY KEY, data BLOB);
                CREATE TABLE mapregion (position INTEGER PRIMARY KEY, data BLOB);";
            cmd.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();
        foreach ((long position, byte fill) in chunks)
        {
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO chunk (position, data) VALUES ($p, $d);";
            insert.Parameters.AddWithValue("$p", position);
            // Long enough to cross the hasher's buffered-word path rather than fitting in one.
            insert.Parameters.AddWithValue("$d", Enumerable.Repeat(fill, 100).ToArray());
            insert.ExecuteNonQuery();
        }
        foreach (long position in mapChunks)
        {
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO mapchunk (position, data) VALUES ($p, x'01');";
            insert.Parameters.AddWithValue("$p", position);
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    static void Write(string path, string sql, long position)
    {
        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$p", position);
        cmd.ExecuteNonQuery();
    }

    static SqliteConnection Open(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ConnectionString);
        conn.Open();
        return conn;
    }
}
