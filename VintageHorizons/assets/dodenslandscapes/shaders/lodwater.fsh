#version 330 core
#define VH_WATER_SURFACE 1
// The same water/thin-cover shading in its own program. Composition stays opaque
// for the extraction comparison. Land texturing, snow and band correction stay out.
#include lodwaterfragment.fsh
