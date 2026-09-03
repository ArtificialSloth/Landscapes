namespace VintageHorizons;

/// <summary>
/// The visual features a player can give up to get frame time back, as one value.
///
/// <para>
/// One type rather than seven parameters, because these travel together everywhere: out of
/// the config file at startup, into the settings window, back out of it on Save, and into the
/// renderer. A seven-argument callback is where a caller eventually swaps two bools of the
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
    bool WaterEffects)
{
    /// <summary>
    /// What a fresh install draws, and what the Defaults button restores.
    ///
    /// Everything except the FAR half of the biome colour, which is off because it is the most
    /// expensive single thing here and the whole point of the 0.3.195 work was to stop paying
    /// for it by default. Quality turns it on.
    /// </summary>
    public static LodVisualSettings Default => new(
        BlendPipeline: true,
        SeamBand: true,
        BlockTextures: true,
        ClimateTint: true,
        ClimateTintRange: CloseClimateTintRange,
        LeafSeasonColours: true,
        HalfBlockTerrain: true,
        WaterEffects: true);

    /// <summary>
    /// Biome colour on level 0 alone - one voxel, one block. The window calls this "Close
    /// Biome Colors" and it is what <see cref="ClimateTint"/> buys on its own.
    ///
    /// The units are blocks per voxel, and the level is what actually matters: a section costs
    /// the same per vertex whatever level it holds, so the bill is set by how many LEVELS pay
    /// rather than by how much area they cover.
    /// </summary>
    public const float CloseClimateTintRange = 1f;

    /// <summary>
    /// Every level works out its own biome colour - "Far Biome Colors", L1 to L6 on top of L0.
    /// Zero means the fade never applies. This is the 0.3.179-0.3.194 behaviour, and it is the
    /// single most expensive thing the renderer can be asked to do.
    /// </summary>
    public const float FullClimateTintRange = 0f;

    /// <summary>Whether the far levels are included, which is what the window's switch shows.</summary>
    public bool FarBiomeColours => ClimateTint && ClimateTintRange <= 0f;

    /// <summary>
    /// Nothing optional drawn. The floor for a machine that cannot afford any of it, and the
    /// state measured as matching 0.3.127's frame time exactly.
    /// </summary>
    public static LodVisualSettings Performance => new(
        BlendPipeline: false,
        SeamBand: false,
        BlockTextures: false,
        ClimateTint: false,
        ClimateTintRange: CloseClimateTintRange,
        LeafSeasonColours: false,
        HalfBlockTerrain: false,
        WaterEffects: false);

    /// <summary>
    /// The two cheapest-per-pixel features kept, the two per-vertex ones given up.
    ///
    /// The split follows the measurement rather than taste: the seam band and the water terms
    /// are paid by the fragments that show them, which is a ring and a lake surface, while the
    /// biome colour and the block textures are paid by every vertex in the view whether or not
    /// anything on screen ends up showing them.
    /// </summary>
    public static LodVisualSettings Balanced => new(
        BlendPipeline: false,
        SeamBand: true,
        BlockTextures: false,
        ClimateTint: true,
        ClimateTintRange: CloseClimateTintRange,
        LeafSeasonColours: false,
        HalfBlockTerrain: true,
        WaterEffects: true);

    /// <summary>Everything, including biome colour at every level. The most the mod draws.</summary>
    public static LodVisualSettings Quality => Default with
    {
        ClimateTintRange = FullClimateTintRange,
    };

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
