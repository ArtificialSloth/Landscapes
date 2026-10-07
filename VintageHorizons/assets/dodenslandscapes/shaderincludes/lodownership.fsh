// Slack around a chunk plane, in blocks. A wall lies exactly IN the plane between two
// ownership cells, and hardware interpolation of an attribute that is constant across the
// quad is not promised to reproduce it to the last bit - a value one ULP light of 32.0
// floors to the chunk below. Anything inside this counts as lying in the plane. It is far
// under the quarter-block thin-cover mat, the shortest surface this shader draws, so no
// ordinary face can be mistaken for a boundary.
const float MASK_PLANE_SLACK = 0.03125;

// Signed distance from a section-local coordinate to the nearest chunk plane, in blocks.
float maskPlaneOffset(float localBlocks)
{
    return localBlocks - 32.0 * floor(localBlocks / 32.0 + 0.5);
}

// Whether vanilla owns one ownership cell, for the band this fragment belongs to.
//
// Every address outside the tracked window, or above or below the world, answers no, so an
// unknown cell falls through to normal cached drawing: the mask can only take ground away
// from the cache where vanilla has proven it draws there.
bool vanillaOwnsCell(int cellX, int cellY, int cellZ, int ownershipBand)
{
    if (cellY < 0 || cellY >= maskVerticalChunks) return false;
    if (cellX < maskMinX || cellX >= maskMinX + maskWidth) return false;
    if (cellZ < maskMinZ || cellZ >= maskMinZ + maskDepth) return false;

    int wrap = maskCapacity - 1;
    ivec2 maskTexel = ivec2(cellX & wrap, (cellZ & wrap) + cellY * maskCapacity);
    // Red is the GROUND footprint, green is water's own. They differ by a whole
    // number of chunks: ground keeps drawing through the ring ownership refuses, as a
    // hole guard, and water does not need that guard - a missing river is local and
    // obvious, while cached water standing inside live terrain is not. Both edges fall
    // on chunk boundaries because the green channel is a chunk-wise dilation of the
    // red one, never a distance from the camera.
    vec4 ownership = texelFetch(readinessMask, maskTexel, 0);
    if ((ownershipBand == 1 ? ownership.g : ownership.r) > 0.5) return true;
    return false;
}

