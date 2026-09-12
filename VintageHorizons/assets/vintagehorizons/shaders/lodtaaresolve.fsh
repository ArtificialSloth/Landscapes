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
// How it got here. 0.4.57 jittered with the camera still: it refused history wherever the
// stored depth differed by more than 3%, which the nudge itself caused in about one distant
// pixel in five; the edge against the sky was never averaged; and the nudge faded in with the
// history instead of after it. 0.4.58 fixed those and let history settle while still; the owner
// found it much steadier, but smeared, even standing still. 0.4.59: history is re-sampled by
// Catmull-Rom instead of bilinear filtering, which softened it a little every frame of a turn;
// the safety check uses the typical colours around a pixel instead of their full range, which
// on snow let nearly anything through; the still memory waits until the whole camera has held
// still; fast turns keep less memory; and the composite sharpens on the way to the screen.
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

// Memory while turning and while still; the per-pixel motion, in pixels per frame, that
// separates still from moving; from how fast a turn keeps only fastShare of its memory; and how
// long the whole camera has held still, eased from 0 to 1.
uniform float feedback;
uniform float stillFeedback;
uniform float stillMotion;
uniform float movingMotion;
uniform float fastMotion;
uniform float fastShare;
uniform float stillness;

// The safety check's width, in standard deviations of the colours around the pixel.
uniform float clipWidth;
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

// History at a point between texels, by Catmull-Rom rather than plain bilinear filtering. While
// the view turns the history is re-sampled every frame; bilinear filtering softens it a little
// each time and that builds up into a smear, where Catmull-Rom keeps the detail. Nine bilinear
// reads stand in for the sixteen texels the curve spans. At a texel centre it is that texel.
vec3 HistoryCatmullRom(vec2 uv, vec2 size)
{
    vec2 position = uv * size;
    vec2 centre = floor(position - 0.5) + 0.5;
    vec2 f = position - centre;
    vec2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    vec2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    vec2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    vec2 w3 = f * f * (-0.5 + 0.5 * f);
    vec2 w12 = w1 + w2;
    vec2 at0 = (centre - 1.0) / size;
    vec2 at12 = (centre + w2 / w12) / size;
    vec2 at3 = (centre + 2.0) / size;
    return (texture(history, vec2(at0.x, at0.y)).rgb * w0.x
            + texture(history, vec2(at12.x, at0.y)).rgb * w12.x
            + texture(history, vec2(at3.x, at0.y)).rgb * w3.x) * w0.y
        + (texture(history, vec2(at0.x, at12.y)).rgb * w0.x
            + texture(history, vec2(at12.x, at12.y)).rgb * w12.x
            + texture(history, vec2(at3.x, at12.y)).rgb * w3.x) * w12.y
        + (texture(history, vec2(at0.x, at3.y)).rgb * w0.x
            + texture(history, vec2(at12.x, at3.y)).rgb * w12.x
            + texture(history, vec2(at3.x, at3.y)).rgb * w3.x) * w3.y;
}

// Pull a colour toward the middle of the box until it is inside, instead of clamping each
// channel on its own, so a pulled-in colour keeps its hue.
vec3 ClipToBox(vec3 colour, vec3 lo, vec3 hi)
{
    vec3 middle = 0.5 * (lo + hi);
    vec3 extent = max(0.5 * (hi - lo), vec3(1e-4));
    vec3 fromMiddle = colour - middle;
    vec3 ratio = abs(fromMiddle) / extent;
    float largest = max(ratio.x, max(ratio.y, ratio.z));
    return largest > 1.0 ? middle + fromMiddle / largest : colour;
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

    // One pass over the 3x3: the colours history is held to while moving, and for a sky pixel
    // the strongest ramp among the cached-terrain pixels beside it.
    float ramp = ours ? Ramp(depth) : 0.0;
    vec3 lo = current;
    vec3 hi = current;
    vec3 sum = vec3(0.0);
    vec3 sumSquares = vec3(0.0);
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            ivec2 at = clamp(texel + ivec2(x, y), ivec2(0), size - 1);
            vec3 neighbour = texelFetch(sceneColor, at, 0).rgb;
            lo = min(lo, neighbour);
            hi = max(hi, neighbour);
            sum += neighbour;
            sumSquares += neighbour * neighbour;
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
                    // How far this ground moved on screen since last frame, in pixels, and how
                    // much of the moving behaviour applies: all of it until the whole camera has
                    // held still for a moment, and again wherever the ground itself moved.
                    float motion = length((previousUv - uv) * vec2(size));
                    float moving = max(1.0 - stillness,
                        smoothstep(stillMotion, movingMotion, motion));

                    // The safety check: while moving, history may only say what this frame's
                    // neighbourhood could also say - within clipWidth standard deviations of its
                    // mean colour, and never outside the colours actually present.
                    vec3 mean = sum / 9.0;
                    vec3 deviation = sqrt(max(sumSquares / 9.0 - mean * mean, vec3(0.0)));
                    vec3 boxLo = max(lo, mean - clipWidth * deviation);
                    vec3 boxHi = min(hi, mean + clipWidth * deviation);

                    vec3 stored = max(HistoryCatmullRom(previousUv, vec2(size)), vec3(0.0));
                    vec3 previous = mix(stored, ClipToBox(stored, boxLo, boxHi), moving);
                    float share = mix(stillFeedback, feedback, moving)
                        * mix(1.0, fastShare, smoothstep(movingMotion, fastMotion, motion))
                        * clamp(2.0 * ramp, 0.0, 1.0);
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
