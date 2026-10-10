using System.Text.RegularExpressions;

namespace VintageHorizons.Checks;

/// <summary>
/// Other LOD mods (owner, 2026-10-10). This mod never steps aside for one; it names the listed
/// ones that are switched on, in the log and in a login notice that tells the player to keep only
/// one. A mod switched off in its own settings is not drawing and must not raise the notice, and
/// "cannot tell" counts as drawing, because a missed conflict is worse than a notice.
///
/// History: until 0.4.139 this mod went idle for them, and 0.2.0's version of that went idle for
/// a Farseer the player had switched off, leaving no distant terrain from either mod.
/// </summary>
public static class DeferralChecks
{
    public static void Run(Check c)
    {
        Table(c);
        Decisions(c);
        Reporting(c);
        SwitchFileShapes(c);
        NoticeWording(c);
        NeverStepsAside(c);
    }

    static void SwitchFileShapes(Check c)
    {
        // The default is what an empty or partial file leaves behind, and it has to be "on".
        c.True(new OtherLodModSwitch().Enabled, "an unspecified switch reads as on");
        c.False(new VintageHorizonsConfig().HideLodModNotice, "the login notice shows until the player hides it");

        c.Eq("farseer", Drawing(Only("farseer"), _ => true), "an explicit true is drawing");
        c.Eq("", Drawing(Only("farseer"), _ => false), "an explicit false is not");
        c.Eq("farseer", Drawing(Only("farseer"), _ => null), "a file that could not be read counts as drawing");
    }

    static void Table(Check c)
    {
        var ids = OtherLodMods.Known.Select(k => k.ModId).ToArray();
        foreach (string id in new[] { "farseer", "chunklod", "topohorizon", "distantvistas", "vintagehorizons" })
            c.True(ids.Contains(id), $"{id} is a known LOD mod");
        c.Eq(ids.Length, ids.Distinct().Count(), "no mod id is listed twice");
        c.False(ids.Contains(ModIdentity.ModId), "this mod is never on its own list");

        // Pinned as ABSENT, because it was present once, on a guess from the name. Vistas
        // Beyond is a server-side worldgen config mod and draws nothing; a wrong entry now
        // costs the player a false notice at every login.
        c.False(ids.Contains("vistasbeyond"), "vistasbeyond does not draw, so it is not listed");

        c.Eq("farseer-client.json", OtherLodMods.Known.First(k => k.ModId == "farseer").SwitchFile,
            "farseer's switch is read from the file its own dialog writes");
        foreach (string id in new[] { "chunklod", "topohorizon", "distantvistas", "vintagehorizons" })
            c.True(OtherLodMods.Known.First(k => k.ModId == id).SwitchFile is null, $"{id} has no switch file we can read");
    }

    static void Decisions(Check c)
    {
        c.Eq("", Drawing(None, AllOn), "nothing installed means nothing to name");
        c.Eq("farseer", Drawing(Only("farseer"), AllOn), "an installed and switched-on mod is named");
        c.Eq("", Drawing(Only("farseer"), AllOff), "an installed mod that is switched off is not");
        c.Eq("farseer", Drawing(Only("farseer"), _ => null), "a missing config file counts as switched on");
        c.Eq("chunklod", Drawing(Only("chunklod"), AllOff), "a mod with no readable switch is named however the others are set");
        c.Eq("chunklod,distantvistas",
            Drawing(name => name is "farseer" or "chunklod" or "distantvistas",
                file => file == "farseer-client.json" ? false : null),
            "every mod still drawing is named, not just the first, and a switched-off one is left out");

        var asked = new List<string>();
        OtherLodMods.Inspect(Only("farseer"), file => { asked.Add(file); return true; });
        c.Eq(1, asked.Count, "exactly one switch file is read when one mod is installed");
        c.Eq("farseer-client.json", asked.FirstOrDefault(), "and it is that mod's own file");

        var askedNone = new List<string>();
        OtherLodMods.Inspect(None, file => { askedNone.Add(file); return false; });
        c.Eq(0, askedNone.Count, "no switch file is read when no LOD mod is installed");
    }

    static void Reporting(Check c)
    {
        (string[] drawing, string[] off) = OtherLodMods.Inspect(Only("farseer"), AllOff);
        c.Eq(0, drawing.Length, "the switched-off mod is not drawing");
        c.Eq("farseer", off.FirstOrDefault(), "and it is reported as switched off");

        (_, string[] unknownOff) = OtherLodMods.Inspect(Only("chunklod"), AllOff);
        c.Eq(0, unknownOff.Length, "a mod with no readable switch is never reported as off");
    }

    static void NoticeWording(Check c)
    {
        c.Eq("Farseer is switched on. It also draws distant terrain, so the two overlap: the horizon "
            + "flickers, and you pay the frame-rate cost twice.\n\nDoden's Landscapes keeps drawing. For the "
            + "best picture, switch Farseer off in the Mod Manager and restart the game.",
            LodModNotice.ConflictText(new[] { "Farseer" }), "one mod: the owner's approved wording");
        string two = LodModNotice.ConflictText(new[] { "Farseer", "Distant Vistas" });
        c.True(two.StartsWith("Farseer and Distant Vistas are switched on. They also draw", StringComparison.Ordinal),
            "two mods are both named, in the plural");
        c.True(two.Contains("switch Farseer and Distant Vistas off", StringComparison.Ordinal), "and both are named in the advice");
        c.Eq("A, B and C", LodModNotice.JoinNames(new[] { "A", "B", "C" }), "three names read as a list");

        c.True(LodModNotice.GeneralText.Contains("does not step aside", StringComparison.Ordinal),
            "the general reminder says this mod does not step aside");
        c.True(LodModNotice.Title(Array.Empty<string>()) != LodModNotice.Title(new[] { "Farseer" }),
            "the two forms have their own titles");

        string notice = File.ReadAllText(Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src", "Gui", "LodModNotice.cs"));
        c.True(notice.Contains("this.offerHide = offerHide && conflicts.Length == 0;", StringComparison.Ordinal),
            "\"Don't show again\" is never offered while a listed mod is switched on");
        string mod = File.ReadAllText(Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src", "VintageHorizonsModSystem.cs"));
        c.True(mod.Contains("if (otherLodMods.Length == 0 && config.HideLodModNotice) return;", StringComparison.Ordinal),
            "hiding the reminder never hides a conflict: a listed mod brings the notice back");
        c.True(mod.Contains("capi.Event.LevelFinalize += ShowLodModNotice;", StringComparison.Ordinal),
            "the notice is shown at every login, once the world is up");
    }

    static void NeverStepsAside(Check c)
    {
        string src = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src");
        string all = string.Join("\n", Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
        c.False(all.Contains("deferringTo", StringComparison.Ordinal), "nothing remains of the idle-for-another-mod state");
        c.False(all.Contains("IgnoreOtherLodMods", StringComparison.Ordinal), "nor of its switch");
        c.False(Regex.IsMatch(all, @"ChatCommands\.Create\(""dldefer""\)"), "and there is no command to step aside");
        c.False(Regex.IsMatch(all, @"override bool ShouldLoad\(ICoreAPI"),
            "no system declines to load because another mod is installed");
    }

    static string Drawing(Func<string, bool> loaded, Func<string, bool?> switches)
        => string.Join(",", OtherLodMods.Inspect(loaded, switches).Drawing);

    static readonly Func<string, bool> None = _ => false;
    static Func<string, bool> Only(string modid) => name => name == modid;
    static readonly Func<string, bool?> AllOn = _ => true;
    static readonly Func<string, bool?> AllOff = _ => false;
}
