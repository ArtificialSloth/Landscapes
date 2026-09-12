#version 330 core

// One full-screen triangle from gl_VertexID alone, exactly as hzbreduce draws it. Distant
// anti-aliasing works per pixel, so geometry has nothing to add, and one triangle avoids the
// seam a two-triangle quad puts down its own diagonal.
void main()
{
    vec2 corner = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
}
