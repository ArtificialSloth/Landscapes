using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace VintageHorizons.Checks;

/// <summary>
/// Reeds at distance. Capture starts at the rain map, which passes through a block that lets rain
/// through, so a reed on land sat one block above where capture began; a reed in water shares its
/// cell with the water. Both were only ever captured under a tree. Everything else that lets rain
/// through must stay out, or every meadow rises by a block.
/// </summary>
public static class ReedCaptureChecks
{
    const int Soil = 10, Reed = 40, Water = 50, TallGrass = 60, Leaf = 70;

    public static void Run(Check c)
    {
        Capture(c);
        CrossedPlanes(c);
    }

    /// <summary>
    /// A reed is two crossed planes at level 0, the height of the game's own far model, and a
    /// mat past it. It hides nothing: the ground keeps its top, and water keeps its surface.
    /// </summary>
    static void CrossedPlanes(Check c)
    {
        const byte Crossed = LodPaletteEntry.FlagCrossed | LodPaletteEntry.FlagCutout;

        var section = new LodSection();
        int soil = section.FindOrAddPaletteEntry(blockId: 1, color: 0x00806040, flags: 0);
        int cattail = section.FindOrAddPaletteEntry(blockId: 2, color: 0x00407030, flags: Crossed);
        int sedge = section.FindOrAddPaletteEntry(blockId: 3, color: 0x00507030,
            flags: Crossed | LodPaletteEntry.FlagHalf);
        int water = section.FindOrAddPaletteEntry(blockId: 4, color: 0x00C06030,
            flags: LodPaletteEntry.FlagWater);
        section.SetColumn(LodSection.ColumnIndex(10, 20), new[]
        {
            LodSection.PackRun(cattail, 11, 10), LodSection.PackRun(soil, 10, 1),
        });

        static int Count(MeshResult mesh, params LodPackedFace[] faces) =>
            Enumerable.Range(0, mesh.PackedOpaqueQuadCount)
                .Count(i => faces.Contains(LodPackedQuadFormat.FaceOf(mesh.PackedOpaqueQuads, i)));

        MeshResult near = LodMesher.BuildMesh(Fixtures.Job(section));
        c.Eq(1, Count(near, LodPackedFace.DiagonalRising), "one plane rises across the column");
        c.Eq(1, Count(near, LodPackedFace.DiagonalFalling), "and one falls across it");
        c.Eq(1, Count(near, LodPackedFace.Top), "the ground under a reed keeps its top");
        c.Eq(12.75f, near.Heights.Opaque.MaxY, "a reed stands 2.75 blocks tall");

        MeshResult far = LodMesher.BuildMesh(Fixtures.Job(section, LodWorld.SectionKey(1, 0, 0)));
        c.Eq(0, Count(far, LodPackedFace.DiagonalRising, LodPackedFace.DiagonalFalling),
            "coarser levels draw no planes");
        c.Eq(2, Count(far, LodPackedFace.Top), "they draw the ground and a mat of the reed's colour");
        c.Eq(10.25f, far.Heights.Opaque.MaxY, "and the mat lies a quarter block over the ground");

        section.Palette[cattail] = section.Palette[cattail] with { TextureSlot = 7 };
        MeshResult sheeted = LodMesher.BuildMesh(Fixtures.Job(section, emitTextureSlots: true));
        int plane = Enumerable.Range(0, sheeted.PackedOpaqueQuadCount)
            .First(i => LodPackedQuadFormat.FaceOf(sheeted.PackedOpaqueQuads, i) == LodPackedFace.DiagonalRising);
        int planeWord = plane * LodPackedQuadFormat.WordsPerQuad;
        c.True(LodPackedQuadFormat.IsTextured(sheeted.PackedOpaqueQuads[planeWord]),
            "with textures on, a plane carries the reed's sheet");
        c.Eq(7, LodPackedQuadFormat.TextureSlot(sheeted.PackedOpaqueQuads[planeWord + 2]),
            "and names the slot its block was given");

        var shorter = new LodSection();
        foreach ((int id, int color, byte flags) in new[] { (1, 0x00806040, (byte)0), (2, 0, Crossed), (3, 0, (byte)(Crossed | LodPaletteEntry.FlagHalf)) })
            shorter.FindOrAddPaletteEntry(id, color, flags);
        shorter.SetColumn(0, new[] { LodSection.PackRun(sedge, 11, 10), LodSection.PackRun(soil, 10, 1) });
        c.Eq(11.25f, LodMesher.BuildMesh(Fixtures.Job(shorter)).Heights.Opaque.MaxY,
            "sedge and cut reeds stand 1.25 blocks tall");

        var wet = new LodSection();
        foreach ((int id, int color, byte flags) in new[] { (1, 0x00806040, (byte)0), (2, 0, Crossed), (3, 0, (byte)0), (4, 0x00C06030, LodPaletteEntry.FlagWater) })
            wet.FindOrAddPaletteEntry(id, color, flags);
        wet.SetColumn(0, new[]
        {
            LodSection.PackRun(cattail, 12, 11), LodSection.PackRun(water, 11, 10),
            LodSection.PackRun(soil, 10, 1),
        });
        MeshResult inWater = LodMesher.BuildMesh(Fixtures.Job(wet));
        c.Eq(12.75f, inWater.Heights.Opaque.MaxY, "a reed in water keeps its height above the bed");
        c.True(inWater.Heights.Water.MaxY >= 11f, "and the water under it keeps its surface");

        Span<uint> words = stackalloc uint[LodPackedQuadFormat.WordsPerQuad];
        LodPackedQuadFormat.Encode(words, LodPackedFace.DiagonalFalling, 3, 4, 1, 3.75f, 5, 6, 0, 0);
        LodPackedQuadFormat.DecodeVertex(words, 0, 0, 1, out float x, out float y, out float z, out _);
        c.True(x == 3 && y == 1 && z == 6, "a falling plane starts at the high Z corner");
        LodPackedQuadFormat.DecodeVertex(words, 0, 2, 1, out x, out y, out z, out _);
        c.True(x == 4 && y == 3.75f && z == 5, "and runs to the low Z corner at its top");
    }

    static void Capture(Check c)
    {
        bool[] table = LodBlockPolicy.ReedTable(new Block?[] { new Block(), new BlockReeds(), null }!);
        c.True(table.SequenceEqual(new[] { false, true, false }), "the table marks reeds by the engine's class");

        // The solid layer, then the fluid layer, of the column at lx.
        static int SolidAt(int lx, int y)
        {
            if (lx is 2 or 3) return y <= 99 ? Soil : lx == 2 && y == 100 ? Reed : 0;
            if (y <= 100) return Soil;
            if (y == 101) return lx switch { 0 or 4 => Reed, 1 => TallGrass, _ => 0 };
            return lx == 4 && y == 105 ? Leaf : 0;
        }
        static int FluidAt(int lx, int y) => lx is 2 or 3 && y == 100 ? Water : 0;

        var chunks = new IWorldChunk?[4];
        for (int cy = 0; cy < chunks.Length; cy++) chunks[cy] = LayeredChunk.Create(cy, SolidAt, FluidAt);

        var rain = new ushort[32 * 32];
        for (int lx = 0; lx <= 3; lx++) rain[lx] = 100;
        rain[4] = 105;

        CaptureResult? Capture(bool[]? reeds) => LodWorker.Capture(new CaptureJob
        {
            Epoch = 1,
            SnowStateObserved = true,
            Chunks = chunks,
            RainMap = rain,
            Reeds = reeds,
        });
        static bool Holds(CaptureResult? result, int lx, params ulong[] runs) =>
            result?.RunsByColumn[LodSection.ColumnIndex(lx, 0)]?.SequenceEqual(runs) == true;

        var reeds = new bool[Leaf + 1];
        reeds[Reed] = true;
        CaptureResult? got = Capture(reeds);

        c.True(Holds(got, 0, LodSection.PackRun(Reed, 102, 101), LodSection.PackRun(Soil, 101, 1)),
            "a reed on land stands one block above the rain map and is captured");
        c.True(Holds(got, 1, LodSection.PackRun(Soil, 101, 1)),
            "tall grass above the rain map stays out, so a meadow keeps its height");
        c.True(Holds(got, 2, LodSection.PackRun(Reed, 102, 101), LodSection.PackRun(Water, 101, 100),
                LodSection.PackRun(Soil, 100, 1)),
            "a reed in water stands on the surface, and the water keeps its cell");
        c.True(Holds(got, 3, LodSection.PackRun(Water, 101, 100), LodSection.PackRun(Soil, 100, 1)),
            "open water beside it is still water");
        c.True(Holds(got, 4, LodSection.PackRun(Leaf, 106, 105), LodSection.PackRun(Reed, 102, 101),
                LodSection.PackRun(Soil, 101, 1)),
            "a reed under a tree is captured as it always was");

        CaptureResult? without = Capture(null);
        c.True(Holds(without, 0, LodSection.PackRun(Soil, 101, 1))
            && Holds(without, 2, LodSection.PackRun(Water, 101, 100), LodSection.PackRun(Soil, 100, 1)),
            "with no table a column captures exactly as it did before reeds");
    }

    /// <summary>Just enough of IWorldChunk for capture, answering each block layer as the game does.</summary>
    public class LayeredChunk : DispatchProxy
    {
        int chunkY;
        System.Func<int, int, int>? solidAt, fluidAt;

        public static IWorldChunk Create(int chunkY, System.Func<int, int, int> solidAt,
            System.Func<int, int, int> fluidAt)
        {
            IWorldChunk chunk = DispatchProxy.Create<IWorldChunk, LayeredChunk>();
            var proxy = (LayeredChunk)(object)chunk;
            proxy.chunkY = chunkY;
            proxy.solidAt = solidAt;
            proxy.fluidAt = fluidAt;
            return chunk;
        }

        int Read(int index, int layer)
        {
            int lx = index % 32, y = chunkY * 32 + index / (32 * 32);
            int solid = solidAt!(lx, y), fluid = fluidAt!(lx, y);
            return layer == BlockLayersAccess.Solid ? solid : fluid != 0 ? fluid : solid;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IWorldChunk.UnpackAndReadBlock) => Read((int)args![0]!, (int)args![1]!),
            "get_" + nameof(IWorldChunk.Disposed) => false,
            _ => throw new NotSupportedException("capture reached for IWorldChunk." + targetMethod?.Name),
        };
    }
}
