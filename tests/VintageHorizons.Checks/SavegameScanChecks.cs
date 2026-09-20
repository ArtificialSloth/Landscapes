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
            FingerprintCoversTheLandscapeOnly(c, Path.Combine(dir, "landscape.vcdbs"));
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

        SingleColumnMatchesTheFullScan(c, scan, trimmed);
    }

    /// <summary>
    /// The audit re-reads one column at a time and compares the answer against a fingerprint
    /// produced by the whole-world scan. If the two ever disagreed for identical content,
    /// every audit would report a mismatch and the mod would reload the entire world on a
    /// loop - so this is the property the audit rests on.
    /// </summary>
    static void SingleColumnMatchesTheFullScan(Check c, LodSavegameScan scan,
        Dictionary<long, LodColumnFingerprint> fromFullScan)
    {
        const int ChunksY = 8;

        c.True(scan.FingerprintColumn(100, 200, ChunksY, out LodColumnFingerprint one),
            "a single column reads through the keyed path");
        c.Eq(LodSavegameScan.Seal(fromFullScan[LodColumnMap.Key(100, 200)]),
            LodSavegameScan.Seal(one),
            "one column read by key fingerprints identically to the whole-world scan");

        c.True(scan.FingerprintColumn(101, 200, ChunksY, out LodColumnFingerprint shortened),
            "the shortened column reads through the keyed path");
        c.Eq(LodSavegameScan.Seal(fromFullScan[LodColumnMap.Key(101, 200)]),
            LodSavegameScan.Seal(shortened),
            "a column that lost a layer also agrees between the two paths");
        c.Eq(3, shortened.Rows, "and its layer count agrees too");

        // A column the savegame does not hold reads as empty rather than failing, so the
        // audit can tell "gone" apart from "could not read".
        c.True(scan.FingerprintColumn(4242, 4242, ChunksY, out LodColumnFingerprint absent),
            "a column with no rows still answers");
        c.Eq(0, absent.Rows, "an absent column holds no layers");
        c.True(LodSavegameScan.Seal(absent) != LodSavegameScan.Seal(one),
            "an emptied column does not fingerprint like a populated one");
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

    /// <summary>
    /// The fingerprint has to move for what the distant view draws and stay still for what it
    /// cannot. Measured on the owner's world, hashing whole rows made 17.8% of flagged columns
    /// reloads for a berry bush's growth timer, a mob walking past or another mod's water
    /// simulation - so each of those is asserted here rather than left to inspection.
    ///
    /// The two failures worth fearing are opposite. Dropping too much is silent: distant
    /// terrain would simply stop being recaptured, which is why the chiselled-block case and
    /// the unknown-field case are here. Dropping too little just costs the work again.
    /// </summary>
    static void FingerprintCoversTheLandscapeOnly(Check c, string path)
    {
        const int Cx = 200, Cz = 300;
        long position = Index(Cx, 0, Cz);
        CreateWorld(path, new[] { (position, (byte)1) }, new[] { position });

        var logger = new CaptureLogger();
        using var reader = new LodSavegameReader(logger, path);
        c.True(reader.Open(), "the landscape fixture opens");

        // The class code a chiselled block carries. Resolved from the block registry in the
        // mod; stated here because a fixture has no registry.
        var scan = new LodSavegameScan(reader, new[] { "MicroBlock" });

        byte[] Row(byte[] blocks, byte[] light, byte[] entities, byte[] bush, byte[] chisel,
            byte[] decors, byte[] fluids, byte[]? extra = null)
        {
            var row = new List<byte>();
            row.AddRange(Field(1, blocks));
            row.AddRange(Field(2, light));
            row.AddRange(Field(6, entities));
            row.AddRange(Field(8, BlockEntity("WildFruitingBush", bush)));
            row.AddRange(Field(8, BlockEntity("MicroBlock", chisel)));
            row.AddRange(Field(9, new byte[] { 9, 9, 9 }));   // another mod's chunk data
            row.AddRange(Field(14, decors));
            row.AddRange(Field(16, fluids));
            row.AddRange(Varint(15, 2));                      // compression version
            if (extra != null) row.AddRange(extra);
            return row.ToArray();
        }

        byte[] one = { 1 }, two = { 2 };
        byte[] baseline = Row(blocks: new byte[] { 10, 11, 12 }, light: one, entities: one,
            bush: one, chisel: new byte[] { 40, 41 }, decors: one, fluids: one);

        ulong Fingerprint(byte[] row)
        {
            WriteBlob(path, position, row);
            var prints = new Dictionary<long, LodColumnFingerprint>();
            if (!scan.ReadFingerprints(prints)) return 0;
            return prints.TryGetValue(LodColumnMap.Key(Cx, Cz), out LodColumnFingerprint print)
                ? LodSavegameScan.Seal(print)
                : 0;
        }

        ulong start = Fingerprint(baseline);
        c.True(start != 0, "the landscape fixture fingerprints");
        c.Eq(start, Fingerprint(baseline), "the same row fingerprints the same way twice");

        // Ignored: nothing here reaches a captured section.
        c.Eq(start, Fingerprint(Row(new byte[] { 10, 11, 12 }, two, one, one,
            new byte[] { 40, 41 }, one, one)), "changed light leaves the fingerprint alone");
        c.Eq(start, Fingerprint(Row(new byte[] { 10, 11, 12 }, one, two, one,
            new byte[] { 40, 41 }, one, one)), "a creature moving leaves the fingerprint alone");
        c.Eq(start, Fingerprint(Row(new byte[] { 10, 11, 12 }, one, one, two,
            new byte[] { 40, 41 }, one, one)),
            "a berry bush's own state leaves the fingerprint alone");
        c.Eq(start, Fingerprint(Row(new byte[] { 10, 11, 12 }, one, one, one,
            new byte[] { 40, 41 }, two, one)), "a decor change leaves the fingerprint alone");

        // Watched: each of these changes what the distance draws.
        c.True(start != Fingerprint(Row(new byte[] { 10, 11, 13 }, one, one, one,
            new byte[] { 40, 41 }, one, one)), "a block change moves the fingerprint");
        c.True(start != Fingerprint(Row(new byte[] { 10, 11, 12 }, one, one, one,
            new byte[] { 40, 41 }, one, two)), "a fluid change moves the fingerprint");
        c.True(start != Fingerprint(Row(new byte[] { 10, 11, 12 }, one, one, one,
            new byte[] { 40, 42 }, one, one)),
            "what a chiselled block is made of moves the fingerprint");
        c.True(start != Fingerprint(Row(new byte[] { 10, 11, 12 }, one, one, one,
            new byte[] { 40, 41 }, one, one, extra: Field(20, one))),
            "a field this rule does not know about moves the fingerprint");

        // The audit re-reads one column by key rather than scanning the world. If the two
        // paths applied the rule differently, every audit would dispute every claim and the
        // mod would recapture the world on a loop - so they are compared on a row that really
        // parses, not only on the unreadable fixture rows above.
        WriteBlob(path, position, baseline);
        var scanned = new Dictionary<long, LodColumnFingerprint>();
        c.True(scan.ReadFingerprints(scanned), "the landscape row reads through the full scan");
        c.True(scan.FingerprintColumn(Cx, Cz, 1, out LodColumnFingerprint keyed),
            "the landscape row reads through the keyed path");
        c.Eq(LodSavegameScan.Seal(scanned[LodColumnMap.Key(Cx, Cz)]), LodSavegameScan.Seal(keyed),
            "the audit's keyed read applies the same rule as the whole-world scan");

        // A chiselled block that was removed must not read as an unchanged column.
        var withoutChisel = new List<byte>();
        withoutChisel.AddRange(Field(1, new byte[] { 10, 11, 12 }));
        withoutChisel.AddRange(Field(8, BlockEntity("WildFruitingBush", one)));
        withoutChisel.AddRange(Field(16, one));
        c.True(start != Fingerprint(withoutChisel.ToArray()),
            "losing a chiselled block moves the fingerprint");

        // Without the registry the rule cannot tell a chisel from a chest, so it keeps every
        // block entity rather than risking the one it needs.
        var everything = new LodSavegameScan(reader, Array.Empty<string>());
        WriteBlob(path, position, baseline);
        var beforeBush = new Dictionary<long, LodColumnFingerprint>();
        c.True(everything.ReadFingerprints(beforeBush), "the conservative scan reads");
        WriteBlob(path, position, Row(new byte[] { 10, 11, 12 }, one, one, two,
            new byte[] { 40, 41 }, one, one));
        var afterBush = new Dictionary<long, LodColumnFingerprint>();
        c.True(everything.ReadFingerprints(afterBush), "the conservative scan reads again");
        c.True(LodSavegameScan.Seal(beforeBush[LodColumnMap.Key(Cx, Cz)])
            != LodSavegameScan.Seal(afterBush[LodColumnMap.Key(Cx, Cz)]),
            "with no known chisel class, every block entity is still watched");

        // A row that is not the shape this rule expects is hashed whole, which is both the
        // old behaviour and the safe direction.
        WriteBlob(path, position, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        var garbage = new Dictionary<long, LodColumnFingerprint>();
        c.True(scan.ReadFingerprints(garbage), "an unparseable row still fingerprints");
        ulong first = LodSavegameScan.Seal(garbage[LodColumnMap.Key(Cx, Cz)]);
        WriteBlob(path, position, new byte[] { 0xFF, 0xFF, 0xFF, 0xFE });
        var garbageAgain = new Dictionary<long, LodColumnFingerprint>();
        c.True(scan.ReadFingerprints(garbageAgain), "the edited unparseable row fingerprints");
        c.True(first != LodSavegameScan.Seal(garbageAgain[LodColumnMap.Key(Cx, Cz)]),
            "an unparseable row that changes is still reported as changed");
    }

    /// <summary>One length-delimited protobuf field, as the engine writes a chunk row's parts.</summary>
    static byte[] Field(int number, byte[] payload)
    {
        var bytes = new List<byte>();
        WriteVarint(bytes, (ulong)((number << 3) | 2));
        WriteVarint(bytes, (ulong)payload.Length);
        bytes.AddRange(payload);
        return bytes.ToArray();
    }

    static byte[] Varint(int number, ulong value)
    {
        var bytes = new List<byte>();
        WriteVarint(bytes, (ulong)(number << 3));
        WriteVarint(bytes, value);
        return bytes.ToArray();
    }

    static void WriteVarint(List<byte> into, ulong value)
    {
        while (value >= 0x80)
        {
            into.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        into.Add((byte)value);
    }

    /// <summary>
    /// A stored block entity: its class code the way <c>BinaryWriter.Write(string)</c> writes
    /// one - a 7-bit encoded length then UTF-8 - followed by its own state.
    /// </summary>
    static byte[] BlockEntity(string className, byte[] state)
    {
        var bytes = new List<byte>();
        byte[] name = System.Text.Encoding.UTF8.GetBytes(className);
        WriteVarint(bytes, (ulong)name.Length);
        bytes.AddRange(name);
        bytes.AddRange(state);
        return bytes.ToArray();
    }

    static void WriteBlob(string path, long position, byte[] data)
    {
        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE chunk SET data = $d WHERE position = $p;";
        cmd.Parameters.AddWithValue("$p", position);
        cmd.Parameters.AddWithValue("$d", data);
        cmd.ExecuteNonQuery();
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
