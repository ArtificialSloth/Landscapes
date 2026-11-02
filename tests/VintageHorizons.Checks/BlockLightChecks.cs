using System.Reflection;
using Vintagestory.API.Common;

namespace VintageHorizons.Checks;

/// <summary>
/// Lamps on distant ground: capture copies the light reaching each column's top, it survives the
/// cache and every merge, and a section's light turns into a tile the shader can read. What this
/// cannot reach is the look at night, which needs a person and a running game.
/// </summary>
public static class BlockLightChecks
{
    const int Soil = 10;

    public static void Run(Check c)
    {
        Values(c);
        Capture(c);
        SetLight(c);
        Mips(c);
        Storage(c);
        Merging(c);
        Tiles(c);
    }

    static void Values(Check c)
    {
        ushort value = LodBlockLight.Pack(22, 45, 5);
        c.Eq(22, LodBlockLight.Level(value), "a value keeps its level");
        c.Eq(45, LodBlockLight.Hue(value), "and its hue");
        c.Eq(5, LodBlockLight.Saturation(value), "and its saturation");
        c.Eq((ushort)0, LodBlockLight.Pack(0, 45, 5), "with no level there is no light, whatever its colour");

        ushort word = (ushort)(9 | 22 << 5 | 45 << 10);
        c.Eq(value, LodBlockLight.FromChunkLight(word, 5), "a chunk's word gives its block light, not its sunlight");
        c.Eq((ushort)0, LodBlockLight.FromChunkLight(31, 0), "full sunlight is not block light");
    }

    static void Capture(Check c)
    {
        var chunks = new IWorldChunk?[4];
        for (int cy = 0; cy < chunks.Length; cy++) chunks[cy] = LitChunk.Create(cy);

        var rain = new ushort[32 * 32];
        for (int lx = 0; lx < 8; lx++) rain[lx] = 100;

        CaptureResult? got = LodWorker.Capture(new CaptureJob
        {
            Epoch = 1,
            SnowStateObserved = true,
            Chunks = chunks,
            RainMap = rain,
        });
        ushort[]? light = got?.BlockLight;
        c.True(light != null, "a lit column gives the capture its light");
        c.Eq(LodBlockLight.Pack(20, 7, 5), light?[LodSection.ColumnIndex(3, 0)] ?? (ushort)0,
            "read from the cell above the column's top block");
        c.Eq((ushort)0, light?[LodSection.ColumnIndex(4, 0)] ?? (ushort)1,
            "light below the top block is under the ground, and does not count");

        CaptureResult? peek = LodWorker.Capture(new CaptureJob
        {
            Epoch = 1,
            SnowStateObserved = false,
            Chunks = chunks,
            RainMap = rain,
        });
        c.True(peek != null && peek.BlockLight == null, "a generated peek has no lighting to report");
    }

    static void SetLight(Check c)
    {
        var section = new LodSection();
        var written = new ulong[]?[LodSection.GridSize * LodSection.GridSize];
        written[5] = Array.Empty<ulong>();
        var light = new ushort[written.Length];
        light[5] = LodBlockLight.Pack(10, 1, 1);
        light[6] = LodBlockLight.Pack(20, 1, 1);

        c.True(section.SetLight(written, light), "a lit column is a change");
        c.Eq(light[5], section.BlockLight![5], "and is taken");
        c.Eq((ushort)0, section.BlockLight![6], "a column that was not written keeps what it had");
        c.False(section.SetLight(written, light), "the same light again is no change");

        ushort[] before = section.BlockLight!;
        written[6] = Array.Empty<ulong>();
        c.True(section.SetLight(written, light), "a second column is a change");
        c.False(ReferenceEquals(before, section.BlockLight), "and replaces the array, which a worker may hold");
        c.Eq((ushort)0, before[6], "leaving the old one as it was");

        c.True(section.SetLight(written, null), "a lamp going out is a change");
        c.True(section.BlockLight == null, "and a section with no light left holds none");
    }

    static void Mips(Check c)
    {
        LodSection child = Fixtures.SolidSection();
        var light = new ushort[LodSection.GridSize * LodSection.GridSize];
        light[LodSection.ColumnIndex(2, 2)] = LodBlockLight.Pack(10, 1, 1);
        light[LodSection.ColumnIndex(3, 3)] = LodBlockLight.Pack(20, 2, 2);
        child.BlockLight = light;

        var parent = new LodSection();
        LodMip.DownsampleIntoParent(child, parent, 0, 0);
        c.Eq(light[LodSection.ColumnIndex(3, 3)], parent.BlockLight?[LodSection.ColumnIndex(1, 1)] ?? (ushort)0,
            "a parent column keeps the brightest of its four, so a torch outlasts its level");

        child.BlockLight = null;
        c.True(LodMip.DownsampleIntoParent(child, parent, 0, 0), "a lamp going out reaches the parent");
        c.True(parent.BlockLight == null, "and leaves it dark");
    }

    static void Storage(Check c)
    {
        LodSection section = Fixtures.SolidSection();
        var light = new ushort[LodSection.GridSize * LodSection.GridSize];
        light[0] = LodBlockLight.Pack(1, 2, 3);
        light[^1] = LodBlockLight.Pack(31, 63, 7);
        section.BlockLight = light;

        LodSection? back = new LodStore(null!).DeserializeForeign(LodStore.Serialize(Fixtures.Snapshot(section)), null);
        c.True(back?.BlockLight != null && back.BlockLight.SequenceEqual(light), "light survives a blob, first column to last");

        section.BlockLight = null;
        back = new LodStore(null!).DeserializeForeign(LodStore.Serialize(Fixtures.Snapshot(section)), null);
        c.True(back != null && back.BlockLight == null, "a section with none reads back with none");
    }

    static void Merging(Check c)
    {
        LodSection source = Fixtures.SolidSection();
        var light = new ushort[LodSection.GridSize * LodSection.GridSize];
        light[7] = LodBlockLight.Pack(12, 3, 4);
        source.BlockLight = light;

        var target = new LodSection();
        target.MergeFrom(source, LodMergeMode.FillMissing);
        c.Eq(light[7], target.BlockLight?[7] ?? (ushort)0, "a merged column brings its light");
    }

    static void Tiles(Check c)
    {
        var levels = new float[32];
        for (int i = 0; i < levels.Length; i++) levels[i] = i / 31f;

        var light = new ushort[LodSection.GridSize * LodSection.GridSize];
        light[1] = LodBlockLight.Pack(31, 0, 0);
        var pixels = new byte[light.Length * 4];
        LodBlockLightTiles.FillPixels(light, levels, pixels);
        c.True(pixels[0] == 0 && pixels[1] == 0 && pixels[2] == 0, "an unlit column is black in its tile");
        c.True(pixels[4] > 200 && pixels[5] > 200 && pixels[6] > 200, "a full white light is bright");

        string shader = File.ReadAllText(Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "assets",
            "dodenslandscapes", "shaderincludes", "lodsurfacefinish.fsh"));
        string vertex = File.ReadAllText(Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "assets",
            "dodenslandscapes", "shaderincludes", "lodterrainbody.vsh"));
        c.True(shader.Contains($"/ {LodBlockLightTiles.AtlasTexels}.0", StringComparison.Ordinal),
            "the shader divides by the tile texture's size");
        c.True(shader.Contains($"vec2({LodBlockLightTiles.TileTexels - 0.5})", StringComparison.Ordinal),
            "and stops half a texel short of a tile's edge");
        c.True(vertex.Contains($"* {LodBlockLightTiles.TileTexels}.0", StringComparison.Ordinal)
                && vertex.Contains($"{LodBlockLightTiles.TilesPerRow}.0", StringComparison.Ordinal),
            "the vertex stage lays tiles out as the texture does");
        c.Eq(32, LodGpuSectionRecord.LightTileOffset + 4, "the tile fills the record's second vec4");
    }

    /// <summary>Soil up to y 100, a light above it at x 3, and one under the ground at x 4.</summary>
    public class LitChunk : DispatchProxy
    {
        int chunkY;

        public static IWorldChunk Create(int chunkY)
        {
            IWorldChunk chunk = DispatchProxy.Create<IWorldChunk, LitChunk>();
            ((LitChunk)(object)chunk).chunkY = chunkY;
            return chunk;
        }

        object? Light(int index, object?[] args)
        {
            int lx = index % 32, y = chunkY * 32 + index / (32 * 32);
            bool lit = (lx == 3 && y == 101) || (lx == 4 && y == 100);
            args[1] = lit ? 5 : 0;
            return lit ? (ushort)(20 << 5 | 7 << 10 | 31) : (ushort)31;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IWorldChunk.UnpackAndReadBlock) =>
                chunkY * 32 + (int)args![0]! / (32 * 32) <= 100 ? Soil : 0,
            nameof(IWorldChunk.Unpack_AndReadLight) => Light((int)args![0]!, args),
            "get_" + nameof(IWorldChunk.Disposed) => false,
            _ => throw new NotSupportedException("capture reached for IWorldChunk." + targetMethod?.Name),
        };
    }
}
