using System.Globalization;
using Vintagestory.API.Client;

namespace VintageHorizons;

/// <summary>
/// Client-only settings window opened by .vhconfig.
///
/// <para>
/// Two tabs, because the window answers two different questions and mixing them was what made
/// the single flat page hard to read. DETAIL is how far and how finely cached terrain is
/// drawn. VISUALS is which of the optional passes are drawn at all - the settings a machine
/// that cannot afford them needs to be able to give up, each one measured rather than guessed.
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

    const double Width = 800;
    const double Height = 640;
    const double Margin = 30;
    const double ContentTop = 96;
    const double SwitchX = 716;
    const double RowHeight = 56;

    readonly Action<int[], int, LodVisualSettings> save;
    readonly int[] pendingThresholds;
    int pendingFarCap;
    LodVisualSettings pendingVisuals;
    int activeTab;
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
    /// Bring one tab to the front, for `.vhconfig visuals` and for a window that is already
    /// open when the command is run again.
    /// </summary>
    public void ShowTab(int index)
    {
        index = Math.Clamp(index, 0, 1);
        if (index == activeTab) return;
        activeTab = index;
        ComposeDialog();
    }

    /// <summary>
    /// One row of the visuals tab: what it draws, what giving it up costs you, and roughly
    /// what it buys back.
    ///
    /// The cost words are from the measured sweep at a seam viewpoint and are deliberately
    /// coarse. What a feature actually costs depends on what is on screen - the water terms
    /// are free with no water in view and dominate over a coastline - so a number here would
    /// be more precise than it is true.
    /// </summary>
    readonly record struct VisualRow(
        string Key, string Title, string Detail, string Cost,
        Func<LodVisualSettings, bool> Read,
        Func<LodVisualSettings, bool, LodVisualSettings> Write);

    static readonly VisualRow[] Rows =
    {
        new("seam-blending", "Seam blending and block textures",
            "Blends distant ground into the game's own where they meet, and draws the nearest "
            + "cached terrain with real block textures instead of one flat colour per block.",
            "Moderate",
            v => v.BlendPipeline,
            (v, on) => v with { BlendPipeline = on, SeamBand = on, BlockTextures = on }),

        new("climate-tint", "Distant biome colours",
            "Colours distant ground by the biome it actually stands in. Off gives the whole "
            + "view the biome colour from where you are standing, as builds before 0.3.179 did.",
            "High",
            v => v.ClimateTint,
            (v, on) => v with { ClimateTint = on }),

        new("climate-range", "Biome colours at every distance",
            "Extends the above to the coarsest terrain. Off keeps it on the nearer levels, "
            + "where a biome edge is still a shape you can make out.",
            "High",
            v => v.ClimateTintRange <= 0f,
            (v, on) => v with
            {
                ClimateTintRange = on
                    ? LodVisualSettings.FullClimateTintRange
                    : LodVisualSettings.DefaultClimateTintRange,
            }),

        new("leaf-season", "Uneven forest colours",
            "Lets distant woodland turn colour in patches through autumn rather than all at "
            + "once, the way the game's own trees do.",
            "Low",
            v => v.LeafSeasonColours,
            (v, on) => v with { LeafSeasonColours = on }),

        new("half-blocks", "Half-height terrain detail",
            "Draws slabs and shallow snow at their real half-block height. Off rounds them up "
            + "to full blocks, which makes smoothed landscapes read as steps. Rebuilds terrain.",
            "Low",
            v => v.HalfBlockTerrain,
            (v, on) => v with { HalfBlockTerrain = on }),

        new("water-effects", "Distant water shine and foam",
            "Carries the game's own specular glint and surface noise out to the horizon, so "
            + "distant water does not go matte at the join. Costs nothing with no water in view.",
            "Varies",
            v => v.WaterEffects,
            (v, on) => v with { WaterEffects = on }),
    };

    void ComposeDialog()
    {
        ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, Width, Height)
            .WithAlignment(EnumDialogArea.CenterMiddle);

        GuiComposer composer = capi.Gui
            .CreateCompo(DebugName, dialogBounds)
            .AddShadedDialogBG(ElementBounds.Fill, true)
            .AddDialogTitleBar("Vintage Horizons", OnTitleClose)
            .BeginChildElements();

        var tabs = new[]
        {
            new GuiTab { DataInt = 0, Name = "Detail" },
            new GuiTab { DataInt = 1, Name = "Visual features" },
        };
        composer.AddHorizontalTabs(tabs,
            ElementBounds.Fixed(Margin, 48, Width - Margin * 2, 32), OnTabClicked,
            // Two sizes rather than two weights: the weight enum lives in cairo-sharp, which
            // this project does not reference, and the size reads as clearly on a tab anyway.
            CairoFont.WhiteSmallText(), CairoFont.WhiteMediumText(), "tabs");

        if (activeTab == 0) ComposeDetailTab(composer);
        else ComposeVisualsTab(composer);

        double buttonsY = Height - 74;
        composer.AddButton("Defaults", OnDefaults,
            ElementBounds.Fixed(Margin, buttonsY, 140, 38), EnumButtonStyle.Normal, "defaults");
        composer.AddButton("Cancel", OnCancel,
            ElementBounds.Fixed(Width - Margin - 275, buttonsY, 130, 38),
            EnumButtonStyle.Normal, "cancel");
        composer.AddButton("Save", OnSave,
            ElementBounds.Fixed(Width - Margin - 140, buttonsY, 140, 38),
            EnumButtonStyle.Normal, "save");

        SingleComposer = composer.EndChildElements().Compose();
        SingleComposer.GetHorizontalTabs("tabs").activeElement = activeTab;

        if (activeTab == 0) InitialiseDetailTab();
        else InitialiseVisualsTab();
    }

    void ComposeDetailTab(GuiComposer composer)
    {
        composer.AddStaticText("LOD transition distances",
            CairoFont.WhiteMediumText(), ElementBounds.Fixed(Margin, ContentTop, 700, 28));
        composer.AddStaticText(
            "Drag L1-L6 on one logarithmic scale. Markers stop at their neighbours.",
            CairoFont.WhiteSmallText(), ElementBounds.Fixed(Margin, ContentTop + 28, 700, 24));

        scale = new LodThresholdScaleElement(capi, pendingThresholds,
            OnThresholdChanged, ElementBounds.Fixed(Margin, ContentTop + 53, 700, 104));
        composer.AddInteractiveElement(scale, "lod-threshold-scale");

        composer.AddStaticText("256", CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(Margin + 8, ContentTop + 157, 80, 20));
        composer.AddStaticText("32,768 blocks", CairoFont.WhiteSmallText(),
            EnumTextOrientation.Right, ElementBounds.Fixed(630, ContentTop + 157, 100, 20));

        for (int i = 0; i < LodWorld.MaxLevel; i++)
        {
            composer.AddDynamicText(ThresholdLabel(i), CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(Margin + i * 116, ContentTop + 182, 112, 24), $"threshold-{i}");
        }

        composer.AddStaticText("Cached terrain draw distance", CairoFont.WhiteMediumText(),
            ElementBounds.Fixed(Margin, ContentTop + 222, 700, 28));
        composer.AddStaticText(
            "How far the cache is drawn at all. Terrain past this is not built or kept.",
            CairoFont.WhiteSmallText(), ElementBounds.Fixed(Margin, ContentTop + 250, 700, 24));
        composer.AddSlider(OnDrawDistanceChanged,
            ElementBounds.Fixed(Margin, ContentTop + 280, 590, 30), "draw-distance");
        composer.AddDynamicText(DrawDistanceLabel(), CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(635, ContentTop + 284, 130, 24), "draw-distance-label");
    }

    void ComposeVisualsTab(GuiComposer composer)
    {
        composer.AddStaticText(
            "Each of these draws something extra on distant terrain, and each costs frame "
            + "time to draw. Turning one off gives that time back.",
            CairoFont.WhiteSmallText(), ElementBounds.Fixed(Margin, ContentTop, 740, 44));

        composer.AddStaticText("Presets", CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(Margin, ContentTop + 52, 80, 28));
        string[] presets = { "Performance", "Balanced", "Quality" };
        for (int i = 0; i < presets.Length; i++)
        {
            int index = i;
            composer.AddSmallButton(presets[i], () => OnPreset(index),
                ElementBounds.Fixed(Margin + 76 + i * 142, ContentTop + 48, 134, 32),
                EnumButtonStyle.Normal, $"preset-{i}");
        }
        composer.AddDynamicText(PresetLabel(), CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(Margin + 508, ContentTop + 54, 240, 24), "preset-label");

        double y = ContentTop + 98;
        foreach (VisualRow row in Rows)
        {
            composer.AddStaticText(row.Title, CairoFont.WhiteMediumText(),
                ElementBounds.Fixed(Margin, y, 660, 26));
            composer.AddStaticText(row.Detail, CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(Margin, y + 24, 650, 32));
            composer.AddStaticText(row.Cost, CairoFont.WhiteDetailText(),
                EnumTextOrientation.Right, ElementBounds.Fixed(SwitchX - 108, y + 4, 100, 22));
            composer.AddSwitch(on => OnVisualChanged(row, on),
                ElementBounds.Fixed(SwitchX, y, 32, 32), row.Key);
            y += RowHeight;
        }
    }

    void InitialiseDetailTab()
    {
        GuiElementSlider drawSlider = SingleComposer.GetSlider("draw-distance");
        drawSlider.SetValues(pendingFarCap, MinDrawDistance, MaxDrawDistance, DrawDistanceStep,
            " blocks");
        drawSlider.ShowTextWhenResting = false;
    }

    void InitialiseVisualsTab()
    {
        foreach (VisualRow row in Rows)
        {
            SingleComposer.GetSwitch(row.Key).On = row.Read(pendingVisuals);
        }
    }

    void OnTabClicked(int index)
    {
        if (index == activeTab) return;
        activeTab = index;
        ComposeDialog();
    }

    bool OnPreset(int index)
    {
        pendingVisuals = index switch
        {
            0 => LodVisualSettings.Performance,
            1 => LodVisualSettings.Balanced,
            _ => LodVisualSettings.Quality,
        };
        RefreshVisuals();
        return true;
    }

    void OnVisualChanged(VisualRow row, bool on)
    {
        pendingVisuals = row.Write(pendingVisuals, on);

        // One switch can move another: the seam row carries the band and the block textures
        // with it, and there is no sense in the biome-colour RANGE reading as on while the
        // colours themselves are off. Rewriting every switch from the value keeps what is on
        // screen equal to what will be saved, which is the only thing that makes the preset
        // label below trustworthy.
        RefreshVisuals();
    }

    void RefreshVisuals()
    {
        if (activeTab != 1 || SingleComposer?.Composed != true) return;
        foreach (VisualRow other in Rows)
        {
            GuiElementSwitch element = SingleComposer.GetSwitch(other.Key);
            bool wanted = other.Read(pendingVisuals);
            if (element.On != wanted) element.On = wanted;
        }
        SingleComposer.GetDynamicText("preset-label")
            .SetNewText(PresetLabel(), false, true, false);
    }

    void OnThresholdChanged(int index, int value)
    {
        pendingThresholds[index] = value;
        if (activeTab == 0 && SingleComposer?.Composed == true)
        {
            SingleComposer.GetDynamicText($"threshold-{index}")
                .SetNewText(ThresholdLabel(index), false, true, false);
        }
    }

    bool OnDrawDistanceChanged(int blocks)
    {
        pendingFarCap = Math.Clamp(blocks, MinDrawDistance, MaxDrawDistance);
        if (activeTab == 0 && SingleComposer?.Composed == true)
        {
            SingleComposer.GetDynamicText("draw-distance-label")
                .SetNewText(DrawDistanceLabel(), false, true, false);
        }
        return true;
    }

    /// <summary>Defaults for the tab in front of you, so it cannot undo an edit you cannot see.</summary>
    bool OnDefaults()
    {
        if (activeTab == 0)
        {
            Array.Copy(LodWorld.DefaultLevelThresholds, pendingThresholds,
                pendingThresholds.Length);
            pendingFarCap = MaxDrawDistance;
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
            return true;
        }

        pendingVisuals = LodVisualSettings.Default;
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

    string PresetLabel() => pendingVisuals.PresetName is { } name ? name : "Custom";

    string ThresholdLabel(int index) => $"L{index + 1}: {FormatBlocks(pendingThresholds[index])}";

    string DrawDistanceLabel() => $"{FormatBlocks(pendingFarCap)} blocks";

    static string FormatBlocks(int blocks) => blocks.ToString("N0", CultureInfo.InvariantCulture);
}
