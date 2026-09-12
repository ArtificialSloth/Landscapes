#include lodvertexinputs.vsh
void main()
{
#ifdef VH_PACKED
    uint vhQuad = uint(gl_VertexID) / 4u;
    uint vhLogicalCorner = uint(gl_VertexID) % 4u;
    uint vhBase = vhQuad * 3u;
    uint vhGeometry = vhPackedWords[vhBase];
    uint vhHeights = vhPackedWords[vhBase + 1u];
    uint vhColor = vhPackedWords[vhBase + 2u];

    float vhX0 = float(vhGeometry & 127u) * vhRecordNoise.z;
    float vhX1 = float((vhGeometry >> 7u) & 127u) * vhRecordNoise.z;
    float vhZ0 = float((vhGeometry >> 14u) & 127u) * vhRecordNoise.z;
    float vhZ1 = float((vhGeometry >> 21u) & 127u) * vhRecordNoise.z;
    uint vhFace = (vhGeometry >> 28u) & 7u;
    float vhY0 = float(vhHeights & 65535u) * 0.25;
    float vhY1 = float(vhHeights >> 16u) * 0.25;

    // vhRecordOrigin is camera-relative. West/North normals point toward negative X/Z;
    // East/South normals point positive. A camera on the opposite side of that plane is
    // exactly the face the fixed-function culler rejects later. Put every corner outside
    // the same clip plane and return before the block-table fetches and shared shader body.
    bool vhBackface = packedBackfaceCull != 0
        && ((vhFace == 2u && vhRecordOrigin.x + vhX0 <= 0.0)
            || (vhFace == 3u && vhRecordOrigin.x + vhX1 >= 0.0)
            || (vhFace == 4u && vhRecordOrigin.z + vhZ0 <= 0.0)
            || (vhFace == 5u && vhRecordOrigin.z + vhZ1 >= 0.0));
    if (vhBackface)
    {
        gl_Position = vec4(2.0, 0.0, 0.0, 1.0);
        return;
    }

    vec3 vertexPositionIn;
    if (vhFace == 0u)
    {
        vertexPositionIn = vhLogicalCorner == 0u ? vec3(vhX0, vhY0, vhZ0)
            : vhLogicalCorner == 1u ? vec3(vhX1, vhY0, vhZ0)
            : vhLogicalCorner == 2u ? vec3(vhX1, vhY0, vhZ1)
            : vec3(vhX0, vhY0, vhZ1);
    }
    else if (vhFace == 1u)
    {
        vertexPositionIn = vhLogicalCorner == 0u ? vec3(vhX0, vhY0, vhZ0)
            : vhLogicalCorner == 1u ? vec3(vhX0, vhY0, vhZ1)
            : vhLogicalCorner == 2u ? vec3(vhX1, vhY0, vhZ1)
            : vec3(vhX1, vhY0, vhZ0);
    }
    else if (vhFace == 2u)
    {
        vertexPositionIn = vhLogicalCorner == 0u ? vec3(vhX0, vhY0, vhZ0)
            : vhLogicalCorner == 1u ? vec3(vhX0, vhY0, vhZ1)
            : vhLogicalCorner == 2u ? vec3(vhX0, vhY1, vhZ1)
            : vec3(vhX0, vhY1, vhZ0);
    }
    else if (vhFace == 3u)
    {
        vertexPositionIn = vhLogicalCorner == 0u ? vec3(vhX1, vhY0, vhZ0)
            : vhLogicalCorner == 1u ? vec3(vhX1, vhY1, vhZ0)
            : vhLogicalCorner == 2u ? vec3(vhX1, vhY1, vhZ1)
            : vec3(vhX1, vhY0, vhZ1);
    }
    else if (vhFace == 4u)
    {
        vertexPositionIn = vhLogicalCorner == 0u ? vec3(vhX0, vhY0, vhZ0)
            : vhLogicalCorner == 1u ? vec3(vhX0, vhY1, vhZ0)
            : vhLogicalCorner == 2u ? vec3(vhX1, vhY1, vhZ0)
            : vec3(vhX1, vhY0, vhZ0);
    }
    else
    {
        vertexPositionIn = vhLogicalCorner == 0u ? vec3(vhX0, vhY0, vhZ1)
            : vhLogicalCorner == 1u ? vec3(vhX1, vhY0, vhZ1)
            : vhLogicalCorner == 2u ? vec3(vhX1, vhY1, vhZ1)
            : vec3(vhX0, vhY1, vhZ1);
    }

    // Word 0's top bit: set means word 2 is a block texture slot, not a colour.
    vec4 vertexColorIn;
    if ((vhGeometry & 0x80000000u) != 0u)
    {
        vhBlockSlot = int(vhColor & 0x00FFFFFFu);
        // Row 1 alone. This is the quad's albedo - the averaged colour it carries INSTEAD of
        // its own, which every fragment needs whether or not the band asks for a texture - so
        // it is the one row that belongs at this stage. The atlas rectangle, the mean and the
        // overlay are read by the band, in the band.
        vec4 vhAverage = texelFetch(blockTable, ivec2(vhBlockSlot, 1), 0);
        vertexColorIn = vec4(vhAverage.rgb, float(vhColor >> 24u) / 255.0);
    }
    else
    {
        vhBlockSlot = -1;
        vertexColorIn = vec4(
            float(vhColor & 255u),
            float((vhColor >> 8u) & 255u),
            float((vhColor >> 16u) & 255u),
            float(vhColor >> 24u)) / 255.0;
    }
#endif

    // The established expanded mesh has no spare vertex attribute, so an opaque textured
    // L0 vertex uses the otherwise-unused fourth alpha band: 192..255 means RGB is the
    // 24-bit block-table slot and alpha - 192 is the original tint slot. Decode it before
    // the common body sees the colour. Packed quads set vhBlockSlot above instead.
    vec4 vhSurfaceColor = vertexColorIn;
#ifndef VH_PACKED
    int vhEncodedAlpha = int(vertexColorIn.a * 255.0 + 0.5);
    vhBlockSlot = -1;
    if (vhEncodedAlpha >= 192)
    {
        ivec3 vhSlotBytes = ivec3(vertexColorIn.rgb * 255.0 + 0.5);
        vhBlockSlot = vhSlotBytes.r | (vhSlotBytes.g << 8) | (vhSlotBytes.b << 16);
        vec4 vhAverage = texelFetch(blockTable, ivec2(vhBlockSlot, 1), 0);
        vhSurfaceColor = vec4(vhAverage.rgb, float(vhEncodedAlpha - 192) / 255.0);
    }
#endif
#include lodvertexsurface.vsh

    // Distant anti-aliasing: move the finished position by this frame's sub-pixel offset,
    // weighted by how many pixels one block spans here. gl_Position.w is the view depth. This
    // ramp is the second half of the resolve's, where history is already at full share.
    if (vhTaaBlockScale > 0.0 && gl_Position.w > 0.0)
    {
        float vhBlockPixels = vhTaaBlockScale / gl_Position.w;
        float vhTaaWeight = clamp((vhTaaStartPixels - vhBlockPixels)
            / max(vhTaaStartPixels - vhTaaFullPixels, 0.001), 0.0, 1.0);
        gl_Position.xy += vec2(vhTaaJitterX, vhTaaJitterY) * (vhTaaWeight * gl_Position.w);
    }

#ifdef VH_INDIRECT
    vhColumnBlocks = vhRecordNoise.z;
    vhMaskSectionOrigin = vhRecordMask.xy;
#endif
}
