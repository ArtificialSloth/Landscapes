using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// One row of the server-side cache as the scanner saw it: its section key and SQLite rowid.
///
/// The rowid is the change marker. LodStore writes every row with INSERT OR REPLACE, which
/// deletes the old row and inserts a new one numbered above every row in the table, so a key
/// whose rowid moved has new contents. Reading it needs no blob and no ModifiedMs, which is
/// the whole of the saving: ModifiedMs is stored after the blob, and reaching it walked every
/// row's overflow pages.
/// </summary>
public readonly record struct LodSiblingRow(long Key, long Rowid);

/// <summary>
/// The server side's cache, read by the client side of the same singleplayer world.
///
/// A savegame sweep can only run on the server side, because only it can ask for chunk
/// columns the player is nowhere near. But the server has no texture atlas, so what it
/// captures is geometry with every palette colour left at zero. The client has the atlas
/// and cannot reach the columns. Each half holds exactly what the other is missing.
///
/// So the swept sections travel the same road as sections fetched from a real server: they
/// arrive colourless, get recoloured from their block codes on install, and land in the
/// client's own cache. LodRemoteKeySet does not care whether a blob came off a socket or
/// off the disk beside it, which is why this is a reader and not a subsystem.
///
/// Deliberately NOT a LodStore. That class creates tables and can delete rows whose format
/// version it does not recognise, and pointing it at a file another pipeline has open for
/// writing is a good way to find out what SQLite does about two writers. This only ever
/// reads, and says so in the connection string.
/// </summary>
public sealed class LodLocalOfferSource : IDisposable
{
    /// <summary>A blob read, with the rowid it was read from; Rowid is 0 when there was no row.</summary>
    public readonly record struct BlobResult(long Key, byte[]? Blob, long Rowid);

    readonly ILogger logger;
    readonly string path;
    readonly ConcurrentQueue<LodSiblingRow[]> rowDeltas = new();
    readonly AutoResetEvent scanSignal = new(false);
    readonly Thread scanThread;
    readonly ConcurrentQueue<long> blobRequests = new();
    readonly ConcurrentQueue<BlobResult> blobResults = new();
    readonly AutoResetEvent blobSignal = new(false);
    readonly Thread blobThread;
    readonly object blobRequestGate = new();
    readonly HashSet<long> blobsInFlight = new();
    readonly Dictionary<long, long> asyncBlobRetryNotBefore = new();
    readonly string? testMissMarker =
        Environment.GetEnvironmentVariable("VINTAGEHORIZONS_TEST_LOCAL_OFFER_MISS_MARKER");
    long? testMissKey;
    volatile bool running = true;

    readonly long openedTimestamp = Stopwatch.GetTimestamp();
    long scanBusyTicks;
    int fullScans;
    int incrementalScans;
    long rowsPublished;

    /// <summary>
    /// Cadence of the incremental scan. It asks only for rows numbered above the highest one
    /// already seen, which SQLite answers with a seek on the rowid tree: about 0.1 ms on the
    /// owner's 1.8 GB cache, where the old full read of ModifiedMs took about 800 ms.
    /// </summary>
    const int ScanIntervalMs = 2000;

    /// <summary>
    /// How often the whole key set is read again. The incremental query cannot see a row that
    /// reused a rowid at or below the highest one seen, which SQLite does once the highest row
    /// has been deleted (an unreadable row, a format purge); a falling maximum triggers this
    /// early, and the interval catches the rest. Rowid and key alone are answered from the
    /// primary-key index without touching a blob, about 15 ms on the same cache.
    /// </summary>
    const long FullScanIntervalMs = 60_000;

    /// <summary>
    /// Bound the amount of quadtree registration one game tick receives when the first
    /// scan finds a large existing cache. Keys are fixed-size and cheap, but an initial
    /// cache can still contain tens of thousands of them.
    /// </summary>
    const int KeysPerDelta = 2048;

    /// <summary>A transient row miss should not become a 20 Hz SQLite poll.</summary>
    const int BlobMissRetryMs = 1000;
    const int MaxBlobRequestsInFlight = 16;

    LodLocalOfferSource(string path, ILogger logger)
    {
        this.path = path;
        this.logger = logger;
        scanThread = new Thread(ScanLoop)
        {
            Name = "dodenslandscapes-local-offers",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        blobThread = new Thread(BlobLoop)
        {
            Name = "dodenslandscapes-local-blob-reader",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        scanThread.Start();
        blobThread.Start();
    }

    static SqliteConnection OpenReadOnly(string path)
    {
        // Pooling off is a correctness requirement; see TryOpen and G13.
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        };
        var opened = new SqliteConnection(builder.ToString());
        opened.Open();
        return opened;
    }

    /// <summary>
    /// Opens the server-side cache beside a client cache, or returns null when there is
    /// none - which is the normal case, since only a singleplayer world that has swept has
    /// one. Never throws: this is an optional extra and must not take a join down with it.
    /// </summary>
    public static LodLocalOfferSource? TryOpen(string clientDbPath, ILogger logger)
    {
        string path = Path.ChangeExtension(clientDbPath, null) + "-server.db";
        if (!File.Exists(path)) return null;

        try
        {
            // Read-only, and shared: in singleplayer the server side of this same process
            // has the file open and is very likely still writing to it.
            //
            // Pooling off, and it is not an optimisation choice. Microsoft.Data.Sqlite
            // pools by default, and a pooled connection's Dispose parks the native handle
            // in a process-wide pool instead of closing it. This handle points at the
            // server side's cache, so it outlived leaving the world - and the next load
            // of the same world had the integrated server refused by its own cache file,
            // "it seems to be not writable", every time, on the platform whose file
            // sharing blocks a writer while any handle is open. This connection is opened
            // once per world; pooling bought nothing to begin with.
            using SqliteConnection opened = OpenReadOnly(path);
            return new LodLocalOfferSource(path, logger);
        }
        catch (Exception e)
        {
            logger.Warning("Could not read the server-side LOD cache at {0}: {1}", path, e.Message);
            return null;
        }
    }

    /// <summary>
    /// One immutable batch of rows that are new or rewritten since the previous scan, at every
    /// detail level, or false when the worker has published nothing since the last game tick.
    /// The first batches after opening list the whole cache; the owning thread decides which
    /// keys it already holds. The SQL enumeration never crosses onto the caller's thread.
    /// </summary>
    public bool TryTakeChangedRows(out LodSiblingRow[] rows) => rowDeltas.TryDequeue(out rows!);

    /// <summary>Wake the reader early. Used by the isolated fixture; production polls.</summary>
    internal void RequestDiscovery() => scanSignal.Set();

    /// <summary>Full key scans since opening: the first, the periodic ones, and any forced by deletions.</summary>
    public int FullScans => Volatile.Read(ref fullScans);

    /// <summary>Incremental scans since opening that found at least one new row.</summary>
    public int IncrementalScans => Volatile.Read(ref incrementalScans);

    /// <summary>Rows published to the owning thread since opening.</summary>
    public long RowsPublished => Interlocked.Read(ref rowsPublished);

    /// <summary>
    /// Share of the wall-clock time since opening that the scan worker spent inside SQLite.
    /// Busy time includes waiting on the disk, so this is an upper bound on its CPU use; the
    /// target (D4) is under 2% of one core.
    /// </summary>
    public double ScanBusyShare
    {
        get
        {
            long elapsed = Stopwatch.GetTimestamp() - openedTimestamp;
            return elapsed <= 0 ? 0 : (double)Interlocked.Read(ref scanBusyTicks) / elapsed;
        }
    }

    /// <summary>Queue a visibility-driven blob read without touching SQLite on the game thread.</summary>
    public bool RequestBlob(long key)
    {
        lock (blobRequestGate)
        {
            if (!running || blobsInFlight.Contains(key)
                || blobsInFlight.Count >= MaxBlobRequestsInFlight) return false;
            if (asyncBlobRetryNotBefore.TryGetValue(key, out long notBefore)
                && Environment.TickCount64 < notBefore) return false;
            blobsInFlight.Add(key);
        }
        blobRequests.Enqueue(key);
        blobSignal.Set();
        return true;
    }

    public bool CanRequestBlob
    {
        get
        {
            lock (blobRequestGate) return running
                && blobsInFlight.Count < MaxBlobRequestsInFlight;
        }
    }

    public bool TryTakeBlobResult(out BlobResult result)
    {
        if (!blobResults.TryDequeue(out result)) return false;
        lock (blobRequestGate) blobsInFlight.Remove(result.Key);
        return true;
    }

    public bool TryPeekBlobResult(out BlobResult result) => blobResults.TryPeek(out result);

    void BlobLoop()
    {
        try
        {
            using SqliteConnection blobConn = OpenReadOnly(path);
            while (running)
            {
                if (!blobRequests.TryDequeue(out long key))
                {
                    blobSignal.WaitOne(200);
                    continue;
                }

                byte[]? blob = null;
                long rowid = 0;
                try
                {
                    // Isolated integration-test hook. A real miss is timing-dependent;
                    // the marker forces exactly one on this worker without introducing a
                    // production game-thread read path just for the fixture.
                    if (testMissKey == null && !string.IsNullOrEmpty(testMissMarker))
                    {
                        testMissKey = key;
                        File.WriteAllText(testMissMarker, DescribeKey(key));
                    }
                    else
                    {
                        (blob, rowid) = ReadBlob(blobConn, key);
                    }
                }
                catch (Exception e)
                {
                    try { logger.Warning("Could not read a server-side LOD section: {0}", e.Message); }
                    catch { /* diagnostics must not kill the reader */ }
                }

                lock (blobRequestGate)
                {
                    if (blob == null || blob.Length == 0)
                        asyncBlobRetryNotBefore[key] = Environment.TickCount64 + BlobMissRetryMs;
                    else
                        asyncBlobRetryNotBefore.Remove(key);
                }
                blobResults.Enqueue(new BlobResult(key, blob, rowid));
            }
        }
        catch (Exception e)
        {
            try { logger.Warning("Could not start server-side LOD blob reader: {0}", e.Message); }
            catch { /* diagnostics must not kill shutdown */ }
        }
    }

    void ScanLoop()
    {
        // Key to the rowid last published for it. Only the full scan consults it: it publishes
        // a key again only when that key's rowid differs.
        var known = new Dictionary<long, long>();
        long highestRowid = 0;
        long nextFullScanMs = 0;
        try
        {
            // These commands are created, used and disposed on this thread. Blob reads have
            // a second worker-owned connection; no command or connection state crosses
            // thread ownership.
            using SqliteConnection scanConn = OpenReadOnly(path);
            using SqliteCommand full = scanConn.CreateCommand();
            full.CommandText = "SELECT rowid, Detail, SX, SZ FROM Section";
            using SqliteCommand incremental = scanConn.CreateCommand();
            incremental.CommandText =
                "SELECT rowid, Detail, SX, SZ FROM Section WHERE rowid > @last ORDER BY rowid";
            SqliteParameter last = incremental.Parameters.Add("@last", SqliteType.Integer);
            using SqliteCommand maximum = scanConn.CreateCommand();
            maximum.CommandText = "SELECT max(rowid) FROM Section";

            while (running)
            {
                long started = Stopwatch.GetTimestamp();
                try
                {
                    long now = Environment.TickCount64;
                    long maxRowid = maximum.ExecuteScalar() is long m ? m : 0;

                    // A maximum below what was already seen means rows were deleted, and the
                    // next insert may reuse a rowid the incremental query would skip.
                    if (now >= nextFullScanMs || maxRowid < highestRowid)
                    {
                        List<LodSiblingRow> scanned = ReadRows(full);

                        // Commit to the known set only after the reader reached EOF. If
                        // SQLite throws halfway through, marking that partial prefix known
                        // before publishing it would suppress those keys forever on retry.
                        var next = new Dictionary<long, long>(scanned.Count);
                        List<LodSiblingRow>? changed = null;
                        long top = 0;
                        foreach (LodSiblingRow row in scanned)
                        {
                            next[row.Key] = row.Rowid;
                            if (row.Rowid > top) top = row.Rowid;
                            if (!known.TryGetValue(row.Key, out long previous) || previous != row.Rowid)
                                (changed ??= new List<LodSiblingRow>()).Add(row);
                        }
                        known = next;
                        highestRowid = top;
                        nextFullScanMs = now + FullScanIntervalMs;
                        Interlocked.Increment(ref fullScans);
                        Publish(changed);
                    }
                    else if (maxRowid > highestRowid)
                    {
                        last.Value = highestRowid;
                        List<LodSiblingRow> scanned = ReadRows(incremental);
                        foreach (LodSiblingRow row in scanned)
                        {
                            known[row.Key] = row.Rowid;
                            if (row.Rowid > highestRowid) highestRowid = row.Rowid;
                        }
                        Interlocked.Increment(ref incrementalScans);
                        Publish(scanned);
                    }
                }
                catch (Exception e)
                {
                    // A live writer can make a read fail transiently. Keep the known set and
                    // try again; the owning thread deduplicates anything published twice.
                    try { logger.Warning("Could not list server-side LOD sections: {0}", e.Message); }
                    catch { /* diagnostics must not kill the reader */ }
                }
                finally
                {
                    Interlocked.Add(ref scanBusyTicks, Stopwatch.GetTimestamp() - started);
                }

                scanSignal.WaitOne(ScanIntervalMs);
            }
        }
        catch (Exception e)
        {
            try { logger.Warning("Could not start server-side LOD discovery: {0}", e.Message); }
            catch { /* diagnostics must not kill shutdown */ }
        }
    }

    static List<LodSiblingRow> ReadRows(SqliteCommand command)
    {
        var rows = new List<LodSiblingRow>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new LodSiblingRow(
                LodWorld.SectionKey(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)),
                reader.GetInt64(0)));
        }
        return rows;
    }

    void Publish(List<LodSiblingRow>? rows)
    {
        if (rows == null || rows.Count == 0) return;
        for (int start = 0; start < rows.Count; start += KeysPerDelta)
        {
            int count = Math.Min(KeysPerDelta, rows.Count - start);
            rowDeltas.Enqueue(rows.GetRange(start, count).ToArray());
        }
        Interlocked.Add(ref rowsPublished, rows.Count);
    }

    static (byte[]? Blob, long Rowid) ReadBlob(SqliteConnection connection, long key)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT rowid, Data FROM Section WHERE Detail=@d AND SX=@x AND SZ=@z";
        cmd.Parameters.AddWithValue("@d", LodWorld.KeyLevel(key));
        cmd.Parameters.AddWithValue("@x", LodWorld.KeySx(key));
        cmd.Parameters.AddWithValue("@z", LodWorld.KeySz(key));
        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read()) return (null, 0);
        return (reader.GetValue(1) as byte[], reader.GetInt64(0));
    }

    internal static string DescribeKey(long key) =>
        $"{LodWorld.KeyLevel(key)},{LodWorld.KeySx(key)},{LodWorld.KeySz(key)}";

    public void Dispose()
    {
        running = false;
        scanSignal.Set();
        blobSignal.Set();
        scanThread.Join(15000);
        blobThread.Join(15000);
        scanSignal.Dispose();
        blobSignal.Dispose();
    }
}
