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
uniform vec4 coveredCells0;
uniform vec4 coveredCells1;
uniform vec4 coveredCells2;
uniform vec4 coveredCells3;
uniform float cacheFadeStart;

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

void main()
{
    if (dist < 0.0 || dist > 1.0) discard;

    ivec2 coveredCell = clamp(ivec2(floor(tilePos / 32.0)), ivec2(0), ivec2(15));
    int coveredBit = coveredCell.y * 16 + coveredCell.x;
    int wordIndex = coveredBit >> 4;
    vec4 coveredGroup = wordIndex < 4 ? coveredCells0
        : wordIndex < 8 ? coveredCells1
        : wordIndex < 12 ? coveredCells2
        : coveredCells3;
    int coveredWord = int(coveredGroup[wordIndex & 3]);
    bool cachedCell = (coveredWord & (1 << (coveredBit & 15))) != 0;
    if (cachedCell && dot(worldPos.xz, worldPos.xz) < cacheFadeStart * cacheFadeStart) discard;

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
