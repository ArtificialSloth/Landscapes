#version 330 core

// Distant anti-aliasing, step two: put the blended result back into the scene - on cached
// terrain inside the ramp, and on the sky pixels right beside it, which the resolve blended so
// the terrain's edge against the sky is averaged too. Every other pixel is discarded, so vanilla
// terrain and near cached ground keep the exact pixels they were drawn with. Alpha is masked
// off on the C# side.
//
// Sharpening happens here and only here. It changes what reaches the screen, never the history
// the next frame blends with, so it cannot build up and cannot bring the shimmer back. A settled
// anti-aliased pixel is the average of everything it covers - thin snow tops become a soft grey
// - and sharpening restores some of the contrast that averaging took away.

uniform sampler2D resolved;

// Neighbour weight at this sharpening setting, before the contrast adaptation; 0 is off.
uniform float sharpenLobe;

// .dltaa show: tint every anti-aliased pixel magenta by its strength.
uniform int showWeight;

layout(location = 0) out vec4 outColor;

vec3 Resolved(ivec2 at, ivec2 size)
{
    return texelFetch(resolved, clamp(at, ivec2(0), size - 1), 0).rgb;
}

void main()
{
    ivec2 texel = ivec2(gl_FragCoord.xy);
    vec4 value = texelFetch(resolved, texel, 0);
    // |alpha| is 1 + ramp on cached terrain and sky, 0 on anything else (lodtaaresolve.fsh).
    float ramp = abs(value.a) - 1.0;
    if (ramp <= 0.0) discard;

    vec3 colour = value.rgb;
    if (sharpenLobe > 0.0)
    {
        ivec2 size = textureSize(resolved, 0);
        vec3 north = Resolved(texel + ivec2(0, 1), size);
        vec3 south = Resolved(texel - ivec2(0, 1), size);
        vec3 east = Resolved(texel + ivec2(1, 0), size);
        vec3 west = Resolved(texel - ivec2(1, 0), size);
        vec3 lo = min(colour, min(min(north, south), min(east, west)));
        vec3 hi = max(colour, max(max(north, south), max(east, west)));
        // Contrast-adaptive, after the idea in AMD's CAS: sharpen most where the neighbourhood
        // is close in colour and least where it already spans a wide range, so an edge against
        // bright sky or white snow does not ring or clip.
        vec3 amount = sqrt(clamp(min(lo, vec3(1.0) - hi) / max(hi, vec3(1e-4)), 0.0, 1.0));
        vec3 lobe = -amount * (sharpenLobe * ramp);
        colour = max((colour + lobe * (north + south + east + west)) / (1.0 + 4.0 * lobe),
            vec3(0.0));
    }
    if (showWeight == 1) colour = mix(colour, vec3(1.0, 0.0, 1.0), 0.5 * ramp);
    outColor = vec4(colour, 1.0);
}
