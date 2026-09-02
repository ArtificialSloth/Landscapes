using Vintagestory.API.MathTools;

namespace VintageHorizons;

/// <summary>
/// How much of a colour-mapped block's tint comes from the SEASON map rather than the CLIMATE
/// map - and the fact that the engine answers that question differently on the CPU than it does
/// in the shader that actually draws the world.
///
/// Both paths blend the same two map pixels the same way: `climate * (1 - w) + season * w`,
/// `ColorUtil.ColorOverlay` on the CPU and `mix` in `colormap.fsh`. The weight is the only
/// thing they disagree about, and they disagree a lot.
///
/// **The shader**, `colormap.vsh`:
/// <code>
///   float x = tempRel * 255;
///   float seaLevelDist = worldPos.y - seaLevel;
///   x += max(0.0, seaLevelDist * 1.5);
///   seasonWeight = clamp(0.5 - cos(x / 42.0) / 2.3
///                        + max(0.0, 128 - x) / 256 / 2
///                        - max(0.0, x - 130) / 200, 0.0, 1.0);
/// </code>
///
/// **The CPU**, `ClientWorldMap.ApplyColorMapOnRgba`, read from its IL:
/// <code>
///   w = Clamp(0.5f - GameMath.Cos(temp / 42f) / 2.3f
///             + Math.Max(0, 128 - temp) / 512      // int / int
///             - Math.Max(0, temp - 130) / 200,     // int / int
///             0f, 1f);
/// </code>
///
/// Two differences, both material:
///
/// 1. **Those two corrections are integer divisions and are always exactly zero.**
///    `Max(0, 128 - temp)` cannot exceed 128 and is divided by 512; `Max(0, temp - 130)` cannot
///    exceed 125 and is divided by 200. Over the whole reachable 0..255 range both evaluate to 0,
///    so the CPU applies neither. The shader computes both in floats, where they reach 0.25 and
///    0.625. Checked exhaustively - see SeasonWeightChecks.
///
/// 2. **The CPU has no altitude term.** It passes the raw temperature to the weight. The shader
///    receives the altitude-ADJUSTED temperature and then adds `dist * 1.5` back, trying to
///    recover ground level - so its weight temperature is the raw one shifted by
///    `dist * (1.5 - 1/1.5)`, about +0.83 per block above sea level. The CPU's own
///    `heightAboveSealevel` parameter is real but is spent entirely on the climate map's
///    coordinate, via `Climate.GetAdjustedTemperature`, and never reaches the weight.
///
/// The two agree only near temperature 128 at sea level. They differ by up to 0.39 in weight at
/// sea level, 0.46 eight blocks up, and 0.85 sixty blocks up; two hundred blocks up the shader
/// has taken seasonal colour to zero while the CPU still returns 0.93. The sign flips around
/// temperature 128, so cold and hot ground is wrong in opposite directions - which is exactly the
/// shape that defeats any single global colour correction, and three of those were tried and
/// rejected in play before this was found.
///
/// Nothing here reads a block, a world or an atlas: it is arithmetic, so the checks can hold it
/// against the engine's own numbers without a client.
/// </summary>
public static class LodSeasonWeight
{
    /// <summary>
    /// The weight `colormap.vsh` computes, which is the one the player actually sees.
    /// </summary>
    /// <param name="adjustedTemp">
    /// The altitude-adjusted unscaled temperature, 0-255 - `tempRel * 255` as the shader
    /// receives it, and what `Climate.DescaleTemperature` returns for a position's climate.
    /// </param>
    /// <param name="blocksAboveSeaLevel">
    /// Signed distance from sea level. Only positive values move the result; the shader's own
    /// `max(0.0, ...)` ignores everything below.
    /// </param>
    public static float Vanilla(float adjustedTemp, float blocksAboveSeaLevel)
    {
        float x = adjustedTemp + Math.Max(0f, blocksAboveSeaLevel * 1.5f);
        return GameMath.Clamp(
            0.5f - MathF.Cos(x / 42f) / 2.3f
                 + Math.Max(0f, 128f - x) / 512f
                 - Math.Max(0f, x - 130f) / 200f,
            0f, 1f);
    }

    /// <summary>
    /// The weight `ApplyColorMapOnRgba` computes, which is what the mod's tint currently carries.
    /// The two dead correction terms are written out rather than dropped, so this reads as a
    /// transcription of the engine and a reader can see for themselves that they contribute
    /// nothing.
    /// </summary>
    /// <param name="rawTemp">
    /// The UNADJUSTED unscaled temperature the engine samples from the climate map, 0-255. This
    /// is the altitude-adjusted value plus `blocksAboveSeaLevel / 1.5`.
    /// </param>
    public static float Engine(int rawTemp)
    {
        // GameMath.Cos rather than MathF.Cos on purpose: this has to be what the engine computes,
        // not what it ought to compute, because its answer is divided out below.
        return GameMath.Clamp(
            0.5f - GameMath.Cos(rawTemp / 42f) / 2.3f
                 + Math.Max(0, 128 - rawTemp) / 512
                 - Math.Max(0, rawTemp - 130) / 200,
            0f, 1f);
    }

    /// <summary>
    /// The raw temperature from the altitude-adjusted one. `Climate.GetAdjustedTemperature`
    /// subtracts `dist / 1.5`; this puts it back.
    /// </summary>
    public static float RawFromAdjusted(float adjustedTemp, float blocksAboveSeaLevel) =>
        adjustedTemp + Math.Max(0f, blocksAboveSeaLevel) / 1.5f;

    /// <summary>
    /// What to multiply the engine's SEASONAL SHARE by to turn its answer into the shader's.
    ///
    /// The engine hands back `climate * (1 - wEngine) + season * wEngine`, so the correction is
    /// entirely inside the part that moved away from the climate colour:
    /// <code>
    ///   corrected = climate + (engineAnswer - climate) * (wVanilla / wEngine)
    /// </code>
    /// which needs neither the season pixel nor the season map's row.
    ///
    /// The division is always safe and that is not luck: `0.5 - cos(t/42)/2.3` is bounded by
    /// 0.5 +/- 1/2.3, so the engine's weight lies in [0.065, 0.935] and can reach neither zero
    /// nor the clamp. A check pins it, because everything above depends on it.
    /// </summary>
    public static float SeasonalShareScale(float adjustedTemp, float blocksAboveSeaLevel)
    {
        // Clamped because the engine reads its temperature from a byte and cannot have sampled
        // outside 0..255; a reconstruction that lands outside it is describing a position the
        // engine never saw.
        int raw = Math.Clamp(
            (int)MathF.Round(RawFromAdjusted(adjustedTemp, blocksAboveSeaLevel)), 0, 255);
        float engine = Engine(raw);
        return engine <= 0f ? 1f : Vanilla(adjustedTemp, blocksAboveSeaLevel) / engine;
    }

    /// <summary>
    /// Whether the adjusted temperature can be turned back into the raw one the engine used.
    ///
    /// `Climate.GetAdjustedTemperature` clamps to 0..255, so a cold enough place high enough up
    /// arrives here saturated and the altitude cannot be added back - the original is gone. The
    /// shader is saturated there too, so both sides agree that it is cold; what is lost is the
    /// engine's weight, which used the unsaturated value. Refuse rather than guess.
    /// </summary>
    public static bool CanRecoverRawTemperature(float adjustedTemp, float blocksAboveSeaLevel) =>
        blocksAboveSeaLevel <= 0f || (adjustedTemp > 0.5f && adjustedTemp < 254.5f);

    /// <summary>Tightest bounds the engine's weight can take, for the checks and the guard above.</summary>
    public const float EngineWeightMin = 0.5f - 1f / 2.3f;
    public const float EngineWeightMax = 0.5f + 1f / 2.3f;
}
