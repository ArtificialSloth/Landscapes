using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageHorizons;

/// <summary>
/// The seam fog's colour (owner, 2026-10-18): the game's own fog colour, but steadier. The game
/// blends its fog colour from the base and a list of ambient modifiers in order
/// (AmbientManager.UpdateAmbient, decompiled 1.22.7), then darkens it by the blended scene and
/// fog brightness. Several of those modifiers describe only where the player stands: the
/// weather overhead, a desert storm, a lightning flash, and "blackfogincaves", which pulls the
/// fog to black when little sunlight reaches the player or they are below sea level. Vanilla's
/// fog is thin and near, so it hides that; a bank on the horizon shows the whole sky's worth
/// of fog turning grey the moment a storm passes over the player.
///
/// This repeats the game's blend with those local modifiers at a share of their weight, so a
/// storm or a cave still darkens the fog a little while time of day ("night", "sunglow"), the
/// world's saved ambient and the colour of water or lava over the player's eyes count whole.
/// </summary>
internal static class LodSteadyFogColour
{
    /// <summary>The ambient modifiers that describe only the player's surroundings.</summary>
    internal static readonly string[] LocalModifiers =
        { "weather", "desertstorm", "lightningambient", "blackfogincaves" };

    /// <summary>How much of the local modifiers' weight the seam fog takes (owner: "only part").</summary>
    internal const float LocalShare = 0.3f;

    internal static bool IsLocal(string name) => Array.IndexOf(LocalModifiers, name) >= 0;

    /// <summary>
    /// The game's fog colour blend with the local modifiers' weights scaled by
    /// <paramref name="localShare"/>; 1 reproduces the game's own colour. Writes into
    /// <paramref name="into"/> so a frame allocates nothing.
    /// </summary>
    internal static Vec4f Blend(AmbientModifier baseModifier,
        IEnumerable<KeyValuePair<string, AmbientModifier>> modifiers, float localShare, Vec4f into)
    {
        float[] baseColour = baseModifier.FogColor.Value;
        float r = baseColour[0], g = baseColour[1], b = baseColour[2];
        float scene = baseModifier.SceneBrightness.Value;
        float fogBrightness = baseModifier.FogBrightness.Value;
        float share = Math.Clamp(localShare, 0f, 1f);
        foreach (KeyValuePair<string, AmbientModifier> pair in modifiers)
        {
            AmbientModifier modifier = pair.Value;
            if (modifier == null) continue;
            float scale = IsLocal(pair.Key) ? share : 1f;
            if (modifier.FogColor != null && modifier.FogColor.Value is { Length: >= 3 } colour)
            {
                float w = modifier.FogColor.Weight * scale;
                r = w * colour[0] + (1f - w) * r;
                g = w * colour[1] + (1f - w) * g;
                b = w * colour[2] + (1f - w) * b;
            }
            if (modifier.SceneBrightness != null)
            {
                float w = modifier.SceneBrightness.Weight * scale;
                scene = w * modifier.SceneBrightness.Value + (1f - w) * scene;
            }
            if (modifier.FogBrightness != null)
            {
                float w = modifier.FogBrightness.Weight * scale;
                fogBrightness = w * modifier.FogBrightness.Value + (1f - w) * fogBrightness;
            }
        }
        float brightness = scene * fogBrightness;
        into.Set(r * brightness, g * brightness, b * brightness, 1f);
        return into;
    }
}
