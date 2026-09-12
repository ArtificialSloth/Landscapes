#version 330 core

// Distant anti-aliasing, step one: blend each distant cached-terrain pixel with where the same
// ground was in earlier frames. See LodTerrainRenderer.Taa.cs.
//
// Past about a thousand blocks one pixel covers several blocks but can only show the one under
// its centre, so any camera movement smaller than a pixel swaps which block that is - snow tops
// against bare walls flip white to dark. The terrain shader nudges distant geometry a different
// fraction of a pixel every frame; this pass lines each pixel up with its own history and
// averages, so over a few frames the pixel settles on what it actually covers.
//
// Only pixels cached terrain drew this frame are touched - their depth is nearer than the
// snapshot taken before cached terrain drew - and only in proportion to how small one block is
// on screen there. Nothing here is a fixed distance.
//
// Output alpha holds the view depth the pixel had, or 0 where it was not cached terrain, so the
// next frame can tell when different ground has moved into a pixel.

uniform sampler2D sceneColor;
uniform sampler2D sceneDepth;
uniform sampler2D beforeDepth;
uniform sampler2D history;

uniform mat4 invProjection;
uniform mat4 invView;
uniform mat4 prevViewProjection;
uniform vec3 cameraDelta;
uniform float projectionA;
uniform float projectionB;
uniform float blockScale;
uniform float startPixels;
uniform float fullPixels;
uniform float feedback;
uniform int historyValid;

layout(location = 0) out vec4 outResolved;

// View depth from a stored depth value, with the projection's own two depth terms.
float ViewDepth(float depth)
{
    return projectionB / (projectionA + (2.0 * depth - 1.0));
}

// 0 where a block is startPixels or wider on screen, 1 at fullPixels or narrower. The terrain
// vertex shader nudges by the same ramp.
float Weight(float viewDepth)
{
    if (blockScale <= 0.0 || viewDepth <= 0.0) return 0.0;
    float blockPixels = blockScale / viewDepth;
    return clamp((startPixels - blockPixels) / max(startPixels - fullPixels, 0.001), 0.0, 1.0);
}

void main()
{
    ivec2 size = textureSize(sceneColor, 0);
    ivec2 texel = ivec2(gl_FragCoord.xy);
    vec3 current = texelFetch(sceneColor, texel, 0).rgb;
    float depth = texelFetch(sceneDepth, texel, 0).r;
    float before = texelFetch(beforeDepth, texel, 0).r;

    // Nearer than what stood here before cached terrain drew means cached terrain drew it.
    if (!(depth < before))
    {
        outResolved = vec4(current, 0.0);
        return;
    }

    float viewDepth = ViewDepth(depth);
    float weight = Weight(viewDepth);
    vec3 result = current;

    if (weight > 0.0 && historyValid == 1)
    {
        // Where this pixel's ground was on screen last frame. Distant terrain does not move, so
        // the depth and the two cameras place it exactly.
        vec2 uv = (vec2(texel) + 0.5) / vec2(size);
        vec4 view = invProjection * vec4(uv * 2.0 - 1.0, 2.0 * depth - 1.0, 1.0);
        view /= view.w;
        vec3 relative = (invView * view).xyz + cameraDelta;
        vec4 previousClip = prevViewProjection * vec4(relative, 1.0);
        if (previousClip.w > 0.0)
        {
            vec2 previousUv = previousClip.xy / previousClip.w * 0.5 + 0.5;
            if (all(greaterThanEqual(previousUv, vec2(0.0)))
                && all(lessThanEqual(previousUv, vec2(1.0))))
            {
                ivec2 previousTexel = clamp(ivec2(previousUv * vec2(size)), ivec2(0), size - 1);
                float storedDepth = texelFetch(history, previousTexel, 0).a;

                // Different ground stood there last frame - a nearer hill moved off it, or it
                // was not ours at all - so its history describes something else.
                if (storedDepth > 0.0
                    && abs(storedDepth - previousClip.w) <= previousClip.w * 0.03 + 0.5)
                {
                    // History may only say what this frame's neighbourhood could also say. At
                    // distance that neighbourhood holds both the white top and the dark wall, so
                    // the clamp lets the average through while still refusing a stale colour.
                    vec3 lo = current;
                    vec3 hi = current;
                    for (int y = -1; y <= 1; y++)
                    {
                        for (int x = -1; x <= 1; x++)
                        {
                            vec3 neighbour = texelFetch(sceneColor,
                                clamp(texel + ivec2(x, y), ivec2(0), size - 1), 0).rgb;
                            lo = min(lo, neighbour);
                            hi = max(hi, neighbour);
                        }
                    }
                    vec3 previous = clamp(texture(history, previousUv).rgb, lo, hi);
                    result = mix(current, previous, feedback * weight);
                }
            }
        }
    }

    outResolved = vec4(result, viewDepth);
}
