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

    /// <summary>
    /// The share as the slot table actually stores it: one byte per channel, packed.
    ///
    /// This is the slot key, and it is deliberately EXACT rather than coarse. It used to be the
    /// mean of the three channels rounded to eighths, which collapsed the base game's 29
    /// top-soil blocks onto six shares: `soil-medium-verysparse` (0.176, 0.125, 0.079) and
    /// `forestfloor-5` (0.188, 0.142, 0.107) both landed in bucket 1, so whichever registered
    /// first lent the other its share and 35% too much blue - and blue is where this shows,
    /// because the seasonal tint carries almost none and the untinted dirt carries all of it.
    ///
    /// Rounding to eighths of a MEAN was doubly lossy: two blocks can agree on the mean and
    /// disagree on every channel. A byte is the finest resolution the published table can carry,
    /// so keying on anything finer would distinguish slots the shader cannot tell apart, and
    /// anything coarser throws away accuracy the table could have held.
    ///
    /// Measured: 29 top-soil blocks hold 24 distinct shares at 1/64 per channel, so an exact key
    /// costs about two dozen slots of 64 rather than the six it costs today. Overflow is handled
    /// by falling back to the nearest registered share rather than by being coarse up front -
    /// see LodTintRegistry.SlotFor.
    /// </summary>
    public int Key => (Byte(R) << 16) | (Byte(G) << 8) | Byte(B);

    static int Byte(float v) => Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    /// <summary>How far another share is from this one, in bytes summed over the channels.</summary>
    public int DistanceTo(LodUntintedShare other) =>
        Math.Abs(Byte(R) - Byte(other.R))
        + Math.Abs(Byte(G) - Byte(other.G))
        + Math.Abs(Byte(B) - Byte(other.B));
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
    /// Everything the SHADER needs to finish a tint itself, one column per slot.
    ///
    /// The ramp above is a finished colour per slot per height, sampled where the player
    /// stands. This is the other way round: the pieces, so the shader can combine them with
    /// the climate of the ground it is actually drawing. Row 0 is the season colour with its
    /// alpha saying whether a season blend applies at all, row 1 the untinted share the tint
    /// must not touch, row 2 the climate map's page. Three RGBA8 rows rather than floats
    /// because every one of them is a colour or a small index.
    /// </summary>
    public const int SlotTableRows = 4;
    public const int SeasonRow = 0;
    public const int ShareRow = 1;
    public const int PageRow = 2;
    public const int FallbackRow = 3;
    readonly int[] slotTable = new int[MaxSlots * SlotTableRows];
    // Per-refresh scratch. Both are properties of the slot rather than of a position: the
    // season colour comes off the map at the calendar's own column, and the fallback is the
    // engine's own blended answer at the reference height.
    readonly float[] sampledSeason = new float[MaxSlots * 3];

    /// <summary>
    /// The engine's own blended answer at the reference height, per slot, published as the slot
    /// table's fourth row.
    ///
    /// This is what a slot draws when the shader cannot finish it - no paged climate map, the
    /// tropical season weight this mod does not reproduce, or no climate field at all. It is
    /// flat in height, which is exactly what every build before 0.3.174 drew and is honest about
    /// being a summary; the alternative was a 96-step height ramp kept alive purely for these
    /// few cases, and two parallel ways to compute one colour is a standing tax. See Session 76.
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

    /// <summary>
    /// Where the year is, as the terrain shader is being told. Not <c>Calendar.YearRel</c>:
    /// see <see cref="LodSeasonColor"/> for why those differ and what it costs.
    /// </summary>
    public float SeasonRel { get; private set; }

    /// <summary>Units of refresh work: one slot.</summary>
    public int RefreshUnitCount => representative.Count;

    public LodTintRegistry()
    {
        representative.Add(null);              // slot 0: no tint
        untintedShare.Add(LodUntintedShare.None);
        slotByMaps[(null, null, 0)] = SlotNone;
        // Identity until the first refresh publishes: an unsampled slot must not tint at all.
        // The fallback colour has to start white for the same reason. BuildSlotTable runs over
        // every slot, so a slot registered but not yet sampled would otherwise publish whatever
        // zero dilutes to - black for a block with no untinted share, which is most of them.
        // This is the same mistake that made a skipped slot draw untinted in 0.3.175, in the
        // other direction: filling a gap with a value that looks like an answer.
        for (int i = 0; i < sampledFallback.Length; i++) sampledFallback[i] = 255f;
        for (int slot = 0; slot < MaxSlots; slot++)
        {
            slotTable[SlotTableTexel(slot, FallbackRow)] = unchecked((int)0xFFFFFFFF);
            slotTable[SlotTableTexel(slot, PageRow)] =
                unchecked((int)0xFF000000) | (LodClimateMapPages.NoPage << 16);
        }
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

        var key = (climate, season, share.Key);
        if (slotByMaps.TryGetValue(key, out int slot)) return slot;

        // Out of slots: hand this block the registered slot on the SAME colour maps whose share
        // is closest. That is what a coarse key does eagerly for every block; doing it lazily
        // means shares are exact while slots last and only degrade once 64 are spoken for.
        // Untinted was the old answer and it is worse than a near neighbour by a wide margin -
        // a share is at most 0.7 and a missing tint is the whole colour.
        if (representative.Count >= MaxSlots) return NearestSlot(climate, season, share);

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

        if (slotByMaps.TryGetValue((climate, season, share.Key), out int slot)) return slot;

        // Only once the table is full does a block draw from a neighbour's slot, so only then
        // may this report one. Answering with a near neighbour before that would describe a slot
        // this block does not use and SlotFor would not give it - a diagnostic that agrees with
        // itself and not with the renderer is worse than one that says "not registered".
        return representative.Count >= MaxSlots ? NearestSlot(climate, season, share) : SlotNone;
    }

    /// <summary>
    /// The registered slot on these colour maps whose untinted share is nearest, or
    /// <see cref="SlotNone"/> when none is registered at all. Only reached once every slot is
    /// spoken for, which the base game does not do on its own.
    /// </summary>
    int NearestSlot(string? climate, string? season, LodUntintedShare share)
    {
        int best = SlotNone;
        int bestDistance = int.MaxValue;
        foreach (((string? c, string? s, int _), int slot) in slotByMaps)
        {
            if (c != climate || s != season) continue;
            if (slot <= SlotNone || slot >= untintedShare.Count) continue;
            int distance = share.DistanceTo(untintedShare[slot]);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = slot;
        }
        return best;
    }

    /// <summary>
    /// Recompute every slot's colour for the current season and climate, by applying the
    /// game's own maps to white at the given position.
    /// </summary>
    public void Refresh(IClientWorldAccessor world, int x, int z)
    {
        BeginRefresh(world);
        for (int slot = 0; slot < RefreshUnitCount; slot++) RefreshUnit(world, x, z, slot);
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

        // Nothing is cleared. A refresh writes only the slots it reaches and the table is
        // rebuilt from per-slot state at the end, so a slot that was skipped - registered after
        // the pass began, or unable to sample - keeps the colour it already had rather than
        // going out as the identity tint and drawing untinted until the next pass reaches it.
    }

    /// <summary>
    /// One unit of refresh work: one slot. The colour maps are no longer read per height -
    /// the shader applies the lapse rate at the vertex's own height - so a slot is one unit
    /// again, and the whole table republishes in as many frames as there are slots.
    /// </summary>
    public void RefreshUnit(IClientWorldAccessor world, int x, int z, int slot)
    {
        if (slot <= SlotNone || slot >= representative.Count) return;
        Block? block = representative[slot];
        if (block == null) return;
        if (!slotReferenceTaken[slot]) SampleReference(world, block, x, z, slot);
    }

    /// <summary>Atomically publish the completely refreshed ramp to the renderer.</summary>
    public void CompleteRefresh()
    {
        BuildSlotTable();
        readySlotCount = representative.Count;
        Version++;
    }

    /// <summary>
    /// Which climate map page each slot reads, or NoPage where the shader cannot finish this
    /// slot's tint itself and the flat fallback has to. Set by the renderer once the pages are
    /// built,
    /// because paging is a GPU-side concern the registry has no other reason to know about.
    /// </summary>
    readonly int[] slotPage = new int[MaxSlots];

    /// <summary>
    /// Which SEASON map page each slot reads, and whether its block is leaves. Both go into the
    /// slot table's page row so the shader can pick a row from noise instead of drawing the
    /// averaged colour - see <see cref="LodSeasonMapPages"/>. Leaves matter because the engine
    /// runs that noise at a 2-to-6 block scale for them and 12-to-24 for everything else.
    /// </summary>
    readonly int[] slotSeasonPage = new int[MaxSlots];
    readonly bool[] slotIsLeaves = new bool[MaxSlots];

    /// <summary>
    /// Record the page each slot's climate map landed on. A slot with no paged map keeps
    /// <see cref="LodClimateMapPages.NoPage"/> and its terrain draws the engine's own answer
    /// flat, which is what every build before 0.3.174 did for every slot.
    /// </summary>
    /// <summary>
    /// Give the pages any climate map a slot uses, then record where each one landed. One call
    /// so the two can never be done in the wrong order: a page index recorded before the page
    /// exists would send a slot to a page of nothing.
    /// </summary>
    internal void AdoptPages(LodClimateMapPages pages, LodSeasonMapPages seasons)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(seasons);
        pages.Adopt(representative);
        seasons.Adopt(representative);
        AssignPages(pages, seasons);
    }

    internal void AssignPages(LodClimateMapPages pages, LodSeasonMapPages seasons)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(seasons);
        for (int slot = 0; slot < MaxSlots; slot++)
        {
            Block? block = slot < representative.Count ? representative[slot] : null;
            slotPage[slot] = block != null && pages.TryPageOf(block.ClimateColorMapResolved,
                out int page) ? page : LodClimateMapPages.NoPage;
            slotSeasonPage[slot] = block != null
                && seasons.TryPageOf(block.SeasonColorMapResolved, out int seasonPage)
                ? seasonPage : LodSeasonMapPages.NoPage;
            slotIsLeaves[slot] = block != null
                && block.BlockMaterial == EnumBlockMaterial.Leaves;
        }
    }

    /// <summary>
    /// Pack the pieces the shader combines. Refusal is per slot and explicit: a slot whose
    /// season colour was never recovered this pass, or whose season map is the tropical one
    /// whose weight this mod does not reproduce, publishes NoPage and draws the engine's own
    /// answer flat. Half a correction applied confidently is worse than none.
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

            // Green carries the SEASON page and blue the flags, so the shader can pick a row
            // from noise rather than take the averaged colour. Only offered where the slot is
            // usable and actually blends a season: without both, there is no row to pick.
            int seasonPage = usable && hasSeason
                ? slotSeasonPage[slot] : LodSeasonMapPages.NoPage;
            int flags = slotIsLeaves[slot] ? 1 : 0;
            slotTable[SlotTableTexel(slot, PageRow)] = unchecked((int)0xFF000000)
                | (page << 16) | (seasonPage << 8) | flags;

            // What a slot draws when the shader cannot finish it: the engine's own blended
            // answer, diluted by the same share, flat in height. Slot 0 is the identity tint and
            // has no sampled colour, so it stays white.
            if (slot == SlotNone || slot >= representative.Count) continue;
            slotTable[SlotTableTexel(slot, FallbackRow)] = unchecked((int)0xFF000000)
                | (Byte(255f * LodTopSoil.Dilute(share.R, sampledFallback[at + 0] / 255f)) << 16)
                | (Byte(255f * LodTopSoil.Dilute(share.G, sampledFallback[at + 1] / 255f)) << 8)
                | Byte(255f * LodTopSoil.Dilute(share.B, sampledFallback[at + 2] / 255f));
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

        // Everything the shader needs from the CPU, or none of it. A slot that cannot answer
        // publishes NoPage and draws the engine's own answer flat, which is what every build
        // before the climate field drew for every slot.
        ColorMap? seasonMap = season;
        slotHasSeason[slot] = season != null;
        bool haveParts = haveSeason && tempSamples > 0 && fullSamples > 0;
        slotCorrectable[slot] = haveParts
            && seasonMap != null && seasonMap.ExtraFlags == 0
            && block.ClimateColorMapResolved != null;
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

    static TintSpread SampleGrid(IClientWorldAccessor world, Block block, int x, int y, int z)
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
            + $"season {block.SeasonColorMap ?? "none"}";
        yield return $"  slot claimed by {claimant.Code}"
            + (claimant.Code == block.Code ? " (this block)" : " (A DIFFERENT BLOCK)");
        yield return $"  untinted share: this block {Fmt(blockShare.R, blockShare.G, blockShare.B)}, "
            + $"slot carries {Fmt(slotShare.R, slotShare.G, slotShare.B)}";

        // What a slot draws when the SHADER cannot finish it - no paged climate map, or the
        // tropical season weight this mod does not reproduce. When it can, the D lines the
        // renderer adds are the ones that describe what reaches the screen; this is the floor
        // underneath them.
        int fallback = slotTable[SlotTableTexel(slot, FallbackRow)];
        (float dr, float dg, float db) = (((fallback >> 16) & 0xFF) / 255f,
            ((fallback >> 8) & 0xFF) / 255f, (fallback & 0xFF) / 255f);
        int page = (slotTable[SlotTableTexel(slot, PageRow)] >> 16) & 0xFF;

        // No angle brackets anywhere in this report. The client parses chat text as markup, so
        // a bare '>' reads as a closing tag with nothing open; it errors, and because the chat
        // scrollback is re-parsed on every redraw it keeps erroring until the message scrolls
        // away. An arrow written as '->' cost the whole panel.
        yield return $"  A flat fallback for this slot: diluted {Fmt(dr, dg, db)} "
            + $"| undiluted {Fmt(Undilute(slotShare.R, dr), Undilute(slotShare.G, dg), Undilute(slotShare.B, db))}"
            + (page == LodClimateMapPages.NoPage
                ? "   (THIS IS WHAT DRAWS: the shader cannot finish this slot)"
                : "   (not used: the shader finishes this slot, see the D lines)");

        // The lattice average and one single position, with the spread that separates them.
        TintSpread here = SampleGrid(world, block, x, y, z);
        (float pr, float pg, float pb) = SamplePoint(world, block, x, y, z);

        yield return $"  B lattice average at this height: {Fmt(here.R, here.G, here.B)}   "
            + "(64 positions, the average distant terrain is meant to be a summary of)";
        yield return $"  C game's own answer, this exact block: {Fmt(pr, pg, pb)}   "
            + "(ONE random season row: compare against B, never against a single draw. See G145)";
        yield return $"  lattice spread at this height: R {here.MinR:0.000}..{here.MaxR:0.000} "
            + $"G {here.MinG:0.000}..{here.MaxG:0.000} B {here.MinB:0.000}..{here.MaxB:0.000}";

        float dist = y - world.SeaLevel;
        ClimateCondition? climateHere = world.BlockAccessor.GetClimateAt(
            new BlockPos(x, y, z), EnumGetClimateMode.WorldGenValues);
        if (climateHere == null)
        {
            yield return "  climate here: unavailable";
            yield break;
        }

        float adjustedHere = Climate.DescaleTemperature(climateHere.WorldGenTemperature);
        yield return $"  climate here: temperature {climateHere.WorldGenTemperature:0.0}C, "
            + $"adjusted {adjustedHere:0.0}/255, {dist:0} blocks above sea level";
        yield return $"  season: read off the map at the shader's own column, SeasonRel "
            + $"{SeasonRel:0.0000}";
    }


    /// <summary>The tint a diluted table entry was built from. Guarded: a share of 1 keeps nothing.</summary>
    static float Undilute(float share, float diluted) =>
        share >= 0.999f ? 1f : (diluted - share) / (1f - share);

    static string Fmt(float r, float g, float b) => $"R{r:0.000} G{g:0.000} B{b:0.000}";
}
