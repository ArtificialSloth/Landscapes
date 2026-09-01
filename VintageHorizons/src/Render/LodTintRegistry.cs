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

    // vec4 per slot: the uniform upload path takes 4 components per element.
    // Two altitude samples per slot, because the climate maps are indexed by
    // temperature and temperature falls with height - the same lapse rate the snow
    // line uses. Sampling once at the player's feet painted mountaintops with valley
    // green instead of the colder, redder grass that actually grows up there. The
    // shader interpolates between these by vertex height.
    readonly float[] tintsLow = new float[MaxSlots * 4];
    readonly float[] tintsHigh = new float[MaxSlots * 4];
    readonly float[] pendingTintsLow = new float[MaxSlots * 4];
    readonly float[] pendingTintsHigh = new float[MaxSlots * 4];
    float pendingSampleYLow;
    float pendingSampleYHigh;

    // Slots are appended, never reordered, so one exclusive upper bound is the complete
    // readiness set. Slot 0 is the identity tint and is valid from construction. A later
    // palette load can append a climate/season slot after the current table was published;
    // that slot must not be used by a first-visible mesh until the next incremental sample
    // has atomically published it.
    int readySlotCount = 1;

    /// <summary>Bumped by Refresh; lets the renderer skip re-uploading unchanged tints.</summary>
    public int Version { get; private set; }
    public float[] TintsLow => tintsLow;
    public float[] TintsHigh => tintsHigh;
    public int SlotCount => representative.Count;
    public int ReadySlotCount => readySlotCount;
    public bool HasUnreadySlots => readySlotCount < representative.Count;

    /// <summary>World Y the two tint tables were sampled at.</summary>
    public float SampleYLow { get; private set; }
    public float SampleYHigh { get; private set; }

    public LodTintRegistry()
    {
        representative.Add(null);              // slot 0: no tint
        untintedShare.Add(LodUntintedShare.None);
        slotByMaps[(null, null, 0)] = SlotNone;
        for (int i = 0; i < tintsLow.Length; i++)
            tintsLow[i] = tintsHigh[i] = pendingTintsLow[i] = pendingTintsHigh[i] = 1f;
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
        for (int slot = 1; slot < representative.Count; slot++) RefreshSlot(world, x, z, slot);
        CompleteRefresh();
    }

    /// <summary>Start an incremental refresh without changing the currently displayed table.</summary>
    public void BeginRefresh(IClientWorldAccessor world)
    {
        Array.Copy(tintsLow, pendingTintsLow, tintsLow.Length);
        Array.Copy(tintsHigh, pendingTintsHigh, tintsHigh.Length);
        // Span the height range terrain actually occupies around the viewer, so the
        // interpolation covers valley floor to peak rather than extrapolating.
        pendingSampleYLow = world.SeaLevel;
        pendingSampleYHigh = world.SeaLevel + 320;
    }

    /// <summary>Refresh one climate/season tint slot; safe to spread over render frames.</summary>
    public void RefreshSlot(IClientWorldAccessor world, int x, int z, int slot)
    {
        if (slot <= SlotNone || slot >= representative.Count) return;
        Block? block = representative[slot];
        if (block == null) return;

        Sample(world, block, x, (int)pendingSampleYLow, z,
            pendingTintsLow, slot, untintedShare[slot]);
        Sample(world, block, x, (int)pendingSampleYHigh, z,
            pendingTintsHigh, slot, untintedShare[slot]);
    }

    /// <summary>Atomically publish the completely refreshed table to the renderer.</summary>
    public void CompleteRefresh()
    {
        Array.Copy(pendingTintsLow, tintsLow, tintsLow.Length);
        Array.Copy(pendingTintsHigh, tintsHigh, tintsHigh.Length);
        SampleYLow = pendingSampleYLow;
        SampleYHigh = pendingSampleYHigh;
        readySlotCount = representative.Count;
        Version++;
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

    static void Sample(IClientWorldAccessor world, Block block, int x, int y, int z, float[] into,
        int slot, LodUntintedShare share)
    {
        TintSpread grid = SampleGrid(world, block, x, y, z);

        // Dilute by the share the tint must not touch, so a slot registered from
        // grass-covered soil reproduces vanilla's top-soil shader: the bare dirt that
        // shows through the overlay stays the colour it already is.
        into[slot * 4 + 0] = LodTopSoil.Dilute(share.R, grid.R);
        into[slot * 4 + 1] = LodTopSoil.Dilute(share.G, grid.G);
        into[slot * 4 + 2] = LodTopSoil.Dilute(share.B, grid.B);
        into[slot * 4 + 3] = 1f;
    }

    /// <summary>
    /// One lattice average, undiluted, with the range it collapsed. The renderer uses only
    /// the average; the range is what <see cref="Describe"/> reports, and it is carried here
    /// rather than measured separately so the diagnostic cannot describe a sampler the
    /// renderer is not using.
    /// </summary>
    public readonly record struct TintSpread(
        float R, float G, float B,
        float MinR, float MaxR, float MinG, float MaxG, float MinB, float MaxB);

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
            + $"season {block.SeasonColorMap ?? "none"}, share bucket {blockShare.Bucket}";
        yield return $"  slot claimed by {claimant.Code}"
            + (claimant.Code == block.Code ? " (this block)" : " (A DIFFERENT BLOCK)");
        yield return $"  untinted share: this block {Fmt(blockShare.R, blockShare.G, blockShare.B)}, "
            + $"slot carries {Fmt(slotShare.R, slotShare.G, slotShare.B)}";

        // What the shader will actually multiply by at this height, and the same value with
        // the slot's share taken back out so it can be compared with the game's answer.
        float blend = Math.Clamp((y - SampleYLow) / Math.Max(1f, SampleYHigh - SampleYLow), 0f, 1f);
        (float dr, float dg, float db) = (
            Lerp(tintsLow[slot * 4 + 0], tintsHigh[slot * 4 + 0], blend),
            Lerp(tintsLow[slot * 4 + 1], tintsHigh[slot * 4 + 1], blend),
            Lerp(tintsLow[slot * 4 + 2], tintsHigh[slot * 4 + 2], blend));

        yield return $"  table: low y={SampleYLow:0} {Fmt(tintsLow[slot * 4], tintsLow[slot * 4 + 1], tintsLow[slot * 4 + 2])}"
            + $" | high y={SampleYHigh:0} {Fmt(tintsHigh[slot * 4], tintsHigh[slot * 4 + 1], tintsHigh[slot * 4 + 2])}"
            + "   (both diluted)";
        // No angle brackets anywhere in this report. The client parses chat text as markup, so
        // a bare '>' reads as a closing tag with nothing open; it errors, and because the chat
        // scrollback is re-parsed on every redraw it keeps erroring until the message scrolls
        // away. An arrow written as '->' cost the whole panel.
        yield return $"  A shader uses here: blend {blend:0.000} | diluted {Fmt(dr, dg, db)} "
            + $"| undiluted {Fmt(Undilute(slotShare.R, dr), Undilute(slotShare.G, dg), Undilute(slotShare.B, db))}";

        // The two stages between A and the game's own answer, measured separately: the same
        // lattice re-run at THIS height removes the two-height interpolation from the picture,
        // so what is left between it and C is the averaging alone.
        TintSpread here = SampleGrid(world, block, x, y, z);
        (float pr, float pg, float pb) = SamplePoint(world, block, x, y, z);

        yield return $"  B lattice average at this height: {Fmt(here.R, here.G, here.B)}   "
            + "(A vs B is the two-height interpolation)";
        yield return $"  C game's own answer, this exact block: {Fmt(pr, pg, pb)}   "
            + "(B vs C is the 56-block lattice and the season rows)";
        yield return $"  lattice spread at this height: R {here.MinR:0.000}..{here.MaxR:0.000} "
            + $"G {here.MinG:0.000}..{here.MaxG:0.000} B {here.MinB:0.000}..{here.MaxB:0.000}";
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>The tint a diluted table entry was built from. Guarded: a share of 1 keeps nothing.</summary>
    static float Undilute(float share, float diluted) =>
        share >= 0.999f ? 1f : (diluted - share) / (1f - share);

    static string Fmt(float r, float g, float b) => $"R{r:0.000} G{g:0.000} B{b:0.000}";
}
