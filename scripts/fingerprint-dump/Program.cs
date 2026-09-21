using System.Diagnostics;
using VintageHorizons;
using VintageHorizons.Checks;
using VintageHorizons.Net;

namespace VintageHorizons.Tools;

/// <summary>
/// Writes the mod's own column fingerprints for a savegame, one column per line, using
/// <see cref="LodSavegameScan"/> itself rather than a copy of its rule.
///
/// What it is for: comparing two worlds' fingerprints to see what a rule change is worth,
/// and comparing a world against a cache's stored claims (scripts/claim-check.py) to measure
/// what the background audit would dispute. Session 105 used it to confirm on the owner's
/// real 3.2 GB world that the landscape-only rule turns 13,754 flagged columns into 11,307.
///
///   dotnet run --project scripts/fingerprint-dump -- WORLD.vcdbs out.tsv [--all-block-entities]
///
/// Output is `cx TAB cz TAB rows TAB sealed-fingerprint`, with the fingerprint unsigned;
/// the cache stores the same value as a signed 64-bit integer.
///
/// The read path opens the file read-only and cannot write to it, so this is safe against a
/// world the game is holding - though a world mid-session is not a settled snapshot, and any
/// column changed since its last save will read differently from what the cache claims.
/// </summary>
public static class Program
{
    /// <summary>
    /// The block-entity classes whose contents a capture reads. The mod resolves these from
    /// the block registry at world start; a bare tool has no registry, so vanilla's two are
    /// named here. A world whose chiselled blocks come from a mod with its own microblock
    /// class needs that class added, or its columns will fingerprint differently here than
    /// they do in game.
    /// </summary>
    static readonly string[] CaptureBlockEntities = { "MicroBlock", "Chisel" };

    public static int Main(string[] args)
    {
        string[] positional = args.Where(a => !a.StartsWith("--")).ToArray();
        if (positional.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: fingerprint-dump <world.vcdbs> <out.tsv> [--all-block-entities]");
            return 2;
        }

        // Outside the game process nothing has initialised the SQLite provider.
        SQLitePCL.Batteries_V2.Init();

        string[] captureClasses = args.Contains("--all-block-entities")
            ? Array.Empty<string>()
            : CaptureBlockEntities;

        var logger = new CaptureLogger();
        using var reader = new LodSavegameReader(logger, positional[0]);
        if (!reader.Open())
        {
            foreach (string line in logger.Lines) Console.Error.WriteLine(line);
            return 1;
        }

        var scan = new LodSavegameScan(reader, captureClasses);
        var clock = Stopwatch.StartNew();
        var prints = new Dictionary<long, LodColumnFingerprint>();
        if (!scan.ReadFingerprints(prints))
        {
            foreach (string line in logger.Lines) Console.Error.WriteLine(line);
            return 1;
        }

        using (var writer = new StreamWriter(positional[1]))
        {
            foreach ((long key, LodColumnFingerprint print) in prints)
            {
                writer.WriteLine("{0}\t{1}\t{2}\t{3}", LodColumnMap.KeyCx(key),
                    LodColumnMap.KeyCz(key), print.Rows, LodSavegameScan.Seal(print));
            }
        }

        Console.WriteLine("{0} columns in {1:0.0}s ({2} journal); block entities kept: {3}",
            prints.Count, clock.Elapsed.TotalSeconds,
            reader.JournalMode.Length == 0 ? "unknown" : reader.JournalMode,
            captureClasses.Length == 0 ? "all" : string.Join("+", captureClasses));
        return 0;
    }
}
