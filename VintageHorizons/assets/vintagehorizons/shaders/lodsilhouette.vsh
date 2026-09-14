#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Horizon silhouettes (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER 4.8), first-look slice.
// The distance fade, the near sink and the globe curve are adapted from Farseer 1.4.0's
// region.vsh (MIT, (c) 2026 Badgerson, https://github.com/ViciousBadger/VSMod-Farseer).
// Differences: the tile origin arrives camera-relative in its own uniform, and the globe
// curve starts where cached terrain ends, so a silhouette filling a gap between explored
// areas stays level with the cached terrain around it.

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

    // Farseer's near sink: five blocks at the start of the fade, none past 2% of it.
    worldPos.y -= max(0.0, mix(5.0, 0.0, dist * 50.0));

    float globe = clamp((horizontal - globeStart) / max(1.0, farViewDistance - globeStart), 0.0, 1.0);
    worldPos.y -= globeEffect * globe * globe * farViewDistance;

    fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);
    rgbaFog = rgbaFogIn;
    nightVisionStrengthv = nightVisionStrength;

    gl_Position = projectionMatrix * (viewMatrix * worldPos);
}
