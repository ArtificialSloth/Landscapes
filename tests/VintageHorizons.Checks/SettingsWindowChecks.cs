using VintageHorizons.Net;

namespace VintageHorizons.Checks;

/// <summary>
/// The settings window (owner, 2026-10-19): General and Generate, plus Developer while General's
/// Developer options switch is on. Every tuning dial is on Developer, one group at a time; Farseer,
/// the seam fog, all-cores collection and the no-sampler warning are on General, in two columns.
/// </summary>
public static class SettingsWindowChecks
{
    public static void Run(Check c)
    {
        DeveloperIsOptIn(c);
        DeveloperGroupsCoverEveryDial(c);
        FarseerRowOnGeneral(c);
        GenerateReadsAsAFlow(c);
        NoticePointsAtTheSettings(c);
        LayoutStaysClear(c);
    }


    static void DeveloperIsOptIn(Check c)
    {
        int[] hidden = VintageHorizonsConfigDialog.VisibleTabs(false);
        int[] shown = VintageHorizonsConfigDialog.VisibleTabs(true);
        c.Eq(2, hidden.Length, "without Developer options: General and Generate");
        c.Eq(3, shown.Length, "with them, Developer as well");
        c.Eq(VintageHorizonsConfigLayout.PlannedTabCount, shown.Length, "the strip is sized for all three");
        c.True(shown.Select((tab, i) => tab == i).All(x => x) && shown.Take(2).SequenceEqual(hidden),
            "a tab's id is its position, and hiding Developer moves no other tab");
        int? general = VintageHorizonsConfigDialog.TabNamed("general");
        c.True(general == shown[0]
                && VintageHorizonsConfigDialog.TabNamed("Farseer") == general
                && VintageHorizonsConfigDialog.TabNamed("performance") == general
                && VintageHorizonsConfigDialog.TabNamed("Developer") == shown[2]
                && VintageHorizonsConfigDialog.TabNamed("aa") == shown[2]
                && VintageHorizonsConfigDialog.TabNamed("nonsense") == null,
            ".dlconfig opens a tab named in any case; Farseer and Performance open General, AA Developer");

        c.False(new VintageHorizonsConfig().ShowDeveloperOptions, "a fresh install hides Developer");
        string dialog = Source("Gui", "VintageHorizonsConfigDialog.cs");
        string mod = Source("VintageHorizonsModSystem.cs");
        c.True(mod.Contains("config.ShowDeveloperOptions = on;", StringComparison.Ordinal)
                && mod.Contains("new DeveloperOptionsTuning(config.ShowDeveloperOptions", StringComparison.Ordinal)
                && dialog.Contains("\"developer-options\"", StringComparison.Ordinal),
            "the Developer options switch is on General and remembered in the mod's config");
        c.True(dialog.Contains("if (Array.IndexOf(VisibleTabs(showDeveloper), tab) < 0) return false;", StringComparison.Ordinal)
                && mod.Contains("if (!configDialog!.ShowTab(tab))", StringComparison.Ordinal),
            ".dlconfig developer says so instead of opening a hidden tab");
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
        c.True(dialog.Contains("\"Farseer horizon terrain\", \"\", FarseerTip, OnSilhouetteEnabledChanged", StringComparison.Ordinal)
                && dialog.Contains("\"silhouette-enabled\"", StringComparison.Ordinal)
                && dialog.Contains("\"silhouette-fog\"", StringComparison.Ordinal),
            "Farseer and the seam fog have their switches on General");
        c.True(dialog.Contains("if (silhouettes.NoSampler())", StringComparison.Ordinal)
                && dialog.Contains("composer.AddHoverText(NoSamplerWarning", StringComparison.Ordinal),
            "with no sampler mod the row shows a short red note whose hover is the full warning");
        c.True(VintageHorizonsConfigDialog.NoSamplerNote.Length <= 40,
            "the note is short enough for the space between the two switches");
        c.True(dialog.Contains("pendingSilhouettes = LodPreset.Quality.ApplyTo(pendingSilhouettes);", StringComparison.Ordinal),
            "Defaults is the Quality preset, Farseer's switches and reach included");
        string mod = Source("VintageHorizonsModSystem.cs");
        string prompt = Source("Gui", "LodPresetPrompt.cs");
        c.False(new VintageHorizonsConfig().PresetPromptShown, "a fresh install asks for a preset once");
        c.True(prompt.Contains("for (int i = 0; i < LodPreset.All.Length; i++)", StringComparison.Ordinal)
                && prompt.Contains("closed();", StringComparison.Ordinal)
                && mod.Contains("config.PresetPromptShown = true;", StringComparison.Ordinal),
            "the prompt offers every preset and, however it closes, is not shown again");
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
        c.True(VintageHorizonsConfigLayout.FarseerRowY >= VintageHorizonsConfigLayout.DrawHeadingY + 34
                && VintageHorizonsConfigLayout.PausedRowY
                    >= VintageHorizonsConfigLayout.FarseerRowY + VintageHorizonsConfigLayout.VisualRowHeight,
            "the Farseer row follows the draw-distance heading, and paused loading a full row lower");
        c.True(VintageHorizonsConfigLayout.DeveloperRowY
                    >= VintageHorizonsConfigLayout.PausedRowY + VintageHorizonsConfigLayout.VisualRowHeight
                && VintageHorizonsConfigLayout.DeveloperRowY + VintageHorizonsConfigLayout.RowSwitchSize
                    <= VintageHorizonsConfigLayout.VisualHeadingY - 4,
            "then Developer options, finishing above the visual features heading");
        c.True(VintageHorizonsConfigLayout.HelpY >= 36 + 30
                && VintageHorizonsConfigLayout.HelpY + 22 <= VintageHorizonsConfigLayout.ScaleY,
            "the help line clears the distances title above it and the scale below it");
        c.True(VintageHorizonsConfigLayout.ColumnX(1) + VintageHorizonsConfigLayout.ColumnWidth
                == VintageHorizonsConfigLayout.SwitchX + 32
                && VintageHorizonsConfigLayout.ColumnCostX(0) - VintageHorizonsConfigLayout.ColumnX(0) >= 250,
            "the right column ends under the Automatic switch, and a title has 250 units before its cost");
        c.True(VintageHorizonsConfigLayout.ColumnX(1) + 60 + 190
                <= VintageHorizonsConfigLayout.ColumnCostX(1),
            "the preset drop-down finishes before the right column's Cost heading");
        // The preset drop-down hangs from the heading, 2 above it and 32 tall.
        c.True(VintageHorizonsConfigLayout.VisualHeadingY + 30 <= VintageHorizonsConfigLayout.VisualRowsY,
            "the preset drop-down finishes before the first feature row");
        c.True(VintageHorizonsConfigLayout.RowSwitchSize <= VintageHorizonsConfigLayout.VisualRowHeight - 2,
            "a row's switch fits its pitch");
        c.True(2 * VintageHorizonsConfigLayout.VisualRowsPerColumn >= VintageHorizonsConfigLayout.VisualRowCount,
            "two columns hold every feature row");
        double lastRowBottom = VintageHorizonsConfigLayout.VisualRowsY
            + VintageHorizonsConfigLayout.VisualRowsPerColumn * VintageHorizonsConfigLayout.VisualRowHeight;
        c.True(lastRowBottom <= VintageHorizonsConfigLayout.ButtonY - 6,
            "the last feature row ends above the Save row");
    }

    static string Source(params string[] parts) => File.ReadAllText(
        Path.Combine(new[] { GameAssemblies.RepoRoot, "VintageHorizons", "src" }.Concat(parts).ToArray()))
        .Replace("\r\n", "\n");
}
