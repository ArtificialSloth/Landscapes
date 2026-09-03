namespace VintageHorizons;

/// <summary>
/// The seasonal colour the terrain SHADER uses, which is not the one the engine's CPU colour
/// call returns.
///
/// Both read the same 128-column season map, but they choose the column differently:
/// <code>
///   CPU    (ClientWorldMap.ApplyColorMapOnRgba)  column = YearRel * width
///   shader (colormap.vsh)                        column = (seasonRel + b/40) * width,
///                                                b = valuenoise(p) + valuenoise(p/2), 0..2
/// </code>
/// The shader advances the season by up to 0.05 of a year and on average 0.025 - about ten
/// in-game days, or three columns of the map - and the CPU advances it not at all.
///
/// Everything the mod knew about the season came from that CPU call, so distant terrain wore a
/// season slightly behind the one the game was drawing. How much that costs depends entirely on
/// how fast the map is changing at the time of year: through midsummer, next to nothing;
/// through the autumn turn, about ten counts of red, which is what the owner measured as
/// distant ground being 5 to 6 percent too red against vanilla in three separate screenshots.
///
/// So the map is read directly here rather than the engine's answer being adjusted. That also
/// retires the algebraic recovery this replaces - the one that divided two nearly equal 8-bit
/// colours by a blend weight, and whose failure made distant colour disappear in whole areas
/// (0.3.176).
/// </summary>
public static class LodSeasonColor
{
    /// <summary>
    /// The largest the shader's column jitter can be: b is two value noises, so at most 2, and
    /// the shader divides it by 40. A static check holds this against colormap.vsh.
    /// </summary>
    public const float MaxColumnJitter = 2f / 40f;

    /// <summary>
    /// How many points the jitter is integrated over. The colour is not linear in the column,
    /// so the average colour is not the colour at the average column - at the owner's time of
    /// year those differ by about 4 counts of red. Nine points with triangular weights spans
    /// the range finely enough that doubling it moves the answer by under a count.
    /// </summary>
    public const int JitterSamples = 9;

    /// <summary>
    /// The row-averaged, jitter-averaged season colour, in 0..255.
    ///
    /// Rows are averaged flat. The shader picks its row from clamped noise rather than
    /// uniformly, which favours the two end rows, but measured against this map that is worth
    /// under 4 counts on any column - far below the column offset this exists to correct, and
    /// not worth reproducing a noise distribution for.
    /// </summary>
    public static bool TryAverage(int[]? pixels, int width, int height, int padding,
        float seasonRel, out float red, out float green, out float blue)
    {
        red = green = blue = 0f;
        int innerWidth = width - padding;
        int innerHeight = height - 2 * padding;
        if (pixels == null || innerWidth <= 0 || innerHeight <= 0
            || pixels.Length < width * height) return false;

        double r = 0, g = 0, b = 0;
        double weightSum = 0;

        for (int j = 0; j < JitterSamples; j++)
        {
            float t = JitterSamples == 1 ? 0.5f : j / (JitterSamples - 1f);
            float jitter = t * MaxColumnJitter;

            // Triangular over the range: b is the sum of two independent noises, so its middle
            // is far more likely than either end. The exact shape barely matters - a flat
            // weighting moves the result by well under a count.
            double weight = 1.0 - Math.Abs(t - 0.5) * 2.0 + 1e-3;

            // The engine's own column arithmetic: wrap into the year, scale, truncate.
            float rel = seasonRel + jitter;
            rel -= MathF.Floor(rel);
            rel = Math.Clamp(rel, 0.01f, 0.99f);
            int column = Math.Clamp((int)(rel * innerWidth), 0, innerWidth - 1);

            for (int row = 0; row < innerHeight; row++)
            {
                int pixel = pixels[(row + padding) * width + column + padding];
                // Stored red-low, like every colour map; the caller wants ordinary channels.
                r += weight * (pixel & 0xFF);
                g += weight * ((pixel >> 8) & 0xFF);
                b += weight * ((pixel >> 16) & 0xFF);
                weightSum += weight;
            }
        }

        if (weightSum <= 0) return false;
        red = (float)(r / weightSum);
        green = (float)(g / weightSum);
        blue = (float)(b / weightSum);
        return true;
    }
}
