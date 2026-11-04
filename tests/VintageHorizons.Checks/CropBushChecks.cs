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
        DenseLeaves(c);
        Wiring(c);
    }

    static void DenseLeaves(Check c)
    {
        const int size = 16;
        const int white = 0x00FFFFFF;
        var sheet = new int[size * size];
        Array.Fill(sheet, white);
        // The left half leaf, in two greens: the mirrored copy fills the right half.
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size / 2; x++)
                sheet[y * size + x] = unchecked((int)(y % 2 == 0 ? 0xFF206020 : 0xFF408040));

        c.True(LodBushTile.ComposeDenseLeaves(size, new int[size * size]) == null,
            "a sheet with no leaf makes no dense picture");
        c.True(LodBushTile.ComposeDenseLeaves(size, new int[8]) == null, "nor does a sheet of the wrong size");

        (int[] Pixels, int Mean)? dense = LodBushTile.ComposeDenseLeaves(size, sheet);
        c.True(dense != null, "a leaf sheet makes a dense picture");
        int[] picture = dense!.Value.Pixels;
        int leafy = picture.Count(pixel => ((pixel >> 24) & 0xFF) != 0);
        c.True(leafy > sheet.Count(pixel => ((pixel >> 24) & 0xFF) != 0),
            "it holds more leaf than the game's sheet");
        bool kept = true;
        for (int i = 0; i < sheet.Length; i++)
            if (((sheet[i] >> 24) & 0xFF) != 0 && picture[i] != sheet[i]) kept = false;
        c.True(kept, "and every leaf of the game's own is still where it was, unchanged");
        c.True(picture.Where(pixel => ((pixel >> 24) & 0xFF) != 0).All(pixel => ((pixel >> 24) & 0xFF) == 0xFF),
            "its leaves are solid: the cut-out test sees a leaf or a hole");
        c.True(picture.Where(pixel => ((pixel >> 24) & 0xFF) == 0)
                .All(pixel => (pixel & 0xFFFFFF) == (dense.Value.Mean & 0xFFFFFF)),
            "its clear pixels store its mean, as a bush's do");
        c.Eq(0x30, (dense.Value.Mean >> 16) & 0xFF,
            "and the copies are not darkened: the mean is the sheet's own two greens'");

        // dev.50: the dense picture is handed to the game as a PNG asset before it builds its atlas,
        // so it is packed beside its leaf picture on the same page (two-page atlases built none).
        c.Eq(0xAE426082u, LodDenseLeafAssets.Crc32(System.Text.Encoding.ASCII.GetBytes("IEND"), 0xFFFFFFFFu) ^ 0xFFFFFFFFu,
            "the PNG checksum is the standard one: an empty IEND chunk's CRC");
        byte[] png = LodDenseLeafAssets.EncodePng(size, size, picture);
        c.True(png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
                && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)) == size
                && png[24] == 8 && png[25] == 6,
            "the picture is written as an 8-bit RGBA PNG of its own size");
        int idatLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(33));
        using var inflate = new System.IO.Compression.ZLibStream(
            new MemoryStream(png, 41, idatLength), System.IO.Compression.CompressionMode.Decompress);
        var raw = new MemoryStream();
        inflate.CopyTo(raw);
        byte[] rows = raw.ToArray();
        bool same = rows.Length == size * (1 + size * 4);
        for (int y = 0; same && y < size; y++)
            for (int x = 0; same && x < size; x++)
            {
                int p = picture[y * size + x], at = y * (1 + size * 4) + 1 + x * 4;
                same = rows[at] == (byte)(p >> 16) && rows[at + 1] == (byte)(p >> 8)
                    && rows[at + 2] == (byte)p && rows[at + 3] == (byte)(p >> 24);
            }
        c.True(same, "and every pixel comes back out of it unchanged, alpha included");

        string root = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src");
        string assets = File.ReadAllText(Path.Combine(root, "Render", "LodDenseLeafAssets.cs"));
        string mod = File.ReadAllText(Path.Combine(root, "VintageHorizonsModSystem.cs"));
        c.True(assets.Contains("AccessTools.Method(typeof(BlockTextureAtlasManager), \"CollectTextures\")", StringComparison.Ordinal)
                && mod.Contains("public override void AssetsLoaded(ICoreAPI api) => denseLeafAssets?.MakePictures();", StringComparison.Ordinal)
                && mod.Contains("block.Textures.TryGetValue(LodDenseLeafAssets.TextureKey, out CompositeTexture? packedDense)", StringComparison.Ordinal),
            "pictures are made at asset load, named on leaf blocks before collection, and read back from their packed place");
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
        c.True(mod.Contains("blockTextures.DenseLeavesShown = v.DenseLeavesOn;", StringComparison.Ordinal)
                && registry.Contains("KeepDenseLeaves(slot, dense, resolved.DenseLeavesMean)", StringComparison.Ordinal),
            "Dense Leaves swaps a leaf slot's rows in the table, so it needs no remesh");
        c.True(body.Contains("if (vhFaceAxis == 5)", StringComparison.Ordinal)
                && body.Contains("1.0 - 0.6 * vhRagged * min(vhCorner.x, vhCorner.y)", StringComparison.Ordinal),
            "standing leaf tiles lose their corners, and the ragged cut fades out along an edge so "
                + "stacked tiles open no seam");
        c.True(cull.Contains("PaletteCrossedShapes = self.PaletteCrossedShapes", StringComparison.Ordinal),
            "the cave cull's rebuilt snapshot keeps how crops stand");
        c.True(body.Contains("texelFetch(blockTable, ivec2(vhBlockSlot, 1), 0).a > 1.5", StringComparison.Ordinal)
                && body.Contains("clamp(2.0 - vhSheetLod, 0.0, 1.0)", StringComparison.Ordinal),
            "only a sheet marked safe skips the fade reeds need (G313)");
        c.True(LodBlockPolicy.CropSheetNames.Contains("crop") && LodBlockPolicy.CropSheetNames.Contains("e"),
            "crops look for the sheets the game's own planes use");
    }
}
