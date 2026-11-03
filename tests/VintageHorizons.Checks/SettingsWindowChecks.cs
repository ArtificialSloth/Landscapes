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
        // Owner, 2026-11-03: the Plants tab became a Developer group, with Plant Detail on General.
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
                && VintageHorizonsConfigDialog.TabNamed("plants") == shown[2]
                && VintageHorizonsConfigDialog.TabNamed("aa") == shown[2]
                && VintageHorizonsConfigDialog.TabNamed("nonsense") == null,
            ".dlconfig opens a tab named in any case; Farseer and Performance open General, AA and plants Developer");
        c.True(VintageHorizonsConfigDialog.DeveloperGroupNamed("Plants") == 4
                && VintageHorizonsConfigDialog.DeveloperGroupNamed("aa") == 0
                && VintageHorizonsConfigDialog.DeveloperGroupNamed("general") == null,
            ".dlconfig plants opens Developer on its Plants group");

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
                && dialog.Contains("\"developer-groups\");", StringComparison.Ordinal)
                && dialog.Contains("GetHorizontalTabs(\"developer-groups\").activeElement = developerGroup;", StringComparison.Ordinal),
            "Developer picks one of three groups from sub-tabs at the top of the page");
        c.False(dialog.Contains("pendingAllCores = false;", StringComparison.Ordinal),
            "Defaults leaves the all-cores collector as it is (owner, 2026-10-19)");
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
        c.True(dialog.Contains("\"Farseer horizon terrain\", FarseerCost, FarseerTip, OnSilhouetteEnabledChanged", StringComparison.Ordinal)
                && dialog.Contains("\"silhouette-enabled\"", StringComparison.Ordinal)
                && dialog.Contains("\"silhouette-fog\"", StringComparison.Ordinal),
            "Farseer and the seam fog have their switches on General");
        c.True(dialog.Contains("if (silhouettes.NoSampler())", StringComparison.Ordinal)
                && dialog.Contains("AddTip(composer, NoSamplerWarning", StringComparison.Ordinal),
            "with no sampler mod the row shows a short red note whose hover is the full warning");
        c.True(VintageHorizonsConfigDialog.NoSamplerNote.Length <= 40,
            "the note is short enough for the space between the two switches");
        c.True(dialog.Contains("pendingSilhouettes = LodPreset.Quality.ApplyTo(pendingSilhouettes);", StringComparison.Ordinal),
            "Defaults is the Quality preset, Farseer's switches and reach included");
        string mod = Source("VintageHorizonsModSystem.cs");
        string prompt = Source("Gui", "LodPresetPrompt.cs");
        c.False(new VintageHorizonsConfig().PresetPromptShown, "a fresh install asks for a preset once");
        c.True(prompt.Contains("for (int i = 0; i < LodPreset.FirstRun.Length; i++)", StringComparison.Ordinal)
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
        c.True(VintageHorizonsConfigLayout.LodTitleY + 28 <= VintageHorizonsConfigLayout.AutoNoteY
                && VintageHorizonsConfigLayout.AutoNoteY + 22 <= VintageHorizonsConfigLayout.ScaleY,
            "one title heads the page, with the line on Automatic between it and the bar (owner, 2026-10-19)");
        string dialog = Source("Gui", "VintageHorizonsConfigDialog.cs");
        c.True(dialog.Contains("\"LOD Thresholds (Drag to adjust)\"", StringComparison.Ordinal)
                && !dialog.Contains("draw-distance-label", StringComparison.Ordinal)
                && !dialog.Contains("threshold-help", StringComparison.Ordinal)
                && dialog.Contains("AddTip(composer, ScaleHelp,", StringComparison.Ordinal),
            "the draw-distance readout and help line are gone; the help is the title's hover");
        c.True(VintageHorizonsConfigLayout.VisualRowsY
                    + VintageHorizonsConfigLayout.VisualLineCount * VintageHorizonsConfigLayout.VisualRowHeight
                    <= VintageHorizonsConfigLayout.VersionY
                && VintageHorizonsConfigLayout.ShowTaaY + 32 <= VintageHorizonsConfigLayout.VersionY
                && VintageHorizonsConfigLayout.VersionY + 18 <= VintageHorizonsConfigLayout.ButtonY
                && dialog.Contains("GetMod(\"dodenslandscapes\")?.Info?.Version", StringComparison.Ordinal),
            "the version sits above Save, clear of the rows and buttons, as the game loaded it (owner, 2026-11-03)");

        // Owner, 2026-11-03: cost words from Session 124's measurements.
        var costs = VintageHorizonsConfigDialog.Rows.Concat(VintageHorizonsConfigDialog.PlantRows)
            .ToDictionary(row => row.Key, row => row.Cost);
        c.True(costs["seam-blending"] == "Low" && costs["climate-tint"] == "Low" && costs["distant-aa"] == "Low"
                && costs["plant-textures"] == "Low",
            "seam blending, close biome colours, anti-aliasing and Plant Textures (it carries leaf shapes) are Low");
        c.True(new[] { "climate-range", "leaf-season", "half-blocks", "water-effects", "distant-lights",
                    "fruit-tree-colors", "crop-shapes", "reed-shapes", "vine-shapes", "forage-markers",
                    "flower-sheets", "bush-textures" }.All(key => costs[key] == "Minimal"),
            "everything the bench could not measure says Minimal");
        c.True(costs.Values.All(word => word is "Minimal" or "Low" or "Moderate" or "High"),
            "every cost word is on the four-word scale");
        c.True(dialog.Contains("const string FarseerCost = \"Low\";", StringComparison.Ordinal)
                && dialog.Contains("const string SeamFogCost = \"Varies\";", StringComparison.Ordinal),
            "Farseer stays Low and the seam fog says Varies, its cost following how much fog is on screen (owner)");
        c.True(dialog.Contains("\"Plant Detail\", \"Low\", PlantDetailTip, OnPlantDetailChanged", StringComparison.Ordinal)
                && dialog.Contains("else if (developerGroup == PlantsGroup) ComposePlantDials(composer);", StringComparison.Ordinal)
                && dialog.Contains("\"Seam band\", \"Plants\" }", StringComparison.Ordinal),
            "Plant Detail is on General and each plant feature under Developer > Plants (owner, 2026-11-03)");

        // An open drop-down list lies over rows whose tips showed through it (owner, 2026-11-03).
        c.True(!dialog.Contains(".AddHoverText(", StringComparison.Ordinal)
                    || dialog.Split(".AddHoverText(").Length == 2,
            "every tip goes through AddTip, so the frame hook knows it");
        c.True(dialog.Contains("dropDownKeys.Add(\"leaf-shape\");", StringComparison.Ordinal)
                && dialog.Contains("listMenu?.IsOpened == true", StringComparison.Ordinal)
                && dialog.Contains("SetAutoDisplay(!listOpen)", StringComparison.Ordinal)
                && dialog.Contains("new FrameHook(capi, OnFrame)", StringComparison.Ordinal),
            "while a drop-down list is open no tip on the page shows");
        string scaleElement = Source("Gui", "LodThresholdScaleElement.cs");
        c.True(scaleElement.Contains("OnLockedMarkerGrabbed?.Invoke();", StringComparison.Ordinal)
                && dialog.Contains("OnLockedMarkerGrabbed = FlashAutomatic,", StringComparison.Ordinal)
                && dialog.Contains("\"auto-label\"", StringComparison.Ordinal),
            "grabbing a locked L1-L6 handle flashes the Automatic label red");
        c.True(dialog.Contains("ComposeFooterToggles(composer);", StringComparison.Ordinal)
                && VintageHorizonsConfigLayout.FooterLeft
                    >= VintageHorizonsConfigLayout.Margin + VintageHorizonsConfigLayout.FooterButtonWidth
                && VintageHorizonsConfigLayout.FooterRight
                    <= VintageHorizonsConfigLayout.Width - VintageHorizonsConfigLayout.Margin
                        - 2 * VintageHorizonsConfigLayout.FooterButtonWidth - VintageHorizonsConfigLayout.FooterButtonGap,
            "paused loading and Developer options sit in the action row between Defaults and Cancel");
        c.True(VintageHorizonsConfigLayout.ScaleY + VintageHorizonsConfigLayout.ScaleHeight
                    <= VintageHorizonsConfigLayout.RuleY
                && VintageHorizonsConfigLayout.RuleY + VintageHorizonsConfigLayout.RuleHeight
                    <= VintageHorizonsConfigLayout.VisualHeadingY - 2,
            "a rule sits between the bar and the visual features heading");
        c.True(VintageHorizonsConfigLayout.VisualRowsPerColumn + 1 == VintageHorizonsConfigLayout.VisualLineCount
                && VintageHorizonsConfigLayout.VisualRowCount - VintageHorizonsConfigLayout.VisualRowsPerColumn + 2
                    <= VintageHorizonsConfigLayout.VisualLineCount,
            "Farseer and five features fill the left column; the fog, all-cores and three features the right");
        c.True(LodPreset.All.All(preset => preset.Visuals.PlantTextures),
            "Plant Textures is on in every preset");
        c.True(VintageHorizonsConfigLayout.ColumnX(1) + VintageHorizonsConfigLayout.ColumnWidth
                == VintageHorizonsConfigLayout.SwitchX + 32
                && VintageHorizonsConfigLayout.ColumnCostX(0) - VintageHorizonsConfigLayout.ColumnX(0) >= 250,
            "the right column ends under the Automatic switch, and a title has 250 units before its cost");
        // Owner, 2026-11-03: the presets head the page as buttons, a rule under them.
        c.True(VintageHorizonsConfigLayout.PresetButtonX(LodPreset.All.Length)
                    + VintageHorizonsConfigLayout.PresetButtonWidth
                    <= VintageHorizonsConfigLayout.Width - VintageHorizonsConfigLayout.Margin
                && VintageHorizonsConfigLayout.PresetRowY + VintageHorizonsConfigLayout.PresetButtonHeight
                    <= VintageHorizonsConfigLayout.PresetRuleY
                && VintageHorizonsConfigLayout.PresetRuleY + VintageHorizonsConfigLayout.RuleHeight
                    <= VintageHorizonsConfigLayout.LodTitleY - 4,
            "every preset and Custom fit across the top, with a rule between them and the LOD title");
        c.True(dialog.Contains("ComposePresetButtons(composer);\n        AddRule(composer, VintageHorizonsConfigLayout.PresetRuleY);", StringComparison.Ordinal)
                && dialog.Contains("composer.GetToggleButton(\"preset-\" + PresetCodes[i])?.SetValue(i == shown);", StringComparison.Ordinal)
                && !dialog.Contains("AddDropDown(PresetCodes", StringComparison.Ordinal),
            "the drop-down is gone, and every press redraws the row from the settings, so Custom stays a readout");
        c.True(VintageHorizonsConfigLayout.VisualHeadingY + 30 <= VintageHorizonsConfigLayout.VisualRowsY,
            "the visual features heading finishes before the first feature row");
        c.True(VintageHorizonsConfigLayout.RowSwitchSize <= VintageHorizonsConfigLayout.VisualRowHeight - 2,
            "a row's switch fits its pitch");
        c.True(VintageHorizonsConfigLayout.NoteY + 22 <= VintageHorizonsConfigLayout.ButtonY - 4,
            "the features and the no-sampler note end above the action row");
        c.True(VintageHorizonsConfigLayout.Margin + VintageHorizonsConfigLayout.ColumnWidth
                <= VintageHorizonsConfigLayout.Width - VintageHorizonsConfigLayout.Margin - 300,
            "the note keeps to the left column, clear of the version on the same line");
        c.True(VintageHorizonsConfigLayout.VersionTopX
                    >= VintageHorizonsConfigLayout.PresetButtonX(LodPreset.All.Length)
                        + VintageHorizonsConfigLayout.PresetButtonWidth + 8
                && VintageHorizonsConfigLayout.VersionTopX + 80
                    <= VintageHorizonsConfigLayout.Width - VintageHorizonsConfigLayout.Margin
                && dialog.Contains("Y(VintageHorizonsConfigLayout.VersionTopY)", StringComparison.Ordinal),
            "on General the version sits after Custom at the end of the preset row (owner, 2026-11-03)");
        c.True(VintageHorizonsConfigLayout.VisualHeadingY + 22 <= VintageHorizonsConfigLayout.VisualRowsY - 16
                && dialog.Contains("HeadingFont().WithOrientation(EnumTextOrientation.Center)", StringComparison.Ordinal),
            "Visual features is centred, and the Cost captions sit just over their rows, clear of it");
        // Lora Bold runs about 6.2 units a point for \"Performance\", the longest name (2026-11-03 review).
        c.True(VintageHorizonsConfigLayout.PresetButtonWidth >= 6.2 * VintageHorizonsConfigLayout.PresetFontSize + 16,
            "every preset name fits its button: at 24 points \"Performance\" was cut to \"Performan\"");
        c.True(VintageHorizonsConfigLayout.SwitchX + 32
                == VintageHorizonsConfigLayout.Width - VintageHorizonsConfigLayout.Margin,
            "the switches end on the same right edge as the bar, the rules and Save");
    }

    static string Source(params string[] parts) => File.ReadAllText(
        Path.Combine(new[] { GameAssemblies.RepoRoot, "VintageHorizons", "src" }.Concat(parts).ToArray()))
        .Replace("\r\n", "\n");
}
