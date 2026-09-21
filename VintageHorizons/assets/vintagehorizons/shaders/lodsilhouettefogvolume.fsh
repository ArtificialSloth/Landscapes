#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// World-anchored, slowly drifting density turns the crossing fog sheets into an irregular
// volume. It never moves with camera yaw, so the cloud reads as part of the landscape.

in vec3 fogWorldPosition;
in float fogWeight;
in float fogProfile;

uniform vec4 rgbaFogIn;
uniform float fogStrength;
uniform float fogCoverage;
uniform float fogBillowSizeBlocks;
uniform float fogEdgeSoftness;
// Distance the billows have already drifted, in blocks, integrated on the CPU. It is not time
// multiplied by the live speed: that jumps the whole pattern whenever the dial moves.
uniform float fogDriftBlocks;
uniform sampler2D sceneDepth;

layout(location = 0) out vec4 outColor;

float vhFogHash(vec3 p)
{
    p = fract(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
}

float vhFogNoise(vec3 p)
{
    vec3 cell = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = vhFogHash(cell + vec3(0.0, 0.0, 0.0));
    float n100 = vhFogHash(cell + vec3(1.0, 0.0, 0.0));
    float n010 = vhFogHash(cell + vec3(0.0, 1.0, 0.0));
    float n110 = vhFogHash(cell + vec3(1.0, 1.0, 0.0));
    float n001 = vhFogHash(cell + vec3(0.0, 0.0, 1.0));
    float n101 = vhFogHash(cell + vec3(1.0, 0.0, 1.0));
    float n011 = vhFogHash(cell + vec3(0.0, 1.0, 1.0));
    float n111 = vhFogHash(cell + vec3(1.0, 1.0, 1.0));
    float n00 = mix(n000, n100, f.x);
    float n10 = mix(n010, n110, f.x);
    float n01 = mix(n001, n101, f.x);
    float n11 = mix(n011, n111, f.x);
    return mix(mix(n00, n10, f.y), mix(n01, n11, f.y), f.z);
}

float vhFogFbm(vec3 p)
{
    float value = 0.0;
    value += 0.57 * vhFogNoise(p);
    p = p * 2.03 + vec3(11.7, 5.3, 17.1);
    value += 0.29 * vhFogNoise(p);
    p = p * 2.01 + vec3(3.1, 19.4, 7.8);
    value += 0.14 * vhFogNoise(p);
    return value;
}

void main()
{
    ivec2 depthSize = textureSize(sceneDepth, 0);
    ivec2 depthTexel = clamp(ivec2(gl_FragCoord.xy), ivec2(0), depthSize - ivec2(1));
    float opaqueDepth = texelFetch(sceneDepth, depthTexel, 0).r;
    if (gl_FragCoord.z > opaqueDepth + 0.000001) discard;

    float broadScale = 1.0 / max(1.0, fogBillowSizeBlocks);
    // Drift is a real ground speed, so the billow-size dial changes their size and not their
    // pace. Shearing the fine octave a little faster keeps the bank churning inside itself.
    vec3 drift = vec3(fogDriftBlocks, 0.0, fogDriftBlocks * -0.67);
    vec3 broadPoint = (fogWorldPosition + drift)
        * vec3(broadScale, broadScale * 1.40, broadScale);
    float broad = vhFogFbm(broadPoint);
    float detail = vhFogNoise((fogWorldPosition + drift * 1.35)
        * vec3(broadScale * 2.75, broadScale * 3.60, broadScale * 2.75));
    float billow = smoothstep(0.28, 0.80, broad * 0.74 + detail * 0.26);

    // The CPU turns the ownership staircase into a continuous rounded ribbon. World-anchored
    // wobble then moves its visible rim inside a geometry-only guard band, adding irregularity
    // without exposing a hard mesh boundary. Zero softness leaves the smooth CPU profile.
    // Two octaves: broad lobes that swell and pinch the whole bank, and a finer one near the
    // 32-block cell size that is what actually dissolves the grid's teeth. One coarse octave
    // alone just slides the sawtooth sideways.
    vec3 wobblePoint = (fogWorldPosition + drift * 0.8) * broadScale;
    float wobble = (vhFogNoise(wobblePoint * 1.10) - 0.5) * 0.80
        + (vhFogNoise(wobblePoint * 2.60 + vec3(37.1, 11.9, 53.7)) - 0.5) * 0.80;
    // Wobble is bounded to +/-0.8. The 0.65 scale therefore moves at most 0.52 profile units;
    // the mesh's -0.55 outer guard remains transparent even at full edge softness.
    float wobbleShift = wobble * fogEdgeSoftness * 0.65;
    float across = clamp(fogProfile + wobbleShift, 0.0, 1.0);
    float shaped = across * across * (3.0 - 2.0 * across);

    // Coverage leaves only a controllable veil in the noise pockets. The old 0.62 floor made
    // every card visible as a bright slab; the layered volume now gets its body from billows.
    float density = max(0.0, fogWeight) * shaped * max(0.0, fogStrength)
        * mix(clamp(fogCoverage, 0.0, 1.0), 1.70, billow);
    float alpha = 1.0 - exp(-2.30 * density);
    if (alpha < 0.003) discard;

    vec3 colour = rgbaFogIn.rgb * mix(0.92, 1.035, broad);
    outColor = vec4(colour, clamp(alpha, 0.0, 0.94));
}
