using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// Who this mod is on disk, on the network and in chat, and what it does about the name it used
/// to have.
///
/// The fork began as AliasFactory's Vintage Horizons (mod id <c>vintagehorizons</c>) and was
/// renamed Doden's Landscapes (<c>dodenslandscapes</c>) for its public release on 2026-10-07.
/// Two consequences outlive the rename:
/// <list type="bullet">
/// <item>The original mod registers the same chat commands, Harmony patches and draw passes under
/// the same code, so the two cannot run together. When it is switched on, every system here
/// declines to load (<see cref="OriginalModBlocks"/>) and one notice says why.</item>
/// <item>A player of the fork's earlier builds has caches and settings under the old id. They are
/// moved once, before anything opens them (<see cref="TakeOverLegacyData"/>); the owner chose a
/// move over a copy (7.8 GB on his machine).</item>
/// </list>
/// Internal formats keep their old names on purpose: the cache's database type code, the height
/// sample file headers, the game start-up file's marker and its backup suffix. Renaming those
/// would make the new build refuse files the old one wrote.
/// </summary>
public static class ModIdentity
{
    public const string ModId = "dodenslandscapes";
    public const string DisplayName = "Doden's Landscapes";
    public const string LegacyModId = "vintagehorizons";

    static readonly object migrationGate = new();
    static bool migrated;

    /// <summary>True when the original Vintage Horizons is switched on: this mod must not load.</summary>
    public static bool OriginalModBlocks(ICoreAPI api) => api.ModLoader.IsModEnabled(LegacyModId);

    /// <summary>
    /// Moves the earlier builds' caches and settings to this id's names, once per process and
    /// before either side opens them. In singleplayer the client and the server both call this;
    /// the gate makes the second wait for the first and then do nothing.
    /// </summary>
    public static void TakeOverLegacyData(ICoreAPI api, ILogger log)
    {
        lock (migrationGate)
        {
            if (migrated) return;
            migrated = true;
            MoveLegacyData(api.DataBasePath, log);
        }
    }

    /// <summary>The move itself, for one game data folder. Separate from the gate so checks can drive it.</summary>
    internal static void MoveLegacyData(string dataBasePath, ILogger log)
    {
        try
        {
            Move(log, Path.Combine(dataBasePath, "ModData", LegacyModId),
                Path.Combine(dataBasePath, "ModData", ModId));
            string configs = Path.Combine(dataBasePath, "ModConfig");
            MoveFile(log, Path.Combine(configs, LegacyModId + ".json"), Path.Combine(configs, ModId + ".json"));
            MoveFile(log, Path.Combine(configs, LegacyModId + "-server.json"),
                Path.Combine(configs, ModId + "-server.json"));
        }
        catch (Exception e)
        {
            // Never stop the mod over this: it then starts with fresh caches, and the old files
            // stay where they were for a later start to pick up.
            log.Warning("Could not take over the caches and settings of the mod's earlier name "
                + "'{0}'; starting with whatever '{1}' already holds. {2}", LegacyModId, ModId, e.Message);
        }
    }

    static void Move(ILogger log, string from, string to)
    {
        if (!Directory.Exists(from)) return;
        if (!Directory.Exists(to))
        {
            Directory.Move(from, to);
            log.Notification("Moved this mod's caches from {0} to {1} (the mod was renamed {2}).",
                from, to, DisplayName);
            return;
        }

        // Both exist (an earlier take-over was interrupted): bring over only what is missing, so a
        // newer file under the new name is never replaced by an older one.
        int moved = 0, kept = 0;
        foreach (string file in Directory.GetFiles(from))
        {
            string target = Path.Combine(to, Path.GetFileName(file));
            if (File.Exists(target))
            {
                kept++;
                continue;
            }
            File.Move(file, target);
            moved++;
        }
        log.Notification("Moved {0} cache files from {1} to {2}; {3} left behind because {2} already "
            + "has newer files of the same name.", moved, from, to, kept);
        if (kept == 0 && Directory.GetFileSystemEntries(from).Length == 0) Directory.Delete(from);
    }

    static void MoveFile(ILogger log, string from, string to)
    {
        if (!File.Exists(from) || File.Exists(to)) return;
        File.Move(from, to);
        log.Notification("Moved this mod's settings from {0} to {1}.", from, to);
    }
}

/// <summary>
/// Loads only when the original Vintage Horizons is switched on, and says once why nothing else
/// from this mod is running.
/// </summary>
public sealed class OriginalModNotice : ModSystem
{
    public override bool ShouldLoad(ICoreAPI api) => ModIdentity.OriginalModBlocks(api);

    public override void Start(ICoreAPI api)
    {
        api.Logger.Error("[Landscapes] {0} is not running: the original Vintage Horizons ('{1}') is "
            + "switched on, and the two share commands and code. Switch one of them off and restart.",
            ModIdentity.DisplayName, ModIdentity.LegacyModId);
    }

    public override void StartClientSide(Vintagestory.API.Client.ICoreClientAPI api)
    {
        api.Event.LevelFinalize += () => api.ShowChatMessage(
            "[Landscapes] Doden's Landscapes is not running: the original Vintage Horizons is switched "
            + "on, and the two cannot run together. Switch one of them off in the Mod Manager and "
            + "restart the game.");
    }
}
