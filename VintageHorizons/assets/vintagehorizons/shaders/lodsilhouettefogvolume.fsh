#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// World-anchored, slowly drifting density turns the crossing fog sheets into an irregular
// volume. It never moves with camera yaw, so the cloud reads as part of the landscape.

in vec3 fogWorldPosition;
in float fogWeight;

uniform vec4 rgbaFogIn;
uniform float fogTime;
uniform float fogStrength;

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
    vec3 drift = vec3(fogTime * 0.006, 0.0, fogTime * -0.004);
    vec3 broadPoint = fogWorldPosition * vec3(0.020, 0.028, 0.020) + drift;
    float broad = vhFogFbm(broadPoint);
    float detail = vhFogNoise(
        fogWorldPosition * vec3(0.055, 0.072, 0.055) + drift * 1.7);
    float billow = smoothstep(0.20, 0.84, broad * 0.74 + detail * 0.26);

    // A nonzero floor keeps the ownership join hidden even where a noise pocket thins the
    // cloud. Overlapping horizontal and vertical sheets then accumulate naturally with alpha.
    float density = max(0.0, fogWeight) * max(0.0, fogStrength)
        * mix(0.62, 1.42, billow);
    float alpha = 1.0 - exp(-2.15 * density);
    if (alpha < 0.003) discard;

    vec3 colour = rgbaFogIn.rgb * mix(0.92, 1.035, broad);
    outColor = vec4(colour, clamp(alpha, 0.0, 0.94));
}
