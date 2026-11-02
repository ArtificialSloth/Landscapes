using OpenTK.Graphics.OpenGL4;

namespace VintageHorizons;

/// <summary>
/// The light of every lit section, one 64 by 64 tile per section in a single texture.
///
/// A tile is one texel per column at any level, so the shader finds a fragment's light from its
/// place in the section alone, and a section with no light has no tile and pays one comparison.
/// Lit sections are about one in a hundred. A tile belongs to a place rather than to a mesh, so
/// it is written when a section is sent to be meshed, and the mesh that follows finds it there.
/// The texture is only made once there is a lit section to put in it.
/// </summary>
internal sealed class LodBlockLightTiles : IDisposable
{
    public const int TileTexels = LodSection.GridSize;
    public const int AtlasTexels = 2048;
    public const int TilesPerRow = AtlasTexels / TileTexels;
    public const int MaxTiles = TilesPerRow * TilesPerRow;

    readonly int unit;
    readonly Dictionary<long, (int Tile, ushort[] Light)> tiles = new();
    readonly Stack<int> free = new(MaxTiles);
    readonly byte[] pixels = new byte[TileTexels * TileTexels * 4];

    public LodBlockLightTiles(int unit)
    {
        this.unit = unit;
        FreeEverything();
    }

    /// <summary>The texture, 0 until a section first needed it.</summary>
    public int TextureId { get; private set; }

    /// <summary>Lit sections held now, and those with no tile left for them, which stay unlit.</summary>
    public int Count => tiles.Count;
    public int Refused { get; private set; }

    /// <summary>A section's tile for the record: its number plus one, 0 with none.</summary>
    public float TileNumber(long key) =>
        tiles.Count != 0 && tiles.TryGetValue(key, out var held) ? held.Tile + 1 : 0f;

    /// <summary>
    /// Hold a section's light. <see cref="LodSection.BlockLight"/> is replaced whole when it
    /// changes, so the same array means the tile is already right. Owning thread only.
    /// </summary>
    public void Set(long key, ushort[]? light, float[] levels)
    {
        if (light == null)
        {
            Release(key);
            return;
        }

        bool had = tiles.TryGetValue(key, out var held);
        if (had && ReferenceEquals(held.Light, light)) return;
        if (!had && free.Count == 0)
        {
            Refused++;
            return;
        }

        int tile = had ? held.Tile : free.Pop();
        FillPixels(light, levels, pixels);
        Upload(tile);
        tiles[key] = (tile, light);
    }

    public void Release(long key)
    {
        if (tiles.Remove(key, out var held)) free.Push(held.Tile);
    }

    public void Clear()
    {
        tiles.Clear();
        FreeEverything();
    }

    public void Dispose()
    {
        Clear();
        if (TextureId == 0) return;
        GL.DeleteTexture(TextureId);
        TextureId = 0;
    }

    /// <summary>A section's light as RGBA, one texel per column. Game-free, so the fast tier can run it.</summary>
    public static void FillPixels(ushort[] light, float[] levels, byte[] pixels)
    {
        for (int col = 0; col < light.Length; col++)
        {
            int rgb = light[col] != 0 ? LodBlockLight.ToRgb(light[col], levels) : 0;
            pixels[col * 4] = (byte)(rgb >> 16);
            pixels[col * 4 + 1] = (byte)(rgb >> 8);
            pixels[col * 4 + 2] = (byte)rgb;
            pixels[col * 4 + 3] = 255;
        }
    }

    void FreeEverything()
    {
        free.Clear();
        for (int tile = MaxTiles - 1; tile >= 0; tile--) free.Push(tile);
    }

    void Upload(int tile)
    {
        // The unit is made current first, for the reason LodTerrainRenderer.UploadOwnTexture gives.
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        if (TextureId == 0)
        {
            TextureId = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, TextureId);
            GL.TexParameter(TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,
                TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D,
                TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8,
                AtlasTexels, AtlasTexels, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        }

        GL.BindTexture(TextureTarget.Texture2D, TextureId);
        GL.TexSubImage2D(TextureTarget.Texture2D, 0,
            tile % TilesPerRow * TileTexels, tile / TilesPerRow * TileTexels, TileTexels, TileTexels,
            PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
    }
}
