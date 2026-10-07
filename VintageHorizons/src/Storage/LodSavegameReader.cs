using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using VintageHorizons.Net;
using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>Why a savegame read path refused to open, or stopped.</summary>
public enum EnumSavegameReadState
{
    /// <summary>Never opened.</summary>
    Closed,

    /// <summary>Open and usable.</summary>
    Ready,

    /// <summary>Disabled for this session after a named failure. Never retried.</summary>
    Disabled,
}

/// <summary>
/// Reads the world's own savegame a second time, read-only, on a thread of its own.
///
/// The sweep's existence and change questions are answered today by 70,225 paced engine
/// probes that cost 5m35s on the owner's world, because they are served by the chunk
/// thread - the same thread that loads terrain for the player. Reading the file directly
/// is not faster per row (random point lookups measured 13,990/s against a sequential
/// scan's 12,536/s, the same speed); it is faster because it is not competing.
///
/// This deliberately does not use the engine's database object. Reflection onto
/// <c>ChunkServerThread.gameDatabase</c> works and the mod has precedent for it, but the
/// engine's reads are not inside its transaction lock while its writes are, so every read
/// would have to be serialised onto the chunk thread - which is the entire complaint. A
/// private connection removes the competition instead of making it cheaper, and cannot
/// break when a game update renames a private field.
///
/// It is an accelerator and never a prerequisite. Any failure disables it for the session
/// with one named log line, and every caller must still work without it.
/// </summary>
public sealed class LodSavegameReader : IDisposable
{
    /// <summary>
    /// Owning-thread work between yields. The engine's autosave cannot overlap a reader
    /// under a rollback journal, so a batch has to be short enough that a save never waits
    /// noticeably: saves measured about five minutes apart and wrote nine chunks in one
    /// observed case, so tens of milliseconds makes the contention immeasurable.
    /// </summary>
    const double RollbackBatchMs = 10.0;

    /// <summary>Pause between batches under a rollback journal, giving the writer the file.</summary>
    const int RollbackYieldMs = 5;

    /// <summary>
    /// Under WAL a reader and the engine's writer do not exclude each other, so the pacing
    /// relaxes. It does not disappear: this still shares a disk and a page cache.
    /// </summary>
    const double WalBatchMs = 40.0;
    const int WalYieldMs = 1;

    /// <summary>
    /// SQLite's busy timeout. <c>SQLITE_BUSY</c> is expected here rather than exceptional -
    /// the engine holds the file whenever it saves - so every statement waits rather than
    /// failing the feature.
    /// </summary>
    const int BusyTimeoutSeconds = 10;

    /// <summary>
    /// Offset of the four-byte big-endian file change counter in the SQLite header, and of
    /// the version-valid-for number that says whether the counter is current. The counter
    /// increments on every transaction commit and persists in the file, which makes it a
    /// far better "has anything been written?" proof than a size and a modification time.
    /// Measured on the owner's world 2026-09-19: counter 651, valid-for 651.
    /// </summary>
    const int ChangeCounterOffset = 24;
    const int VersionValidForOffset = 92;
    const int HeaderBytes = 100;

    /// <summary>The tables this reader knows how to interpret, and what it needs from them.</summary>
    const string ChunkTable = "chunk";
    const string MapChunkTable = "mapchunk";

    readonly ILogger logger;
    readonly string path;
    readonly object gate = new();

    SqliteConnection? connection;

    public EnumSavegameReadState State { get; private set; } = EnumSavegameReadState.Closed;

    /// <summary>Why the reader disabled itself, for status lines. Empty while healthy.</summary>
    public string DisabledReason { get; private set; } = "";

    /// <summary>
    /// The journal mode the world actually uses, lowercased. Detected, never set: switching
    /// a world to WAL is a permanent change to the player's file that the engine then
    /// inherits, it creates side files, and it behaves badly on network drives.
    /// </summary>
    public string JournalMode { get; private set; } = "";

    public bool IsWal => JournalMode == "wal";

    public bool Available => State == EnumSavegameReadState.Ready;

    double BatchMilliseconds => IsWal ? WalBatchMs : RollbackBatchMs;
    int YieldMilliseconds => IsWal ? WalYieldMs : RollbackYieldMs;

    public LodSavegameReader(ILogger logger, string savegamePath)
    {
        this.logger = logger;
        path = savegamePath;
    }

    /// <summary>
    /// Opens the savegame read-only and proves it is the shape this code understands.
    /// Returns false and disables the reader on any failure; it is never retried, because a
    /// file that cannot be opened once will not become openable by asking again every tick.
    /// </summary>
    public bool Open()
    {
        lock (gate)
        {
            if (State != EnumSavegameReadState.Closed) return Available;

            try
            {
                if (!File.Exists(path))
                {
                    Disable($"the savegame is not at {path}");
                    return false;
                }

                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    // Read-only at the SQLite level, not by convention. This connection is
                    // incapable of writing to the player's world.
                    Mode = SqliteOpenMode.ReadOnly,
                    // The engine owns this file; a pooled handle outliving us would keep a
                    // reader open across a world change.
                    Pooling = false,
                };

                var opened = new SqliteConnection(builder.ConnectionString);
                opened.DefaultTimeout = BusyTimeoutSeconds;
                opened.Open();
                connection = opened;

                JournalMode = ReadJournalMode(opened);
                if (!SchemaLooksRight(opened, out string missing))
                {
                    Disable($"the savegame's {missing}; this reader will not guess at an "
                        + "unexpected schema");
                    return false;
                }

                State = EnumSavegameReadState.Ready;
                logger.Notification(
                    "[Landscapes] Savegame read path open ({0} journal, change counter {1}). "
                    + "Read-only, on its own thread, and never a prerequisite: any failure turns "
                    + "it off for the session and the sweep behaves as it did before.",
                    JournalMode.Length == 0 ? "unknown" : JournalMode,
                    ReadChangeCounter()?.ToString() ?? "unreadable");
                return true;
            }
            catch (Exception e)
            {
                Disable($"opening it failed: {e.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// The savegame's file change counter, or null when the header cannot be read.
    ///
    /// Under a rollback journal this increments on every commit, so an unchanged counter is
    /// proof that nothing was written. Under WAL the main file's header is only updated at
    /// a checkpoint, so a WAL world must also consider its <c>-wal</c> side file; callers
    /// get <see cref="IsWal"/> to decide. Read straight off the file rather than through the
    /// connection, so it works whether or not the connection is healthy.
    /// </summary>
    public uint? ReadChangeCounter()
    {
        try
        {
            // Share everything: the engine must never be blocked by this, and a world being
            // deleted underneath us is the engine's business, not an error of ours.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[HeaderBytes];
            if (stream.Read(header) != HeaderBytes) return null;

            uint counter = BinaryPrimitives.ReadUInt32BigEndian(header[ChangeCounterOffset..]);
            uint validFor = BinaryPrimitives.ReadUInt32BigEndian(header[VersionValidForOffset..]);

            // The two disagree while a writer is mid-transaction, or under WAL between
            // checkpoints. Either way the counter is not currently a statement about the
            // file's contents, and a caller must not treat it as one.
            return counter == validFor ? counter : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Runs one query, handing each row to <paramref name="row"/> on this caller's thread in
    /// short batches with a yield between them, so the engine's own writer is never kept
    /// waiting for long. Returns false if the reader is unavailable or the read failed; a
    /// failure disables the reader.
    ///
    /// The caller supplies <paramref name="shouldStop"/> so a world shutting down does not
    /// have to wait for a whole-world scan to finish.
    /// </summary>
    public bool Read(string sql, Action<SqliteDataReader> row, Func<bool>? shouldStop = null,
        IReadOnlyList<(string Name, long Value)>? parameters = null)
    {
        SqliteConnection? live;
        lock (gate)
        {
            if (!Available) return false;
            live = connection;
        }
        if (live == null) return false;

        try
        {
            using var command = live.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = BusyTimeoutSeconds;
            if (parameters != null)
            {
                foreach ((string name, long value) in parameters)
                {
                    command.Parameters.AddWithValue(name, value);
                }
            }

            using SqliteDataReader reader = command.ExecuteReader();
            var budget = new LodWorkBudget(BatchMilliseconds);
            while (reader.Read())
            {
                if (shouldStop?.Invoke() == true) return false;
                row(reader);

                if (!budget.Expired) continue;
                // Hand the file back before taking another batch. Thread.Sleep rather than
                // a spin: the point is to be absent, not to be ready.
                Thread.Sleep(YieldMilliseconds);
                budget = new LodWorkBudget(BatchMilliseconds);
            }
            return true;
        }
        catch (Exception e)
        {
            Disable($"reading it failed: {e.Message}");
            return false;
        }
    }

    static string ReadJournalMode(SqliteConnection open)
    {
        // With no value this is a query, not an assignment. Assigning one would change the
        // player's world permanently, which this must never do.
        using var command = open.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        object? mode = command.ExecuteScalar();
        return mode?.ToString()?.ToLowerInvariant() ?? "";
    }

    /// <summary>
    /// Confirms the two tables this reader reads exist and are keyed the way it assumes.
    /// A game update that reshapes the savegame must turn this feature off rather than let
    /// it interpret unfamiliar rows as terrain answers.
    /// </summary>
    static bool SchemaLooksRight(SqliteConnection open, out string missing)
    {
        foreach (string table in new[] { ChunkTable, MapChunkTable })
        {
            if (!TableHasPositionAndData(open, table))
            {
                missing = $"{table} table is missing or is not keyed by (position, data)";
                return false;
            }
        }
        missing = "";
        return true;
    }

    static bool TableHasPositionAndData(SqliteConnection open, string table)
    {
        bool position = false, data = false;
        using var command = open.CreateCommand();
        // table_info is a pragma function, so the table name binds as a value rather than
        // being pasted into the statement.
        command.CommandText = "SELECT name FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", table);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string name = reader.GetString(0);
            if (string.Equals(name, "position", StringComparison.OrdinalIgnoreCase)) position = true;
            else if (string.Equals(name, "data", StringComparison.OrdinalIgnoreCase)) data = true;
        }
        return position && data;
    }

    void Disable(string reason)
    {
        State = EnumSavegameReadState.Disabled;
        DisabledReason = reason;
        try { connection?.Dispose(); } catch { /* already gone; nothing to salvage */ }
        connection = null;
        logger.Notification(
            "[Landscapes] Savegame read path off for this session: {0}. This is an "
            + "accelerator, so the sweep continues exactly as it did without it.", reason);
    }

    public void Dispose()
    {
        lock (gate)
        {
            try { connection?.Dispose(); } catch { /* shutting down anyway */ }
            connection = null;
            if (State == EnumSavegameReadState.Ready) State = EnumSavegameReadState.Closed;
        }
    }
}
