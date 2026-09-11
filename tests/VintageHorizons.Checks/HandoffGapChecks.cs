namespace VintageHorizons.Checks;

/// <summary>
/// `.vhgap`: the classification its log prints, the rule that keeps its release comparison
/// from fighting the gain path, the episode pairing, and the committed-cell walk it scans.
/// </summary>
public static class HandoffGapChecks
{
    public static void Run(Check c)
    {
        EveryReasonHasASummarySlot(c);
        ClassificationFollowsTheEnginesOrder(c);
        UnmeasurableSignalsNeverReportAGap(c);
        ReleaseOnlyWhatTheGainPathAlsoRefuses(c);
        EpisodesCountTheFramesTheyWereSeen(c);
        ReleaseInTheFoundFrameIsNeverVisible(c);
        ReasonChangesAndStaleSpheresAreKept(c);
        CommittedCellsAreCollectedFromTheWindow(c);
    }

    static VanillaGapSignals Drawing() => new(
        Present: true, DrawnKnown: true, DrawnCount: 3, Empty: false,
        GeometryKnown: true, HoldsGeometry: true, Drawable: true,
        CullKnown: true, CullVisible: true, RangeKnown: true, RangeDraws: true);

    static VanillaGapReason Classify(VanillaGapSignals signals) => VanillaHandoffGap.Classify(signals);

    static void EveryReasonHasASummarySlot(Check c)
    {
        c.Eq(Enum.GetValues<VanillaGapReason>().Length, VanillaHandoffGap.ReasonCount,
            "the per-reason summary array has a slot for every reason");
    }

    static void ClassificationFollowsTheEnginesOrder(Check c)
    {
        c.Eq(VanillaGapReason.Drawing, Classify(Drawing()), "every signal saying yes is drawing");
        c.Eq(VanillaGapReason.Unloaded, Classify(Drawing() with { Present = false }),
            "a chunk the client no longer holds is unloaded, whatever else is set");
        c.Eq(VanillaGapReason.Fresh, Classify(Drawing() with { DrawnCount = 0 }),
            "a chunk object the tessellator has never visited is fresh");
        c.Eq(VanillaGapReason.Empty, Classify(Drawing() with { Empty = true }),
            "air holding a suppressing texel is reported as air");
        c.Eq(VanillaGapReason.NoMesh,
            Classify(Drawing() with { HoldsGeometry = false, Drawable = false }),
            "a tessellated chunk holding no pool geometry has no mesh");
        c.Eq(VanillaGapReason.Hidden, Classify(Drawing() with { Drawable = false }),
            "geometry with nothing submittable is hidden");
        c.Eq(VanillaGapReason.Culled, Classify(Drawing() with { CullVisible = false }),
            "the ray culler's verdict is reported as occlusion culling");
        c.Eq(VanillaGapReason.OutOfRange, Classify(Drawing() with { RangeDraws = false }),
            "the engine's range clause is reported as out of range");

        c.Eq(VanillaGapReason.Fresh,
            Classify(Drawing() with { DrawnCount = 0, HoldsGeometry = false, Drawable = false, CullVisible = false }),
            "fresh outranks the mesh and the culler, which a new chunk object has not been through");
        c.Eq(VanillaGapReason.NoMesh,
            Classify(Drawing() with { HoldsGeometry = false, Drawable = false, CullVisible = false, RangeDraws = false }),
            "no mesh outranks culling and range, whose inputs live on the mesh");
    }

    static void UnmeasurableSignalsNeverReportAGap(Check c)
    {
        c.Eq(VanillaGapReason.Unknown,
            Classify(Drawing() with { GeometryKnown = false, HoldsGeometry = false, Drawable = false }),
            "an unavailable geometry binding is unknown, not a missing mesh");
        c.Eq(VanillaGapReason.Drawing, Classify(Drawing() with { DrawnKnown = false, DrawnCount = 0 }),
            "an unreadable drawn counter never reports a fresh chunk");
        c.Eq(VanillaGapReason.Drawing, Classify(Drawing() with { CullKnown = false, CullVisible = false }),
            "an unreadable culler verdict never reports culling");
        c.Eq(VanillaGapReason.Drawing, Classify(Drawing() with { RangeKnown = false, RangeDraws = false }),
            "an unavailable range clause never reports out of range");
    }

    static void ReleaseOnlyWhatTheGainPathAlsoRefuses(Check c)
    {
        foreach (VanillaGapReason reason in new[]
        {
            VanillaGapReason.Unloaded, VanillaGapReason.Fresh, VanillaGapReason.Empty,
            VanillaGapReason.NoMesh, VanillaGapReason.OutOfRange,
        })
        {
            c.True(VanillaHandoffGap.Releasable(reason),
                $"{reason} may be released: the ordinary gain path refuses it too");
        }

        foreach (VanillaGapReason reason in new[]
        {
            VanillaGapReason.Drawing, VanillaGapReason.Hidden, VanillaGapReason.Culled,
            VanillaGapReason.Unknown,
        })
        {
            c.False(VanillaHandoffGap.Releasable(reason),
                $"{reason} is never released: the gain path would take it straight back");
        }

        c.False(VanillaHandoffGap.LoggedIndividually(VanillaGapReason.Culled),
            "occlusion culling is summarised, not listed chunk by chunk");
        c.True(VanillaHandoffGap.LoggedIndividually(VanillaGapReason.Unloaded)
            && VanillaHandoffGap.LoggedIndividually(VanillaGapReason.Fresh)
            && VanillaHandoffGap.LoggedIndividually(VanillaGapReason.OutOfRange),
            "the handoff reasons each get a line");
    }

    static void EpisodesCountTheFramesTheyWereSeen(Check c)
    {
        var tracker = new VanillaHandoffGapTracker();
        var ended = new List<VanillaHandoffGapTracker.Episode>();
        var cell = new VanillaChunkCell(4, 2, 7);

        for (long frame = 10; frame <= 12; frame++)
        {
            tracker.BeginFrame(frame, frame * 16);
            bool opened = tracker.Observe(cell, VanillaGapReason.Unloaded, false, 70.0,
                onCrossing: frame == 10);
            c.Eq(frame == 10, opened, $"frame {frame}: only the first sighting opens an episode");
            c.Eq(0, tracker.EndFrame(_ => VanillaGapEnd.HandedToCache, ended),
                $"frame {frame}: a cell sighted this frame stays open");
        }

        tracker.BeginFrame(13, 13 * 16);
        c.Eq(1, tracker.EndFrame(_ => VanillaGapEnd.HandedToCache, ended),
            "the first frame without a sighting closes the episode");
        c.Eq(1, ended.Count, "one episode is reported");
        if (ended.Count != 1) return;

        VanillaHandoffGapTracker.Episode episode = ended[0];
        c.Eq(3L, episode.VisibleFrames, "sighted in three frames, drawn wrong in three frames");
        c.Eq(10L, episode.StartFrame, "the episode starts at its first sighting");
        c.Eq(48L, episode.EndMilliseconds - episode.StartMilliseconds, "and reports its length in time");
        c.Eq(VanillaGapEnd.HandedToCache, episode.End, "the end cause is the caller's answer");
        c.True(episode.StartedOnCrossing, "a gap that began on a chunk crossing says so");
        c.Eq(0, tracker.OpenCount, "nothing is left open");

        tracker.BeginFrame(14, 14 * 16);
        c.True(tracker.Observe(cell, VanillaGapReason.Unloaded, false, 70.0, false),
            "a gap after a clean frame is a second episode, not a continuation");
        tracker.Clear();
        c.Eq(0, tracker.OpenCount, "clearing forgets open episodes");
        c.Eq(0, tracker.EndFrame(_ => VanillaGapEnd.VanillaResumed, ended),
            "and a cleared episode is never reported");
    }

    static void ReleaseInTheFoundFrameIsNeverVisible(Check c)
    {
        var tracker = new VanillaHandoffGapTracker();
        var ended = new List<VanillaHandoffGapTracker.Episode>();
        var cell = new VanillaChunkCell(1, 1, 1);

        tracker.BeginFrame(20, 320);
        tracker.Observe(cell, VanillaGapReason.Fresh, false, 40.0, false);
        c.True(tracker.Release(cell, ended), "an open episode can be released");
        c.Eq(1, ended.Count, "a release reports the episode at once");
        if (ended.Count == 1)
        {
            c.Eq(0L, ended[0].VisibleFrames, "released before the cache draws, so never drawn wrong");
            c.Eq(VanillaGapEnd.ReleasedByGapScan, ended[0].End, "and it says who released it");
        }

        c.Eq(0, tracker.EndFrame(_ => VanillaGapEnd.VanillaResumed, ended),
            "a released episode is not closed a second time");
        c.False(tracker.Release(cell, ended), "releasing a cell with no open episode does nothing");
    }

    static void ReasonChangesAndStaleSpheresAreKept(Check c)
    {
        var tracker = new VanillaHandoffGapTracker();
        var ended = new List<VanillaHandoffGapTracker.Episode>();
        var cell = new VanillaChunkCell(2, 0, 3);

        tracker.BeginFrame(1, 0);
        tracker.Observe(cell, VanillaGapReason.Culled, false, 60.0, false);
        tracker.EndFrame(_ => VanillaGapEnd.VanillaResumed, ended);
        tracker.BeginFrame(2, 16);
        tracker.Observe(cell, VanillaGapReason.OutOfRange, true, 61.0, false);
        tracker.EndFrame(_ => VanillaGapEnd.VanillaResumed, ended);
        tracker.BeginFrame(3, 32);
        tracker.EndFrame(_ => VanillaGapEnd.LeftWindow, ended);

        c.Eq(1, ended.Count, "a reason change stays one episode");
        if (ended.Count != 1) return;
        c.Eq(VanillaGapReason.Culled, ended[0].StartReason, "the first reason is kept");
        c.Eq(VanillaGapReason.OutOfRange, ended[0].LastReason, "and so is the last");
        c.True(ended[0].SphereStale, "a stale recorded sphere seen at any point is kept");
        c.Eq(60.0, ended[0].StartDistanceBlocks, "the distance is the one at the start");
        c.Eq(VanillaGapEnd.LeftWindow, ended[0].End, "the end cause is the one at the close");
    }

    static void CommittedCellsAreCollectedFromTheWindow(Check c)
    {
        var model = new VanillaRenderReadiness(8, 2);
        model.SetWindow(0, 0, 8, 8);
        var owned = new VanillaChunkCell(3, 1, 4);
        var observed = new VanillaChunkCell(5, 0, 5);

        Commit(model, owned, 1);
        model.Observe(observed, rendered: true, 5, out _);
        c.Eq(VanillaReadinessState.VanillaReady, model.State(owned), "the fixture commits one cell");

        var into = new List<VanillaChunkCell>();
        c.Eq(1, model.CollectCommittedCells(into), "only the committed cell is collected");
        c.SeqEq(new[] { owned }, into, "and it is the committed cell");
        c.Eq(VanillaReadinessState.ObservedRendered, model.State(observed),
            "collecting changes no cell's state");

        model.SetWindow(8, 8, 8, 8);
        into.Clear();
        c.Eq(0, model.CollectCommittedCells(into),
            "a committed column that has left the window is not collected");
    }

    static void Commit(VanillaRenderReadiness model, VanillaChunkCell cell, long frame)
    {
        model.Observe(cell, rendered: true, frame, out _);
        model.Observe(cell, rendered: true, frame + 1, out VanillaReadinessPublication publication);
        model.ResolvePublication(publication, accepted: true);
    }
}
