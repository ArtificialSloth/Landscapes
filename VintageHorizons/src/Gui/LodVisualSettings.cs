namespace VintageHorizons;

/// <summary>
/// The visual features a player can give up to get frame time back, as one value.
///
/// <para>
/// One type rather than nine parameters, because these travel together everywhere: out of
/// the config file at startup, into the settings window, back out of it on Save, and into the
/// renderer. A nine-argument callback is where a caller eventually swaps two bools of the
/// same type and nothing complains.
/// </para>
/// <para>
/// Everything here is OFF-able and ON by default. Each field removes work the renderer would
/// otherwise do, so "all true" is the established look and an older config file - which names
/// none of them - loads as exactly that.
/// </para>
/// </summary>
internal readonly record struct LodVisualSettings(
    bool BlendPipeline,
    bool SeamBand,
    bool BlockTextures,
    bool ClimateTint,
    float ClimateTintRange,
    bool LeafSeasonColours,
    bool HalfBlockTerrain,
    bool WaterEffects,
    bool DistantAntiAliasing,
    bool PlantTextures,
    bool PlantDetail = true,
    bool ReedShapes = true,
    bool CropShapes = true,
    bool BushTextures = true,
    bool FruitTreeColors = true,
    LodLeafShape LeafShape = LodLeafShape.ClustersWithTops,
    bool VineShapes = true,
    bool ForageMarkers = true,
    bool FlowerSheets = false,
    bool DistantLights = true,
    float BandReach = LodVisualSettings.QualityBandReach,
    float Level0Pixels = LodVisualSettings.Level0FollowsTarget,
    bool DenseLeaves = true)
{
    /// <summary>
    /// How far the seam band reaches, as a share of level 0 beyond vanilla's edge: Balanced a
    /// third, Quality two thirds, Max all of it (owner, 2026-11-02). Level 0 is where the band's
    /// block textures live, so all of it is as far as the band can go.
    /// </summary>
    public const float BalancedBandReach = 0.33f;
    public const float QualityBandReach = 0.66f;
    public const float MaxBandReach = 1f;

    /// <summary>
    /// How wide a level-0 column is on screen where L1 begins, when that is finer than the LOD
    /// detail target; every later level keeps the target. Quality carries level 0 out to 1 px
    /// while the rest stay at 1.5 (owner, 2026-11-03: its 624-block handover felt too close; his
    /// hand-tried 960 "feels much better", measured +0.5 ms). The largest target there is means
    /// level 0 simply follows the target, which is what every config written before it loads as.
    /// </summary>
    public const float Level0FollowsTarget = LodAutoThresholds.MaxTargetPixels;
    public const float QualityLevel0Pixels = 1f;

    // What the plant switches come to. Plant Detail (General) is one switch over all of them and
    // keeps their positions, so turning it back on restores the player's own choices.
    public bool PlantTexturesOn => PlantDetail && PlantTextures;
    public bool ReedShapesOn => PlantDetail && ReedShapes;

    /// <summary>A vine's sheet is its picture, so with no sheets it is a flat patch.</summary>
    public bool VineShapesOn => PlantTexturesOn && VineShapes;
    public bool ForageMarkersOn => PlantDetail && ForageMarkers;
    public bool FlowerSheetsOn => PlantTexturesOn && FlowerSheets;
    public bool CropShapesOn => PlantDetail && CropShapes;
    public bool BushTexturesOn => PlantDetail && BushTextures;

    /// <summary>A bush's sheet is cut out by its picture, so with no pictures it is a block.</summary>
    public bool BushSheetsOn => PlantTexturesOn && BushTextures;

    /// <summary>Leaves wear their layered picture only where they wear a picture at all.</summary>
    public bool DenseLeavesOn => PlantTexturesOn && DenseLeaves;
    public bool FruitTreeColorsOn => PlantDetail && FruitTreeColors;

    /// <summary>A leaf plane is cut out by its sheet, so with no sheets leaves stay cubes.</summary>
    public LodLeafShape LeafShapeOn => PlantTexturesOn ? LeafShape : LodLeafShape.Cubes;

    /// <summary>
    /// What a fresh install draws, and what the Defaults button restores: the Quality preset's
    /// switches, every one on, with level 0 carried out to 1 px.
    /// </summary>
    public static LodVisualSettings Default => new(
        BlendPipeline: true,
        SeamBand: true,
        BlockTextures: true,
        ClimateTint: true,
        ClimateTintRange: FullClimateTintRange,
        LeafSeasonColours: true,
        HalfBlockTerrain: true,
        WaterEffects: true,
        DistantAntiAliasing: true,
        PlantTextures: true,
        FlowerSheets: true,
        Level0Pixels: QualityLevel0Pixels);

    /// <summary>
    /// Biome colour out to four blocks per voxel - levels L0, L1 and L2. The window calls this
    /// "Close Biome Colors" and it is what <see cref="ClimateTint"/> buys on its own.
    ///
    /// The units are blocks per voxel; the LEVEL is what actually matters, because a section
    /// costs the same per vertex whatever level it holds, so the bill is set by how many levels
    /// pay rather than by how much area they cover.
    ///
    /// It was briefly 1 - level 0 alone - which halved the distance distant ground wore its own
    /// biome colour, from about 2,048 blocks to about 1,024 on default thresholds, and brought
    /// the handover to the standing biome twice as close to the player. That was not a decision;
    /// it was the settings window's description being treated as a specification. The number is
    /// the measured one and the description follows it, not the other way round.
    /// </summary>
    public const float CloseClimateTintRange = 4f;

    /// <summary>
    /// Every level works out its own biome colour - "Far Biome Colors", L3 to L6 on top of
    /// L0-L2.
    /// Zero means the fade never applies. This is the 0.3.179-0.3.194 behaviour. Once called the
    /// single most expensive thing here, from Session 77's 80 us for all biome colour together;
    /// the 2026-11-02/03 preset benchmarks put it at 0.05 ms or less on every preset, so every
    /// preset but Performance has it (owner, 2026-11-03: it stays its own switch).
    /// </summary>
    public const float FullClimateTintRange = 0f;

    /// <summary>Whether the far levels are included, which is what the window's switch shows.</summary>
    public bool FarBiomeColours => ClimateTint && ClimateTintRange <= 0f;

    /// <summary>
    /// High frame rate at the expense of looks (owner, 2026-11-03), with every switch the preset
    /// benchmarks found essentially free left on: tree colours, slabs, water effects, plants with
    /// their sheets and flowers, Distant Lights (all together +0.06 ms). Off are what costs:
    /// seam blending, biome colour, distant anti-aliasing, and leaf shapes, which stay cubes.
    /// </summary>
    public static LodVisualSettings Performance => new(
        BlendPipeline: false,
        SeamBand: false,
        BlockTextures: false,
        ClimateTint: false,
        ClimateTintRange: CloseClimateTintRange,
        LeafSeasonColours: true,
        HalfBlockTerrain: true,
        WaterEffects: true,
        DistantAntiAliasing: false,
        PlantTextures: true,
        FlowerSheets: true,
        LeafShape: LodLeafShape.Cubes,
        BandReach: BalancedBandReach);

    /// <summary>
    /// More to see than Performance without giving up its frame rate (owner, 2026-11-03): every
    /// switch on, seam blending over a third of level 0, at detail target 2. 0.13 ms over the
    /// Balanced before it, which had no seam blending, far biome colour or flowers.
    /// </summary>
    public static LodVisualSettings Balanced => Default with
    {
        BandReach = BalancedBandReach,
        Level0Pixels = Level0FollowsTarget,
    };

    /// <summary>
    /// A significant step up that spares the very costly (owner, 2026-11-03): every switch on,
    /// seam blending over two thirds of level 0, and level 0 carried out to 1 px while every
    /// later level stays at the 1.5 px target, which is its one real cost (+0.5 ms).
    /// </summary>
    public static LodVisualSettings Quality => Default;

    /// <summary>
    /// The finest the mod draws (owner, 2026-11-03: Ultra renamed Max): Quality's switches, the
    /// band over all of level 0, and every level at 1 px through the detail target.
    /// </summary>
    public static LodVisualSettings Max => Default with
    {
        BandReach = MaxBandReach,
        Level0Pixels = Level0FollowsTarget,
    };

    /// <summary>
    /// Which named preset this matches, or null for a hand-made combination. Used only to
    /// light the matching button in the settings window.
    /// </summary>
    public string? PresetName =>
        this == Performance ? "Performance"
        : this == Balanced ? "Balanced"
        : this == Quality ? "Quality"
        : this == Max ? "Max"
        : null;
}

/// <summary>
/// A named preset: the visual switches plus how far cached terrain is drawn, whether Farseer and
/// its seam fog are on, Farseer's reach, and the on-screen size automatic LOD distances hand over
/// at (owner, 2026-10-19). A preset keys detail to that size rather than to block distances, so it
/// means the same at any field of view or resolution, and choosing one turns Automatic on. The settings window's drop-down
/// and the first-run prompt both pick from <see cref="All"/>.
///
/// Farseer is on in every preset, even where its reach equals the cached distance: it fills the
/// ground nobody has explored inside that distance, which is most of it in a new world (owner).
/// </summary>
internal sealed record LodPreset(string Code, string Name, LodVisualSettings Visuals,
    int DrawDistance, bool Farseer, int FarseerReach, bool SeamFog, float TargetPixels,
    string Summary)
{
    public static readonly LodPreset Performance = new("performance", "Performance",
        LodVisualSettings.Performance, 2048, true, 8192, false, 3f,
        "Cached terrain to 2,048 blocks with coarser detail, and a Farseer outline to 8,192. Only the effects that cost nothing.");

    public static readonly LodPreset Balanced = new("balanced", "Balanced",
        LodVisualSettings.Balanced, 8192, true, 8192, true, 2f,
        "Cached terrain and Farseer to 8,192 blocks, with every effect and a narrow seam blend.");

    /// <summary>What a fresh install draws (owner: players should see what the mod can do).</summary>
    public static readonly LodPreset Quality = new("quality", "Quality",
        LodVisualSettings.Quality, 8192, true, 8192, true, LodAutoThresholds.DefaultTargetPixels,
        "Cached terrain and Farseer to 8,192 blocks, full detail carried further out, with every effect.");

    /// <summary>Everything on, at the finest detail target there is, which also carries level 0 further out.</summary>
    public static readonly LodPreset Max = new("max", "Max",
        LodVisualSettings.Max, 8192, true, 8192, true, LodAutoThresholds.MinTargetPixels,
        "Everything at the finest detail, with the seam blend over all of level 0, to 8,192 blocks.");

    public static readonly LodPreset[] All = { Performance, Balanced, Quality, Max };

    /// <summary>
    /// What a new player is asked to pick from, which is about what their machine can carry. Max
    /// waits in the settings window for someone who has seen the rest.
    /// </summary>
    public static readonly LodPreset[] FirstRun = { Performance, Balanced, Quality };

    /// <summary>The preset these values are, or null for a hand-made combination.</summary>
    public static LodPreset? Matching(LodVisualSettings visuals, int drawDistance,
        LodSilhouetteSettings farseer, bool automatic, float targetPixels) => Array.Find(All, preset =>
            preset.Visuals == visuals
            && preset.DrawDistance == drawDistance
            && preset.Farseer == farseer.Enabled
            && preset.SeamFog == farseer.FogEnabled
            && preset.FarseerReach == farseer.ReachBlocks
            && automatic
            && Math.Abs(preset.TargetPixels - targetPixels) < 0.01f);

    /// <summary>Farseer's switches and reach set to this preset's; every dial left alone.</summary>
    public LodSilhouetteSettings ApplyTo(LodSilhouetteSettings farseer) => farseer with
    {
        Enabled = Farseer,
        FogEnabled = SeamFog,
        ReachBlocks = FarseerReach,
    };
}
