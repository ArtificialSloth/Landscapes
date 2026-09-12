#version 330 core

// Distant anti-aliasing, step two: put the blended result back into the scene, on cached
// terrain only and only where a block is small enough on screen to need it. Every other pixel is
// discarded, so vanilla terrain, the sky and near cached ground keep the exact pixels they were
// drawn with. Alpha is masked off on the C# side.

uniform sampler2D resolved;
uniform float blockScale;
uniform float startPixels;
uniform float fullPixels;

// .vhtaa show: tint every anti-aliased pixel magenta by its strength.
uniform int showWeight;

layout(location = 0) out vec4 outColor;

void main()
{
    vec4 value = texelFetch(resolved, ivec2(gl_FragCoord.xy), 0);
    if (value.a <= 0.0 || blockScale <= 0.0) discard;

    float blockPixels = blockScale / value.a;
    float weight = clamp((startPixels - blockPixels) / max(startPixels - fullPixels, 0.001),
        0.0, 1.0);
    if (weight <= 0.0) discard;

    vec3 colour = value.rgb;
    if (showWeight == 1) colour = mix(colour, vec3(1.0, 0.0, 1.0), 0.5 * weight);
    outColor = vec4(colour, 1.0);
}
