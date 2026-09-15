using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace VintageHorizons.Silhouette;

/// <summary>
/// One presample (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER P0.3), asked for with
/// <c>/vhsilhouetteserver presample</c>. A background thread asks every accelerator for the height
/// of every cell of a chunk square, one call per cell so each is timed, and writes the answers to a
/// file that a later session compares with the real terrain once it has been generated.
///
/// A later session, because a generator in this process could reuse the samplers' cached region
/// data and agree with them for the wrong reason; silhouettes are built well before the land is
/// generated, usually in another session. Accelerators are asked one after another, so the first
/// pays for building region data and later ones may find it built.
/// </summary>
internal sealed class LodAcceleratorPresampleRun : ILodBackgroundReport
{
    readonly ConcurrentQueue<string> lines = new();
    volatile bool finished;

    public LodAcceleratorPresampleRun(string path, IReadOnlyList<(int X, int Z)> cells, HashSet<long> heldChunks,
        IReadOnlyList<ILodHeightAccelerator> accelerators, int seaLevel)
    {
        var thread = new Thread(() => Run(path, cells, heldChunks, accelerators, seaLevel))
        {
            Name = "vintagehorizons-silhouette-presample",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    /// <summary>Set after the last line is queued.</summary>
    public bool Finished => finished;

    public bool TryTakeLine(out string line) => lines.TryDequeue(out line!);

    /// <summary>A chunk's key in the held-chunk set.</summary>
    public static long ChunkKey(int cx, int cz) => ((long)cx << 32) | (uint)cz;

    /// <summary>
    /// The corner block of every cell in the square of (2r+1) chunks a side centred on a chunk, eight
    /// by eight cells a chunk: the positions where tiles hold heights.
    /// </summary>
    public static List<(int X, int Z)> SquareCells(int centreCx, int centreCz, int radiusChunks)
    {
        var cells = new List<(int X, int Z)>();
        for (int cz = centreCz - radiusChunks; cz <= centreCz + radiusChunks; cz++)
            for (int cx = centreCx - radiusChunks; cx <= centreCx + radiusChunks; cx++)
                for (int j = 0; j < LodSilhouetteTile.CellsPerChunk; j++)
                    for (int i = 0; i < LodSilhouetteTile.CellsPerChunk; i++)
                        cells.Add((cx * LodSilhouetteTile.ChunkBlocks + i * LodSilhouetteTile.CellBlocks,
                            cz * LodSilhouetteTile.ChunkBlocks + j * LodSilhouetteTile.CellBlocks));
        return cells;
    }

    void Run(string path, IReadOnlyList<(int X, int Z)> cells, HashSet<long> heldChunks,
        IReadOnlyList<ILodHeightAccelerator> accelerators, int seaLevel)
    {
        try
        {
            var heights = new int?[accelerators.Count][];
            var ms = new double[accelerators.Count][];
            var one = new (int X, int Z)[1];
            for (int a = 0; a < accelerators.Count; a++)
            {
                heights[a] = new int?[cells.Count];
                ms[a] = new double[cells.Count];
                var times = new List<double>(cells.Count);
                var firstInRegion = new List<double>();
                var regions = new HashSet<long>();
                int atSea = 0;
                string? failure = null;
                long started = Stopwatch.GetTimestamp();
                for (int i = 0; i < cells.Count; i++)
                {
                    one[0] = cells[i];
                    long before = Stopwatch.GetTimestamp();
                    int sampled;
                    try
                    {
                        sampled = accelerators[a].Sample(one)[0];
                    }
                    catch (Exception e)
                    {
                        failure = (e is TargetInvocationException { InnerException: { } inner } ? inner : e).Message;
                        break;
                    }
                    double t = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
                    heights[a][i] = sampled;
                    ms[a][i] = t;
                    times.Add(t);
                    // A map region is 512 blocks; its first sample pays for whatever the sampler builds.
                    if (regions.Add(ChunkKey(cells[i].X >> 9, cells[i].Z >> 9))) firstInRegion.Add(t);
                    if (sampled == seaLevel) atSea++;
                }
                double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                lines.Enqueue(Describe(accelerators[a].Name, times, firstInRegion, atSea, seconds)
                    + (failure == null ? "" : $"; FAILED after {times.Count} cells: {failure}"));
            }

            var presample = new LodPresample
            {
                Accelerators = accelerators.Select(a => a.Name).ToList(),
                SeaLevel = seaLevel,
                TakenUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            };
            int unheld = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                (int x, int z) = cells[i];
                bool held = heldChunks.Contains(ChunkKey(x >> 5, z >> 5));
                if (!held) unheld++;
                presample.Cells[(x, z)] = new LodPresampleCell(held,
                    heights.Select(h => h[i]).ToArray(), ms.Select(m => m[i]).ToArray());
            }
            LodHeightSampleFile.WritePresample(path, presample);
            lines.Enqueue(string.Format(CultureInfo.InvariantCulture,
                "{0} cells written to {1}; {2} of them lie in chunks the server had not loaded, so a later "
                + "session can compare them with the terrain once it is generated", cells.Count, path, unheld));
        }
        catch (Exception e)
        {
            lines.Enqueue("the presample itself failed: " + e.Message);
        }
        finally
        {
            finished = true;
        }
    }

    static string Describe(string name, List<double> times, List<double> firstInRegion, int atSea, double seconds)
    {
        if (times.Count == 0) return name + ": no cells sampled";
        return string.Format(CultureInfo.InvariantCulture,
            "{0}: {1} cells in {2:0.0} s; {3:0.00} ms per sample (95% {4:0.00}, max {5:0.0}); first sample in "
            + "each of {6} map regions: median {7:0.0} ms, max {8:0.0}; {9} answers exactly at sea level",
            name, times.Count, seconds, times.Average(), Percentile(times, 0.95), times.Max(),
            firstInRegion.Count, Percentile(firstInRegion, 0.5), firstInRegion.Count == 0 ? 0 : firstInRegion.Max(),
            atSea);
    }

    /// <summary>Nearest-rank percentile; zero when empty.</summary>
    static double Percentile(List<double> values, double q)
    {
        if (values.Count == 0) return 0;
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Length) - 1, 0, sorted.Length - 1)];
    }
}
