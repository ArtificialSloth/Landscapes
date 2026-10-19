using System.Runtime.InteropServices;

namespace VintageHorizons;

/// <summary>
/// Section keys ordered nearest-first to a point, in reusable storage (pre-release review P2).
///
/// The request pumps sort every wanted section by distance on each 50 ms tick, to hand the
/// nearest few to a transport with 4 or 16 slots. They used to build two fresh arrays and sort
/// with a comparison that worked out both sections' distances on every compare, about
/// 2 n log n distance calculations for a list that can run to thousands during a server sync.
/// This keeps one key list and one distance buffer, computes each distance once, and sorts the
/// keys by them. The order is the same; only its cost changes. Owning thread only.
/// </summary>
internal sealed class LodNearestFirst
{
    double[] distances = Array.Empty<double>();

    /// <summary>The keys to order. Cleared and refilled by the caller each tick.</summary>
    public List<long> Keys { get; } = new();

    /// <summary>Sorts <see cref="Keys"/> by nearest-edge distance to (x, z), nearest first.</summary>
    public void Sort(double x, double z)
    {
        int count = Keys.Count;
        if (count < 2) return;
        if (distances.Length < count) distances = new double[Math.Max(count, distances.Length * 2)];

        Span<long> keys = CollectionsMarshal.AsSpan(Keys);
        Span<double> by = distances.AsSpan(0, count);
        for (int i = 0; i < count; i++) by[i] = LodWorld.NearestDistanceSqTo(keys[i], x, z);
        by.Sort(keys);
    }
}
