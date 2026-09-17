// Dense fog over the exact Farseer/real-terrain handoff. One packed byte describes each
// 32-block ownership cell: available, replaced, then west/east/north/south boundary bits.
// Four bytes share one BGRA texel. The CPU marks both cells beside every boundary, so this
// one sample produces the same one-chunk fade on the LOD and Farseer sides.
uniform sampler2D silhouetteSeamMask;
uniform int silhouetteSeamFogEnabled;
uniform int silhouetteSeamMinCellX;
uniform int silhouetteSeamMinCellZ;
uniform int silhouetteSeamWidthCells;
uniform int silhouetteSeamDepthCells;
uniform int silhouetteSeamTextureWidth;
uniform float silhouetteSeamFogWidthBlocks;
uniform float silhouetteSeamFogStrength;

const int VH_SEAM_AVAILABLE = 1;
const int VH_SEAM_WEST = 4;
const int VH_SEAM_EAST = 8;
const int VH_SEAM_NORTH = 16;
const int VH_SEAM_SOUTH = 32;

int vhSilhouetteSeamState(ivec2 cell)
{
    ivec2 local = cell - ivec2(silhouetteSeamMinCellX, silhouetteSeamMinCellZ);
    if (local.x < 0 || local.y < 0
        || local.x >= silhouetteSeamWidthCells || local.y >= silhouetteSeamDepthCells)
        return 0;

    vec4 packed = texelFetch(
        silhouetteSeamMask, ivec2(local.x / 4, local.y), 0);
    // LoadOrUpdateTextureFromBgra sends CPU byte lanes 0,1,2,3 to B,G,R,A.
    float lane = (local.x & 3) == 0 ? packed.b
        : (local.x & 3) == 1 ? packed.g
        : (local.x & 3) == 2 ? packed.r
        : packed.a;
    return int(floor(lane * 255.0 + 0.5));
}

float vhSilhouetteSeamFog(ivec2 cell, vec2 positionInCell)
{
    if (silhouetteSeamFogEnabled != 1) return 0.0;
    int state = vhSilhouetteSeamState(cell);
    if ((state & VH_SEAM_AVAILABLE) == 0 || (state & 60) == 0) return 0.0;

    float cellBlocks = 32.0;
    float nearest = max(1.0, silhouetteSeamFogWidthBlocks);
    if ((state & VH_SEAM_WEST) != 0) nearest = min(nearest, positionInCell.x);
    if ((state & VH_SEAM_EAST) != 0)
        nearest = min(nearest, cellBlocks - positionInCell.x);
    if ((state & VH_SEAM_NORTH) != 0) nearest = min(nearest, positionInCell.y);
    if ((state & VH_SEAM_SOUTH) != 0)
        nearest = min(nearest, cellBlocks - positionInCell.y);

    // The exponent keeps the middle of each chunk hazy while smoothstep still lands at exactly
    // zero on its far edge. At the ownership edge the result is one: the seam itself is fully
    // fog-coloured, not merely tinted.
    float ramp = 1.0 - smoothstep(
        0.0, max(1.0, silhouetteSeamFogWidthBlocks), nearest);
    return clamp(silhouetteSeamFogStrength, 0.0, 1.0)
        * pow(clamp(ramp, 0.0, 1.0), 0.60);
}

float vhSilhouetteSeamFogAt(vec2 absolutePosition)
{
    vec2 cellPosition = floor(absolutePosition / 32.0);
    ivec2 cell = ivec2(cellPosition);
    return vhSilhouetteSeamFog(
        cell, clamp(absolutePosition - cellPosition * 32.0, 0.0, 32.0));
}

void vhApplySilhouetteSeamFog(float amount, vec3 fogColor,
    inout vec4 surfaceColor, inout vec4 surfaceGlow)
{
    float fog = clamp(amount, 0.0, 1.0);
    surfaceColor.rgb = mix(surfaceColor.rgb, fogColor, fog);
    surfaceColor.a = mix(surfaceColor.a, 1.0, fog);
    surfaceGlow *= 1.0 - fog;
}
