using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace VintageHorizons.Checks;

/// <summary>
/// Crops on crossed planes with their own pictures, and berry bushes dressed from their own
/// leaf sheets. Each guards a choice measured on the game's art: a crop's clear pixels store its
/// mean, and one bush sheet is 5 to 7% leaf.
/// </summary>
public static class CropBushChecks
{
    public static void Run(Check c)
    {
        CropsAreCrossed(c);
        BushTile(c);
        Wiring(c);
    }

    static void CropsAreCrossed(Check c)
    {
        var crop = new BlockCrop();
        c.True(LodBlockPolicy.IsCrossedPlant(crop), "a crop draws as crossed planes");
        c.False(LodBlockPolicy.ReedTable(new Block?[] { crop })[0],
            "but capture does not look above the rain map for one: crops stop rain, and are captured");
        c.Eq((byte)LodBlockPolicy.CrossedQuartersPerBlock, LodBlockPolicy.CrossedShapeOf(crop),
            "a crop stands a block tall");
        c.Eq(LodBlockPolicy.CrossedShapeByFlags, LodBlockPolicy.CrossedShapeOf(new BlockReeds()),
            "a reed stands by its flags, as it always did");
        c.Eq(LodBlockPolicy.CrossedShapeByFlags, LodBlockPolicy.CrossedShapeOf(null),
            "and an unknown block by its flags");
    }

    static void BushTile(Check c)
    {
        const int size = 16;
        const int leaf = unchecked((int)0xFF999999);
        const int white = 0x00FFFFFF;

        var sparse = new int[size * size];
        Array.Fill(sparse, white);
        for (int i = 0; i < 24; i++) sparse[i * 7 % sparse.Length] = leaf;

        c.True(LodBushTile.ComposePlant(Array.Empty<(int, int, int[])>()) == null, "no sheets, no picture");
        var clear = new int[size * size];
        Array.Fill(clear, white);
        c.True(LodBushTile.ComposePlant(new[] { (size, size, clear) }) == null,
            "a sheet with no leaf makes no picture rather than a flat one");
        c.True(LodBushTile.ComposePlant(new[] { (size, size, sparse), (8, 8, new int[64]) }) != null,
            "a sheet of another size is left out");

        (float R, float G, float B) stored = LodBlockTextureRegistry.Channels(
            LodBushTile.ToStoredColor(unchecked((int)0xFF102030)));
        c.Near(0x10 / 255.0, stored.R, 0.001, "a tile's mean reaches the table with red as red");
        c.Near(0x20 / 255.0, stored.G, 0.001, "green as green");
        c.Near(0x30 / 255.0, stored.B, 0.001, "and blue as blue");

        (int[] Pixels, int Mean)? plant = LodBushTile.ComposePlant(new[] { (size, size, sparse) });
        c.True(plant != null, "sparse sheets make a plant picture");
        int[] picture = plant!.Value.Pixels;
        c.True(picture.Any(pixel => ((pixel >> 24) & 0xFF) == 0),
            "a plant picture stays see-through");
        c.True(picture.Where(pixel => ((pixel >> 24) & 0xFF) == 0)
                .All(pixel => (pixel & 0xFFFFFF) == (plant.Value.Mean & 0xFFFFFF)),
            "and its clear pixels store its mean, so mips stir no white in (G313)");
        c.True(picture.Count(pixel => ((pixel >> 24) & 0xFF) != 0) <= sparse.Count(pixel => pixel == leaf) * 2,
            "it is only upright and mirrored: a plant is never turned over");
    }

    static void Wiring(Check c)
    {
        string root = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons");
        string registry = File.ReadAllText(Path.Combine(root, "src", "Render", "LodBlockTextureRegistry.cs"));
        string mod = File.ReadAllText(Path.Combine(root, "src", "VintageHorizonsModSystem.cs"));
        string cull = File.ReadAllText(Path.Combine(root, "src", "Lod", "LodCaveCull.cs"));
        string body = File.ReadAllText(Path.Combine(root, "assets", "dodenslandscapes", "shaderincludes", "lodterrainbody.fsh"));
        c.True(registry.Contains("block.EntityClass != null && !LodBlockPolicy.IsBerryBush(block)", StringComparison.Ordinal),
            "a bush may hold a slot; a chiselled block still may not (G46)");
        c.True(mod.Contains("block.EntityClass != null && !LodBlockPolicy.IsBerryBush(block)", StringComparison.Ordinal),
            "a bush gives the table its colour: zero there drew it black (owner screenshots)");
        string tints = File.ReadAllText(Path.Combine(root, "src", "Render", "LodTintRegistry.cs"));
        c.True(tints.Contains("block is not Vintagestory.GameContent.BlockCrop", StringComparison.Ordinal),
            "a crop's art is full colour, so it takes no plant tint");
        c.True(mod.Contains("RegenMipMaps(page)", StringComparison.Ordinal),
            "a tile inserted at runtime has its mips rebuilt: the band samples below the top level");
        c.True(cull.Contains("PaletteCrossedShapes = self.PaletteCrossedShapes", StringComparison.Ordinal),
            "the cave cull's rebuilt snapshot keeps how crops stand");
        c.True(body.Contains("texelFetch(blockTable, ivec2(vhBlockSlot, 1), 0).a > 1.5", StringComparison.Ordinal)
                && body.Contains("clamp(2.0 - vhSheetLod, 0.0, 1.0)", StringComparison.Ordinal),
            "only a sheet marked safe skips the fade reeds need (G313)");
        c.True(LodBlockPolicy.CropSheetNames.Contains("crop") && LodBlockPolicy.CropSheetNames.Contains("e"),
            "crops look for the sheets the game's own planes use");
    }
}
