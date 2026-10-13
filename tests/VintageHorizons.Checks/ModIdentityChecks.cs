using System.Text.RegularExpressions;

namespace VintageHorizons.Checks;

/// <summary>
/// The 2026-10-07 rename to Doden's Landscapes. The earlier builds' caches and settings must be
/// moved under the new id exactly once, never over newer files, and never while the original
/// Vintage Horizons is switched on and still using them; and the formats that outlive the rename
/// keep their old names, or the new build would refuse caches the old one wrote.
/// </summary>
public static class ModIdentityChecks
{
    public static void Run(Check c)
    {
        MovesTheWholeFolderWhenNothingIsThere(c);
        MergesWithoutReplacingNewerFiles(c);
        LeavesAFreshInstallAlone(c);
        EverySystemTakesOverTheOldDataSafely(c);
        OldFormatsKeepTheirNames(c);
        LicenceFilesShipWithEveryCopy(c);
    }

    /// <summary>
    /// The owner's licence (2026-10-11): GPL-3 only, with a Vintage Story exception, plus the MIT
    /// notices of AliasFactory's Vintage Horizons and Badgerson's Farseer, which those licences
    /// require in every copy. 0.1.0 once shipped without its licence at all.
    /// </summary>
    static void LicenceFilesShipWithEveryCopy(Check c)
    {
        string root = GameAssemblies.RepoRoot;
        string licence = File.ReadAllText(Path.Combine(root, "LICENSE"));
        c.True(licence.Contains("GNU GENERAL PUBLIC LICENSE", StringComparison.Ordinal)
                && licence.Contains("Version 3, 29 June 2007", StringComparison.Ordinal),
            "LICENSE is the GPL version 3 text");
        string notice = File.ReadAllText(Path.Combine(root, "NOTICE"));
        c.True(notice.Contains("Copyright (C) 2026 Doden", StringComparison.Ordinal), "NOTICE names the copyright holder");
        c.True(notice.Contains("version 3 only, not any later", StringComparison.Ordinal), "the licence is GPL-3 only");
        c.True(notice.Contains("Additional permission under GNU GPL version 3 section 7", StringComparison.Ordinal)
                && notice.Contains("Vintage Story", StringComparison.Ordinal),
            "and carries the Vintage Story exception");
        string thirdParty = File.ReadAllText(Path.Combine(root, "THIRD-PARTY-NOTICES"));
        c.True(thirdParty.Contains("Copyright (c) 2026 AliasFactory", StringComparison.Ordinal)
                && thirdParty.Contains("Copyright 2026 Badgerson", StringComparison.Ordinal)
                && Regex.Matches(thirdParty, "shall be included in all").Count == 2,
            "both MIT notices are reproduced in full");
        string project = File.ReadAllText(Path.Combine(root, "VintageHorizons", "VintageHorizons.csproj"));
        c.True(project.Contains("../LICENSE;../NOTICE;../THIRD-PARTY-NOTICES", StringComparison.Ordinal),
            "all three are copied into the mod folder that becomes the zip");
    }

    static string NewFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-identity-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    static void MovesTheWholeFolderWhenNothingIsThere(Check c)
    {
        string root = NewFolder();
        try
        {
            Write(Path.Combine(root, "ModData", "vintagehorizons", "world.db"), "cache");
            Write(Path.Combine(root, "ModData", "vintagehorizons", "world-server.db"), "server cache");
            Write(Path.Combine(root, "ModConfig", "vintagehorizons.json"), "{client}");
            Write(Path.Combine(root, "ModConfig", "vintagehorizons-server.json"), "{server}");

            ModIdentity.MoveLegacyData(root, new CaptureLogger());

            string data = Path.Combine(root, "ModData", "dodenslandscapes");
            c.Eq("cache", File.ReadAllText(Path.Combine(data, "world.db")), "the client cache moves to the new folder");
            c.Eq("server cache", File.ReadAllText(Path.Combine(data, "world-server.db")), "and so does the server cache");
            c.False(Directory.Exists(Path.Combine(root, "ModData", "vintagehorizons")), "the old folder is gone: a move, not a copy");
            c.Eq("{client}", File.ReadAllText(Path.Combine(root, "ModConfig", "dodenslandscapes.json")), "client settings move");
            c.Eq("{server}", File.ReadAllText(Path.Combine(root, "ModConfig", "dodenslandscapes-server.json")), "server settings move");
            c.False(File.Exists(Path.Combine(root, "ModConfig", "vintagehorizons.json")), "the old settings file is gone");

            ModIdentity.MoveLegacyData(root, new CaptureLogger());
            c.Eq("cache", File.ReadAllText(Path.Combine(data, "world.db")), "a second start changes nothing");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static void MergesWithoutReplacingNewerFiles(Check c)
    {
        string root = NewFolder();
        try
        {
            Write(Path.Combine(root, "ModData", "vintagehorizons", "a.db"), "old a");
            Write(Path.Combine(root, "ModData", "vintagehorizons", "b.db"), "old b");
            Write(Path.Combine(root, "ModData", "dodenslandscapes", "a.db"), "new a");
            Write(Path.Combine(root, "ModConfig", "vintagehorizons.json"), "{old}");
            Write(Path.Combine(root, "ModConfig", "dodenslandscapes.json"), "{new}");

            ModIdentity.MoveLegacyData(root, new CaptureLogger());

            string data = Path.Combine(root, "ModData", "dodenslandscapes");
            c.Eq("new a", File.ReadAllText(Path.Combine(data, "a.db")), "a file already under the new name is never replaced");
            c.Eq("old b", File.ReadAllText(Path.Combine(data, "b.db")), "a file only the old folder has is brought over");
            c.True(File.Exists(Path.Combine(root, "ModData", "vintagehorizons", "a.db")),
                "the older duplicate is left where it was, not deleted");
            c.Eq("{new}", File.ReadAllText(Path.Combine(root, "ModConfig", "dodenslandscapes.json")),
                "settings already under the new name are kept");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static void LeavesAFreshInstallAlone(Check c)
    {
        string root = NewFolder();
        try
        {
            var log = new CaptureLogger();
            ModIdentity.MoveLegacyData(root, log);
            c.False(Directory.Exists(Path.Combine(root, "ModData")), "nothing to take over creates nothing");
            c.False(Directory.Exists(Path.Combine(root, "ModConfig")), "not even an empty settings folder");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    static void EverySystemTakesOverTheOldDataSafely(Check c)
    {
        string src = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src");
        foreach (string file in new[] { "VintageHorizonsModSystem.cs", Path.Combine("Net", "LodServerCaptureSystem.cs"),
                     Path.Combine("Net", "LodAssistServerSystem.cs") })
        {
            string text = File.ReadAllText(Path.Combine(src, file));
            c.True(text.Contains("StartPre(ICoreAPI api) => ModIdentity.TakeOverLegacyData(api, Mod.Logger);",
                    StringComparison.Ordinal),
                $"{Path.GetFileName(file)} takes over the old data before anything opens it");
        }
        string all = string.Join("\n", Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
        c.Eq(3, Regex.Matches(all, @": ModSystem\b").Count,
            "three mod systems, all of them loading whatever else is installed (owner, 2026-10-10)");
        string identity = File.ReadAllText(Path.Combine(src, "ModIdentity.cs"));
        int skip = identity.IndexOf("if (api.ModLoader.IsModEnabled(LegacyModId))", StringComparison.Ordinal);
        int move = identity.IndexOf("MoveLegacyData(api.DataBasePath, log);", StringComparison.Ordinal);
        c.True(skip >= 0 && move > skip,
            "the old data is not moved while the original Vintage Horizons is switched on and still using it");
    }

    static void OldFormatsKeepTheirNames(Check c)
    {
        string src = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src");
        string store = File.ReadAllText(Path.Combine(src, "Storage", "LodStore.cs"));
        c.True(store.Contains("\"vintagehorizons lod cache\"", StringComparison.Ordinal),
            "the cache's database type code keeps the old name, so moved caches still open");
        string collector = File.ReadAllText(Path.Combine(src, "Gui", "GameCollectorSetting.cs"));
        c.True(collector.Contains("\"VintageHorizons.AllCoresCollector\"", StringComparison.Ordinal)
                && collector.Contains("\".vintagehorizons-backup\"", StringComparison.Ordinal),
            "the game start-up file's marker and backup keep the old names, so the switch still recognises its own edit");
        string modinfo = File.ReadAllText(Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "modinfo.json"));
        c.True(modinfo.Contains("\"modid\": \"dodenslandscapes\"", StringComparison.Ordinal), "the mod id is the new one");
        c.True(Directory.Exists(Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "assets", "dodenslandscapes")),
            "the assets live under the new id, the domain the game gives the mod");
    }
}
