namespace VintageHorizons;

/// <summary>
/// One see-through picture of a plant the game crosses several sparse sheets for, layered from
/// its own art: a berry bush, fennel, licorice.
///
/// A bush stacks many planes of sheets each 5 to 7% leaf, so one sheet on a plane is a few grey
/// specks and its clear texels store white. Layered, they read as a bush. The result is variation
/// around the block's colour like any other block texture, so the climate tint still decides the
/// green. Pixels are 0xAARRGGBB. Game-free, so the fast tier can run it.
/// </summary>
internal static class LodBushTile
{
    /// <summary>Upright and mirrored: a plant cannot be turned over.</summary>
    static readonly int[] PlantOrientations = { 0, 2 };

    /// <summary>The first layer's brightness: lower leaves sit in the bush's own shade.</summary>
    const float DeepestLayer = 0.75f;

    const int OpaqueAlpha = 127;

    /// <summary>
    /// The picture and its mean, or null with no sheet of the size the first one sets. Sheets of
    /// another size are left out rather than resampled. It stays upright, and its clear pixels
    /// store the leaves' mean, so mips stir no foreign colour in.
    /// </summary>
    public static (int[] Pixels, int Mean)? ComposePlant(IReadOnlyList<(int Width, int Height, int[] Argb)> sheets)
    {
        int[] orientations = PlantOrientations;
        if (sheets.Count == 0) return null;
        int size = sheets[0].Width;
        if (size <= 0 || sheets[0].Height != size) return null;

        var layered = new int[size * size];
        long leafRed = 0, leafGreen = 0, leafBlue = 0, leaves = 0;
        int layerCount = sheets.Count * orientations.Length;
        for (int sheet = 0; sheet < sheets.Count; sheet++)
        {
            (int width, int height, int[] argb) = sheets[sheet];
            if (width != size || height != size || argb.Length != size * size) continue;
            for (int turn = 0; turn < orientations.Length; turn++)
            {
                int orientation = orientations[turn];
                float t = (sheet * orientations.Length + turn) / (float)Math.Max(1, layerCount - 1);
                float light = DeepestLayer + (1f - DeepestLayer) * t;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int sourceX = (orientation & 2) != 0 ? size - 1 - x : x;
                        int pixel = argb[y * size + sourceX];
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

        int leafMean = (int)(leafRed / leaves) << 16 | (int)(leafGreen / leaves) << 8
            | (int)(leafBlue / leaves);
        for (int i = 0; i < layered.Length; i++)
            if (layered[i] == 0) layered[i] = leafMean;
        return (layered, unchecked((int)0xFF000000) | leafMean);
    }

    /// <summary>
    /// A mean as the texture table stores colours, red in the low byte. A tile's pixels and mean
    /// are 0xAARRGGBB like the art they came from, and grey bushes never showed the difference.
    /// </summary>
    public static int ToStoredColor(int argb) => unchecked((int)0xFF000000)
        | (argb & 0xFF) << 16 | (argb & 0xFF00) | ((argb >> 16) & 0xFF);

    static int Shaded(int pixel, float light)
    {
        int Channel(int shift) => Math.Clamp((int)(((pixel >> shift) & 0xFF) * light), 0, 255) << shift;
        return unchecked((int)0xFF000000) | Channel(16) | Channel(8) | Channel(0);
    }
}
