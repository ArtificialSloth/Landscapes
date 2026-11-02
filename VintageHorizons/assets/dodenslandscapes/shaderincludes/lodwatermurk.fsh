// Underwater darkening for this mod's own shaders, from an exact picture of where water is.
// Include after fogandlight.fsh and underwatereffects.fsh.
//
// The engine's helpers read its quarter-resolution liquid depth, blurred. Even resolved by
// majority (lodwaterresolve.fsh) that leaves a rim one quarter texel wide at every shore, and
// cached ground under a false rim also loses its atmospheric fog, so at distance it reads as a
// clear, dark spot in the haze. Here cached water comes from the full-resolution image it was
// resolved from, and vanilla water from the quarter snapshot taken before cached water was
// added, filtered as the engine filters its own. The formulas are the engine's, unchanged.

// Immutable vanilla-only depth, never the attached liquid-depth destination. The depth
// and OIT surface passes both read the same snapshot so a clear boundary pixel cannot get
// cached murk coverage without the cached surface that belongs over it.
uniform sampler2D vanillaWaterDepth;
// Cached water at the primary framebuffer's size, 1.0 where there is none.
uniform sampler2D vhCachedWaterDepth;
// 1 when both images belong to this frame; 0 falls back to the engine's helpers.
uniform int vhExactWaterDepth;

// The quarter snapshot at this full-resolution pixel, bilinear as the engine reads liquidDepth.
// texelFetch, because the snapshot's own filter is nearest for the ownership tests' == 1.0.
float vhVanillaWaterDepthAt(vec2 pixel)
{
    ivec2 size = textureSize(vanillaWaterDepth, 0);
    vec2 q = pixel * vec2(size) / vec2(textureSize(vhCachedWaterDepth, 0)) - 0.5;
    ivec2 i = ivec2(floor(q));
    vec2 f = q - floor(q);
    ivec2 last = size - ivec2(1);
    float a = texelFetch(vanillaWaterDepth, clamp(i, ivec2(0), last), 0).r;
    float b = texelFetch(vanillaWaterDepth, clamp(i + ivec2(1, 0), ivec2(0), last), 0).r;
    float c = texelFetch(vanillaWaterDepth, clamp(i + ivec2(0, 1), ivec2(0), last), 0).r;
    float d = texelFetch(vanillaWaterDepth, clamp(i + ivec2(1, 1), ivec2(0), last), 0).r;
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float vhLiquidDepthAt(vec2 pixel)
{
    ivec2 last = textureSize(vhCachedWaterDepth, 0) - ivec2(1);
    float cached = texelFetch(vhCachedWaterDepth, clamp(ivec2(pixel), ivec2(0), last), 0).r;
    return min(vhVanillaWaterDepthAt(pixel), cached);
}

// underwatereffects.fsh's getUnderwaterMurkiness, reading the exact picture.
float vhUnderwaterMurkiness()
{
    if (vhExactWaterDepth == 0) return getUnderwaterMurkiness();
    if (cameraUnderwater > 0.7) return 0.0;
    float ldepth = linearDepth(max(
        vhLiquidDepthAt(gl_FragCoord.xy),
        vhLiquidDepthAt(gl_FragCoord.xy + vec2(0.0, 3.0))));
    float fdepth = linearDepth(gl_FragCoord.z);
    return clamp(max(0.0, fdepth - ldepth) * 350.0, 0.0, 1.0);
}

// underwatereffects.fsh's getSkyMurkiness, reading the exact picture.
float vhSkyMurkiness()
{
    if (vhExactWaterDepth == 0) return getSkyMurkiness();
    if (cameraUnderwater > 0.7) return 0.0;
    float ldepth1 = linearDepth(vhLiquidDepthAt(gl_FragCoord.xy));
    float ldepth2 = linearDepth(vhLiquidDepthAt(gl_FragCoord.xy + vec2(0.0, 3.0)));
    float ldepth3 = linearDepth(vhLiquidDepthAt(gl_FragCoord.xy + vec2(0.0, 6.0)));
    return 1.0 - (ldepth1 + ldepth2 + ldepth3) / 3.0;
}
