using System.Globalization;

namespace VintageHorizons;

/// <summary>
/// The settings window's Anti-Aliasing sliders, one per <see cref="LodTaaDials"/> dial. Kept
/// apart from the window so the check tier can pin each slider to its dial without a GUI.
///
/// <para>
/// A slider holds whole numbers, so each dial is stored multiplied by its <c>Scale</c>: reach in
/// tenths of a pixel, the rest in hundredths. Every range comes from <see cref="LodTaaMath"/>,
/// and every default lands on one of its slider's steps, so opening the window and saving
/// without touching anything changes nothing. The titles are the player's words for the
/// `.dltaa` dials; each tip names the command that sets the same value.
/// </para>
/// </summary>
internal static class LodTaaSliders
{
    internal readonly record struct Row(
        string Key, string Title, string Tip, int Min, int Max, int Step, int Scale,
        string Format, string Suffix,
        Func<LodTaaDials, float> Read,
        Func<LodTaaDials, float, LodTaaDials> Write)
    {
        public int ToSlider(float value) => Math.Clamp((int)MathF.Round(value * Scale), Min, Max);

        public float FromSlider(int value) => value / (float)Scale;

        public string Label(float value) =>
            value.ToString(Format, CultureInfo.InvariantCulture) + Suffix;
    }

    static int At(float value, int scale) => (int)MathF.Round(value * scale);

    internal static readonly Row[] All =
    {
        new("taa-reach", "Reach",
            "Where anti-aliasing begins, as the width of one block on screen. Higher reaches "
            + "closer terrain; it is full strength where blocks are half as wide. Only cached "
            + "terrain is ever touched, never vanilla terrain. (.dltaa reach)",
            At(LodTaaMath.MinReachPixels, 10), At(LodTaaMath.MaxReachPixels, 10), 5, 10,
            "0.0", " px", d => d.Reach, (d, v) => d with { Reach = v }),

        new("taa-nudge", "Nudge",
            "How far distant terrain is moved each frame so it can be smoothed. Lower is "
            + "crisper, but the shimmer comes back. (.dltaa nudge)",
            0, 100, 5, 100, "0.00", "", d => d.Nudge, (d, v) => d with { Nudge = v }),

        new("taa-sharpen", "Sharpen",
            "Restores the contrast that smoothing takes away. It is applied only on the way to "
            + "the screen, so it cannot bring the shimmer back; too high looks crunchy. "
            + "(.dltaa sharpen)",
            0, 100, 5, 100, "0.00", "", d => d.Sharpen, (d, v) => d with { Sharpen = v }),

        new("taa-blend", "Memory While Turning",
            "How much of earlier frames is kept while the camera moves. Higher is steadier but "
            + "smears when you turn. (.dltaa blend)",
            0, At(LodTaaMath.MaxFeedback, 100), 1, 100, "0.00", "",
            d => d.Blend, (d, v) => d with { Blend = v }),

        new("taa-still", "Memory When Still",
            "How much of earlier frames is kept once the camera holds still. Higher is steadier; "
            + "lower settles faster after anything changes. (.dltaa still)",
            0, At(LodTaaMath.MaxStillFeedback, 100), 1, 100, "0.00", "",
            d => d.Still, (d, v) => d with { Still = v }),

        new("taa-clip", "Smear Guard",
            "How closely earlier frames must match the colors around each pixel now. Lower "
            + "smears less but flickers more. (.dltaa clip)",
            At(LodTaaMath.MinClip, 100), At(LodTaaMath.MaxClip, 100), 5, 100, "0.00", "",
            d => d.Clip, (d, v) => d with { Clip = v }),
    };
}
