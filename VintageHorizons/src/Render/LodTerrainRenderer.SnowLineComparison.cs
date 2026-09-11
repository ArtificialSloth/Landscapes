namespace VintageHorizons;

/// <summary>
/// `.vhsnowline`: runtime switches that each undo one part of the 0.4.40 spatial snow line.
///
/// <para>
/// The owner bisected the vanilla-to-cache handoff flash to 0.4.40 (0.4.39 clean, 0.4.40
/// flashing, 0.4.41 milder than 0.4.50). That build changed only the snow line: every cell of
/// the climate field gained its own threshold, computed with two climate lookups per cell per
/// refresh; the texture's alpha byte started carrying it; and the terrain shader started
/// reading it. 0.4.42 then refreshed the field whenever observed snow arrived. Each of those is
/// a switch here, so one session can say which part the flash follows. `fetch` (0.4.52) puts
/// the vertex lookup back where 0.4.39 had it, the one difference the first four left in place.
/// </para>
/// <para>
/// Session-only and not saved. Every change is written to the client log as well as chat (G72).
/// </para>
/// </summary>
public partial class LodTerrainRenderer
{
    /// <summary>
    /// Per-cell thresholds. Off: every cell takes the player-local threshold computed once per
    /// refresh, which removes the per-cell climate lookups entirely.
    /// </summary>
    public bool SnowLinePerCell { get; private set; } = true;

    /// <summary>Terrain reads its threshold from the climate texture. Off: from the single snowLineY uniform, as 0.4.39 did.</summary>
    public bool SnowLineFromField { get; private set; } = true;

    /// <summary>Refresh early when new observed snow arrives (0.4.42 onward). Off: the 30-second cadence only.</summary>
    public bool SnowLineEarlyRefresh { get; private set; } = true;

    /// <summary>Every vertex reads the climate field (0.4.40 onward). Off: only a vertex tinted from it does, as in 0.4.39.</summary>
    public bool SnowLineFetchAll { get; private set; } = true;

    /// <summary>The climate texture's alpha byte carries the threshold. Off: a constant 255, as 0.4.39's texels did.</summary>
    public bool SnowLineInTexture => climateField.EncodeSnowLine;

    public string SetSnowLineMode(string part, bool on)
    {
        switch (part)
        {
            case "cells":
                SnowLinePerCell = on;
                break;
            case "shader":
                SnowLineFromField = on;
                break;
            case "alpha":
                climateField.EncodeSnowLine = on;
                break;
            case "early":
                SnowLineEarlyRefresh = on;
                break;
            case "fetch":
                SnowLineFetchAll = on;
                break;
            case "all":
                SnowLinePerCell = on;
                SnowLineFromField = on;
                SnowLineEarlyRefresh = on;
                climateField.EncodeSnowLine = on;
                SnowLineFetchAll = on;
                break;
            default:
                return "[VintageHorizons] " + DescribeSnowLineModes();
        }

        // Cells and alpha only change what the next publish writes. Restart the refresh so the
        // comparison lands within a second or two instead of at the next 30-second cadence.
        if (part is "cells" or "alpha" or "all") RestartSeasonalRefresh();

        string line = "[VintageHorizons] snow line " + part + " " + OnOff(on)
            + " at frame " + frameCounter + ". " + DescribeSnowLineModes();
        capi.Logger.Notification("{0}", line);
        return line;
    }

    public string DescribeSnowLineModes() =>
        "Snow line: cells " + OnOff(SnowLinePerCell) + ", shader " + OnOff(SnowLineFromField)
        + ", alpha " + OnOff(climateField.EncodeSnowLine) + ", early " + OnOff(SnowLineEarlyRefresh)
        + ", fetch " + OnOff(SnowLineFetchAll)
        + ". All on is the current design; all off is 0.4.39's single player-local threshold.";

    static string OnOff(bool value) => value ? "on" : "off";

    void RestartSeasonalRefresh()
    {
        seasonalRefreshActive = false;
        lastSeasonRefreshMs = long.MinValue / 4;
    }
}
