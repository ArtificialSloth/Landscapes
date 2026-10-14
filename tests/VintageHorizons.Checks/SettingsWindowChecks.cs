using VintageHorizons.Net;

namespace VintageHorizons.Checks;

/// <summary>
/// The settings window's simple and advanced views (owner, 2026-10-13): a player sees the
/// distances, the feature switches and Farseer's two switches; the tuning dials are behind
/// "Advanced settings", and hiding them never changes their values.
/// </summary>
public static class SettingsWindowChecks
{
    public static void Run(Check c)
    {
        AdvancedIsOptIn(c);
        AntiAliasingTabIsAdvanced(c);
        ChangedHiddenValuesAreFlagged(c);
        GenerateReadsAsAFlow(c);
        NoticePointsAtTheSettings(c);
        LayoutStaysClear(c);
    }

    static void AdvancedIsOptIn(Check c)
    {
        c.False(new VintageHorizonsConfig().ShowAdvancedSettings,
            "a fresh install shows the simple view");
        string mod = Source("VintageHorizonsModSystem.cs");
        c.True(mod.Contains("config.ShowAdvancedSettings = on;", StringComparison.Ordinal)
                && mod.Contains("new AdvancedTuning(config.ShowAdvancedSettings", StringComparison.Ordinal),
            "the choice is remembered in the mod's config");
    }

    static void AntiAliasingTabIsAdvanced(Check c)
    {
        int[] simple = VintageHorizonsConfigDialog.VisibleTabs(false);
        int[] advanced = VintageHorizonsConfigDialog.VisibleTabs(true);
        c.Eq(4, simple.Length, "the simple view has General, Farseer, Generate and Performance");
        c.Eq(5, advanced.Length, "the advanced view adds Anti-Aliasing");
        c.True(advanced.Except(simple).Count() == 1, "and that is the only difference");
        c.Eq(advanced[0], simple[0], "General comes first either way");

        string dialog = Source("Gui", "VintageHorizonsConfigDialog.cs");
        c.True(dialog.Contains("Array.IndexOf(VisibleTabs(showAdvanced), shownTab)", StringComparison.Ordinal),
            "the highlighted tab is found by position, since a click reports the tab's id");
        c.True(dialog.Contains("if (shownTab == AntiAliasingTab) shownTab = GeneralTab;", StringComparison.Ordinal),
            "hiding the dials while on Anti-Aliasing moves to General");
        c.True(dialog.Contains("if (!showAdvanced)", StringComparison.Ordinal)
                && dialog.Contains("composer.AddStaticText(SilhouetteFooter", StringComparison.Ordinal),
            "Farseer's simple page is its two switches and the footer");
    }

    static void ChangedHiddenValuesAreFlagged(Check c)
    {
        LodSilhouetteSettings defaults = LodSilhouetteSettings.Default;
        c.False(VintageHorizonsConfigDialog.HiddenValuesChanged(LodTaaDials.Default, defaults),
            "defaults everywhere: nothing hidden differs");
        c.False(VintageHorizonsConfigDialog.HiddenValuesChanged(LodTaaDials.Default,
                defaults with { Enabled = false, FogEnabled = false, ReachBlocks = 4096 }),
            "Farseer's switches and reach are on show, so they do not count");
        c.True(VintageHorizonsConfigDialog.HiddenValuesChanged(LodTaaDials.Default,
                defaults with { SkyTint = 2f }),
            "a changed Farseer appearance dial counts");
        c.True(VintageHorizonsConfigDialog.HiddenValuesChanged(LodTaaDials.Default,
                defaults with { FogStrength = 1.5f }),
            "and so does a fog dial");
        c.True(VintageHorizonsConfigDialog.HiddenValuesChanged(
                LodTaaDials.Default with { Sharpen = 0.2f }, defaults),
            "and an anti-aliasing dial");
    }

    static void GenerateReadsAsAFlow(Check c)
    {
        c.Eq(VintageHorizonsConfigDialog.WorldGenIdleHint,
            VintageHorizonsConfigDialog.WorldGenPageMessage(EnumWorldGenRemoteState.Idle,
                "[Landscapes] No savegame world-generation job has run. Use /dlworldgen start ..."),
            "at rest the page explains its buttons instead of quoting the chat command");
        c.Eq("", VintageHorizonsConfigDialog.WorldGenPageMessage(EnumWorldGenRemoteState.Running, "x"),
            "while running, the progress text carries the message, so it is not repeated");
        c.Eq("[Landscapes] Interrupted world-generation job available: 10/20 durable columns.",
            LodWorldGenPageMath.PageText(
                "[Landscapes] Interrupted world-generation job available: 10/20 durable columns. "
                + "Use /dlworldgen resume or stop."),
            "a command tail is dropped where the page has the buttons");
        c.Eq("Ready. Confirm within two minutes.",
            VintageHorizonsConfigDialog.WorldGenPageMessage(EnumWorldGenRemoteState.Prepared,
                "Ready. Confirm within two minutes."),
            "a prepared job's message shows as the server wrote it");
        c.Eq("Ready.", LodWorldGenPageMath.PageText("Ready."), "a message without one is unchanged");
        c.Eq("", LodWorldGenPageMath.PageText(null), "and no message is empty");

        c.False(VintageHorizonsConfigDialog.ShowsWorldGenProgress(EnumWorldGenRemoteState.Idle),
            "no progress bar at rest");
        c.False(VintageHorizonsConfigDialog.ShowsWorldGenProgress(EnumWorldGenRemoteState.Prepared),
            "nor while a prepared job waits for Confirm");
        c.True(VintageHorizonsConfigDialog.ShowsWorldGenProgress(EnumWorldGenRemoteState.Running)
                && VintageHorizonsConfigDialog.ShowsWorldGenProgress(EnumWorldGenRemoteState.Paused)
                && VintageHorizonsConfigDialog.ShowsWorldGenProgress(EnumWorldGenRemoteState.Complete),
            "from the start of a run until the next Prepare");

        string dialog = Source("Gui", "VintageHorizonsConfigDialog.cs");
        c.True(dialog.Contains("if (shownTab == GenerateTab) return;", StringComparison.Ordinal),
            "Generate has no Save, Cancel or Defaults: it works through its own buttons");
        c.True(dialog.Contains("worldGen.Save(pendingWorldGen.Normalize(ServerWorldGenMaxRadius()));\n\n        int x",
                StringComparison.Ordinal),
            "so Prepare is what remembers the chosen area");
        c.False(dialog.Contains("Balanced (engine default)", StringComparison.Ordinal),
            "and the preset line that looked like a choice is gone");
    }

    static void NoticePointsAtTheSettings(Check c)
    {
        c.True(LodModNotice.SettingsPointer.Contains(".dlconfig", StringComparison.Ordinal)
                && LodModNotice.SettingsPointer.Contains("Landscapes", StringComparison.Ordinal),
            "the login notice names both ways to the settings");
        string notice = Source("Gui", "LodModNotice.cs");
        c.True(notice.Contains("composer.AddButton(\"Open settings\", OnOpenSettings", StringComparison.Ordinal)
                && notice.Contains("+ \"\\n\\n\" + SettingsPointer", StringComparison.Ordinal),
            "and offers a button that opens them, under either form of the notice");
        string mod = Source("VintageHorizonsModSystem.cs");
        c.True(mod.Contains("\"dodenslandscapes-notice-settings\"", StringComparison.Ordinal),
            "the button opens the window once the notice has finished with the click");
    }

    static void LayoutStaysClear(Check c)
    {
        c.True(VintageHorizonsConfigLayout.PausedRowY >= VintageHorizonsConfigLayout.DrawHeadingY + 34,
            "the paused-loading switch has a row of its own below the draw-distance heading");
        c.True(VintageHorizonsConfigLayout.PausedRowY + 32 <= VintageHorizonsConfigLayout.VisualHeadingY - 4,
            "and finishes above the visual features heading");
    }

    static string Source(params string[] parts) => File.ReadAllText(
        Path.Combine(new[] { GameAssemblies.RepoRoot, "VintageHorizons", "src" }.Concat(parts).ToArray()))
        .Replace("\r\n", "\n");
}
