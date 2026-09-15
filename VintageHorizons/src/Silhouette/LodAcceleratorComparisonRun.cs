using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;

namespace VintageHorizons.Silhouette;

/// <summary>A position where the server holds a real height, captured or previewed.</summary>
internal readonly record struct LodRealHeight(int X, int Z, int Height, EnumSilhouetteOrigin Origin);

/// <summary>
/// One run of the accelerator comparison (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER P0.3), asked for
/// with <c>/vhsilhouetteserver compare</c>.
///
/// A background thread asks each installed accelerator for the heights at positions where the
/// server holds real ones, one position at a time so each call is timed. The main thread reads
/// the finished lines. Accelerators run other mods' world-generation code, so they never run on
/// the main thread, and a failure ends that accelerator's part of the run with its message.
/// </summary>
internal sealed class LodAcceleratorComparisonRun
{
    readonly ConcurrentQueue<string> lines = new();
    volatile bool finished;

    public LodAcceleratorComparisonRun(IReadOnlyList<LodRealHeight> points,
        IReadOnlyList<ILodHeightAccelerator> accelerators, int seaLevel)
    {
        var thread = new Thread(() => Run(points, accelerators, seaLevel))
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

    void Run(IReadOnlyList<LodRealHeight> points, IReadOnlyList<ILodHeightAccelerator> accelerators, int seaLevel)
    {
        try
        {
            foreach (ILodHeightAccelerator accelerator in accelerators)
            {
                var compared = new List<LodHeightPoint>(points.Count);
                var ms = new List<double>(points.Count);
                var one = new (int X, int Z)[1];
                string? failure = null;
                foreach (LodRealHeight point in points)
                {
                    one[0] = (point.X, point.Z);
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
                    compared.Add(new LodHeightPoint(point.X, point.Z, point.Height, sampled, point.Origin));
                }
                string report = LodHeightComparison.Of(compared, seaLevel, ms).Describe(accelerator.Name);
                lines.Enqueue(failure == null ? report : $"{report}; FAILED after {compared.Count} points: {failure}");
            }
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
}
