using System.Globalization;

namespace VintageHorizons;

/// <summary>
/// The handoff flash, fixed where it starts: the range half of chunk ownership is checked again
/// after the engine has moved the player for this frame.
///
/// <para>
/// Measured with `.vhsky` (0.4.52): every large flash was one frame of open sky on a chunk 65-67
/// blocks out - the edge of vanilla's own draw range at view distance 64, where
/// <c>ViewDistanceSq</c> is 64 * 64 + 400 - in a cell the mask had handed to vanilla, over real
/// loaded ground, while the engine still called the chunk rendered. The mask had not changed in
/// that frame. It changed in the next.
/// </para>
/// <para>
/// The engine's frame runs the Before stage, then fills its frustum culler from
/// <c>player.Entity.Pos.AsBlockPos</c>, then draws. The player is moved INSIDE the Before stage,
/// by <c>EntityBehaviorPlayerPhysics</c> at order 1.0 in fixed 1/60 s steps. Since 0.4.13 this
/// renderer's per-frame preparation - including the range sweep that withdraws ownership the
/// moment vanilla's range clause stops admitting a chunk - runs earlier in the same stage, at
/// 0.994, so that it can precede vanilla's liquid-depth pass at 0.995. It has therefore measured
/// the range from where the player stood BEFORE this frame's physics step. Whenever that step
/// carried the player across a block boundary that took a chunk out of vanilla's range, vanilla
/// stopped drawing the chunk in this frame and the mask went on hiding cached terrain there
/// until the next: one frame with nothing drawn at all. 0.4.12 swept in the opaque stage, after
/// the culler was filled, and could not do this.
/// </para>
/// <para>
/// The preparation cannot simply move, because it has to precede the liquid-depth pass. So this
/// is a second, loss-only pass at Before/<see cref="LateRangeBeforeOrder"/>, after the physics
/// step and before anything draws. It captures the range inputs again and, only when the
/// player's block or the culler's settings changed since the preparation swept, runs the same
/// sweep through the same publication handshake and publishes the ground texels. It can hand
/// ground to the cache sooner; it can never take ground away. Water's footprint is left for the
/// next preparation, so the cached-water depth pass that has already run this frame and the
/// water passes still to come read the same footprint.
/// </para>
/// <para>
/// `.vhlaterange off` restores the old timing for a comparison. Session-only, on by default.
/// </para>
/// </summary>
public partial class LodTerrainRenderer
{
    public bool LateRangeCheck { get; private set; } = true;

    /// <summary>Frames in which the late check found the range inputs moved since the preparation.</summary>
    public long LateRangeSweeps { get; private set; }

    /// <summary>Cells handed to the cache by the late check, in the frame vanilla stopped drawing them.</summary>
    public long LateRangeLosses { get; private set; }

    bool lateRangeFailed;

    void RunLateRangeCheck(float dt)
    {
        if (!LateRangeCheck || lateRangeFailed || readinessDisabled || readiness == null) return;
        try
        {
            VanillaDrawRangeState late = CaptureDrawRange();
            // The usual case by far: the physics step did not change the player's block, so the
            // preparation's sweep already saw exactly what the culler is about to be given.
            if (!late.Available || !late.DiffersFrom(lastSweptDrawRange)) return;

            // Kept for the rest of the frame even when no sweep follows: it is what the culler is
            // about to be given, which the preparation's capture no longer is.
            drawRange = late;
            long sweepsBefore = ReadinessRangeSweeps;
            long lossesBefore = ReadinessRangeSweepLosses;
            SweepRangeLosses(frameCounter);
            // The sweep declines on its own when the exact rule or the mask is off.
            if (ReadinessRangeSweeps == sweepsBefore) return;
            LateRangeSweeps++;
            long moved = ReadinessRangeSweepLosses - lossesBefore;
            if (moved <= 0) return;

            LateRangeLosses += moved;
            PublishReadinessMask(rebuildWaterFootprint: false);
            PublishSeamField();
        }
        catch (Exception e)
        {
            // Fail toward the old timing, which was one frame late but never wrong in any
            // other way.
            lateRangeFailed = true;
            capi.Logger.Warning(
                "[VintageHorizons] the late range check stopped for this session; ownership keeps "
                + "the old timing: {0}", e.Message);
        }
    }

    public string SetLateRangeCheck(bool on)
    {
        LateRangeCheck = on;
        lateRangeFailed = false;
        string line = "[VintageHorizons] late range check " + (on ? "on" : "off") + " at frame "
            + frameCounter.ToString(CultureInfo.InvariantCulture) + ". " + DescribeLateRangeCheck();
        capi.Logger.Notification("{0}", line);
        return line;
    }

    public string DescribeLateRangeCheck() =>
        "Late range check " + (LateRangeCheck ? "on" : "off")
        + (lateRangeFailed ? " (STOPPED after an error; see the warning in the log)" : "")
        + ": " + LateRangeSweeps.ToString(CultureInfo.InvariantCulture)
        + " re-sweeps after the game moved the player, "
        + LateRangeLosses.ToString(CultureInfo.InvariantCulture)
        + " chunks handed to distant terrain in the frame vanilla stopped drawing them. Off is the "
        + "timing every build from 0.4.13 to 0.4.52 used, kept only for comparison.";
}
