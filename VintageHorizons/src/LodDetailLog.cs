using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// Where a line that only a diagnosis needs goes. A session used to put about half of a player's
/// client-main.log and a quarter of their server-main.log here, which buried other mods' lines and
/// made logs too big to paste. These lines still reach a file with no setup, because chat cannot be
/// copied: the game's client-debug.log and server-debug.log, beside the main logs, archived with
/// them. Warnings, errors and the handful of lines that say what a session is running stay in the
/// main log. The bench and check scripts set VINTAGEHORIZONS_STATS=1 and read the main log, so with
/// it set these stay there.
/// </summary>
internal static class LodDetailLog
{
    static readonly EnumLogType Level =
        Environment.GetEnvironmentVariable("VINTAGEHORIZONS_STATS") == "1"
            ? EnumLogType.Notification
            : EnumLogType.Debug;

    public static void Detail(this ILogger logger, string format, params object[] args) =>
        logger.Log(Level, format, args);
}
