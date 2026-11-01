using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VintageHorizons;

/// <summary>
/// How a block is drawn, or whether it is drawn at all. Shared by both sides: a section
/// captured by a server and one captured by a client have to agree about what counts as
/// terrain, or the same ground would come out differently depending on who saw it first.
///
/// Which tint applies is a separate question, answered client-side by LodTintRegistry.
/// </summary>
public static class LodBlockPolicy
{
    public static byte FlagsFor(Block block)
    {
        // Vanilla snow is captured as real blocks, but the distant representation needs
        // only its coverage. Keeping this as a distinct derived flag (rather than Skip)
        // leaves the run available to older readers while current meshers draw the surface
        // below it with a snow material marker.
        if (block.BlockMaterial == EnumBlockMaterial.Snow)
        {
            return LodPaletteEntry.FlagSnowLayer;
        }

        if (block.BlockMaterial is EnumBlockMaterial.Water or EnumBlockMaterial.Lava)
        {
            return LodPaletteEntry.FlagWater;
        }

        // Ice is solid terrain, including the glacier and packed-glacier layers used on
        // mountain summits. It used to share FlagWater merely because some ice uses the
        // transparent render pass. That made the LOD wave it like liquid, and—more
        // importantly—submitted whole glaciers to the engine's liquid-depth target. The
        // quarter-resolution depth then applied underwater murk around snowy peaks.
        //
        // Let ice continue through the ordinary terrain rules. Palette flags are rebuilt
        // from the live Block whenever stored sections load, so existing caches gain the
        // correction without re-exploration.

        // Sparse, mostly-transparent ground cover. As solid LOD cubes these come out as
        // pale grey blobs, because their textures average toward the transparent pixels.
        // Not all of EnumBlockMaterial.Plant: skipping every plant was tried and
        // flattened the landscape, and dense cover like grass reads fine as solid colour.
        // The Plant guard also keeps ferntree (material Wood, an actual tree) opaque.
        // A fern is a mat past level 0 and crossed planes at it, so it carries both flags.
        if (block.BlockMaterial == EnumBlockMaterial.Plant && IsThinGroundCover(block))
        {
            return IsCrossedPlant(block)
                ? (byte)(LodPaletteEntry.FlagThin | CrossedFlags(block))
                : LodPaletteEntry.FlagThin;
        }

        // Not terrain at all, so it never becomes geometry.
        if (block.BlockMaterial is EnumBlockMaterial.Fire or EnumBlockMaterial.Meta)
        {
            return LodPaletteEntry.FlagSkip;
        }

        byte flags = 0;

        if (block.Variant != null
            && block.Variant.TryGetValue("cover", out string? cover)
            && string.Equals(cover, "snow", StringComparison.OrdinalIgnoreCase))
        {
            flags |= LodPaletteEntry.FlagSnowCovered;
        }

        // Terrain that only fills the bottom half of its cell. Runs are stored in whole
        // blocks, so this is the only place the difference can be recorded, and the mesher
        // lowers the surface by half a block wherever it is set.
        if (IsBottomHalfBlock(block)) flags |= LodPaletteEntry.FlagHalf;

        // Drawn as a cube, but it covers nothing. See IsSeeThroughPlant.
        if (IsSeeThroughPlant(block)) flags |= LodPaletteEntry.FlagCutout;

        if (IsCrossedPlant(block)) flags |= CrossedFlags(block);

        return flags;
    }

    /// <summary>
    /// A plant drawn as crossed planes at level 0: reeds, ferns and crops. A fern as a mat is half
    /// see-through and a quarter block off the ground, so it reads as nothing. A crop's game art
    /// is planes already, so a cube was only ever a block-shaped patch of its colour.
    /// </summary>
    public static bool IsCrossedPlant(Block block) => IsReedOrFern(block) || block is BlockCrop;

    /// <summary>The crossed plants capture has to look above the rain map for (<see cref="ReedTable"/>).</summary>
    static bool IsReedOrFern(Block block) =>
        block is BlockReeds
        || (block.BlockMaterial == EnumBlockMaterial.Plant && block.Code?.Path is { } path
            && (path.StartsWith("fern-", StringComparison.Ordinal)
                || path.StartsWith("tallfern", StringComparison.Ordinal)));

    /// <summary>A reed or fern, whose planes follow its flags (see LodMesher).</summary>
    public const byte CrossedShapeByFlags = 0;

    /// <summary>A crop on planes a block tall, standing on the ground as the game's own do.</summary>
    public const byte CrossedShapeCrop = 1;

    /// <summary>A crop on the game's two-block planes.</summary>
    public const byte CrossedShapeTallCrop = 2;

    /// <summary>
    /// How a crossed plant stands. Not a flag: flags are stored in the palette and every bit of
    /// the byte is taken, and this is only ever wanted while meshing.
    /// </summary>
    public static byte CrossedShapeOf(Block? block)
    {
        if (block is not BlockCrop) return CrossedShapeByFlags;
        return block.Shape?.Base?.Path?.Contains("tall", StringComparison.Ordinal) == true
            ? CrossedShapeTallCrop
            : CrossedShapeCrop;
    }

    /// <summary>
    /// A berry bush, wild or planted. The game builds one from many see-through sheets, so no
    /// single texture of its own can dress a cube (LodBushTile). Its block entity tracks growth
    /// and has no bearing on its colour, which is why a bush may hold a texture slot.
    /// </summary>
    public static bool IsBerryBush(Block block) =>
        block.Code?.Path?.StartsWith("fruitingbush-", StringComparison.Ordinal) == true;

    /// <summary>Sedge and ferns stand about half as tall as reeds, and a cut reed is a stub.</summary>
    static byte CrossedFlags(Block block) =>
        block.Code?.Path is { } path
        && (path.Contains("brownsedge", StringComparison.Ordinal)
            || path.Contains("-harvested-", StringComparison.Ordinal)
            || path.StartsWith("fern-", StringComparison.Ordinal))
            ? (byte)(LodPaletteEntry.FlagCrossed | LodPaletteEntry.FlagHalf)
            : LodPaletteEntry.FlagCrossed;

    /// <summary>
    /// A plant the game draws as a see-through cutout rather than a solid cube: a crop, tall
    /// grass, a berry bush, a vine, a mushroom.
    ///
    /// Read from the engine's own occlusion answer, not from a list of names.
    /// <c>Block.AllSidesOpaque</c> is the property vanilla's own tesselator consults to decide
    /// whether a block hides the face next to it, and every one of these declares
    /// <c>sideopaque: {all: false}</c> in its block data. The LOD goes on drawing them as
    /// cubes - that is what keeps a meadow from flattening - but a cube that claims an
    /// occlusion the game does not provide opens a hole wherever the game draws instead of us,
    /// which is the vanilla seam. See LodMesher's coverage rules.
    ///
    /// The Plant guard is the same one thin cover uses, and it is doing real work: leaves,
    /// glass, fences, stairs and chiselled blocks are all non-opaque too, and freeing every
    /// one of them from covering would put a wall between every pair of leaves in every
    /// canopy. Leaves have the same defect at the seam and are deliberately left for a
    /// measurement of their own.
    /// </summary>
    static bool IsSeeThroughPlant(Block block) =>
        block.BlockMaterial == EnumBlockMaterial.Plant && !block.AllSidesOpaque;

    /// <summary>
    /// Which block ids are reeds: cattails, papyrus, tule and sedge, by the engine's own class.
    /// Capture starts at the game's rain map, which passes straight through a block that lets
    /// rain through, so on land a reed sits one block above where capture begins and only drew
    /// where a tree happened to stop the rain above it. In water it shares its cell with the
    /// water, which is what a capture read answers. Capture looks for reeds in both places.
    ///
    /// A table, because capture runs on a worker that does not touch the block registry.
    /// Deliberately not every block rain passes through: that is also tall grass, loose stones,
    /// torches and signs, and tall grass alone would raise every meadow by a block.
    /// </summary>
    public static bool[] ReedTable(IList<Block> blocks)
    {
        var table = new bool[blocks.Count];
        for (int id = 0; id < table.Length; id++)
            table[id] = blocks[id] is { } block && IsReedOrFern(block);
        return table;
    }

    /// <summary>
    /// A block whose solid part is the lower half of its cell: a full 1x1 footprint,
    /// standing on the cell floor, no taller than half a block.
    ///
    /// Deliberately geometric rather than a list of block codes. Terrain-slab mods are the
    /// reason this exists, but nothing here knows their names - the same rule catches a
    /// shallow snow layer, and it cannot be fooled by a mod choosing different codes.
    ///
    /// Collision comes first because it is what a player walks on, and falls back to the
    /// selection box for terrain a player can pass through. A block that answers neither is
    /// left as a full cube, which is exactly today's behaviour.
    /// </summary>
    static bool IsBottomHalfBlock(Block block)
    {
        Cuboidf[]? boxes = block.CollisionBoxes;
        if (boxes == null || boxes.Length == 0) boxes = block.SelectionBoxes;
        if (boxes == null || boxes.Length != 1) return false;

        Cuboidf box = boxes[0];
        const float Slack = 0.01f;

        // The whole cell horizontally: a fence post or a torch is not half a terrain block,
        // and drawing it as one would sink a whole column of distant ground.
        if (box.X1 > Slack || box.X2 < 1f - Slack) return false;
        if (box.Z1 > Slack || box.Z2 < 1f - Slack) return false;

        // Standing on the floor, and reaching no higher than the midpoint. A block sitting
        // in the upper half (an inverted slab) still reads as full: its TOP is where the
        // ground is, and that is the only surface the LOD draws.
        return box.Y1 <= Slack && box.Y2 <= 0.5f + Slack;
    }

    static readonly string[] BranchyLeavesPrefixes = { "leavesbranchystatic-", "leavesbranchy-" };

    /// <summary>
    /// The plain-leaves code a branchy-leaves block is drawn as at distance, or null for any
    /// other block. Vanilla's branchy and plain leaves share their variant names
    /// (<c>leavesbranchy-grown3-redwood</c> and <c>leaves-grown3-redwood</c>), so the twin is
    /// the same code with "leaves" in front. Whether that block exists is the caller's to ask.
    /// </summary>
    public static string? PlainLeavesCodeFor(string? path)
    {
        if (path == null) return null;
        foreach (string prefix in BranchyLeavesPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal) && path.Length > prefix.Length)
                return "leaves-" + path.Substring(prefix.Length);
        }
        return null;
    }

    static readonly string[] ThinGroundCoverPrefixes = { "flower", "fern", "tallfern" };

    static bool IsThinGroundCover(Block block)
    {
        string? path = block.Code?.Path;
        if (path == null) return false;

        foreach (string prefix in ThinGroundCoverPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
