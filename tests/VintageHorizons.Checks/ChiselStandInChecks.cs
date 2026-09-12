using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace VintageHorizons.Checks;

/// <summary>
/// Chiselled blocks drawn as their most visible material (0.4.62). Covers the arithmetic over
/// the engine's own cuboid encoding, the placeholder test whose exact comparison let
/// near-white through before, and the capture worker's substitution over proxy chunks - no
/// game process needed. What it cannot reach is a live chunk's block-entity table and the
/// apply step's snow read, which need a running world.
/// </summary>
public static class ChiselStandInChecks
{
    const int Stone = 10, Planks = 20, Gold = 30, Chisel = 500;

    static uint Box(int x1, int y1, int z1, int x2, int y2, int z2, int material) =>
        BlockEntityMicroBlock.ToUint(x1, y1, z1, x2, y2, z2, material);

    static readonly uint[] Solid = { Box(0, 0, 0, 16, 16, 16, 0) };

    /// <summary>A stone body, material 0, under a one-voxel plank trim, material 1.</summary>
    static readonly uint[] Trim = { Box(0, 0, 0, 16, 15, 16, 0), Box(0, 15, 0, 16, 16, 16, 1) };

    /// <summary>A one-voxel skin, material 0, wrapped round a core, material 1.</summary>
    static readonly uint[] Skinned =
    {
        Box(1, 1, 1, 15, 15, 15, 1),
        Box(0, 0, 0, 16, 1, 16, 0), Box(0, 15, 0, 16, 16, 16, 0),
        Box(0, 1, 0, 1, 15, 16, 0), Box(15, 1, 0, 16, 15, 16, 0),
        Box(1, 1, 0, 15, 15, 1, 0), Box(1, 1, 15, 15, 15, 16, 0),
    };

    public static void Run(Check c)
    {
        Arithmetic(c);
        Placeholders(c);
        Capture(c);
    }

    static void Arithmetic(Check c)
    {
        bool decoded = LodChiselStandIn.Decode(Box(1, 2, 3, 16, 15, 14, 7),
            out int x1, out int y1, out int z1, out int x2, out int y2, out int z2, out int material);
        c.True(decoded && x1 == 1 && y1 == 2 && z1 == 3 && x2 == 16 && y2 == 15 && z2 == 14 && material == 7,
            "cuboid decoding reads back what the engine's own encoder wrote");
        c.False(LodChiselStandIn.Decode(Box(15, 0, 0, 1, 16, 16, 0), out _, out _, out _, out _, out _, out _, out _),
            "a cuboid with no volume is refused");

        c.Eq(6L * 256, LodChiselStandIn.ExposedFaces(Solid, 1)[0], "a full block shows all six faces");
        c.Eq(Stone, LodChiselStandIn.StandInFor(Solid, new[] { Stone }), "a solid chisel draws as its only material");

        long[] trim = LodChiselStandIn.ExposedFaces(Trim, 2);
        c.Eq(256L + 4 * 16 * 15, trim[0], "a body shows its bottom and sides but not the face under its trim");
        c.Eq(256L + 4 * 16, trim[1], "a trim shows its top and its thin sides");
        c.Eq(Stone, LodChiselStandIn.StandInFor(Trim, new[] { Stone, Planks }),
            "a thin trim does not outvote the body it sits on");

        long[] skin = LodChiselStandIn.ExposedFaces(Skinned, 2);
        c.Eq(6L * 256, skin[0], "a skin shows exactly the outside of the block");
        c.Eq(0L, skin[1], "a core wrapped on every side shows nothing");
        int[] skinMaterials = { Planks, Stone };
        c.Eq(Planks, LodChiselStandIn.StandInFor(Skinned, skinMaterials),
            "the most visible material wins...");
        c.Eq(Stone, BlockEntityMicroBlock.getMajorityMaterial(new List<uint>(Skinned), skinMaterials),
            "...where the game's own majority-by-volume answer is the hidden core");

        uint[] post = { Box(7, 0, 7, 9, 16, 9, 0) };
        c.Eq(4L * 2 * 16 + 2 * 4, LodChiselStandIn.ExposedFaces(post, 1)[0], "a post shows four sides and two ends");
        c.Eq(Planks, LodChiselStandIn.StandInFor(post, new[] { Planks }), "a thin post still draws as its material");

        c.Eq(Planks, LodChiselStandIn.StandInFor(Trim, new[] { 0, Planks }),
            "an ineligible material gives way to the next most visible one");
        c.Eq(0, LodChiselStandIn.StandInFor(Trim, new[] { 0, 0 }), "no eligible material means no stand-in");
        c.Eq(0, LodChiselStandIn.StandInFor(ReadOnlySpan<uint>.Empty, new[] { Stone }), "an empty chisel has no stand-in");

        uint[] stray = { Box(0, 0, 0, 16, 8, 16, 0), Box(0, 8, 0, 16, 16, 16, 5) };
        c.Eq(256L + 4 * 16 * 8, LodChiselStandIn.ExposedFaces(stray, 1)[0],
            "a material index past the list still hides what it covers");
        c.Eq(Stone, LodChiselStandIn.StandInFor(stray, new[] { Stone }), "and is never chosen itself");

        uint[] striped = { Box(0, 0, 0, 3, 16, 16, 0), Box(3, 0, 0, 13, 16, 16, 1), Box(13, 0, 0, 16, 16, 16, 2) };
        c.Eq(Stone, LodChiselStandIn.StandInFor(striped, new[] { Stone, Planks, Stone }),
            "two stone slices outvote the plank slice between them together, not one by one");

        uint[] halves = { Box(0, 0, 0, 8, 16, 16, 0), Box(8, 0, 0, 16, 16, 16, 1) };
        c.Eq(Stone, LodChiselStandIn.StandInFor(halves, new[] { Stone, Planks }),
            "a tie goes to the first material, the one the game reports");

        c.Eq((101 * 32 + 3) * 32 + 2, LodChiselStandIn.IndexOf(2, 101, 3), "a column-local index is y, then z, then x");
        c.True(LodChiselStandIn.RunKey(4095, 16383) > 0
            && LodChiselStandIn.RunKey(1, 0) != LodChiselStandIn.RunKey(0, 1)
            && LodChiselStandIn.RunKey(4095, 16383) != LodChiselStandIn.RunKey(4094, 16383),
            "run keys stay positive and distinct across a whole section");

        var records = new List<LodChiselRecord>
        {
            new(LodChiselStandIn.IndexOf(1, 2, 3), Chisel, Skinned, new[] { Planks, Stone }),
            new(LodChiselStandIn.IndexOf(4, 5, 6), Chisel, Trim, new[] { 0, 0 }),
        };
        for (int pass = 0; pass < 2; pass++)
        {
            Dictionary<int, (int Chisel, int StandIn)>? resolved = LodChiselStandIn.Resolve(records, out int without);
            c.True(resolved != null && resolved.Count == 1
                && resolved.TryGetValue(LodChiselStandIn.IndexOf(1, 2, 3), out var standIn)
                && standIn == (Chisel, Planks),
                pass == 0 ? "a recorded chisel resolves to its stand-in" : "a repeated shape answers the same from the cache");
            c.Eq(1, without, "a chisel with nothing eligible is counted, not resolved");
        }
        c.True(LodChiselStandIn.Resolve(null, out int none) == null && none == 0, "no records resolve to nothing");
    }

    static void Placeholders(Check c)
    {
        const int atlas = 0x00FCFCFC;
        int file = unchecked((int)0xFFF9F9FF);
        int opaqueAtlas = unchecked((int)0xFFFCFCFC);
        int material = unchecked((int)0xFF6E8992);

        c.True(LodPaletteRepair.IsPlaceholder(atlas, atlas, file), "the atlas's own estimate is the placeholder");
        c.True(LodPaletteRepair.IsPlaceholder(opaqueAtlas, atlas, file),
            "the estimate repacked opaque is still the placeholder - the comparison 0.4.61 missed");
        c.True(LodPaletteRepair.IsPlaceholder(file, atlas, file), "the file's own mean is the placeholder");
        c.True(LodPaletteRepair.IsPlaceholder(0x00F9F9FF, atlas, file), "so is that mean without alpha");
        c.False(LodPaletteRepair.IsPlaceholder(LodPaletteRepair.UnknownBlockColor, atlas, file),
            "neutral grey is not the placeholder");
        c.False(LodPaletteRepair.IsPlaceholder(material, atlas, file), "a real material colour is not the placeholder");
        c.False(LodPaletteRepair.IsPlaceholder(0, atlas, file), "uncoloured is a different repair");
        c.False(LodPaletteRepair.IsPlaceholder(atlas, 0, 0), "an unknown reference matches nothing");

        const int missingModBlock = 7;
        var section = new LodSection();
        section.Palette.Add(new LodPaletteEntry { BlockId = Chisel, Color = opaqueAtlas });
        section.Palette.Add(new LodPaletteEntry { BlockId = missingModBlock, Color = opaqueAtlas });
        section.Palette.Add(new LodPaletteEntry { BlockId = Chisel + 1, Color = material });
        section.Palette.Add(new LodPaletteEntry { BlockId = 0, Color = atlas });

        System.Func<int, bool> isPlaceholder = color => LodPaletteRepair.IsPlaceholder(color, atlas, file);
        System.Func<int, int> entityGrey = blockId => blockId is Chisel or Chisel + 1 ? LodPaletteRepair.UnknownBlockColor : 0;
        c.Eq(1, LodPaletteRepair.ReplacePlaceholders(section, isPlaceholder, entityGrey),
            "only the white block-entity entry is repaired");
        c.Eq(LodPaletteRepair.UnknownBlockColor, section.Palette[0].Color, "a stored white chisel becomes neutral grey");
        c.Eq(opaqueAtlas, section.Palette[1].Color, "a removed mod's block keeps the game's own missing-texture look");
        c.Eq(material, section.Palette[2].Color, "a real stored colour is left alone");
        c.Eq(atlas, section.Palette[3].Color, "an unresolved entry is left to the unknown-block path");
        c.Eq(0, LodPaletteRepair.ReplacePlaceholders(section, isPlaceholder, entityGrey),
            "a repaired section is finished: the next load changes nothing");
    }

    /// <summary>
    /// The worker's substitution end to end over proxy chunks. Four columns along x at z 0:
    /// a plank-skinned chisel stacked on a solid stone chisel; a chisel whose entity was never
    /// copied; a stale entity over ordinary stone; and a chisel made of nothing that can
    /// stand in. Stone fills the columns below.
    /// </summary>
    static void Capture(Check c)
    {
        static int BlockAt(int lx, int y, int lz)
        {
            if (lz != 0 || y < 1 || lx > 3) return 0;
            bool chiselled = lx switch
            {
                0 => y is 101 or 102,
                1 or 3 => y == 101,
                _ => false,
            };
            if (chiselled) return Chisel;
            return y <= (lx == 2 ? 101 : 100) ? Stone : 0;
        }

        var entities = new Dictionary<BlockPos, BlockEntity>();
        void AddChisel(int x, int y, int z, uint[] cuboids, params int[] ids)
        {
            var chisel = new BlockEntityMicroBlock
            {
                Pos = new BlockPos(x, y, z),
                VoxelCuboids = new List<uint>(cuboids),
                BlockIds = ids,
            };
            entities[chisel.Pos] = chisel;
        }
        AddChisel(0, 102, 0, Skinned, Planks, Stone);
        AddChisel(0, 101, 0, Solid, Stone);
        AddChisel(2, 101, 0, Solid, Planks);
        AddChisel(3, 101, 0, Solid, Gold);

        var chunks = new IWorldChunk?[4];
        for (int cy = 0; cy < chunks.Length; cy++) chunks[cy] = ProxyChunk.Create(cy, BlockAt, entities);

        List<LodChiselRecord>? records = LodChiselStandIn.Collect(chunks, 0, 0,
            blockId => blockId == Chisel, blockId => blockId is Stone or Planks);
        c.Eq(3, records?.Count ?? 0, "only block entities standing on chisel blocks are copied");

        var rain = new ushort[32 * 32];
        for (int lx = 0; lx <= 3; lx++) rain[lx] = 110;

        CaptureResult? result = LodWorker.Capture(new CaptureJob
        {
            Epoch = 1,
            SnowStateObserved = true,
            Chunks = chunks,
            RainMap = rain,
            Chisels = records,
        });
        c.True(result != null, "the proxy column captures");
        if (result == null) return;
        CaptureResult got = result;

        ulong[]? RunsAt(CaptureResult from, int lx) => from.RunsByColumn[LodSection.ColumnIndex(lx, 0)];
        bool Original(int lx, int yTop) => got.StandInOriginals != null
            && got.StandInOriginals.TryGetValue(LodChiselStandIn.RunKey(LodSection.ColumnIndex(lx, 0), yTop), out int chisel)
            && chisel == Chisel;

        c.True(RunsAt(got, 0)?.SequenceEqual(new[] { LodSection.PackRun(Planks, 103, 102), LodSection.PackRun(Stone, 102, 1) }) == true,
            "stacked chisels split into runs by their own materials, and a stone chisel joins the stone below it");
        c.True(got.StandInOriginals?.Count == 2 && Original(0, 103) && Original(0, 102),
            "each run topped by a stand-in remembers the chisel it replaced, for the column's snow");
        c.True(RunsAt(got, 1)?.SequenceEqual(new[] { LodSection.PackRun(Chisel, 102, 101), LodSection.PackRun(Stone, 101, 1) }) == true,
            "a chisel whose contents were never copied stays a chisel");
        c.True(RunsAt(got, 2)?.SequenceEqual(new[] { LodSection.PackRun(Stone, 102, 1) }) == true,
            "a stale block entity over an ordinary block changes nothing");
        c.True(RunsAt(got, 3)?.SequenceEqual(new[] { LodSection.PackRun(Chisel, 102, 101), LodSection.PackRun(Stone, 101, 1) }) == true,
            "a chisel with no material that can stand in stays a chisel");
        c.Eq(2, got.ChiselStandIns, "both readable chisels were substituted");
        c.Eq(1, got.ChiselsWithoutMaterial, "the chisel with nothing eligible is counted");

        CaptureResult? plain = LodWorker.Capture(new CaptureJob
        {
            Epoch = 1,
            SnowStateObserved = true,
            Chunks = chunks,
            RainMap = rain,
        });
        c.True(plain != null
            && RunsAt(plain, 0)?.SequenceEqual(new[] { LodSection.PackRun(Chisel, 103, 101), LodSection.PackRun(Stone, 101, 1) }) == true
            && plain.StandInOriginals == null && plain.ChiselStandIns == 0,
            "with nothing copied a column captures exactly as it did before stand-ins");
    }

    /// <summary>
    /// Just enough of IWorldChunk for capture: block reads, the block-entity table and
    /// Disposed. Anything else capture reaches for fails the check by name.
    /// </summary>
    public class ProxyChunk : DispatchProxy
    {
        System.Func<int, int>? blockAt;
        Dictionary<BlockPos, BlockEntity>? entities;

        public static IWorldChunk Create(int chunkY, System.Func<int, int, int, int> blockAt,
            Dictionary<BlockPos, BlockEntity> entities)
        {
            IWorldChunk chunk = DispatchProxy.Create<IWorldChunk, ProxyChunk>();
            var proxy = (ProxyChunk)(object)chunk;
            proxy.entities = entities;
            proxy.blockAt = index => blockAt(index % 32, chunkY * 32 + index / (32 * 32), index / 32 % 32);
            return chunk;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IWorldChunk.UnpackAndReadBlock) => blockAt!((int)args![0]!),
            "get_" + nameof(IWorldChunk.Disposed) => false,
            "get_" + nameof(IWorldChunk.BlockEntities) => entities,
            _ => throw new NotSupportedException("capture reached for IWorldChunk." + targetMethod?.Name),
        };
    }
}
