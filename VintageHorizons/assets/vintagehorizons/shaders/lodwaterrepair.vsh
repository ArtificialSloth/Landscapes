#version 330 core

void main()
{
    vec2 p = gl_VertexID == 0 ? vec2(-1.0, -1.0)
        : gl_VertexID == 1 ? vec2(3.0, -1.0) : vec2(-1.0, 3.0);
    gl_Position = vec4(p, 0.0, 1.0);
}
