using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;

namespace VintageHorizons.Silhouette;

/// <summary>A tile read back from the store. Tile is null when the store holds none, or it did not decode.</summary>
public readonly record struct LodSilhouetteStoredTile(int TileX, int TileZ, LodSilhouetteTile? Tile);

/// <summary>
/// Silhouette tiles on disk (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER 4.3 and P5), one row per tile,
/// sharing nothing with the section cache. The server uses
/// <c>ModData/dodenslandscapes/&lt;savegame&gt;-silhouette-server.db</c>; the client uses the same
/// schema in <c>&lt;savegame&gt;-silhouette.db</c>.
///
/// One dedicated thread owns the only connection (G9), unpooled so a world reloaded in the same
/// process can open the file again (G13). It publishes the stored tile keys once, answers every
/// load (a tile, or null for none), and writes saves in batches. Server saves merge into the row
/// already stored, so a tile rebuilt without reading its stored copy first can only add to what
/// the disk holds. Client saves replace exactly: the connected server remains authoritative even
/// if its store was rebuilt and its revision sequence restarted.
///
/// If the file cannot be opened, the store says so once and carries on answering: loads find
/// nothing and saves are counted as lost. Silhouettes then work for the session only.
/// </summary>
public sealed class LodSilhouetteStore : IDisposable
{
    /// <summary>Bump when the meaning of a stored row changes; old rows are then discarded.</summary>
    const string SchemaVersion = "1";

    public const int MaxLoadsOutstanding = 8;
    const int ShutdownWaitMs = 5_000;

    readonly string path;
    readonly ILogger logger;
    readonly bool mergeStoredRows;
    readonly ConcurrentQueue<(int TileX, int TileZ)> loads = new();
    readonly ConcurrentQueue<LodSilhouetteStoredTile> loaded = new();
    readonly object saveGate = new();
    readonly Dictionary<long, LodSilhouetteTile> pendingSaves = new();
    readonly Queue<long> saveOrder = new();
    readonly AutoResetEvent signal = new(false);
    readonly Thread thread;
    volatile bool running = true;
    HashSet<long>? publishedIndex;
    int loadsOutstanding;
    int disposed;

    public int Saved, SaveErrors, ReadErrors, Undecodable, StoredTiles;
    public volatile bool OpenFailed;

    public LodSilhouetteStore(string path, ILogger logger, bool mergeStoredRows = true)
    {
        this.path = path;
        this.logger = logger;
        this.mergeStoredRows = mergeStoredRows;
        thread = new Thread(Loop)
        {
            Name = "dodenslandscapes-silhouette-store",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    public string Path => path;

    /// <summary>Main thread. The keys stored before this session, once, when the thread has read them.</summary>
    public bool TryTakeIndex(out HashSet<long> keys)
    {
        keys = Interlocked.Exchange(ref publishedIndex, null)!;
        return keys != null;
    }

    /// <summary>False while <see cref="MaxLoadsOutstanding"/> loads are unanswered, or after shutdown.</summary>
    public bool TryRequestLoad(int tileX, int tileZ)
    {
        if (!running) return false;
        if (Interlocked.Increment(ref loadsOutstanding) > MaxLoadsOutstanding)
        {
            Interlocked.Decrement(ref loadsOutstanding);
            return false;
        }
        loads.Enqueue((tileX, tileZ));
        signal.Set();
        return true;
    }

    public bool TryTakeLoaded(out LodSilhouetteStoredTile result)
    {
        if (!loaded.TryDequeue(out result)) return false;
        Interlocked.Decrement(ref loadsOutstanding);
        return true;
    }

    /// <summary>
    /// Queue a tile for writing. The store owns <paramref name="snapshot"/> from here, so pass a
    /// copy (<see cref="LodSilhouetteTile.Snapshot"/>). A newer copy of the same tile replaces
    /// one still waiting. False after shutdown began.
    /// </summary>
    public bool Save(LodSilhouetteTile snapshot)
        => QueueSave(snapshot);

    /// <summary>
    /// Queue a client tile that is already immutable. This avoids copying up to 48 KiB on the
    /// game thread for every received tile. Server builders must continue to pass snapshots.
    /// </summary>
    public bool SaveImmutable(LodSilhouetteTile tile)
        => QueueSave(tile);

    bool QueueSave(LodSilhouetteTile snapshot)
    {
        lock (saveGate)
        {
            if (!running) return false;
            if (!pendingSaves.ContainsKey(snapshot.Key)) saveOrder.Enqueue(snapshot.Key);
            pendingSaves[snapshot.Key] = snapshot;
        }
        signal.Set();
        return true;
    }

    public int PendingSaves
    {
        get
        {
            lock (saveGate) return pendingSaves.Count;
        }
    }

    /// <summary>The database file's size on disk, for status lines. Zero when it cannot be read.</summary>
    public long FileBytes
    {
        get
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch (IOException)
            {
                return 0;
            }
        }
    }

    void Loop()
    {
        SqliteConnection? connection = null;
        try
        {
            connection = OpenConnection();
            HashSet<long> keys = ReadIndex(connection);
            Interlocked.Exchange(ref StoredTiles, keys.Count);
            Volatile.Write(ref publishedIndex, keys);
        }
        catch (Exception e)
        {
            OpenFailed = true;
            connection?.Dispose();
            connection = null;
            Volatile.Write(ref publishedIndex, new HashSet<long>());
            try
            {
                logger.Error("Horizon silhouettes: could not open the tile store {0}, so silhouettes "
                    + "work for this session only and nothing is kept: {1}", path, e.Message);
            }
            catch
            {
                // Diagnostics must never kill the store thread.
            }
        }

        try
        {
            while (true)
            {
                bool stopping = !running;
                bool worked = ServeLoads(connection);
                worked |= WriteSaves(connection);
                if (stopping && !worked) break;
                if (!worked) signal.WaitOne(200);
            }
        }
        finally
        {
            connection?.Dispose();
        }
    }

    SqliteConnection OpenConnection()
    {
        var options = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Disposal must close the native handle: a pooled one outlives the world on
            // Windows, and the next load of the same world could not open the file (G13).
            Pooling = false,
        };
        var connection = new SqliteConnection(options.ToString());
        connection.Open();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Meta (Key TEXT PRIMARY KEY, Value TEXT);
                CREATE TABLE IF NOT EXISTS Tile (
                    TX INTEGER NOT NULL,
                    TZ INTEGER NOT NULL,
                    Revision INTEGER NOT NULL,
                    Data BLOB NOT NULL,
                    ModifiedMs INTEGER NOT NULL,
                    PRIMARY KEY (TX, TZ)
                );";
            command.ExecuteNonQuery();
        }
        PurgeOutdated(connection);
        return connection;
    }

    void PurgeOutdated(SqliteConnection connection)
    {
        string? existing;
        using (SqliteCommand check = connection.CreateCommand())
        {
            check.CommandText = "SELECT Value FROM Meta WHERE Key = 'SchemaVersion'";
            existing = check.ExecuteScalar() as string;
        }
        if (existing == SchemaVersion) return;

        using SqliteCommand command = connection.CreateCommand();
        if (existing != null)
        {
            // Said only when a store really held another format, and with a count: deliberate
            // destruction is fine, silent destruction is not.
            command.CommandText = "SELECT COUNT(*) FROM Tile";
            long discarded = (long)(command.ExecuteScalar() ?? 0L);
            logger.Notification("Horizon silhouettes: tile store format {0} is not ours ({1}); "
                + "discarding {2} stored tiles. They rebuild as the server loads and previews terrain.",
                existing, SchemaVersion, discarded);
        }
        command.CommandText = "DELETE FROM Tile; "
            + "INSERT OR REPLACE INTO Meta (Key, Value) VALUES ('SchemaVersion', $version);";
        command.Parameters.AddWithValue("$version", SchemaVersion);
        command.ExecuteNonQuery();
    }

    static HashSet<long> ReadIndex(SqliteConnection connection)
    {
        var keys = new HashSet<long>();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT TX, TZ FROM Tile";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) keys.Add(LodSilhouetteTile.KeyOf(reader.GetInt32(0), reader.GetInt32(1)));
        return keys;
    }

    /// <summary>Every request gets an answer, even with no file: a null tile.</summary>
    bool ServeLoads(SqliteConnection? connection)
    {
        bool worked = false;
        while (loads.TryDequeue(out (int TileX, int TileZ) request))
        {
            worked = true;
            LodSilhouetteTile? tile = null;
            if (connection != null)
            {
                try
                {
                    tile = Read(connection, null, request.TileX, request.TileZ);
                }
                catch (Exception e)
                {
                    if (Interlocked.Increment(ref ReadErrors) == 1)
                        TryWarn("could not read a stored tile", e);
                }
            }
            loaded.Enqueue(new LodSilhouetteStoredTile(request.TileX, request.TileZ, tile));
        }
        return worked;
    }

    LodSilhouetteTile? Read(SqliteConnection connection, SqliteTransaction? transaction, int tileX, int tileZ)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Revision, Data FROM Tile WHERE TX = $tx AND TZ = $tz";
        command.Parameters.AddWithValue("$tx", tileX);
        command.Parameters.AddWithValue("$tz", tileZ);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        LodSilhouetteTile? tile = LodSilhouetteTile.Decode(tileX, tileZ, reader.GetInt32(0),
            reader.GetFieldValue<byte[]>(1));
        if (tile == null) Interlocked.Increment(ref Undecodable);
        return tile;
    }

    /// <summary>
    /// Write everything waiting in one transaction. A server tile first takes the cells its
    /// stored row has and it lacks (<see cref="LodSilhouetteTile.MergeFrom"/>). A client tile is
    /// an exact replacement from the authoritative server.
    /// </summary>
    bool WriteSaves(SqliteConnection? connection)
    {
        var batch = new List<LodSilhouetteTile>();
        lock (saveGate)
        {
            while (saveOrder.Count > 0)
            {
                long key = saveOrder.Dequeue();
                if (pendingSaves.Remove(key, out LodSilhouetteTile? tile)) batch.Add(tile);
            }
        }
        if (batch.Count == 0) return false;
        if (connection == null)
        {
            Interlocked.Add(ref SaveErrors, batch.Count);
            return true;
        }

        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using SqliteTransaction transaction = connection.BeginTransaction();
            int added = 0;
            foreach (LodSilhouetteTile snapshot in batch)
            {
                LodSilhouetteTile? stored = Read(connection, transaction, snapshot.TileX, snapshot.TileZ);
                if (stored != null)
                {
                    if (mergeStoredRows) snapshot.MergeFrom(stored);
                }
                else added++;
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT OR REPLACE INTO Tile (TX, TZ, Revision, Data, ModifiedMs) "
                    + "VALUES ($tx, $tz, $revision, $data, $ms)";
                command.Parameters.AddWithValue("$tx", snapshot.TileX);
                command.Parameters.AddWithValue("$tz", snapshot.TileZ);
                command.Parameters.AddWithValue("$revision", snapshot.Revision);
                command.Parameters.AddWithValue("$data", snapshot.Encode());
                command.Parameters.AddWithValue("$ms", now);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
            Interlocked.Add(ref Saved, batch.Count);
            Interlocked.Add(ref StoredTiles, added);
        }
        catch (Exception e)
        {
            if (Interlocked.Add(ref SaveErrors, batch.Count) == batch.Count)
                TryWarn("could not write tiles; they stay in memory for this session", e);
        }
        return true;
    }

    void TryWarn(string what, Exception e)
    {
        try
        {
            logger.Warning("Horizon silhouettes: the tile store {0}: {1}", what, e.Message);
        }
        catch
        {
            // Diagnostics must never kill the store thread.
        }
    }

    /// <summary>
    /// Stop taking work, write what is waiting, and close. Waits a bounded time; if the thread is
    /// still writing after that, it says how much may be lost. Silhouette tiles rebuild from
    /// terrain, so a lost save costs rework, never correctness.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        running = false;
        signal.Set();
        if (thread.Join(ShutdownWaitMs))
        {
            signal.Dispose();
            return;
        }
        logger.Warning("Horizon silhouettes: the tile store was still writing {0} s into shutdown; up "
            + "to {1} tiles may not be saved. They rebuild as terrain loads.", ShutdownWaitMs / 1000,
            PendingSaves);
    }
}
