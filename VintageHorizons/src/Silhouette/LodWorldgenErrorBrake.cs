using System.Globalization;
using Vintagestory.API.Common;

namespace VintageHorizons.Silhouette;

/// <summary>
/// Stops silhouette previews on a world whose generator reports errors while they run.
///
/// On the owner's bodanboys world, both stalls of the world-generation thread (0.4.77 and 0.4.80,
/// Session 93) came right after a burst of worldgen errors logged while previews were being
/// generated; test2 logged none and never stalled. The cause is not established, so this only
/// reads the symptom: error entries the server logs while the builder has previews waiting on the
/// generator. A few of those, and previews stop for the session before anything worse follows.
///
/// Log entries arrive on whatever thread wrote them, so the count is interlocked. The mod's own
/// lines never count. Pure, so the fast tier drives it without a game.
/// </summary>
public sealed class LodWorldgenErrorBrake
{
    /// <summary>Errors during previews that stop them. The owner's world logged 99 and 144.</summary>
    public const int DefaultThreshold = 3;

    const int MaxErrorTextLength = 240;

    readonly int threshold;
    readonly string? ownMarker;
    int previewsOutstanding;
    int errorsDuringPreviews;
    string? firstError;

    /// <param name="ownMarker">Text that marks the mod's own log lines, which never count.</param>
    public LodWorldgenErrorBrake(string? ownMarker, int threshold = DefaultThreshold)
    {
        this.ownMarker = string.IsNullOrEmpty(ownMarker) ? null : ownMarker;
        this.threshold = threshold;
    }

    /// <summary>Latched: set the first time the count reaches the threshold.</summary>
    public bool Tripped { get; private set; }

    public int ErrorsDuringPreviews => Volatile.Read(ref errorsDuringPreviews);

    /// <summary>The first counted error's text, shortened, for the log.</summary>
    public string? FirstError => Volatile.Read(ref firstError);

    /// <summary>Main thread: how many previews the generator still owes the builder.</summary>
    public void SetPreviewsOutstanding(int count) => Volatile.Write(ref previewsOutstanding, count);

    /// <summary>
    /// Any thread: one server log entry (the logger's EntryAdded). Counts an error or fatal entry
    /// written while previews are outstanding, unless it is one of the mod's own lines.
    /// </summary>
    public void Observe(EnumLogType logType, string message, params object[] args)
    {
        if (logType != EnumLogType.Error && logType != EnumLogType.Fatal) return;
        if (Volatile.Read(ref previewsOutstanding) <= 0) return;
        if (ownMarker != null && message != null && message.Contains(ownMarker, StringComparison.Ordinal))
            return;
        if (Interlocked.Increment(ref errorsDuringPreviews) == 1)
            Interlocked.CompareExchange(ref firstError, Shorten(message, args), null);
    }

    /// <summary>Main thread, once per builder tick. True only on the tick the brake trips.</summary>
    public bool Tick()
    {
        if (Tripped || ErrorsDuringPreviews < threshold) return false;
        Tripped = true;
        return true;
    }

    static string Shorten(string? message, object[]? args)
    {
        string text;
        try
        {
            text = message == null ? ""
                : args is { Length: > 0 } ? string.Format(CultureInfo.InvariantCulture, message, args)
                : message;
        }
        catch (FormatException)
        {
            text = message ?? "";
        }
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= MaxErrorTextLength ? text : text[..MaxErrorTextLength] + "...";
    }
}
