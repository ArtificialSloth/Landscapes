using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace VintageHorizons.Checks;

/// <summary>
/// A client cache against its server-side sibling, measured with no game process.
///
/// WHY THIS EXISTS. P3 of the world-generation plan claims that once the client merges the
/// server half's rows, the client rows behind the server fall to near zero. That is a claim
/// about two databases, so it is measured on the two databases: for every key both hold, how
/// many columns the server's row has captured that the client's row has not. Run it on a
/// snapshot taken before the fix and on one taken after the same ground has been revisited.
///
/// It also times the offer scanner's queries on the server file (D4) - the old full read that
/// reached ModifiedMs through every row's overflow pages, the rowid-and-key read, and the
/// incremental read - and then runs the shipping LodLocalOfferSource over it for a while and
/// reports how much of that time its scan thread was busy.
///
/// Usage: siblingfield CLIENT.db [SERVER.db] [--scan-seconds N]. SERVER.db defaults to the
/// sibling name the mod uses. Both are opened read-only. Asserts nothing.
/// </summary>
public static class SiblingField
{
    const int Total = LodSection.GridSize * LodSection.GridSize;

    public static int Run(string[] args)
    {
        string? clientPath = null, serverPath = null;
        int scanSeconds = 10;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.Equals(arg, "siblingfield", StringComparison.OrdinalIgnoreCase)) continue;
            if (arg == "--scan-seconds" && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], out scanSeconds)) scanSeconds = 10;
                continue;
            }
            if (arg.StartsWith('-')) continue;
            if (clientPath == null) clientPath = arg;
            else serverPath ??= arg;
        }

        if (clientPath == null)
        {
            Console.WriteLine("  usage: siblingfield CLIENT.db [SERVER.db] [--scan-seconds N]");
            return 2;
        }
        serverPath ??= Path.ChangeExtension(clientPath, null) + "-server.db";
        if (!File.Exists(clientPath) || !File.Exists(serverPath))
        {
            Console.WriteLine($"  missing: {(File.Exists(clientPath) ? serverPath : clientPath)}");
            return 2;
        }

        Console.WriteLine();
        Console.WriteLine($"  client cache: {clientPath}");
        Console.WriteLine($"  server cache: {serverPath}");
        Console.WriteLine();
        CompareRows(clientPath, serverPath);
        Console.WriteLine();
        TimeQueries(serverPath);
        Console.WriteLine();
        RunScanner(clientPath, scanSeconds);
        Console.WriteLine();
        return 0;
    }

    static SqliteConnection OpenReadOnly(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    static Dictionary<long, long> Keys(SqliteConnection connection)
    {
        var keys = new Dictionary<long, long>();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT rowid, Detail, SX, SZ FROM Section";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            keys[LodWorld.SectionKey(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3))] =
                reader.GetInt64(0);
        }
        return keys;
    }

    static byte[]? Blob(SqliteConnection connection, long key)
    {
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Data FROM Section WHERE Detail=@d AND SX=@x AND SZ=@z";
        cmd.Parameters.AddWithValue("@d", LodWorld.KeyLevel(key));
        cmd.Parameters.AddWithValue("@x", LodWorld.KeySx(key));
        cmd.Parameters.AddWithValue("@z", LodWorld.KeySz(key));
        return cmd.ExecuteScalar() as byte[];
    }

    static void CompareRows(string clientPath, string serverPath)
    {
        using SqliteConnection client = OpenReadOnly(clientPath);
        using SqliteConnection server = OpenReadOnly(serverPath);
        Dictionary<long, long> clientKeys = Keys(client);
        Dictionary<long, long> serverKeys = Keys(server);
        var store = new LodStore(null!);

        const int levels = LodWorld.MaxLevel + 1;
        var serverRows = new int[levels];
        var clientRows = new int[levels];
        var shared = new int[levels];
        var behind = new int[levels];
        var ahead = new int[levels];
        var missingColumns = new long[levels];
        var unreadable = new int[levels];

        foreach (long key in clientKeys.Keys) clientRows[LodWorld.KeyLevel(key)]++;
        foreach (long key in serverKeys.Keys)
        {
            int level = LodWorld.KeyLevel(key);
            serverRows[level]++;
            if (!clientKeys.ContainsKey(key)) continue;
            shared[level]++;

            byte[]? mineBlob = Blob(client, key);
            byte[]? theirsBlob = Blob(server, key);
            LodSection? mine = mineBlob == null ? null : store.DeserializeForeign(mineBlob, null);
            LodSection? theirs = theirsBlob == null ? null : store.DeserializeForeign(theirsBlob, null);
            if (mine == null || theirs == null)
            {
                unreadable[level]++;
                continue;
            }

            int lacking = 0, extra = 0;
            for (int col = 0; col < Total; col++)
            {
                if (theirs.Captured[col] && !mine.Captured[col]) lacking++;
                if (mine.Captured[col] && !theirs.Captured[col]) extra++;
            }
            if (lacking > 0)
            {
                behind[level]++;
                missingColumns[level] += lacking;
            }
            if (extra > 0) ahead[level]++;
        }

        Console.WriteLine("  Rows by level. \"Behind\" is a key both caches hold where the server's row has");
        Console.WriteLine("  captured columns the client's row lacks; \"ahead\" is the reverse.");
        Console.WriteLine();
        Console.WriteLine("  level   server   client   shared   behind   columns lacking   ahead   unreadable");
        for (int level = 0; level < levels; level++)
        {
            Console.WriteLine($"  L{level}    {serverRows[level],7}  {clientRows[level],7}  {shared[level],7}  "
                + $"{behind[level],7}  {missingColumns[level],16}  {ahead[level],6}  {unreadable[level],10}");
        }
        Console.WriteLine($"  all   {serverRows.Sum(),7}  {clientRows.Sum(),7}  {shared.Sum(),7}  "
            + $"{behind.Sum(),7}  {missingColumns.Sum(),16}  {ahead.Sum(),6}  {unreadable.Sum(),10}");

        Console.WriteLine();
        Console.WriteLine("  merge records in the client cache: " + DescribeMergeRecords(client));
    }

    static string DescribeMergeRecords(SqliteConnection client)
    {
        using SqliteCommand exists = client.CreateCommand();
        exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='SiblingMerge'";
        if ((long)(exists.ExecuteScalar() ?? 0L) == 0) return "none (the table predates P3)";
        using SqliteCommand count = client.CreateCommand();
        count.CommandText = "SELECT count(*) FROM SiblingMerge";
        return ((long)(count.ExecuteScalar() ?? 0L)).ToString();
    }

    static void TimeQueries(string serverPath)
    {
        using SqliteConnection connection = OpenReadOnly(serverPath);
        using SqliteCommand maximum = connection.CreateCommand();
        maximum.CommandText = "SELECT max(rowid) FROM Section";
        long max = maximum.ExecuteScalar() is long m ? m : 0;

        Console.WriteLine("  Offer scan queries on the server cache: median of three, process CPU and wall time.");
        Time(connection, "old full read, reaching ModifiedMs", "SELECT Detail, SX, SZ, ModifiedMs FROM Section");
        Time(connection, "new full read, rowid and key", "SELECT rowid, Detail, SX, SZ FROM Section");
        Time(connection, "incremental, last 300 rowids",
            $"SELECT rowid, Detail, SX, SZ FROM Section WHERE rowid > {max - 300} ORDER BY rowid");
        Time(connection, "incremental, nothing new",
            $"SELECT rowid, Detail, SX, SZ FROM Section WHERE rowid > {max} ORDER BY rowid");
    }

    static void Time(SqliteConnection connection, string label, string sql)
    {
        var cpu = new List<double>();
        var wall = new List<double>();
        int rows = 0;
        for (int run = 0; run < 3; run++)
        {
            TimeSpan cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            var clock = Stopwatch.StartNew();
            rows = 0;
            using (SqliteCommand cmd = connection.CreateCommand())
            {
                cmd.CommandText = sql;
                using SqliteDataReader reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    for (int i = 0; i < reader.FieldCount; i++) reader.GetInt64(i);
                    rows++;
                }
            }
            wall.Add(clock.Elapsed.TotalMilliseconds);
            cpu.Add((Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds);
        }
        cpu.Sort();
        wall.Sort();
        Console.WriteLine($"    {label,-36} rows {rows,7}   cpu {cpu[1],8:0.0} ms   wall {wall[1],8:0.0} ms");
    }

    static void RunScanner(string clientPath, int seconds)
    {
        using LodLocalOfferSource? offers = LodLocalOfferSource.TryOpen(clientPath, new CaptureLogger());
        if (offers == null)
        {
            Console.WriteLine("  shipping scanner: no server-side cache beside the client path");
            return;
        }

        var clock = Stopwatch.StartNew();
        long rows = 0;
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            while (offers.TryTakeChangedRows(out LodSiblingRow[] batch)) rows += batch.Length;
            Thread.Sleep(50);
        }
        while (offers.TryTakeChangedRows(out LodSiblingRow[] rest)) rows += rest.Length;
        Console.WriteLine($"  shipping scanner over {seconds} s: {offers.FullScans} full and "
            + $"{offers.IncrementalScans} incremental scans, {rows} rows published, busy "
            + $"{offers.ScanBusyShare * 100:0.000}% of one thread (wall time in SQLite, an upper bound on CPU)");
    }
}
