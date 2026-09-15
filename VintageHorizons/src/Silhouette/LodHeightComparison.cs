using System.Globalization;

namespace VintageHorizons.Silhouette;

/// <summary>One position where a real height and an accelerator's height were both known.</summary>
public readonly record struct LodHeightPoint(int X, int Z, int Real, int Sampled, EnumSilhouetteOrigin RealOrigin);

/// <summary>
/// How far an accelerator's heights are from real ones at the same block positions
/// (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER P0.3). Real means worldgen heights the server captured
/// or previewed. The owner judges the tolerance (D8); this only measures.
///
/// Coastlines get their own figures because they decide whether islands and inlets survive
/// (D12): a height a few blocks off matters little inland and a great deal where it flips land
/// to sea. Pure, so the fast tier can pin the arithmetic.
/// </summary>
public sealed class LodHeightComparison
{
    /// <summary>A real height within this many blocks of sea level counts as coast.</summary>
    public const int CoastBand = 4;

    public int Count { get; private init; }
    public int Captured { get; private init; }
    public int Previewed { get; private init; }
    public int MedianAbs { get; private init; }
    public int P90Abs { get; private init; }
    public int P95Abs { get; private init; }
    public int MaxAbs { get; private init; }

    /// <summary>Mean of sampled minus real: positive when the accelerator reads high.</summary>
    public double MeanSigned { get; private init; }

    /// <summary>Points where one says land and the other sea (land: at or above sea level).</summary>
    public int LandSeaDisagreements { get; private init; }

    public int CoastCount { get; private init; }
    public int CoastMedianAbs { get; private init; }
    public int CoastLandSeaDisagreements { get; private init; }

    /// <summary>An accelerator that fails quietly answers sea level, so exact answers are counted.</summary>
    public int SampledAtSeaLevel { get; private init; }

    public int RealAtSeaLevel { get; private init; }
    public double MeanMs { get; private init; }
    public double P95Ms { get; private init; }
    public double MaxMs { get; private init; }
    public IReadOnlyList<LodHeightPoint> Worst { get; private init; } = Array.Empty<LodHeightPoint>();

    /// <param name="sampleMs">Time each sample took, in any order; may be empty.</param>
    public static LodHeightComparison Of(IReadOnlyList<LodHeightPoint> points, int seaLevel,
        IReadOnlyList<double> sampleMs, int worstCount = 5)
    {
        int n = points.Count;
        var abs = new int[n];
        var coastAbs = new List<int>();
        long signed = 0;
        int landSea = 0, coastLandSea = 0, sampledAtSea = 0, realAtSea = 0, captured = 0, previewed = 0;
        for (int i = 0; i < n; i++)
        {
            LodHeightPoint p = points[i];
            int error = p.Sampled - p.Real;
            abs[i] = Math.Abs(error);
            signed += error;
            bool disagrees = p.Real >= seaLevel != p.Sampled >= seaLevel;
            if (disagrees) landSea++;
            if (Math.Abs(p.Real - seaLevel) <= CoastBand)
            {
                coastAbs.Add(abs[i]);
                if (disagrees) coastLandSea++;
            }
            if (p.Sampled == seaLevel) sampledAtSea++;
            if (p.Real == seaLevel) realAtSea++;
            if (p.RealOrigin == EnumSilhouetteOrigin.Captured) captured++;
            else if (p.RealOrigin == EnumSilhouetteOrigin.Preview) previewed++;
        }
        Array.Sort(abs);
        coastAbs.Sort();
        double[] ms = sampleMs.ToArray();
        Array.Sort(ms);

        return new LodHeightComparison
        {
            Count = n,
            Captured = captured,
            Previewed = previewed,
            MedianAbs = Rank(abs, 0.5),
            P90Abs = Rank(abs, 0.9),
            P95Abs = Rank(abs, 0.95),
            MaxAbs = n == 0 ? 0 : abs[n - 1],
            MeanSigned = n == 0 ? 0 : (double)signed / n,
            LandSeaDisagreements = landSea,
            CoastCount = coastAbs.Count,
            CoastMedianAbs = Rank(coastAbs, 0.5),
            CoastLandSeaDisagreements = coastLandSea,
            SampledAtSeaLevel = sampledAtSea,
            RealAtSeaLevel = realAtSea,
            MeanMs = ms.Length == 0 ? 0 : ms.Average(),
            P95Ms = Rank(ms, 0.95),
            MaxMs = ms.Length == 0 ? 0 : ms[^1],
            Worst = points.OrderByDescending(p => Math.Abs(p.Sampled - p.Real)).ThenBy(p => p.X).ThenBy(p => p.Z)
                .Take(worstCount).ToList(),
        };
    }

    /// <summary>Nearest-rank percentile of an ascending list; zero when empty.</summary>
    static T Rank<T>(IReadOnlyList<T> sorted, double q) where T : struct =>
        sorted.Count == 0 ? default : sorted[Math.Clamp((int)Math.Ceiling(q * sorted.Count) - 1, 0, sorted.Count - 1)];

    /// <summary>One line for the log and chat.</summary>
    public string Describe(string name)
    {
        var c = CultureInfo.InvariantCulture;
        if (Count == 0) return name + ": no points compared";
        string worst = string.Join("; ", Worst.Select(p => string.Format(c, "{0},{1} real {2} sampled {3}",
            p.X, p.Z, p.Real, p.Sampled)));
        return string.Format(c,
            "{0}: {1} points; error median {2}, 90% {3}, 95% {4}, max {5} blocks; bias {6:+0.0;-0.0;0} "
            + "(positive reads high); land/sea disagrees at {7} points ({8:0.0}%); coast points (real "
            + "within {9} of sea level) {10}, error median {11}, land/sea disagrees at {12}; {13} answers "
            + "exactly at sea level (real: {14}); {15:0.00} ms per sample (95% {16:0.00}, max {17:0.0}); "
            + "worst: {18}",
            name, Count, MedianAbs, P90Abs, P95Abs, MaxAbs, MeanSigned, LandSeaDisagreements,
            100.0 * LandSeaDisagreements / Count, CoastBand, CoastCount, CoastMedianAbs,
            CoastLandSeaDisagreements, SampledAtSeaLevel, RealAtSeaLevel, MeanMs, P95Ms, MaxMs, worst);
    }
}
