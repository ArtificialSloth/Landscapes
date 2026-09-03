using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageHorizons;

/// <summary>
/// Maps each block to a tint SLOT, and keeps every slot's live colour up to date.
///
/// Vintage Story does not have one foliage tint: leaves pick a seasonal map per
/// species (seasonalOak, seasonalNeedles, seasonalBirch, seasonalMaple, ...) on top of
/// one of several climate maps, and water has its own climateWaterTint. Collapsing all
/// of that into a single "foliage" tint meant every leaf in the LOD took whichever
/// block the registry scan happened to hit first - a conifer, so nothing ever turned
/// for autumn - and water was left untinted grey.
///
/// A slot is one distinct (climate map, season map) pair. The captured colour stays
/// untinted and the slot's colour is recomputed from the game's own colour maps every
/// few seconds, so distant terrain follows the calendar without re-capturing anything.
///
/// Slots are derived from the live Block, never persisted: an existing cache picks up
/// correct per-species tints with no re-exploration, and the mapping stays right if a
/// game or mod update changes which map a block uses.
/// </summary>
/// <summary>
/// The share of a block's stored colour that the live tint must NOT touch, per channel.
///
/// Grass-covered ground is not one surface. Vanilla's own top-soil shader draws it as
/// <c>brownSoil * (1 - grass.a) + grass * grass.a</c>: the grass overlay is colour-mapped,
/// and the bare dirt showing through it is left exactly as it is. The overlay is only about
/// 69% opaque for full coverage and less for the sparse variants, so roughly a third of
/// every grassy block is untinted brown - which is what makes vanilla ground read olive
/// rather than green, and where nearly all of its blue comes from.
///
/// A LOD vertex carries one colour, so the split has to live here instead: the stored
/// colour is the composite, and the slot's tint is diluted to
/// <c>share + (1 - share) * tint</c>, which reproduces the shader exactly for the block the
/// slot was registered from.
/// </summary>
public readonly struct LodUntintedShare
{
    public readonly float R, G, B;

    public LodUntintedShare(float r, float g, float b) { R = r; G = g; B = b; }

    /// <summary>Everything is tinted: the ordinary case, and what a plain block gets.</summary>
    public static LodUntintedShare None => default;

    /// <summary>Coarse key so blocks of the same coverage share one slot, at 1/8 steps.</summary>
    public int Bucket => (int)((R + G + B) / 3f * 8f + 0.5f);
}

/// <summary>
/// Vanilla's top-soil compositing, as arithmetic: `chunktopsoil.fsh` draws grass-covered
/// ground as <c>brownSoil * (1 - grass.a) + grass * grass.a</c>, colour-mapping only the
/// grass. A LOD vertex carries one colour, so the composite is stored and the tint is
/// diluted by the share that came from untinted dirt.
///
/// The identity that makes that exact:
///   composite * (share + (1 - share) * tint) == soil * (1 - a) + grass * a * tint
/// where composite = soil * (1 - a) + grass * a and share = soil * (1 - a) / composite.
/// A check holds it, because everything the renderer does with these two values assumes it.
/// </summary>
public static class LodTopSoil
{
    /// <summary>One channel of the composite: what the face averages to, untinted.</summary>
    public static float Composite(float soil, float grass, float coverage) =>
        soil * (1f - coverage) + grass * coverage;

    /// <summary>The share of that channel the live tint must leave alone.</summary>
    public static float UntintedShare(float soil, float grass, float coverage)
    {
        float composite = Composite(soil, grass, coverage);
        return composite <= 0f ? 0f : Math.Clamp(soil * (1f - coverage) / composite, 0f, 1f);
    }

    /// <summary>The diluted tint a slot holds, from the sampled tint and the share.</summary>
    public static float Dilute(float share, float tint) => share + (1f - share) * tint;
}

public class LodTintRegistry
{
    /// <summary>Slot 0 is the identity tint, used by everything with no colour map.</summary>
    public const int SlotNone = 0;

    /// <summary>
    /// Kept small on purpose: the alpha byte carries the slot, and the shader holds one
    /// vec3 per slot. 64 covers every map pair in the base game with room to spare.
    /// </summary>
    public const int MaxSlots = 64;

    // MaxSlots is also hardcoded as `const int TINT_SLOTS` in lodterrain.vsh/.fsh, because
    // this game version offers no way to inject a #define. There used to be a second C#
    // constant mirroring that number by hand, compared against MaxSlots at shader load -
    // but comparing two constants in the same file cannot detect a shader being edited,
    // and the compiler said so, flagging the branch as unreachable. The real check reads
    // the shader files: see StaticAssetChecks in the fast tier of scripts/check.sh.

    readonly Dictionary<(string?, string?, int), int> slotByMaps = new();
    readonly List<Block?> representative = new();

    /// <summary>Per slot, the share of the colour the tint must leave alone. See LodUntintedShare.</summary>
    readonly List<LodUntintedShare> untintedShare = new();

    /// <summary>
    /// Steps in the height ramp, and how far apart they are. Everything height-dependent in a
    /// tint comes from temperature falling at a fixed 1 unit per 1.5 blocks, so the range over
    /// which anything moves is a fixed window around SEA LEVEL and is the same in a 256-block
    /// world and a 1024-block one. Above about 380 blocks over sea level both the climate
    /// coordinate and the season weight are flat for every reachable temperature, and below sea
    /// level they saturate the other way, so a fixed window with a clamp at each end is exact
    /// rather than approximate. Nothing here reads the world height.
    ///
    /// 8 blocks because that is what the season weight needs: interpolating it linearly between
    /// samples 8 apart is worth 0.034 of weight in the worst case, 16 apart is 0.072, and 32
    /// apart is 0.157 - and a weight error of 0.15 is a visibly different season.
    /// </summary>
    public const int RampSteps = 96;
    public const float RampStepBlocks = 8f;
    public const float RampMinDistToSeaLevel = -256f;

    /// <summary>
    /// How many heights the colour MAPS are actually read at. The ramp is finer than this on
    /// purpose: the weight is arithmetic and costs nothing per step, while the climate pixel is
    /// the only part that needs a map read, and it moves smoothly enough that 47 blocks between
    /// reads carries it. Do not lower this to save time - spread the refresh over more frames
    /// instead. See PLAN_DISTANT_TINT_SAMPLING section 5B.
    /// </summary>
    public const int SampledHeights = 17;

    /// <summary>
    /// The published ramp: one texel per (slot, step), already diluted by the slot's untinted
    /// share. A texture rather than a uniform array because 64x96 would be 6,144 vec4 and past
    /// any guaranteed limit, and 1/255 is far finer than a tint multiplied into an 8-bit colour
    /// can show.
    ///
    /// Packed the way this renderer's other two CPU-built textures are - BGRA ints with red at
    /// bits 16-23, so the shader reads .rgb - because `LoadOrUpdateTextureFromBgra` is the upload
    /// path and the seam field and readiness mask already agree on it.
    /// </summary>
    readonly int[] ramp = new int[MaxSlots * RampSteps];

    /// <summary>
    /// Everything the SHADER needs to finish a tint itself, one column per slot.
    ///
    /// The ramp above is a finished colour per slot per height, sampled where the player
    /// stands. This is the other way round: the pieces, so the shader can combine them with
    /// the climate of the ground it is actually drawing. Row 0 is the season colour with its
    /// alpha saying whether a season blend applies at all, row 1 the untinted share the tint
    /// must not touch, row 2 the climate map's page. Three RGBA8 rows rather than floats
    /// because every one of them is a colour or a small index.
    /// </summary>
    public const int SlotTableRows = 3;
    public const int SeasonRow = 0;
    public const int ShareRow = 1;
    public const int PageRow = 2;
    readonly int[] slotTable = new int[MaxSlots * SlotTableRows];
    readonly float[] pendingRamp = new float[MaxSlots * RampSteps * 4];

    // Per-refresh scratch. The climate pixel is the only thing sampled per height; the season
    // pixel and the raw temperature are properties of x/z alone and are gathered once per slot.
    readonly float[] sampledClimate = new float[MaxSlots * SampledHeights * 3];
    readonly float[] sampledSeason = new float[MaxSlots * 3];
    readonly float[] sampledRawTemp = new float[MaxSlots];

    /// <summary>
    /// The engine's own blended answer at the reference height, per slot. Used only where the
    /// temperature could not be read at all, so neither weight can be computed; holding the
    /// engine's answer flat up the ramp is wrong about height but is at least the colour the
    /// engine would have produced, where inventing a temperature is wrong about everything.
    /// </summary>
    readonly float[] sampledFallback = new float[MaxSlots * 3];
    readonly bool[] slotReferenceTaken = new bool[MaxSlots];
    readonly bool[] slotCorrectable = new bool[MaxSlots];

    /// <summary>
    /// Whether a slot's block blends a SEASON colour at all. Water does not: climateWaterTint
    /// has no season map, and the engine simply skips the blend for it. The ramp has to skip it
    /// too - blending toward a season colour that was never read would drag distant water down
    /// toward black by the weight. The shader path carries the same fact in the slot table's
    /// alpha.
    /// </summary>
    readonly bool[] slotHasSeason = new bool[MaxSlots];
    float pendingSeaLevel;

    // Slots are appended, never reordered, so one exclusive upper bound is the complete
    // readiness set. Slot 0 is the identity tint and is valid from construction. A later
    // palette load can append a climate/season slot after the current table was published;
    // that slot must not be used by a first-visible mesh until the next incremental sample
    // has atomically published it.
    int readySlotCount = 1;

    /// <summary>Bumped by Refresh; lets the renderer skip re-uploading unchanged tints.</summary>
    public int Version { get; private set; }

    /// <summary>The published ramp, ready for LoadOrUpdateTextureFromBgra.</summary>
    public int[] Ramp => ramp;
    public static int RampWidth => MaxSlots;
    public static int RampHeight => RampSteps;

    /// <summary>The published slot table, ready for LoadOrUpdateTextureFromBgra.</summary>
    public int[] SlotTable => slotTable;
    public static int SlotTableWidth => MaxSlots;
    public static int SlotTableHeight => SlotTableRows;

    /// <summary>
    /// Where one (slot, row) lands in <see cref="SlotTable"/>. Slot across, row down, the same
    /// arrangement the ramp uses, and held to the shader's by a static check for the same
    /// reason: a disagreement misreads every slot at once and looks like a colour bug.
    /// </summary>
    public static int SlotTableTexel(int slot, int row) => row * MaxSlots + slot;

    public int SlotCount => representative.Count;
    public int ReadySlotCount => readySlotCount;
    public bool HasUnreadySlots => readySlotCount < representative.Count;

    /// <summary>World Y of ramp step 0. Step n is this plus n * RampStepBlocks.</summary>
    public float RampOriginY { get; private set; }

    /// <summary>
    /// Where the year is, as the terrain shader is being told. Not <c>Calendar.YearRel</c>:
    /// see <see cref="LodSeasonColor"/> for why those differ and what it costs.
    /// </summary>
    public float SeasonRel { get; private set; }

    /// <summary>Units of refresh work: one slot at one sampled height.</summary>
    public int RefreshUnitCount => representative.Count * SampledHeights;

    /// <summary>
    /// Where one (slot, step) triple starts in <see cref="Ramp"/>. The renderer uploads these
    /// bytes and the vertex shader indexes them as a texture, so this addressing rule exists in
    /// two languages and a static check holds the two together - the same arrangement the
    /// readiness mask and the seam field already use, and for the same reason: a silent
    /// disagreement misaddresses every slot at once and looks like a colour bug.
    /// </summary>
    public static int RampTexel(int slot, int step) => step * MaxSlots + slot;

    public LodTintRegistry()
    {
        representative.Add(null);              // slot 0: no tint
        untintedShare.Add(LodUntintedShare.None);
        slotByMaps[(null, null, 0)] = SlotNone;
        // Identity until the first refresh publishes: an unsampled slot must not tint at all.
        for (int i = 0; i < ramp.Length; i++) ramp[i] = unchecked((int)0xFFFFFFFF);
        for (int i = 0; i < pendingRamp.Length; i++) pendingRamp[i] = 1f;
    }

    /// <summary>
    /// A block carrying climatePlantTint, used for plants that declare no colour map of
    /// their own. Ferns are the case that forced this: their textures ship greyscale
    /// (stored colour is exactly RGB 148,148,148) and vanilla greens them from its block
    /// class rather than from JSON, so an untinted LOD cube came out grey.
    /// </summary>
    public Block? PlantTintFallback;

    /// <summary>
    /// Slot for this block, registering a new one if this (map pair, untinted share) is
    /// unseen. The share is part of the key because two blocks on the same colour maps can
    /// still need different amounts of it - full, sparse and very sparse grass coverage
    /// share `climatePlantTint`/`seasonalGrass` and show quite different amounts of bare
    /// dirt through it.
    /// </summary>
    public int SlotFor(Block? block, LodUntintedShare share)
    {
        if (block == null) return SlotNone;

        string? climate = block.ClimateColorMapResolved != null ? block.ClimateColorMap : null;
        string? season = block.SeasonColorMapResolved != null ? block.SeasonColorMap : null;

        if (climate == null && season == null)
        {
            return block.BlockMaterial == EnumBlockMaterial.Plant && PlantTintFallback != null
                ? SlotFor(PlantTintFallback, LodUntintedShare.None)
                : SlotNone;
        }

        var key = (climate, season, share.Bucket);
        if (slotByMaps.TryGetValue(key, out int slot)) return slot;

        if (representative.Count >= MaxSlots) return SlotNone; // out of slots: untinted beats wrong
        slot = representative.Count;
        representative.Add(block);
        untintedShare.Add(share);
        slotByMaps[key] = slot;
        return slot;
    }

    /// <summary>
    /// The slot this block would use if one is already registered, or <see cref="SlotNone"/>.
    /// Registers nothing: a diagnostic must not create a slot, because a new slot forces an
    /// off-cadence refresh and withholds every mesh that uses it until the table republishes.
    /// </summary>
    public int PeekSlot(Block? block, LodUntintedShare share)
    {
        if (block == null) return SlotNone;

        string? climate = block.ClimateColorMapResolved != null ? block.ClimateColorMap : null;
        string? season = block.SeasonColorMapResolved != null ? block.SeasonColorMap : null;

        if (climate == null && season == null)
        {
            return block.BlockMaterial == EnumBlockMaterial.Plant && PlantTintFallback != null
                ? PeekSlot(PlantTintFallback, LodUntintedShare.None)
                : SlotNone;
        }

        return slotByMaps.TryGetValue((climate, season, share.Bucket), out int slot) ? slot : SlotNone;
    }

    /// <summary>
    /// Recompute every slot's colour for the current season and climate, by applying the
    /// game's own maps to white at the given position.
    /// </summary>
    public void Refresh(IClientWorldAccessor world, int x, int z)
    {
        BeginRefresh(world);
        for (int unit = 0; unit < RefreshUnitCount; unit++) RefreshUnit(world, x, z, unit);
        CompleteRefresh();
    }

    /// <summary>Start an incremental refresh without changing the currently displayed table.</summary>
    public void BeginRefresh(IClientWorldAccessor world) => BeginRefresh(world, SeasonRel);

    /// <summary>
    /// Start a refresh at a known season position. The caller passes the engine's own
    /// `DefaultShaderUniforms.SeasonRel` - the very number it hands its terrain shaders - so
    /// the mod cannot disagree with the shader about what time of year it is.
    /// </summary>
    public void BeginRefresh(IClientWorldAccessor world, float seasonRel)
    {
        SeasonRel = seasonRel;
        pendingSeaLevel = world.SeaLevel;
        Array.Clear(slotReferenceTaken);

        // Seed from what is already published. A refresh writes only the slots it reaches, and
        // CompleteRefresh publishes the whole buffer, so without this a slot that was skipped -
        // registered after the pass began, or unable to sample - would go out as the identity
        // tint and its terrain would draw untinted until the next pass reached it.
        for (int texel = 0; texel < ramp.Length; texel++)
        {
            int packed = ramp[texel];
            pendingRamp[texel * 4 + 0] = ((packed >> 16) & 0xFF) / 255f;
            pendingRamp[texel * 4 + 1] = ((packed >> 8) & 0xFF) / 255f;
            pendingRamp[texel * 4 + 2] = (packed & 0xFF) / 255f;
            pendingRamp[texel * 4 + 3] = 1f;
        }
    }

    /// <summary>
    /// How far one of the sampled heights sits above ramp step 0, in blocks. Separated from the
    /// world so a check can hold it against the ramp's own span: the first and last sampled
    /// heights have to land exactly on the first and last ramp steps, or the ends of the ramp
    /// extrapolate off the end of the samples that feed them.
    /// </summary>
    public static float SampledHeightOffset(int index) =>
        index * (RampSteps - 1) * RampStepBlocks / (SampledHeights - 1);

    /// <summary>The world Y one of the sampled heights sits at.</summary>
    float SampledHeightY(int index) =>
        pendingSeaLevel + RampMinDistToSeaLevel + SampledHeightOffset(index);

    /// <summary>
    /// One unit of refresh work: one slot at one sampled height. Safe to spread over frames, and
    /// deliberately smaller than a whole slot - reading the maps at seventeen heights in one frame
    /// would be an order of magnitude more colour-map calls than the old two-height refresh did.
    /// A slot's ramp is rebuilt once its last height lands.
    /// </summary>
    public void RefreshUnit(IClientWorldAccessor world, int x, int z, int unit)
    {
        if (unit < 0 || unit >= RefreshUnitCount) return;
        int slot = unit / SampledHeights;
        int height = unit % SampledHeights;
        if (slot <= SlotNone || slot >= representative.Count) return;
        Block? block = representative[slot];
        if (block == null) return;

        SampleClimate(world, block, x, (int)SampledHeightY(height), z, slot, height);
        if (!slotReferenceTaken[slot]) SampleReference(world, block, x, z, slot);
        if (height == SampledHeights - 1) BuildRamp(slot);
    }

    /// <summary>
    /// How much to scale a slot's seasonal share by at this height, or 0 for "leave it alone".
    ///
    /// Zero is returned for every case this cannot answer honestly rather than an approximation
    /// being applied. The correction is worth up to a doubling of the seasonal colour, so a wrong
    /// one is far worse than none.
    /// </summary>
    float SeasonScale(IClientWorldAccessor world, Block block, int x, int y, int z)
    {
        if (!SeasonWeightCorrection) return 0f;

        // Nothing to rebalance without both maps: the engine's blend never runs.
        ColorMap? season = block.SeasonColorMapResolved;
        if (season == null || block.ClimateColorMapResolved == null) return 0f;

        // The shader takes a different weight entirely for a map carrying ExtraFlags 1 - the
        // tropical branch in colormap.vsh, which only `tropicalKapok` uses in the base game.
        // That formula is not reproduced here, so those keep the engine's answer.
        if (season.ExtraFlags != 0) return 0f;

        float distToSeaLevel = y - world.SeaLevel;
        ClimateCondition? climate = world.BlockAccessor.GetClimateAt(
            new BlockPos(x, y, z), EnumGetClimateMode.WorldGenValues);
        if (climate == null) return 0f;

        // What the shader receives as tempRel * 255: the altitude-adjusted unscaled temperature.
        // DescaleTemperature is the exact inverse of the scaling the climate read applied, and it
        // saturates at the same 0..255 the tesselator packs, so the two agree even at the ends.
        float adjusted = Climate.DescaleTemperature(climate.WorldGenTemperature);
        if (!LodSeasonWeight.CanRecoverRawTemperature(adjusted, distToSeaLevel)) return 0f;

        float scale = LodSeasonWeight.SeasonalShareScale(adjusted, distToSeaLevel);
        return float.IsFinite(scale) && scale > 0f ? scale : 0f;
    }

    /// <summary>
    /// Rebalance the engine's climate/season blend to the one the terrain shader draws.
    /// See <see cref="LodSeasonWeight"/>. On by default; `.vhtint weight off` restores the
    /// engine's own blend, which is what every build before this one used.
    /// </summary>
    public bool SeasonWeightCorrection { get; set; } = true;

    /// <summary>Atomically publish the completely refreshed ramp to the renderer.</summary>
    public void CompleteRefresh()
    {
        for (int i = 0; i < ramp.Length; i++)
        {
            int r = Math.Clamp((int)MathF.Round(pendingRamp[i * 4 + 0] * 255f), 0, 255);
            int g = Math.Clamp((int)MathF.Round(pendingRamp[i * 4 + 1] * 255f), 0, 255);
            int b = Math.Clamp((int)MathF.Round(pendingRamp[i * 4 + 2] * 255f), 0, 255);
            ramp[i] = unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;
        }
        RampOriginY = pendingSeaLevel + RampMinDistToSeaLevel;
        BuildSlotTable();
        readySlotCount = representative.Count;
        Version++;
    }

    /// <summary>
    /// Which climate map page each slot reads, or NoPage where the shader cannot finish this
    /// slot's tint itself and the ramp has to. Set by the renderer once the pages are built,
    /// because paging is a GPU-side concern the registry has no other reason to know about.
    /// </summary>
    readonly int[] slotPage = new int[MaxSlots];

    /// <summary>
    /// Record the page each slot's climate map landed on. A slot with no paged map keeps
    /// <see cref="LodClimateMapPages.NoPage"/> and its terrain draws from the ramp, which is
    /// what every build before the climate field did for every slot.
    /// </summary>
    /// <summary>
    /// Give the pages any climate map a slot uses, then record where each one landed. One call
    /// so the two can never be done in the wrong order: a page index recorded before the page
    /// exists would send a slot to a page of nothing.
    /// </summary>
    internal void AdoptPages(LodClimateMapPages pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        pages.Adopt(representative);
        AssignPages(pages);
    }

    internal void AssignPages(LodClimateMapPages pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        for (int slot = 0; slot < MaxSlots; slot++)
        {
            Block? block = slot < representative.Count ? representative[slot] : null;
            slotPage[slot] = block != null && pages.TryPageOf(block.ClimateColorMapResolved,
                out int page) ? page : LodClimateMapPages.NoPage;
        }
    }

    /// <summary>
    /// Pack the pieces the shader combines. Refusal is per slot and explicit: a slot whose
    /// season colour was never recovered this pass, or whose season map is the tropical one
    /// whose weight this mod does not reproduce, publishes NoPage and keeps the ramp. Half a
    /// correction applied confidently is worse than none.
    /// </summary>
    void BuildSlotTable()
    {
        for (int slot = 0; slot < MaxSlots; slot++)
        {
            LodUntintedShare share = slot < untintedShare.Count
                ? untintedShare[slot] : LodUntintedShare.None;
            int at = slot * 3;

            bool hasSeason = slot > SlotNone && slot < representative.Count
                && slotCorrectable[slot];
            int seasonR = hasSeason ? Byte(sampledSeason[at + 0]) : 0;
            int seasonG = hasSeason ? Byte(sampledSeason[at + 1]) : 0;
            int seasonB = hasSeason ? Byte(sampledSeason[at + 2]) : 0;
            slotTable[SlotTableTexel(slot, SeasonRow)] =
                (hasSeason ? unchecked((int)0xFF000000) : 0)
                | (seasonR << 16) | (seasonG << 8) | seasonB;

            slotTable[SlotTableTexel(slot, ShareRow)] = unchecked((int)0xFF000000)
                | (Byte(share.R * 255f) << 16) | (Byte(share.G * 255f) << 8)
                | Byte(share.B * 255f);

            // A slot the shader cannot finish is marked here rather than left to be inferred
            // from a colour, so the fallback is one explicit value in one place.
            bool usable = slot > SlotNone
                && slotPage[slot] != LodClimateMapPages.NoPage
                && (hasSeason || NoSeasonMap(slot));
            int page = usable ? slotPage[slot] : LodClimateMapPages.NoPage;
            slotTable[SlotTableTexel(slot, PageRow)] =
                unchecked((int)0xFF000000) | (page << 16);
        }
    }

    /// <summary>A slot whose block carries a climate map but no season map: nothing to blend.</summary>
    bool NoSeasonMap(int slot)
    {
        Block? block = slot < representative.Count ? representative[slot] : null;
        return block != null && block.SeasonColorMapResolved == null;
    }

    static int Byte(float value) => Math.Clamp((int)MathF.Round(value), 0, 255);

    /// <summary>
    /// The lattice-averaged climate pixel at one height, stored for this slot. The climate map is
    /// the only part of the tint that needs a map read per height: the season pixel's average does
    /// not move with height and the weight is arithmetic.
    /// </summary>
    void SampleClimate(IClientWorldAccessor world, Block block, int x, int y, int z,
        int slot, int heightIndex)
    {
        int maxX = world.BlockAccessor.MapSizeX - 1;
        int maxZ = world.BlockAccessor.MapSizeZ - 1;
        double r = 0, g = 0, b = 0;

        for (int i = 0; i < SampleGridSide; i++)
        {
            int sx = GameMath.Clamp(x + (i - SampleGridSide / 2) * SampleGridStride, 0, maxX);
            for (int j = 0; j < SampleGridSide; j++)
            {
                int sz = GameMath.Clamp(z + (j - SampleGridSide / 2) * SampleGridStride, 0, maxZ);
                int rgba = world.ApplyColorMapOnRgba(
                    block.ClimateColorMapResolved, null, unchecked((int)0xFFFFFFFF), sx, y, sz);
                r += (rgba >> 16) & 0xFF; g += (rgba >> 8) & 0xFF; b += rgba & 0xFF;
            }
        }

        const float n = SampleGridSide * SampleGridSide;
        int at = (slot * SampledHeights + heightIndex) * 3;
        sampledClimate[at + 0] = (float)(r / n);
        sampledClimate[at + 1] = (float)(g / n);
        sampledClimate[at + 2] = (float)(b / n);
    }

    /// <summary>
    /// The two things that do not vary with height: the lattice-averaged season pixel, and the
    /// raw temperature the weight is computed from. Taken at sea level, where the adjusted and raw
    /// temperatures are the same value so no reconstruction is needed.
    ///
    /// The season pixel is recovered rather than read. Asking the engine for the season map alone
    /// returns `white * (1 - w) + season * w`, and asking for both returns
    /// `climate * (1 - w) + season * w`; the difference between them is `(255 - climate) * (1 - w)`,
    /// which yields the engine's OWN weight with no temperature involved, and the season pixel
    /// follows. Recovering it this way rather than reproducing the season map's row hash keeps one
    /// more piece of engine arithmetic out of this mod - see G139 and G140 for why that matters.
    /// </summary>
    void SampleReference(IClientWorldAccessor world, Block block, int x, int z, int slot)
    {
        slotReferenceTaken[slot] = true;
        slotCorrectable[slot] = false;

        int y = (int)pendingSeaLevel;
        int maxX = world.BlockAccessor.MapSizeX - 1;
        int maxZ = world.BlockAccessor.MapSizeZ - 1;
        double temp = 0;
        double fr = 0, fg = 0, fb = 0;
        int tempSamples = 0, fullSamples = 0;
        var pos = new BlockPos(0, y, 0);

        for (int i = 0; i < SampleGridSide; i++)
        {
            int sx = GameMath.Clamp(x + (i - SampleGridSide / 2) * SampleGridStride, 0, maxX);
            for (int j = 0; j < SampleGridSide; j++)
            {
                int sz = GameMath.Clamp(z + (j - SampleGridSide / 2) * SampleGridStride, 0, maxZ);
                // This height IS sea level, so the adjusted temperature the climate read
                // returns is also the raw one and no reconstruction is involved.
                pos.Set(sx, y, sz);
                ClimateCondition? climate = world.BlockAccessor.GetClimateAt(
                    pos, EnumGetClimateMode.WorldGenValues);
                if (climate == null) continue;
                float rawHere = Climate.DescaleTemperature(climate.WorldGenTemperature);
                temp += rawHere;
                tempSamples++;

                int white = unchecked((int)0xFFFFFFFF);
                int full = world.ApplyColorMapOnRgba(
                    block.ClimateColorMapResolved, block.SeasonColorMapResolved, white, sx, y, sz);
                fr += (full >> 16) & 0xFF; fg += (full >> 8) & 0xFF; fb += full & 0xFF;
                fullSamples++;
            }
        }

        int at = slot * 3;
        if (fullSamples > 0)
        {
            sampledFallback[at + 0] = (float)(fr / fullSamples);
            sampledFallback[at + 1] = (float)(fg / fullSamples);
            sampledFallback[at + 2] = (float)(fb / fullSamples);
        }
        // The season colour comes off the map itself, at the column the SHADER reads rather
        // than the one the engine's CPU call reads. Those differ by up to 0.05 of a year, and
        // through the autumn turn that is worth about ten counts of red - which is exactly the
        // 5 to 6 percent excess red the owner measured against vanilla.
        ColorMap? season = block.SeasonColorMapResolved;
        bool haveSeason = season == null || LodSeasonColor.TryAverage(
            season.Pixels, season.OuterSize?.Width ?? 0, season.OuterSize?.Height ?? 0,
            season.Padding, SeasonRel,
            out sampledSeason[at + 0], out sampledSeason[at + 1], out sampledSeason[at + 2]);
        if (season == null) sampledSeason[at + 0] = sampledSeason[at + 1] = sampledSeason[at + 2] = 0f;

        // Everything the correction needs, or none of it. A slot that cannot answer keeps the
        // engine's own weight, which is exactly what every build before this one drew.
        // A negative temperature is the single "this slot cannot be rebuilt" signal, and it has
        // to cover a missing SEASON pixel too: BuildRamp blends against that pixel whether or not
        // the weight correction applies, so proceeding without one paints from stale memory.
        ColorMap? seasonMap = season;
        slotHasSeason[slot] = season != null;
        bool haveParts = haveSeason && tempSamples > 0 && fullSamples > 0;
        sampledRawTemp[slot] = haveParts ? (float)(temp / tempSamples) : -1f;
        slotCorrectable[slot] = haveParts
            && seasonMap != null && seasonMap.ExtraFlags == 0
            && block.ClimateColorMapResolved != null;
    }

    /// <summary>
    /// Turn one slot's sampled heights into its ramp. The climate pixel is interpolated between
    /// the heights it was read at; the weight is computed at every step, which is the whole point
    /// of the ramp being finer than the sampling.
    /// </summary>
    void BuildRamp(int slot)
    {
        LodUntintedShare share = untintedShare[slot];
        int seasonAt = slot * 3;
        bool correct = SeasonWeightCorrection && slotCorrectable[slot];
        float raw = sampledRawTemp[slot];

        // No temperature means neither weight can be computed. Publish the engine's own answer
        // flat rather than blending with a made-up one: a fabricated temperature would produce a
        // confident colour that nothing in the world supports.
        if (raw < 0f)
        {
            int flat = slot * 3;
            for (int step = 0; step < RampSteps; step++)
            {
                int to = RampTexel(slot, step) * 4;
                for (int ch = 0; ch < 3; ch++)
                {
                    float shareFlat = ch == 0 ? share.R : ch == 1 ? share.G : share.B;
                    pendingRamp[to + ch] = Math.Clamp(
                        LodTopSoil.Dilute(shareFlat, sampledFallback[flat + ch] / 255f), 0f, 1f);
                }
                pendingRamp[to + 3] = 1f;
            }
            return;
        }

        for (int step = 0; step < RampSteps; step++)
        {
            float dist = RampMinDistToSeaLevel + step * RampStepBlocks;

            float t = step * (SampledHeights - 1f) / (RampSteps - 1f);
            int i0 = Math.Clamp((int)t, 0, SampledHeights - 2);
            float f = t - i0;
            int a0 = (slot * SampledHeights + i0) * 3;
            int a1 = a0 + 3;

            // No season map means no blend, whatever the weight says.
            if (!slotHasSeason[slot])
            {
                int flatInto = RampTexel(slot, step) * 4;
                for (int ch = 0; ch < 3; ch++)
                {
                    float climateOnly = sampledClimate[a0 + ch]
                        + (sampledClimate[a1 + ch] - sampledClimate[a0 + ch]) * f;
                    float shareOnly = ch == 0 ? share.R : ch == 1 ? share.G : share.B;
                    pendingRamp[flatInto + ch] = Math.Clamp(
                        LodTopSoil.Dilute(shareOnly, climateOnly / 255f), 0f, 1f);
                }
                pendingRamp[flatInto + 3] = 1f;
                continue;
            }

            float weight;
            if (correct)
            {
                float adjusted = Climate.GetAdjustedTemperature(
                    (int)MathF.Round(raw), (int)MathF.Round(dist));
                weight = LodSeasonWeight.Vanilla(adjusted, dist);
            }
            else
            {
                weight = LodSeasonWeight.Engine(Math.Clamp((int)MathF.Round(raw), 0, 255));
            }

            int into = RampTexel(slot, step) * 4;
            for (int ch = 0; ch < 3; ch++)
            {
                float climate = sampledClimate[a0 + ch] + (sampledClimate[a1 + ch] - sampledClimate[a0 + ch]) * f;
                float season = sampledSeason[seasonAt + ch];
                float tint = (climate * (1f - weight) + season * weight) / 255f;
                float shareCh = ch == 0 ? share.R : ch == 1 ? share.G : share.B;
                pendingRamp[into + ch] = Math.Clamp(LodTopSoil.Dilute(shareCh, tint), 0f, 1f);
            }
            pendingRamp[into + 3] = 1f;
        }
    }

    /// <summary>
    /// True when every live tint used by this section has been sampled and published.
    /// Out-of-range slots are treated as identity because the mesher normalizes them to
    /// SlotNone; withholding such a section would disagree with what its mesh can encode.
    /// </summary>
    public bool SectionTintsReady(LodSection section) =>
        SectionTintsReady(section, readySlotCount);

    internal static bool SectionTintsReady(LodSection section, int readySlotCount)
    {
        for (int i = 0; i < section.Palette.Count; i++)
        {
            int slot = section.Palette[i].TintSlot;
            if (slot > SlotNone && slot < MaxSlots && slot >= readySlotCount) return false;
        }
        return true;
    }

    /// <summary>
    /// New slots are appearance work, not an ordinary seasonal update: they bypass the
    /// cadence immediately. Stable published tables retain the 30-second cadence.
    /// </summary>
    internal static bool RefreshDue(bool initialized, bool hasUnreadySlots,
        long elapsedMilliseconds, long intervalMilliseconds) =>
        !initialized || hasUnreadySlots || elapsedMilliseconds >= intervalMilliseconds;

    /// <summary>
    /// A new world must not briefly inherit the previous world's climate table. Existing
    /// slots stay registered, but tinted meshes wait until this world publishes them.
    /// </summary>
    public void InvalidateReadiness() => readySlotCount = 1;

    /// <summary>
    /// Positions each tint is averaged over, on a lattice of this many blocks. A seasonal
    /// map is not one colour: `seasonalGrass` is 128x16, and the engine picks the ROW from
    /// a hash of each block's own position, so what a field actually looks like is all
    /// sixteen rows mixed together. A single sample takes one row and paints every distant
    /// field with it - in midsummer the rows run #628100 to #97B825 around a true mean of
    /// #7B9C0D, so the green was off by up to a quarter in red, and it re-rolled every time
    /// the player moved far enough to change the hash.
    ///
    /// 64 positions eight blocks apart cover a 56-block square: wide enough for the hashes
    /// to decorrelate, narrow enough that the climate underneath them is still the player's
    /// own. The renderer updates one slot per frame and publishes the completed table at
    /// once, avoiding both a frame spike and a half-old/half-new seasonal palette.
    /// </summary>
    const int SampleGridSide = 8;
    const int SampleGridStride = 8;

    /// <summary>
    /// One lattice average, undiluted, with the range it collapsed. The renderer uses only
    /// the average; the range is what <see cref="Describe"/> reports, and it is carried here
    /// rather than measured separately so the diagnostic cannot describe a sampler the
    /// renderer is not using.
    /// </summary>
    public readonly record struct TintSpread(
        float R, float G, float B,
        float MinR, float MaxR, float MinG, float MaxG, float MinB, float MaxB);

    /// <summary>
    /// Correct the engine's blend of the two colour maps to the one the terrain shader draws.
    /// See <see cref="LodSeasonWeight"/> for why they differ; this is the application of it.
    ///
    /// The engine returns `climate * (1 - wEngine) + season * wEngine`, so the whole correction
    /// lives in the part that moved away from the climate colour, and neither the season pixel nor
    /// the season map's row has to be reproduced:
    /// <code>
    ///   corrected = climate + (engineAnswer - climate) * (wVanilla / wEngine)
    /// </code>
    /// The result stays a blend of the same two colours whenever `wVanilla` is in 0..1, which it
    /// is by construction, so it cannot leave the range the engine could have returned. It is
    /// clamped anyway; a byte that escaped would be a bright speck on distant ground.
    ///
    /// One extra colour-map call per sampled position. `RefreshSlot` skips this entirely for a
    /// slot it cannot correct, so the cost is only paid where it buys something.
    /// </summary>
    static int Corrected(IClientWorldAccessor world, Block block, int engineAnswer,
        int x, int y, int z, float scale)
    {
        int climateOnly = world.ApplyColorMapOnRgba(
            block.ClimateColorMapResolved, null, unchecked((int)0xFFFFFFFF), x, y, z);

        int result = unchecked((int)0xFF000000);
        for (int shift = 0; shift <= 16; shift += 8)
        {
            float climate = (climateOnly >> shift) & 0xFF;
            float engine = (engineAnswer >> shift) & 0xFF;
            int channel = (int)MathF.Round(climate + (engine - climate) * scale);
            result |= Math.Clamp(channel, 0, 255) << shift;
        }
        return result;
    }

    static TintSpread SampleGrid(IClientWorldAccessor world, Block block, int x, int y, int z,
        float seasonScale)
    {
        // Clamped to the map: GetClimate answers 0 - freezing and bone dry - for a position
        // off the edge of the world, and one such sample drags the whole average with it.
        int maxX = world.BlockAccessor.MapSizeX - 1;
        int maxZ = world.BlockAccessor.MapSizeZ - 1;

        int r = 0, g = 0, b = 0;
        int minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
        for (int i = 0; i < SampleGridSide; i++)
        {
            int sx = GameMath.Clamp(x + (i - SampleGridSide / 2) * SampleGridStride, 0, maxX);
            for (int j = 0; j < SampleGridSide; j++)
            {
                int sz = GameMath.Clamp(z + (j - SampleGridSide / 2) * SampleGridStride, 0, maxZ);

                int rgba = world.ApplyColorMapOnRgba(
                    block.ClimateColorMapResolved, block.SeasonColorMapResolved,
                    unchecked((int)0xFFFFFFFF), sx, y, sz);

                if (seasonScale > 0f) rgba = Corrected(world, block, rgba, sx, y, sz, seasonScale);

                // Unpacked by hand rather than through ColorUtil.ToRGBAFloats, which
                // allocates a float[4] per call and this now calls it 64 times per slot
                // per height. The channel order is the one that function uses, and it is
                // the trap here: ApplyColorMapOnRgba flips red and blue by default, so red
                // arrives at bits 16-23. Reading red out of the low byte swapped R and B
                // and turned every grass tint teal.
                int pr = (rgba >> 16) & 0xFF, pg = (rgba >> 8) & 0xFF, pb = rgba & 0xFF;
                r += pr; g += pg; b += pb;
                if (pr < minR) minR = pr; if (pr > maxR) maxR = pr;
                if (pg < minG) minG = pg; if (pg > maxG) maxG = pg;
                if (pb < minB) minB = pb; if (pb > maxB) maxB = pb;
            }
        }

        const float scale = SampleGridSide * SampleGridSide * 255f;
        return new TintSpread(r / scale, g / scale, b / scale,
            minR / 255f, maxR / 255f, minG / 255f, maxG / 255f, minB / 255f, maxB / 255f);
    }

    /// <summary>
    /// The game's own colour-map answer for one exact block, with no averaging and no
    /// dilution. This is the reference the slot is supposed to reproduce.
    ///
    /// It is the CPU path, not the terrain shader: `colormap.vsh` derives the same two map
    /// coordinates itself and adds per-block value noise on both axes. The two agree on what
    /// the maps say at a position, so a gap between this and the slot is the mod's own doing,
    /// but a small difference against what a screenshot measures is expected and is not
    /// evidence on its own.
    /// </summary>
    static (float R, float G, float B) SamplePoint(
        IClientWorldAccessor world, Block block, int x, int y, int z)
    {
        int rgba = world.ApplyColorMapOnRgba(
            block.ClimateColorMapResolved, block.SeasonColorMapResolved,
            unchecked((int)0xFFFFFFFF), x, y, z);
        return (((rgba >> 16) & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, (rgba & 0xFF) / 255f);
    }

    /// <summary>
    /// Take one slot apart, for `.vhtint`.
    ///
    /// Three things separate the colour a cached surface is multiplied by from the colour the
    /// game would multiply the same block by, and each has its own line here so a reading can
    /// say which one is responsible rather than only that they disagree:
    ///
    /// - the slot's untinted share is whichever block claimed the 1/8 bucket first, and may
    ///   not be this block's own;
    /// - the tint is averaged over a 56-block lattice and all sixteen season-map rows, where
    ///   the game reads one exact position;
    /// - the table holds two heights and the shader interpolates between them linearly, where
    ///   the game's own seasonal weight falls off on a curve that is nothing like a line.
    ///
    /// Every value is reported undiluted unless the line says otherwise, because the stored
    /// table is already diluted and comparing a diluted number against the game's undiluted
    /// one would blame the sampler for the share.
    /// </summary>
    public IEnumerable<string> Describe(
        IClientWorldAccessor world, Block block, int slot, LodUntintedShare blockShare,
        int x, int y, int z)
    {
        if (slot <= SlotNone || slot >= representative.Count)
        {
            yield return $"{block.Code} has no live tint slot: it is drawn with its stored colour "
                + "exactly as captured. Nothing here applies to it.";
            yield break;
        }

        Block claimant = representative[slot] ?? block;
        LodUntintedShare slotShare = untintedShare[slot];

        yield return $"{block.Code} at {x},{y},{z} | sea level {world.SeaLevel} | "
            + $"slot {slot} of {representative.Count - 1} ({readySlotCount - 1} published)";
        yield return $"  maps: climate {block.ClimateColorMap ?? "none"}, "
            + $"season {block.SeasonColorMap ?? "none"}, share bucket {blockShare.Bucket}";
        yield return $"  slot claimed by {claimant.Code}"
            + (claimant.Code == block.Code ? " (this block)" : " (A DIFFERENT BLOCK)");
        yield return $"  untinted share: this block {Fmt(blockShare.R, blockShare.G, blockShare.B)}, "
            + $"slot carries {Fmt(slotShare.R, slotShare.G, slotShare.B)}";

        // What the shader will actually multiply by at this height, read out of the published
        // ramp exactly the way the vertex shader reads it, and the same value with the slot's
        // share taken back out so it can be compared with the game's answer.
        float rampT = Math.Clamp((y - RampOriginY) / RampStepBlocks, 0f, RampSteps - 1f);
        int step0 = Math.Clamp((int)rampT, 0, RampSteps - 2);
        float rampF = rampT - step0;
        int t0 = ramp[RampTexel(slot, step0)], t1 = ramp[RampTexel(slot, step0 + 1)];
        (float dr, float dg, float db) = (
            Lerp(((t0 >> 16) & 0xFF) / 255f, ((t1 >> 16) & 0xFF) / 255f, rampF),
            Lerp(((t0 >> 8) & 0xFF) / 255f, ((t1 >> 8) & 0xFF) / 255f, rampF),
            Lerp((t0 & 0xFF) / 255f, (t1 & 0xFF) / 255f, rampF));

        yield return $"  ramp: {RampSteps} steps of {RampStepBlocks:0} blocks from y={RampOriginY:0}"
            + $" | this height is step {rampT:0.00}"
            + $" | maps read at {SampledHeights} heights, {SampledHeightY(1) - SampledHeightY(0):0} apart";
        // No angle brackets anywhere in this report. The client parses chat text as markup, so
        // a bare '>' reads as a closing tag with nothing open; it errors, and because the chat
        // scrollback is re-parsed on every redraw it keeps erroring until the message scrolls
        // away. An arrow written as '->' cost the whole panel.
        yield return $"  A shader uses here: diluted {Fmt(dr, dg, db)} "
            + $"| undiluted {Fmt(Undilute(slotShare.R, dr), Undilute(slotShare.G, dg), Undilute(slotShare.B, db))}";

        // The two stages between A and the game's own answer, measured separately: the same
        // lattice re-run at THIS height removes the two-height interpolation from the picture,
        // so what is left between it and C is the averaging alone.
        float scale = SeasonScale(world, block, x, y, z);
        TintSpread here = SampleGrid(world, block, x, y, z, scale);
        (float pr, float pg, float pb) = SamplePoint(world, block, x, y, z);

        yield return $"  B lattice average at this height: {Fmt(here.R, here.G, here.B)}   "
            + "(A vs B is the two-height table, INCLUDING its weight at those two heights)";
        yield return $"  C game's own answer, this exact block: {Fmt(pr, pg, pb)}";

        // C carries the ENGINE's blend weight while A and B carry the shader's, so comparing them
        // directly mixes the weight correction into whatever else is being measured. The same
        // point corrected the same way is the like-for-like partner for B, and the gap between
        // the two C lines is the correction on its own.
        if (scale > 0f)
        {
            int point = world.ApplyColorMapOnRgba(
                block.ClimateColorMapResolved, block.SeasonColorMapResolved,
                unchecked((int)0xFFFFFFFF), x, y, z);
            int fixedPoint = Corrected(world, block, point, x, y, z, scale);
            yield return "  C' the same block with the weight corrected: "
                + Fmt(((fixedPoint >> 16) & 0xFF) / 255f, ((fixedPoint >> 8) & 0xFF) / 255f,
                      (fixedPoint & 0xFF) / 255f)
                + "   (B vs C' is the 56-block lattice and the season rows, alone)";
        }
        yield return $"  lattice spread at this height: R {here.MinR:0.000}..{here.MaxR:0.000} "
            + $"G {here.MinG:0.000}..{here.MaxG:0.000} B {here.MinB:0.000}..{here.MaxB:0.000}";

        // The season/climate blend weight, both ways. C above goes through the engine's CPU call
        // and so carries the engine's weight, which means A against C cannot see this difference
        // at all - it cancels. It has to be reported rather than measured.
        ColorMap? season = block.SeasonColorMapResolved;
        if (season == null || block.ClimateColorMapResolved == null)
        {
            yield return "  season weight: n/a, this block does not blend two colour maps";
            yield break;
        }

        float dist = y - world.SeaLevel;
        ClimateCondition? climate = world.BlockAccessor.GetClimateAt(
            new BlockPos(x, y, z), EnumGetClimateMode.WorldGenValues);
        if (climate == null)
        {
            yield return "  season weight: climate unavailable here";
            yield break;
        }

        float adjusted = Climate.DescaleTemperature(climate.WorldGenTemperature);
        float raw = LodSeasonWeight.RawFromAdjusted(adjusted, dist);
        float vanilla = LodSeasonWeight.Vanilla(adjusted, dist);
        float engine = LodSeasonWeight.Engine(Math.Clamp((int)MathF.Round(raw), 0, 255));

        yield return $"  climate here: temperature {climate.WorldGenTemperature:0.0}C, "
            + $"adjusted {adjusted:0.0}/255, raw {raw:0.0}/255, {dist:0} blocks above sea level";
        yield return $"  season weight: shader {vanilla:0.000}, engine {engine:0.000}, "
            + $"correction x{(engine > 0f ? vanilla / engine : 1f):0.000}"
            + (scale > 0f ? "" : " (NOT APPLIED here)");
        if (scale <= 0f)
        {
            string why = !SeasonWeightCorrection ? "switched off with .vhtint weight off"
                : season.ExtraFlags != 0 ? "this map uses the shader's tropical weight, which is not reproduced"
                : !LodSeasonWeight.CanRecoverRawTemperature(adjusted, dist)
                    ? "the adjusted temperature is saturated, so the engine's own weight cannot be recovered"
                    : "the computed scale was not usable";
            yield return $"    reason: {why}";
        }
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>The tint a diluted table entry was built from. Guarded: a share of 1 keeps nothing.</summary>
    static float Undilute(float share, float diluted) =>
        share >= 0.999f ? 1f : (diluted - share) / (1f - share);

    static string Fmt(float r, float g, float b) => $"R{r:0.000} G{g:0.000} B{b:0.000}";
}
