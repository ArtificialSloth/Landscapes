using System.Globalization;
using VintageHorizons.Net;

namespace VintageHorizons;

/// <summary>Remembered client-side choices for the server-owned Generate page.</summary>
internal readonly record struct LodWorldGenPreferences(
    int RadiusChunks, EnumWorldGenShape Shape, EnumWorldGenCentreMode CentreMode,
    int X, int Z, string Preset)
{
    internal static LodWorldGenPreferences Default => new(
        32, LodWorldGenPlan.DefaultShape, EnumWorldGenCentreMode.Here, 0, 0, "Balanced");

    internal LodWorldGenPreferences Normalize(int maxRadius)
    {
        int ceiling = Math.Max(1, maxRadius);
        return new LodWorldGenPreferences(
            Math.Clamp(RadiusChunks, 1, ceiling),
            Enum.IsDefined(Shape) ? Shape : LodWorldGenPlan.DefaultShape,
            Enum.IsDefined(CentreMode) ? CentreMode : EnumWorldGenCentreMode.Here,
            Math.Max(0, X), Math.Max(0, Z), "Balanced");
    }
}

/// <summary>Generate-page state and actions injected into both Settings entry points.</summary>
internal sealed record WorldGenTuning(
    LodWorldGenPreferences Initial,
    LodAssistClient? Client,
    Action<LodWorldGenPreferences> Save);

/// <summary>Pure estimate/format helpers shared by the page and its checks.</summary>
internal static class LodWorldGenPageMath
{
    internal static long ColumnCount(int radius, EnumWorldGenShape shape)
    {
        radius = Math.Max(0, radius);
        if (shape == EnumWorldGenShape.Square)
        {
            long width = 2L * radius + 1;
            return width * width;
        }

        long count = 0;
        long radiusSq = (long)radius * radius;
        for (int dx = -radius; dx <= radius; dx++)
        {
            int halfWidth = (int)Math.Floor(Math.Sqrt(radiusSq - (long)dx * dx));
            count += 2L * halfWidth + 1;
        }
        return count;
    }

    internal static string Estimate(LodWorldGenPreferences value)
    {
        long columns = ColumnCount(value.RadiusChunks, value.Shape);
        long bytes = columns * LodWorldGenJob.EstimatedTotalBytesPerColumn;
        long seconds = (long)Math.Ceiling(columns / LodWorldGenJob.BaselineColumnsPerSecond);
        return $"About {columns:N0} columns, {FormatBytes(bytes)} disk, "
            + $"and {FormatDuration(seconds)} at the measured one-thread baseline.";
    }

    internal static string Progress(AssistWorldGenStatus? status)
    {
        if (status == null) return "Waiting for server status.";
        if (status.TotalColumns <= 0) return status.Message;

        int percent = status.DoneColumns * 100 / Math.Max(1, status.TotalColumns);
        string eta = status.EtaSeconds >= 0 ? FormatDuration(status.EtaSeconds) : "measuring";
        return string.Format(CultureInfo.InvariantCulture,
            "{0:N0}/{1:N0} columns ({2}%), {3:0.0}/s, ETA {4}. "
            + "Disk +{5} savegame, +{6} cache. Queue {7}/{8}, {9}/{10} kept; "
            + "{11} retries, {12} failures. {13}",
            status.DoneColumns, status.TotalColumns, percent, status.ColumnsPerSecond, eta,
            FormatBytes(status.SavegameBytes), FormatBytes(status.CacheBytes),
            status.EngineQueue, status.QueueTarget, status.InFlight, status.MaxInFlight,
            status.Retries, status.Failures,
            string.IsNullOrWhiteSpace(status.Gate) ? status.Message : status.Gate + ".");
    }

    internal static int Percent(AssistWorldGenStatus? status) => status?.TotalColumns > 0
        ? Math.Clamp(status.DoneColumns * 100 / status.TotalColumns, 0, 100)
        : 0;

    internal static string FormatBytes(long bytes)
    {
        double mib = Math.Max(0, bytes) / (1024.0 * 1024.0);
        return mib >= 1024
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} GiB", mib / 1024)
            : string.Format(CultureInfo.InvariantCulture, "{0:0.0} MiB", mib);
    }

    internal static string FormatDuration(long seconds)
    {
        if (seconds < 120) return Math.Max(0, seconds) + " seconds";
        if (seconds < 7200) return (seconds + 30) / 60 + " minutes";
        return string.Format(CultureInfo.InvariantCulture, "{0:0.0} hours", seconds / 3600.0);
    }
}
