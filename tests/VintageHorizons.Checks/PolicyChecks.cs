using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageHorizons.Checks;

/// <summary>
/// How a block is classified for LOD. Shared by both sides deliberately: a section
/// captured by a server and one captured by a client must agree about what counts as
/// terrain, or the same ground looks different depending on who saw it first.
/// </summary>
public static class PolicyChecks
{
    public static void Run(Check c)
    {
        Materials(c);
        GroundCover(c);
        FernTree(c);
        HalfBlocks(c);
        Degenerate(c);
        BranchyLeavesLookLikeLeaves(c);
    }

    /// <summary>
    /// Branchy leaves carry no colour map of their own - vanilla tints them inside their shape -
    /// so at distance they are drawn as the plain leaves of the same tree and variant.
    /// </summary>
    static void BranchyLeavesLookLikeLeaves(Check c)
    {
        c.Eq("leaves-grown3-redwood", LodBlockPolicy.PlainLeavesCodeFor("leavesbranchy-grown3-redwood"),
            "a branchy redwood leaf is drawn as the plain redwood leaf of the same variant");
        c.Eq("leaves-grown-birch", LodBlockPolicy.PlainLeavesCodeFor("leavesbranchystatic-grown-birch"),
            "the static birch variant too, not misread as branchy with a 'static' prefix");
        c.Eq(null, LodBlockPolicy.PlainLeavesCodeFor("leaves-grown-oak"), "plain leaves keep their own look");
        c.Eq(null, LodBlockPolicy.PlainLeavesCodeFor("leavesnarrow-grown-cypress-1"),
            "narrow leaves have a colour map of their own and keep it");
        c.Eq(null, LodBlockPolicy.PlainLeavesCodeFor("leavesbranchy-"), "a bare prefix names no twin");
        c.Eq(null, LodBlockPolicy.PlainLeavesCodeFor(null), "nor does a block with no code");
    }

    static void Materials(Check c)
    {
        c.Eq(LodPaletteEntry.FlagWater, Flags(EnumBlockMaterial.Water, "water-still-7"), "water is translucent");
        c.Eq(LodPaletteEntry.FlagWater, Flags(EnumBlockMaterial.Lava, "lava-still-7"), "lava uses the water path");
        c.Eq((byte)0, Flags(EnumBlockMaterial.Ice, "lakeice"),
            "lake ice is solid terrain rather than liquid-depth water");
        c.Eq((byte)0, Flags(EnumBlockMaterial.Ice, "glacierice"),
            "transparent glacier ice is solid terrain rather than liquid-depth water");
        c.Eq((byte)0, Flags(EnumBlockMaterial.Ice, "packedglacierice"),
            "opaque packed glacier ice is solid terrain rather than liquid-depth water");
        c.Eq(LodPaletteEntry.FlagSnowLayer, Flags(EnumBlockMaterial.Snow, "snowlayer-3"),
            "vanilla snow is retained as coverage rather than ordinary terrain");

        // Not terrain at all, so it never becomes geometry.
        c.Eq(LodPaletteEntry.FlagSkip, Flags(EnumBlockMaterial.Fire, "fire"), "fire is skipped");
        c.Eq(LodPaletteEntry.FlagSkip, Flags(EnumBlockMaterial.Meta, "meta-invisible"), "meta blocks are skipped");

        c.Eq((byte)0, Flags(EnumBlockMaterial.Stone, "rock-granite"), "stone is ordinary opaque terrain");
        c.Eq((byte)0, Flags(EnumBlockMaterial.Soil, "soil-medium-normal"), "soil is ordinary opaque terrain");
        c.Eq((byte)0, Flags(EnumBlockMaterial.Wood, "log-grown-pine-ud"), "wood is ordinary opaque terrain");
    }

    /// <summary>
    /// Sparse ground cover renders as a pale grey blob when drawn as a solid cube, because
    /// its texture averages toward its transparent pixels. Not all plants qualify: skipping
    /// every plant was tried and flattened the landscape, and dense cover like grass reads
    /// fine as solid colour.
    /// </summary>
    static void GroundCover(Check c)
    {
        c.Eq(LodPaletteEntry.FlagThin, Flags(EnumBlockMaterial.Plant, "flower-forgetmenot"), "flowers are thin");
        c.Eq(LodPaletteEntry.FlagThin, Flags(EnumBlockMaterial.Plant, "fern-normal"), "ferns are thin");
        c.Eq(LodPaletteEntry.FlagThin, Flags(EnumBlockMaterial.Plant, "tallfern-normal"), "tall ferns are thin");

        c.Eq((byte)0, Flags(EnumBlockMaterial.Plant, "tallgrass-tall"), "dense grass stays solid");
        c.Eq((byte)0, Flags(EnumBlockMaterial.Plant, "seaweed-top"), "unlisted plants stay solid");
    }

    /// <summary>
    /// "fern" is a prefix of "ferntree", and a ferntree is an actual tree. The material
    /// guard is the only thing that stops the prefix match from turning every ferntree
    /// trunk into a quarter-block mat - the prefix list alone would classify it as cover.
    /// </summary>
    static void FernTree(Check c)
    {
        c.Eq((byte)0, Flags(EnumBlockMaterial.Wood, "ferntree-grown-medium"),
            "a ferntree is opaque wood, not ground cover");
        c.Eq((byte)0, Flags(EnumBlockMaterial.Wood, "fern-something-wooden"),
            "the material guard, not the prefix list, is what protects wood");

        // And the guard is genuinely load-bearing: the same code path with material Plant
        // does match the prefix. If someone removes the material check thinking the prefix
        // list is enough, this pair is what shows the difference.
        c.Eq(LodPaletteEntry.FlagThin, Flags(EnumBlockMaterial.Plant, "ferntree-grown-medium"),
            "the same code as a plant would be treated as cover");
    }

    /// <summary>
    /// Terrain-slab mods replace the top block of a smoothed step with a half-height block.
    /// Nothing here knows their names: the rule is the block's own shape, so it catches a
    /// slab whatever a mod calls it and catches a shallow snow layer for free.
    ///
    /// The failure that matters is the false positive. Anything narrower than its cell -
    /// a fence post, a torch, a plant stem - would sink a whole column of distant ground
    /// half a block if it were treated as terrain half a block tall.
    /// </summary>
    static void HalfBlocks(Check c)
    {
        c.Eq(LodPaletteEntry.FlagHalf, Shaped(EnumBlockMaterial.Soil, "soil-low-normal", 0f, 0.5f),
            "a bottom half-slab of soil is half a block tall");
        c.Eq(LodPaletteEntry.FlagHalf, Shaped(EnumBlockMaterial.Stone, "rock-granite", 0f, 0.5f),
            "so is a slab of rock");
        c.Eq(LodPaletteEntry.FlagSnowLayer, Shaped(EnumBlockMaterial.Snow, "snowlayer-3", 0f, 0.375f),
            "a shallow snow layer is appearance rather than half-block terrain");

        c.Eq((byte)0, Shaped(EnumBlockMaterial.Soil, "soil-low-normal", 0f, 1f),
            "a full block is unchanged");
        c.Eq(LodPaletteEntry.FlagSnowLayer, Shaped(EnumBlockMaterial.Snow, "snowlayer-6", 0f, 0.75f),
            "deep snow is the same coverage material rather than a full terrain block");
        c.Eq((byte)0, Shaped(EnumBlockMaterial.Stone, "rock-granite", 0.5f, 1f),
            "a TOP slab keeps its full height: its top face is where the ground is");

        // Narrow things. Each of these is half a block tall or less and none of them is
        // half a block of terrain.
        c.Eq((byte)0, Boxed(EnumBlockMaterial.Wood, "fence-section", 0.4f, 0f, 0.4f, 0.6f, 0.5f, 0.6f),
            "a post is not a slab, however short it is");
        c.Eq((byte)0, Boxed(EnumBlockMaterial.Stone, "rock-granite", 0f, 0f, 0f, 0.5f, 0.5f, 1f),
            "half a block wide is not half a block tall");

        // Water keeps its own classification: it is answered before the shape test, and a
        // half-height water surface is not something this mod draws.
        c.Eq(LodPaletteEntry.FlagWater, Shaped(EnumBlockMaterial.Water, "water-still-3", 0f, 0.5f),
            "a shallow water block is still water, not a slab");

        var covered = new Block
        {
            BlockMaterial = EnumBlockMaterial.Soil,
            Code = new AssetLocation("game", "soil-medium-snow"),
            Variant = new Vintagestory.API.Util.RelaxedReadOnlyDictionary<string, string>(
                new Dictionary<string, string> { ["cover"] = "snow" }),
        };
        c.Eq(LodPaletteEntry.FlagSnowCovered, LodBlockPolicy.FlagsFor(covered),
            "vanilla's cover variant marks an in-place snow-covered surface");
    }

    static void Degenerate(Check c)
    {
        // Code is null on a block that failed to resolve. Classification must survive it,
        // because a modded install is exactly where this happens and exactly where the
        // purple-block reports come from.
        c.NoThrow(() => LodBlockPolicy.FlagsFor(new Block { BlockMaterial = EnumBlockMaterial.Plant }),
            "a block with no code does not throw");
        c.Eq((byte)0, LodBlockPolicy.FlagsFor(new Block { BlockMaterial = EnumBlockMaterial.Plant }),
            "a plant with no code stays solid rather than guessing");
    }

    static byte Flags(EnumBlockMaterial material, string path) =>
        LodBlockPolicy.FlagsFor(new Block
        {
            BlockMaterial = material,
            Code = new AssetLocation("game", path),
        });

    /// <summary>A full-footprint block standing between two heights.</summary>
    static byte Shaped(EnumBlockMaterial material, string path, float y1, float y2) =>
        Boxed(material, path, 0f, y1, 0f, 1f, y2, 1f);

    static byte Boxed(EnumBlockMaterial material, string path,
        float x1, float y1, float z1, float x2, float y2, float z2) =>
        LodBlockPolicy.FlagsFor(new Block
        {
            BlockMaterial = material,
            Code = new AssetLocation("game", path),
            CollisionBoxes = new[] { new Cuboidf(x1, y1, z1, x2, y2, z2) },
        });
}
