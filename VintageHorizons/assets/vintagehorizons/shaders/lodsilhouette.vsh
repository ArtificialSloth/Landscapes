#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Horizon silhouettes (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER 4.8), first-look slice.
// The distance fade, the near sink and the globe curve are adapted from Farseer 1.4.0's
// region.vsh (MIT, (c) 2026 Badgerson, https://github.com/ViciousBadger/VSMod-Farseer).
// Differences: the tile origin arrives camera-relative in its own uniform. A per-vertex apron
// slopes only the Farseer edge under cached terrain, and the globe curve starts only where cached
// terrain ends.

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 underlayDataIn;

uniform mat4 viewMatrix;
uniform mat4 projectionMatrix;

// The tile's minimum corner relative to the camera. Vertex heights stay absolute.
uniform vec3 tileOrigin;

uniform vec4 rgbaFogIn;
uniform float fogMinIn;
uniform float fogDensityIn;

// The silhouette reach, and the distance cached terrain draws to.
uniform float farViewDistance;
uniform float globeStart;
uniform float globeEffect;
uniform float underlaySinkBlocks;
uniform float underlayWidthBlocks;
uniform int underlayActive;

out vec4 worldPos;
out float yLevel;
out vec4 rgbaFog;
out float dist;
out float fogAmount;
out float nightVisionStrengthv;
out vec2 tilePos;

#include vertexflagbits.ash
#include colorutil.ash
#include shadowcoords.vsh
#include fogandlight.vsh
#include vertexwarp.vsh

void main()
{
    yLevel = vertexPositionIn.y;
    tilePos = vertexPositionIn.xz;
    worldPos = vec4(vertexPositionIn + tileOrigin, 1.0);
    worldPos = applyGlobalWarping(worldPos);

    float horizontal = length(worldPos.xz);
    // This is a colour/fog ramp, not an ownership radius. Farseer stays available on the near
    // side until a render-ready vanilla or cached-LOD replacement takes over its exact cell.
    float distStart = viewDistance * 0.785;
    dist = max(0.0,
        (horizontal - distStart) / max(1.0, farViewDistance - distStart - 512.0));

    // Only the apron next to actual cached coverage slopes down. The rest of the Farseer surface
    // stays at its sampled height instead of becoming a camera-centred sunken plateau.
    float underlay = 1.0 - smoothstep(
        0.0, max(1.0, underlayWidthBlocks), underlayDataIn.x);
    worldPos.y -= underlaySinkBlocks * underlay * float(underlayActive);

    // Farseer's original near sink: five blocks at the start of the fade, none past 2% of it.
    worldPos.y -= max(0.0, mix(5.0, 0.0, dist * 50.0));

    float globe = clamp((horizontal - globeStart) / max(1.0, farViewDistance - globeStart), 0.0, 1.0);
    worldPos.y -= globeEffect * globe * globe * farViewDistance;

    fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);
    rgbaFog = rgbaFogIn;
    nightVisionStrengthv = nightVisionStrength;

    gl_Position = projectionMatrix * (viewMatrix * worldPos);
}
