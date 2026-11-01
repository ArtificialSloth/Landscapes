namespace VintageHorizons;

/// <summary>
/// One solid tile for a berry bush, layered from the bush's own leaf sheets.
///
/// The game has no texture for a bush. It stacks many planes of see-through sheets, each 5 to 7%
/// leaf, so one sheet on a cube is a few grey specks in the band, and its clear texels store
/// white. Layered in all eight orientations the same sheets cover 60 to 90% of a tile, with the
/// gaps between leaves dark, as they are inside a real bush. The result is variation around the
/// block's colour like any other block texture, so the climate tint still decides the green.
/// Pixels are 0xAARRGGBB. Game-free, so the fast tier can run it.
/// </summary>
internal static class LodBushTile
{
    const int Orientations = 8;

    /// <summary>The first layer's brightness: lower leaves sit in the bush's own shade.</summary>
    const float DeepestLayer = 0.75f;

    /// <summary>The gaps, as a share of the leaves' mean.</summary>
    const float GapShade = 0.55f;

    const int OpaqueAlpha = 127;

    /// <summary>
    /// The tile and its mean, or null with no sheet of the size the first one sets. Sheets of
    /// another size are left out rather than resampled.
    /// </summary>
    public static (int[] Pixels, int Mean)? Compose(IReadOnlyList<(int Width, int Height, int[] Argb)> sheets)
    {
        if (sheets.Count == 0) return null;
        int size = sheets[0].Width;
        if (size <= 0 || sheets[0].Height != size) return null;

        var layered = new int[size * size];
        long leafRed = 0, leafGreen = 0, leafBlue = 0, leaves = 0;
        int layerCount = sheets.Count * Orientations;
        for (int sheet = 0; sheet < sheets.Count; sheet++)
        {
            (int width, int height, int[] argb) = sheets[sheet];
            if (width != size || height != size || argb.Length != size * size) continue;
            for (int orientation = 0; orientation < Orientations; orientation++)
            {
                float t = (sheet * Orientations + orientation) / (float)Math.Max(1, layerCount - 1);
                float light = DeepestLayer + (1f - DeepestLayer) * t;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int sourceX = (orientation & 1) == 0 ? x : y;
                        int sourceY = (orientation & 1) == 0 ? y : x;
                        if ((orientation & 2) != 0) sourceX = size - 1 - sourceX;
                        if ((orientation & 4) != 0) sourceY = size - 1 - sourceY;
                        int pixel = argb[sourceY * size + sourceX];
                        if (((pixel >> 24) & 0xFF) <= OpaqueAlpha) continue;

                        leafRed += (pixel >> 16) & 0xFF;
                        leafGreen += (pixel >> 8) & 0xFF;
                        leafBlue += pixel & 0xFF;
                        leaves++;
                        layered[y * size + x] = Shaded(pixel, light);
                    }
                }
            }
        }
        if (leaves == 0) return null;

        int gap = Shaded(unchecked((int)0xFF000000)
            | (int)(leafRed / leaves) << 16 | (int)(leafGreen / leaves) << 8 | (int)(leafBlue / leaves),
            GapShade);
        long red = 0, green = 0, blue = 0;
        for (int i = 0; i < layered.Length; i++)
        {
            if (layered[i] == 0) layered[i] = gap;
            red += (layered[i] >> 16) & 0xFF;
            green += (layered[i] >> 8) & 0xFF;
            blue += layered[i] & 0xFF;
        }
        long count = layered.Length;
        int mean = unchecked((int)0xFF000000)
            | (int)(red / count) << 16 | (int)(green / count) << 8 | (int)(blue / count);
        return (layered, mean);
    }

    static int Shaded(int pixel, float light)
    {
        int Channel(int shift) => Math.Clamp((int)(((pixel >> shift) & 0xFF) * light), 0, 255) << shift;
        return unchecked((int)0xFF000000) | Channel(16) | Channel(8) | Channel(0);
    }
}
