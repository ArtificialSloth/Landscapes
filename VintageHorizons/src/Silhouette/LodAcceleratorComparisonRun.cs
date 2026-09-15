using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace VintageHorizons.Silhouette;

/// <summary>A position where the server holds a real height, captured or previewed.</summary>
internal readonly record struct LodRealHeight(int X, int Z, int Height, EnumSilhouetteOrigin Origin);

/// <summary>A background accelerator run whose lines the main thread reads as they finish.</summary>
internal interface ILodBackgroundReport
{
    /// <summary>Set after the last line is queued.</summary>
    bool Finished { get; }

    bool TryTakeLine(out string line);
}

/// <summary>
/// One run of the accelerator comparison (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER P0.3), asked for
/// with <c>/vhsilhouetteserver compare</c>.
///
/// A background thread asks each installed accelerator for the height of every cell where the
/// server holds a real one, one cell at a time so each call is timed. Where an earlier session's
/// presample holds an answer for a cell whose chunk was not loaded then, that answer is measured
/// too: it is how silhouettes would meet land nobody has generated. Every cell goes into a map file
/// for the side-by-side (D8). The main thread reads the finished lines. Accelerators run other
/// mods' world-generation code, so they never run on the main thread, and a failure ends that
/// accelerator's part of the run with its message.
/// </summary>
internal sealed class LodAcceleratorComparisonRun : ILodBackgroundReport
{
    readonly ConcurrentQueue<string> lines = new();
    volatile bool finished;

    public LodAcceleratorComparisonRun(IReadOnlyList<LodRealHeight> cells,
        IReadOnlyList<ILodHeightAccelerator> accelerators, int seaLevel, string presamplePath, string mapPath)
    {
        var thread = new Thread(() => Run(cells, accelerators, seaLevel, presamplePath, mapPath))
        {
            Name = "vintagehorizons-silhouette-compare",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    /// <summary>Set after the last line is queued.</summary>
    public bool Finished => finished;

    public bool TryTakeLine(out string line) => lines.TryDequeue(out line!);

    void Run(IReadOnlyList<LodRealHeight> cells, IReadOnlyList<ILodHeightAccelerator> accelerators, int seaLevel,
        string presamplePath, string mapPath)
    {
        try
        {
            LodPresample? presample = LodHeightSampleFile.ReadPresample(presamplePath);
            if (presample != null)
                lines.Enqueue($"using the presample taken {presample.TakenUtc} ({presample.Cells.Count} cells)");
            var now = new int?[accelerators.Count][];
            // An accelerator the presample did not ask leaves its entry null.
            var before = presample == null ? null : new int?[]?[accelerators.Count];
            var one = new (int X, int Z)[1];
            for (int a = 0; a < accelerators.Count; a++)
            {
                ILodHeightAccelerator accelerator = accelerators[a];
                now[a] = new int?[cells.Count];
                var compared = new List<LodHeightPoint>(cells.Count);
                var ms = new List<double>(cells.Count);
                string? failure = null;
                for (int i = 0; i < cells.Count; i++)
                {
                    LodRealHeight cell = cells[i];
                    one[0] = (cell.X, cell.Z);
                    long started = Stopwatch.GetTimestamp();
                    int sampled;
                    try
                    {
                        sampled = accelerator.Sample(one)[0];
                    }
                    catch (Exception e)
                    {
                        failure = (e is TargetInvocationException { InnerException: { } inner } ? inner : e).Message;
                        break;
                    }
                    ms.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    now[a][i] = sampled;
                    compared.Add(new LodHeightPoint(cell.X, cell.Z, cell.Height, sampled, cell.Origin));
                }
                string report = LodHeightComparison.Of(compared, seaLevel, ms).Describe(accelerator.Name + ", sampled now");
                lines.Enqueue(failure == null ? report : $"{report}; FAILED after {compared.Count} cells: {failure}");
                if (presample != null) before![a] = CompareEarlier(presample, accelerator.Name, cells, now[a], seaLevel);
            }

            LodHeightSampleFile.WriteMap(mapPath, accelerators.Select(a => a.Name).ToList(), seaLevel,
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                cells.Select((c, i) => new LodMapRow(c.X, c.Z, c.Height, c.Origin,
                    now.Select(n => n[i]).ToArray(), before?.Select(b => b?[i]).ToArray())));
            lines.Enqueue($"map of {cells.Count} cells written to {mapPath}");
        }
        catch (Exception e)
        {
            lines.Enqueue("the comparison itself failed: " + e.Message);
        }
        finally
        {
            finished = true;
        }
    }

    /// <summary>
    /// The presample's answers at these cells for one accelerator, or null when the presample did not
    /// ask it. Reports the cells whose chunk was not loaded when the presample was taken, and how often
    /// the earlier and present answers differ.
    /// </summary>
    int?[]? CompareEarlier(LodPresample presample, string name, IReadOnlyList<LodRealHeight> cells, int?[] now,
        int seaLevel)
    {
        int index = presample.Accelerators.ToList().IndexOf(name);
        if (index < 0)
        {
            lines.Enqueue($"{name}: the presample did not ask this accelerator");
            return null;
        }
        var earlier = new int?[cells.Count];
        var unexplored = new List<LodHeightPoint>();
        var earlierMs = new List<double>();
        int both = 0, differ = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            LodRealHeight cell = cells[i];
            if (!presample.Cells.TryGetValue((cell.X, cell.Z), out LodPresampleCell? p) || p.Heights[index] is not int h)
                continue;
            earlier[i] = h;
            if (!p.Held)
            {
                unexplored.Add(new LodHeightPoint(cell.X, cell.Z, cell.Height, h, cell.Origin));
                earlierMs.Add(p.Ms[index]);
            }
            if (now[i] is int n)
            {
                both++;
                if (n != h) differ++;
            }
        }
        lines.Enqueue(LodHeightComparison.Of(unexplored, seaLevel, earlierMs)
            .Describe(name + ", sampled in an earlier session before this land was loaded")
            + string.Format(CultureInfo.InvariantCulture,
                "; the earlier and present answers differ at {0} of {1} cells", differ, both));
        return earlier;
    }
}
