using VintageHorizons.Net;

namespace VintageHorizons.Checks;

/// <summary>
/// The settings window has one view (owner, 2026-10-19): General, Generate, Performance and
/// Developer. There is no Advanced switch; every tuning dial is on Developer, one group at a time,
/// and Farseer's two switches and the no-sampler warning share one row on General.
/// </summary>
public static class SettingsWindowChecks
{
    public static void Run(Check c)
    {
        OneViewFourTabs(c);
        DeveloperGroupsCoverEveryDial(c);
        FarseerRowOnGeneral(c);
        GenerateReadsAsAFlow(c);
        NoticePointsAtTheSettings(c);
        LayoutStaysClear(c);
    }


    static void OneViewFourTabs(Check c)
    {
        int[] tabs = VintageHorizonsConfigDialog.Tabs;
        c.Eq(4, tabs.Length, "General, Generate, Performance and Developer");
        c.Eq(VintageHorizonsConfigLayout.PlannedTabCount, tabs.Length, "the strip is sized for them");
        c.True(tabs.Select((tab, i) => tab == i).All(x => x),
            "a tab's id is its position in the strip");
        int? general = VintageHorizonsConfigDialog.TabNamed("general");
        c.True(general == tabs[0]
                && VintageHorizonsConfigDialog.TabNamed("Farseer") == general
                && VintageHorizonsConfigDialog.TabNamed("performance") == tabs[2]
                && VintageHorizonsConfigDialog.TabNamed("Developer") == tabs[3]
                && VintageHorizonsConfigDialog.TabNamed("aa") == tabs[3]
                && VintageHorizonsConfigDialog.TabNamed("nonsense") == null,
            ".dlconfig opens a tab named in any case; Farseer opens General and AA opens Developer");

        string dialog = Source("Gui", "VintageHorizonsConfigDialog.cs");
        string mod = Source("VintageHorizonsModSystem.cs");
        c.False(dialog.Contains("show-advanced", StringComparison.Ordinal)
                || mod.Contains("ShowAdvancedSettings", StringComparison.Ordinal),
            "there is no Advanced switch and nothing remembers one");
    }

    static void DeveloperGroupsCoverEveryDial(Check c)
    {
        string dialog = Source("Gui", "VintageHorizonsConfigDialog.cs");
        c.True(dialog.Contains("\"Anti-aliasing\", \"Farseer look\", \"Seam fog\"", StringComparison.Ordinal)
                && dialog.Contains("AddDropDown(DeveloperGroupCodes, DeveloperGroupNames", StringComparison.Ordinal),
            "Developer picks one of three groups from a drop-down");
        c.True(dialog.Contains("foreach (LodTaaSliders.Row row in LodTaaSliders.All)", StringComparison.Ordinal)
                && dialog.Contains("\"taa-show\"", StringComparison.Ordinal),
            "the anti-aliasing group has every dial and the tint switch");
        c.True(dialog.Contains("\"silhouette-reach\"", StringComparison.Ordinal)
                && dialog.Contains("SilhouetteRow[] rows = GroupRows(fog);", StringComparison.Ordinal)
                && dialog.Contains("row.Key.StartsWith(\"sil-fog\", StringComparison.Ordinal) == fog", StringComparison.Ordinal),
            "Farseer look has the reach and the look dials, Seam fog the fog dials, split by key");
        c.True(dialog.Contains("foreach (SilhouetteRow row in GroupRows(developerGroup == SeamFogGroup))\n                    pendingSilhouettes = row.Write(pendingSilhouettes, row.Read(defaults));",
                StringComparison.Ordinal),
            "Defaults on Developer resets only the group on show");
    }

    static void FarseerRowOnGeneral(Check c)
    {
        string dialog = Source("Gui", "VintageHorizonsConfigDialog.cs");
        c.True(dialog.Contains("ComposeFarseerRow(composer);", StringComparison.Ordinal)
                && dialog.Contains("\"silhouette-enabled\"", StringComparison.Ordinal)
                && dialog.Contains("\"silhouette-fog\"", StringComparison.Ordinal),
            "Farseer and the seam fog have their switches on General");
        c.True(dialog.Contains("if (silhouettes.NoSampler())", StringComparison.Ordinal)
                && dialog.Contains("composer.AddHoverText(NoSamplerWarning", StringComparison.Ordinal),
            "with no sampler mod the row shows a short red note whose hover is the full warning");
        c.True(VintageHorizonsConfigDialog.NoSamplerNote.Length <= 40,
            "the note is short enough for the space between the two switches");
        c.True(dialog.Contains("FogEnabled = LodSilhouetteSettings.Default.FogEnabled,", StringComparison.Ordinal),
            "Defaults puts the seam fog back too, now that it is on General");
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
        c.True(VintageHorizonsConfigLayout.FarseerRowY
                >= VintageHorizonsConfigLayout.PausedRowY + VintageHorizonsConfigLayout.VisualRowHeight,
            "the Farseer row follows it a full row lower");
        c.True(VintageHorizonsConfigLayout.FarseerRowY + VintageHorizonsConfigLayout.RowSwitchSize
                <= VintageHorizonsConfigLayout.VisualHeadingY - 4,
            "and finishes above the visual features heading");
        // The preset drop-down hangs from the heading, 2 above it and 32 tall.
        c.True(VintageHorizonsConfigLayout.VisualHeadingY + 30 <= VintageHorizonsConfigLayout.VisualRowsY,
            "the preset drop-down finishes before the first feature row");
        c.True(VintageHorizonsConfigLayout.RowSwitchSize <= VintageHorizonsConfigLayout.VisualRowHeight - 2,
            "a row's switch fits its pitch");
        double lastRowBottom = VintageHorizonsConfigLayout.VisualRowsY
            + VintageHorizonsConfigLayout.VisualRowCount * VintageHorizonsConfigLayout.VisualRowHeight;
        c.True(lastRowBottom <= VintageHorizonsConfigLayout.ButtonY - 6,
            "the last feature row ends above the Save row");
    }

    static string Source(params string[] parts) => File.ReadAllText(
        Path.Combine(new[] { GameAssemblies.RepoRoot, "VintageHorizons", "src" }.Concat(parts).ToArray()))
        .Replace("\r\n", "\n");
}
