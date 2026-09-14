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

uniform float fogDensityIn;
uniform float fogMinIn;
uniform float horizonFog;
uniform vec3 sunPosition;
uniform vec3 sunColor;
uniform float dayLight;

uniform float skyTint;
uniform vec4 colorTint;
uniform float lightLevelBias;
uniform float fadeBias;
uniform int seaLevel;

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

float silhouetteBias(float value, float b)
{
    return pow(value, log(0.5) / log(b));
}

vec3 silhouetteBias(vec3 value, float b)
{
    return pow(value, vec3(log(0.5) / log(b)));
}

void main()
{
    if (dist < 0.0 || dist > 1.0) discard;

    // The sky with a height-dependent offset is the terrain's own colour.
    vec4 terraColor = vec4(1.0);
    vec4 terraGlow = vec4(1.0);
    float a = float(seaLevel) + 2.0;
    float b = float(seaLevel) - 1.0;
    float sealevelOffsetFactor = skyTint + ((yLevel - a) / (b - a)) * (-skyTint);
    getSkyColorAt(worldPos.xyz, sunPosition, sealevelOffsetFactor, clamp(dayLight, 0.0, 1.0),
        horizonFog, terraColor, terraGlow);

    // The real sky, for the fade.
    vec4 skyColor = vec4(1.0);
    vec4 skyGlow = vec4(1.0);
    vec3 worldPosInSky = normalize(worldPos.xyz) * 250.0;
    getSkyColorAt(worldPosInSky, sunPosition, 0.25, clamp(dayLight, 0.0, 1.0), horizonFog,
        skyColor, skyGlow);
    float murkiness = max(0.0, getSkyMurkiness() - 14.0 * fogDensityIn);
    skyColor.rgb = applyUnderwaterEffects(skyColor.rgb, murkiness);
    skyGlow.y *= clamp((dayLight - 0.05) * 2.0 - 50.0 * murkiness, 0.0, 1.0);

    terraColor.rgb = mix(terraColor.rgb, colorTint.rgb, colorTint.a);
    terraColor.rgb *= silhouetteBias(clamp(sunColor * dayLight, 0.0, 1.0), lightLevelBias);
    terraColor = applyFog(terraColor, fogAmount);
    terraColor = applySpheresFog(terraColor, fogAmount, worldPos.xyz);
    terraGlow *= dist;

    float fade = min(1.0, silhouetteBias(dist, fadeBias));
    outColor = mix(terraColor, skyColor, fade);
    outGlow = mix(terraGlow, skyGlow, fade);

#if SSAOLEVEL > 0
    outGPosition = vec4(0.0);
    outGNormal = vec4(0.0);
#endif
}
