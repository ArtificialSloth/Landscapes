using System.Text.RegularExpressions;

namespace VintageHorizons.Checks;

/// <summary>
/// The 2026-10-07 rename to Doden's Landscapes. The mod never touches anything under the old id,
/// which is also the original Vintage Horizons' own folder (pre-release review B1, removed in
/// 0.4.160); and the formats that outlive the rename keep their old names, or the new build would
/// refuse caches the old one wrote.
/// </summary>
public static class ModIdentityChecks
{
    public static void Run(Check c)
    {
        NeverTouchesTheOriginalModsData(c);
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

    /// <summary>
    /// Builds 0.4.138-0.4.159 moved ModData/vintagehorizons and its settings into this mod's names
    /// at start-up. That folder is also the original Vintage Horizons' own, so the move could take
    /// over (and, through the cache format purge, wipe) another mod's data. Nothing may build a
    /// path from the old id again: its only use is naming the original on the login notice.
    /// </summary>
    static void NeverTouchesTheOriginalModsData(Check c)
    {
        string src = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src");
        var uses = new List<string>();
        foreach (string file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            string text = File.ReadAllText(file);
            if (name != "ModIdentity.cs" && text.Contains("\"vintagehorizons\"", StringComparison.Ordinal))
                uses.Add(name + " spells out the old id");
            if (name != "ModIdentity.cs" && name != "OtherLodMods.cs"
                && text.Contains("LegacyModId", StringComparison.Ordinal))
                uses.Add(name + " uses LegacyModId");
            if (text.Contains("TakeOverLegacyData", StringComparison.Ordinal)
                || text.Contains("MoveLegacyData", StringComparison.Ordinal))
                uses.Add(name + " still calls the old-data move");
        }
        c.Eq("", string.Join("; ", uses),
            "the old id only names the original Vintage Horizons on the login notice; no file is moved");

        string all = string.Join("\n", Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
        c.Eq(3, Regex.Matches(all, @": ModSystem\b").Count,
            "three mod systems, all of them loading whatever else is installed (owner, 2026-10-10)");
        c.True(OtherLodMods.Known.Any(known => known.ModId == ModIdentity.LegacyModId),
            "the original is still on the list of other LOD mods the login notice names");
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
