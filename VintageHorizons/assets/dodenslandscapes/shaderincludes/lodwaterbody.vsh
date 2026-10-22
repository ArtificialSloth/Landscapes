// Water and thin cover retain their established vertex format and transforms.
#include lodvertexinputs.vsh
void main()
{
    // Water keeps the expanded vertex layout. The shared surface body consumes the
    // normalized colour and texture selector that terrain now prepares explicitly.
    vec4 vhSurfaceColor = vertexColorIn;
    vhBlockSlot = -1;
    vhFaceAxis = 0;
#include lodvertexsurface.vsh
}
