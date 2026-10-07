namespace VintageHorizons;

/// <summary>
/// How big one block is on screen, under the projection and render resolution actually in use.
///
/// <para>
/// Shared by distant anti-aliasing, which works only where a block has shrunk below a couple of
/// pixels, and by automatic LOD thresholds, which hand every level over at one chosen on-screen
/// size. Both were previously expressible only as distances in blocks, and a distance in blocks is
/// right for one field of view and one resolution. The owner's shimmer began at about 1,000
/// blocks because at 60 degrees and 75% of 1440 rows one block is one pixel at 935 blocks; at
/// 100% it would have begun at 1,247, and a player who changes either moves it again.
/// </para>
/// <para>
/// Engine-free so the check tier can pin it.
/// </para>
/// </summary>
internal static class LodScreenScale
{
    /// <summary>
    /// Pixels one block spans at one block of view depth. The projection's Y scale is the
    /// cotangent of half the vertical field of view, so a surface one block across at view
    /// depth z covers renderHeight * yScale / (2 z) rows: a block is BlockScale / z pixels.
    /// </summary>
    public static float BlockScale(int renderHeight, float projectionYScale) =>
        renderHeight <= 0 || !(projectionYScale > 0f) || !float.IsFinite(projectionYScale)
            ? 0f
            : renderHeight * projectionYScale * 0.5f;

    /// <summary>The view depth at which one block spans the given number of pixels.</summary>
    public static float DepthForBlockPixels(float blockScale, float pixels) =>
        pixels > 0f ? blockScale / pixels : 0f;
}

/// <summary>
/// Distant anti-aliasing's constants and arithmetic. The terrain vertex shader and the resolve
/// pass are handed their ramps through uniforms set from here, so geometry is nudged only where
/// the history blend is already at full share and will average the nudge back out.
///
/// <para>
/// The defaults are the owner's own tuning, set by eye in 0.4.60's settings tab on 2026-09-12
/// and made the defaults in 0.4.61: reach 2.5 px, nudge 0.5, sharpen 1, memory 0.97 turning and
/// 0.99 still, safety check 2.25. Until then they were 2, 1, 0.5, 0.9, 0.97 and 1.25. Sharpen and
/// both memories sit at the top of their ranges, which is where the owner put them.
/// </para>
/// <para>
/// One number sets where it works: the reach, the on-screen width of one block at which it
/// begins. It is full strength where a block is half that wide, so the fade always spans one
/// doubling of distance, whatever the field of view, resolution or reach. History comes in over
/// the first half of the fade and the nudge over the second. 0.4.57 faded both in together, and
/// a pixel nudged at half strength but blended at half share showed most of its nudge, which the
/// owner saw as the faded-in terrain vibrating with the camera still.
/// </para>
/// </summary>
internal static class LodTaaMath
{
    /// <summary>Default reach: anti-aliasing begins where a block is 2.5 pixels wide.</summary>
    public const float DefaultReachPixels = 2.5f;

    public const float MinReachPixels = 1f;

    /// <summary>
    /// Enough to cover every cached block at any usual resolution and FOV: at 32 px it is full
    /// strength from about 60 blocks at 60 degrees and 1080 rendered rows. It works on cached
    /// pixels only, so however large, it never reaches vanilla terrain.
    /// </summary>
    public const float MaxReachPixels = 32f;

    /// <summary>Memory while the view turns (`.dltaa blend`): about the last thirty frames.</summary>
    public const float DefaultFeedback = 0.97f;

    /// <summary>The most turning memory `.dltaa blend` accepts.</summary>
    public const float MaxFeedback = 0.97f;

    /// <summary>
    /// Memory while the whole camera holds still (`.dltaa still`). The nudge repeats every eight
    /// frames, and a running blend of a repeating signal ripples: for a feature seen in one of
    /// the eight positions, by about 4.5% of its contrast at 0.9, 1.3% at 0.97 and 0.4% at 0.99.
    /// </summary>
    public const float DefaultStillFeedback = 0.99f;

    public const float MaxStillFeedback = 0.99f;

    /// <summary>A pixel whose ground moved less than this far since last frame is still.</summary>
    public const float StillMotionPixels = 0.05f;

    /// <summary>From this far per frame on it is moving: full safety check, turning memory.</summary>
    public const float MovingMotionPixels = 0.5f;

    /// <summary>
    /// From this far per frame on a turn is fast and keeps only <see cref="FastShare"/> of the
    /// turning memory: a fast turn smears most, and shimmer is hardest to see during one.
    /// </summary>
    public const float FastMotionPixels = 8f;

    public const float FastShare = 0.85f;

    /// <summary>
    /// Frames the whole camera must hold still before the still memory is fully in force. 0.4.58
    /// decided per pixel, so walking - which barely moves distant ground on screen - counted as
    /// still, and the long memory held on to a turn's blur the moment the turn stopped.
    /// </summary>
    public const int StillEaseFrames = 16;

    /// <summary>A camera step shorter than this, in blocks per frame, is not a movement.</summary>
    public const double StillStepBlocks = 5e-4;

    /// <summary>A view-matrix change smaller than this in every element is not a turn.</summary>
    public const float StillTurn = 1e-5f;

    /// <summary>
    /// Width of the safety check (`.dltaa clip`), in standard deviations of the colours around a
    /// pixel, and never wider than the colours actually there. Lower is tighter: less smear,
    /// more flicker. 4 is in effect the 0.4.58 check, the full range of the neighbourhood, which
    /// on snow - white tops beside dark walls in nearly every 3x3 - let almost any history through.
    /// </summary>
    public const float DefaultClip = 2.25f;
    public const float MinClip = 0.5f;
    public const float MaxClip = 4f;

    /// <summary>
    /// How far terrain is nudged (`.dltaa nudge`), as a share of the full half pixel. Lower is
    /// crisper and shimmers more; 0 leaves only the blending. Half by default: sharper than the
    /// full nudge, with the long memory holding it steady.
    /// </summary>
    public const float DefaultNudge = 0.5f;

    /// <summary>
    /// Sharpening of the anti-aliased terrain (`.dltaa sharpen`), 0 to 1. Applied to the picture
    /// on its way to the screen and never to the memory, so it cannot build up frame on frame.
    /// </summary>
    public const float DefaultSharpen = 1f;

    /// <summary>
    /// The neighbour weight at full sharpening, before the contrast adaptation scales it down:
    /// the strongest setting of AMD's contrast-adaptive sharpening uses -1/5.
    /// </summary>
    public const float MaxSharpenLobe = 0.2f;

    /// <summary>Sub-pixel positions visited before the nudge repeats.</summary>
    public const int JitterSamples = 8;

    public static float ClampReach(float reach) => Math.Clamp(
        float.IsFinite(reach) ? reach : DefaultReachPixels, MinReachPixels, MaxReachPixels);

    /// <summary>Full strength where a block is this wide or narrower: half the reach.</summary>
    public static float FullPixels(float reach) => ClampReach(reach) * 0.5f;

    /// <summary>
    /// Where the nudge begins: three quarters of the reach, the middle of the fade. The vertex
    /// shader's own ramp runs from here to <see cref="FullPixels"/>, which is exactly
    /// <see cref="NudgeStrength"/> of <see cref="Ramp"/>.
    /// </summary>
    public static float NudgeStartPixels(float reach) => ClampReach(reach) * 0.75f;

    /// <summary>0 where a block is the reach or wider on screen, 1 at half the reach or narrower.</summary>
    public static float Ramp(float blockScale, float viewDepth, float reach)
    {
        if (!(blockScale > 0f) || !(viewDepth > 0f)) return 0f;
        float start = ClampReach(reach);
        float full = start * 0.5f;
        return Math.Clamp((start - blockScale / viewDepth) / (start - full), 0f, 1f);
    }

    /// <summary>The history share's part of the ramp: full by its middle.</summary>
    public static float HistoryStrength(float ramp) => Math.Clamp(2f * ramp, 0f, 1f);

    /// <summary>The nudge's part: nothing until the middle of the ramp, full at its end.</summary>
    public static float NudgeStrength(float ramp) => Math.Clamp(2f * ramp - 1f, 0f, 1f);

    /// <summary>A dial's value kept to its range; anything that is not a number is the default.</summary>
    public static float ClampDial(float value, float fallback, float min, float max) =>
        Math.Clamp(float.IsFinite(value) ? value : fallback, min, max);

    /// <summary>How far the still memory is in force after the camera has held still this many frames.</summary>
    public static float Stillness(int stillFrames) =>
        Math.Clamp(stillFrames / (float)StillEaseFrames, 0f, 1f);

    /// <summary>The radical inverse of index in the given base: a low-discrepancy sequence.</summary>
    public static float Halton(int index, int radix)
    {
        float fraction = 1f;
        float result = 0f;
        while (index > 0)
        {
            fraction /= radix;
            result += fraction * (index % radix);
            index /= radix;
        }
        return result;
    }

    /// <summary>
    /// This frame's offset in pixels, each axis in [-0.5, 0.5). Halton bases 2 and 3 spread
    /// eight consecutive frames evenly across the pixel, which is what lets eight frames of
    /// history stand in for eight samples.
    /// </summary>
    public static (float X, float Y) JitterPixels(long frame)
    {
        int index = (int)(((frame % JitterSamples) + JitterSamples) % JitterSamples) + 1;
        return (Halton(index, 2) - 0.5f, Halton(index, 3) - 0.5f);
    }
}

/// <summary>
/// Distant anti-aliasing's tuning dials as one value, so the settings window can show a change
/// at once and put back what was in force when it opened.
/// </summary>
internal readonly record struct LodTaaDials(
    float Reach, float Nudge, float Sharpen, float Blend, float Still, float Clip)
{
    public static LodTaaDials Default => new(
        LodTaaMath.DefaultReachPixels, LodTaaMath.DefaultNudge, LodTaaMath.DefaultSharpen,
        LodTaaMath.DefaultFeedback, LodTaaMath.DefaultStillFeedback, LodTaaMath.DefaultClip);
}

/// <summary>
/// L1-L6 chosen from the screen rather than typed in blocks.
///
/// <para>
/// A level's columns are twice as wide as the level before it, so if every threshold is twice
/// the one before, every level hands over at the same on-screen column width. The owner's
/// hand-set list (1,024 / 1,536 / 2,048 / 4,096 ...) was not a doubling sequence: level 0 ran on
/// until its columns were 0.9 px wide and level 1 until 1.2 px, while every later level stopped
/// at 1.8 px - geometry finer than a pixel close in, which costs triangles and is exactly the
/// detail that shimmers.
/// </para>
/// </summary>
internal static class LodAutoThresholds
{
    public const float DefaultTargetPixels = 1.5f;
    public const float MinTargetPixels = 1f;
    public const float MaxTargetPixels = 4f;

    /// <summary>
    /// L1 is rounded to this many blocks, so nothing smaller than a real settings change can
    /// re-select the world.
    /// </summary>
    public const int RoundingBlocks = 16;

    /// <summary>
    /// A doubling sequence whose first step puts a level-0 column at targetPixels on screen,
    /// held at or beyond floorBlocks, inside the limits every threshold is kept to.
    /// </summary>
    public static int[] Compute(float blockScale, float targetPixels, int floorBlocks)
    {
        float target = Math.Clamp(
            float.IsFinite(targetPixels) ? targetPixels : DefaultTargetPixels,
            MinTargetPixels, MaxTargetPixels);
        double first = blockScale > 0f && float.IsFinite(blockScale)
            ? blockScale / target
            : LodWorld.DefaultLevelThresholds[0];
        first = Math.Min(first, LodWorld.MaxLevelThreshold);

        int rounded = (int)(Math.Round(first / RoundingBlocks, MidpointRounding.AwayFromZero)
            * RoundingBlocks);
        int floor = (int)(Math.Ceiling(Math.Clamp(floorBlocks, 0, LodWorld.MaxLevelThreshold)
            / (double)RoundingBlocks) * RoundingBlocks);
        rounded = Math.Clamp(Math.Max(rounded, floor),
            LodWorld.MinDetailDistance, LodWorld.MaxLevelThreshold);

        int[] values = new int[LodWorld.MaxLevel];
        for (int i = 0; i < values.Length; i++)
            values[i] = (int)Math.Min((long)rounded << i, int.MaxValue);
        return LodWorld.NormalizeLevelThresholds(values, LodWorld.DefaultLevelThresholds[0]);
    }
}
