using VintageHorizons.Net;

namespace VintageHorizons.Checks;

/// <summary>
/// The safety rule behind sweeping and generation: which columns a bulk pass can load,
/// which it can peek, and which it must not touch. This rule keeps two promises. The
/// sweep must not make the engine generate terrain. Generation must never peek a column
/// that exists, because a peek regenerates from the seed and would describe the terrain
/// as it was before anyone built on it.
/// </summary>
public static class GenerateChecks
{
    public static void Run(Check c)
    {
        KeyInjective(c);
        Neighbourhood(c);
        ClassifyRule(c);
        RefreshNeverGenerates(c);
        GeneratorConstants(c);
        AbsentSampling(c);
        SweepAbsenceVerdicts(c);
        SnowRefreshCampaigns(c);
        SnowActivityBands(c);
    }

    static void SweepAbsenceVerdicts(Check c)
    {
        c.Eq(EnumSweepAbsenceResult.Verified,
            LodSavegameSweep.ClassifyAbsenceResult(regrown: 0, otherGenerationQueuePeak: 0),
            "a clean absence sample verifies an idle sweep");
        c.Eq(EnumSweepAbsenceResult.Verified,
            LodSavegameSweep.ClassifyAbsenceResult(regrown: 0, otherGenerationQueuePeak: 201),
            "other generation does not erase a clean absence result");
        c.Eq(EnumSweepAbsenceResult.Unsafe,
            LodSavegameSweep.ClassifyAbsenceResult(regrown: 1, otherGenerationQueuePeak: 0),
            "new terrain with no competing engine work retains the unsafe-sweep warning");
        c.Eq(EnumSweepAbsenceResult.Inconclusive,
            LodSavegameSweep.ClassifyAbsenceResult(regrown: 6, otherGenerationQueuePeak: 201),
            "new terrain during independently observed pregen is inconclusive, not a sweep warning");
    }

    static void RefreshNeverGenerates(Check c)
    {
        c.Eq(LodPlayerPregen.EnumRunAction.Peek,
            LodPlayerPregen.SelectAction(EnumColumnAction.Peek, LodPlayerPregen.EnumPurpose.Generate),
            "generation may peek a missing column");
        c.Eq(LodPlayerPregen.EnumRunAction.SkipMissing,
            LodPlayerPregen.SelectAction(EnumColumnAction.Peek, LodPlayerPregen.EnumPurpose.RefreshExisting),
            "refresh skips missing columns instead of peeking them");
        c.Eq(LodPlayerPregen.EnumRunAction.Load,
            LodPlayerPregen.SelectAction(EnumColumnAction.Load, LodPlayerPregen.EnumPurpose.RefreshExisting),
            "refresh loads a safe existing column for recapture");
        c.Eq(LodPlayerPregen.EnumRunAction.SkipFrontier,
            LodPlayerPregen.SelectAction(EnumColumnAction.SkipFrontier,
                LodPlayerPregen.EnumPurpose.RefreshExisting),
            "refresh preserves the no-generation frontier rule");
    }

    static void SnowRefreshCampaigns(Check c)
    {
        var refresh = new LodSnowRefreshScheduler();
        c.False(refresh.TryNext(out _, out _, out _), "an empty snow scheduler has no work");

        c.True(refresh.Add(-1, -1, 16), "the first snow candidate is accepted");
        c.True(refresh.Add(-2, -1, 16), "a second column in the region is accepted");
        c.False(refresh.Add(-1, -1, 16), "snow candidates are deduplicated");
        c.Eq(2, refresh.Count, "deduplication keeps the candidate set bounded");
        c.Eq(1, refresh.RegionCount, "negative columns use floor division into one map region");
        c.True(refresh.TryRegionCanaryAt(0, (x, _) => x == -2,
                out int regionX, out int regionZ, out int canaryX, out int canaryZ),
            "a region can choose a later safe candidate when its first column is frontier");
        c.Eq((-1, -1), (regionX, regionZ), "the canary retains the signed map-region key");
        c.Eq((-2, -1), (canaryX, canaryZ), "the eligibility rule selected the safe canary");

        c.Eq(2, refresh.Signal(-1, -1, EnumLodSnowChange.Accumulating),
            "a weather change creates one finite regional campaign");
        c.Eq(0, refresh.Signal(-1, -1, EnumLodSnowChange.Accumulating),
            "a repeated signal merges with pending columns instead of duplicating them");
        c.Eq(2, refresh.Pending, "the finite queue has one obligation per candidate");

        c.True(refresh.TryNext(out int firstX, out int firstZ, out EnumLodSnowChange firstChange),
            "the first campaign column is available");
        c.Eq((-1, -1), (firstX, firstZ), "campaign order is stable");
        c.Eq(EnumLodSnowChange.Accumulating, firstChange, "the weather direction is retained");
        c.True(refresh.TryNext(out int secondX, out int secondZ, out _),
            "the second campaign column is available");
        c.Eq((-2, -1), (secondX, secondZ), "the region drains exactly once");
        c.False(refresh.TryNext(out _, out _, out _),
            "a completed campaign becomes idle instead of starting another pass");

        refresh.Observe(-1, -1, observedColumns: 1024, coveredColumns: 1024);
        refresh.Observe(-2, -1, observedColumns: 1024, coveredColumns: 0);
        c.Eq(1, refresh.Signal(-1, -1, EnumLodSnowChange.Accumulating),
            "accumulation skips a fully snowy column but retains a bare one");
        c.True(refresh.TryNext(out secondX, out secondZ, out _), "the bare column was queued");
        c.Eq((-2, -1), (secondX, secondZ), "the accumulation filter chose the bare column");

        c.Eq(1, refresh.Signal(-1, -1, EnumLodSnowChange.Melting),
            "melting skips a bare column but retains a snowy one");
        c.True(refresh.TryNext(out firstX, out firstZ, out _), "the snowy column was queued");
        c.Eq((-1, -1), (firstX, firstZ), "the melt filter chose the snowy column");
    }

    static void SnowActivityBands(Check c)
    {
        var activity = new LodSnowActivityTracker();
        long region = LodColumnMap.Key(3, 5);
        activity.Prime(region, 100);

        float[] lightSnow = Enumerable.Repeat(0.5f, 8).ToArray();
        c.Eq(EnumLodSnowChange.None, activity.Observe(region, 101, lightSnow, 3.6f),
            "sub-band accumulation does not start a premature campaign");
        c.Eq(EnumLodSnowChange.None, activity.Observe(region, 102, lightSnow, 3.6f),
            "activity accumulates across vanilla hourly snapshots");
        c.Eq(EnumLodSnowChange.Accumulating, activity.Observe(region, 103, lightSnow, 3.6f),
            "crossing a visual snow band starts accumulation work");

        c.Eq(EnumLodSnowChange.None, activity.Observe(region, 103, lightSnow, 3.6f),
            "the same vanilla snapshot is never processed twice");
        c.Eq(EnumLodSnowChange.Accumulating, activity.Observe(region, 110,
                Enumerable.Repeat(20f, 8).ToArray(), 3.6f),
            "a time jump clamps at the configured snow range and signals once");
        c.Eq(EnumLodSnowChange.None, activity.Observe(region, 111,
                Enumerable.Repeat(20f, 8).ToArray(), 3.6f),
            "a permanently cold region becomes idle after reaching the clamp");

        c.Eq(EnumLodSnowChange.Melting, activity.Observe(region, 112,
                Enumerable.Repeat(-2f, 8).ToArray(), 3.6f),
            "a later thaw crosses back into a finite melt campaign");

        var bareActivity = new LodSnowActivityTracker();
        bareActivity.Prime(region, 200);
        c.Eq(EnumLodSnowChange.Melting, bareActivity.Observe(region, 201,
                Enumerable.Repeat(-0.5f, 8).ToArray(), 3.6f),
            "half a snow level is enough to check whether thin existing snow melted");
        c.Eq(EnumLodSnowChange.Melting, bareActivity.Observe(region, 202,
                Enumerable.Repeat(-20f, 8).ToArray(), 3.6f),
            "reaching vanilla's -1 floor produces the final melt check");
        c.Eq(EnumLodSnowChange.None, bareActivity.Observe(region, 203,
                Enumerable.Repeat(-20f, 8).ToArray(), 3.6f),
            "a permanently warm region becomes idle at vanilla's -1 floor");
    }

    static void GeneratorConstants(Check c)
    {
        // A canary, with the reason in the message: raising the pass without the
        // Harmony guard TopoHorizon ships crashes vanilla worldgen.
        c.Eq(Vintagestory.API.Server.EnumWorldGenPass.Terrain, LodPlayerPregen.Pass,
            "peeks stop at Terrain: Vegetation NREs in vanilla worldgen without a Harmony guard we do not ship");

        // These numbers go to the person who typed the command.
        c.Eq(1, LodPlayerPregen.ColumnsFor(0), "radius 0 is one column");
        c.Eq(2401, LodPlayerPregen.ColumnsFor(24), "radius 24 is 2401 columns");
        c.Eq(66049, LodPlayerPregen.ColumnsFor(128), "radius 128 is 66049 columns");
        c.True(LodPlayerPregen.EstimateSeconds(2401, 16) > 0, "a real run estimates a non-zero time");
        c.Eq(0, LodPlayerPregen.EstimateSeconds(1, 16), "one column rounds to zero seconds, not a crash");

        CentrePrecedence(c);
    }

    /// <summary>
    /// Where a command run centres itself. World coordinates are never negative, so -1
    /// means "not given". One coordinate alone used to be ignored in silence, and the
    /// run centred somewhere else entirely without saying so.
    /// </summary>
    static void CentrePrecedence(Check c)
    {
        c.Eq(LodPlayerPregen.EnumCentre.Argument,
            LodPlayerPregen.ResolveCentre(480000, 480000, callerHasPosition: true),
            "both coordinates win over the caller's position");
        c.Eq(LodPlayerPregen.EnumCentre.Argument,
            LodPlayerPregen.ResolveCentre(0, 0, callerHasPosition: false),
            "0,0 is a real coordinate, not a missing one");
        c.Eq(LodPlayerPregen.EnumCentre.Caller,
            LodPlayerPregen.ResolveCentre(-1, -1, callerHasPosition: true),
            "no coordinates centres on the caller");
        c.Eq(LodPlayerPregen.EnumCentre.Spawn,
            LodPlayerPregen.ResolveCentre(-1, -1, callerHasPosition: false),
            "the console has no position, so it falls back to spawn");

        // The case that used to pass silently. Either half alone is refused.
        c.Eq(LodPlayerPregen.EnumCentre.Incomplete,
            LodPlayerPregen.ResolveCentre(480000, -1, callerHasPosition: true),
            "an x with no z is refused, not ignored");
        c.Eq(LodPlayerPregen.EnumCentre.Incomplete,
            LodPlayerPregen.ResolveCentre(-1, 480000, callerHasPosition: true),
            "a z with no x is refused, not ignored");
        c.Eq(LodPlayerPregen.EnumCentre.Incomplete,
            LodPlayerPregen.ResolveCentre(480000, -1, callerHasPosition: false),
            "a half coordinate is refused from the console too");
    }

    static void AbsentSampling(Check c)
    {
        // The verify sample must contain only absent positions - a present position in
        // the sample would count a legitimate load as a broken promise.
        const int R = 10;
        var map = new LodColumnMap();
        for (int cz = -R; cz <= R; cz++)
        for (int cx = -R; cx <= R; cx++)
        {
            map.Add(cx, cz);
        }

        List<long> sample = map.AbsentSample(0, 0, 14, 64);
        bool onlyAbsent = true, inRadius = true;
        foreach (long key in sample)
        {
            int cx = LodColumnMap.KeyCx(key), cz = LodColumnMap.KeyCz(key);
            onlyAbsent &= !map.Contains(cx, cz);
            inRadius &= Math.Max(Math.Abs(cx), Math.Abs(cz)) <= 14;
        }
        c.True(onlyAbsent, "the sample holds only positions absent from the map");
        c.True(inRadius, "the sample stays inside the requested radius");
        c.Eq(64, sample.Count, "a large absent set fills the sample to its cap exactly");

        // 29x29 window minus the 21x21 square = 400 absents; ask for more than exist.
        List<long> all = map.AbsentSample(0, 0, 14, 1000);
        c.Eq(29 * 29 - 21 * 21, all.Count, "a cap above the absent count returns every absent position");

        c.Eq(0, map.AbsentSample(0, 0, R - LodColumnMap.SafeNeighbourhood, 100).Count,
            "a fully generated area has nothing to sample");

        // Round-trip of the key unpacking the verifier depends on, at negatives too.
        c.Eq(-37, LodColumnMap.KeyCx(LodColumnMap.Key(-37, 91)), "KeyCx round-trips a negative cx");
        c.Eq(91, LodColumnMap.KeyCz(LodColumnMap.Key(-37, 91)), "KeyCz round-trips");

        EligibilityKeepsTheSampleUseful(c);
    }

    /// <summary>
    /// A run centred on a player must still verify something.
    ///
    /// The verifier ignores positions close to an online player, because the engine
    /// generates terrain around players as ordinary play. When the sample was drawn
    /// before that filter ran, a player-centred run had its whole sample discarded and
    /// reported "Verified 0/0" - honest, and completely uninformative, for the most
    /// common way anyone uses the command.
    /// </summary>
    static void EligibilityKeepsTheSampleUseful(Check c)
    {
        var empty = new LodColumnMap();   // nothing exists, so every position is absent

        // A player sits at the centre. Everything within 12 chunks is theirs to explain.
        Func<int, int, bool> away = (cx, cz) => Math.Max(Math.Abs(cx), Math.Abs(cz)) > 12;

        List<long> filtered = empty.AbsentSample(0, 0, 20, 64, away);
        c.Eq(64, filtered.Count, "a player-centred run still fills its sample");
        int inside = filtered.Count(k =>
            !away(LodColumnMap.KeyCx(k), LodColumnMap.KeyCz(k)));
        c.Eq(0, inside, "no sampled position sits inside the player's own radius");

        // With no eligible position anywhere, it falls back rather than returning empty.
        // Something measured beats nothing, and the verifier reports the skips either way.
        List<long> allInside = empty.AbsentSample(0, 0, 5, 16, away);
        c.True(allInside.Count > 0,
            "when every position is near a player, the sample falls back instead of emptying");

        // Without a filter the behaviour is unchanged, so existing callers are unaffected.
        c.Eq(64, empty.AbsentSample(0, 0, 20, 64).Count, "an unfiltered sample is unchanged");
    }

    static void KeyInjective(Check c)
    {
        // The packing looks obviously fine and is exactly the kind of thing that breaks
        // at negative coordinates. World coordinates are non-negative today; the map
        // must not silently rely on that.
        var seen = new HashSet<long>();
        for (int cz = -64; cz <= 64; cz++)
        for (int cx = -64; cx <= 64; cx++)
        {
            seen.Add(LodColumnMap.Key(cx, cz));
        }
        c.Eq(129 * 129, seen.Count, "Key is injective over a -64..64 grid");
    }

    static void Neighbourhood(Check c)
    {
        c.Eq(4, LodColumnMap.SafeNeighbourhood,
            "the neighbourhood width is the measured 4 (no check 1460, r1 714, r2 509, r4 0 columns generated)");

        int r = LodColumnMap.SafeNeighbourhood;

        // A full 9x9 is complete, including at negative coordinates.
        foreach ((int ox, int oz) in new[] { (10, 10), (-100, -100), (0, 0) })
        {
            var map = new LodColumnMap();
            for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                map.Add(ox + dx, oz + dz);
            }
            c.True(map.NeighbourhoodComplete(ox, oz), $"a full 9x9 around ({ox},{oz}) is complete");
        }

        // Any single missing cell breaks it. All 81 positions, not a sample: a corner
        // that the loop misses is exactly the bug this exists to catch.
        int broken = 0;
        for (int missZ = -r; missZ <= r; missZ++)
        for (int missX = -r; missX <= r; missX++)
        {
            var map = new LodColumnMap();
            for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (dx == missX && dz == missZ) continue;
                map.Add(10 + dx, 10 + dz);
            }
            if (!map.NeighbourhoodComplete(10, 10)) broken++;
        }
        c.Eq(81, broken, "each of the 81 cells is load-bearing: removing any one breaks completeness");
    }

    static void ClassifyRule(Check c)
    {
        int r = LodColumnMap.SafeNeighbourhood;

        // An empty map peeks everywhere. No frontier rule applies to a peek, and that
        // asymmetry is the difference between generation and the sweep.
        var empty = new LodColumnMap();
        bool allPeek = true;
        foreach ((int cx, int cz) in new[] { (0, 0), (500, 500), (-3, 7), (100000, -100000) })
        {
            allPeek &= empty.Classify(cx, cz) == EnumColumnAction.Peek;
        }
        c.True(allPeek, "an empty map classifies every position Peek, neighbours or none");

        // A filled square of Chebyshev radius 10 around the origin. The expected action
        // at every position follows from the distance alone, so assert the whole window
        // against the formula and then the three counts against each other.
        const int R = 10;
        var disc = new LodColumnMap();
        for (int cz = -R; cz <= R; cz++)
        for (int cx = -R; cx <= R; cx++)
        {
            disc.Add(cx, cz);
        }

        int load = 0, frontier = 0, peek = 0, mismatches = 0, peekedExisting = 0;
        const int W = 20;
        for (int cz = -W; cz <= W; cz++)
        for (int cx = -W; cx <= W; cx++)
        {
            int cheb = Math.Max(Math.Abs(cx), Math.Abs(cz));
            EnumColumnAction expected =
                cheb <= R - r ? EnumColumnAction.Load
                : cheb <= R   ? EnumColumnAction.SkipFrontier
                :               EnumColumnAction.Peek;

            EnumColumnAction got = disc.Classify(cx, cz);
            if (got != expected) mismatches++;
            if (got == EnumColumnAction.Peek && disc.Contains(cx, cz)) peekedExisting++;

            switch (got)
            {
                case EnumColumnAction.Load: load++; break;
                case EnumColumnAction.SkipFrontier: frontier++; break;
                case EnumColumnAction.Peek: peek++; break;
            }
        }

        c.Eq(0, mismatches, "every position in the window classifies by Chebyshev distance alone");
        c.Eq((2 * (R - r) + 1) * (2 * (R - r) + 1), load, "Load covers the interior square");
        c.Eq((2 * R + 1) * (2 * R + 1) - (2 * (R - r) + 1) * (2 * (R - r) + 1), frontier,
            "SkipFrontier covers the outer rings of the existing square");
        c.Eq((2 * W + 1) * (2 * W + 1) - (2 * R + 1) * (2 * R + 1), peek,
            "Peek covers exactly the positions that do not exist");

        // The non-destructive assertion. A peek of an existing column would cache a
        // horizon from before the player built there.
        c.Eq(0, peekedExisting, "Classify never returns Peek for a column that exists");
    }
}
