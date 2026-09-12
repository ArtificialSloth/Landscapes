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
/// pass are handed the same ramp through uniforms set from here, so geometry is nudged exactly
/// where the history blend will average it back out and nowhere else.
/// </summary>
internal static class LodTaaMath
{
    /// <summary>No anti-aliasing where a block is at least this many pixels wide.</summary>
    public const float StartBlockPixels = 2f;

    /// <summary>Full strength where a block is this many pixels wide or less.</summary>
    public const float FullBlockPixels = 1f;

    /// <summary>History share at full strength: roughly the last ten frames.</summary>
    public const float DefaultFeedback = 0.9f;

    /// <summary>Beyond this the history can hardly ever be corrected by what is on screen.</summary>
    public const float MaxFeedback = 0.97f;

    /// <summary>Sub-pixel positions visited before the nudge repeats.</summary>
    public const int JitterSamples = 8;

    /// <summary>0 where a block is StartBlockPixels or wider, 1 at FullBlockPixels or narrower.</summary>
    public static float Weight(float blockScale, float viewDepth)
    {
        if (!(blockScale > 0f) || !(viewDepth > 0f)) return 0f;
        float blockPixels = blockScale / viewDepth;
        return Math.Clamp((StartBlockPixels - blockPixels) / (StartBlockPixels - FullBlockPixels),
            0f, 1f);
    }

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
