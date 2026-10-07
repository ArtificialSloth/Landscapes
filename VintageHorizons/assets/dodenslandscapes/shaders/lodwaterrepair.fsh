#version 330 core

uniform sampler2D sourceDepth;
out vec4 outColor;

void main()
{
    ivec2 p = ivec2(gl_FragCoord.xy);
    ivec2 size = textureSize(sourceDepth, 0);
    // Only clear holes. Existing vanilla and cached depths are never changed.
    if (texelFetch(sourceDepth, p, 0).r < 1.0) discard;
    for (int ry = 1; ry <= 2; ry++)
    {
        for (int rx = 1; rx <= 2; rx++)
        {
            if (p.x < rx || p.y < ry || p.x + rx >= size.x || p.y + ry >= size.y) continue;
            float l = texelFetch(sourceDepth, p - ivec2(rx, 0), 0).r;
            float r = texelFetch(sourceDepth, p + ivec2(rx, 0), 0).r;
            float u = texelFetch(sourceDepth, p - ivec2(0, ry), 0).r;
            float d = texelFetch(sourceDepth, p + ivec2(0, ry), 0).r;
            if (max(max(l, r), max(u, d)) >= 1.0) continue;
            float horizontal = (l + r) * 0.5;
            float vertical = (u + d) * 0.5;
            // Both axes must predict the same surface, within two D24 depth steps.
            // A shoreline with water on only one side cannot pass this test.
            if (abs(horizontal - vertical) > 2.0 / 16777215.0) continue;
            gl_FragDepth = max(horizontal, vertical);
            outColor = vec4(1.0);
            return;
        }
    }
    discard;
}
