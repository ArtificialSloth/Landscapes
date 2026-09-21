using System.Globalization;
using Vintagestory.API.Client;
using VintageHorizons.Net;

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
    internal const double Height = 820;
    internal const double Margin = 32;
    internal const double SwitchX = 824;
    internal const double ButtonY = Height - 58;
    internal const double TabsWidth = 640;
    internal const int PlannedTabCount = 4;

    internal const double ScaleY = 96;
    internal const double ScaleHeight = 210;
    internal const double DrawHeadingY = 326;
    internal const double VisualHeadingY = 394;
    internal const double VisualRowsY = 432;
    internal const double VisualRowHeight = 36;
    internal const int VisualRowCount = 7;

    internal const double DialSliderX = 330;
    internal const double DialSliderWidth = 400;
    internal const double DialRowsY = 146;
    internal const double DialRowHeight = 58;
    internal const double ShowTaaY = 510;

    internal const double EmbeddedTabsY = 82;
    // Vanilla's header ends at 75. The editor's own tabs begin below that edge, with their
    // content following beneath, instead of sitting through the header's bottom rule.
    internal const double EmbeddedContentY = 78;
    internal const double EmbeddedHeight = Height + EmbeddedContentY;
}

/// <summary>
/// Client-only settings editor shared by the in-game Settings page and .vhconfig window.
///
/// <para>
/// Four tabs. General is how far and how finely cached terrain is drawn, and which of the
/// optional passes are drawn at all - the settings a machine that cannot afford them needs to
/// be able to give up, each one measured rather than guessed. Anti-Aliasing holds distant
/// anti-aliasing's tuning dials as sliders, added at the owner's request in 0.4.60 so it can be
/// tuned by eye rather than by typed commands. Generate controls the server-owned, explicitly
/// confirmed savegame generator and remains read-only when the player lacks permission.
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
    const int SilhouettesTab = 2;
    const int GenerateTab = 3;

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

    readonly Action<int[], bool, int, bool, LodVisualSettings, LodTaaDials,
        LodSilhouetteSettings> save;
    readonly TaaTuning taa;
    readonly VanillaDistanceTuning vanilla;
    readonly WorldGenTuning worldGen;
    readonly SilhouetteTuning silhouettes;
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
    LodSilhouetteSettings pendingSilhouettes;
    bool pendingShowTaa;
    LodWorldGenPreferences pendingWorldGen;
    LodThresholdScaleElement? scale;
    GuiComposer? activeComposer;
    bool sessionClosed;
    double contentTop;

    /// <summary>Which editor tab is showing.</summary>
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
        TaaTuning taa, SilhouetteTuning silhouettes, WorldGenTuning worldGen,
        Action<int[], bool, int, bool, LodVisualSettings, LodTaaDials,
            LodSilhouetteSettings> save)
        : this(capi, thresholds, automaticThresholds, automatic, farCap, loadWhilePaused,
            visuals, vanilla, taa, silhouettes, worldGen, false, null, null, save)
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
        TaaTuning taa, SilhouetteTuning silhouettes, WorldGenTuning worldGen,
        Action requestRecompose, Action requestClose,
        Action<int[], bool, int, bool, LodVisualSettings, LodTaaDials,
            LodSilhouetteSettings> save)
        : this(capi, thresholds, automaticThresholds, automatic, farCap, loadWhilePaused,
            visuals, vanilla, taa, silhouettes, worldGen, true, requestRecompose, requestClose, save)
    {
    }

    VintageHorizonsConfigDialog(ICoreClientAPI capi, IReadOnlyList<int> thresholds,
        IReadOnlyList<int> automaticThresholds, bool automatic, int farCap,
        bool loadWhilePaused, LodVisualSettings visuals, VanillaDistanceTuning vanilla,
        TaaTuning taa, SilhouetteTuning silhouettes, WorldGenTuning worldGen, bool embedded,
        Action? requestRecompose, Action? requestClose,
        Action<int[], bool, int, bool, LodVisualSettings, LodTaaDials,
            LodSilhouetteSettings> save) : base(capi)
    {
        this.save = save;
        this.taa = taa;
        this.silhouettes = silhouettes;
        this.vanilla = vanilla;
        this.worldGen = worldGen;
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
        pendingSilhouettes = silhouettes.Initial.Normalize();
        pendingShowTaa = taa.ShowInitial;
        pendingWorldGen = worldGen.Initial.Normalize(ServerWorldGenMaxRadius());
        if (worldGen.Client != null)
            worldGen.Client.WorldGenStatusChanged += OnWorldGenStatus;
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

    readonly record struct SilhouetteRow(
        string Key, string Title, string Tip, int Min, int Max, int Step,
        Func<LodSilhouetteSettings, int> Read,
        Func<LodSilhouetteSettings, int, LodSilhouetteSettings> Write,
        Func<int, string> Label);

    static readonly SilhouetteRow[] SilhouetteRows =
    {
        new("sil-sky", "Sky-height tint", "How strongly height above or below sea level changes the sampled sky colour.",
            0, 100, 1, s => (int)MathF.Round(s.SkyTint * 10),
            (s, v) => s with { SkyTint = v / 10f }, v => (v / 10f).ToString("0.0")),
        new("sil-red", "Tint red", "Red channel of Farseer's colour tint.",
            0, 100, 1, s => (int)MathF.Round(s.TintRed * 100),
            (s, v) => s with { TintRed = v / 100f }, v => (v / 100f).ToString("0.00")),
        new("sil-green", "Tint green", "Green channel of Farseer's colour tint.",
            0, 100, 1, s => (int)MathF.Round(s.TintGreen * 100),
            (s, v) => s with { TintGreen = v / 100f }, v => (v / 100f).ToString("0.00")),
        new("sil-blue", "Tint blue", "Blue channel of Farseer's colour tint.",
            0, 100, 1, s => (int)MathF.Round(s.TintBlue * 100),
            (s, v) => s with { TintBlue = v / 100f }, v => (v / 100f).ToString("0.00")),
        new("sil-strength", "Tint strength", "How far the sampled sky colour is mixed toward the tint.",
            0, 100, 1, s => (int)MathF.Round(s.TintStrength * 100),
            (s, v) => s with { TintStrength = v / 100f }, v => (v / 100f).ToString("0.00")),
        new("sil-light", "Light bias", "Shapes daylight response. Higher values retain more low light.",
            51, 99, 1, s => (int)MathF.Round(s.LightLevelBias * 100),
            (s, v) => s with { LightLevelBias = v / 100f }, v => (v / 100f).ToString("0.00")),
        new("sil-fade", "Far fade bias", "Shapes how Farseer terrain fades back into the real sky at its reach.",
            5, 95, 1, s => (int)MathF.Round(s.FadeBias * 100),
            (s, v) => s with { FadeBias = v / 100f }, v => (v / 100f).ToString("0.00")),
        new("sil-globe", "Globe curve", "Gently lowers only far Farseer terrain to hide the finite horizon edge.",
            0, 200, 1, s => (int)MathF.Round(s.GlobeEffect * 1000),
            (s, v) => s with { GlobeEffect = v / 1000f }, v => (v / 1000f).ToString("0.000")),
        new("sil-underlay", "LOD edge depth", "How far the short Farseer apron slopes below the cached-terrain edge. Zero keeps only Farseer's small original transition; higher hides larger sky gaps.",
            (int)Silhouette.LodSilhouetteField.MinUnderlayDepthBlocks,
            (int)Silhouette.LodSilhouetteField.MaxUnderlayDepthBlocks, 4,
            s => (int)MathF.Round(s.UnderlayDepthBlocks),
            (s, v) => s with { UnderlayDepthBlocks = v }, v => $"{v} blocks"),
        new("sil-underlay-width", "LOD edge width", "How far the Farseer edge takes to climb from its lowered apron back to the real sampled terrain height.",
            (int)Silhouette.LodSilhouetteField.MinUnderlayWidthBlocks,
            (int)Silhouette.LodSilhouetteField.MaxUnderlayWidthBlocks, 32,
            s => (int)MathF.Round(s.UnderlayWidthBlocks),
            (s, v) => s with { UnderlayWidthBlocks = v }, v => $"{v} blocks"),
        new("sil-detail", "Geometry detail", "Controls only how dense the drawn Farseer mesh is. Lower is lighter; hill softness controls the outline shape.",
            0, 100, 1, s => (int)MathF.Round(s.OutlineDetail * 100),
            (s, v) => s with { OutlineDetail = v / 100f }, OutlineDetailLabel),
        new("sil-softness", "Hill softness", "Rounds and feathers the distant height profile. Higher produces broader rolling hills; regeneration is gradual and runs off-thread.",
            0, 100, 1, s => (int)MathF.Round(s.HillSoftness * 100),
            (s, v) => s with { HillSoftness = v / 100f }, HillSoftnessLabel),
        new("sil-fog-strength", "Fog density", "Overall opacity of the fog hiding the Farseer/LOD ownership seam. Zero disables the fog pass.",
            0, 200, 5, s => (int)MathF.Round(s.FogStrength * 100),
            (s, v) => s with { FogStrength = v / 100f }, v => $"{v}%"),
        new("sil-fog-coverage", "Fog coverage", "Minimum veil left inside low-density noise pockets. Lower makes more open gaps; higher hides the seam more continuously.",
            0, 100, 5, s => (int)MathF.Round(s.FogCoverage * 100),
            (s, v) => s with { FogCoverage = v / 100f }, v => $"{v}%"),
        new("sil-fog-spill", "Fog spread", "How far the fog spills across both Farseer and real LOD terrain from the ownership seam.",
            (int)LodSilhouetteFogVolume.MinSpillBlocks,
            (int)LodSilhouetteFogVolume.MaxSpillBlocks, 4,
            s => (int)MathF.Round(s.FogSpillBlocks),
            (s, v) => s with { FogSpillBlocks = v }, v => $"{v} blocks"),
        new("sil-fog-height", "Fog height", "How high the seam fog hangs above the sampled terrain surface.",
            (int)LodSilhouetteFogVolume.MinHeightBlocks,
            (int)LodSilhouetteFogVolume.MaxHeightBlocks, 4,
            s => (int)MathF.Round(s.FogHeightBlocks),
            (s, v) => s with { FogHeightBlocks = v }, v => $"{v} blocks"),
        new("sil-fog-floor", "Fog floor depth", "How far the fog extends below the sampled terrain surface to cover cliffs, shores and vertical gaps.",
            (int)LodSilhouetteFogVolume.MinFloorDepthBlocks,
            (int)LodSilhouetteFogVolume.MaxFloorDepthBlocks, 2,
            s => (int)MathF.Round(s.FogFloorDepthBlocks),
            (s, v) => s with { FogFloorDepthBlocks = v }, v => $"{v} blocks"),
        new("sil-fog-billow", "Fog billow size", "World-space size of the broad cloud shapes. Higher gives larger, slower-changing banks; lower gives smaller wisps.",
            (int)LodSilhouetteFogVolume.MinBillowSizeBlocks,
            (int)LodSilhouetteFogVolume.MaxBillowSizeBlocks, 4,
            s => (int)MathF.Round(s.FogBillowSizeBlocks),
            (s, v) => s with { FogBillowSizeBlocks = v }, v => $"{v} blocks"),
        new("sil-fog-drift", "Fog drift speed", "How quickly the world-anchored billows move. Zero keeps their shape stationary.",
            0, 300, 5, s => (int)MathF.Round(s.FogDriftSpeed * 100),
            (s, v) => s with { FogDriftSpeed = v / 100f }, v => $"{v}%"),
        new("sil-fog-edge", "Fog edge softness", "How far a broad world-anchored wobble pushes the fog's rim in and out. Higher dissolves the chunk-grid outline and rounds its corners; zero leaves the bare geometric edge.",
            0, 100, 5, s => (int)MathF.Round(s.FogEdgeSoftness * 100),
            (s, v) => s with { FogEdgeSoftness = v / 100f }, v => $"{v}%"),
        new("sil-fog-detail", "Fog quality", "Raymarch precision through the continuous fog volume. Higher preserves finer billow and terrain detail but costs more fragment work.",
            0, 100, 1, s => (int)MathF.Round(s.FogLayerDetail * 100),
            (s, v) => s with { FogLayerDetail = v / 100f }, FogQualityLabel),
    };

    static string OutlineDetailLabel(int value) => $"{value}% " + (value switch
    {
        <= 20 => "Lightest",
        <= 40 => "Light",
        <= 60 => "Balanced",
        <= 80 => "Fine",
        _ => "Finest",
    });

    static string HillSoftnessLabel(int value) => $"{value}% " + (value switch
    {
        <= 20 => "Angular",
        <= 45 => "Gentle",
        <= 75 => "Rolling",
        _ => "Very soft",
    });

    static string FogQualityLabel(int value) => $"{value}% " + (value switch
    {
        <= 20 => "Low",
        <= 55 => "Medium",
        <= 80 => "High",
        _ => "Highest",
    });

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
        AddTabs(composer.EndChildElements(), ElementBounds.Fixed(
            12, -28, VintageHorizonsConfigLayout.TabsWidth, 28));

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
            VintageHorizonsConfigLayout.EmbeddedTabsY,
            VintageHorizonsConfigLayout.TabsWidth, 28));
        ComposePage(composer, VintageHorizonsConfigLayout.EmbeddedContentY);
    }

    internal void CompleteEmbeddedComposition() => CompleteComposition();

    internal void CloseEmbedded() => CloseSession();

    void ComposePage(GuiComposer composer, double top)
    {
        contentTop = top;
        scale = null;
        if (shownTab == AntiAliasingTab) ComposeAntiAliasing(composer);
        else if (shownTab == SilhouettesTab) ComposeSilhouettes(composer);
        else if (shownTab == GenerateTab) ComposeGenerate(composer);
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
                new GuiTab { Name = "Farseer", DataInt = SilhouettesTab },
                new GuiTab { Name = "Generate", DataInt = GenerateTab },
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
        else if (shownTab == SilhouettesTab) SetSilhouetteValues();
        else if (shownTab == GenerateTab) SetGenerateValues();
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
            vanillaViewDistance, pendingFarCap, pendingSilhouettes.ReachBlocks,
            pendingSilhouettes.Enabled, OnThresholdChanged, OnVanillaDistanceChanged,
            blocks => OnDrawDistanceChanged(blocks), OnSilhouetteReachChanged,
            ElementBounds.Fixed(Margin, Y(VintageHorizonsConfigLayout.ScaleY),
                Width - 2 * Margin, VintageHorizonsConfigLayout.ScaleHeight))
        {
            ReadOnly = pendingAutomatic,
        };
        composer.AddInteractiveElement(scale, "lod-threshold-scale");

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
        composer.AddDynamicText(DrawDistanceLabel(),
            CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Right),
            ElementBounds.Fixed(Width - Margin - 120,
                Y(VintageHorizonsConfigLayout.DrawHeadingY + 8), 120, 22),
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

    void ComposeSilhouettes(GuiComposer composer)
    {
        composer.AddStaticText("Farseer horizon terrain", CairoFont.WhiteMediumText(),
            ElementBounds.Fixed(Margin, Y(42), 500, 30));
        composer.AddStaticText("Enabled", CairoFont.WhiteSmallishText(),
            EnumTextOrientation.Right, ElementBounds.Fixed(SwitchX - 150, Y(48), 140, 24));
        composer.AddSwitch(OnSilhouetteEnabledChanged,
            ElementBounds.Fixed(SwitchX, Y(42), 32, 32), "silhouette-enabled");
        composer.AddDynamicText(silhouettes.Status(), CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(Margin, Y(78), Width - 2 * Margin, 36), "silhouette-status");

        composer.AddStaticText("Reach", CairoFont.WhiteSmallishText(),
            ElementBounds.Fixed(Margin, Y(122), 100, 26));
        composer.AddSlider(OnSilhouetteReachSlider,
            ElementBounds.Fixed(135, Y(116), 545, 30), "silhouette-reach");
        composer.AddDynamicText(SilhouetteReachLabel(), CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(700, Y(122), 160, 24), "silhouette-reach-label");

        const double columnWidth = 250;
        const double columnGap = 29;
        const double sliderWidth = 160;
        const double rowHeight = 78;
        for (int i = 0; i < SilhouetteRows.Length; i++)
        {
            SilhouetteRow row = SilhouetteRows[i];
            int column = i % 3;
            int line = i / 3;
            double x = Margin + column * (columnWidth + columnGap);
            double y = 172 + line * rowHeight;
            composer.AddStaticText(row.Title, CairoFont.WhiteSmallishText(),
                ElementBounds.Fixed(x, Y(y), columnWidth, 26));
            composer.AddHoverText(row.Tip, CairoFont.WhiteSmallText(), 330,
                ElementBounds.Fixed(x, Y(y - 2), columnWidth, 28), $"tip-{row.Key}");
            composer.AddSlider(value => OnSilhouetteDialChanged(row, value),
                ElementBounds.Fixed(x, Y(y + 30), sliderWidth, 30), row.Key);
            int shown = row.Read(pendingSilhouettes);
            composer.AddDynamicText(row.Label(shown), CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(x + sliderWidth + 12, Y(y + 35), 92, 22), $"{row.Key}-label");
        }

        composer.AddStaticText(
            "Changes on this tab show immediately. Off releases received tiles and all Farseer GPU meshes.",
            CairoFont.WhiteSmallText(), ElementBounds.Fixed(Margin, Y(724), Width - 2 * Margin, 28));
    }

    void SetSilhouetteValues()
    {
        if (activeComposer?.Composed != true) return;
        activeComposer.GetSwitch("silhouette-enabled").On = pendingSilhouettes.Enabled;
        GuiElementSlider reach = activeComposer.GetSlider("silhouette-reach");
        reach.SetValues(pendingSilhouettes.ReachBlocks, LodSilhouetteSettings.MinReachBlocks,
            LodSilhouetteSettings.MaxReachBlocks, LodSilhouetteSettings.ReachStepBlocks, " blocks");
        reach.ShowTextWhenResting = false;
        foreach (SilhouetteRow row in SilhouetteRows)
        {
            GuiElementSlider slider = activeComposer.GetSlider(row.Key);
            slider.SetValues(row.Read(pendingSilhouettes), row.Min, row.Max, row.Step);
            slider.ShowTextWhenResting = false;
            slider.OnSliderTooltip = value => row.Label(value);
        }
    }

    static readonly string[] WorldGenCentreCodes = { "here", "spawn", "coordinates" };
    static readonly string[] WorldGenCentreNames = { "My position", "World spawn", "Coordinates (blocks)" };
    static readonly string[] WorldGenShapeCodes = { "circle", "square" };
    static readonly string[] WorldGenShapeNames = { "Circle", "Square" };

    void ComposeGenerate(GuiComposer composer)
    {
        composer.AddStaticText("Generate real terrain", CairoFont.WhiteMediumText(),
            ElementBounds.Fixed(Margin, Y(42), 500, 30));
        composer.AddDynamicText(WorldGenAvailability(), CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(Margin, Y(74), Width - 2 * Margin, 42), "wg-availability");

        composer.AddStaticText("Centre", CairoFont.WhiteSmallishText(),
            ElementBounds.Fixed(Margin, Y(124), 100, 28));
        composer.AddDropDown(WorldGenCentreCodes, WorldGenCentreNames,
            (int)pendingWorldGen.CentreMode, OnWorldGenCentreChanged,
            ElementBounds.Fixed(135, Y(118), 220, 32), "wg-centre");
        composer.AddStaticText("X", CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(382, Y(124), 22, 26));
        composer.AddNumberInput(ElementBounds.Fixed(406, Y(118), 155, 32),
            OnWorldGenXChanged, CairoFont.WhiteSmallText(), "wg-x");
        composer.AddStaticText("Z", CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(588, Y(124), 22, 26));
        composer.AddNumberInput(ElementBounds.Fixed(612, Y(118), 155, 32),
            OnWorldGenZChanged, CairoFont.WhiteSmallText(), "wg-z");

        composer.AddStaticText("Radius", CairoFont.WhiteSmallishText(),
            ElementBounds.Fixed(Margin, Y(174), 100, 28));
        composer.AddSlider(OnWorldGenRadiusChanged,
            ElementBounds.Fixed(135, Y(168), 545, 30), "wg-radius");
        composer.AddDynamicText(WorldGenRadiusLabel(),
            CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Right),
            ElementBounds.Fixed(695, Y(174), 170, 24),
            "wg-radius-label");

        composer.AddStaticText("Shape", CairoFont.WhiteSmallishText(),
            ElementBounds.Fixed(Margin, Y(220), 100, 28));
        composer.AddDropDown(WorldGenShapeCodes, WorldGenShapeNames,
            pendingWorldGen.Shape == EnumWorldGenShape.Circle ? 0 : 1,
            OnWorldGenShapeChanged, ElementBounds.Fixed(135, Y(214), 220, 32), "wg-shape");
        composer.AddStaticText("Preset", CairoFont.WhiteSmallishText(),
            ElementBounds.Fixed(410, Y(220), 90, 28));
        composer.AddStaticText("Balanced (engine default)", CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(505, Y(222), 260, 24));

        composer.AddDynamicText(LodWorldGenPageMath.Estimate(pendingWorldGen),
            CairoFont.WhiteSmallText(), ElementBounds.Fixed(Margin, Y(266), Width - 2 * Margin, 28),
            "wg-estimate");
        composer.AddStaticText(
            "This permanently adds terrain to the savegame. Prepare shows the exact area, time "
            + "and disk estimate; Confirm is a separate click. Back up important worlds first.",
            CairoFont.WhiteSmallText(), ElementBounds.Fixed(Margin, Y(304), Width - 2 * Margin, 52));

        composer.AddStaticText("Progress", CairoFont.WhiteSmallishText(),
            ElementBounds.Fixed(Margin, Y(374), 100, 28));
        composer.AddSlider(_ => false,
            ElementBounds.Fixed(135, Y(368), 545, 30), "wg-progress");
        composer.AddDynamicText(WorldGenPercentLabel(),
            CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Right),
            ElementBounds.Fixed(695, Y(374), 170, 24),
            "wg-progress-label");
        composer.AddDynamicText(LodWorldGenPageMath.Progress(worldGen.Client?.WorldGenStatus),
            CairoFont.WhiteSmallText(), ElementBounds.Fixed(Margin, Y(414), Width - 2 * Margin, 76),
            "wg-progress-text");
        composer.AddDynamicText(WorldGenLastMessage(), CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(Margin, Y(494), Width - 2 * Margin, 66), "wg-message");

        composer.AddButton(NeedsLiveWorldGenWindow() ? "Open live controls" : "Prepare",
            OnWorldGenPrepare,
            ElementBounds.Fixed(Margin, Y(574), 170, 38), EnumButtonStyle.Normal, "wg-prepare");
        composer.AddButton("Confirm", OnWorldGenConfirm,
            ElementBounds.Fixed(Margin + 180, Y(574), 170, 38), EnumButtonStyle.Normal, "wg-confirm");
        composer.AddButton("Pause", OnWorldGenPause,
            ElementBounds.Fixed(Margin + 360, Y(574), 135, 38), EnumButtonStyle.Normal, "wg-pause");
        composer.AddButton("Resume", OnWorldGenResume,
            ElementBounds.Fixed(Margin + 505, Y(574), 135, 38), EnumButtonStyle.Normal, "wg-resume");
        composer.AddButton("Stop", OnWorldGenStop,
            ElementBounds.Fixed(Margin + 650, Y(574), 135, 38), EnumButtonStyle.Normal, "wg-stop");

        worldGen.Client?.SetWorldGenPageOpen(!NeedsLiveWorldGenWindow());
    }

    void SetGenerateValues()
    {
        if (activeComposer?.Composed != true) return;
        int maxRadius = ServerWorldGenMaxRadius();
        pendingWorldGen = pendingWorldGen.Normalize(maxRadius);
        GuiElementSlider radius = activeComposer.GetSlider("wg-radius");
        radius.SetValues(pendingWorldGen.RadiusChunks, 1, maxRadius, 1, " chunks");
        radius.ShowTextWhenResting = false;
        GuiElementSlider progress = activeComposer.GetSlider("wg-progress");
        progress.SetValues(LodWorldGenPageMath.Percent(worldGen.Client?.WorldGenStatus), 0, 100, 1, "%");
        progress.ShowTextWhenResting = false;
        progress.Enabled = false;

        GuiElementNumberInput x = activeComposer.GetNumberInput("wg-x");
        GuiElementNumberInput z = activeComposer.GetNumberInput("wg-z");
        x.IntMode = z.IntMode = true;
        x.SetValue(pendingWorldGen.X);
        z.SetValue(pendingWorldGen.Z);
        RefreshWorldGenControls();
    }

    int ServerWorldGenMaxRadius() => Math.Max(1,
        worldGen.Client?.WorldGenStatus?.MaxRadiusChunks
        ?? worldGen.Client?.ServerSettings?.WorldGenMaxRadiusChunks
        ?? 256);

    int ServerWorldGenDefaultRadius() => Math.Clamp(
        worldGen.Client?.WorldGenStatus?.DefaultRadiusChunks
        ?? worldGen.Client?.ServerSettings?.WorldGenDefaultRadiusChunks
        ?? LodWorldGenPreferences.Default.RadiusChunks,
        1, ServerWorldGenMaxRadius());

    string WorldGenAvailability()
    {
        LodAssistClient? client = worldGen.Client;
        if (client == null || !client.WorldGenerationAvailable)
            return "Unavailable: load a world on a server running a current Vintage Horizons build.";
        if (NeedsLiveWorldGenWindow())
            return "Singleplayer is paused in Settings. Open live controls to start or manage generation.";
        if (client.WorldGenStatus?.Available == false)
            return "Temporarily unavailable: " + client.WorldGenStatus.Message;
        return client.CanControlWorldGeneration
            ? "Available. The server owns generation and enforces its radius and resource limits."
            : "Read-only: you can watch progress, but controlserver permission is required to change it.";
    }

    string WorldGenRadiusLabel() => $"{pendingWorldGen.RadiusChunks:N0} chunks / "
        + $"{pendingWorldGen.RadiusChunks * 32:N0} blocks";

    string WorldGenPercentLabel() =>
        LodWorldGenPageMath.Percent(worldGen.Client?.WorldGenStatus) + "%";

    string WorldGenLastMessage()
    {
        LodAssistClient? client = worldGen.Client;
        if (!string.IsNullOrWhiteSpace(client?.LastWorldGenControlError))
            return client.LastWorldGenControlError;
        if (NeedsLiveWorldGenWindow())
            return "Opening the live window leaves Settings so the integrated server can run; the mod does not force-unpause it.";
        return client?.WorldGenStatus?.Message
            ?? "Open this tab after joining a world to request server status.";
    }

    void OnWorldGenStatus(AssistWorldGenStatus status)
    {
        if (sessionClosed || activeComposer?.Composed != true || shownTab != GenerateTab) return;
        activeComposer.GetDynamicText("wg-availability")
            .SetNewText(WorldGenAvailability(), false, true, false);
        activeComposer.GetDynamicText("wg-progress-text")
            .SetNewText(LodWorldGenPageMath.Progress(status), false, true, false);
        activeComposer.GetDynamicText("wg-message")
            .SetNewText(WorldGenLastMessage(), false, true, false);
        activeComposer.GetDynamicText("wg-progress-label")
            .SetNewText(WorldGenPercentLabel(), false, true, false);
        activeComposer.GetSlider("wg-progress").SetValue(LodWorldGenPageMath.Percent(status));
        RefreshWorldGenControls();
    }

    void RefreshWorldGenControls()
    {
        if (activeComposer?.Composed != true || shownTab != GenerateTab) return;
        LodAssistClient? client = worldGen.Client;
        AssistWorldGenStatus? status = client?.WorldGenStatus;
        bool available = client?.WorldGenerationAvailable == true && status?.Available != false;
        bool canControl = available && client!.CanControlWorldGeneration;
        EnumWorldGenRemoteState state = status?.State ?? EnumWorldGenRemoteState.Idle;
        bool openLive = NeedsLiveWorldGenWindow();
        bool areaLocked = state is EnumWorldGenRemoteState.Prepared
            or EnumWorldGenRemoteState.Running or EnumWorldGenRemoteState.Paused
            or EnumWorldGenRemoteState.Interrupted or EnumWorldGenRemoteState.Faulted;
        bool coordinates = pendingWorldGen.CentreMode == EnumWorldGenCentreMode.Coordinates;

        activeComposer.GetDropDown("wg-centre").Enabled = canControl && !areaLocked;
        activeComposer.GetDropDown("wg-shape").Enabled = canControl && !areaLocked;
        activeComposer.GetSlider("wg-radius").Enabled = canControl && !areaLocked;
        activeComposer.GetNumberInput("wg-x").Enabled = canControl && !areaLocked && coordinates;
        activeComposer.GetNumberInput("wg-z").Enabled = canControl && !areaLocked && coordinates;
        activeComposer.GetButton("wg-prepare").Enabled = canControl && (openLive
            || (!areaLocked && status?.PendingConfirmation != true));
        activeComposer.GetButton("wg-confirm").Enabled = !openLive && canControl
            && status?.PendingConfirmation == true;
        activeComposer.GetButton("wg-pause").Enabled = !openLive && canControl
            && state == EnumWorldGenRemoteState.Running;
        activeComposer.GetButton("wg-resume").Enabled = !openLive && canControl
            && state is EnumWorldGenRemoteState.Paused or EnumWorldGenRemoteState.Interrupted;
        activeComposer.GetButton("wg-stop").Enabled = !openLive && canControl
            && areaLocked;
    }

    internal static bool NeedsLiveWorldGenWindow(
        bool embedded, bool singlePlayer, bool gamePaused) =>
        embedded && singlePlayer && gamePaused;

    bool NeedsLiveWorldGenWindow() => NeedsLiveWorldGenWindow(
        embedded, capi.IsSinglePlayer, capi.IsGamePaused);

    void OnWorldGenCentreChanged(string code, bool selected)
    {
        if (!selected) return;
        EnumWorldGenCentreMode centre = code switch
        {
            "spawn" => EnumWorldGenCentreMode.Spawn,
            "coordinates" => EnumWorldGenCentreMode.Coordinates,
            _ => EnumWorldGenCentreMode.Here,
        };
        pendingWorldGen = pendingWorldGen with { CentreMode = centre };
        RefreshWorldGenControls();
    }

    void OnWorldGenShapeChanged(string code, bool selected)
    {
        if (!selected) return;
        pendingWorldGen = pendingWorldGen with
        {
            Shape = code == "square" ? EnumWorldGenShape.Square : EnumWorldGenShape.Circle,
        };
        RefreshWorldGenEstimate();
    }

    bool OnWorldGenRadiusChanged(int radius)
    {
        pendingWorldGen = pendingWorldGen with
        {
            RadiusChunks = Math.Clamp(radius, 1, ServerWorldGenMaxRadius()),
        };
        if (activeComposer?.Composed == true && shownTab == GenerateTab)
        {
            activeComposer.GetDynamicText("wg-radius-label")
                .SetNewText(WorldGenRadiusLabel(), false, true, false);
            RefreshWorldGenEstimate();
        }
        return true;
    }

    void OnWorldGenXChanged(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x))
            pendingWorldGen = pendingWorldGen with { X = Math.Max(0, x) };
    }

    void OnWorldGenZChanged(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int z))
            pendingWorldGen = pendingWorldGen with { Z = Math.Max(0, z) };
    }

    void RefreshWorldGenEstimate()
    {
        if (activeComposer?.Composed != true || shownTab != GenerateTab) return;
        activeComposer.GetDynamicText("wg-estimate")
            .SetNewText(LodWorldGenPageMath.Estimate(pendingWorldGen), false, true, false);
    }

    bool OnWorldGenPrepare()
    {
        if (NeedsLiveWorldGenWindow() && worldGen.OpenLive != null)
        {
            worldGen.Client?.SetWorldGenPageOpen(false);
            worldGen.OpenLive(pendingWorldGen.Normalize(ServerWorldGenMaxRadius()));
            return true;
        }

        int x = pendingWorldGen.CentreMode == EnumWorldGenCentreMode.Coordinates
            ? pendingWorldGen.X : -1;
        int z = pendingWorldGen.CentreMode == EnumWorldGenCentreMode.Coordinates
            ? pendingWorldGen.Z : -1;
        worldGen.Client?.RequestWorldGen(EnumWorldGenControlAction.Prepare,
            pendingWorldGen.RadiusChunks, pendingWorldGen.Shape,
            pendingWorldGen.CentreMode, x, z, pendingWorldGen.Preset);
        return true;
    }

    bool OnWorldGenConfirm() { worldGen.Client?.RequestWorldGen(EnumWorldGenControlAction.Confirm); return true; }
    bool OnWorldGenPause() { worldGen.Client?.RequestWorldGen(EnumWorldGenControlAction.Pause); return true; }
    bool OnWorldGenResume() { worldGen.Client?.RequestWorldGen(EnumWorldGenControlAction.Resume); return true; }
    bool OnWorldGenStop() { worldGen.Client?.RequestWorldGen(EnumWorldGenControlAction.Stop); return true; }

    void OnTabClicked(int tab)
    {
        if (tab == shownTab) return;
        worldGen.Client?.SetWorldGenPageOpen(
            tab == GenerateTab && !NeedsLiveWorldGenWindow());
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

    void OnSilhouetteEnabledChanged(bool on)
    {
        pendingSilhouettes = pendingSilhouettes with { Enabled = on };
        silhouettes.Preview(pendingSilhouettes);
        scale?.SetSilhouette(pendingSilhouettes.ReachBlocks, on);
        RefreshSilhouetteStatus();
    }

    bool OnSilhouetteReachSlider(int blocks)
    {
        OnSilhouetteReachChanged(blocks);
        return true;
    }

    void OnSilhouetteReachChanged(int blocks)
    {
        int reach = LodSilhouetteSettings.ClampReach(blocks);
        pendingSilhouettes = pendingSilhouettes with { ReachBlocks = reach };
        scale?.SetSilhouette(reach, pendingSilhouettes.Enabled);
        silhouettes.Preview(pendingSilhouettes);
        if (activeComposer?.Composed == true && shownTab == SilhouettesTab)
            activeComposer.GetDynamicText("silhouette-reach-label")
                .SetNewText(SilhouetteReachLabel(), false, true, false);
        RefreshSilhouetteStatus();
    }

    bool OnSilhouetteDialChanged(SilhouetteRow row, int value)
    {
        pendingSilhouettes = row.Write(pendingSilhouettes, value).Normalize();
        silhouettes.Preview(pendingSilhouettes);
        if (activeComposer?.Composed == true && shownTab == SilhouettesTab)
            activeComposer.GetDynamicText($"{row.Key}-label")
                .SetNewText(row.Label(row.Read(pendingSilhouettes)), false, true, false);
        return true;
    }

    void RefreshSilhouetteStatus()
    {
        if (activeComposer?.Composed != true || shownTab != SilhouettesTab) return;
        activeComposer.GetDynamicText("silhouette-status")
            .SetNewText(silhouettes.Status(), false, true, false);
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
    int PresetIndex() => (pendingVisuals.PresetName, pendingSilhouettes.Enabled) switch
    {
        ("Performance", false) => 0,
        ("Balanced", true) => 1,
        ("Quality", true) => 2,
        _ => 3,
    };

    void OnPresetSelected(string code, bool selected)
    {
        if (!selected) return;
        switch (code)
        {
            case "performance":
                pendingVisuals = LodVisualSettings.Performance;
                pendingSilhouettes = pendingSilhouettes with { Enabled = false };
                break;
            case "balanced":
                pendingVisuals = LodVisualSettings.Balanced;
                pendingSilhouettes = pendingSilhouettes with { Enabled = true };
                break;
            case "quality":
                pendingVisuals = LodVisualSettings.Quality;
                pendingSilhouettes = pendingSilhouettes with { Enabled = true };
                break;
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
        scale?.SetSilhouette(pendingSilhouettes.ReachBlocks, pendingSilhouettes.Enabled);
    }

    /// <summary>What the scale shows: automatic's live values, or the player's own.</summary>
    int[] DisplayedThresholds() => pendingAutomatic ? automaticThresholds : pendingThresholds;

    string ThresholdHelp() => pendingAutomatic
        ? "Drag Vanilla in 32-block steps (applies immediately). Grey is vanilla; colour starts at L0. "
            + "L1-L6 are automatic; Cache and Farseer set the two far edges."
        : "Drag Vanilla (applies immediately), L1-L6, Cache or Farseer. Blue is Farseer reach.";

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
        activeComposer.GetDynamicText("threshold-help")
            .SetNewText(ThresholdHelp(), false, true, false);
    }

    void OnPausedLoadingChanged(bool on) => pendingLoadWhilePaused = on;

    bool OnDrawDistanceChanged(int blocks)
    {
        pendingFarCap = Math.Clamp(blocks, MinDrawDistance, MaxDrawDistance);
        scale?.SetDrawDistance(pendingFarCap);
        scale?.SetSilhouette(pendingSilhouettes.ReachBlocks, pendingSilhouettes.Enabled);
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

        if (shownTab == GenerateTab)
        {
            pendingWorldGen = (LodWorldGenPreferences.Default with
            {
                RadiusChunks = ServerWorldGenDefaultRadius(),
            }).Normalize(ServerWorldGenMaxRadius());
            if (activeComposer?.Composed != true) return true;
            activeComposer.GetDropDown("wg-centre").SetSelectedIndex((int)pendingWorldGen.CentreMode);
            activeComposer.GetDropDown("wg-shape").SetSelectedIndex(
                pendingWorldGen.Shape == EnumWorldGenShape.Circle ? 0 : 1);
            activeComposer.GetNumberInput("wg-x").SetValue(pendingWorldGen.X);
            activeComposer.GetNumberInput("wg-z").SetValue(pendingWorldGen.Z);
            activeComposer.GetSlider("wg-radius").SetValue(pendingWorldGen.RadiusChunks);
            activeComposer.GetDynamicText("wg-radius-label")
                .SetNewText(WorldGenRadiusLabel(), false, true, false);
            RefreshWorldGenEstimate();
            RefreshWorldGenControls();
            return true;
        }

        if (shownTab == SilhouettesTab)
        {
            pendingSilhouettes = LodSilhouetteSettings.Default;
            silhouettes.Preview(pendingSilhouettes);
            if (activeComposer?.Composed != true) return true;
            SetSilhouetteValues();
            activeComposer.GetDynamicText("silhouette-reach-label")
                .SetNewText(SilhouetteReachLabel(), false, true, false);
            foreach (SilhouetteRow row in SilhouetteRows)
                activeComposer.GetDynamicText($"{row.Key}-label")
                    .SetNewText(row.Label(row.Read(pendingSilhouettes)), false, true, false);
            RefreshSilhouetteStatus();
            return true;
        }

        Array.Copy(LodWorld.DefaultLevelThresholds, pendingThresholds,
            pendingThresholds.Length);
        pendingAutomatic = true;
        pendingFarCap = DefaultDrawDistance;
        pendingLoadWhilePaused = true;
        pendingVisuals = LodVisualSettings.Default;
        pendingSilhouettes = pendingSilhouettes with { Enabled = true };

        RefreshThresholds();
        if (activeComposer?.Composed != true) return true;
        scale?.SetDrawDistance(pendingFarCap);
        scale?.SetSilhouette(pendingSilhouettes.ReachBlocks, pendingSilhouettes.Enabled);
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
        worldGen.Save(pendingWorldGen.Normalize(ServerWorldGenMaxRadius()));
        save(pendingThresholds, pendingAutomatic, pendingFarCap, pendingLoadWhilePaused,
            pendingVisuals, pendingTaa, pendingSilhouettes.Normalize());
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
        worldGen.Client?.SetWorldGenPageOpen(false);
        if (worldGen.Client != null)
            worldGen.Client.WorldGenStatusChanged -= OnWorldGenStatus;
        activeComposer = null;
        if (!saved) taa.Preview(taa.Initial, taa.ShowInitial);
        if (!saved) silhouettes.Preview(silhouettes.Initial);
    }

    string DrawDistanceLabel() => $"{FormatBlocks(pendingFarCap)} blocks";

    string SilhouetteReachLabel() => $"{FormatBlocks(pendingSilhouettes.ReachBlocks)} blocks";

    static string FormatBlocks(int blocks) => blocks.ToString("N0", CultureInfo.InvariantCulture);
}
