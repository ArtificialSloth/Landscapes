using Microsoft.Data.Sqlite;
using VintageHorizons;
using VintageHorizons.Net;

namespace VintageHorizons.Checks;

/// <summary>
/// The private savegame read path (terrain-change plan phase A1).
///
/// These build real SQLite files rather than mocking one, because every claim worth making
/// here is a claim about SQLite's behaviour: that the connection cannot write, that the
/// header change counter moves when the file is written and not otherwise, and that an
/// unfamiliar schema is refused instead of guessed at.
/// </summary>
public static class SavegameReaderChecks
{
    public static void Run(Check c)
    {
        string dir = Path.Combine(Path.GetTempPath(),
            "vh-savegame-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            ReadsASavegameShapedFile(c, Path.Combine(dir, "world.vcdbs"));
            TheGameCanSaveBetweenBatches(c, Path.Combine(dir, "batches.vcdbs"));
            RefusesAnUnfamiliarSchema(c, Path.Combine(dir, "strange.vcdbs"));
            RefusesAMissingFile(c, Path.Combine(dir, "absent.vcdbs"));
            TracksTheChangeCounter(c, Path.Combine(dir, "counter.vcdbs"));
            RealSavegameIfOffered(c);
        }
        finally
        {
            // Pooling is off in the reader, but the writer connections here are this
            // method's own; clear the pool so Windows will let the directory go.
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { /* a temp directory; not worth failing a check over */ }
        }
    }

    static void ReadsASavegameShapedFile(Check c, string path)
    {
        CreateSavegame(path, chunkRows: 40, mapChunkRows: 12);

        var logger = new CaptureLogger();
        using var reader = new LodSavegameReader(logger, path);

        c.True(reader.Open(), "a savegame-shaped file opens");
        c.Eq(EnumSavegameReadState.Ready, reader.State, "an opened reader reports itself ready");
        c.Eq("delete", reader.JournalMode,
            "the journal mode is detected from the file, and SQLite's default is a rollback journal");
        c.False(reader.IsWal, "a rollback-journal world is not reported as WAL");

        int rows = 0;
        c.True(reader.Read("SELECT position FROM mapchunk;", _ => rows++),
            "a whole-table read succeeds");
        c.Eq(12, rows, "every map-chunk row is handed to the caller exactly once");

        // The batch pacing must not change what a read returns, only how long it takes.
        // 40 rows against a 10ms budget is at least one yield on any machine slow enough
        // to matter and none on a fast one; the row count is the same either way.
        int chunks = 0;
        c.True(reader.Read("SELECT position, data FROM chunk;", _ => chunks++),
            "a paced read of the larger table succeeds");
        c.Eq(40, chunks, "pacing a read into batches does not drop or repeat rows");

        // Stopping early is the caller's, so a world shutting down never waits for a scan.
        int seen = 0;
        c.False(reader.Read("SELECT position FROM chunk;", _ => seen++, () => seen >= 5),
            "a read that was asked to stop reports that it did not complete");
        c.Eq(5, seen, "the stop request is honoured at the row it was raised on");

        // The read-only claim, proven rather than asserted. This is the whole reason the
        // reader exists as a separate connection: it must be incapable of touching the
        // player's world, not merely uninterested in doing so.
        c.False(reader.Read("DELETE FROM mapchunk;", _ => { }),
            "the connection cannot write to the savegame");
        c.Eq(EnumSavegameReadState.Disabled, reader.State,
            "a failed statement disables the reader rather than leaving it half-usable");
        c.True(logger.Contains("Savegame read path off for this session"),
            "disabling says so once, by name");

        using var check = OpenWritable(path);
        using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM mapchunk;";
        c.Eq(12L, (long)count.ExecuteScalar()!,
            "the rejected write left every row in place");
    }

    /// <summary>
    /// Pre-release review B3. A query holds SQLite's read lock until it closes, sleeps
    /// included, so a whole-table scan as one query kept the game from saving for as long as
    /// it ran - fatal on a server running without a journal (CorruptionProtection off), where
    /// the game's save gives up after 30 seconds. The fixture uses SQLite's default rollback
    /// journal, which locks the same way.
    /// </summary>
    static void TheGameCanSaveBetweenBatches(Check c, string path)
    {
        CreateSavegame(path, chunkRows: 40, mapChunkRows: 12);
        var logger = new CaptureLogger();
        using var reader = new LodSavegameReader(logger, path);
        c.True(reader.Open(), "the batch fixture opens");

        // The old shape, shown failing: a write attempted while one long query is open.
        bool savedDuringOneQuery = true;
        bool tried = false;
        reader.Read("SELECT position, data FROM chunk;", _ =>
        {
            if (tried) return;
            tried = true;
            savedDuringOneQuery = TryGameSave(path, 5000);
        });
        c.False(savedDuringOneQuery, "while one long query is open the game cannot save at all");

        // The new shape: every batch closes its query, and the game saves in the gap.
        var positions = new List<long>();
        int gaps = 0, saves = 0;
        reader.BetweenBatches = () =>
        {
            gaps++;
            if (gaps == 1 && TryGameSave(path, 6000)) saves++;
        };
        c.True(reader.ReadTable("chunk", ", data", row => positions.Add(row.GetInt64(0)),
                maxRowsPerBatch: 7),
            "a batched whole-table read succeeds");
        reader.BetweenBatches = null;
        c.Eq(1, saves, "between batches the game saves without waiting");
        c.True(gaps >= 5, "forty rows in batches of seven close the query at least five times");
        c.SeqEq(Enumerable.Range(0, 40).Select(i => (long)i).Append(6000).ToList(), positions,
            "every row is read exactly once, in order, including one the game saved mid-read");

        int seen = 0;
        c.False(reader.ReadTable("mapchunk", "", _ => seen++, () => seen >= 5, maxRowsPerBatch: 2),
            "a batched read that was asked to stop reports that it did not complete");
        c.Eq(5, seen, "and stops at the row it was asked to");
        c.Throws<ArgumentException>(() => reader.ReadTable("gamedata", "", _ => { }),
            "only the two terrain tables can be read this way");
    }

    /// <summary>One write the way the game makes one, with a one-second patience. True if it committed.</summary>
    static bool TryGameSave(string path, long position)
    {
        try
        {
            using var conn = OpenWritable(path);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 1;
            cmd.CommandText = "INSERT INTO chunk (position, data) VALUES ($p, x'00');";
            cmd.Parameters.AddWithValue("$p", position);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    static void RefusesAnUnfamiliarSchema(Check c, string path)
    {
        using (var conn = OpenWritable(path))
        using (var cmd = conn.CreateCommand())
        {
            // A game update that reshapes the savegame must turn the feature off, not make
            // this code interpret unfamiliar rows as terrain answers.
            cmd.CommandText = @"
                CREATE TABLE chunk (position INTEGER PRIMARY KEY, data BLOB);
                CREATE TABLE mapchunk (id INTEGER PRIMARY KEY, payload BLOB);";
            cmd.ExecuteNonQuery();
        }

        var logger = new CaptureLogger();
        using var reader = new LodSavegameReader(logger, path);
        c.False(reader.Open(), "a savegame whose tables are keyed differently is refused");
        c.Eq(EnumSavegameReadState.Disabled, reader.State, "refusing the schema disables the reader");
        c.True(reader.DisabledReason.Contains("mapchunk"),
            "the reason names the table that did not match");
        c.False(reader.Available, "a refused reader is not available to callers");
    }

    static void RefusesAMissingFile(Check c, string path)
    {
        var logger = new CaptureLogger();
        using var reader = new LodSavegameReader(logger, path);
        c.False(reader.Open(), "a savegame that is not there is refused");
        c.True(reader.DisabledReason.Contains("not at"), "the reason says the file is absent");
        c.False(reader.Read("SELECT 1;", _ => { }),
            "reads through an unavailable reader fail rather than reopening it");
    }

    static void TracksTheChangeCounter(Check c, string path)
    {
        CreateSavegame(path, chunkRows: 4, mapChunkRows: 4);

        var logger = new CaptureLogger();
        using var reader = new LodSavegameReader(logger, path);
        c.True(reader.Open(), "the counter fixture opens");

        uint? first = reader.ReadChangeCounter();
        c.True(first.HasValue, "a settled savegame has a readable change counter");
        c.Eq(first, reader.ReadChangeCounter(),
            "reading the counter twice without a write in between gives the same answer");

        using (var conn = OpenWritable(path))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO mapchunk (position, data) VALUES (9999, x'00');";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        uint? second = reader.ReadChangeCounter();
        c.True(second.HasValue, "the counter is still readable after a commit");
        c.True(second > first,
            "a commit advances the change counter, which is what makes it a proof of change");
    }

    /// <summary>
    /// Opt-in: point `VH_REAL_SAVEGAME` at a real world to run the reader against it.
    ///
    /// A fixture proves the logic; only a real savegame proves the assumptions about the
    /// engine's schema and journal mode. Skipped silently when the variable is unset, so the
    /// suite never depends on a multi-gigabyte file being present. Read-only throughout,
    /// and the game should be closed.
    /// </summary>
    static void RealSavegameIfOffered(Check c)
    {
        string? path = Environment.GetEnvironmentVariable("VH_REAL_SAVEGAME");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var logger = new CaptureLogger();
        using var reader = new LodSavegameReader(logger, path);
        c.True(reader.Open(), "the real savegame opens and matches the expected schema");
        c.True(reader.JournalMode.Length > 0, "the real savegame reports a journal mode");
        c.True(reader.ReadChangeCounter().HasValue,
            "the real savegame has a readable, settled change counter");

        var scan = new LodSavegameScan(reader);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var exists = new LodColumnMap();
        c.True(scan.ReadExistence(exists), "the real savegame's existence map reads");
        double existenceSeconds = clock.Elapsed.TotalSeconds;
        c.True(exists.Count > 0, "the real savegame holds map columns");

        // The number that decides whether a scan-skipping fast path is worth its risk. The
        // plan's fast path was designed when a world start cost 5m35s of paced engine
        // probes; this is what a start costs once that pass is gone.
        clock.Restart();
        var prints = new Dictionary<long, LodColumnFingerprint>(exists.Count);
        c.True(scan.ReadFingerprints(prints), "the real savegame fingerprints every column");
        double fingerprintSeconds = clock.Elapsed.TotalSeconds;
        c.True(prints.Count > 0, "the real savegame produced fingerprints");

        Console.WriteLine($"    real savegame: {reader.JournalMode} journal, change counter "
            + $"{reader.ReadChangeCounter()}, {exists.Count} map columns in "
            + $"{existenceSeconds:0.0}s, {prints.Count} fingerprints in {fingerprintSeconds:0.0}s "
            + $"(whole index {existenceSeconds + fingerprintSeconds:0.0}s)");
    }

    static void CreateSavegame(string path, int chunkRows, int mapChunkRows)
    {
        using var conn = OpenWritable(path);
        using var cmd = conn.CreateCommand();
        // The engine's real shape: position is an INTEGER PRIMARY KEY, which in SQLite *is*
        // the row id, which is why a re-saved chunk reuses its number and why this plan
        // needs a content fingerprint rather than a version.
        cmd.CommandText = @"
            CREATE TABLE gamedata (savegameid INTEGER PRIMARY KEY, data BLOB);
            CREATE TABLE chunk (position INTEGER PRIMARY KEY, data BLOB);
            CREATE TABLE mapchunk (position INTEGER PRIMARY KEY, data BLOB);
            CREATE TABLE mapregion (position INTEGER PRIMARY KEY, data BLOB);";
        cmd.ExecuteNonQuery();

        using var tx = conn.BeginTransaction();
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText =
            "INSERT INTO chunk (position, data) VALUES ($p, $d);";
        var p = insert.CreateParameter();
        p.ParameterName = "$p";
        insert.Parameters.Add(p);
        var d = insert.CreateParameter();
        d.ParameterName = "$d";
        insert.Parameters.Add(d);

        for (int i = 0; i < chunkRows; i++)
        {
            p.Value = i;
            d.Value = new byte[] { (byte)i, 1, 2, 3 };
            insert.ExecuteNonQuery();
        }

        insert.CommandText = "INSERT INTO mapchunk (position, data) VALUES ($p, $d);";
        for (int i = 0; i < mapChunkRows; i++)
        {
            p.Value = i;
            d.Value = new byte[] { (byte)i };
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    static SqliteConnection OpenWritable(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        var conn = new SqliteConnection(builder.ConnectionString);
        conn.Open();
        return conn;
    }
}
