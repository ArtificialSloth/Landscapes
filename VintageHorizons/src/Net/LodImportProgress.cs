namespace VintageHorizons.Net;

/// <summary>
/// How much explored land the savegame sweep still has to import, by area, so each player can be
/// told about the part near them (owner, 2026-10-21: everyone affected sees the popup, but nobody
/// is bothered about land outside their own Cache distance).
///
/// Counted in cells of 16 by 16 chunk columns, 512 blocks a side. A column is counted when the
/// load pass starts, unless the cache already proved it holds that column, and uncounted when the
/// pass reaches it. Frontier columns are counted until then too, so the figure runs a few percent
/// high - it is an "about", and says so wherever it is shown. Game-free so it can be checked
/// without a game process; owned by the server thread.
/// </summary>
internal sealed class LodImportTally
{
    /// <summary>Columns per cell side: 16 chunks of 32 blocks, 512 blocks.</summary>
    public const int CellColumns = 16;

    readonly Dictionary<long, int> cells = new();

    /// <summary>Columns still to import, across the whole world.</summary>
    public int Pending { get; private set; }

    /// <summary>Columns counted when the pass started.</summary>
    public int Started { get; private set; }

    public void Clear()
    {
        cells.Clear();
        Pending = 0;
        Started = 0;
    }

    public void Add(long columnKey)
    {
        long cell = CellOf(columnKey);
        cells[cell] = cells.TryGetValue(cell, out int n) ? n + 1 : 1;
        Pending++;
        Started++;
    }

    /// <summary>The pass reached this column. Ignores a column that was never counted.</summary>
    public void Remove(long columnKey)
    {
        long cell = CellOf(columnKey);
        if (!cells.TryGetValue(cell, out int n)) return;
        if (n <= 1) cells.Remove(cell);
        else cells[cell] = n - 1;
        Pending--;
    }

    /// <summary>
    /// Columns still to import by distance from a player, in bands of
    /// <see cref="LodImportStatusBands.BandBlocks"/>. The last band holds everything further out,
    /// for a player whose Cache distance is unlimited. Distance is to the middle of each cell, so a
    /// column can land one band either side of where it really is.
    /// </summary>
    public int[] Histogram(double playerX, double playerZ)
    {
        var bands = new int[LodImportStatusBands.Count];
        const double cellBlocks = CellColumns * 32.0;
        foreach ((long cell, int count) in cells)
        {
            double cx = (LodColumnMap.KeyCx(cell) + 0.5) * cellBlocks;
            double cz = (LodColumnMap.KeyCz(cell) + 0.5) * cellBlocks;
            double dx = cx - playerX, dz = cz - playerZ;
            int band = (int)(Math.Sqrt(dx * dx + dz * dz) / LodImportStatusBands.BandBlocks);
            bands[Math.Min(band, bands.Length - 1)] += count;
        }
        return bands;
    }

    static long CellOf(long columnKey) => LodColumnMap.Key(
        FloorDiv(LodColumnMap.KeyCx(columnKey), CellColumns),
        FloorDiv(LodColumnMap.KeyCz(columnKey), CellColumns));

    static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }
}

/// <summary>The distance bands an import status is reported in, shared by both sides.</summary>
public static class LodImportStatusBands
{
    /// <summary>Band width in blocks: one tally cell.</summary>
    public const int BandBlocks = LodImportTally.CellColumns * 32;

    /// <summary>
    /// 64 bands reach 32,768 blocks, the settings window's largest Cache distance; one more holds
    /// everything beyond, for an unlimited Cache distance.
    /// </summary>
    public const int Count = 65;
}

/// <summary>
/// The sweep's recent import pace, in columns loaded per second, over the last two minutes. The
/// pace is set by how fast this machine captures terrain and by whether players are moving
/// (the sweep slows to a floor while they are), neither of which is known in advance, so the
/// estimate is measured rather than predicted. Game-free; owned by the server thread.
/// </summary>
internal sealed class LodRateMeter
{
    const long SampleMs = 5_000;
    const int Samples = 24;            // two minutes
    const long MinimumWindowMs = 30_000;

    readonly long[] times = new long[Samples];
    readonly long[] counts = new long[Samples];
    int next;
    int filled;

    /// <summary>Records the running total; takes one sample every five seconds.</summary>
    public void Note(long nowMs, long total)
    {
        if (filled > 0 && nowMs - times[(next + Samples - 1) % Samples] < SampleMs) return;
        times[next] = nowMs;
        counts[next] = total;
        next = (next + 1) % Samples;
        if (filled < Samples) filled++;
    }

    /// <summary>Columns per second over the window, or 0 until 30 seconds have been measured.</summary>
    public double PerSecond
    {
        get
        {
            if (filled < 2) return 0;
            int newest = (next + Samples - 1) % Samples;
            int oldest = filled < Samples ? 0 : next;
            long ms = times[newest] - times[oldest];
            if (ms < MinimumWindowMs) return 0;
            return Math.Max(0, counts[newest] - counts[oldest]) * 1000.0 / ms;
        }
    }

    public void Clear()
    {
        next = 0;
        filled = 0;
    }
}

/// <summary>
/// What one player is told about the import, decided from the server's report and the player's own
/// Cache distance. Game-free so the thresholds can be checked offline.
/// </summary>
public static class LodImportView
{
    /// <summary>
    /// The pace assumed until the server has measured one. Eight columns a second is what the
    /// owner's machine managed with nobody moving (2026-10-21 sandbox run); the real figure
    /// replaces it within the first minute.
    /// </summary>
    public const double AssumedColumnsPerSecond = 8;

    /// <summary>The popup appears only for an import expected to take at least this long nearby.</summary>
    public const double PopupSeconds = 10 * 60;

    /// <summary>The overlay appears only for an import expected to take at least this long nearby.</summary>
    public const double OverlaySeconds = 60;

    /// <summary>
    /// Columns still to import within <paramref name="cacheBlocks"/> of the player; 0 or less
    /// means unlimited. A band counts when it starts inside the distance, the last one included:
    /// `.dlrange` allows distances past the settings window's 32,768.
    /// </summary>
    public static int PendingWithin(int[]? bands, int cacheBlocks)
    {
        if (bands == null) return 0;
        int total = 0;
        for (int i = 0; i < bands.Length; i++)
        {
            if (cacheBlocks > 0 && (long)i * LodImportStatusBands.BandBlocks >= cacheBlocks) break;
            total += bands[i];
        }
        return total;
    }

    /// <summary>The pace to estimate with: the server's measurement, or the assumed one.</summary>
    public static double Pace(double measured) => measured > 0 ? measured : AssumedColumnsPerSecond;

    /// <summary>Seconds to import <paramref name="columns"/> at <paramref name="pace"/>.</summary>
    public static double SecondsFor(int columns, double pace) => columns / Math.Max(0.1, pace);

    /// <summary>"about 1 h 20 min", "about 25 min", "under a minute".</summary>
    public static string Describe(double seconds)
    {
        if (seconds < 60) return "under a minute";
        long minutes = (long)Math.Round(seconds / 60);
        if (minutes < 60) return $"about {minutes} min";
        // Past an hour, five-minute steps: the pace moves too much for minutes to mean anything.
        long rounded = (long)Math.Round(seconds / 300) * 5;
        long hours = rounded / 60, rest = rounded % 60;
        return rest == 0 ? $"about {hours} h" : $"about {hours} h {rest} min";
    }
}

/// <summary>
/// What the player sees of the import, one step a second: the faint overlay's line and how faint,
/// and whether the popup is due. Game-free so the rules can be checked offline.
///
/// The overlay appears once the land left nearby would take a minute or more, stays until that
/// land is done, says so for a few seconds and then fades out. The popup appears once a session,
/// the first time what is left nearby would take ten minutes or more. Progress is measured against
/// the most this session has seen nearby, so it starts at 0% when the player joins.
/// </summary>
public sealed class LodImportPresenter
{
    /// <summary>How long "imported" stays up, then how long it takes to fade out.</summary>
    public const long DoneHoldMs = 8_000;
    public const long FadeMs = 2_000;

    /// <summary>Full overlay opacity: faint on purpose, a ghost over the game rather than a panel.</summary>
    public const float GhostAlpha = 0.55f;

    int baseline;
    bool overlayShown;
    long doneSinceMs = -1;

    /// <summary>The overlay's line, or null when it is hidden.</summary>
    public string? OverlayText { get; private set; }

    /// <summary>The overlay's opacity, <see cref="GhostAlpha"/> at most.</summary>
    public float OverlayAlpha { get; private set; }

    /// <summary>True from the step the popup becomes due until <see cref="PopupShown"/> is called.</summary>
    public bool PopupDue { get; private set; }
    bool popupDone;

    /// <summary>Columns still to import nearby at the last step.</summary>
    public int PendingNearby { get; private set; }

    /// <summary>Seconds those would take at the last step's pace.</summary>
    public double SecondsNearby { get; private set; }

    /// <summary>True when the last step's pace was the server's measurement rather than assumed.</summary>
    public bool PaceMeasured { get; private set; }

    public void PopupShown()
    {
        PopupDue = false;
        popupDone = true;
    }

    /// <param name="status">The newest status, or null when none has arrived.</param>
    /// <param name="cacheBlocks">The player's Cache distance; 0 or less is unlimited.</param>
    public void Step(AssistImportStatus? status, int cacheBlocks, long nowMs)
    {
        if (status == null)
        {
            Hide();
            return;
        }

        PendingNearby = status.Finished ? 0 : PendingWithin(status, cacheBlocks);
        PaceMeasured = status.ColumnsPerSecond > 0;
        SecondsNearby = LodImportView.SecondsFor(PendingNearby,
            LodImportView.Pace(status.ColumnsPerSecond));

        if (PendingNearby <= 0)
        {
            StepDone(nowMs);
            return;
        }

        doneSinceMs = -1;
        baseline = Math.Max(baseline, PendingNearby);
        if (!popupDone && SecondsNearby >= LodImportView.PopupSeconds) PopupDue = true;
        if (!overlayShown && SecondsNearby < LodImportView.OverlaySeconds)
        {
            Hide();
            return;
        }

        overlayShown = true;
        int percent = (int)((long)(baseline - PendingNearby) * 100 / baseline);
        OverlayText = $"Importing explored land: {percent}% · "
            + (PaceMeasured
                ? LodImportView.Describe(SecondsNearby) + " left"
                : "working out the time left");
        OverlayAlpha = GhostAlpha;
    }

    static int PendingWithin(AssistImportStatus status, int cacheBlocks) =>
        LodImportView.PendingWithin(status.PendingByBand, cacheBlocks);

    void StepDone(long nowMs)
    {
        PopupDue = false;
        if (!overlayShown)
        {
            Hide();
            return;
        }

        if (doneSinceMs < 0) doneSinceMs = nowMs;
        long shown = nowMs - doneSinceMs;
        if (shown >= DoneHoldMs + FadeMs)
        {
            // Gone until the player meets more land still to import, which starts it afresh.
            overlayShown = false;
            baseline = 0;
            doneSinceMs = -1;
            Hide();
            return;
        }

        OverlayText = "Explored land nearby is imported";
        OverlayAlpha = shown <= DoneHoldMs
            ? GhostAlpha
            : GhostAlpha * (1f - (float)(shown - DoneHoldMs) / FadeMs);
    }

    void Hide()
    {
        OverlayText = null;
        OverlayAlpha = 0;
    }
}
