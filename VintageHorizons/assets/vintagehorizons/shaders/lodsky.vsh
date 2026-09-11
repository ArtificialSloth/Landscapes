#version 430 core

// `.vhsky`: one triangle over the whole viewport, placed on the far plane. With the depth test
// at LEQUAL and depth writes off, a fragment survives only where nothing opaque was drawn.
void main()
{
    vec2 p = gl_VertexID == 0 ? vec2(-1.0, -1.0)
        : gl_VertexID == 1 ? vec2(3.0, -1.0) : vec2(-1.0, 3.0);
    gl_Position = vec4(p, 1.0, 1.0);
}
