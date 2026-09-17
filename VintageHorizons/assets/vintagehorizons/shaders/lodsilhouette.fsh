#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Horizon silhouettes: the classic colour (owner decision D11), adapted from Farseer 1.4.0's
// region.fsh (MIT, (c) 2026 Badgerson, https://github.com/ViciousBadger/VSMod-Farseer).
// Terrain takes its colour from the sky itself, offset by height relative to sea level, is
// tinted, lit by the sun and the day, fogged, and faded into the real sky at the reach.

in vec4 worldPos;
in float yLevel;
in vec4 rgbaFog;
in float dist;
in float fogAmount;
in float nightVisionStrengthv;
in vec2 tilePos;

uniform float fogDensityIn;
uniform float fogMinIn;
uniform float horizonFog;
uniform vec3 sunPosition;
uniform vec3 sunColor;
uniform float dayLight;

uniform float silhouetteSkyTint;
uniform vec4 silhouetteColorTint;
uniform float silhouetteLightLevelBias;
uniform float silhouetteFadeBias;
uniform int silhouetteSeaLevel;
uniform vec4 replacementCells0;
uniform vec4 replacementCells1;
uniform vec4 replacementCells2;
uniform vec4 replacementCells3;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

#include noise3d.ash
#include dither.fsh
#include fogandlight.fsh
#include skycolor.fsh
#include underwatereffects.fsh
#include lodsilhouettecolor.fsh

bool vhSilhouetteReplaced(ivec2 cell)
{
    if (cell.x < 0 || cell.y < 0 || cell.x >= 16 || cell.y >= 16) return false;
    int bit = cell.y * 16 + cell.x;
    int wordIndex = bit >> 4;
    vec4 replacementGroup = wordIndex < 4 ? replacementCells0
        : wordIndex < 8 ? replacementCells1
        : wordIndex < 12 ? replacementCells2
        : replacementCells3;
    int replacementWord = int(replacementGroup[wordIndex & 3]);
    return (replacementWord & (1 << (bit & 15))) != 0;
}

void main()
{
    if (dist > 1.0) discard;

    // A bit exists only where vanilla has committed render readiness or an exact data-covered
    // 32-block cell has resident level-zero LOD ground. Neither answer depends on camera direction.
    // Unknown and still-loading cells retain Farseer instead of opening onto the sky.
    ivec2 replacementCell = clamp(ivec2(floor(tilePos / 32.0)), ivec2(0), ivec2(15));
    if (vhSilhouetteReplaced(replacementCell)) discard;

    // The sky with a height-dependent offset is the terrain's own colour.
    vec4 terraColor;
    vec4 terraGlow;
    vhSilhouetteTerrainColor(worldPos.xyz, yLevel, sunPosition, sunColor, dayLight,
        horizonFog, fogAmount, silhouetteSkyTint, silhouetteColorTint,
        silhouetteLightLevelBias, terraColor, terraGlow);

    // The real sky, for the fade.
    vec4 skyColor = vec4(1.0);
    vec4 skyGlow = vec4(1.0);
    vec3 worldPosInSky = normalize(worldPos.xyz) * 250.0;
    getSkyColorAt(worldPosInSky, sunPosition, 0.25, clamp(dayLight, 0.0, 1.0), horizonFog,
        skyColor, skyGlow);
    float murkiness = max(0.0, getSkyMurkiness() - 14.0 * fogDensityIn);
    skyColor.rgb = applyUnderwaterEffects(skyColor.rgb, murkiness);
    skyGlow.y *= clamp((dayLight - 0.05) * 2.0 - 50.0 * murkiness, 0.0, 1.0);

    terraGlow *= dist;

    float fade = min(1.0, vhSilhouetteBias(dist, silhouetteFadeBias));
    outColor = mix(terraColor, skyColor, fade);
    outGlow = mix(terraGlow, skyGlow, fade);

#if SSAOLEVEL > 0
    outGPosition = vec4(0.0);
    outGNormal = vec4(0.0);
#endif
}
