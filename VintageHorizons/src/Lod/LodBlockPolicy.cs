using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

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
        if (block.BlockMaterial is EnumBlockMaterial.Water or EnumBlockMaterial.Lava or EnumBlockMaterial.Ice)
        {
            return LodPaletteEntry.FlagWater;
        }

        // Sparse, mostly-transparent ground cover. As solid LOD cubes these come out as
        // pale grey blobs, because their textures average toward the transparent pixels.
        // Not all of EnumBlockMaterial.Plant: skipping every plant was tried and
        // flattened the landscape, and dense cover like grass reads fine as solid colour.
        // The Plant guard also keeps ferntree (material Wood, an actual tree) opaque.
        if (block.BlockMaterial == EnumBlockMaterial.Plant && IsThinGroundCover(block))
        {
            return LodPaletteEntry.FlagThin;
        }

        // Not terrain at all, so it never becomes geometry.
        if (block.BlockMaterial is EnumBlockMaterial.Fire or EnumBlockMaterial.Meta)
        {
            return LodPaletteEntry.FlagSkip;
        }

        // Terrain that only fills the bottom half of its cell. Runs are stored in whole
        // blocks, so this is the only place the difference can be recorded, and the mesher
        // lowers the surface by half a block wherever it is set.
        if (IsBottomHalfBlock(block)) return LodPaletteEntry.FlagHalf;

        return 0;
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
