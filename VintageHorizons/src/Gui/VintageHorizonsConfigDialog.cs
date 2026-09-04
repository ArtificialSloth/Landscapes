using System.Globalization;
using Vintagestory.API.Client;

namespace VintageHorizons;

/// <summary>
/// Client-only settings window opened by .vhconfig.
///
/// <para>
/// One page. The top half is how far and how finely cached terrain is drawn; the bottom half
/// is which of the optional passes are drawn at all - the settings a machine that cannot
/// afford them needs to be able to give up, each one measured rather than guessed.
/// </para>
/// <para>
/// Each feature is a title and a switch, with what it does on hover. The descriptions were
/// printed under the titles for one build and it did not work: a medium-weight title is taller
/// than its box, so every row overran the two lines beneath it.
/// </para>
/// <para>
/// Nothing is applied until Save. The pending values below are the whole state of the window,
/// so switching tabs re-composes freely without losing an edit.
/// </para>
/// </summary>
internal sealed class VintageHorizonsConfigDialog : GuiDialog
{
    const int MinDrawDistance = 1024;
    const int MaxDrawDistance = 32768;
    const int DrawDistanceStep = 512;

    /// <summary>
    /// What Defaults restores, and the same number a fresh install starts at - see
    /// <see cref="VintageHorizonsConfig.FarViewDistanceCap"/>. The two have to agree or
    /// Defaults means something the mod has never done.
    /// </summary>
    const int DefaultDrawDistance = 4096;

    const double Width = 800;
    const double Height = 700;
    const double Margin = 30;
    const double SwitchX = 716;
    const double RowHeight = 40;

    readonly Action<int[], int, LodVisualSettings> save;
    readonly int[] pendingThresholds;
    int pendingFarCap;
    LodVisualSettings pendingVisuals;
    LodThresholdScaleElement? scale;

    public override string DebugName => "vintagehorizons-config";
    public override string? ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => true;

    public VintageHorizonsConfigDialog(ICoreClientAPI capi, IReadOnlyList<int> thresholds,
        int farCap, LodVisualSettings visuals,
        Action<int[], int, LodVisualSettings> save) : base(capi)
    {
        this.save = save;
        pendingThresholds = LodWorld.NormalizeLevelThresholds(
            thresholds, LodWorld.DefaultLevelThresholds[0]);
        pendingFarCap = Math.Clamp(farCap <= 0 ? MaxDrawDistance : farCap,
            MinDrawDistance, MaxDrawDistance);
        pendingVisuals = visuals;
        ComposeDialog();
    }

    /// <summary>
    /// One switchable feature: the name on the row, the tip that explains it on hover, a
    /// coarse word for what it costs, and how it reads and writes the settings value.
    ///
    /// The cost words are deliberately coarse. What a feature actually costs depends on what
    /// is on screen - the water terms are free with no water in view and dominate over a
    /// coastline - so a number here would be more precise than it is true.
    /// </summary>
    readonly record struct VisualRow(
        string Key, string Title, string Tip, string Cost,
        Func<LodVisualSettings, bool> Read,
        Func<LodVisualSettings, bool, LodVisualSettings> Write);

    static readonly VisualRow[] Rows =
    {
        new("seam-blending", "Vanilla and LOD Seam Blending",
            "Blends the vanilla terrain with LOD terrain using vanilla textures and speckle "
            + "effects.",
            "Moderate",
            v => v.BlendPipeline,
            (v, on) => v with { BlendPipeline = on, SeamBand = on, BlockTextures = on }),

        // Close (L0-L2) and Far (L3-L6) are the two ends of ONE shader threshold, not two
        // independent switches, so Far implies Close and turning Close off takes Far with it.
        // "Far but not close" would mean near ground wearing the biome you are standing in
        // while distant ground wore its own - a state nobody would choose, and one the
        // renderer cannot express.
        new("climate-tint", "Close Biome Colors",
            "Applies seasonal colors to LOD L0-L2 terrain based on the location of those "
            + "chunks - Off makes the season colors based off the chunk you are standing in.",
            "High",
            v => v.ClimateTint,
            (v, on) => v with
            {
                ClimateTint = on,
                ClimateTintRange = on
                    ? v.ClimateTintRange
                    : LodVisualSettings.CloseClimateTintRange,
            }),

        new("climate-range", "Far Biome Colors",
            "Applies seasonal colors to LOD L3-L6 terrain based on the location of those "
            + "chunks - Off makes the season colors based off the chunk you are standing in.",
            "High",
            v => v.FarBiomeColours,
            (v, on) => v with
            {
                ClimateTint = on || v.ClimateTint,
                ClimateTintRange = on
                    ? LodVisualSettings.FullClimateTintRange
                    : LodVisualSettings.CloseClimateTintRange,
            }),

        new("leaf-season", "Varied Tree Colors",
            "Applies seasonal colors to trees individually.",
            "Low",
            v => v.LeafSeasonColours,
            (v, on) => v with { LeafSeasonColours = on }),

        new("half-blocks", "Slab Terrain",
            "Implements LOD rendering of slabs - turn on with Terrain Slabs.",
            "Low",
            v => v.HalfBlockTerrain,
            (v, on) => v with { HalfBlockTerrain = on }),

        new("water-effects", "Water Effects",
            "Adds glint and foam to the water.",
            "Varies",
            v => v.WaterEffects,
            (v, on) => v with { WaterEffects = on }),
    };

    static readonly string[] PresetCodes = { "performance", "balanced", "quality", "custom" };
    static readonly string[] PresetNames = { "Performance", "Balanced", "Quality", "Custom" };

    void ComposeDialog()
    {
        ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, Width, Height)
            .WithAlignment(EnumDialogArea.CenterMiddle);

        GuiComposer composer = capi.Gui
            .CreateCompo(DebugName, dialogBounds)
            .AddShadedDialogBG(ElementBounds.Fill, true)
            .AddDialogTitleBar("Vintage Horizons", OnTitleClose)
            .BeginChildElements();

        composer.AddStaticText("LOD transition distances",
            CairoFont.WhiteMediumText(), ElementBounds.Fixed(Margin, 48, 700, 30));
        composer.AddStaticText(
            "Drag L1-L6 on one logarithmic scale. Markers stop at their neighbours.",
            CairoFont.WhiteSmallText(), ElementBounds.Fixed(Margin, 78, 700, 22));

        scale = new LodThresholdScaleElement(capi, pendingThresholds,
            OnThresholdChanged, ElementBounds.Fixed(Margin, 100, 700, 104));
        composer.AddInteractiveElement(scale, "lod-threshold-scale");

        composer.AddStaticText("256", CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(Margin + 8, 206, 80, 20));
        composer.AddStaticText("32,768 blocks", CairoFont.WhiteSmallText(),
            EnumTextOrientation.Right, ElementBounds.Fixed(630, 206, 100, 20));

        for (int i = 0; i < LodWorld.MaxLevel; i++)
        {
            composer.AddDynamicText(ThresholdLabel(i), CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(Margin + i * 116, 228, 112, 22), $"threshold-{i}");
        }

        composer.AddStaticText("Cached terrain draw distance", CairoFont.WhiteMediumText(),
            ElementBounds.Fixed(Margin, 262, 700, 30));
        composer.AddSlider(OnDrawDistanceChanged,
            ElementBounds.Fixed(Margin, 296, 590, 30), "draw-distance");
        composer.AddDynamicText(DrawDistanceLabel(), CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(635, 300, 130, 22), "draw-distance-label");

        composer.AddStaticText("Visual features", CairoFont.WhiteMediumText(),
            ElementBounds.Fixed(Margin, 344, 190, 30));
        composer.AddDropDown(PresetCodes, PresetNames, PresetIndex(), OnPresetSelected,
            ElementBounds.Fixed(Margin + 190, 342, 224, 32), "preset");

        // The cost words are a column, so they get a column heading. It sits over the same box
        // the words themselves use and shares their right edge, because a right-aligned word
        // under a left-aligned heading does not read as one column.
        composer.AddStaticText("Cost", CairoFont.WhiteDetailText(),
            EnumTextOrientation.Right, ElementBounds.Fixed(SwitchX - 110, 358, 100, 22));

        double y = 386;
        foreach (VisualRow row in Rows)
        {
            composer.AddStaticText(row.Title, CairoFont.WhiteMediumText(),
                ElementBounds.Fixed(Margin, y + 2, 560, 30));
            composer.AddStaticText(row.Cost, CairoFont.WhiteDetailText(),
                EnumTextOrientation.Right, ElementBounds.Fixed(SwitchX - 110, y + 8, 100, 22));
            composer.AddSwitch(on => OnVisualChanged(row, on),
                ElementBounds.Fixed(SwitchX, y, 32, 32), row.Key);

            // One hover region over the whole row, so the tip appears from the title, the cost
            // word or the switch alike. GuiElementHoverText handles no mouse-down, so this
            // cannot swallow the click that lands on the switch underneath it.
            composer.AddHoverText(row.Tip, CairoFont.WhiteSmallText(), 380,
                ElementBounds.Fixed(Margin, y, SwitchX + 32 - Margin, RowHeight - 2),
                $"tip-{row.Key}");
            y += RowHeight;
        }

        double buttonsY = Height - 58;
        composer.AddButton("Defaults", OnDefaults,
            ElementBounds.Fixed(Margin, buttonsY, 140, 38), EnumButtonStyle.Normal, "defaults");
        composer.AddButton("Cancel", OnCancel,
            ElementBounds.Fixed(Width - Margin - 275, buttonsY, 130, 38),
            EnumButtonStyle.Normal, "cancel");
        composer.AddButton("Save", OnSave,
            ElementBounds.Fixed(Width - Margin - 140, buttonsY, 140, 38),
            EnumButtonStyle.Normal, "save");

        SingleComposer = composer.EndChildElements().Compose();

        GuiElementSlider drawSlider = SingleComposer.GetSlider("draw-distance");
        drawSlider.SetValues(pendingFarCap, MinDrawDistance, MaxDrawDistance, DrawDistanceStep,
            " blocks");
        drawSlider.ShowTextWhenResting = false;
        foreach (VisualRow row in Rows)
        {
            SingleComposer.GetSwitch(row.Key).On = row.Read(pendingVisuals);
        }
    }

    /// <summary>Which preset the current combination is, or the trailing "Custom" entry.</summary>
    int PresetIndex() => pendingVisuals.PresetName switch
    {
        "Performance" => 0,
        "Balanced" => 1,
        "Quality" => 2,
        _ => 3,
    };

    void OnPresetSelected(string code, bool selected)
    {
        if (!selected) return;
        switch (code)
        {
            case "performance": pendingVisuals = LodVisualSettings.Performance; break;
            case "balanced": pendingVisuals = LodVisualSettings.Balanced; break;
            case "quality": pendingVisuals = LodVisualSettings.Quality; break;
            default: return;   // "Custom" is a readout, not a choice.
        }
        RefreshVisuals();
    }

    void OnVisualChanged(VisualRow row, bool on)
    {
        pendingVisuals = row.Write(pendingVisuals, on);

        // One switch can move another - the seam row carries the band and the block textures
        // with it, and Far biome colour implies Close. Rewriting every switch from the value
        // keeps what is on screen equal to what will be saved, which is the only thing that
        // makes the preset readout trustworthy.
        RefreshVisuals();
    }

    void RefreshVisuals()
    {
        if (SingleComposer?.Composed != true) return;
        foreach (VisualRow other in Rows)
        {
            GuiElementSwitch element = SingleComposer.GetSwitch(other.Key);
            bool wanted = other.Read(pendingVisuals);
            if (element.On != wanted) element.On = wanted;
        }
        SingleComposer.GetDropDown("preset").SetSelectedIndex(PresetIndex());
    }

    void OnThresholdChanged(int index, int value)
    {
        pendingThresholds[index] = value;
        if (SingleComposer?.Composed == true)
        {
            SingleComposer.GetDynamicText($"threshold-{index}")
                .SetNewText(ThresholdLabel(index), false, true, false);
        }
    }

    bool OnDrawDistanceChanged(int blocks)
    {
        pendingFarCap = Math.Clamp(blocks, MinDrawDistance, MaxDrawDistance);
        if (SingleComposer?.Composed == true)
        {
            SingleComposer.GetDynamicText("draw-distance-label")
                .SetNewText(DrawDistanceLabel(), false, true, false);
        }
        return true;
    }

    /// <summary>Everything back to what a fresh install draws, both halves of the page.</summary>
    bool OnDefaults()
    {
        Array.Copy(LodWorld.DefaultLevelThresholds, pendingThresholds,
            pendingThresholds.Length);
        pendingFarCap = DefaultDrawDistance;
        pendingVisuals = LodVisualSettings.Default;

        scale?.SetValues(pendingThresholds);
        for (int i = 0; i < pendingThresholds.Length; i++)
        {
            SingleComposer.GetDynamicText($"threshold-{i}")
                .SetNewText(ThresholdLabel(i), false, true, false);
        }
        SingleComposer.GetSlider("draw-distance").SetValues(
            pendingFarCap, MinDrawDistance, MaxDrawDistance, DrawDistanceStep, " blocks");
        SingleComposer.GetDynamicText("draw-distance-label")
            .SetNewText(DrawDistanceLabel(), false, true, false);
        RefreshVisuals();
        return true;
    }

    bool OnSave()
    {
        save(pendingThresholds, pendingFarCap, pendingVisuals);
        TryClose();
        return true;
    }

    bool OnCancel()
    {
        TryClose();
        return true;
    }

    void OnTitleClose() => TryClose();

    string ThresholdLabel(int index) => $"L{index + 1}: {FormatBlocks(pendingThresholds[index])}";

    string DrawDistanceLabel() => $"{FormatBlocks(pendingFarCap)} blocks";

    static string FormatBlocks(int blocks) => blocks.ToString("N0", CultureInfo.InvariantCulture);
}
