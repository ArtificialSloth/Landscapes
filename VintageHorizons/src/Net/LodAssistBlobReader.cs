using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;

namespace VintageHorizons.Net;

internal readonly record struct LodAssistBlobReadRequest(
    string PlayerUid, long Session, long Key, long RequestedAtMilliseconds);

internal readonly record struct LodAssistBlobReadResult(
    string PlayerUid, long Session, long Key, byte[] Blob, bool ReadFailed,
    long ElapsedTicks, long AllocatedBytes, long RequestedAtMilliseconds, long Version = 0);

/// <summary>
/// A request for a versioned span of level-0 sections (see <see cref="LodSectionSyncLedger"/>).
/// A join names its player; a broadcast does not. <see cref="BaselineOnly"/> asks for the current
/// end and no rows, which establishes where the first broadcast starts.
/// </summary>
internal readonly record struct LodSectionSpanQuery(
    string? PlayerUid, long Session, long From, bool BaselineOnly = false, int Attempt = 1);

/// <summary>
/// The answer: every level-0 section with a version in (From, To], ascending by version. A join
/// asked from past the cache's end - a record newer than the cache, as after restoring an older
/// copy - is answered from <see cref="LodAssistSync.NoRecord"/> instead.
/// </summary>
internal readonly record struct LodSectionSpanResult(
    LodSectionSpanQuery Query, long From, long To, long[] Keys, long[] Versions, bool Failed);

/// <summary>
/// Reads server-assist blobs on one dedicated thread and one read-only SQLite connection.
/// Requests and results are both FIFO, and the outstanding cap covers queued, executing,
/// and completed reads so large blobs cannot accumulate without bound.
/// </summary>
internal sealed class LodAssistBlobReader : IDisposable
{
    // The client protocol retains at most this many unanswered sections. Keeping the
    // server reader to the same total bounds both queued work and completed blob bytes.
    internal const int MaxOutstanding = LodAssist.MaxSectionsInFlight;

    readonly string path;
    readonly ILogger logger;
    readonly bool trackAllocations;
    readonly ConcurrentQueue<LodAssistBlobReadRequest> requests = new();
    readonly ConcurrentQueue<LodAssistBlobReadResult> results = new();

    // Span queries share the reader's connection and thread but not its blob queues or cap: they
    // are one per greeting and one per 30 s, and their order among themselves is what keeps each
    // player's spans contiguous (LodSectionSyncLedger), so they have one FIFO of their own.
    readonly ConcurrentQueue<LodSectionSpanQuery> spanQueries = new();
    readonly ConcurrentQueue<LodSectionSpanResult> spanResults = new();
    bool versioned;
    readonly AutoResetEvent signal = new(false);
    readonly Thread thread;
    volatile bool running = true;
    int outstanding;
    int disposed;
    long resultBytes;
    long activeRequestedAtMilliseconds;

    public LodAssistBlobReader(string path, ILogger logger, bool trackAllocations)
    {
        this.path = path;
        this.logger = logger;
        this.trackAllocations = trackAllocations;
        thread = new Thread(Loop)
        {
            Name = "vintagehorizons-assist-reader",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    public bool TryEnqueue(LodAssistBlobReadRequest request)
    {
        if (!running || !TryReserve()) return false;
        requests.Enqueue(request);
        signal.Set();
        return true;
    }

    bool TryReserve()
    {
        while (true)
        {
            int current = Volatile.Read(ref outstanding);
            if (current >= MaxOutstanding) return false;
            if (Interlocked.CompareExchange(ref outstanding, current + 1, current) == current)
                return true;
        }
    }

    public bool TryPeekResult(out LodAssistBlobReadResult result) => results.TryPeek(out result);

    public void EnqueueSpan(LodSectionSpanQuery query)
    {
        if (!running) return;
        spanQueries.Enqueue(query);
        signal.Set();
    }

    /// <summary>Span results in exactly the order their queries were queued.</summary>
    public bool TryTakeSpanResult(out LodSectionSpanResult result) => spanResults.TryDequeue(out result);

    public bool TryTakeResult(out LodAssistBlobReadResult result)
    {
        if (!results.TryDequeue(out result)) return false;
        Interlocked.Add(ref resultBytes, -result.Blob.LongLength);
        Interlocked.Decrement(ref outstanding);
        return true;
    }

    public int Outstanding => Math.Max(0, Volatile.Read(ref outstanding));
    public int PendingRequests => requests.Count;
    public int PendingResults => results.Count;
    public long PendingResultBytes => Math.Max(0, Interlocked.Read(ref resultBytes));

    public long OldestAgeMilliseconds
    {
        get
        {
            long oldest = 0;
            if (results.TryPeek(out LodAssistBlobReadResult result))
                oldest = result.RequestedAtMilliseconds;
            if (requests.TryPeek(out LodAssistBlobReadRequest request)
                && (oldest == 0 || request.RequestedAtMilliseconds < oldest))
                oldest = request.RequestedAtMilliseconds;
            long active = Interlocked.Read(ref activeRequestedAtMilliseconds);
            if (active > 0 && (oldest == 0 || active < oldest)) oldest = active;
            return oldest == 0 ? 0 : Math.Max(0, Environment.TickCount64 - oldest);
        }
    }

    void Loop()
    {
        SqliteConnection? connection = null;
        SqliteCommand? command = null;
        try
        {
            while (running)
            {
                bool worked = false;
                if (requests.TryDequeue(out LodAssistBlobReadRequest request))
                {
                    worked = true;
                    Interlocked.Exchange(ref activeRequestedAtMilliseconds,
                        request.RequestedAtMilliseconds);
                    ReadOne(request, ref connection, ref command);
                    Interlocked.Exchange(ref activeRequestedAtMilliseconds, 0);
                }

                // Alternating with blob reads, so neither a stream of player requests nor a large
                // re-check span can hold the other up.
                if (spanQueries.TryDequeue(out LodSectionSpanQuery query))
                {
                    worked = true;
                    spanResults.Enqueue(QuerySpan(query, ref connection, ref command));
                }

                if (!worked) signal.WaitOne(200);
            }
        }
        finally
        {
            command?.Dispose();
            connection?.Dispose();
        }
    }

    void ReadOne(LodAssistBlobReadRequest request,
        ref SqliteConnection? connection, ref SqliteCommand? command)
    {
        long started = Stopwatch.GetTimestamp();
        long allocationStart = trackAllocations
            ? GC.GetAllocatedBytesForCurrentThread()
            : -1;
        byte[] blob = Array.Empty<byte>();
        long version = 0;
        bool failed = false;

        try
        {
            SqliteCommand read = EnsureConnection(ref connection, ref command);
            read.Parameters["@detail"].Value = LodWorld.KeyLevel(request.Key);
            read.Parameters["@sx"].Value = LodWorld.KeySx(request.Key);
            read.Parameters["@sz"].Value = LodWorld.KeySz(request.Key);
            using SqliteDataReader row = read.ExecuteReader();
            if (row.Read())
            {
                blob = row.GetValue(0) as byte[] ?? Array.Empty<byte>();
                if (versioned) version = row.GetInt64(1);
            }
        }
        catch (Exception e)
        {
            failed = true;
            int errors = Interlocked.Increment(ref ReadErrors);
            try
            {
                FirstReadError ??= e.ToString();
                if (errors == 1)
                    logger.Warning("Could not read a server-assist LOD section: {0}", e.Message);
            }
            catch
            {
                // Diagnostics must never kill the reader or strand a client request.
            }

            command?.Dispose();
            connection?.Dispose();
            command = null;
            connection = null;
        }

        long elapsed = Stopwatch.GetTimestamp() - started;
        long allocated = allocationStart < 0
            ? -1
            : GC.GetAllocatedBytesForCurrentThread() - allocationStart;
        Interlocked.Add(ref resultBytes, blob.LongLength);
        results.Enqueue(new LodAssistBlobReadResult(
            request.PlayerUid, request.Session, request.Key, blob, failed,
            elapsed, allocated, request.RequestedAtMilliseconds, version));
    }

    /// <summary>
    /// Open the read-only connection and prepare the blob read, noting whether the cache carries
    /// section versions: the server's own store adds them before this reader exists, but a reader
    /// must never assume a column a file might lack.
    /// </summary>
    SqliteCommand EnsureConnection(ref SqliteConnection? connection, ref SqliteCommand? command)
    {
        if (command != null) return command;

        connection = OpenReadOnly(path);
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Section') WHERE name='Version'";
            versioned = (long)(check.ExecuteScalar() ?? 0L) > 0;
        }
        command = connection.CreateCommand();
        command.CommandText = versioned
            ? "SELECT Data, Version FROM Section WHERE Detail=@detail AND SX=@sx AND SZ=@sz"
            : "SELECT Data FROM Section WHERE Detail=@detail AND SX=@sx AND SZ=@sz";
        command.Parameters.Add("@detail", SqliteType.Integer);
        command.Parameters.Add("@sx", SqliteType.Integer);
        command.Parameters.Add("@sz", SqliteType.Integer);
        command.Prepare();
        return command;
    }

    /// <summary>
    /// Answer one span query. The end is read first and the rows are bounded by it: the counter is
    /// committed in the same transaction as the rows it numbers, so every row at or below the end
    /// is visible, and a row written meanwhile carries a higher version and falls in a later span.
    /// </summary>
    LodSectionSpanResult QuerySpan(LodSectionSpanQuery query,
        ref SqliteConnection? connection, ref SqliteCommand? command)
    {
        try
        {
            EnsureConnection(ref connection, ref command);
            if (!versioned)
                return new LodSectionSpanResult(query, query.From, 0, Array.Empty<long>(), Array.Empty<long>(), true);

            long to;
            using (var end = connection!.CreateCommand())
            {
                end.CommandText = "SELECT Value FROM Meta WHERE Key='SectionVersion'";
                long counter = long.TryParse(end.ExecuteScalar() as string,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;
                // Level 0 only, an index seek: spans are level-0, and the counter above already
                // bounds every level. This is a guard against a counter that lags its rows.
                end.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM Section INDEXED BY SectionVersions "
                    + "WHERE Detail = 0";
                to = Math.Max(counter, (long)(end.ExecuteScalar() ?? 0L));
            }

            long from = query.From;
            if (query.PlayerUid != null && from > to) from = LodAssistSync.NoRecord;
            if (query.BaselineOnly || from >= to)
                return new LodSectionSpanResult(query, from, Math.Max(from, to), Array.Empty<long>(), Array.Empty<long>(), false);

            var keys = new List<long>();
            var versions = new List<long>();
            using (var rows = connection.CreateCommand())
            {
                // Named, not left to the planner: without statistics it prefers SectionKeys'
                // equality on Detail and then reads each version from its row, past the blob.
                // SectionVersions answers from the index alone, already in version order.
                rows.CommandText = "SELECT SX, SZ, Version FROM Section INDEXED BY SectionVersions "
                    + "WHERE Detail = 0 AND Version > @from AND Version <= @to ORDER BY Version";
                rows.Parameters.AddWithValue("@from", from);
                rows.Parameters.AddWithValue("@to", to);
                using SqliteDataReader reader = rows.ExecuteReader();
                while (reader.Read())
                {
                    keys.Add(LodWorld.SectionKey(0, reader.GetInt32(0), reader.GetInt32(1)));
                    versions.Add(reader.GetInt64(2));
                }
            }
            return new LodSectionSpanResult(query, from, to, keys.ToArray(), versions.ToArray(), false);
        }
        catch (Exception e)
        {
            int errors = Interlocked.Increment(ref ReadErrors);
            try
            {
                FirstReadError ??= e.ToString();
                if (errors == 1)
                    logger.Warning("Could not read which server-cache sections changed: {0}", e.Message);
            }
            catch
            {
                // Diagnostics must never kill the reader.
            }
            command?.Dispose();
            connection?.Dispose();
            command = null;
            connection = null;
            return new LodSectionSpanResult(query, query.From, 0, Array.Empty<long>(), Array.Empty<long>(), true);
        }
    }

    static SqliteConnection OpenReadOnly(string path)
    {
        var options = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
            // Disposal must close the native handle; a pooled reader can retain the
            // server cache file across world teardown on Windows.
            Pooling = false,
        };
        var connection = new SqliteConnection(options.ToString());
        connection.Open();
        return connection;
    }

    public int ReadErrors;
    public string? FirstReadError;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        running = false;
        signal.Set();
        thread.Join(15000);
        signal.Dispose();
    }
}
