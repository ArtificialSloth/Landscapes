#version 330 core

// One full-screen triangle. Fog shape and depth come from the continuous field and the scene's
// depth texture, so adding geometry here would only reintroduce the surfaces this pass removes.
void main()
{
    vec2 corner = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
}
