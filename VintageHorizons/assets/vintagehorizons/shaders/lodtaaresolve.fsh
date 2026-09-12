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
// A pixel is one of three kinds. Cached terrain: its depth is nearer than the snapshot taken
// before cached terrain drew. Sky: nothing had been drawn there before cached terrain, and
// cached terrain did not draw it either; it joins the blend only beside cached terrain, so the
// terrain's edge against the sky is averaged like the rest. Anything else - vanilla terrain, a
// creature - is never changed, and its history is never used.
//
// 0.4.57 jittered with the camera still, for three measured reasons: it refused history
// wherever the stored depth differed by more than 3%, and the nudge itself swaps a tree top
// for the ground behind it in about one distant pixel in five; a pixel that was sky last frame
// had no history, so the edge against the sky was never averaged; and where the nudge was
// partial the history share was partial too. Now there is no depth test - distant terrain does
// not move, and the neighbourhood clamp handles real change - and history is at full share
// before the nudge begins. With the view still, history lands exactly on itself, so the clamp
// is let go and the share raised, and the eight nudge positions settle to their average.
//
// Output alpha records the kind and the ramp, for the composite and for the next frame:
// 1 + ramp on cached terrain, -(1 + ramp) on sky, 0 on anything else.

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
uniform float stillFeedback;
uniform float stillMotion;
uniform float movingMotion;
uniform int historyValid;

layout(location = 0) out vec4 outResolved;

// View depth from a stored depth value, with the projection's own two depth terms.
float ViewDepth(float depth)
{
    return projectionB / (projectionA + (2.0 * depth - 1.0));
}

// 0 where a block is startPixels or wider on screen, 1 at fullPixels or narrower. History comes
// in over the first half of this ramp; the terrain vertex shader nudges over the second half.
float Ramp(float depth)
{
    float viewDepth = ViewDepth(depth);
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

    bool ours = depth < before;
    bool sky = !ours && before >= 1.0;
    if (!ours && !sky)
    {
        outResolved = vec4(current, 0.0);
        return;
    }

    // One pass over the 3x3: the colour range history is held to while moving, and for a sky
    // pixel the strongest ramp among the cached-terrain pixels beside it.
    float ramp = ours ? Ramp(depth) : 0.0;
    vec3 lo = current;
    vec3 hi = current;
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            ivec2 at = clamp(texel + ivec2(x, y), ivec2(0), size - 1);
            vec3 neighbour = texelFetch(sceneColor, at, 0).rgb;
            lo = min(lo, neighbour);
            hi = max(hi, neighbour);
            if (sky)
            {
                float neighbourDepth = texelFetch(sceneDepth, at, 0).r;
                if (neighbourDepth < texelFetch(beforeDepth, at, 0).r)
                    ramp = max(ramp, Ramp(neighbourDepth));
            }
        }
    }

    vec3 result = current;
    if (ramp > 0.0 && historyValid == 1)
    {
        // Where this pixel's ground was on screen last frame. Distant terrain does not move, so
        // the depth and the two cameras place it; for the sky the depth is the far plane.
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
                // Only history that was cached terrain or sky. Anything else was vanilla
                // terrain or a creature, and blending it in would leave a trail behind it.
                if (abs(texelFetch(history, previousTexel, 0).a) >= 1.0)
                {
                    // How far this ground moved on screen since last frame, in pixels. Still,
                    // history lands exactly on itself and may keep all it has learned. Moving,
                    // it may only say what this frame's neighbourhood could also say - at
                    // distance that neighbourhood holds both the white top and the dark wall,
                    // so the average gets through while a stale colour does not.
                    float motion = length((previousUv - uv) * vec2(size));
                    float moving = smoothstep(stillMotion, movingMotion, motion);
                    vec3 stored = texture(history, previousUv).rgb;
                    vec3 previous = mix(stored, clamp(stored, lo, hi), moving);
                    float share = mix(stillFeedback, feedback, moving) * clamp(2.0 * ramp, 0.0, 1.0);
                    result = mix(current, previous, share);
                }
            }
        }
    }

    // A value that is not a number would otherwise never leave the history while the view is
    // still.
    if (any(isnan(result)) || any(isinf(result))) result = current;

    float kind = 1.0 + ramp;
    outResolved = vec4(result, ours ? kind : -kind);
}
