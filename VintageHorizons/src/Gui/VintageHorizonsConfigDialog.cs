using System.Globalization;
using Vintagestory.API.Client;

namespace VintageHorizons;

/// <summary>
/// What the Anti-Aliasing tab needs from the mod: the dials in force when the window opened and
/// whether the tint was showing, a way to show a change at once, and the screen's block scale
/// for turning the reach into distances.
/// </summary>
internal sealed record TaaTuning(LodTaaDials Initial, bool ShowInitial,
    Action<LodTaaDials, bool> Preview, Func<float> BlockScale);

/// <summary>
/// Vanilla owns this setting. The editor mirrors its Graphics slider and asks for refreshed
/// automatic LOD values after a committed drag so the whole map stays truthful.
/// </summary>
internal sealed record VanillaDistanceTuning(int Initial, Func<int, int[]> Apply);

/// <summary>
/// One set of coordinates for both the standalone command window and the page embedded in
/// vanilla Settings. Keeping the row edges here makes clipping a testable rule instead of a
/// collection of unrelated literals.
/// </summary>
internal static class VintageHorizonsConfigLayout
{
    internal const double Width = 900;
    internal const double Height = 740;
    internal const double Margin = 32;
    internal const double SwitchX = 824;
    internal const double ButtonY = Height - 58;

    internal const double ScaleY = 96;
    internal const double ScaleHeight = 150;
    internal const double ThresholdLabelsY = 248;
    internal const double DrawHeadingY = 280;
    internal const double VisualHeadingY = 362;
    internal const double VisualRowsY = 400;
    internal const double VisualRowHeight = 36;
    internal const int VisualRowCount = 7;

    internal const double DialSliderX = 330;
    internal const double DialSliderWidth = 400;
    internal const double DialRowsY = 146;
    internal const double DialRowHeight = 58;
    internal const double ShowTaaY = 510;

    internal const double EmbeddedTabsY = 82;
    // Vanilla's header ends at 75. The editor's own two tabs begin below that edge, with their
    // content following beneath, instead of sitting through the header's bottom rule.
    internal const double EmbeddedContentY = 78;
    internal const double EmbeddedHeight = Height + EmbeddedContentY;
}

/// <summary>
/// Client-only settings editor shared by the in-game Settings page and .vhconfig window.
///
/// <para>
/// Two tabs. General is how far and how finely cached terrain is drawn, and which of the
/// optional passes are drawn at all - the settings a machine that cannot afford them needs to
/// be able to give up, each one measured rather than guessed. Anti-Aliasing holds distant
/// anti-aliasing's tuning dials as sliders, added at the owner's request in 0.4.60 so it can be
/// tuned by eye rather than by typed commands.
/// </para>
/// <para>
/// Each feature is a title and a control, with what it does on hover. The descriptions were
/// printed under the titles for one build and it did not work: a medium-weight title is taller
/// than its box, so every row overran the two lines beneath it. That build also had tabs, and
/// its strip clipped the labels to "Det" and "Visual fe". A tab here is as wide as its label,
/// and the strip sits above the window, as vanilla's character window has it, with room for both.
/// </para>
/// <para>
/// The LOD distances are either the player's own, dragged on the scale, or automatic - chosen
/// from the field of view and render resolution so every level hands over at the same size on
/// screen. Automatic shows its live values on the scale and does not let them move; the player's
/// own distances are kept underneath and come back when it is switched off.
/// </para>
/// <para>
/// On General no MOD setting is applied until Save. Vanilla render distance is the deliberate
/// exception: it is a second copy of the game's own control and commits on mouse-up whether this
/// editor is later saved or cancelled. The anti-aliasing sliders show their effect as they move,
/// because judging them by eye is the point: Save keeps them, and Cancel or closing the window
/// any other way puts back what was in force when it opened. The pending values below are the
/// whole mod state of the window, so switching tabs re-composes freely without losing an edit.
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

    const double Width = VintageHorizonsConfigLayout.Width;
    const double Height = VintageHorizonsConfigLayout.Height;
    const double Margin = VintageHorizonsConfigLayout.Margin;
    const double SwitchX = VintageHorizonsConfigLayout.SwitchX;
    const double RowHeight = VintageHorizonsConfigLayout.VisualRowHeight;

    const int GeneralTab = 0;
    const int AntiAliasingTab = 1;

    // The anti-aliasing rows: where each slider starts, how wide it is, and the row pitch.
    const double DialSliderX = VintageHorizonsConfigLayout.DialSliderX;
    const double DialSliderWidth = VintageHorizonsConfigLayout.DialSliderWidth;
    const double DialRowHeight = VintageHorizonsConfigLayout.DialRowHeight;

    const string AutomaticTip =
        "Chooses L1-L6 from your field of view and render resolution, so every level hands over "
        + "at the same size on screen and follows those settings when you change them. Off uses "
        + "the distances you drag on the scale.";

    const string PausedLoadingTip =
        "Keeps cache loading, LOD preparation and bounded terrain updates moving while the "
        + "singleplayer pause menu is open. It does not unpause the game simulation.";

    const string ShowTip =
        "Tints anti-aliased terrain magenta, stronger where it is fully applied, to show what the "
        + "sliders reach. For this session only; it is never saved. (.vhtaa show)";

    readonly Action<int[], bool, int, bool, LodVisualSettings, LodTaaDials> save;
    readonly TaaTuning taa;
    readonly VanillaDistanceTuning vanilla;
    readonly bool embedded;
    readonly Action? requestEmbeddedRecompose;
    readonly Action? requestEmbeddedClose;
    readonly int[] pendingThresholds;
    readonly int[] automaticThresholds;
    bool pendingAutomatic;
    int pendingFarCap;
    bool pendingLoadWhilePaused;
    int vanillaViewDistance;
    LodVisualSettings pendingVisuals;
    LodTaaDials pendingTaa;
    bool pendingShowTaa;
    LodThresholdScaleElement? scale;
    GuiComposer? activeComposer;
    bool sessionClosed;
    double contentTop;

    /// <summary>Which tab is showing: <see cref="GeneralTab"/> or <see cref="AntiAliasingTab"/>.</summary>
    int shownTab = GeneralTab;

    /// <summary>Set by Save, so closing afterwards keeps the anti-aliasing dials rather than putting them back.</summary>
    bool saved;

    public override string DebugName => "vintagehorizons-config";
    public override string? ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => true;

    /// <param name="thresholds">The player's own distances, kept whether or not automatic is on.</param>
    /// <param name="automaticThresholds">What automatic mode would use right now, for display.</param>
    public VintageHorizonsConfigDialog(ICoreClientAPI capi, IReadOnlyList<int> thresholds,
        IReadOnlyList<int> automaticThresholds, bool automatic, int farCap,
        bool loadWhilePaused, LodVisualSettings visuals, VanillaDistanceTuning vanilla,
        TaaTuning taa, Action<int[], bool, int, bool, LodVisualSettings, LodTaaDials> save)
        : this(capi, thresholds, automaticThresholds, automatic, farCap, loadWhilePaused,
            visuals, vanilla, taa, false, null, null, save)
    {
        ComposeDialog();
    }

    /// <summary>
    /// The same stateful editor without its own dialog chrome. The vanilla Settings adapter
    /// owns the surrounding composer and asks us to fill it, re-compose it, or leave it.
    /// </summary>
    internal VintageHorizonsConfigDialog(ICoreClientAPI capi, IReadOnlyList<int> thresholds,
        IReadOnlyList<int> automaticThresholds, bool automatic, int farCap,
        bool loadWhilePaused, LodVisualSettings visuals, VanillaDistanceTuning vanilla,
        TaaTuning taa,
        Action requestRecompose, Action requestClose,
        Action<int[], bool, int, bool, LodVisualSettings, LodTaaDials> save)
        : this(capi, thresholds, automaticThresholds, automatic, farCap, loadWhilePaused,
            visuals, vanilla, taa, true, requestRecompose, requestClose, save)
    {
    }

    VintageHorizonsConfigDialog(ICoreClientAPI capi, IReadOnlyList<int> thresholds,
        IReadOnlyList<int> automaticThresholds, bool automatic, int farCap,
        bool loadWhilePaused, LodVisualSettings visuals, VanillaDistanceTuning vanilla,
        TaaTuning taa, bool embedded,
        Action? requestRecompose, Action? requestClose,
        Action<int[], bool, int, bool, LodVisualSettings, LodTaaDials> save) : base(capi)
    {
        this.save = save;
        this.taa = taa;
        this.vanilla = vanilla;
        vanillaViewDistance = Math.Clamp(vanilla.Initial,
            LodThresholdScaleLayout.MinVanillaDistance,
            LodThresholdScaleLayout.MaxVanillaDistance);
        this.embedded = embedded;
        requestEmbeddedRecompose = requestRecompose;
        requestEmbeddedClose = requestClose;
        pendingThresholds = LodWorld.NormalizeLevelThresholds(
            thresholds, LodWorld.DefaultLevelThresholds[0]);
        this.automaticThresholds = LodWorld.NormalizeLevelThresholds(
            automaticThresholds, LodWorld.DefaultLevelThresholds[0]);
        pendingAutomatic = automatic;
        pendingFarCap = Math.Clamp(farCap <= 0 ? MaxDrawDistance : farCap,
            MinDrawDistance, MaxDrawDistance);
        pendingLoadWhilePaused = loadWhilePaused;
        pendingVisuals = visuals;
        pendingTaa = taa.Initial;
        pendingShowTaa = taa.ShowInitial;
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

        new("distant-aa", "Distant Anti-Aliasing",
            "Stops distant terrain shimmering as you move by blending it over several frames. "
            + "Tune it on the Anti-Aliasing tab.",
            "Low",
            v => v.DistantAntiAliasing,
            (v, on) => v with { DistantAntiAliasing = on }),
    };

    static readonly string[] PresetCodes = { "performance", "balanced", "quality", "custom" };
    static readonly string[] PresetNames = { "Performance", "Balanced", "Quality", "Custom" };

    void ComposeDialog()
    {
        if (sessionClosed) return;
        ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, Width, Height)
            .WithAlignment(EnumDialogArea.CenterMiddle);

        // A re-compose on a tab switch needs no disposal of its own: the engine's CreateCompo
        // disposes the composer already registered under this dialog's name (GuiComposerManager,
        // decompiled 1.22.7), which is why the switch waits until the click that asked for it
        // has finished with the old one.
        GuiComposer composer = capi.Gui
            .CreateCompo(DebugName, dialogBounds)
            .AddShadedDialogBG(ElementBounds.Fill, true)
            .AddDialogTitleBar("Vintage Horizons", OnTitleClose)
            .BeginChildElements();

        activeComposer = composer;
        ComposePage(composer, 0);

        // Added after the children, so the strip hangs off the window itself and sits above it.
        AddTabs(composer.EndChildElements(), ElementBounds.Fixed(12, -28, 420, 28));

        SingleComposer = composer.Compose();
        activeComposer = SingleComposer;
        CompleteComposition();
    }

    /// <summary>Add this editor to the composer made by vanilla's Settings header.</summary>
    internal void ComposeEmbedded(GuiComposer composer)
    {
        if (!embedded || sessionClosed)
            throw new InvalidOperationException("This Vintage Horizons editor is not active in Settings.");

        activeComposer = composer;
        AddTabs(composer, ElementBounds.Fixed(Margin,
            VintageHorizonsConfigLayout.EmbeddedTabsY, 420, 28));
        ComposePage(composer, VintageHorizonsConfigLayout.EmbeddedContentY);
    }

    internal void CompleteEmbeddedComposition() => CompleteComposition();

    internal void CloseEmbedded() => CloseSession();

    void ComposePage(GuiComposer composer, double top)
    {
        contentTop = top;
        scale = null;
        if (shownTab == AntiAliasingTab) ComposeAntiAliasing(composer);
        else ComposeGeneral(composer);

        composer.AddButton("Defaults", OnDefaults,
            ElementBounds.Fixed(Margin, Y(VintageHorizonsConfigLayout.ButtonY), 140, 38),
            EnumButtonStyle.Normal, "defaults");
        composer.AddButton("Cancel", OnCancel,
            ElementBounds.Fixed(Width - Margin - 275, Y(VintageHorizonsConfigLayout.ButtonY), 130, 38),
            EnumButtonStyle.Normal, "cancel");
        composer.AddButton("Save", OnSave,
            ElementBounds.Fixed(Width - Margin - 140, Y(VintageHorizonsConfigLayout.ButtonY), 140, 38),
            EnumButtonStyle.Normal, "save");
    }

    void AddTabs(GuiComposer composer, ElementBounds bounds)
    {
        composer.AddHorizontalTabs(
            new[]
            {
                new GuiTab { Name = "General", DataInt = GeneralTab },
                new GuiTab { Name = "Anti-Aliasing", DataInt = AntiAliasingTab },
            },
            bounds, OnTabClicked,
            CairoFont.WhiteSmallText(), CairoFont.WhiteSmallText().WithColor(GuiStyle.ActiveButtonTextColor),
            "tabs");
    }

    void CompleteComposition()
    {
        if (activeComposer?.Composed != true) return;
        activeComposer.GetHorizontalTabs("tabs").activeElement = shownTab;

        if (shownTab == AntiAliasingTab) SetAntiAliasingValues();
        else SetGeneralValues();
    }

    double Y(double y) => contentTop + y;

    void ComposeGeneral(GuiComposer composer)
    {
        composer.AddStaticText("LOD transition distances",
            CairoFont.WhiteMediumText(), ElementBounds.Fixed(Margin, Y(42), 480, 30));
        composer.AddStaticText("Automatic", CairoFont.WhiteSmallText(),
            EnumTextOrientation.Right, ElementBounds.Fixed(SwitchX - 190, Y(48), 180, 22));
        composer.AddSwitch(OnAutomaticChanged,
            ElementBounds.Fixed(SwitchX, Y(42), 32, 32), "auto-thresholds");
        // Over the label and the switch alike. GuiElementHoverText handles no mouse-down, so it
        // cannot swallow the click on the switch underneath it.
        composer.AddHoverText(AutomaticTip, CairoFont.WhiteSmallText(), 380,
            ElementBounds.Fixed(SwitchX - 190, Y(40), 222, 36), "tip-auto-thresholds");
        composer.AddDynamicText(ThresholdHelp(), CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(Margin, Y(72), Width - 2 * Margin, 22), "threshold-help");

        scale = new LodThresholdScaleElement(capi, DisplayedThresholds(),
            vanillaViewDistance, OnThresholdChanged, OnVanillaDistanceChanged,
            ElementBounds.Fixed(Margin, Y(VintageHorizonsConfigLayout.ScaleY),
                Width - 2 * Margin, VintageHorizonsConfigLayout.ScaleHeight))
        {
            ReadOnly = pendingAutomatic,
        };
        composer.AddInteractiveElement(scale, "lod-threshold-scale");

        double thresholdWidth = (Width - 2 * Margin) / LodWorld.MaxLevel;
        for (int i = 0; i < LodWorld.MaxLevel; i++)
        {
            composer.AddDynamicText(ThresholdLabel(i),
                CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Center),
                ElementBounds.Fixed(Margin + i * thresholdWidth,
                    Y(VintageHorizonsConfigLayout.ThresholdLabelsY), thresholdWidth, 22),
                $"threshold-{i}");
        }

        composer.AddStaticText("Cached terrain draw distance", CairoFont.WhiteMediumText(),
            ElementBounds.Fixed(Margin, Y(VintageHorizonsConfigLayout.DrawHeadingY), 500, 30));
        composer.AddStaticText("Load terrain while paused", CairoFont.WhiteSmallishText(),
            EnumTextOrientation.Right,
            ElementBounds.Fixed(552, Y(VintageHorizonsConfigLayout.DrawHeadingY + 5), 262, 26));
        composer.AddSwitch(OnPausedLoadingChanged,
            ElementBounds.Fixed(SwitchX, Y(VintageHorizonsConfigLayout.DrawHeadingY), 32, 32),
            "load-while-paused");
        composer.AddHoverText(PausedLoadingTip, CairoFont.WhiteSmallText(), 380,
            ElementBounds.Fixed(552, Y(VintageHorizonsConfigLayout.DrawHeadingY),
                SwitchX + 32 - 552, 34), "tip-load-while-paused");
        composer.AddSlider(OnDrawDistanceChanged,
            ElementBounds.Fixed(Margin, Y(VintageHorizonsConfigLayout.DrawHeadingY + 34), 680, 30),
            "draw-distance");
        composer.AddDynamicText(DrawDistanceLabel(),
            CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Right),
            ElementBounds.Fixed(Width - Margin - 120,
                Y(VintageHorizonsConfigLayout.DrawHeadingY + 38), 120, 22),
            "draw-distance-label");

        composer.AddStaticText("Visual features", CairoFont.WhiteMediumText(),
            ElementBounds.Fixed(Margin, Y(VintageHorizonsConfigLayout.VisualHeadingY), 230, 30));
        composer.AddStaticText("Preset", CairoFont.WhiteDetailText(),
            EnumTextOrientation.Right,
            ElementBounds.Fixed(250, Y(VintageHorizonsConfigLayout.VisualHeadingY + 8), 80, 22));
        composer.AddDropDown(PresetCodes, PresetNames, PresetIndex(), OnPresetSelected,
            ElementBounds.Fixed(342, Y(VintageHorizonsConfigLayout.VisualHeadingY - 2), 224, 32),
            "preset");

        // The cost words are a column, so they get a column heading. It sits over the same box
        // the words themselves use and shares their right edge, because a right-aligned word
        // under a left-aligned heading does not read as one column.
        composer.AddStaticText("Cost", CairoFont.WhiteDetailText(),
            EnumTextOrientation.Right,
            ElementBounds.Fixed(SwitchX - 110,
                Y(VintageHorizonsConfigLayout.VisualHeadingY + 10), 100, 22));

        double y = VintageHorizonsConfigLayout.VisualRowsY;
        foreach (VisualRow row in Rows)
        {
            composer.AddStaticText(row.Title, CairoFont.WhiteSmallishText(),
                ElementBounds.Fixed(Margin, Y(y + 5), 650, 26));
            composer.AddStaticText(row.Cost, CairoFont.WhiteDetailText(),
                EnumTextOrientation.Right,
                ElementBounds.Fixed(SwitchX - 110, Y(y + 8), 100, 22));
            composer.AddSwitch(on => OnVisualChanged(row, on),
                ElementBounds.Fixed(SwitchX, Y(y), 32, 32), row.Key);

            // One hover region over the whole row, so the tip appears from the title, the cost
            // word or the switch alike. GuiElementHoverText handles no mouse-down, so this
            // cannot swallow the click that lands on the switch underneath it.
            composer.AddHoverText(row.Tip, CairoFont.WhiteSmallText(), 380,
                ElementBounds.Fixed(Margin, Y(y), SwitchX + 32 - Margin, RowHeight - 2),
                $"tip-{row.Key}");
            y += RowHeight;
        }
    }

    void SetGeneralValues()
    {
        if (activeComposer?.Composed != true) return;
        GuiElementSlider drawSlider = activeComposer.GetSlider("draw-distance");
        drawSlider.SetValues(pendingFarCap, MinDrawDistance, MaxDrawDistance, DrawDistanceStep,
            " blocks");
        drawSlider.ShowTextWhenResting = false;
        activeComposer.GetSwitch("auto-thresholds").On = pendingAutomatic;
        activeComposer.GetSwitch("load-while-paused").On = pendingLoadWhilePaused;
        foreach (VisualRow row in Rows)
        {
            activeComposer.GetSwitch(row.Key).On = row.Read(pendingVisuals);
        }
    }

    void ComposeAntiAliasing(GuiComposer composer)
    {
        composer.AddStaticText("Distant anti-aliasing", CairoFont.WhiteMediumText(),
            ElementBounds.Fixed(Margin, Y(42), 480, 30));
        composer.AddStaticText(
            "Changes show at once. Save keeps them, Cancel puts them back, Defaults resets this tab.",
            CairoFont.WhiteSmallText(), ElementBounds.Fixed(Margin, Y(74), Width - 2 * Margin, 22));
        composer.AddDynamicText(ReachHelp(), CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(Margin, Y(100), Width - 2 * Margin, 22), "taa-reach-help");

        double y = VintageHorizonsConfigLayout.DialRowsY;
        foreach (LodTaaSliders.Row row in LodTaaSliders.All)
        {
            composer.AddStaticText(row.Title, CairoFont.WhiteSmallishText(),
                ElementBounds.Fixed(Margin, Y(y + 3), DialSliderX - Margin - 12, 26));
            // Over the title only. A slider is dragged by mouse moves, and a tip region lying over
            // it could be the element that gets them.
            composer.AddHoverText(row.Tip, CairoFont.WhiteSmallText(), 380,
                ElementBounds.Fixed(Margin, Y(y - 2), DialSliderX - Margin - 12, 34),
                $"tip-{row.Key}");
            composer.AddSlider(value => OnDialChanged(row, value),
                ElementBounds.Fixed(DialSliderX, Y(y), DialSliderWidth, 30), row.Key);
            composer.AddDynamicText(row.Label(row.Read(pendingTaa)), CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(DialSliderX + DialSliderWidth + 15, Y(y + 4), 110, 22),
                $"{row.Key}-label");
            y += DialRowHeight;
        }

        composer.AddStaticText("Show Anti-Aliased Area", CairoFont.WhiteMediumText(),
            ElementBounds.Fixed(Margin, Y(VintageHorizonsConfigLayout.ShowTaaY + 2), 650, 30));
        composer.AddSwitch(OnShowChanged,
            ElementBounds.Fixed(SwitchX, Y(VintageHorizonsConfigLayout.ShowTaaY), 32, 32),
            "taa-show");
        composer.AddHoverText(ShowTip, CairoFont.WhiteSmallText(), 380,
            ElementBounds.Fixed(Margin, Y(VintageHorizonsConfigLayout.ShowTaaY),
                SwitchX + 32 - Margin, RowHeight - 2), "tip-taa-show");
    }

    void SetAntiAliasingValues()
    {
        if (activeComposer?.Composed != true) return;
        foreach (LodTaaSliders.Row row in LodTaaSliders.All)
        {
            GuiElementSlider slider = activeComposer.GetSlider(row.Key);
            slider.SetValues(row.ToSlider(row.Read(pendingTaa)), row.Min, row.Max, row.Step);
            slider.ShowTextWhenResting = false;
            slider.OnSliderTooltip = value => row.Label(row.FromSlider(value));
        }
        activeComposer.GetSwitch("taa-show").On = pendingShowTaa;
    }

    void OnTabClicked(int tab)
    {
        if (tab == shownTab) return;
        shownTab = tab;
        // Once this click has finished with the composer it came from, not in the middle of it:
        // the re-compose disposes that composer.
        capi.Event.EnqueueMainThreadTask(() =>
        {
            if (sessionClosed) return;
            if (embedded) requestEmbeddedRecompose?.Invoke();
            else if (IsOpened()) ComposeDialog();
        }, "vintagehorizons-config-tab");
    }

    bool OnDialChanged(LodTaaSliders.Row row, int value)
    {
        pendingTaa = row.Write(pendingTaa, row.FromSlider(value));
        taa.Preview(pendingTaa, pendingShowTaa);
        if (activeComposer?.Composed == true && shownTab == AntiAliasingTab)
        {
            activeComposer.GetDynamicText($"{row.Key}-label")
                .SetNewText(row.Label(row.Read(pendingTaa)), false, true, false);
            activeComposer.GetDynamicText("taa-reach-help")
                .SetNewText(ReachHelp(), false, true, false);
        }
        return true;
    }

    void OnShowChanged(bool on)
    {
        pendingShowTaa = on;
        taa.Preview(pendingTaa, pendingShowTaa);
    }

    /// <summary>Every slider and label on the Anti-Aliasing tab rewritten from the pending dials.</summary>
    void RefreshDials()
    {
        if (activeComposer?.Composed != true || shownTab != AntiAliasingTab) return;
        foreach (LodTaaSliders.Row row in LodTaaSliders.All)
        {
            activeComposer.GetSlider(row.Key).SetValue(row.ToSlider(row.Read(pendingTaa)));
            activeComposer.GetDynamicText($"{row.Key}-label")
                .SetNewText(row.Label(row.Read(pendingTaa)), false, true, false);
        }
        activeComposer.GetDynamicText("taa-reach-help")
            .SetNewText(ReachHelp(), false, true, false);
    }

    /// <summary>The reach as distances, at the field of view and resolution of the last frame drawn.</summary>
    string ReachHelp()
    {
        float blockScale = taa.BlockScale();
        if (!(blockScale > 0f)) return "Distances show once a frame has been drawn.";
        float reach = LodTaaMath.ClampReach(pendingTaa.Reach);
        int begins = (int)MathF.Round(LodScreenScale.DepthForBlockPixels(blockScale, reach));
        int full = (int)MathF.Round(LodScreenScale.DepthForBlockPixels(blockScale,
            LodTaaMath.FullPixels(reach)));
        return $"At this FOV and resolution: starts about {FormatBlocks(begins)} blocks out, "
            + $"full strength past {FormatBlocks(full)}.";
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
        if (activeComposer?.Composed != true || shownTab != GeneralTab) return;
        foreach (VisualRow other in Rows)
        {
            GuiElementSwitch element = activeComposer.GetSwitch(other.Key);
            bool wanted = other.Read(pendingVisuals);
            if (element.On != wanted) element.On = wanted;
        }
        activeComposer.GetDropDown("preset").SetSelectedIndex(PresetIndex());
    }

    /// <summary>What the scale shows: automatic's live values, or the player's own.</summary>
    int[] DisplayedThresholds() => pendingAutomatic ? automaticThresholds : pendingThresholds;

    string ThresholdHelp() => pendingAutomatic
        ? "Drag Vanilla in 32-block steps (applies immediately). Grey is vanilla; colour starts at L0. "
            + "L1-L6 are automatic."
        : "Drag Vanilla (applies immediately) or L1-L6. Grey is vanilla; colour starts at L0.";

    void OnAutomaticChanged(bool on)
    {
        pendingAutomatic = on;
        RefreshThresholds();
    }

    void RefreshThresholds()
    {
        if (activeComposer?.Composed != true || shownTab != GeneralTab) return;
        if (scale != null)
        {
            scale.ReadOnly = pendingAutomatic;
            scale.SetValues(DisplayedThresholds());
        }
        for (int i = 0; i < pendingThresholds.Length; i++)
        {
            activeComposer.GetDynamicText($"threshold-{i}")
                .SetNewText(ThresholdLabel(i), false, true, false);
        }
        activeComposer.GetDynamicText("threshold-help")
            .SetNewText(ThresholdHelp(), false, true, false);
        GuiElementSwitch automatic = activeComposer.GetSwitch("auto-thresholds");
        if (automatic.On != pendingAutomatic) automatic.On = pendingAutomatic;
    }

    void OnThresholdChanged(int index, int value)
    {
        // The scale is read-only while automatic is on, so a drag can only be the player's own.
        if (pendingAutomatic) return;
        pendingThresholds[index] = value;
        if (activeComposer?.Composed == true && shownTab == GeneralTab)
        {
            activeComposer.GetDynamicText($"threshold-{index}")
                .SetNewText(ThresholdLabel(index), false, true, false);
        }
    }

    void OnVanillaDistanceChanged(int value)
    {
        vanillaViewDistance = Math.Clamp(value,
            LodThresholdScaleLayout.MinVanillaDistance,
            LodThresholdScaleLayout.MaxVanillaDistance);
        int[] refreshed = vanilla.Apply(vanillaViewDistance);
        if (refreshed.Length == automaticThresholds.Length)
        {
            int[] normalized = LodWorld.NormalizeLevelThresholds(
                refreshed, LodWorld.DefaultLevelThresholds[0]);
            Array.Copy(normalized, automaticThresholds, automaticThresholds.Length);
        }

        scale?.SetVanillaDistance(vanillaViewDistance);
        if (pendingAutomatic) scale?.SetValues(automaticThresholds);
        if (activeComposer?.Composed != true || shownTab != GeneralTab) return;
        for (int i = 0; i < pendingThresholds.Length; i++)
        {
            activeComposer.GetDynamicText($"threshold-{i}")
                .SetNewText(ThresholdLabel(i), false, true, false);
        }
        activeComposer.GetDynamicText("threshold-help")
            .SetNewText(ThresholdHelp(), false, true, false);
    }

    void OnPausedLoadingChanged(bool on) => pendingLoadWhilePaused = on;

    bool OnDrawDistanceChanged(int blocks)
    {
        pendingFarCap = Math.Clamp(blocks, MinDrawDistance, MaxDrawDistance);
        if (activeComposer?.Composed == true && shownTab == GeneralTab)
        {
            activeComposer.GetDynamicText("draw-distance-label")
                .SetNewText(DrawDistanceLabel(), false, true, false);
        }
        return true;
    }

    /// <summary>
    /// The tab that is showing, back to what a fresh install draws. On Anti-Aliasing that shows
    /// at once, like any slider; on General nothing applies until Save.
    /// </summary>
    bool OnDefaults()
    {
        if (shownTab == AntiAliasingTab)
        {
            pendingTaa = LodTaaDials.Default;
            taa.Preview(pendingTaa, pendingShowTaa);
            RefreshDials();
            return true;
        }

        Array.Copy(LodWorld.DefaultLevelThresholds, pendingThresholds,
            pendingThresholds.Length);
        pendingAutomatic = true;
        pendingFarCap = DefaultDrawDistance;
        pendingLoadWhilePaused = true;
        pendingVisuals = LodVisualSettings.Default;

        RefreshThresholds();
        if (activeComposer?.Composed != true) return true;
        activeComposer.GetSlider("draw-distance").SetValues(
            pendingFarCap, MinDrawDistance, MaxDrawDistance, DrawDistanceStep, " blocks");
        activeComposer.GetDynamicText("draw-distance-label")
            .SetNewText(DrawDistanceLabel(), false, true, false);
        activeComposer.GetSwitch("load-while-paused").On = pendingLoadWhilePaused;
        RefreshVisuals();
        return true;
    }

    bool OnSave()
    {
        if (sessionClosed) return true;
        saved = true;
        save(pendingThresholds, pendingAutomatic, pendingFarCap, pendingLoadWhilePaused,
            pendingVisuals, pendingTaa);
        if (embedded)
        {
            sessionClosed = true;
            activeComposer = null;
            requestEmbeddedClose?.Invoke();
        }
        else
        {
            TryClose();
        }
        return true;
    }

    bool OnCancel()
    {
        if (embedded)
        {
            CloseSession();
            requestEmbeddedClose?.Invoke();
        }
        else
        {
            TryClose();
        }
        return true;
    }

    void OnTitleClose() => TryClose();

    /// <summary>
    /// Closing without Save - Cancel, the title bar's cross, or Escape - puts the anti-aliasing
    /// dials and the tint back to what was in force when the window opened, because the sliders
    /// showed their effect as they moved.
    /// </summary>
    public override void OnGuiClosed()
    {
        CloseSession();
        base.OnGuiClosed();
    }

    public override void Dispose()
    {
        CloseSession();
        base.Dispose();
    }

    void CloseSession()
    {
        if (sessionClosed) return;
        sessionClosed = true;
        activeComposer = null;
        if (!saved) taa.Preview(taa.Initial, taa.ShowInitial);
    }

    string ThresholdLabel(int index) =>
        $"L{index + 1}: {FormatBlocks(DisplayedThresholds()[index])}";

    string DrawDistanceLabel() => $"{FormatBlocks(pendingFarCap)} blocks";

    static string FormatBlocks(int blocks) => blocks.ToString("N0", CultureInfo.InvariantCulture);
}
