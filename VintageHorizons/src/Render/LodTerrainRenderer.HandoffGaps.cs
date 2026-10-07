using System.Diagnostics;
using System.Globalization;
using System.Text;
using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// `.dlgap`: the handoff gap log, and its release comparison.
///
/// <para>
/// The flash reported since the snow builds lasts a frame or two. `.dlpaint` turns it magenta,
/// so the mask is hiding cached terrain there, and without paint the sky shows through, so
/// nothing else is drawing either. Turning the engine's occlusion culling off did not change
/// it. The tracker's own counters cannot see this: they describe the tracker's beliefs, and
/// the periodic report fires once, 30 seconds after joining - the 0.4.49 one recorded no
/// vanilla-to-cache handoff at all. This asks the ENGINE, every frame, about every cell whose
/// texel suppresses cached terrain, and writes one client-log line per episode in which the
/// engine is not drawing it: which signal failed, for how many frames, and what ended it.
/// </para>
/// <para>
/// `release` is a comparison, not the fix. It hands such a cell to the cache in the frame the
/// scan finds it, through the same publication handshake the range sweep uses, and only for
/// reasons the ordinary gain path also refuses (<see cref="VanillaHandoffGap.Releasable"/>).
/// It withdraws only ground nothing is drawing, so it adds no overlap, and it leaves
/// <see cref="OverlapMarginBlocks"/> alone.
/// </para>
/// <para>
/// While on, it takes one chunk lookup per suppressing texel per frame - the client's chunk
/// lock (G45) - which is why it is session-only and off by default. Its summary reports what
/// the scan actually cost.
/// </para>
/// </summary>
public partial class LodTerrainRenderer
{
    const int GapLinesPerSecond = 40;
    const long GapSummaryMilliseconds = 1_000;
    const long GapHeartbeatMilliseconds = 10_000;

    public bool HandoffGapLog { get; private set; }
    public bool HandoffGapRelease { get; private set; }

    readonly VanillaHandoffGapTracker gapTracker = new();
    readonly List<VanillaChunkCell> gapCommitted = new();
    readonly List<VanillaHandoffGapTracker.Episode> gapEnded = new();
    readonly int[] gapStartedByReason = new int[VanillaHandoffGap.ReasonCount];
    readonly int[] gapChunkEvents = new int[4];
    System.Func<VanillaChunkCell, VanillaGapEnd>? gapEndOf;
    long gapIntervalStartMs;
    long gapLastSummaryMs;
    long gapLineWindowMs;
    int gapLinesInWindow;
    int gapLinesSuppressed;
    int gapEpisodesEnded;
    long gapVisibleFramesSum;
    long gapVisibleFramesMax;
    int gapReleases;
    int gapAirTexelsCleared;
    int gapScans;
    double gapScanMsSum;
    double gapScanMsMax;
    int gapSuppressingNow;
    int gapSuppressingPeak;
    int gapCrossings;
    int gapInstalledOverOwned;
    int gapLastChunkX = int.MinValue;
    int gapLastChunkZ = int.MinValue;
    long gapEpisodesTotal;

    public string SetHandoffGapLog(bool on)
    {
        long now = capi.ElapsedMilliseconds;
        if (!on && HandoffGapLog) WriteGapSummary(now, force: true);
        bool changed = HandoffGapLog != on || (!on && HandoffGapRelease);
        HandoffGapLog = on;
        if (!on) HandoffGapRelease = false;
        if (changed)
        {
            ResetHandoffGaps(now);
            capi.Logger.Notification("{0}",
                $"[Landscapes] gap log {(on ? "on" : "off")} at frame {frameCounter}; "
                + $"release comparison {(HandoffGapRelease ? "on" : "off")}.{GapPrecondition()}");
        }
        return DescribeHandoffGaps();
    }

    public string SetHandoffGapRelease(bool on)
    {
        long now = capi.ElapsedMilliseconds;
        if (on == HandoffGapRelease && (!on || HandoffGapLog)) return DescribeHandoffGaps();

        // Close the running interval first, so no summary line mixes frames from both sides.
        if (HandoffGapLog) WriteGapSummary(now, force: true);
        else if (on)
        {
            HandoffGapLog = true;
            ResetHandoffGaps(now);
        }
        HandoffGapRelease = on;
        capi.Logger.Notification("{0}",
            $"[Landscapes] gap release comparison {(on ? "on" : "off")} at frame {frameCounter}: "
            + (on
                ? "chunks the engine has stopped drawing (unloaded, fresh, air, no mesh, out of "
                    + "range) are handed to the cache in the frame the scan finds them."
                : "ownership is back to the ordinary probe and range-sweep paths.")
            + GapPrecondition());
        return DescribeHandoffGaps();
    }

    public string DescribeHandoffGaps() =>
        $"[Landscapes] gap log {(HandoffGapLog ? "on" : "off")}, release comparison "
        + $"{(HandoffGapRelease ? "on" : "off")}; {gapEpisodesTotal} gaps since it was turned on, "
        + $"{gapTracker.OpenCount} open now. Every gap, and a summary each busy second (every "
        + "10 s when quiet), goes to the client log." + GapPrecondition();

    string GapPrecondition() =>
        !ChunkMaskEnabled || readinessMaskFailed
            ? " The ownership mask is off, so there is nothing to scan until .dlmask is on."
            : "";

    void ResetHandoffGaps(long now)
    {
        gapTracker.Clear();
        ResetGapInterval(now);
        gapEpisodesTotal = 0;
        gapLastChunkX = gapLastChunkZ = int.MinValue;
        gapLineWindowMs = now;
        gapLinesInWindow = 0;
    }

    void ResetGapInterval(long now)
    {
        Array.Clear(gapStartedByReason);
        Array.Clear(gapChunkEvents);
        gapLinesSuppressed = 0;
        gapEpisodesEnded = 0;
        gapVisibleFramesSum = 0;
        gapVisibleFramesMax = 0;
        gapReleases = 0;
        gapAirTexelsCleared = 0;
        gapScans = 0;
        gapScanMsSum = 0;
        gapScanMsMax = 0;
        gapSuppressingPeak = 0;
        gapCrossings = 0;
        gapInstalledOverOwned = 0;
        gapIntervalStartMs = now;
        gapLastSummaryMs = now;
    }

    /// <summary>
    /// One pass over the suppressing texels, after every ordinary ownership path has run for
    /// this frame and before the mask is published, so what it sees is exactly what cached
    /// terrain is about to be discarded against.
    /// </summary>
    void ScanHandoffGaps(long frame)
    {
        if (!HandoffGapLog) return;
        if (readiness == null || readinessMask == null || readinessMaskFailed || !ChunkMaskEnabled)
            return;

        long scanStarted = Stopwatch.GetTimestamp();
        long now = capi.ElapsedMilliseconds;

        int chunkX = VanillaRenderReadiness.ChunkCoordinate(camPos.X);
        int chunkZ = VanillaRenderReadiness.ChunkCoordinate(camPos.Z);
        bool crossed = gapLastChunkX != int.MinValue
            && (chunkX != gapLastChunkX || chunkZ != gapLastChunkZ);
        if (crossed) gapCrossings++;
        gapLastChunkX = chunkX;
        gapLastChunkZ = chunkZ;

        gapTracker.BeginFrame(frame, now);
        gapEnded.Clear();
        gapCommitted.Clear();
        readiness.CollectCommittedCells(gapCommitted);

        int suppressing = 0;
        foreach (VanillaChunkCell cell in gapCommitted)
        {
            // Only a suppressing texel can hide cached terrain. Air owns its column aggregate
            // but never the texel (G40), so it is skipped here exactly as the shader skips it.
            if (!readinessMask.IsReady(cell)) continue;
            suppressing++;

            IWorldChunk? chunk;
            try { chunk = capi.World.BlockAccessor.GetChunk(cell.X, cell.Y, cell.Z); }
            catch { continue; }

            VanillaGapReason reason = VanillaHandoffGap.Classify(ReadGapSignals(chunk));
            if (reason is VanillaGapReason.Drawing or VanillaGapReason.Unknown) continue;

            // The range sweep decides from the sphere recorded at the last probe. A live sphere
            // out of range while the recorded one is still in range is a gap the sweep could not
            // have caught, which is a different fault from the sweep being late.
            bool sphereStale = reason == VanillaGapReason.OutOfRange
                && readiness.TryGetDrawSphere(cell, out float recordedX, out float recordedZ,
                    out int recordedLods)
                && drawRange.Draws(recordedX, recordedZ, recordedLods);
            string? detail = !gapTracker.IsOpen(cell) && VanillaHandoffGap.LoggedIndividually(reason)
                ? VanillaChunkGeometry.DescribeSignals(chunk)
                : null;
            double distance = VanillaRenderReadiness.ColumnDistanceBlocksFrom(
                cell.X, cell.Z, camPos.X, camPos.Z);

            if (gapTracker.Observe(cell, reason, sphereStale, distance, crossed, detail))
            {
                gapStartedByReason[(int)reason]++;
                gapEpisodesTotal++;
            }

            if (HandoffGapRelease && VanillaHandoffGap.Releasable(reason)
                && ReleaseGapCell(cell, reason, frame))
            {
                gapTracker.Release(cell, gapEnded);
            }
        }

        gapSuppressingNow = suppressing;
        if (suppressing > gapSuppressingPeak) gapSuppressingPeak = suppressing;

        gapEndOf ??= GapEndOf;
        gapTracker.EndFrame(gapEndOf, gapEnded);
        foreach (VanillaHandoffGapTracker.Episode episode in gapEnded) RecordGapEpisode(episode, now);

        double elapsedMs = (Stopwatch.GetTimestamp() - scanStarted) * 1000.0 / Stopwatch.Frequency;
        gapScans++;
        gapScanMsSum += elapsedMs;
        if (elapsedMs > gapScanMsMax) gapScanMsMax = elapsedMs;

        WriteGapSummary(now, force: false);
    }

    VanillaGapSignals ReadGapSignals(IWorldChunk? chunk)
    {
        if (chunk == null)
        {
            return new VanillaGapSignals(Present: false, DrawnKnown: false, DrawnCount: 0,
                Empty: false, GeometryKnown: false, HoldsGeometry: false, Drawable: false,
                CullKnown: false, CullVisible: true, RangeKnown: false, RangeDraws: true);
        }

        bool drawnKnown = VanillaChunkGeometry.TryDrawnCount(chunk, out int drawn);
        bool geometryKnown = VanillaChunkGeometry.TryHoldsGeometry(chunk, out bool holds);
        float centreX = 0f, centreZ = 0f;
        int lodMask = 0;
        bool drawable = geometryKnown && holds
            && VanillaChunkGeometry.TryReadDrawBounds(chunk, out centreX, out centreZ, out lodMask);
        bool cullKnown = VanillaChunkGeometry.TryIsCullVisible(chunk, out bool cullVisible);
        // The engine's clause exactly, with no margin: this measures what the engine did.
        bool rangeKnown = drawable && drawRange.Available;
        bool rangeDraws = !rangeKnown || drawRange.Draws(centreX, centreZ, lodMask);

        bool empty;
        try { empty = chunk.Empty; }
        catch { empty = false; }

        return new VanillaGapSignals(Present: true, DrawnKnown: drawnKnown, DrawnCount: drawn,
            Empty: empty, GeometryKnown: geometryKnown, HoldsGeometry: holds, Drawable: drawable,
            CullKnown: cullKnown, CullVisible: cullVisible, RangeKnown: rangeKnown,
            RangeDraws: rangeDraws);
    }

    bool ReleaseGapCell(VanillaChunkCell cell, VanillaGapReason reason, long frame)
    {
        if (readiness == null || readinessMask == null) return false;

        if (reason == VanillaGapReason.Empty)
        {
            // The publication path's own rule, applied late: air owns its cell for the column
            // aggregate and never the texel (G40). Releasing CPU ownership instead would pull
            // the radial handoff in for the whole column, which is what collapsed 0.3.3.
            readiness.SetMaskExcluded(cell, true);
            readinessMask.Set(cell, false);
            gapAirTexelsCleared++;
            return true;
        }

        // Straight through the publication handshake, exactly as SweepRangeLosses does, so a
        // gap-scan loss is indistinguishable downstream from a probe or sweep loss.
        if (!readiness.Observe(cell, rendered: false, frame,
            out VanillaReadinessPublication publication)) return false;
        if (!readiness.ResolvePublication(publication, accepted: true)) return false;

        readiness.ClearDrawSphere(cell);
        readinessMask.Set(publication.Cell, false);
        readinessHandoffStale = true;
        ReadinessLostTransitions++;
        gapReleases++;
        return true;
    }

    VanillaGapEnd GapEndOf(VanillaChunkCell cell)
    {
        // State first: the mask is a ring, so a texel lookup for a column that has scrolled out
        // answers for whichever column now occupies its slot.
        if (readiness == null || readiness.State(cell) == VanillaReadinessState.Unknown)
            return VanillaGapEnd.LeftWindow;
        return readinessMask != null && readinessMask.IsReady(cell)
            ? VanillaGapEnd.VanillaResumed
            : VanillaGapEnd.HandedToCache;
    }

    void RecordGapEpisode(in VanillaHandoffGapTracker.Episode episode, long now)
    {
        gapEpisodesEnded++;
        gapVisibleFramesSum += episode.VisibleFrames;
        if (episode.VisibleFrames > gapVisibleFramesMax) gapVisibleFramesMax = episode.VisibleFrames;

        if (!VanillaHandoffGap.LoggedIndividually(episode.StartReason)
            && !VanillaHandoffGap.LoggedIndividually(episode.LastReason)) return;

        if (now - gapLineWindowMs >= 1_000)
        {
            gapLineWindowMs = now;
            gapLinesInWindow = 0;
        }
        if (gapLinesInWindow >= GapLinesPerSecond)
        {
            gapLinesSuppressed++;
            return;
        }
        gapLinesInWindow++;

        VanillaChunkCell cell = episode.Cell;
        var line = new StringBuilder(320);
        line.Append("[Landscapes] gap: chunk ")
            .Append(cell.X).Append(',').Append(cell.Y).Append(',').Append(cell.Z)
            .Append(" hid cached terrain while vanilla drew nothing there for ")
            .Append(episode.VisibleFrames).Append(episode.VisibleFrames == 1 ? " frame (" : " frames (")
            .Append(episode.EndMilliseconds - episode.StartMilliseconds).Append(" ms): ")
            .Append(VanillaHandoffGap.Name(episode.StartReason));
        if (episode.LastReason != episode.StartReason)
            line.Append(", then ").Append(VanillaHandoffGap.Name(episode.LastReason));
        if (episode.SphereStale) line.Append(" (the recorded sphere still said in range)");
        line.Append("; ended: ").Append(VanillaHandoffGap.Name(episode.End))
            .Append("; ").Append((long)Math.Round(episode.StartDistanceBlocks)).Append(" blocks away");
        if (episode.StartedOnCrossing) line.Append(", began on a chunk crossing");
        line.Append("; frame ").Append(episode.StartFrame);
        if (episode.StartDetail != null) line.Append(" | at start ").Append(episode.StartDetail);
        capi.Logger.Notification("{0}", line.ToString());
    }

    /// <summary>
    /// One line per busy second and one every ten seconds when quiet. The quiet line is the
    /// proof the log is running: an empty log would otherwise read the same as a broken one.
    /// </summary>
    void WriteGapSummary(long now, bool force)
    {
        if (!HandoffGapLog) return;

        int began = 0;
        foreach (int count in gapStartedByReason) began += count;
        bool busy = began > 0 || gapEpisodesEnded > 0 || gapLinesSuppressed > 0;
        long sinceSummary = now - gapLastSummaryMs;
        if (!force && sinceSummary < (busy ? GapSummaryMilliseconds : GapHeartbeatMilliseconds)) return;

        CultureInfo invariant = CultureInfo.InvariantCulture;
        string seconds = ((now - gapIntervalStartMs) / 1000.0).ToString("0.0", invariant);
        string meanFrames = (gapEpisodesEnded == 0 ? 0.0 : (double)gapVisibleFramesSum / gapEpisodesEnded)
            .ToString("0.0", invariant);
        string scanMean = (gapScans == 0 ? 0.0 : gapScanMsSum * 1000.0 / gapScans).ToString("0", invariant);
        string scanPeak = (gapScanMsMax * 1000.0).ToString("0", invariant);
        int queued = readiness?.PendingCandidates ?? 0;
        long oldest = readiness?.OldestCandidateFrame() ?? -1;
        long queueAge = oldest < 0 ? 0 : Math.Max(0, frameCounter - oldest);

        capi.Logger.Notification("{0}",
            $"[Landscapes] gap summary, {seconds} s: {began} began ("
            + $"unloaded {Began(VanillaGapReason.Unloaded)}, fresh {Began(VanillaGapReason.Fresh)}, "
            + $"air {Began(VanillaGapReason.Empty)}, no mesh {Began(VanillaGapReason.NoMesh)}, "
            + $"hidden {Began(VanillaGapReason.Hidden)}, "
            + $"occlusion-culled {Began(VanillaGapReason.Culled)}, "
            + $"out of range {Began(VanillaGapReason.OutOfRange)}), {gapEpisodesEnded} ended, "
            + $"longest {gapVisibleFramesMax} frames, mean {meanFrames}; "
            + $"release comparison {(HandoffGapRelease ? "on" : "off")}: {gapReleases} released, "
            + $"{gapAirTexelsCleared} air texels cleared; {gapLinesSuppressed} lines suppressed; "
            + $"{gapCrossings} chunk crossings; {gapSuppressingNow} suppressing texels now, "
            + $"peak {gapSuppressingPeak}; chunk events {gapChunkEvents[0]} created/"
            + $"{gapChunkEvents[1]} loaded/{gapChunkEvents[2]} marked dirty/{gapChunkEvents[3]} other, "
            + $"{gapInstalledOverOwned} installed over a cell vanilla owned; "
            + $"probe queue {queued}, oldest {queueAge} frames; "
            + $"scan {scanMean} us mean/{scanPeak} us peak over {gapScans} frames");

        ResetGapInterval(now);
    }

    int Began(VanillaGapReason reason) => gapStartedByReason[(int)reason];

    /// <summary>
    /// Counts the engine's chunk events while the log is on. An install where the mask already
    /// gives vanilla the ground is the client replacing a chunk it was drawing - one way the
    /// engine could briefly hold nothing there - so it is counted on its own.
    /// </summary>
    void NoteGapChunkEvent(int chunkX, int chunkY, int chunkZ, EnumChunkDirtyReason reason)
    {
        if (!HandoffGapLog) return;
        int slot = reason switch
        {
            EnumChunkDirtyReason.NewlyCreated => 0,
            EnumChunkDirtyReason.NewlyLoaded => 1,
            EnumChunkDirtyReason.MarkedDirty => 2,
            _ => 3,
        };
        gapChunkEvents[slot]++;
        if (slot > 1 || readiness == null || readinessMask == null) return;

        var cell = new VanillaChunkCell(chunkX, chunkY, chunkZ);
        if (readiness.State(cell) == VanillaReadinessState.VanillaReady && readinessMask.IsReady(cell))
            gapInstalledOverOwned++;
    }
}
