using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace VintageHorizons.Silhouette;

/// <summary>
/// One cell of a presample: whether the server held real heights for its chunk when it was taken,
/// and each accelerator's height (null where that accelerator failed) and time.
/// </summary>
public sealed record LodPresampleCell(bool Held, int?[] Heights, double[] Ms);

/// <summary>Accelerator heights an earlier session took, read back for the comparison (plan P0.3).</summary>
public sealed class LodPresample
{
    public IReadOnlyList<string> Accelerators { get; init; } = Array.Empty<string>();
    public int SeaLevel { get; init; }
    public string TakenUtc { get; init; } = "";
    public Dictionary<(int X, int Z), LodPresampleCell> Cells { get; } = new();
}

/// <summary>One compared cell for the side-by-side map; Before is null without a presample.</summary>
public readonly record struct LodMapRow(int X, int Z, int Real, EnumSilhouetteOrigin Origin, int?[] Now, int?[]? Before);

/// <summary>
/// The accelerator comparison's files (plan P0.3), gzip-compressed CSV so a person can read them:
/// the presample one session takes before the land exists, and the map of every compared cell that
/// scripts/silhouette-compare-render.py draws side by side. Each is written to a temporary file and
/// moved into place, so a reader never sees half of one. Pure, so the fast tier can round-trip it.
/// </summary>
public static class LodHeightSampleFile
{
    const string PresampleMagic = "# vintagehorizons silhouette presample 1";
    const string MapMagic = "# vintagehorizons silhouette map 1";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void WritePresample(string path, LodPresample presample)
    {
        WriteAtomically(path, w =>
        {
            WriteHeader(w, PresampleMagic, presample.Accelerators, presample.SeaLevel, presample.TakenUtc);
            var columns = new StringBuilder("x,z,held");
            for (int a = 0; a < presample.Accelerators.Count; a++) columns.Append(Inv, $",a{a},a{a}ms");
            w.WriteLine(columns);
            var line = new StringBuilder();
            foreach (((int x, int z), LodPresampleCell cell) in presample.Cells)
            {
                line.Clear().Append(Inv, $"{x},{z},{(cell.Held ? 1 : 0)}");
                for (int a = 0; a < presample.Accelerators.Count; a++)
                {
                    line.Append(',').Append(cell.Heights[a]?.ToString(Inv) ?? "");
                    line.Append(',').Append(cell.Heights[a] == null ? "" : cell.Ms[a].ToString("0.####", Inv));
                }
                w.WriteLine(line);
            }
        });
    }

    /// <summary>The presample at <paramref name="path"/>, or null when there is none or it is not one.</summary>
    public static LodPresample? ReadPresample(string path)
    {
        if (!File.Exists(path)) return null;
        using var reader = new StreamReader(new GZipStream(File.OpenRead(path), CompressionMode.Decompress), Encoding.UTF8);
        if (reader.ReadLine() != PresampleMagic) return null;
        var names = new List<string>();
        int sea = 0;
        string utc = "";
        var rows = new List<string>();
        for (string? line = reader.ReadLine(); line != null; line = reader.ReadLine())
        {
            if (line.StartsWith("# accelerators ", StringComparison.Ordinal))
                names.AddRange(line["# accelerators ".Length..].Split('|', StringSplitOptions.RemoveEmptyEntries));
            else if (line.StartsWith("# sealevel ", StringComparison.Ordinal))
                int.TryParse(line["# sealevel ".Length..], NumberStyles.Integer, Inv, out sea);
            else if (line.StartsWith("# utc ", StringComparison.Ordinal))
                utc = line["# utc ".Length..];
            else if (!line.StartsWith('#') && !line.StartsWith("x,", StringComparison.Ordinal))
                rows.Add(line);
        }
        var presample = new LodPresample { Accelerators = names, SeaLevel = sea, TakenUtc = utc };
        foreach (string row in rows)
        {
            string[] f = row.Split(',');
            if (f.Length != 3 + 2 * names.Count
                || !int.TryParse(f[0], NumberStyles.Integer, Inv, out int x)
                || !int.TryParse(f[1], NumberStyles.Integer, Inv, out int z)) continue;
            var heights = new int?[names.Count];
            var ms = new double[names.Count];
            for (int a = 0; a < names.Count; a++)
            {
                if (int.TryParse(f[3 + 2 * a], NumberStyles.Integer, Inv, out int h)) heights[a] = h;
                double.TryParse(f[4 + 2 * a], NumberStyles.Float, Inv, out ms[a]);
            }
            presample.Cells[(x, z)] = new LodPresampleCell(f[2] == "1", heights, ms);
        }
        return presample;
    }

    public static void WriteMap(string path, IReadOnlyList<string> accelerators, int seaLevel, string takenUtc,
        IEnumerable<LodMapRow> rows)
    {
        WriteAtomically(path, w =>
        {
            WriteHeader(w, MapMagic, accelerators, seaLevel, takenUtc);
            var columns = new StringBuilder("x,z,real,origin");
            for (int a = 0; a < accelerators.Count; a++) columns.Append(Inv, $",a{a}now,a{a}before");
            w.WriteLine(columns);
            var line = new StringBuilder();
            foreach (LodMapRow r in rows)
            {
                line.Clear().Append(Inv, $"{r.X},{r.Z},{r.Real},{(r.Origin == EnumSilhouetteOrigin.Captured ? 'C' : 'P')}");
                for (int a = 0; a < accelerators.Count; a++)
                {
                    line.Append(',').Append(r.Now[a]?.ToString(Inv) ?? "");
                    line.Append(',').Append(r.Before?[a]?.ToString(Inv) ?? "");
                }
                w.WriteLine(line);
            }
        });
    }

    static void WriteHeader(StreamWriter w, string magic, IReadOnlyList<string> accelerators, int seaLevel, string utc)
    {
        w.WriteLine(magic);
        w.WriteLine("# accelerators " + string.Join("|", accelerators));
        w.WriteLine("# sealevel " + seaLevel.ToString(Inv));
        w.WriteLine("# utc " + utc);
    }

    static void WriteAtomically(string path, Action<StreamWriter> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + ".tmp";
        using (var writer = new StreamWriter(new GZipStream(File.Create(temporary), CompressionLevel.Optimal),
                   new UTF8Encoding(false)))
        {
            write(writer);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
