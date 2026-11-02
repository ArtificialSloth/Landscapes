using System.IO.Compression;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace VintageHorizons.Checks;

/// <summary>
/// The Plants tab's promises: leaves in each shape cost what their rule says and cover nothing,
/// a plant switched off draws its cheaper form, one switch governs all of it, and a fruit tree's
/// variant survives the trip through storage while older readers can still read the row.
/// </summary>
public static class PlantDetailChecks
{
    const int Leaf = 1;
    const int Stone = 2;
    static readonly bool[] Leaves = { false, true, false };

    public static void Run(Check c)
    {
        Switches(c);
        LeafShapes(c);
        LeavesCoverNothing(c);
        BuriedLeaves(c);
        VineSheets(c);
        GrassUnderTrees(c);
        ForageMarkers(c);
        FlowerSheets(c);
        BushSheets(c);
        UltraPreset(c);
        FruitWoodInTheCanopy(c);
        SwitchedOffPlants(c);
        VariantsInThePalette(c);
        VariantsInStorage(c);
        FruitTreeParts(c);
        Wiring(c);
    }

    static void Switches(Check c)
    {
        LodVisualSettings on = LodVisualSettings.Default with { LeafShape = LodLeafShape.Planes };
        c.True(on.PlantTexturesOn && on.ReedShapesOn && on.CropShapesOn && on.BushTexturesOn
                && on.FruitTreeColorsOn && on.LeafShapeOn == LodLeafShape.Planes,
            "every plant feature is on by default");

        LodVisualSettings off = on with { PlantDetail = false };
        c.False(off.PlantTexturesOn || off.ReedShapesOn || off.CropShapesOn || off.BushTexturesOn
                || off.FruitTreeColorsOn,
            "Plant Detail off turns every one of them off");
        c.Eq(LodLeafShape.Cubes, off.LeafShapeOn, "and leaves go back to cubes");
        c.True(off.PlantTextures && off.LeafShape == LodLeafShape.Planes,
            "keeping the switches' own positions for when it comes back on");

        c.Eq(LodLeafShape.Cubes, (on with { PlantTextures = false }).LeafShapeOn,
            "a leaf plane has nothing to cut it out with Plant Textures off");

        c.False(LodVisualSettings.Performance.PlantDetail, "Performance gives plants up");
        c.True(LodVisualSettings.Quality.PlantDetail && LodVisualSettings.Balanced.PlantDetail,
            "Quality and Balanced keep them");
        c.Eq(LodLeafShape.ClustersWithTops, LodVisualSettings.Default.LeafShape,
            "a new player gets the shape the owner chose");
        c.Eq(LodLeafShape.ClustersWithTops, new VintageHorizonsConfig().LeafShape,
            "a fresh config says the same");
        c.Eq(LodLeafShape.Cubes, LodVisualSettings.Performance.LeafShapeOn, "and Performance still draws cubes");
    }

    /// <summary>A tree crown: a 3x3 block of leaves four high, whose middle column is walled in.</summary>
    static LodSection Crown()
    {
        var columns = new List<(int, ulong[])>();
        for (int z = 10; z < 13; z++)
        {
            for (int x = 10; x < 13; x++)
                columns.Add((LodSection.ColumnIndex(x, z), new[] { LodSection.PackRun(0, 104, 100) }));
        }
        LodSection section = Fixtures.Section(columns.ToArray());
        section.Palette.Clear();
        section.Palette.Add(new LodPaletteEntry { BlockId = Leaf, Color = 0x336633, TextureSlot = 7 });
        return section;
    }

    static MeshResult Mesh(LodSection section, LodLeafShape shape)
    {
        bool[]? table = shape == LodLeafShape.Cubes ? null : Leaves;
        return LodMesher.BuildMesh(new MeshJob
        {
            Key = 0,
            Self = SectionSnapshot.Of(section, crossedShapes: _ => LodBlockPolicy.CrossedShapeByFlags,
                leafPlanes: table),
            Neighbors = new SectionSnapshot?[4],
            EmitTextureSlots = true,
            LeafShape = shape,
        });
    }

    static int Planes(MeshResult mesh)
    {
        int planes = 0;
        for (int q = 0; q < mesh.PackedOpaqueQuadCount; q++)
        {
            if (LodPackedQuadFormat.FaceOf(mesh.PackedOpaqueQuads, q) >= LodPackedFace.DiagonalRising) planes++;
        }
        return planes;
    }

    static void LeafShapes(Check c)
    {
        LodSection crown = Crown();
        c.Eq(0, Planes(Mesh(crown, LodLeafShape.Cubes)), "cubes draw no planes");

        // Eight outer columns merge up their height. The middle column's sides are all leaves, so
        // only its top block and its bottom block can be seen: two stretches, not one.
        MeshResult planes = Mesh(crown, LodLeafShape.Planes);
        c.Eq((8 + 2) * 2, Planes(planes), "two planes for each unbroken visible stretch of a column");
        c.Eq(planes.PackedOpaqueQuadCount, Planes(planes), "and nothing else");

        MeshResult tops = Mesh(crown, LodLeafShape.PlanesWithTops);
        c.Eq(Planes(planes) + 1, Planes(tops), "the canopy's tops merge into one flat plane");
        c.Eq(tops.PackedOpaqueQuadCount, Planes(tops), "which is a plane, not a solid top");

        // The crown spans x and z 10..12, so two-block cells 5 and 6 on each axis and heights
        // 50 and 51: four stacks of cells.
        MeshResult clusters = Mesh(crown, LodLeafShape.Clusters);
        c.Eq(4 * 2, Planes(clusters), "cells stack, and each stack is two planes");
        c.Eq(4 * 3, Planes(Mesh(crown, LodLeafShape.ClustersWithTops)), "tops add a flat plane to each stack");
        c.True(clusters.PackedOpaqueQuadCount < planes.PackedOpaqueQuadCount,
            "clusters are the cheapest in faces");
    }

    static void LeavesCoverNothing(Check c)
    {
        // A stone pillar beside a leaf block: as cubes the leaf hides its side, as planes it must not.
        LodSection section = Fixtures.Section(
            (LodSection.ColumnIndex(10, 10), new[] { LodSection.PackRun(0, 104, 100) }),
            (LodSection.ColumnIndex(11, 10), new[] { LodSection.PackRun(1, 104, 100) }));
        section.Palette.Clear();
        section.Palette.Add(new LodPaletteEntry { BlockId = Leaf, Color = 0x336633, TextureSlot = 7 });
        section.Palette.Add(new LodPaletteEntry { BlockId = Stone, Color = 0x777777 });

        int cubes = Mesh(section, LodLeafShape.Cubes).PackedOpaqueQuadCount;
        MeshResult planes = Mesh(section, LodLeafShape.Planes);
        // Stone's top, bottom and four walls, and the leaf's two planes.
        c.Eq(6 + 2, planes.PackedOpaqueQuadCount, "the stone keeps the wall a cube of leaves would hide");
        c.True(cubes > 0, "and a cube of leaves is still drawn the old way");

        section.Palette[0] = section.Palette[0] with { TextureSlot = 0 };
        c.Eq(0, Planes(Mesh(section, LodLeafShape.Planes)),
            "a leaf with no texture slot has nothing to cut a plane out with, and stays a cube");
    }

    static void BuriedLeaves(Check c)
    {
        // A lone leaf, and a leaf ringed by others at the reach and roofed over.
        LodSection lone = Fixtures.Section(
            (LodSection.ColumnIndex(10, 10), new[] { LodSection.PackRun(0, 104, 100) }));
        lone.Palette.Clear();
        lone.Palette.Add(new LodPaletteEntry { BlockId = Leaf, Color = 0x336633, TextureSlot = 7 });
        c.Eq(0, HighestShade(Mesh(lone, LodLeafShape.Planes)), "a leaf with nothing around it is not shaded");

        var columns = new List<(int, ulong[])>();
        for (int z = 6; z <= 14; z++)
        {
            for (int x = 6; x <= 14; x++)
            {
                ulong[] runs = x == 10 && z == 10
                    ? new[] { LodSection.PackRun(0, 120, 110), LodSection.PackRun(0, 104, 100) }
                    : new[] { LodSection.PackRun(0, 104, 100) };
                columns.Add((LodSection.ColumnIndex(x, z), runs));
            }
        }
        LodSection ringed = Fixtures.Section(columns.ToArray());
        ringed.Palette.Clear();
        ringed.Palette.Add(new LodPaletteEntry { BlockId = Leaf, Color = 0x336633, TextureSlot = 7 });
        c.True(HighestShade(Mesh(ringed, LodLeafShape.Clusters)) >= 8,
            "a leaf deep in a crown is, and the slot beside the shade still reads back");
        MeshResult mesh = Mesh(ringed, LodLeafShape.Clusters);
        c.Eq(7, LodPackedQuadFormat.TextureSlot(mesh.PackedOpaqueQuads[2]), "the shade leaves the slot alone");
        c.True(HighestShade(mesh) <= LodPackedQuadFormat.MaximumLeafShade, "and fits its four bits");
        c.Eq(0, HighestShade(Mesh(ringed, LodLeafShape.Cubes)), "cubes carry no shade at all");
    }

    static void VineSheets(Check c)
    {
        c.Eq(LodBlockPolicy.CrossedShapeVineNorth, LodBlockPolicy.VineShapeOf("wildvine-section-north"),
            "a vine's code gives the way it faces");
        c.Eq(LodBlockPolicy.CrossedShapeVineWest, LodBlockPolicy.VineShapeOf("wildvine-tropical-end-west"),
            "whatever its other parts");
        c.Eq(LodBlockPolicy.CrossedShapeAsMat, LodBlockPolicy.VineShapeOf("wildvine"),
            "and one with no direction is a flat patch");

        LodSection section = Fixtures.Section(
            (LodSection.ColumnIndex(10, 10), new[] { LodSection.PackRun(0, 110, 104) }));
        section.Palette.Clear();
        section.Palette.Add(new LodPaletteEntry
        {
            BlockId = Leaf, Color = 0x336633, Flags = LodPaletteEntry.FlagCrossed, TextureSlot = 7,
        });

        MeshResult Build(byte shape, long key = 0) => LodMesher.BuildMesh(new MeshJob
        {
            Key = key,
            Self = SectionSnapshot.Of(section, crossedShapes: _ => shape),
            Neighbors = new SectionSnapshot?[4],
            EmitTextureSlots = true,
        });

        (int X0, int X1, int Z0, int Z1) Span(MeshResult mesh)
        {
            uint geometry = mesh.PackedOpaqueQuads[0];
            return ((int)(geometry & 127u), (int)((geometry >> 7) & 127u),
                (int)((geometry >> 14) & 127u), (int)((geometry >> 21) & 127u));
        }

        MeshResult north = Build(LodBlockPolicy.CrossedShapeVineNorth);
        c.Eq(1, north.PackedOpaqueQuadCount, "a run of vine is one sheet, however long");
        c.Eq((10, 11, 11, 11), Span(north), "a north-facing vine hangs on its cell's south edge");
        c.Eq((10, 11, 10, 10), Span(Build(LodBlockPolicy.CrossedShapeVineSouth)), "south on the north edge");
        c.Eq((10, 10, 10, 11), Span(Build(LodBlockPolicy.CrossedShapeVineEast)), "east on the west edge");
        c.Eq((11, 11, 10, 11), Span(Build(LodBlockPolicy.CrossedShapeVineWest)), "and west on the east edge");
        c.Eq(7, LodPackedQuadFormat.TextureSlot(north.PackedOpaqueQuads[2]), "it wears its own sheet");

        c.Eq(0, Planes(Build(LodBlockPolicy.CrossedShapeAsMat)), "switched off it draws no sheet");
        c.Eq(0, Build(LodBlockPolicy.CrossedShapeVineNorth, LodWorld.SectionKey(1, 0, 0)).PackedOpaqueQuadCount,
            "and past level 0 a vine draws nothing");

        LodVisualSettings on = LodVisualSettings.Default;
        c.True(on.VineShapesOn, "vine sheets are on by default");
        c.False((on with { PlantTextures = false }).VineShapesOn, "a sheet needs Plant Textures");
        c.False((on with { PlantDetail = false }).VineShapesOn, "and Plant Detail governs it");
    }

    static Block Named(string path, EnumBlockMaterial material) => new()
    {
        Code = new AssetLocation("game", path),
        BlockMaterial = material,
    };

    static void ForageMarkers(Check c)
    {
        byte marker = (byte)(LodPaletteEntry.FlagThin | LodPaletteEntry.FlagCrossed);
        c.Eq(marker, LodBlockPolicy.FlagsFor(Named("mushroom-flyagaric-normal", EnumBlockMaterial.Plant)),
            "a mushroom is a marker");
        c.Eq(marker, LodBlockPolicy.FlagsFor(Named("looseflints-flint-free", EnumBlockMaterial.Stone)),
            "so is loose flint");
        c.Eq(marker, LodBlockPolicy.FlagsFor(Named("looseores-nativecopper-granite-free", EnumBlockMaterial.Ore)),
            "and loose ore");
        c.Eq(LodPaletteEntry.FlagSkip, LodBlockPolicy.FlagsFor(Named("loosestones-granite-free", EnumBlockMaterial.Stone)),
            "loose stones are not drawn at all");
        c.Eq(LodBlockPolicy.CrossedShapeMushroom,
            LodBlockPolicy.CrossedShapeOf(Named("mushroom-flyagaric-normal", EnumBlockMaterial.Plant)),
            "a mushroom stands half a block");
        c.Eq(LodBlockPolicy.CrossedShapeLooseItem,
            LodBlockPolicy.CrossedShapeOf(Named("looseflints-flint-free", EnumBlockMaterial.Stone)),
            "and flint a quarter");

        Block[] registry =
        {
            Named("air", EnumBlockMaterial.Air),
            Named("mushroom-flyagaric-normal", EnumBlockMaterial.Plant),
            Named("looseores-nativecopper-granite-free", EnumBlockMaterial.Ore),
            Named("loosestones-granite-free", EnumBlockMaterial.Stone),
            Named("log-resin-pine-ud", EnumBlockMaterial.Wood),
            Named("log-resinharvested-pine-ud", EnumBlockMaterial.Wood),
        };
        bool[] above = LodBlockPolicy.ReedTable(registry);
        c.True(above[1] && above[2], "capture looks above the rain map for a mushroom and for ore");
        c.False(above[3], "but not for the stones nobody wants drawn");
        bool[] resin = LodBlockPolicy.ResinTable(registry);
        c.True(resin[4], "a log holding resin is marked");
        c.False(resin[5], "a harvested one is not");

        LodSection ground = Fixtures.Section(
            (LodSection.ColumnIndex(10, 12), new[] { LodSection.PackRun(0, 101, 100) }));
        ground.Palette.Clear();
        ground.Palette.Add(new LodPaletteEntry { BlockId = 1, Color = 0x2244AA, Flags = marker });
        MeshResult Build(byte shape) => LodMesher.BuildMesh(new MeshJob
        {
            Key = 0,
            Self = SectionSnapshot.Of(ground, crossedShapes: _ => shape),
            Neighbors = new SectionSnapshot?[4],
        });

        MeshResult mushroom = Build(LodBlockPolicy.CrossedShapeMushroom);
        c.Eq(1, mushroom.PackedOpaqueQuadCount, "a marker is one quad");
        uint geometry = mushroom.PackedOpaqueQuads[0];
        c.True((geometry & 127u) == 10u && ((geometry >> 7) & 127u) == 10u
                && ((geometry >> 14) & 127u) == 12u && ((geometry >> 21) & 127u) == 12u,
            "with no footprint, which is how the vertex stage knows it");
        c.Eq((mushroom.PackedOpaqueQuads[1] >> 16) * 0.25f, 100.5f, "half a block tall from the ground");
        c.Eq(0x2244AAu, mushroom.PackedOpaqueQuads[2] & 0xFFFFFFu, "in the block's own colour");
        c.True((mushroom.PackedOpaqueQuads[2] >> 24) < LodTintRegistry.MaxSlots, "and opaque");
        c.Eq(0, Planes(Build(LodBlockPolicy.CrossedShapeAsMat)), "switched off it is the flat patch");

        LodSection log = Fixtures.Section(
            (LodSection.ColumnIndex(10, 10), new[] { LodSection.PackRun(0, 103, 100) }));
        log.Palette.Clear();
        log.Palette.Add(new LodPaletteEntry { BlockId = 2, Color = 0x223344 });
        int Markers(bool[]? table, long key = 0)
        {
            MeshResult mesh = LodMesher.BuildMesh(new MeshJob
            {
                Key = key,
                Self = SectionSnapshot.Of(log, resinLogs: table),
                Neighbors = new SectionSnapshot?[4],
            });
            return Planes(mesh);
        }
        bool[] logs = { false, false, true };
        c.Eq(3, Markers(logs), "every block of a resin log wears a marker");
        c.Eq(0, Markers(null), "with the switch off the table is not passed, and it is a plain log");
        c.Eq(0, Markers(logs, LodWorld.SectionKey(1, 0, 0)), "and past level 0 there are none");

        c.False((LodVisualSettings.Default with { PlantDetail = false }).ForageMarkersOn,
            "Plant Detail governs the markers");
        c.True((LodVisualSettings.Default with { PlantTextures = false }).ForageMarkersOn,
            "which wear no picture, so need no Plant Textures");
    }

    static void FlowerSheets(Check c)
    {
        Block flower = Named("flower-catmint-free", EnumBlockMaterial.Plant);
        c.Eq((byte)(LodPaletteEntry.FlagThin | LodPaletteEntry.FlagCrossed), LodBlockPolicy.FlagsFor(flower),
            "a flower is thin past level 0 and may stand at it");
        c.Eq(LodBlockPolicy.CrossedShapeFlower, LodBlockPolicy.CrossedShapeOf(flower), "as a sheet");
        c.False(LodBlockPolicy.IsFlower(Named("flowerpot-burned", EnumBlockMaterial.Ceramic)), "a flower pot is not one");

        LodSection ground = Fixtures.Section(
            (LodSection.ColumnIndex(10, 12), new[] { LodSection.PackRun(0, 101, 100) }));
        ground.Palette.Clear();
        ground.Palette.Add(new LodPaletteEntry
        {
            BlockId = 1, Color = 0x2244AA, TextureSlot = 9,
            Flags = (byte)(LodPaletteEntry.FlagThin | LodPaletteEntry.FlagCrossed),
        });
        MeshResult Build(byte shape) => LodMesher.BuildMesh(new MeshJob
        {
            Key = 0,
            Self = SectionSnapshot.Of(ground, crossedShapes: _ => shape),
            Neighbors = new SectionSnapshot?[4],
            EmitTextureSlots = true,
        });

        MeshResult sheet = Build(LodBlockPolicy.CrossedShapeFlower);
        c.Eq(1, sheet.PackedOpaqueQuadCount, "one quad, the same as the patch it replaces");
        c.Eq(9, LodPackedQuadFormat.TextureSlot(sheet.PackedOpaqueQuads[2]), "wearing the flower's picture");
        c.Eq(101f, (sheet.PackedOpaqueQuads[1] >> 16) * 0.25f, "a block tall");
        c.Eq(0, Planes(Build(LodBlockPolicy.CrossedShapeAsMat)), "switched off it is the patch again");

        ground.Palette[0] = ground.Palette[0] with { TextureSlot = 0 };
        c.Eq(0, Planes(Build(LodBlockPolicy.CrossedShapeFlower)), "and with no picture to wear, so is it then");
    }

    static void BushSheets(Check c)
    {
        Block[] registry =
        {
            Named("air", EnumBlockMaterial.Air),
            Named("fruitingbush-wild-blackberry-free", EnumBlockMaterial.Plant),
            Named("fruitingbush-wild-blackberry-snow", EnumBlockMaterial.Snow),
        };
        bool[] bushes = LodBlockPolicy.BushTable(registry);
        c.True(bushes[1], "a bush stands as sheets");
        c.False(bushes[2], "and a snowed-over one does not");

        // A bush with a stone pillar beside it, and one at the far corner of the section.
        int last = LodSection.GridSize - 1;
        LodSection section = Fixtures.Section(
            (LodSection.ColumnIndex(10, 10), new[] { LodSection.PackRun(0, 101, 100) }),
            (LodSection.ColumnIndex(11, 10), new[] { LodSection.PackRun(1, 104, 100) }),
            (LodSection.ColumnIndex(last, last), new[] { LodSection.PackRun(0, 101, 100) }));
        section.Palette.Clear();
        section.Palette.Add(new LodPaletteEntry { BlockId = 1, Color = 0x336633, TextureSlot = 7 });
        section.Palette.Add(new LodPaletteEntry { BlockId = Stone, Color = 0x777777 });
        bool[] table = { false, true, false };

        MeshResult Build(bool[]? sheets) => LodMesher.BuildMesh(new MeshJob
        {
            Key = 0,
            Self = SectionSnapshot.Of(section, crossedShapes: _ => LodBlockPolicy.CrossedShapeByFlags,
                bushSheets: sheets),
            Neighbors = new SectionSnapshot?[4],
            EmitTextureSlots = true,
        });

        MeshResult sheets = Build(table);
        c.Eq(3 * 2, Planes(sheets), "two crossing planes and one lying through them, for each bush");
        c.Eq(0, Planes(Build(null)), "and with the switch off, a block");

        int widest = 0;
        for (int q = 0; q < sheets.PackedOpaqueQuadCount; q++)
        {
            if (LodPackedQuadFormat.FaceOf(sheets.PackedOpaqueQuads, q) < LodPackedFace.DiagonalRising) continue;
            uint geometry = sheets.PackedOpaqueQuads[q * LodPackedQuadFormat.WordsPerQuad];
            widest = Math.Max(widest, (int)((geometry >> 7) & 127u) - (int)(geometry & 127u));
            c.True(((geometry >> 7) & 127u) <= LodSection.GridSize, "and stays inside its section");
        }
        c.Eq(2, widest, "two blocks across, as the game draws it");
        float tallest = 0f;
        for (int q = 0; q < sheets.PackedOpaqueQuadCount; q++)
        {
            if (LodPackedQuadFormat.FaceOf(sheets.PackedOpaqueQuads, q) < LodPackedFace.DiagonalRising) continue;
            tallest = Math.Max(tallest, (sheets.PackedOpaqueQuads[q * LodPackedQuadFormat.WordsPerQuad + 1] >> 16) * 0.25f);
        }
        c.Eq(101.25f, tallest, "and a block and a quarter tall from the ground, not floating at two");
        c.Eq(7, LodPackedQuadFormat.TextureSlot(sheets.PackedOpaqueQuads[2]), "in its own picture");

        section.Palette[0] = section.Palette[0] with { TextureSlot = 0 };
        c.Eq(0, Planes(Build(table)), "with no picture to wear it stays a block");

        LodVisualSettings on = LodVisualSettings.Default;
        c.True(on.BushSheetsOn, "bush sheets are on by default");
        c.False((on with { PlantTextures = false }).BushSheetsOn, "a sheet needs Plant Textures");
        c.False((on with { BushTextures = false }).BushSheetsOn, "and has its own switch");
    }

    static void UltraPreset(Check c)
    {
        LodVisualSettings ultra = LodVisualSettings.Ultra;
        c.True(ultra.PlantTexturesOn && ultra.ReedShapesOn && ultra.CropShapesOn && ultra.BushTexturesOn
                && ultra.VineShapesOn && ultra.ForageMarkersOn && ultra.FruitTreeColorsOn && ultra.FlowerSheetsOn,
            "Ultra has every plant feature");
        c.True(ultra.FarBiomeColours, "and the far biome colors Quality holds back");
        c.Eq(LodLeafShape.ClustersWithTops, ultra.LeafShapeOn, "and the chosen leaf shape");
        c.False(LodVisualSettings.Quality.FlowerSheetsOn, "flowers are Ultra's, being purely for the look");
        c.Eq("Ultra", ultra.PresetName, "it names itself");
        c.Eq(LodAutoThresholds.MinTargetPixels, LodPreset.Ultra.TargetPixels, "at the finest detail target");
        c.Eq(LodPreset.Ultra, LodPreset.All[^1], "above Quality in the list");
        c.False(LodPreset.FirstRun.Contains(LodPreset.Ultra), "and not on the first-run prompt, which has three places");
    }

    static void GrassUnderTrees(Check c)
    {
        var grass = new Block
        {
            Code = new AssetLocation("game", "tallgrass-medium-free"),
            BlockMaterial = EnumBlockMaterial.Plant,
        };
        c.True((LodBlockPolicy.FlagsFor(grass) & LodPaletteEntry.FlagThin) != 0,
            "tall grass under a tree lies flat instead of standing as a cube");
    }

    static int HighestShade(MeshResult mesh)
    {
        int highest = 0;
        for (int q = 0; q < mesh.PackedOpaqueQuadCount; q++)
        {
            if (LodPackedQuadFormat.IsTextured(mesh.PackedOpaqueQuads[q * LodPackedQuadFormat.WordsPerQuad]))
            {
                highest = Math.Max(highest, LodPackedQuadFormat.LeafShade(
                    mesh.PackedOpaqueQuads[q * LodPackedQuadFormat.WordsPerQuad + 2]));
            }
        }
        return highest;
    }

    static void FruitWoodInTheCanopy(Check c)
    {
        // A trunk from 100 to 110, with foliage either side of it from 106 up.
        LodSection tree = Fixtures.Section(
            (LodSection.ColumnIndex(10, 10), new[] { LodSection.PackRun(1, 110, 100) }),
            (LodSection.ColumnIndex(9, 10), new[] { LodSection.PackRun(0, 110, 106) }),
            (LodSection.ColumnIndex(11, 10), new[] { LodSection.PackRun(0, 110, 106) }));
        tree.Palette.Clear();
        tree.Palette.Add(new LodPaletteEntry { BlockId = Leaf, Color = 0x336633, TextureSlot = 7 });
        tree.Palette.Add(new LodPaletteEntry { BlockId = Stone, Color = 0x222211 });
        bool[] wood = { false, false, true };

        float Top(LodLeafShape shape, bool[]? table)
        {
            MeshResult mesh = LodMesher.BuildMesh(new MeshJob
            {
                Key = 0,
                Self = SectionSnapshot.Of(tree, crossedShapes: _ => LodBlockPolicy.CrossedShapeByFlags,
                    leafPlanes: shape == LodLeafShape.Cubes ? null : Leaves, canopyWood: table),
                Neighbors = new SectionSnapshot?[4],
                EmitTextureSlots = true,
                LeafShape = shape,
            });
            float top = 0f;
            for (int q = 0; q < mesh.PackedOpaqueQuadCount; q++)
            {
                if (LodPackedQuadFormat.FaceOf(mesh.PackedOpaqueQuads, q) >= LodPackedFace.DiagonalRising) continue;
                top = Math.Max(top, (mesh.PackedOpaqueQuads[q * LodPackedQuadFormat.WordsPerQuad + 1] >> 16) * 0.25f);
            }
            return top;
        }

        c.Eq(110f, Top(LodLeafShape.Planes, null), "without the table a trunk stands to its full height");
        c.Eq(106f, Top(LodLeafShape.Planes, wood), "with it, the wood stops where the foliage starts");
        c.Eq(110f, Top(LodLeafShape.Cubes, wood), "and cubes of leaves hide the trunk as they always did");
    }

    static void SwitchedOffPlants(Check c)
    {
        LodSection section = Fixtures.Section(
            (LodSection.ColumnIndex(10, 10), new[] { LodSection.PackRun(0, 101, 100) }));
        section.Palette.Clear();
        section.Palette.Add(new LodPaletteEntry
        {
            BlockId = Leaf, Color = 0x336633, Flags = LodPaletteEntry.FlagCrossed,
        });

        MeshResult Build(byte shape) => LodMesher.BuildMesh(new MeshJob
        {
            Key = 0,
            Self = SectionSnapshot.Of(section, crossedShapes: _ => shape),
            Neighbors = new SectionSnapshot?[4],
        });

        c.Eq(2, Planes(Build(LodBlockPolicy.CrossedQuartersPerBlock)), "a crop stands on two planes");
        MeshResult mat = Build(LodBlockPolicy.CrossedShapeAsMat);
        c.Eq(0, Planes(mat), "switched off it draws none");
        c.Eq(1, mat.PackedOpaqueQuadCount, "only the one flat patch coarser levels draw");
    }

    static void VariantsInThePalette(Check c)
    {
        var section = new LodSection();
        int cherry = section.FindOrAddPaletteEntry(5, 1, 0, variant: "cherry:plain");
        int pear = section.FindOrAddPaletteEntry(5, 2, 0, variant: "pear:plain");
        int plain = section.FindOrAddPaletteEntry(5, 3, 0);
        c.True(cherry != pear && pear != plain, "one block id holds a palette entry per variant");
        c.Eq(cherry, section.FindOrAddPaletteEntry(5, 9, 0, variant: "cherry:plain"),
            "and finds each again");

        var other = new LodSection();
        other.ReplaceColumns(ColumnWith(LodSection.PackRun(
            other.FindOrAddPaletteEntry(5, 1, 0, variant: "cherry:ripe"), 104, 100)), out _);
        section.MergeFrom(other, LodMergeMode.FillMissing);
        c.True(section.Palette.Any(e => e.Variant == "cherry:ripe"), "a merge keeps a variant");
    }

    static ulong[]?[] ColumnWith(ulong run)
    {
        var batch = new ulong[]?[LodSection.GridSize * LodSection.GridSize];
        batch[0] = new[] { run };
        return batch;
    }

    static void VariantsInStorage(Check c)
    {
        var section = new LodSection();
        section.FindOrAddPaletteEntry(0, 11, 0);
        int cherry = section.FindOrAddPaletteEntry(0, 22, 0, variant: "cherry:flowering");
        section.ReplaceColumns(ColumnWith(LodSection.PackRun(cherry, 104, 100)), out _);

        LodSaveSnapshot snapshot = Fixtures.Snapshot(section);
        snapshot.PaletteVariants = new[] { null, "cherry:flowering" };
        byte[] blob = LodStore.Serialize(snapshot);
        LodSection? back = new LodStore(null!).DeserializeForeign(blob, null);
        c.Eq("cherry:flowering", back?.Palette[cherry].Variant, "a variant survives a save");
        c.Eq(null, back?.Palette[0].Variant, "and the entry beside it has none");

        snapshot.PaletteVariants = Array.Empty<string?>();
        byte[] without = LodStore.Serialize(snapshot);
        c.True(new LodStore(null!).DeserializeForeign(without, null) != null,
            "a section with no fruit tree reads back");

        // A row from before variants ends after its snow tails: this one, less its two-byte count.
        LodSection? older = new LodStore(null!).DeserializeForeign(WithoutLastTwoBytes(without), null);
        c.True(older != null && older.Palette.All(e => e.Variant == null),
            "a row written before variants reads back as a section with none");

        snapshot.PaletteVariants = new string?[] { null, new string('x', LodStore.MaxVariantBytes + 1) };
        c.True(new LodStore(null!).DeserializeForeign(LodStore.Serialize(snapshot), null) == null,
            "a variant longer than any real one refuses the blob");
    }

    static byte[] WithoutLastTwoBytes(byte[] blob)
    {
        using var inflated = new MemoryStream();
        using (var input = new MemoryStream(blob, 1, blob.Length - 1))
        using (var inflate = new DeflateStream(input, CompressionMode.Decompress))
            inflate.CopyTo(inflated);

        using var output = new MemoryStream();
        output.WriteByte(blob[0]);
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(inflated.GetBuffer(), 0, (int)inflated.Length - 2);
        return output.ToArray();
    }

    static void FruitTreeParts(Check c)
    {
        var leaf = (100f, 100f, 100f);
        var blossom = (240f, 200f, 220f);
        var fruit = (200f, 40f, 40f);
        var bark = (60f, 50f, 40f);
        List<LodModelParts.Part> Parts(EnumFoliageState state) =>
            LodFruitTrees.PartsOf(state, leaf, blossom, fruit, bark).ToList();

        c.True(Parts(EnumFoliageState.Plain).All(part => part.Tinted), "a tree in leaf takes the whole tint");
        c.False(Parts(EnumFoliageState.Flowering).Any(part => part.Tinted),
            "a tree in blossom shows no leaves, so nothing on it does");
        c.False(Parts(EnumFoliageState.DormantNoLeaves).Any(part => part.Tinted)
                || Parts(EnumFoliageState.Dead).Any(part => part.Tinted),
            "nor does a bare or dead one");
        c.True(LodModelParts.TryCompose(Parts(EnumFoliageState.Ripe), out float r, out _, out float b,
                out LodUntintedShare share) && r > b && share.R > 0f,
            "ripe fruit reddens a tree, and the tint leaves the fruit alone");

        c.True(LodFruitTrees.TryParse(LodFruitTrees.VariantOf("pinkapple", EnumFoliageState.Flowering),
                out string type, out EnumFoliageState state)
                && type == "pinkapple" && state == EnumFoliageState.Flowering,
            "a variant reads back as what it was made from");
        c.False(LodFruitTrees.TryParse("nonsense", out _, out _)
                || LodFruitTrees.TryParse("cherry:sideways", out _, out _),
            "and one this build cannot read is refused");
        c.True(ReferenceEquals(LodFruitTrees.VariantOf("pear", EnumFoliageState.Ripe),
                LodFruitTrees.VariantOf("pear", EnumFoliageState.Ripe)),
            "one tree's blocks share one string, which is how capture compares them");
    }

    static void Wiring(Check c)
    {
        string dialog = Source("Gui", "VintageHorizonsConfigDialog.cs");
        foreach (string row in new[] { "plant-detail", "fruit-tree-colors", "crop-shapes", "reed-shapes", "vine-shapes", "forage-markers", "flower-sheets",
            "plant-textures", "bush-textures", "leaf-shape" })
        {
            c.True(dialog.Contains($"\"{row}\"", StringComparison.Ordinal), $"the Plants tab has {row}");
        }

        string mod = Source("VintageHorizonsModSystem.cs");
        foreach (string name in new[] { "PlantDetail", "ReedShapes", "VineShapes", "ForageMarkers", "FlowerSheets", "CropShapes", "BushTextures",
            "FruitTreeColors", "LeafShape" })
        {
            c.True(mod.Contains($"config.{name} = v.{name};", StringComparison.Ordinal)
                    && mod.Contains($"{name}: config.{name}", StringComparison.Ordinal),
                $"{name} reaches the config file and comes back out of it");
        }
        // Save reads the plant switches back out of the config, so with a renderer present they
        // have to be written first or the window reopens on the old value and saves it again.
        int apply = mod.IndexOf("void ApplyGuiConfig(", StringComparison.Ordinal);
        int writes = mod.IndexOf("WriteVisualSettings(visuals);", apply, StringComparison.Ordinal);
        int rendererBranch = mod.IndexOf("if (renderer != null)", apply, StringComparison.Ordinal);
        c.True(apply >= 0 && writes >= 0 && writes < rendererBranch,
            "a Save writes the visual settings whether or not a renderer exists");
        c.Eq(2.5f, LodTaaMath.SeamReach(2.5f, 0f, 256f), "with no frame drawn the reach is the player's own");
        c.Eq(32f, LodTaaMath.SeamReach(32f, 1000f, 512f), "and a reach set wider is never narrowed");
        float atSeam = LodTaaMath.SeamReach(2.5f, 1000f, 256f);
        c.True(LodTaaMath.Ramp(1000f, 256f * 0.6f, atSeam) >= 1f,
            "anti-aliasing is at full strength where cached terrain can first appear");
        c.True(LodTaaMath.Ramp(1000f, 256f * 0.1f, atSeam) < 1f,
            "and not on stand-in terrain far inside vanilla's distance");
        c.True(mod.Contains("|| renderer.LeafShape != v.LeafShapeOn", StringComparison.Ordinal)
                && mod.Contains("|| renderer.CropShapesEnabled != v.CropShapesOn", StringComparison.Ordinal),
            "a shape switch re-meshes, since it decides what geometry exists");

        c.True(mod.Contains("if (LodBlockPolicy.IsFruitWood(block)) return FruitWoodColor;", StringComparison.Ordinal)
                && mod.Contains("if (LodBlockPolicy.IsFruitWood(block)) return LodTintRegistry.SlotNone;",
                    StringComparison.Ordinal)
                && mod.Contains("LodBlockPolicy.IsFruitFoliage(block) || LodBlockPolicy.IsFruitWood(block)",
                    StringComparison.Ordinal),
            "a fruit tree's wood takes its one bark colour and no plant tint, wherever a colour is asked for");

        c.True(mod.Contains("found ??= ColorMapCarrier(climate, season);", StringComparison.Ordinal)
                && mod.Contains("if (block.Textures == null) return null;", StringComparison.Ordinal),
            "a season named only inside a model reaches the tint table, so small ferns turn as the game's do");
        c.True(mod.Contains("TryMushroomCapColor(block, out int capColor)", StringComparison.Ordinal),
            "a mushroom takes the colour of its cap");
        c.Eq("cap", LodBlockPolicy.MushroomCapTexture(new[] { "cap", "stipe", "pores" }), "a model may name it");
        c.Eq("puffball", LodBlockPolicy.MushroomCapTexture(new[] { "puffball" }), "or use the mushroom's own name");
        c.Eq("kingbolete", LodBlockPolicy.MushroomCapTexture(new[] { "pores", "kingbolete" }), "beside its underside");
        c.Eq("honeymushroom", LodBlockPolicy.MushroomCapTexture(new[] { "gills", "honeymushroom" }), "whichever comes first");
        c.Eq(null, LodBlockPolicy.MushroomCapTexture(new[] { "pores" }), "and with nothing else it answers nothing");
        c.True(Source("Render", "LodTintRegistry.cs").Contains("block is not Vintagestory.GameContent.BlockMushroom",
                StringComparison.Ordinal),
            "a mushroom takes no plant tint, which turned white ones orange");

        string renderer = Source("Render", "LodTerrainRenderer.cs");
        c.True(renderer.Contains("LodWorld.KeyLevel(best) == 0 ? leafPlanes : null", StringComparison.Ordinal),
            "only level 0 draws leaves as planes");
    }

    static string Source(params string[] path) => File.ReadAllText(Path.Combine(
        new[] { GameAssemblies.RepoRoot, "VintageHorizons", "src" }.Concat(path).ToArray()));
}
