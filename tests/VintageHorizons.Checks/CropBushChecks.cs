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
        c.Eq(LodBlockPolicy.CrossedShapeCrop, LodBlockPolicy.CrossedShapeOf(crop),
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

        (int[] Pixels, int Mean)? tile = LodBushTile.Compose(new[] { (size, size, sparse) });
        c.True(tile != null, "one sheet makes a tile");
        int[] pixels = tile!.Value.Pixels;
        c.True(pixels.All(pixel => ((pixel >> 24) & 0xFF) == 0xFF),
            "the tile is solid: no clear texel for a mip to stir white into");
        c.False(pixels.Any(pixel => (pixel & 0xFF) > 0x99),
            "and no clear pixel's stored white reaches it");
        c.True(pixels.Distinct().Count() > 2, "leaves and gaps differ, so there is a pattern");
        int covered = pixels.Count(pixel => (pixel & 0xFF) >= 0x99 * 3 / 4);
        c.True(covered > sparse.Count(pixel => pixel == leaf),
            "orientations cover more of the tile than the one sheet did");
        double total = pixels.Sum(pixel => pixel & 0xFF);
        c.Near(total / pixels.Length, tile.Value.Mean & 0xFF, 1.0,
            "the mean is the tile's own, which the shader divides by");

        c.True(LodBushTile.Compose(Array.Empty<(int, int, int[])>()) == null, "no sheets, no tile");
        var clear = new int[size * size];
        Array.Fill(clear, white);
        c.True(LodBushTile.Compose(new[] { (size, size, clear) }) == null,
            "a sheet with no leaf makes no tile rather than a flat one");
        c.True(LodBushTile.Compose(new[] { (size, size, sparse), (8, 8, new int[64]) }) != null,
            "a sheet of another size is left out");
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
        c.True(mod.Contains("RegenMipMaps(page)", StringComparison.Ordinal),
            "a tile inserted at runtime has its mips rebuilt: the band samples below the top level");
        c.True(cull.Contains("PaletteCrossedShapes = self.PaletteCrossedShapes", StringComparison.Ordinal),
            "the cave cull's rebuilt snapshot keeps how crops stand");
        c.True(body.Contains("texelFetch(blockTable, ivec2(vhBlockSlot, 1), 0).a > 1.5", StringComparison.Ordinal)
                && body.Contains("clamp(2.0 - vhSheetLod, 0.0, 1.0)", StringComparison.Ordinal),
            "only a sheet marked safe skips the fade reeds need (G313)");
        foreach (string name in new[] { "crop", "e", "s" })
            c.True(mod.Contains("\"" + name + "\"", StringComparison.Ordinal), $"crops look for the {name} sheet");
    }
}
