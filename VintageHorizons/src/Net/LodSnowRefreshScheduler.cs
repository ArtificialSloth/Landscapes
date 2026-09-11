namespace VintageHorizons.Net;

[Flags]
internal enum EnumLodSnowChange : byte
{
    None = 0,
    Accumulating = 1,
    Melting = 2,
}

/// <summary>
/// Finite, region-scoped snow recapture campaigns.
///
/// There is deliberately no "can accept snow" metadata. Every generated column remains a
/// candidate, and the authoritative capture result only records how much of its surface was
/// bare or snow-covered. That is both information we already need for rendering and a safer
/// filter than guessing which collection of modded blocks might accept snow.
/// </summary>
internal sealed class LodSnowRefreshScheduler
{
    sealed class Candidate
    {
        public required int Cx;
        public required int Cz;
        public required long RegionKey;
        public int ObservedColumns;
        public int CoveredColumns;
    }

    readonly Dictionary<long, Candidate> candidates = new();
    readonly Dictionary<long, List<long>> candidatesByRegion = new();
    readonly List<long> regions = new();
    readonly Queue<long> pending = new();
    readonly Dictionary<long, EnumLodSnowChange> pendingDirections = new();

    public int Count => candidates.Count;
    public int RegionCount => regions.Count;
    public int Pending => pendingDirections.Count;
    public long Campaigns { get; private set; }

    public bool Add(int cx, int cz, int chunksPerRegion)
    {
        long key = LodColumnMap.Key(cx, cz);
        if (candidates.ContainsKey(key)) return false;

        int regionX = FloorDiv(cx, chunksPerRegion);
        int regionZ = FloorDiv(cz, chunksPerRegion);
        long regionKey = LodColumnMap.Key(regionX, regionZ);
        candidates.Add(key, new Candidate { Cx = cx, Cz = cz, RegionKey = regionKey });

        if (!candidatesByRegion.TryGetValue(regionKey, out List<long>? regionCandidates))
        {
            regionCandidates = new List<long>();
            candidatesByRegion.Add(regionKey, regionCandidates);
            regions.Add(regionKey);
        }
        regionCandidates.Add(key);
        return true;
    }

    public void Observe(int cx, int cz, int observedColumns, int coveredColumns)
    {
        if (!candidates.TryGetValue(LodColumnMap.Key(cx, cz), out Candidate? candidate)) return;
        candidate.ObservedColumns = Math.Max(0, observedColumns);
        candidate.CoveredColumns = Math.Clamp(coveredColumns, 0, candidate.ObservedColumns);
    }

    /// <summary>
    /// Enqueue at most one obligation per column for this weather change. Further hourly
    /// snapshots merge into that obligation while the finite campaign drains.
    /// </summary>
    public int Signal(int regionX, int regionZ, EnumLodSnowChange change)
    {
        if (change == EnumLodSnowChange.None) return 0;
        long regionKey = LodColumnMap.Key(regionX, regionZ);
        if (!candidatesByRegion.TryGetValue(regionKey, out List<long>? regionCandidates)) return 0;

        int added = 0;
        foreach (long key in regionCandidates)
        {
            Candidate candidate = candidates[key];
            EnumLodSnowChange eligible = Eligible(candidate, change);
            if (eligible == EnumLodSnowChange.None) continue;

            if (pendingDirections.TryGetValue(key, out EnumLodSnowChange existing))
            {
                pendingDirections[key] = existing | eligible;
                continue;
            }

            pendingDirections.Add(key, eligible);
            pending.Enqueue(key);
            added++;
        }

        if (added > 0) Campaigns++;
        return added;
    }

    public bool TryNext(out int cx, out int cz, out EnumLodSnowChange change)
    {
        while (pending.TryDequeue(out long key))
        {
            if (!pendingDirections.Remove(key, out change)) continue;
            Candidate candidate = candidates[key];
            change = Eligible(candidate, change);
            if (change == EnumLodSnowChange.None) continue;

            cx = candidate.Cx;
            cz = candidate.Cz;
            return true;
        }

        cx = cz = 0;
        change = EnumLodSnowChange.None;
        return false;
    }

    public bool TryRegionAt(int index, out int regionX, out int regionZ,
        out int canaryCx, out int canaryCz)
    {
        if ((uint)index >= (uint)regions.Count)
        {
            regionX = regionZ = canaryCx = canaryCz = 0;
            return false;
        }

        long regionKey = regions[index];
        long candidateKey = candidatesByRegion[regionKey][0];
        Candidate candidate = candidates[candidateKey];
        regionX = LodColumnMap.KeyCx(regionKey);
        regionZ = LodColumnMap.KeyCz(regionKey);
        canaryCx = candidate.Cx;
        canaryCz = candidate.Cz;
        return true;
    }

    public bool TryRegionCanaryAt(int index, Func<int, int, bool> eligible,
        out int regionX, out int regionZ, out int canaryCx, out int canaryCz)
    {
        if ((uint)index >= (uint)regions.Count)
        {
            regionX = regionZ = canaryCx = canaryCz = 0;
            return false;
        }

        long regionKey = regions[index];
        regionX = LodColumnMap.KeyCx(regionKey);
        regionZ = LodColumnMap.KeyCz(regionKey);
        foreach (long candidateKey in candidatesByRegion[regionKey])
        {
            Candidate candidate = candidates[candidateKey];
            if (!eligible(candidate.Cx, candidate.Cz)) continue;
            canaryCx = candidate.Cx;
            canaryCz = candidate.Cz;
            return true;
        }

        canaryCx = canaryCz = 0;
        return false;
    }

    public bool ContainsRegion(long regionKey) => candidatesByRegion.ContainsKey(regionKey);

    static EnumLodSnowChange Eligible(Candidate candidate, EnumLodSnowChange requested)
    {
        // Unknown is eligible in either direction. Once captured, accumulation only needs
        // columns that are not wholly white and melting only needs columns with some white.
        // Permanently mixed surfaces are still allowed to refresh; the bounded weather
        // activity bands, not a guessed snow-capability flag, end their campaigns.
        if (candidate.ObservedColumns == 0) return requested;

        EnumLodSnowChange eligible = EnumLodSnowChange.None;
        if ((requested & EnumLodSnowChange.Accumulating) != 0
            && candidate.CoveredColumns < candidate.ObservedColumns)
            eligible |= EnumLodSnowChange.Accumulating;
        if ((requested & EnumLodSnowChange.Melting) != 0
            && candidate.CoveredColumns > 0)
            eligible |= EnumLodSnowChange.Melting;
        return eligible;
    }

    static int FloorDiv(int value, int divisor)
    {
        divisor = Math.Max(1, divisor);
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }
}

/// <summary>
/// Reduces vanilla's hourly eight-corner snow deltas to coarse, bounded visual-change
/// signals. A permanently cold or warm region reaches a clamp and then becomes idle;
/// crossing back through the bands later produces the opposite finite campaign.
/// </summary>
internal sealed class LodSnowActivityTracker
{
    const float AccumulationBand = 1.5f;
    const float MeltBand = 0.5f;

    sealed class RegionActivity
    {
        public readonly float[] Levels = new float[8];
        public double LastSnapshotHours;
    }

    readonly Dictionary<long, RegionActivity> regions = new();

    public void Prime(long regionKey, double lastSnapshotHours)
    {
        if (!regions.TryGetValue(regionKey, out RegionActivity? state))
        {
            state = new RegionActivity();
            regions.Add(regionKey, state);
        }
        state.LastSnapshotHours = Math.Max(state.LastSnapshotHours, lastSnapshotHours);
    }

    public double LastSnapshotHours(long regionKey) =>
        regions.TryGetValue(regionKey, out RegionActivity? state)
            ? state.LastSnapshotHours : double.NegativeInfinity;

    public EnumLodSnowChange Observe(long regionKey, double snapshotHours,
        ReadOnlySpan<float> cornerDeltas, float limit)
    {
        if (!regions.TryGetValue(regionKey, out RegionActivity? state))
        {
            state = new RegionActivity();
            regions.Add(regionKey, state);
        }
        if (snapshotHours <= state.LastSnapshotHours) return EnumLodSnowChange.None;

        limit = Math.Max(AccumulationBand, limit);
        EnumLodSnowChange change = EnumLodSnowChange.None;
        int count = Math.Min(state.Levels.Length, cornerDeltas.Length);
        for (int i = 0; i < count; i++)
        {
            int beforeBand = VisualBand(state.Levels[i]);
            // Vanilla ultimately stores each surface column in [-1, snow-layer-count+0.6].
            // Its intermediate regional sum is symmetric, but carrying that deep negative
            // range here would delay the next winter even though the real column stopped at -1.
            state.Levels[i] = Math.Clamp(state.Levels[i] + cornerDeltas[i], -1f, limit);
            int afterBand = VisualBand(state.Levels[i]);
            if (afterBand > beforeBand) change |= EnumLodSnowChange.Accumulating;
            if (afterBand < beforeBand) change |= EnumLodSnowChange.Melting;
        }
        state.LastSnapshotHours = snapshotHours;
        return change;
    }

    static int VisualBand(float level) => level >= 0
        ? (int)(level / AccumulationBand)
        : (int)(level / MeltBand);
}
