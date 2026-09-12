#version 330 core

// Distant anti-aliasing, step two: put the blended result back into the scene - on cached
// terrain inside the ramp, and on the sky pixels right beside it, which the resolve blended so
// the terrain's edge against the sky is averaged too. Every other pixel is discarded, so vanilla
// terrain and near cached ground keep the exact pixels they were drawn with. Alpha is masked
// off on the C# side.

uniform sampler2D resolved;

// .vhtaa show: tint every anti-aliased pixel magenta by its strength.
uniform int showWeight;

layout(location = 0) out vec4 outColor;

void main()
{
    vec4 value = texelFetch(resolved, ivec2(gl_FragCoord.xy), 0);
    // |alpha| is 1 + ramp on cached terrain and sky, 0 on anything else (lodtaaresolve.fsh).
    float ramp = abs(value.a) - 1.0;
    if (ramp <= 0.0) discard;

    vec3 colour = value.rgb;
    if (showWeight == 1) colour = mix(colour, vec3(1.0, 0.0, 1.0), 0.5 * ramp);
    outColor = vec4(colour, 1.0);
}
