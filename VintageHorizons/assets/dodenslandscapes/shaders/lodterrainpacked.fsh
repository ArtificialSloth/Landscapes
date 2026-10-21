#version 330 core
#extension GL_ARB_explicit_attrib_location: enable

// Only the vertex stage of the packed program needs GLSL 4.30. This stage stays
// at 3.30 like the indirect one because it includes the game's fogandlight.fsh
// and skycolor.fsh, whose clamp(x, 0, 0.9)-style calls mix int and float
// literals. Under 4.x the double overloads of clamp join the candidates, and
// at least NVIDIA 617.42 (an RTX 5090 player report, 2026-10-21) rejects those
// calls as ambiguous, so the whole packed program failed to compile. This stage
// is now the indirect one plus a define nothing in it reads.
#define VH_INDIRECT 1
#define VH_PACKED 1
#include lodterrainbody.fsh
