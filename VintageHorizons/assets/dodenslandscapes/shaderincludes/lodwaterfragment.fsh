#include lodfragmentinputs.fsh
#include lodownership.fsh
void main()
{
#include lodownershiptest.fsh
#include lodbanddebug.fsh
#include lodsurfacecolor.fsh
#include lodsurfacefinish.fsh
#ifdef VH_WATER_OIT
    OIT(outColor, vhWaterGlow.r);
#endif
}
