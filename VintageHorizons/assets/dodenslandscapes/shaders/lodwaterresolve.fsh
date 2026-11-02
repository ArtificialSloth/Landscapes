#version 330 core

// Cached water depth at full resolution, cleared to 1.0 where there is none.
uniform sampler2D cachedWaterDepth;
// Full-resolution pixels per quarter texel on each axis, about 4.
uniform vec2 blockSize;
out vec4 outColor;

void main()
{
    // The pixels this quarter texel covers. A texel takes cached water only where at least half
    // of them are water, so water smaller than that never flickers in, and a mostly-water texel
    // whose centre missed it is no longer a hole.
    vec2 texel = floor(gl_FragCoord.xy);
    ivec2 size = textureSize(cachedWaterDepth, 0);
    ivec2 lo = min(ivec2(floor(texel * blockSize)), size);
    ivec2 hi = min(ivec2(floor((texel + 1.0) * blockSize)), size);
    int total = 0;
    int water = 0;
    float sum = 0.0;
    for (int y = lo.y; y < hi.y; y++)
    {
        for (int x = lo.x; x < hi.x; x++)
        {
            float d = texelFetch(cachedWaterDepth, ivec2(x, y), 0).r;
            total++;
            if (d < 1.0)
            {
                water++;
                sum += d;
            }
        }
    }
    if (total == 0 || water * 2 < total) discard;
    // Composed under the pass's Less test, so vanilla water nearer than this stays.
    gl_FragDepth = sum / float(water);
    outColor = vec4(1.0);
}
