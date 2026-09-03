namespace VintageHorizons;

/// <summary>
/// Rainfall as the engine computes it, which is not the number a climate read hands back.
///
/// `Climate.GetRainFall(raw, y)` adds two height terms to the stored rainfall before anything
/// looks at a colour map:
/// <code>
///   raw + (y - Sealevel) / 2 + 5 * clamp(8 + Sealevel - y, 0, 8)
/// </code>
/// clamped to 0..255. The first rises steadily with altitude; the second is a bonus of up to
/// 40 that applies only at and just below sea level and is gone eight blocks above it.
///
/// This matters because the climate field stores one rainfall per cell and the terrain in that
/// cell is at some other height. Reading rainfall at sea level and using it unchanged - which
/// is what 0.3.179 and 0.3.180 did - takes the number at exactly the height where the bonus is
/// largest and then spends it everywhere, which is worth tens of counts on a map that is 256
/// counts wide. The height ramp this replaced never had the bug, because it asked the engine
/// for a finished colour at each height and the engine applied this itself.
///
/// So the field stores the RAW rainfall, recovered by subtracting the offset at the height it
/// was sampled at, and the shader adds the offset back for the height it is drawing.
///
/// Note the sea level here is `Climate.Sealevel`, which is not necessarily the world's: the
/// temperature lapse rate uses the client world map's sea level and this uses the static. They
/// are passed separately rather than assumed equal.
/// </summary>
public static class LodClimateMath
{
    /// <summary>
    /// What <c>GetRainFall</c> adds to the stored rainfall at this height. Integer division,
    /// truncating toward zero, because that is what the engine's does - below sea level -3/2
    /// is -1 there and would be -2 under a floor.
    /// </summary>
    public static int RainfallOffset(int y, int rainSeaLevel) =>
        (y - rainSeaLevel) / 2 + 5 * Math.Clamp(8 + rainSeaLevel - y, 0, 8);

    /// <summary>The engine's rainfall at a height, from the raw stored value.</summary>
    public static int RainfallAt(int rawRainfall, int y, int rainSeaLevel) =>
        Math.Clamp(rawRainfall + RainfallOffset(y, rainSeaLevel), 0, 255);

    /// <summary>
    /// Undo the height terms to get the raw rainfall back out of a climate read.
    ///
    /// Refused where the engine's clamp destroyed the information: a reading of 255 could have
    /// come from any raw value at or above the point of saturation, and inventing one would put
    /// a confident wrong colour on every cell in a wet biome. Those cells are left unknown and
    /// the field's spread fills them from a neighbour that answered.
    /// </summary>
    public static bool TryRawRainfall(float sampled, int sampledAtY, int rainSeaLevel,
        out float rawRainfall)
    {
        int offset = RainfallOffset(sampledAtY, rainSeaLevel);
        rawRainfall = sampled - offset;
        return sampled > 0f && sampled < 255f && rawRainfall >= 0f && rawRainfall <= 255f;
    }
}
