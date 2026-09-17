#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Layered fog cards around the exact Farseer/real-terrain frontier. Geometry gives the fog
// height and depth; the fragment shader breaks the regular sheets into one continuous cloud.

layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec2 fogDataIn;

uniform mat4 viewMatrix;
uniform mat4 projectionMatrix;
uniform vec3 volumeOrigin;
uniform float volumeWorldOriginX;
uniform float volumeWorldOriginZ;
uniform float farViewDistance;
uniform float globeStart;
uniform float globeEffect;

out vec3 fogWorldPosition;
out float fogWeight;

#include vertexflagbits.ash
#include colorutil.ash
#include shadowcoords.vsh
#include fogandlight.vsh
#include vertexwarp.vsh

void main()
{
    fogWorldPosition = vec3(
        vertexPositionIn.x + volumeWorldOriginX,
        vertexPositionIn.y,
        vertexPositionIn.z + volumeWorldOriginZ);
    fogWeight = fogDataIn.x;

    vec4 relative = vec4(vertexPositionIn + volumeOrigin, 1.0);
    relative = applyGlobalWarping(relative);
    float horizontal = length(relative.xz);
    float globe = clamp(
        (horizontal - globeStart) / max(1.0, farViewDistance - globeStart), 0.0, 1.0);
    relative.y -= globeEffect * globe * globe * farViewDistance;
    gl_Position = projectionMatrix * (viewMatrix * relative);
}
