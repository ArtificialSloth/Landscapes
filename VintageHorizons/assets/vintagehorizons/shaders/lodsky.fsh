#version 430 core

// `.vhsky`: counts sky that still shows below the horizon once all opaque terrain is drawn.
//
// The depth test runs BEFORE this shader, so everything below happens only for pixels that
// nothing opaque has covered. A pixel whose view ray points down, and would meet the lowest
// plausible ground well inside the distance cached terrain is drawn to, should be showing
// ground; if it is still sky, nothing drew there. Every path ends in discard, so the picture
// is never touched - only the counters are.
layout(early_fragment_tests) in;

// Camera-relative with the eye at the origin: the matrices the terrain itself is drawn with.
uniform mat4 inverseViewProjection;
// x, y, width, height of the viewport in window pixels.
uniform vec4 viewportRect;
// The lowest plausible ground relative to the eye (negative when the eye is above it), and how
// far out cached terrain still draws. Sky past that distance is the horizon, not a hole.
uniform float floorRelY;
uniform float reachBlocks;
uniform int tilesX;
uniform int tilesY;

// Word layout mirrors SkyWitnessLayout: 0 counted pixels, 1 horizon past the reach,
// 2-5 bounding box, then one word per screen tile from word 8.
layout(std430, binding = 5) buffer SkyWitnessWords
{
    uint skyWords[];
};

void main()
{
    vec2 unit = (gl_FragCoord.xy - viewportRect.xy) / viewportRect.zw;
    vec2 ndc = unit * 2.0 - 1.0;
    vec4 nearPoint = inverseViewProjection * vec4(ndc, -1.0, 1.0);
    vec4 farPoint = inverseViewProjection * vec4(ndc, 1.0, 1.0);
    vec3 ray = normalize(farPoint.xyz / farPoint.w - nearPoint.xyz / nearPoint.w);
    if (ray.y >= 0.0) discard;

    // A shallow ray meets the floor plane far away. Past the cached terrain's reach that is the
    // ordinary horizon, so it is counted apart and never reported.
    if (floorRelY < 0.0 && length(ray.xz) * (floorRelY / ray.y) > reachBlocks)
    {
        atomicAdd(skyWords[1], 1u);
        discard;
    }

    atomicAdd(skyWords[0], 1u);
    uvec2 pixel = uvec2(max(gl_FragCoord.xy - viewportRect.xy, vec2(0.0)));
    atomicMin(skyWords[2], pixel.x);
    atomicMin(skyWords[3], pixel.y);
    atomicMax(skyWords[4], pixel.x);
    atomicMax(skyWords[5], pixel.y);
    int tx = clamp(int(unit.x * float(tilesX)), 0, tilesX - 1);
    int ty = clamp(int(unit.y * float(tilesY)), 0, tilesY - 1);
    atomicAdd(skyWords[8 + ty * tilesX + tx], 1u);
    discard;
}
