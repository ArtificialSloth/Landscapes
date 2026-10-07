#version 330 core

// Rebuilds the half-resolution seam fog at full resolution. Each screen pixel blends the four
// nearest half-resolution samples by position, but only as far as each sample's ray ended at
// about the same distance as this pixel's own: fog marched past a far hillside does not bleed
// onto a near tree in front of it. The checkerboard of farthest and nearest rays gives both
// sides of a depth edge a matching sample. The result is premultiplied fog.

uniform sampler2D sceneDepth;
uniform sampler2D fogLow;
uniform sampler2D fogLowDistance;
uniform mat4 invProjection;

layout(location = 0) out vec4 outColor;

void main()
{
    ivec2 fullSize = textureSize(sceneDepth, 0);
    ivec2 texel = clamp(ivec2(gl_FragCoord.xy), ivec2(0), fullSize - ivec2(1));
    // The same endpoint reconstruction as the march, so equal surfaces compare equal.
    float depth = min(texelFetch(sceneDepth, texel, 0).r, 0.999999);
    vec2 screenUv = (vec2(texel) + 0.5) / vec2(fullSize);
    vec4 viewPoint = invProjection * vec4(screenUv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    float surfaceDistance = abs(viewPoint.w) < 0.0000001
        ? 0.0 : length(viewPoint.xyz / viewPoint.w);
    float tolerance = surfaceDistance * 0.03 + 1.0;

    ivec2 lowSize = textureSize(fogLow, 0);
    vec2 lowPosition = (vec2(texel) + 0.5) * 0.5 - 0.5;
    ivec2 lowBase = ivec2(floor(lowPosition));
    vec2 fraction = lowPosition - vec2(lowBase);
    vec4 blended = vec4(0.0);
    float weights = 0.0;
    vec4 closest = vec4(0.0);
    float closestGap = 1e30;
    for (int corner = 0; corner < 4; corner++)
    {
        ivec2 offset = ivec2(corner & 1, corner >> 1);
        ivec2 sampleTexel = clamp(lowBase + offset, ivec2(0), lowSize - ivec2(1));
        vec4 fog = texelFetch(fogLow, sampleTexel, 0);
        float gap = abs(texelFetch(fogLowDistance, sampleTexel, 0).r - surfaceDistance);
        float bilinear = (offset.x == 1 ? fraction.x : 1.0 - fraction.x)
            * (offset.y == 1 ? fraction.y : 1.0 - fraction.y);
        float weight = bilinear * exp(-gap / tolerance);
        blended += fog * weight;
        weights += weight;
        if (gap < closestGap)
        {
            closestGap = gap;
            closest = fog;
        }
    }
    // No sample ended near this pixel's surface: take the nearest match rather than a blend.
    vec4 fog = weights > 0.001 ? blended / weights : closest;
    if (fog.a < 0.002) discard;
    outColor = fog;
}
