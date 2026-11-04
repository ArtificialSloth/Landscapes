using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace VintageHorizons;

/// <summary>
/// Dense Leaves on every install (owner, 2026-11-03).
///
/// <para>
/// The dense pictures used to be inserted into the block atlas after the game had built it, which
/// puts a picture on the first page with room. A fresh install's 4096 x 2048 atlas is two pages
/// with the first one full, so they landed on the second, which textured terrain cannot read, and
/// the owner's RTX 3060 Ti machine built none at all.
/// </para>
/// <para>
/// So every leaf picture's dense version is now made while the game loads its assets, on the main
/// thread before its loading threads start, and handed to it as an asset of this mod. Just before
/// the game collects block textures, every leaf block gains one more texture naming its dense
/// picture. The game then packs it right after the leaf picture it was made from - on the same
/// page, with its own mipmaps - and nothing it draws uses it. A leaf whose picture lies outside
/// block/plant/leaves gets none, and falls back to the late insert.
/// </para>
/// </summary>
internal sealed class LodDenseLeafAssets : IDisposable
{
    internal const string HarmonyId = "dodenslandscapes.denseleaves";
    internal const string TextureKey = "dodenslandscapes-dense";
    const string SourceFolder = "textures/block/plant/leaves/";

    static LodDenseLeafAssets? active;

    readonly ICoreClientAPI capi;
    readonly Harmony harmony = new(HarmonyId);

    /// <summary>
    /// A berry bush's layered picture (LodBushTile.ComposePlant), under its own key. It had the same
    /// fault: made after the atlas was built, it went to a second page and the bush stayed a cube
    /// (owner, 2026-11-03, currants on the two-page RTX 3060 Ti machine).
    /// </summary>
    internal const string BushTextureKey = "dodenslandscapes-bushsheet";

    // A leaf picture as a block names it (domain:block/plant/leaves/...) to its dense picture's.
    readonly Dictionary<string, AssetLocation> denseBySource = new(StringComparer.Ordinal);

    // A bush's leaf folder (domain:block/.../leaves) to its layered picture, and that picture's
    // mean as the composition gave it: unshaded, so the lower layers keep their shade.
    readonly Dictionary<string, AssetLocation> bushByFolder = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> bushMeans = new(StringComparer.Ordinal);
    MethodBase? patched;

    /// <summary>A packed bush picture's mean, 0xAARRGGBB, or null for a picture this did not make.</summary>
    internal int? BushMean(AssetLocation? texture) =>
        texture != null && bushMeans.TryGetValue(texture.ToString(), out int mean) ? mean : null;

    internal LodDenseLeafAssets(ICoreClientAPI capi) => this.capi = capi;

    /// <summary>Hooks the game's block texture collection. Fails open: no hook, no dense pictures.</summary>
    internal void Install()
    {
        MethodInfo? collect = AccessTools.Method(typeof(BlockTextureAtlasManager), "CollectTextures");
        MethodInfo? before = AccessTools.Method(typeof(LodDenseLeafAssets), nameof(BeforeCollect));
        if (collect == null || before == null)
        {
            capi.Logger.Warning("[Landscapes] Dense Leaves could not find the game's texture collection; "
                + "leaves keep the late picture, which a two-page atlas cannot show.");
            return;
        }

        try
        {
            harmony.Patch(collect, prefix: new HarmonyMethod(before));
            patched = collect;
            active = this;
        }
        catch (Exception e)
        {
            capi.Logger.Warning("[Landscapes] Dense Leaves could not hook texture collection: {0}", e.Message);
        }
    }

    /// <summary>Makes every leaf picture's dense version, while the game loads its assets.</summary>
    internal void MakePictures()
    {
        if (patched == null) return;
        try
        {
            foreach (IAsset asset in capi.Assets.GetMany(SourceFolder, null, true))
            {
                AssetLocation file = asset.Location;
                if (!file.Path.EndsWith(".png", StringComparison.Ordinal) || asset.Data == null) continue;

                int width, height;
                int[] argb;
                using (BitmapExternal bitmap = capi.Render.BitmapCreateFromPng(asset.Data))
                {
                    width = bitmap.Width;
                    height = bitmap.Height;
                    argb = bitmap.Pixels;
                }
                if (width != height || argb == null || argb.Length != width * height) continue;
                if (LodBushTile.ComposeDenseLeaves(width, argb) is not { } dense) continue;

                // block/plant/leaves/large/oak1 as the block names it; the dense one keeps the
                // source's domain in its own path so two mods' "oak1" cannot collide.
                string named = file.Path["textures/".Length..^".png".Length];
                string relative = named["block/plant/leaves/".Length..];
                var texture = new AssetLocation("dodenslandscapes",
                    "block/denseleaves/" + file.Domain + "/" + relative);
                var denseFile = new AssetLocation("dodenslandscapes", "textures/" + texture.Path + ".png");
                capi.Assets.Add(denseFile, new Asset(EncodePng(width, height, dense.Pixels), denseFile, null));
                denseBySource[file.Domain + ":" + named] = texture;
            }
        }
        catch (Exception e)
        {
            capi.Logger.Warning("[Landscapes] Dense Leaves stopped making pictures: {0}", e.Message);
        }

        MakeBushPictures();
    }

    /// <summary>
    /// Every folder holding a bush's leaf sheets, layered as VintageHorizonsModSystem's late tiles
    /// were: centre then inside, 1 to 3, so the picture is the same one, now packed in time.
    /// </summary>
    void MakeBushPictures()
    {
        try
        {
            var folders = new SortedSet<string>(StringComparer.Ordinal);
            foreach (AssetLocation location in capi.Assets.GetLocations("textures/block/", null))
            {
                if (!location.Path.EndsWith("/leavescenter1.png", StringComparison.Ordinal)) continue;
                folders.Add(location.Domain + ":" + location.Path["textures/".Length..location.Path.LastIndexOf('/')]);
            }

            foreach (string folder in folders)
            {
                int colon = folder.IndexOf(':');
                string domain = folder[..colon], path = folder[(colon + 1)..];
                var sheets = new List<(int Width, int Height, int[] Argb)>();
                foreach (string part in new[] { "center", "inside" })
                {
                    for (int n = 1; n <= 3; n++)
                    {
                        IAsset? sheet = capi.Assets.TryGet(new AssetLocation(domain, $"textures/{path}/leaves{part}{n}.png"));
                        if (sheet?.Data == null) continue;
                        using BitmapExternal bitmap = capi.Render.BitmapCreateFromPng(sheet.Data);
                        if (bitmap.Pixels == null || bitmap.Pixels.Length != bitmap.Width * bitmap.Height) continue;
                        sheets.Add((bitmap.Width, bitmap.Height, (int[])bitmap.Pixels.Clone()));
                    }
                }
                if (sheets.Count == 0 || LodBushTile.ComposePlant(sheets) is not { } tile) continue;

                int size = sheets[0].Width;
                var texture = new AssetLocation("dodenslandscapes", "block/bushsheets/" + domain + "/" + path);
                var file = new AssetLocation("dodenslandscapes", "textures/" + texture.Path + ".png");
                capi.Assets.Add(file, new Asset(EncodePng(size, size, tile.Pixels), file, null));
                bushByFolder[folder] = texture;
                bushMeans[texture.ToString()] = tile.Mean;
            }
        }
        catch (Exception e)
        {
            capi.Logger.Warning("[Landscapes] Bush sheets stopped making pictures: {0}", e.Message);
        }
    }

    static void BeforeCollect(BlockTextureAtlasManager __instance, IList<Block> blocks)
    {
        try
        {
            active?.ReserveFirst(__instance);
            active?.GiveBlocks(blocks);
        }
        catch (Exception e)
        {
            active?.capi.Logger.Warning("[Landscapes] Dense Leaves could not name its pictures: {0}", e.Message);
        }
    }

    /// <summary>
    /// Registers every picture this made before any block's: the game packs in the order it first
    /// hears of a texture, so they open the first page, the one textured terrain reads, whichever
    /// page the blocks' own pictures end up on. Blocks naming them later reuse these entries.
    /// </summary>
    void ReserveFirst(BlockTextureAtlasManager atlas)
    {
        foreach (AssetLocation texture in denseBySource.Values.Concat(bushByFolder.Values))
            atlas.GetOrAddTextureLocation(new AssetLocationAndSource(texture));
    }

    /// <summary>
    /// Names each leaf block's dense picture among its textures. Runs on the game's loading thread
    /// and only touches the blocks it is handed; the assets themselves were added earlier.
    /// </summary>
    void GiveBlocks(IList<Block> blocks)
    {
        if (denseBySource.Count == 0 && bushByFolder.Count == 0) return;
        int given = 0, bushes = 0;
        foreach (Block block in blocks)
        {
            if (block?.Code == null || block.Textures == null) continue;
            if (LodBlockPolicy.IsBerryBush(block))
            {
                if (block.Textures.ContainsKey(BushTextureKey)) continue;
                if (BushFolderOf(block) is not { } folder
                    || !bushByFolder.TryGetValue(folder, out AssetLocation? sheet)) continue;
                block.Textures[BushTextureKey] = new CompositeTexture(sheet);
                bushes++;
                continue;
            }
            if (block.BlockMaterial != EnumBlockMaterial.Leaves || block.Textures.ContainsKey(TextureKey)) continue;
            if (DenseFor(SheetOf(block)) is not { } dense) continue;
            block.Textures[TextureKey] = new CompositeTexture(dense);
            given++;
        }
        capi.Logger.Notification("[Landscapes] Dense Leaves: {0} pictures made, {1} leaf blocks given one; "
            + "bush sheets: {2} pictures made, {3} bush blocks given one.",
            denseBySource.Count, given, bushByFolder.Count, bushes);
    }

    /// <summary>
    /// The folder a bush's leaf sheets live in, read off its particle texture, as the late tiles
    /// were keyed (VintageHorizonsModSystem.BushLeafFolder), with its domain.
    /// </summary>
    static string? BushFolderOf(Block block)
    {
        if (!block.Textures.TryGetValue("particle", out CompositeTexture? particle)) return null;
        AssetLocation? source = particle?.Base;
        int end = source?.Path.LastIndexOf('/') ?? -1;
        return end > 0 ? source!.Domain + ":" + source.Path[..end] : null;
    }

    /// <summary>
    /// The picture this mod reads a leaf's colour and sheet from: a named top face, else the one the
    /// game picks for the block colour - its attribute's, else the first. The same rule as
    /// VintageHorizonsModSystem.ColorFace, read before the game has baked anything.
    /// </summary>
    static CompositeTexture? SheetOf(Block block)
    {
        foreach (string name in VintageHorizonsModSystem.UpFaceTextureNames)
            if (block.Textures.TryGetValue(name, out CompositeTexture? named)) return named;
        string? attribute = block.Attributes?["textureCodeForBlockColor"].AsString();
        if (attribute != null && block.Textures.TryGetValue(attribute, out CompositeTexture? chosen)) return chosen;
        foreach (KeyValuePair<string, CompositeTexture> first in block.Textures) return first.Value;
        return null;
    }

    /// <summary>
    /// The dense picture for a texture, or null. A name ending in '*' is the game's set of numbered
    /// alternates; the first of them stands for the set, as one species' pictures are alike.
    /// </summary>
    AssetLocation? DenseFor(CompositeTexture? sheet)
    {
        AssetLocation? source = sheet?.Base;
        if (source == null) return null;
        string name = source.Domain + ":" + source.Path;
        if (!name.EndsWith('*')) return denseBySource.GetValueOrDefault(name);

        string prefix = name[..^1];
        string? best = null;
        foreach (string key in denseBySource.Keys)
            if (key.StartsWith(prefix, StringComparison.Ordinal)
                && (best == null || string.CompareOrdinal(key, best) < 0)) best = key;
        return best == null ? null : denseBySource[best];
    }

    /// <summary>
    /// A PNG of 0xAARRGGBB pixels: unfiltered rows, zlib-deflated. Only the game's own decoder reads
    /// it, so nothing beyond what any PNG reader needs.
    /// </summary>
    internal static byte[] EncodePng(int width, int height, int[] argb)
    {
        var raw = new byte[height * (1 + width * 4)];
        int o = 0;
        for (int y = 0; y < height; y++)
        {
            raw[o++] = 0;
            for (int x = 0; x < width; x++)
            {
                int pixel = argb[y * width + x];
                raw[o++] = (byte)(pixel >> 16);
                raw[o++] = (byte)(pixel >> 8);
                raw[o++] = (byte)pixel;
                raw[o++] = (byte)(pixel >> 24);
            }
        }

        byte[] deflated;
        using (var packed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(raw);
            deflated = packed.ToArray();
        }

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bits per channel
        header[9] = 6; // RGBA
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", deflated);
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    static void WriteChunk(Stream png, string type, byte[] data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
        png.Write(four);
        byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);
        uint crc = Crc32(typeBytes, 0xFFFFFFFFu);
        crc = Crc32(data, crc) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(four, crc);
        png.Write(four);
    }

    static readonly uint[] CrcTable = BuildCrcTable();

    static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    internal static uint Crc32(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public void Dispose()
    {
        if (patched != null)
        {
            try { harmony.Unpatch(patched, HarmonyPatchType.All, HarmonyId); }
            catch { /* the game is going down; nothing further depends on it */ }
            patched = null;
        }
        if (ReferenceEquals(active, this)) active = null;
    }
}
