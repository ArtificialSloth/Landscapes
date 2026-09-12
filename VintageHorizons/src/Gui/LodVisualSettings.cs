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
    bool DistantAntiAliasing)
{
    /// <summary>
    /// What a fresh install draws, and what the Defaults button restores.
    ///
    /// Everything except the FAR half of the biome colour, which is off because it is the most
    /// expensive single thing here and the whole point of the 0.3.195 work was to stop paying
    /// for it by default. Quality keeps that deliberate exception too.
    /// </summary>
    public static LodVisualSettings Default => new(
        BlendPipeline: true,
        SeamBand: true,
        BlockTextures: true,
        ClimateTint: true,
        ClimateTintRange: CloseClimateTintRange,
        LeafSeasonColours: true,
        HalfBlockTerrain: true,
        WaterEffects: true,
        DistantAntiAliasing: true);

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
    /// Zero means the fade never applies. This is the 0.3.179-0.3.194 behaviour, and it is the
    /// single most expensive thing the renderer can be asked to do.
    /// </summary>
    public const float FullClimateTintRange = 0f;

    /// <summary>Whether the far levels are included, which is what the window's switch shows.</summary>
    public bool FarBiomeColours => ClimateTint && ClimateTintRange <= 0f;

    /// <summary>
    /// Nothing optional drawn. The floor for a machine that cannot afford any of it, and the
    /// state measured as matching 0.3.127's frame time exactly - which is why distant
    /// anti-aliasing, a pass that did not exist then, is off here too.
    /// </summary>
    public static LodVisualSettings Performance => new(
        BlendPipeline: false,
        SeamBand: false,
        BlockTextures: false,
        ClimateTint: false,
        ClimateTintRange: CloseClimateTintRange,
        LeafSeasonColours: false,
        HalfBlockTerrain: false,
        WaterEffects: false,
        DistantAntiAliasing: false);

    /// <summary>
    /// The two cheapest-per-pixel features kept, the two per-vertex ones given up.
    ///
    /// The split follows the measurement rather than taste: the seam band and the water terms
    /// are paid by the fragments that show them, which is a ring and a lake surface, while the
    /// biome colour and the block textures are paid by every vertex in the view whether or not
    /// anything on screen ends up showing them. Distant anti-aliasing is a per-pixel pass over
    /// distant terrain alone and stays.
    /// </summary>
    public static LodVisualSettings Balanced => new(
        BlendPipeline: false,
        SeamBand: true,
        BlockTextures: false,
        ClimateTint: true,
        ClimateTintRange: CloseClimateTintRange,
        LeafSeasonColours: false,
        HalfBlockTerrain: true,
        WaterEffects: true,
        DistantAntiAliasing: true);

    /// <summary>Every default visual feature, while keeping costly far biome colour off.</summary>
    public static LodVisualSettings Quality => Default;

    /// <summary>
    /// Which named preset this matches, or null for a hand-made combination. Used only to
    /// light the matching button in the settings window.
    /// </summary>
    public string? PresetName =>
        this == Performance ? "Performance"
        : this == Balanced ? "Balanced"
        : this == Quality ? "Quality"
        : null;
}
