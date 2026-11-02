using Vintagestory.API.MathTools;

namespace VintageHorizons;

/// <summary>
/// The light of lamps, torches and lava on the top surface of a column, one value per column.
///
/// The game has already worked this out: every cell's light holds what reaches it, stopped by
/// walls and coloured by its source. Capture copies the cell just above a column's top block, so
/// a sealed room lights nothing outside and an open torch counts though the column never
/// records the torch itself (G312). The value keeps the game's level, hue and saturation
/// numbers, because their colours come from tables a server capture has no use for.
/// </summary>
public static class LodBlockLight
{
    const int LevelBits = 5;
    const int HueBits = 6;
    const int LevelMask = (1 << LevelBits) - 1;
    const int HueMask = (1 << HueBits) - 1;

    /// <summary>A column's value, 0 where no block light reaches it.</summary>
    public static ushort Pack(int level, int hue, int saturation) => (ushort)(level == 0 ? 0
        : (level & LevelMask) | (hue & HueMask) << LevelBits | saturation << (LevelBits + HueBits));

    public static int Level(ushort value) => value & LevelMask;

    public static int Hue(ushort value) => (value >> LevelBits) & HueMask;

    public static int Saturation(ushort value) => value >> (LevelBits + HueBits);

    /// <summary>
    /// A value from a chunk's light word and saturation: sunlight in bits 0-4, block light's level
    /// in 5-9 and its hue from 10 up, as WorldMap.GetLightRGBsAsInt reads them.
    /// </summary>
    public static ushort FromChunkLight(ushort word, int saturation) =>
        Pack((word >> 5) & LevelMask, word >> 10, saturation);

    /// <summary>
    /// The colour of a value, as WorldMap.GetLightRGBSVec4f works it out: 0xRRGGBB.
    /// <paramref name="levels"/> is the world's BlockLightLevels.
    /// </summary>
    public static int ToRgb(ushort value, float[] levels) => ColorUtil.HsvToRgb(
        Hue(value) * ColorUtil.HueMul,
        Saturation(value) * ColorUtil.SatMul,
        (int)(levels[Level(value)] * 255f));
}
