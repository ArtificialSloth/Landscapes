using System.Globalization;
using Vintagestory.API.Client;
using VintageHorizons.Net;

namespace VintageHorizons;

/// <summary>
/// <summary>
/// What the Developer tab's anti-aliasing dials need from the mod: the dials in force when the window opened and
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
/// Automatic LOD distances' target: the on-screen size every level hands over at, and what the
/// distances would be at another target, so a preset or the Developer slider can show them.
/// </summary>
internal sealed record DetailTuning(float InitialTarget, Func<float, int[]> Preview);

/// <summary>
/// General's all-cores garbage collection switch (<see cref="GameCollectorSetting"/>):
/// what the settings say, whether this session runs it, and Save, which edits the game's
/// start-up file and reports what happened.
/// </summary>
internal sealed record CollectorTuning(bool Initial, Func<bool> InEffect, Func<bool, string> Save);

/// <summary>
/// Whether the Developer tab is shown, and how the choice is remembered: at once, not on Save,
/// because it is how the window looks rather than a setting of the mod (owner, 2026-10-19).
/// </summary>
internal sealed record DeveloperOptionsTuning(bool Initial, Action<bool> Remember);

/// <summary>
/// <summary>
/// One set of coordinates for both the standalone command window and the page embedded in
/// vanilla Settings. Keeping the row edges here makes clipping a testable rule instead of a
/// collection of unrelated literals.
/// </summary>
internal static class VintageHorizonsConfigLayout
{
    internal const double Width = 900;

    /// <summary>
    /// 640 tall so the window fits a 1080-pixel screen at GUI scale 1.5 with the tab strip above
    /// it, and inside vanilla Settings below its 78-unit header (718 x 1.5 = 1077), and a
    /// 768-pixel laptop screen at 1.0. It was 820: at 1.5 on 1080p the title, tabs and the
    /// Save row were off-screen (sandbox screen check, 2026-10-16; owner chose the tighter
    /// layout over scrolling).
    /// </summary>
    internal const double Height = 640;
    internal const double Margin = 32;
    internal const double SwitchX = 824;
    internal const double ButtonY = Height - 48;
    internal const double TabsWidth = 760;
    internal const int PlannedTabCount = 3;

    /// <summary>
    /// One title over the bar, "LOD Thresholds (Drag to adjust)", beside the Automatic switch
    /// (owner, 2026-10-19). Until 0.4.173 a draw-distance heading with its readout, a distances
    /// title and a help line stood here; the bar's Cache box already shows the draw distance, and
    /// the help line is the title's hover.
    /// </summary>
    internal const double LodTitleY = 36;

    /// <summary>
    /// A small line under the title saying why Automatic is recommended (owner, 2026-10-19).
    /// </summary>
    internal const double AutoNoteY = 72;
    internal const double ScaleY = 104;

    /// <summary>
    /// 214 so each lane of marker boxes has a gap to the next: at 200 the Farseer box sat on the
    /// lower LOD boxes (L4 over Farseer in the owner's 0.4.166 screenshot). The lanes themselves are
    /// in <see cref="LodThresholdScaleElement"/>; the lowest box ends at 213.
    /// </summary>
    internal const double ScaleHeight = 214;

    /// <summary>
    /// A horizontal rule under the bar parts the distances from the switches (owner, 2026-10-19),
    /// where the draw distance heading used to be.
    /// </summary>
    internal const double RuleY = 324;
    internal const double RuleHeight = 4;

    /// <summary>
    /// Visual features head everything under the rule (owner, 2026-10-19): five lines of two
    /// columns, Farseer beside its seam fog first and all-cores collection under the fog, at a
    /// roomier 30-unit pitch now that the page has the space.
    /// </summary>
    internal const double VisualHeadingY = 340;
    internal const double VisualRowsY = 378;
    internal const int VisualLineCount = 5;
    internal const double VisualRowHeight = 30;

    /// <summary>The no-sampler note, a line under the features.</summary>
    internal const double NoteY = VisualRowsY + VisualLineCount * VisualRowHeight + 6;

    /// <summary>
    /// Paused loading and Developer options sit in the action row between Defaults and Cancel
    /// (owner, 2026-10-19): they change how the mod works rather than what it draws.
    /// </summary>
    internal const double FooterLeft = Margin + 140 + 20;
    internal const double FooterRight = Width - Margin - 275 - 16;

    /// <summary>The rows' switches, sized to their 28-unit pitch.</summary>
    internal const double RowSwitchSize = 26;
    internal const int VisualRowCount = 7;

    /// <summary>
    /// The feature rows run down the left column under Farseer first, then the right column under
    /// the seam fog and all-cores collection (owner: two columns, 2026-10-19).
    /// </summary>
    internal const int VisualRowsPerColumn = 4;

    /// <summary>
    /// The two columns span the window from the margin to the right edge of the Automatic
    /// switch, so the right column's switches line up under it.
    /// </summary>
    internal const double ColumnGap = 32;
    internal const double ColumnWidth = (SwitchX + 32 - Margin - ColumnGap) / 2;
    internal const double CostWidth = 72;
    internal static double ColumnX(int column) => Margin + column * (ColumnWidth + ColumnGap);
    internal static double ColumnSwitchX(int column) => ColumnX(column) + ColumnWidth - RowSwitchSize - 3;
    internal static double ColumnCostX(int column) => ColumnSwitchX(column) - 8 - CostWidth;

    // The Developer tab's anti-aliasing rows: where each slider starts, how wide it is, and the
    // row pitch, under the group drop-down, the explainer and the reach help.
    internal const double DialSliderX = 330;
    internal const double DialSliderWidth = 400;
    internal const double DialRowsY = 146;
    // 52 rather than 58, so the LOD detail target fits as a seventh row above the tint switch.
    internal const double DialRowHeight = 52;
    internal const double ShowTaaY = 510;

    internal const double EmbeddedTabsY = 82;
    // Vanilla's header ends at 75. The editor's own tabs begin below that edge, with their
    // content following beneath, instead of sitting through the header's bottom rule.
    internal const double EmbeddedContentY = 78;
    internal const double EmbeddedHeight = Height + EmbeddedContentY;
}

/// <summary>
/// Client-only settings editor shared by the in-game Settings page and .dlconfig window.
///
/// <para>
/// Two tabs, three with Developer options on. General is how far and how finely cached terrain
/// is drawn, and which of the optional passes are drawn at all, Farseer and its fog included -
/// the settings a machine that cannot afford them needs to be able to give up, each one measured
/// rather than guessed - with the all-cores collector beside them. Generate controls the
/// server-owned, explicitly confirmed savegame generator and remains read-only when the player
/// lacks permission. Developer holds every tuning dial, in three groups picked from a drop-down,
/// and shows only while General's Developer options switch is on.
/// </para>
/// <para>
/// The dials sat behind an Advanced switch, on an Anti-Aliasing tab and Farseer's own tab, until
/// 0.4.166. The owner dropped the switch and moved them to the Developer tab, with Farseer's two
/// switches on General (2026-10-19). All of them together do not fit the 640-tall window, so the
/// drop-down shows one group at a time.
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
/// editor is later saved or cancelled. The Farseer switches and every Developer dial show their
/// effect at once, because judging the dials by eye is the point: Save keeps them, and Cancel or
/// closing the window any other way puts back what was in force when it opened. The pending values below are the whole mod state of the window, so
/// switching tabs re-composes freely without losing an edit.
/// </para>
/// </summary>
internal sealed class VintageHorizonsConfigDialog : GuiDialog
{
    const int MinDrawDistance = 1024;
    const int MaxDrawDistance = 32768;
    const int DrawDistanceStep = 512;


    const double Width = VintageHorizonsConfigLayout.Width;
    const double Height = VintageHorizonsConfigLayout.Height;
    const double Margin = VintageHorizonsConfigLayout.Margin;
    const double SwitchX = VintageHorizonsConfigLayout.SwitchX;
    const double RowHeight = VintageHorizonsConfigLayout.VisualRowHeight;

    const int GeneralTab = 0;
    const int GenerateTab = 1;
    const int DeveloperTab = 2;

    // The Developer tab's groups, one at a time from the sub-tabs at the top of its page. Each
    // group's id is its position in the strip.
    const int AntiAliasingGroup = 0;
    const int FarseerLookGroup = 1;
    const int SeamFogGroup = 2;
    static readonly string[] DeveloperGroupNames = { "Anti-aliasing", "Farseer look", "Seam fog" };

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

    const string AllCoresTip =
        "Runs the game's garbage collection on every core instead of one, and far less often. "
        + "Those collections are the small freezes behind stutter while exploring: about 20 times "
        + "fewer in the owner's tests. Uses more memory. It adds one line to the game's own start-up "
        + "file, so it applies from the next start; switch it off here before removing Vintage "
        + "Horizons, or the game keeps it until its next update.";

    const string ShowTip =
        "Tints anti-aliased terrain magenta, stronger where it is fully applied, to show what the "
        + "sliders reach. For this session only; it is never saved. (.dltaa show)";

    readonly Action<int[], bool, int, bool, LodVisualSettings, LodTaaDials,
        LodSilhouetteSettings, float> save;
    readonly DetailTuning detail;
    float pendingTargetPixels;
    readonly TaaTuning taa;
    readonly VanillaDistanceTuning vanilla;
    readonly WorldGenTuning worldGen;
    readonly SilhouetteTuning silhouettes;
    readonly CollectorTuning collector;
    readonly DeveloperOptionsTuning developerOptions;
    bool showDeveloper;
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

    /// <summary>The Developer group on show; for this window only.</summary>
    int developerGroup = AntiAliasingGroup;
    LodWorldGenPreferences pendingWorldGen;
    bool pendingAllCores;

    /// <summary>Whether the Generate page was last composed with its progress rows.</summary>
    bool composedWithProgress;
    LodThresholdScaleElement? scale;
    GuiComposer? activeComposer;
    bool sessionClosed;
    double contentTop;

    /// <summary>Which editor tab is showing.</summary>
    int shownTab = GeneralTab;

    /// <summary>Set by Save, so closing afterwards keeps the switches and dials rather than putting them back.</summary>
    bool saved;

    public override string DebugName => "dodenslandscapes-config";
    public override string? ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => true;

    /// <param name="thresholds">The player's own distances, kept whether or not automatic is on.</param>
    /// <param name="automaticThresholds">What automatic mode would use right now, for display.</param>
    public VintageHorizonsConfigDialog(ICoreClientAPI capi, IReadOnlyList<int> thresholds,
        IReadOnlyList<int> automaticThresholds, bool automatic, int farCap,
        bool loadWhilePaused, LodVisualSettings visuals, VanillaDistanceTuning vanilla,
        DetailTuning detail, TaaTuning taa, SilhouetteTuning silhouettes, WorldGenTuning worldGen,
        CollectorTuning collector, DeveloperOptionsTuning developerOptions,
        Action<int[], bool, int, bool, LodVisualSettings, LodTaaDials,
            LodSilhouetteSettings, float> save)
        : this(capi, thresholds, automaticThresholds, automatic, farCap, loadWhilePaused,
            visuals, vanilla, detail, taa, silhouettes, worldGen, collector, developerOptions, false,
            null, null, save)
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
        DetailTuning detail, TaaTuning taa, SilhouetteTuning silhouettes, WorldGenTuning worldGen,
        CollectorTuning collector, DeveloperOptionsTuning developerOptions,
        Action requestRecompose, Action requestClose,
        Action<int[], bool, int, bool, LodVisualSettings, LodTaaDials,
            LodSilhouetteSettings, float> save)
        : this(capi, thresholds, automaticThresholds, automatic, farCap, loadWhilePaused,
            visuals, vanilla, detail, taa, silhouettes, worldGen, collector, developerOptions, true,
            requestRecompose, requestClose, save)
    {
    }

    VintageHorizonsConfigDialog(ICoreClientAPI capi, IReadOnlyList<int> thresholds,
        IReadOnlyList<int> automaticThresholds, bool automatic, int farCap,
        bool loadWhilePaused, LodVisualSettings visuals, VanillaDistanceTuning vanilla,
        DetailTuning detail, TaaTuning taa, SilhouetteTuning silhouettes, WorldGenTuning worldGen,
        CollectorTuning collector, DeveloperOptionsTuning developerOptions, bool embedded,
        Action? requestRecompose, Action? requestClose,
        Action<int[], bool, int, bool, LodVisualSettings, LodTaaDials,
            LodSilhouetteSettings, float> save) : base(capi)
    {
        this.save = save;
        this.taa = taa;
        this.detail = detail;
        pendingTargetPixels = detail.InitialTarget;
        this.silhouettes = silhouettes;
        this.vanilla = vanilla;
        this.worldGen = worldGen;
        this.collector = collector;
        this.developerOptions = developerOptions;
        showDeveloper = developerOptions.Initial;
        pendingAllCores = collector.Initial;
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
    /// The cost words are deliberately coarse - Low, Moderate, High - because what a feature
    /// costs depends on what is on screen, so a number here would be more precise than it is
    /// true. Water used to say "Varies", which told a player nothing (owner, 2026-10-19); it is
    /// Low, with when it costs more in its tip.
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

        // Paid only by pixels that show water: nothing visible in the owner's own switch test
        // (Session 77), more with a lake or coastline filling the view.
        new("water-effects", "Water Effects",
            "Adds glint and foam to distant water. Costs only where water is on screen, so most "
            + "with a large lake or coastline in view.",
            "Low",
            v => v.WaterEffects,
            (v, on) => v with { WaterEffects = on }),

        new("distant-aa", "Distant Anti-Aliasing",
            "Stops distant terrain shimmering as you move by blending it over several frames.",
            "Low",
            v => v.DistantAntiAliasing,
            (v, on) => v with { DistantAntiAliasing = on }),
    };

    static readonly string[] PresetCodes =
        LodPreset.All.Select(preset => preset.Code).Append("custom").ToArray();
    static readonly string[] PresetNames =
        LodPreset.All.Select(preset => preset.Name).Append("Custom").ToArray();

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
        // No LOD edge depth or width: fixed at the owner's values since 0.4.146 (see
        // LodSilhouetteField.DefaultUnderlayDepthBlocks).
        new("sil-detail", "Geometry detail", "Controls only how dense the drawn Farseer mesh is. Lower is lighter; hill softness controls the outline shape.",
            0, 100, 1, s => (int)MathF.Round(s.OutlineDetail * 100),
            (s, v) => s with { OutlineDetail = v / 100f }, OutlineDetailLabel),
        new("sil-softness", "Hill softness", "Rounds and feathers the distant height profile. Higher produces broader rolling hills; regeneration is gradual and runs off-thread.",
            0, 100, 1, s => (int)MathF.Round(s.HillSoftness * 100),
            (s, v) => s with { HillSoftness = v / 100f }, HillSoftnessLabel),
        new("sil-fog-seam", "Seam strip density", "How thick the shallow strip of fog lying on the Farseer/LOD seam is. It is what hides the stepped edge: 100% hides it from every angle, lower lets it show through.",
            0, 200, 5, s => (int)MathF.Round(s.FogSeamStrength * 100),
            (s, v) => s with { FogSeamStrength = v / 100f }, v => $"{v}%"),
        new("sil-fog-strip-height", "Seam strip height", "How deep the seam strip lies over the ground. The stepped edge is at ground level, so a few blocks hide it; raise it if taller edges show through.",
            (int)LodSilhouetteFogVolume.MinStripHeightBlocks,
            (int)LodSilhouetteFogVolume.MaxStripHeightBlocks, 1,
            s => (int)MathF.Round(s.FogStripHeightBlocks),
            (s, v) => s with { FogStripHeightBlocks = v }, v => $"{v} blocks"),
        new("sil-fog-strength", "Fog density", "How thick the fog bank above and around the strip is. At 100% Farseer terrain behind it shows through, veiled; higher makes a solid wall.",
            0, 200, 5, s => (int)MathF.Round(s.FogStrength * 100),
            (s, v) => s with { FogStrength = v / 100f }, v => $"{v}%"),
        new("sil-fog-coverage", "Fog coverage", "How solid the bank is. Lower breaks it into separate billows and strands; higher makes one continuous blanket. The seam itself stays covered either way.",
            0, 100, 5, s => (int)MathF.Round(s.FogCoverage * 100),
            (s, v) => s with { FogCoverage = v / 100f }, v => $"{v}%"),
        new("sil-fog-spill", "Fog spread", "How far the fog thins out from the seam on each side. It reaches further over Farseer than over real terrain.",
            (int)LodSilhouetteFogVolume.MinSpillBlocks,
            (int)LodSilhouetteFogVolume.MaxSpillBlocks, 4,
            s => (int)MathF.Round(s.FogSpillBlocks),
            (s, v) => s with { FogSpillBlocks = v }, v => $"{v} blocks"),
        new("sil-fog-height", "Fog height", "How tall the bank stands over the seam. It is lower toward its edges and densest at the ground; height does not change how well the seam is hidden.",
            (int)LodSilhouetteFogVolume.MinHeightBlocks,
            (int)LodSilhouetteFogVolume.MaxHeightBlocks, 4,
            s => (int)MathF.Round(s.FogHeightBlocks),
            (s, v) => s with { FogHeightBlocks = v }, v => $"{v} blocks"),
        new("sil-fog-floor", "Fog floor depth", "How far the fog extends below the sampled terrain surface to cover cliffs, shores and vertical gaps.",
            (int)LodSilhouetteFogVolume.MinFloorDepthBlocks,
            (int)LodSilhouetteFogVolume.MaxFloorDepthBlocks, 2,
            s => (int)MathF.Round(s.FogFloorDepthBlocks),
            (s, v) => s with { FogFloorDepthBlocks = v }, v => $"{v} blocks"),
        new("sil-fog-billow", "Fog billow size", "Size of the billows and wisps. Higher gives larger, slower-changing shapes; lower gives finer ones.",
            (int)LodSilhouetteFogVolume.MinBillowSizeBlocks,
            (int)LodSilhouetteFogVolume.MaxBillowSizeBlocks, 4,
            s => (int)MathF.Round(s.FogBillowSizeBlocks),
            (s, v) => s with { FogBillowSizeBlocks = v }, v => $"{v} blocks"),
        new("sil-fog-drift", "Fog drift speed", "How quickly the world-anchored billows move. Zero keeps their shape stationary.",
            0, 300, 5, s => (int)MathF.Round(s.FogDriftSpeed * 100),
            (s, v) => s with { FogDriftSpeed = v / 100f }, v => $"{v}%"),
        new("sil-fog-fade", "Fog fade time", "When terrain loads in and the seam moves on, how long the fog left behind takes to dissolve. Zero makes it vanish at once.",
            (int)(LodSilhouetteFogVolume.MinFadeSeconds * 10),
            (int)(LodSilhouetteFogVolume.MaxFadeSeconds * 10), 1,
            s => (int)MathF.Round(s.FogFadeSeconds * 10),
            (s, v) => s with { FogFadeSeconds = v / 10f }, v => $"{v / 10f:0.0} s"),
        new("sil-fog-edge", "Fog edge softness", "How wispy the edges are: how much they fray into strands and wander in and out. Zero gives a smooth, even fade.",
            0, 100, 5, s => (int)MathF.Round(s.FogEdgeSoftness * 100),
            (s, v) => s with { FogEdgeSoftness = v / 100f }, v => $"{v}%"),
        new("sil-fog-detail", "Fog quality", "Raymarch precision through the fog. Higher keeps finer detail and smoother gradients but costs more GPU time; the Seam fog switch shows its whole cost.",
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

    /// <summary>The Farseer look dials, or the seam fog ones: the fog's keys all start "sil-fog".</summary>
    static SilhouetteRow[] GroupRows(bool fog) => Array.FindAll(SilhouetteRows,
        row => row.Key.StartsWith("sil-fog", StringComparison.Ordinal) == fog);

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
            .AddDialogTitleBar("Doden's Landscapes", OnTitleClose)
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
            throw new InvalidOperationException("This Doden's Landscapes editor is not active in Settings.");

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
        if (shownTab == GenerateTab) ComposeGenerate(composer);
        else if (shownTab == DeveloperTab) ComposeDeveloper(composer);
        else ComposeGeneral(composer);

        // Generate works through its own buttons. Save, Cancel and Defaults there invited the
        // question whether a generation had been saved.
        if (shownTab == GenerateTab) return;

        composer.AddButton("Defaults", OnDefaults,
            ElementBounds.Fixed(Margin, Y(VintageHorizonsConfigLayout.ButtonY), 140, 38),
            EnumButtonStyle.Normal, "defaults");
        ComposeFooterToggles(composer);
        composer.AddButton("Cancel", OnCancel,
            ElementBounds.Fixed(Width - Margin - 275, Y(VintageHorizonsConfigLayout.ButtonY), 130, 38),
            EnumButtonStyle.Normal, "cancel");
        composer.AddButton("Save", OnSave,
            ElementBounds.Fixed(Width - Margin - 140, Y(VintageHorizonsConfigLayout.ButtonY), 140, 38),
            EnumButtonStyle.Normal, "save");
    }

    /// <summary>The tabs on show, in order. Each tab's id is its position.</summary>
    internal static int[] VisibleTabs(bool showDeveloper) => showDeveloper
        ? new[] { GeneralTab, GenerateTab, DeveloperTab }
        : new[] { GeneralTab, GenerateTab };

    /// <summary>
    /// The tab a `.dlconfig` argument names, or null. Farseer, Performance and Anti-Aliasing had
    /// tabs of their own: Farseer's and Performance's switches are on General now, and the
    /// anti-aliasing dials open Developer, whose first group they are.
    /// </summary>
    internal static int? TabNamed(string name) => name.Trim().ToLowerInvariant() switch
    {
        "general" or "farseer" or "performance" => GeneralTab,
        "generate" => GenerateTab,
        "developer" or "dev" or "antialiasing" or "anti-aliasing" or "aa" => DeveloperTab,
        _ => null,
    };

    /// <summary>Shows a tab, as a click on it would; false when it is hidden.</summary>
    internal bool ShowTab(int tab)
    {
        if (Array.IndexOf(VisibleTabs(showDeveloper), tab) < 0) return false;
        if (tab != shownTab) OnTabClicked(tab);
        return true;
    }

    static string TabName(int tab) => tab switch
    {
        DeveloperTab => "Developer",
        GenerateTab => "Generate",
        _ => "General",
    };

    void AddTabs(GuiComposer composer, ElementBounds bounds)
    {
        composer.AddHorizontalTabs(
            VisibleTabs(showDeveloper)
                .Select(tab => new GuiTab { Name = TabName(tab), DataInt = tab })
                .ToArray(),
            bounds, OnTabClicked,
            CairoFont.WhiteSmallText(), CairoFont.WhiteSmallText().WithColor(GuiStyle.ActiveButtonTextColor),
            "tabs");
    }

    void CompleteComposition()
    {
        if (activeComposer?.Composed != true) return;
        // The highlight is a position in the strip, the click handler's value a tab id.
        activeComposer.GetHorizontalTabs("tabs").activeElement =
            Math.Max(0, Array.IndexOf(VisibleTabs(showDeveloper), shownTab));

        if (shownTab == GenerateTab) SetGenerateValues();
        else if (shownTab == DeveloperTab) SetDeveloperValues();
        else SetGeneralValues();
        SetFooterValues();
    }

    double Y(double y) => contentTop + y;

    void ComposeGeneral(GuiComposer composer)
    {
        const double titleY = VintageHorizonsConfigLayout.LodTitleY;
        composer.AddStaticText("LOD Thresholds (Drag to adjust)",
            CairoFont.WhiteMediumText(), ElementBounds.Fixed(Margin, Y(titleY), 560, 30));
        composer.AddHoverText(ScaleHelp, CairoFont.WhiteSmallText(), 420,
            ElementBounds.Fixed(Margin, Y(titleY - 2), 560, 34), "tip-scale-help");
        composer.AddStaticText(AutoNote,
            FittedFont(AutoNote, Width - 2 * Margin, CairoFont.WhiteSmallText()),
            ElementBounds.Fixed(Margin, Y(VintageHorizonsConfigLayout.AutoNoteY), Width - 2 * Margin, 22));
        // One line, shrinking if it must: at 180 wide it wrapped onto the line under the title
        // (owner screenshot, 0.4.174).
        const string automaticLabel = "Automatic (Recommended)";
        composer.AddStaticText(automaticLabel, FittedFont(automaticLabel, 240, CairoFont.WhiteSmallText()),
            EnumTextOrientation.Right, ElementBounds.Fixed(SwitchX - 250, Y(titleY + 6), 240, 22));
        composer.AddSwitch(OnAutomaticChanged,
            ElementBounds.Fixed(SwitchX, Y(titleY), 32, 32), "auto-thresholds");
        // Over the label and the switch alike. GuiElementHoverText handles no mouse-down, so it
        // cannot swallow the click on the switch underneath it.
        composer.AddHoverText(AutomaticTip, CairoFont.WhiteSmallText(), 380,
            ElementBounds.Fixed(SwitchX - 250, Y(titleY - 2), 282, 36), "tip-auto-thresholds");

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

        // The rule: a dark line over a faint light one, the groove look of the game's own insets.
        composer.AddStaticCustomDraw(ElementBounds.Fixed(Margin, Y(VintageHorizonsConfigLayout.RuleY),
            Width - 2 * Margin, VintageHorizonsConfigLayout.RuleHeight), (ctx, surface, bounds) =>
        {
            double line = Math.Max(1, Math.Round(GuiElement.scaled(1)));
            double y = Math.Round(bounds.drawY + bounds.InnerHeight / 2 - line);
            ctx.SetSourceRGBA(0, 0, 0, 0.45);
            ctx.Rectangle(bounds.drawX, y, bounds.InnerWidth, line);
            ctx.Fill();
            ctx.SetSourceRGBA(1, 1, 1, 0.16);
            ctx.Rectangle(bounds.drawX, y + line, bounds.InnerWidth, line);
            ctx.Fill();
        });

        composer.AddStaticText("Visual features", CairoFont.WhiteMediumText(),
            ElementBounds.Fixed(Margin, Y(VintageHorizonsConfigLayout.VisualHeadingY), 230, 30));
        // The preset sits over the right column, clear of the left column's Cost heading.
        double presetX = VintageHorizonsConfigLayout.ColumnX(1);
        composer.AddStaticText("Preset", CairoFont.WhiteDetailText(),
            ElementBounds.Fixed(presetX, Y(VintageHorizonsConfigLayout.VisualHeadingY + 8), 56, 22));
        composer.AddDropDown(PresetCodes, PresetNames, PresetIndex(), OnPresetSelected,
            ElementBounds.Fixed(presetX + 60, Y(VintageHorizonsConfigLayout.VisualHeadingY - 2), 190, 32),
            "preset");

        // The cost words are a column in each half, so each gets a column heading. It sits over
        // the same box the words themselves use and shares their right edge, because a
        // right-aligned word under a left-aligned heading does not read as one column.
        for (int column = 0; column < 2; column++)
        {
            composer.AddStaticText("Cost", CairoFont.WhiteDetailText(),
                EnumTextOrientation.Right,
                ElementBounds.Fixed(VintageHorizonsConfigLayout.ColumnCostX(column),
                    Y(VintageHorizonsConfigLayout.VisualHeadingY + 10),
                    VintageHorizonsConfigLayout.CostWidth, 22));
        }

        // Farseer and its seam fog lead the list. The fog is its own switch, not Farseer's: it is
        // also drawn with Farseer off, over the cached terrain's edge.
        AddToggle(composer, 0, VisualLine(0), "silhouette-enabled",
            "Farseer horizon terrain", FarseerCost, FarseerTip, OnSilhouetteEnabledChanged);
        AddToggle(composer, 1, VisualLine(0), "silhouette-fog",
            "Seam fog", SeamFogCost, SeamFogTip, OnSilhouetteFogChanged);
        // It applies only from the next start, which the hover says along with what this session
        // runs; Save says so in chat too.
        AddToggle(composer, 1, VisualLine(1), "all-cores-gc",
            "All-Cores Garbage Collection", "More RAM", AllCoresTipText(), OnAllCoresChanged);

        // The feature rows: the left column's under Farseer, the rest under all-cores collection.
        for (int i = 0; i < Rows.Length; i++)
        {
            VisualRow row = Rows[i];
            bool left = i < VintageHorizonsConfigLayout.VisualRowsPerColumn;
            double y = left ? VisualLine(1 + i) : VisualLine(2 + i - VintageHorizonsConfigLayout.VisualRowsPerColumn);
            AddToggle(composer, left ? 0 : 1, y, row.Key, row.Title, row.Cost, row.Tip,
                on => OnVisualChanged(row, on));
        }

        if (silhouettes.NoSampler())
        {
            // A short red note under the features, its hover carrying the whole warning.
            const double noteY = VintageHorizonsConfigLayout.NoteY;
            double noteWidth = VintageHorizonsConfigLayout.ColumnWidth;
            composer.AddStaticText(NoSamplerNote, NoSamplerFont(),
                ElementBounds.Fixed(Margin, Y(noteY + 4), noteWidth, 22));
            composer.AddHoverText(NoSamplerWarning, CairoFont.WhiteSmallText(), 420,
                ElementBounds.Fixed(Margin, Y(noteY), noteWidth, RowHeight - 2), "tip-no-sampler");
        }
    }

    /// <summary>
    /// Paused loading and Developer options in the action row, on General and Developer alike, so
    /// Developer options can hide the tab from the tab itself. Small text, each label shrinking
    /// if it would not fit beside its switch.
    /// </summary>
    void ComposeFooterToggles(GuiComposer composer)
    {
        const double size = VintageHorizonsConfigLayout.RowSwitchSize;
        const double left = VintageHorizonsConfigLayout.FooterLeft;
        const double right = VintageHorizonsConfigLayout.FooterRight;
        const double gap = 16;
        const string paused = "Load terrain while paused";
        const string developer = "Developer options";
        double y = VintageHorizonsConfigLayout.ButtonY + 6;
        // The room split in proportion to each label's own width, so neither shrinks before it must.
        double pausedText = CairoFont.WhiteSmallText().GetTextExtents(paused).Width;
        double developerText = CairoFont.WhiteSmallText().GetTextExtents(developer).Width;
        double labels = right - left - gap - 2 * (size + 8);
        double pausedWidth = labels * pausedText / Math.Max(1, pausedText + developerText) + size + 8;
        AddFooterToggle(composer, left, pausedWidth, y, "load-while-paused", paused,
            PausedLoadingTip, OnPausedLoadingChanged);
        AddFooterToggle(composer, left + pausedWidth + gap, right - left - pausedWidth - gap, y,
            "developer-options", developer, DeveloperOptionsTip, OnDeveloperOptionsChanged);

        void AddFooterToggle(GuiComposer c, double x, double width, double top, string key, string title,
            string tip, Action<bool> onToggle)
        {
            double labelWidth = width - size - 8;
            c.AddStaticText(title, FittedFont(title, labelWidth, CairoFont.WhiteSmallText()),
                ElementBounds.Fixed(x, Y(top + 4), labelWidth, 22));
            c.AddSwitch(onToggle, ElementBounds.Fixed(x + width - size, Y(top), size, size), key, size);
            c.AddHoverText(tip, CairoFont.WhiteSmallText(), 380,
                ElementBounds.Fixed(x, Y(top - 2), width, size + 4), $"tip-{key}");
        }
    }

    void SetFooterValues()
    {
        if (activeComposer?.Composed != true || shownTab == GenerateTab) return;
        activeComposer.GetSwitch("load-while-paused").On = pendingLoadWhilePaused;
        activeComposer.GetSwitch("developer-options").On = showDeveloper;
    }

    static double VisualLine(int line) => VintageHorizonsConfigLayout.VisualRowsY + line * RowHeight;

    /// <summary>
    /// Farseer: a few thousand coarse quads since 0.4.93's geometry cut, and the owner's 0.4.155
    /// readings showed no visible drop with it on. An estimate, not a clean A/B.
    /// </summary>
    const string FarseerCost = "Low";

    /// <summary>
    /// The seam fog: measured by the owner at 0.52-0.68 ms a frame at 1440p (490 to 390 FPS in
    /// 0.4.155, 550 to 400 in 0.4.123), the largest single switch here.
    /// </summary>
    const string SeamFogCost = "High";

    /// <summary>
    /// One switch in a column: its title on the left, an optional cost word right-aligned before
    /// the switch, and one hover region over the whole cell, so the tip appears from the title,
    /// the cost word or the switch alike. GuiElementHoverText handles no mouse-down, so it cannot
    /// swallow the click that lands on the switch underneath it.
    /// </summary>
    void AddToggle(GuiComposer composer, int column, double y, string key, string title,
        string cost, string tip, Action<bool> onToggle)
    {
        double x = VintageHorizonsConfigLayout.ColumnX(column);
        double switchX = VintageHorizonsConfigLayout.ColumnSwitchX(column);
        double costX = VintageHorizonsConfigLayout.ColumnCostX(column);
        const double size = VintageHorizonsConfigLayout.RowSwitchSize;
        composer.AddStaticText(title, FittedFont(title, costX - 4 - x, CairoFont.WhiteSmallishText()),
            ElementBounds.Fixed(x, Y(y + 2), costX - 4 - x, 26));
        if (cost.Length > 0)
        {
            composer.AddStaticText(cost,
                FittedFont(cost, VintageHorizonsConfigLayout.CostWidth, CairoFont.WhiteDetailText()),
                EnumTextOrientation.Right,
                ElementBounds.Fixed(costX, Y(y + 5), VintageHorizonsConfigLayout.CostWidth, 22));
        }
        composer.AddSwitch(onToggle, ElementBounds.Fixed(switchX, Y(y + 1), size, size), key, size);
        composer.AddHoverText(tip, CairoFont.WhiteSmallText(), 380,
            ElementBounds.Fixed(x, Y(y), switchX + size - x, RowHeight - 2), $"tip-{key}");
    }

    void SetGeneralValues()
    {
        if (activeComposer?.Composed != true) return;
        activeComposer.GetSwitch("auto-thresholds").On = pendingAutomatic;
        activeComposer.GetSwitch("silhouette-enabled").On = pendingSilhouettes.Enabled;
        activeComposer.GetSwitch("silhouette-fog").On = pendingSilhouettes.FogEnabled;
        activeComposer.GetSwitch("all-cores-gc").On = pendingAllCores;
        foreach (VisualRow row in Rows)
        {
            activeComposer.GetSwitch(row.Key).On = row.Read(pendingVisuals);
        }
    }

    const string DeveloperOptionsTip =
        "Shows the Developer tab: every tuning dial for distant anti-aliasing, Farseer's look and "
        + "the seam fog. Hiding it keeps their values.";

    void OnDeveloperOptionsChanged(bool on)
    {
        if (on == showDeveloper) return;
        showDeveloper = on;
        developerOptions.Remember(on);
        // The tint's only switch is on Developer, so hiding the tab puts it away too, and a window
        // showing Developer moves to General.
        if (!on && pendingShowTaa)
        {
            pendingShowTaa = false;
            taa.Preview(pendingTaa, pendingShowTaa);
        }
        if (!on && shownTab == DeveloperTab) shownTab = GeneralTab;
        RequestRecompose();
    }

    /// <summary>
    /// The font, made smaller only when the text would not sit on one line in its box with room
    /// to spare. At GUI scale 1.0 on 1440p "Vanilla and LOD Seam Blending" and "More RAM" wrapped
    /// onto a second line (owner screenshots, 0.4.167 and 0.4.168); the labels stay as written
    /// (owner: labels are fine). 0.4.168 fitted to the box exactly and still wrapped: the line
    /// breaker counted it a pixel or two wider than the measure did, so it now aims for 92%.
    /// </summary>
    static CairoFont FittedFont(string text, double width, CairoFont font)
    {
        double available = GuiElement.scaled(width) * 0.92;
        double needed = font.GetTextExtents(text).Width;
        if (needed > available && needed > 0)
            font.UnscaledFontsize *= available / needed;
        return font;
    }

    const string FarseerTip =
        "A light outline of the land beyond your cached terrain, out to the blue Farseer reach on the "
        + "bar above. Needs Doden's Landscapes on the server. Off releases its tiles and GPU meshes.";

    /// <summary>
    void OnAllCoresChanged(bool on)
    {
        pendingAllCores = on;
        RefreshAllCoresStatus();
    }

    /// <summary>
    /// The tip under the all-cores switch, with what this session runs. It applies from the next
    /// start only, and the page has no room for a status line, so the hover says it.
    /// </summary>
    string AllCoresTipText() => AllCoresTip + "\n\n" + AllCoresStatus();

    void RefreshAllCoresStatus()
    {
        if (activeComposer?.Composed != true || shownTab != GeneralTab) return;
        ((GuiElementHoverText)activeComposer.GetElement("tip-all-cores-gc"))
            .SetNewText(AllCoresTipText());
    }

    string AllCoresStatus()
    {
        string now = collector.InEffect()
            ? "This session runs all-cores garbage collection."
            : "This session runs the game's default garbage collection.";
        return pendingAllCores == collector.InEffect()
            ? now
            : now + " Save, then restart the game, to switch.";
    }

    internal const string NoSamplerWarning =
        "Neither Algernon's Terrain Sampler nor Watersheds was found, so Farseer builds slowly from game "
        + "previews. Install either one (on the server, in multiplayer) for a faster, finer horizon. Neither is required.";

    /// <summary>What fits beside Developer options; hovering it shows <see cref="NoSamplerWarning"/>.</summary>
    internal const string NoSamplerNote = "No terrain sampler mod: builds slowly";

    static CairoFont NoSamplerFont() => CairoFont.WhiteSmallText().WithColor(GuiStyle.ErrorTextColor);

    /// <summary>
    /// Every tuning dial, one group at a time from the sub-tabs at the top of the page:
    /// anti-aliasing, Farseer's look and reach, or the seam fog (a drop-down until 0.4.170, owner).
    /// They show their effect as they move; Defaults resets only the group on show.
    /// </summary>
    void ComposeDeveloper(GuiComposer composer)
    {
        composer.AddHorizontalTabs(
            DeveloperGroupNames.Select((name, group) => new GuiTab { Name = name, DataInt = group }).ToArray(),
            ElementBounds.Fixed(Margin, Y(40), Width - 2 * Margin, 30), OnDeveloperGroupClicked,
            CairoFont.WhiteSmallishText(),
            CairoFont.WhiteSmallishText().WithColor(GuiStyle.ActiveButtonTextColor),
            "developer-groups");
        composer.AddStaticText(
            "Tuning dials. Changes show at once: Save keeps them, Cancel puts them back, Defaults resets this group.",
            CairoFont.WhiteSmallText(), ElementBounds.Fixed(Margin, Y(78), Width - 2 * Margin, 22));

        if (developerGroup == AntiAliasingGroup) ComposeAntiAliasingDials(composer);
        else ComposeSilhouetteDials(composer, developerGroup == SeamFogGroup);
    }

    void ComposeAntiAliasingDials(GuiComposer composer)
    {
        composer.AddDynamicText(ReachHelp(), CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(Margin, Y(104), Width - 2 * Margin, 22), "taa-reach-help");

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

        // Not an anti-aliasing dial but the same kind of number, an on-screen size; the presets
        // set it, and it waits for Save like the rest of General's distances.
        composer.AddStaticText("LOD detail target", CairoFont.WhiteSmallishText(),
            ElementBounds.Fixed(Margin, Y(y + 3), DialSliderX - Margin - 12, 26));
        composer.AddHoverText(DetailTargetTip, CairoFont.WhiteSmallText(), 380,
            ElementBounds.Fixed(Margin, Y(y - 2), DialSliderX - Margin - 12, 34), "tip-detail-target");
        composer.AddSlider(OnDetailTargetChanged,
            ElementBounds.Fixed(DialSliderX, Y(y), DialSliderWidth, 30), "detail-target");
        composer.AddDynamicText(DetailTargetLabel(), CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(DialSliderX + DialSliderWidth + 15, Y(y + 4), 110, 22),
            "detail-target-label");

        composer.AddStaticText("Show Anti-Aliased Area", CairoFont.WhiteMediumText(),
            ElementBounds.Fixed(Margin, Y(VintageHorizonsConfigLayout.ShowTaaY + 2), 650, 30));
        composer.AddSwitch(OnShowChanged,
            ElementBounds.Fixed(SwitchX, Y(VintageHorizonsConfigLayout.ShowTaaY), 32, 32),
            "taa-show");
        composer.AddHoverText(ShowTip, CairoFont.WhiteSmallText(), 380,
            ElementBounds.Fixed(Margin, Y(VintageHorizonsConfigLayout.ShowTaaY),
                SwitchX + 32 - Margin, 26), "tip-taa-show");
    }

    /// <summary>
    /// Farseer's look (with its reach, a second copy of the distance bar's) or the seam fog: a grid
    /// of three columns, title over slider.
    /// </summary>
    void ComposeSilhouetteDials(GuiComposer composer, bool fog)
    {
        double top = 118;
        if (!fog)
        {
            composer.AddStaticText("Reach", CairoFont.WhiteSmallishText(),
                ElementBounds.Fixed(Margin, Y(118), 100, 26));
            // The report `.dlsilhouette stats` prints is several lines long, so it is a hover over
            // the Reach title rather than printed, where it ran into the dials.
            composer.AddHoverText(SilhouetteStatusTip(), CairoFont.WhiteSmallText(), 500,
                ElementBounds.Fixed(Margin, Y(114), 100, 34), "silhouette-status");
            composer.AddSlider(OnSilhouetteReachSlider,
                ElementBounds.Fixed(135, Y(114), 545, 28), "silhouette-reach");
            composer.AddDynamicText(SilhouetteReachLabel(), CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(700, Y(118), 160, 24), "silhouette-reach-label");
            top = 166;
        }

        const double columnWidth = 250;
        const double columnGap = 29;
        // The readout beside each slider carries a word as well as a number ("50% Balanced"); at
        // GUI scale 1.25 and above a 92-wide box wrapped the word onto a hidden second line.
        const double sliderWidth = 140;
        const double readoutWidth = 120;
        const double rowHeight = 54;
        SilhouetteRow[] rows = GroupRows(fog);
        for (int i = 0; i < rows.Length; i++)
        {
            SilhouetteRow row = rows[i];
            double x = Margin + i % 3 * (columnWidth + columnGap);
            double y = top + i / 3 * rowHeight;
            composer.AddStaticText(row.Title, CairoFont.WhiteSmallishText(),
                ElementBounds.Fixed(x, Y(y), columnWidth, 26));
            composer.AddHoverText(row.Tip, CairoFont.WhiteSmallText(), 330,
                ElementBounds.Fixed(x, Y(y - 2), columnWidth, 28), $"tip-{row.Key}");
            composer.AddSlider(value => OnSilhouetteDialChanged(row, value),
                ElementBounds.Fixed(x, Y(y + 25), sliderWidth, 26), row.Key);
            composer.AddDynamicText(row.Label(row.Read(pendingSilhouettes)), CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(x + sliderWidth + 12, Y(y + 28), readoutWidth, 22), $"{row.Key}-label");
        }
    }

    void SetDeveloperValues()
    {
        if (activeComposer?.Composed != true) return;
        activeComposer.GetHorizontalTabs("developer-groups").activeElement = developerGroup;
        if (developerGroup == AntiAliasingGroup)
        {
            foreach (LodTaaSliders.Row row in LodTaaSliders.All)
            {
                GuiElementSlider slider = activeComposer.GetSlider(row.Key);
                slider.SetValues(row.ToSlider(row.Read(pendingTaa)), row.Min, row.Max, row.Step);
                slider.ShowTextWhenResting = false;
                slider.OnSliderTooltip = value => row.Label(row.FromSlider(value));
            }
            GuiElementSlider target = activeComposer.GetSlider("detail-target");
            target.SetValues((int)MathF.Round(pendingTargetPixels * 100),
                (int)(LodAutoThresholds.MinTargetPixels * 100), (int)(LodAutoThresholds.MaxTargetPixels * 100), 5);
            target.ShowTextWhenResting = false;
            target.OnSliderTooltip = value => $"{value / 100f:0.00} px";
            activeComposer.GetSwitch("taa-show").On = pendingShowTaa;
            return;
        }

        if (developerGroup == FarseerLookGroup)
        {
            GuiElementSlider reach = activeComposer.GetSlider("silhouette-reach");
            reach.SetValues(pendingSilhouettes.ReachBlocks, LodSilhouetteSettings.MinReachBlocks,
                LodSilhouetteSettings.MaxReachBlocks, LodSilhouetteSettings.ReachStepBlocks, " blocks");
            reach.ShowTextWhenResting = false;
        }
        foreach (SilhouetteRow row in GroupRows(developerGroup == SeamFogGroup))
        {
            GuiElementSlider slider = activeComposer.GetSlider(row.Key);
            slider.SetValues(row.Read(pendingSilhouettes), row.Min, row.Max, row.Step);
            slider.ShowTextWhenResting = false;
            slider.OnSliderTooltip = value => row.Label(value);
        }
    }

    void OnDeveloperGroupClicked(int group)
    {
        if (group < 0 || group >= DeveloperGroupNames.Length || group == developerGroup) return;
        developerGroup = group;
        RequestRecompose();
    }

    /// <summary>Whether that Developer group is the page on show, so its elements exist.</summary>
    bool ShowingGroup(int group) => activeComposer?.Composed == true
        && shownTab == DeveloperTab && developerGroup == group;

    string SilhouetteStatusTip() => silhouettes.Status() + " (.dlsilhouette stats)";

    string SilhouetteReachLabel() => $"{FormatBlocks(pendingSilhouettes.ReachBlocks)} blocks";

    bool OnDialChanged(LodTaaSliders.Row row, int value)
    {
        pendingTaa = row.Write(pendingTaa, row.FromSlider(value));
        taa.Preview(pendingTaa, pendingShowTaa);
        if (ShowingGroup(AntiAliasingGroup))
        {
            activeComposer!.GetDynamicText($"{row.Key}-label")
                .SetNewText(row.Label(row.Read(pendingTaa)), false, true, false);
            activeComposer.GetDynamicText("taa-reach-help")
                .SetNewText(ReachHelp(), false, true, false);
        }
        return true;
    }

    const string DetailTargetTip =
        "How wide a level-0 column is on screen, in pixels, where L1 begins; every later level hands "
        + "over at the same width. Lower keeps finer detail further out and draws more geometry. The "
        + "presets set it: Quality 1.5, Balanced 2, Performance 3. Used while Automatic is on; Save "
        + "keeps it. (.dldetail target)";

    string DetailTargetLabel() => string.Create(CultureInfo.InvariantCulture, $"{pendingTargetPixels:0.00} px");

    bool OnDetailTargetChanged(int value)
    {
        pendingTargetPixels = value / 100f;
        RefreshAutomaticValues();
        if (ShowingGroup(AntiAliasingGroup))
            activeComposer!.GetDynamicText("detail-target-label")
                .SetNewText(DetailTargetLabel(), false, true, false);
        return true;
    }

    /// <summary>
    /// What automatic distances would be at the pending target, kept for the distance bar, which
    /// shows them while Automatic is on.
    /// </summary>
    void RefreshAutomaticValues()
    {
        int[] values = LodWorld.NormalizeLevelThresholds(
            detail.Preview(pendingTargetPixels), LodWorld.DefaultLevelThresholds[0]);
        Array.Copy(values, automaticThresholds, automaticThresholds.Length);
        if (pendingAutomatic) scale?.SetValues(automaticThresholds);
    }

    void OnShowChanged(bool on)
    {
        pendingShowTaa = on;
        taa.Preview(pendingTaa, pendingShowTaa);
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

    bool OnSilhouetteReachSlider(int blocks)
    {
        OnSilhouetteReachChanged(blocks);
        return true;
    }

    bool OnSilhouetteDialChanged(SilhouetteRow row, int value)
    {
        pendingSilhouettes = row.Write(pendingSilhouettes, value).Normalize();
        silhouettes.Preview(pendingSilhouettes);
        bool fog = row.Key.StartsWith("sil-fog", StringComparison.Ordinal);
        if (ShowingGroup(fog ? SeamFogGroup : FarseerLookGroup))
            activeComposer!.GetDynamicText($"{row.Key}-label")
                .SetNewText(row.Label(row.Read(pendingSilhouettes)), false, true, false);
        return true;
    }

    void RefreshSilhouetteStatus()
    {
        if (!ShowingGroup(FarseerLookGroup)) return;
        ((GuiElementHoverText)activeComposer!.GetElement("silhouette-status"))
            .SetNewText(SilhouetteStatusTip());
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
        // "32 chunks / 1,024 blocks" needs about 200 wide: at GUI scale 1.25 a 170-wide box
        // dropped "blocks" onto a hidden second line.
        composer.AddSlider(OnWorldGenRadiusChanged,
            ElementBounds.Fixed(135, Y(168), 505, 30), "wg-radius");
        composer.AddDynamicText(WorldGenRadiusLabel(),
            CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Right),
            ElementBounds.Fixed(650, Y(174), Width - Margin - 650, 24),
            "wg-radius-label");

        composer.AddStaticText("Shape", CairoFont.WhiteSmallishText(),
            ElementBounds.Fixed(Margin, Y(220), 100, 28));
        composer.AddDropDown(WorldGenShapeCodes, WorldGenShapeNames,
            pendingWorldGen.Shape == EnumWorldGenShape.Circle ? 0 : 1,
            OnWorldGenShapeChanged, ElementBounds.Fixed(135, Y(214), 220, 32), "wg-shape");
        // No preset line: it named the engine's default in a place that looked like a choice,
        // and there is nothing to choose.

        composer.AddDynamicText(LodWorldGenPageMath.Estimate(pendingWorldGen),
            CairoFont.WhiteSmallText(), ElementBounds.Fixed(Margin, Y(266), Width - 2 * Margin, 28),
            "wg-estimate");
        composer.AddStaticText(
            "This permanently adds terrain to the savegame. Prepare shows the exact area, time "
            + "and disk estimate; Confirm is a separate click. Back up important worlds first.",
            CairoFont.WhiteSmallText(), ElementBounds.Fixed(Margin, Y(304), Width - 2 * Margin, 52));

        // Progress only while there is a job to report on. At rest the bar sat at 0% over two
        // copies of the server's idle message, which told the player to type a command for what
        // the buttons below already do (owner screenshot, 2026-10-13).
        composedWithProgress = ShowsWorldGenProgress(worldGen.Client?.WorldGenStatus);
        double messageY = 374;
        if (composedWithProgress)
        {
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
            messageY = 494;
        }
        composer.AddDynamicText(WorldGenLastMessage(), CairoFont.WhiteSmallText(),
            ElementBounds.Fixed(Margin, Y(messageY), Width - 2 * Margin, 66), "wg-message");

        // Paused in Settings, the page's one action is to leave for the live window, unpausing the
        // game on the way (owner, 2026-10-19); the generation buttons could do nothing here, and
        // the longer label needs their room.
        if (NeedsLiveWorldGenWindow())
        {
            composer.AddButton(LiveControlsLabel, OnWorldGenPrepare,
                ElementBounds.Fixed(Margin, Y(VintageHorizonsConfigLayout.ButtonY), 420, 38),
                EnumButtonStyle.Normal, "wg-prepare");
            worldGen.Client?.SetWorldGenPageOpen(false);
            return;
        }
        composer.AddButton("Prepare", OnWorldGenPrepare,
            ElementBounds.Fixed(Margin, Y(VintageHorizonsConfigLayout.ButtonY), 170, 38), EnumButtonStyle.Normal, "wg-prepare");
        composer.AddButton("Confirm", OnWorldGenConfirm,
            ElementBounds.Fixed(Margin + 180, Y(VintageHorizonsConfigLayout.ButtonY), 170, 38), EnumButtonStyle.Normal, "wg-confirm");
        composer.AddButton("Pause", OnWorldGenPause,
            ElementBounds.Fixed(Margin + 360, Y(VintageHorizonsConfigLayout.ButtonY), 135, 38), EnumButtonStyle.Normal, "wg-pause");
        composer.AddButton("Resume", OnWorldGenResume,
            ElementBounds.Fixed(Margin + 505, Y(VintageHorizonsConfigLayout.ButtonY), 135, 38), EnumButtonStyle.Normal, "wg-resume");
        composer.AddButton("Stop", OnWorldGenStop,
            ElementBounds.Fixed(Margin + 650, Y(VintageHorizonsConfigLayout.ButtonY), 135, 38), EnumButtonStyle.Normal, "wg-stop");

        worldGen.Client?.SetWorldGenPageOpen(true);
    }

    internal const string LiveControlsLabel = "Open Live Controls + Unpause";

    void SetGenerateValues()
    {
        if (activeComposer?.Composed != true) return;
        int maxRadius = ServerWorldGenMaxRadius();
        pendingWorldGen = pendingWorldGen.Normalize(maxRadius);
        GuiElementSlider radius = activeComposer.GetSlider("wg-radius");
        radius.SetValues(pendingWorldGen.RadiusChunks, 1, maxRadius, 1, " chunks");
        radius.ShowTextWhenResting = false;
        if (composedWithProgress)
        {
            GuiElementSlider progress = activeComposer.GetSlider("wg-progress");
            progress.SetValues(LodWorldGenPageMath.Percent(worldGen.Client?.WorldGenStatus), 0, 100, 1, "%");
            progress.ShowTextWhenResting = false;
            progress.Enabled = false;
        }

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
            return "Unavailable: load a world on a server running a current Doden's Landscapes build.";
        if (NeedsLiveWorldGenWindow())
            return "Singleplayer is paused in Settings. " + LiveControlsLabel + " to start or manage generation.";
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
            return "The button below leaves Settings, unpauses the game so the world can generate, and opens the live window.";
        AssistWorldGenStatus? status = client?.WorldGenStatus;
        if (status == null) return "Open this tab after joining a world to request server status.";
        return WorldGenPageMessage(status.State, status.Message);
    }

    internal const string WorldGenIdleHint =
        "Choose the centre, radius and shape, then Prepare to see exactly what it will do.";

    /// <summary>
    /// The page's message line for a server status. At rest it is a hint about the buttons, not
    /// the server's own idle text, which is written for the chat command. While progress shows,
    /// the progress text already ends with the server's message, so it is not repeated.
    /// </summary>
    internal static string WorldGenPageMessage(EnumWorldGenRemoteState state, string? message) =>
        state == EnumWorldGenRemoteState.Idle ? WorldGenIdleHint
        : ShowsWorldGenProgress(state) ? ""
        : LodWorldGenPageMath.PageText(message);

    /// <summary>Whether the page shows its progress rows: from the start of a run until the next Prepare.</summary>
    internal static bool ShowsWorldGenProgress(EnumWorldGenRemoteState state) =>
        state is EnumWorldGenRemoteState.Running or EnumWorldGenRemoteState.Paused
            or EnumWorldGenRemoteState.Interrupted or EnumWorldGenRemoteState.Faulted
            or EnumWorldGenRemoteState.Complete;

    static bool ShowsWorldGenProgress(AssistWorldGenStatus? status) =>
        status != null && ShowsWorldGenProgress(status.State);

    void OnWorldGenStatus(AssistWorldGenStatus status)
    {
        if (sessionClosed || activeComposer?.Composed != true || shownTab != GenerateTab) return;

        // A job starting, or a new Prepare replacing a finished one, changes which rows exist.
        if (ShowsWorldGenProgress(status) != composedWithProgress)
        {
            RequestRecompose();
            return;
        }

        activeComposer.GetDynamicText("wg-availability")
            .SetNewText(WorldGenAvailability(), false, true, false);
        activeComposer.GetDynamicText("wg-message")
            .SetNewText(WorldGenLastMessage(), false, true, false);
        if (composedWithProgress)
        {
            activeComposer.GetDynamicText("wg-progress-text")
                .SetNewText(LodWorldGenPageMath.Progress(status), false, true, false);
            activeComposer.GetDynamicText("wg-progress-label")
                .SetNewText(WorldGenPercentLabel(), false, true, false);
            activeComposer.GetSlider("wg-progress").SetValue(LodWorldGenPageMath.Percent(status));
        }
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
        // The generation buttons are not on the page while it offers only the live window.
        SetButtonEnabled("wg-prepare", canControl && (openLive
            || (!areaLocked && status?.PendingConfirmation != true)));
        SetButtonEnabled("wg-confirm", !openLive && canControl
            && status?.PendingConfirmation == true);
        SetButtonEnabled("wg-pause", !openLive && canControl
            && state == EnumWorldGenRemoteState.Running);
        SetButtonEnabled("wg-resume", !openLive && canControl
            && state is EnumWorldGenRemoteState.Paused or EnumWorldGenRemoteState.Interrupted);
        SetButtonEnabled("wg-stop", !openLive && canControl && areaLocked);
    }

    void SetButtonEnabled(string key, bool enabled)
    {
        if (activeComposer?.GetElement(key) is GuiElementTextButton button) button.Enabled = enabled;
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

    // The coordinates the game shows, measured from spawn, so they may be negative.
    void OnWorldGenXChanged(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x))
            pendingWorldGen = pendingWorldGen with { X = x };
    }

    void OnWorldGenZChanged(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int z))
            pendingWorldGen = pendingWorldGen with { Z = z };
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

        // The tab has no Save button, so the area is remembered when it is used.
        worldGen.Save(pendingWorldGen.Normalize(ServerWorldGenMaxRadius()));

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
        RequestRecompose();
    }

    /// <summary>
    /// Compose the page again once the click or status update that asked for it has finished
    /// with the composer it came from, not in the middle of it: the re-compose disposes that
    /// composer.
    /// </summary>
    void RequestRecompose()
    {
        capi.Event.EnqueueMainThreadTask(() =>
        {
            if (sessionClosed) return;
            if (embedded) requestEmbeddedRecompose?.Invoke();
            else if (IsOpened()) ComposeDialog();
        }, "dodenslandscapes-config-tab");
    }

    void OnSilhouetteEnabledChanged(bool on)
    {
        pendingSilhouettes = pendingSilhouettes with { Enabled = on };
        silhouettes.Preview(pendingSilhouettes);
        scale?.SetSilhouette(pendingSilhouettes.ReachBlocks, on);
        RefreshPresetReadout();
    }

    const string SeamFogTip = "Fog over the seam between Farseer and real terrain, or with Farseer "
        + "off, over the edge of the cached terrain. The most expensive switch here: about half a "
        + "millisecond a frame at 1440p. Off draws no fog and builds nothing for it. .dlfog toggles "
        + "it without saving.";

    void OnSilhouetteFogChanged(bool on)
    {
        pendingSilhouettes = pendingSilhouettes with { FogEnabled = on };
        silhouettes.Preview(pendingSilhouettes);
        RefreshPresetReadout();
    }

    void OnSilhouetteReachChanged(int blocks)
    {
        int reach = LodSilhouetteSettings.ClampReach(blocks);
        pendingSilhouettes = pendingSilhouettes with { ReachBlocks = reach };
        scale?.SetSilhouette(reach, pendingSilhouettes.Enabled);
        silhouettes.Preview(pendingSilhouettes);
        if (ShowingGroup(FarseerLookGroup))
            activeComposer!.GetDynamicText("silhouette-reach-label")
                .SetNewText(SilhouetteReachLabel(), false, true, false);
        RefreshSilhouetteStatus();
        RefreshPresetReadout();
    }

    /// <summary>Which preset the current combination is, or the trailing "Custom" entry.</summary>
    int PresetIndex() => LodPreset.Matching(pendingVisuals, pendingFarCap, pendingSilhouettes,
            pendingAutomatic, pendingTargetPixels) is { } preset
        ? Array.IndexOf(LodPreset.All, preset)
        : LodPreset.All.Length;

    void OnPresetSelected(string code, bool selected)
    {
        // "Custom" is a readout, not a choice.
        if (!selected || Array.Find(LodPreset.All, preset => preset.Code == code) is not { } chosen) return;
        ApplyPreset(chosen);
    }

    /// <summary>
    /// A preset's switches, draw distance, Farseer reach and detail target, with Automatic on so
    /// the target is what sets the LOD distances; the player's own distances are kept for when
    /// Automatic goes off again. The Farseer switches show at once, as they do when clicked; the
    /// rest waits for Save.
    /// </summary>
    void ApplyPreset(LodPreset preset)
    {
        pendingAutomatic = true;
        pendingTargetPixels = preset.TargetPixels;
        RefreshAutomaticValues();
        RefreshThresholds();
        pendingVisuals = preset.Visuals;
        pendingSilhouettes = preset.ApplyTo(pendingSilhouettes);
        silhouettes.Preview(pendingSilhouettes);
        pendingFarCap = preset.DrawDistance;
        scale?.SetDrawDistance(pendingFarCap);
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
        GuiElementSwitch farseer = activeComposer.GetSwitch("silhouette-enabled");
        if (farseer.On != pendingSilhouettes.Enabled) farseer.On = pendingSilhouettes.Enabled;
        GuiElementSwitch fog = activeComposer.GetSwitch("silhouette-fog");
        if (fog.On != pendingSilhouettes.FogEnabled) fog.On = pendingSilhouettes.FogEnabled;
        activeComposer.GetDropDown("preset").SetSelectedIndex(PresetIndex());
        scale?.SetSilhouette(pendingSilhouettes.ReachBlocks, pendingSilhouettes.Enabled);
    }

    /// <summary>The preset readout after a Farseer switch, draw distance or reach, which the presets also set.</summary>
    void RefreshPresetReadout()
    {
        if (activeComposer?.Composed != true || shownTab != GeneralTab) return;
        activeComposer.GetDropDown("preset").SetSelectedIndex(PresetIndex());
    }

    /// <summary>What the scale shows: automatic's live values, or the player's own.</summary>
    int[] DisplayedThresholds() => pendingAutomatic ? automaticThresholds : pendingThresholds;

    /// <summary>Why Automatic is recommended, in one line under the title.</summary>
    internal const string AutoNote =
        "Automatic sets each level by how big its blocks look on your screen, keeping detail where you can see it.";

    /// <summary>The help line that used to stand under the title, now its hover.</summary>
    const string ScaleHelp =
        "Drag Vanilla (it applies immediately), Cache for how far cached terrain is drawn, or Farseer "
        + "for its reach. L1-L6 can be dragged while Automatic is off. Grey is vanilla; blue is "
        + "Farseer's reach.";

    void OnAutomaticChanged(bool on)
    {
        pendingAutomatic = on;
        RefreshThresholds();
        RefreshPresetReadout();
    }

    void RefreshThresholds()
    {
        if (activeComposer?.Composed != true || shownTab != GeneralTab) return;
        if (scale != null)
        {
            scale.ReadOnly = pendingAutomatic;
            scale.SetValues(DisplayedThresholds());
        }
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
        // Applied first, so the automatic values below see the new vanilla edge.
        vanilla.Apply(vanillaViewDistance);
        scale?.SetVanillaDistance(vanillaViewDistance);
        RefreshAutomaticValues();
    }

    void OnPausedLoadingChanged(bool on) => pendingLoadWhilePaused = on;

    bool OnDrawDistanceChanged(int blocks)
    {
        pendingFarCap = Math.Clamp(blocks, MinDrawDistance, MaxDrawDistance);
        scale?.SetDrawDistance(pendingFarCap);
        scale?.SetSilhouette(pendingSilhouettes.ReachBlocks, pendingSilhouettes.Enabled);
        RefreshPresetReadout();
        return true;
    }

    /// <summary>
    /// The tab that is showing, back to what a fresh install draws; on Developer, only the group on
    /// show. The Farseer switches and the dials show at once, as they do when moved; nothing else
    /// applies until Save.
    /// </summary>
    bool OnDefaults()
    {
        if (shownTab == DeveloperTab)
        {
            if (developerGroup == AntiAliasingGroup)
            {
                pendingTaa = LodTaaDials.Default;
                taa.Preview(pendingTaa, pendingShowTaa);
                pendingTargetPixels = LodPreset.Quality.TargetPixels;
                RefreshAutomaticValues();
            }
            else
            {
                // Only this group's dials; Farseer's switches are General's.
                LodSilhouetteSettings defaults = LodSilhouetteSettings.Default;
                foreach (SilhouetteRow row in GroupRows(developerGroup == SeamFogGroup))
                    pendingSilhouettes = row.Write(pendingSilhouettes, row.Read(defaults));
                if (developerGroup == FarseerLookGroup)
                    pendingSilhouettes = pendingSilhouettes with { ReachBlocks = defaults.ReachBlocks };
                pendingSilhouettes = pendingSilhouettes.Normalize();
                silhouettes.Preview(pendingSilhouettes);
            }
            RequestRecompose();
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

        // What a fresh install draws: the Quality preset (owner, 2026-10-19).
        Array.Copy(LodWorld.DefaultLevelThresholds, pendingThresholds,
            pendingThresholds.Length);
        pendingAutomatic = true;
        pendingTargetPixels = LodPreset.Quality.TargetPixels;
        RefreshAutomaticValues();
        pendingFarCap = LodPreset.Quality.DrawDistance;
        pendingLoadWhilePaused = true;
        // Not the all-cores collector: it is how this machine runs the game, applies only after a
        // restart, and is not part of what is drawn (owner, 2026-10-19). Developer options is left
        // alone for the same kind of reason.
        pendingVisuals = LodPreset.Quality.Visuals;
        pendingSilhouettes = LodPreset.Quality.ApplyTo(pendingSilhouettes);
        silhouettes.Preview(pendingSilhouettes);

        RefreshThresholds();
        if (activeComposer?.Composed != true) return true;
        scale?.SetDrawDistance(pendingFarCap);
        scale?.SetSilhouette(pendingSilhouettes.ReachBlocks, pendingSilhouettes.Enabled);
        activeComposer.GetSwitch("load-while-paused").On = pendingLoadWhilePaused;
        RefreshVisuals();
        return true;
    }

    bool OnSave()
    {
        if (sessionClosed) return true;
        saved = true;
        worldGen.Save(pendingWorldGen.Normalize(ServerWorldGenMaxRadius()));
        collector.Save(pendingAllCores);
        save(pendingThresholds, pendingAutomatic, pendingFarCap, pendingLoadWhilePaused,
            pendingVisuals, pendingTaa, pendingSilhouettes.Normalize(), pendingTargetPixels);
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
    /// Closing without Save - Cancel, the title bar's cross, or Escape - puts the Farseer switches,
    /// the dials and the anti-aliasing tint back to what was in force when the window opened,
    /// because they showed their effect as they moved.
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

    static string FormatBlocks(int blocks) => blocks.ToString("N0", CultureInfo.InvariantCulture);
}
