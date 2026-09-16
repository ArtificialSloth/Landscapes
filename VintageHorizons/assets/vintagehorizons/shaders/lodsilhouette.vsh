#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Horizon silhouettes (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER 4.8), first-look slice.
// The distance fade, the near sink and the globe curve are adapted from Farseer 1.4.0's
// region.vsh (MIT, (c) 2026 Badgerson, https://github.com/ViciousBadger/VSMod-Farseer).
// Differences: the tile origin arrives camera-relative in its own uniform. Near Farseer terrain
// is a buried underlay behind cached terrain; it returns to its sampled height before the cache
// fades, and the globe curve starts only where cached terrain ends.

layout(location = 0) in vec3 vertexPositionIn;

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
uniform float cacheFadeStart;
uniform float underlaySinkBlocks;

out vec4 worldPos;
out float yLevel;
out vec4 rgbaFog;
out float dist;
out float fogAmount;
out float nightVisionStrengthv;

#include vertexflagbits.ash
#include colorutil.ash
#include shadowcoords.vsh
#include fogandlight.vsh
#include vertexwarp.vsh

void main()
{
    yLevel = vertexPositionIn.y;
    worldPos = vec4(vertexPositionIn + tileOrigin, 1.0);
    worldPos = applyGlobalWarping(worldPos);

    float horizontal = length(worldPos.xz);
    float distStart = viewDistance * 0.785;
    dist = (horizontal - distStart) / max(1.0, farViewDistance - distStart - 512.0);

    // A partial Farseer tile continues behind nearby cached terrain instead of trying to meet its
    // edge. Bury it deeply, then recover smoothly before cached terrain starts fading into it.
    float underlayRamp = max(1.0, min(512.0, cacheFadeStart - distStart));
    float underlay = 1.0 - smoothstep(
        cacheFadeStart - underlayRamp, cacheFadeStart, horizontal);
    worldPos.y -= underlaySinkBlocks * underlay;

    // Farseer's original near sink: five blocks at the start of the fade, none past 2% of it.
    worldPos.y -= max(0.0, mix(5.0, 0.0, dist * 50.0));

    float globe = clamp((horizontal - globeStart) / max(1.0, farViewDistance - globeStart), 0.0, 1.0);
    worldPos.y -= globeEffect * globe * globe * farViewDistance;

    fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);
    rgbaFog = rgbaFogIn;
    nightVisionStrengthv = nightVisionStrength;

    gl_Position = projectionMatrix * (viewMatrix * worldPos);
}
