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
    vhFaceAxis = vhFace < 2u ? 1 : (vhFace < 4u ? 2 : (vhFace < 6u ? 3 : 4));
    vhPlaneUv = vec2(0.0);
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
    else if (vhFace >= 6u && vhY0 == vhY1)
    {
        // A leaf canopy's flat plane (LodMesher.AddFlatPlane): a crossed plane with no height.
        // Its corners run the other way round from below, so it faces the camera from both sides.
        uint vhFlatCorner = vhRecordOrigin.y + vhY0 > 0.0 ? (4u - vhLogicalCorner) % 4u : vhLogicalCorner;
        vertexPositionIn = vhFlatCorner == 0u ? vec3(vhX0, vhY0, vhZ0)
            : vhFlatCorner == 1u ? vec3(vhX0, vhY0, vhZ1)
            : vhFlatCorner == 2u ? vec3(vhX1, vhY0, vhZ1)
            : vec3(vhX1, vhY0, vhZ0);
        vhPlaneUv = vertexPositionIn.xz;
    }
    else if (vhFace >= 6u)
    {
        // A reed's crossed plane (LodMesher.AddCrossedPlanes). Seen from behind its corners run
        // the other way round the quad, so it faces the camera from both sides with one quad.
        // With no sheet to wear, the top narrows by a quarter a side so it reads as a tuft.
        vec2 vhPlaneA = vec2(vhX0, vhFace == 6u ? vhZ0 : vhZ1);
        vec2 vhPlaneB = vec2(vhX1, vhFace == 6u ? vhZ1 : vhZ0);
        vec2 vhPlaneMiddle = (vhPlaneA + vhPlaneB) * 0.5;
        vec2 vhPlaneAlong = vhPlaneB - vhPlaneA;
        if (dot(vec2(-vhPlaneAlong.y, vhPlaneAlong.x), -(vhRecordOrigin.xz + vhPlaneA)) < 0.0)
        {
            vec2 vhPlaneSwap = vhPlaneA;
            vhPlaneA = vhPlaneB;
            vhPlaneB = vhPlaneSwap;
        }
        bool vhPlaneFarEnd = vhLogicalCorner == 1u || vhLogicalCorner == 2u;
        vec2 vhPlaneEnd = vhPlaneFarEnd ? vhPlaneB : vhPlaneA;
        float vhPlaneNarrow = (vhGeometry & 0x80000000u) != 0u && plantTextureEnabled != 0 ? 0.0 : 0.5;
        vertexPositionIn = vhLogicalCorner < 2u
            ? vec3(vhPlaneEnd.x, vhY0, vhPlaneEnd.y)
            : vec3(mix(vhPlaneEnd.x, vhPlaneMiddle.x, vhPlaneNarrow), vhY1,
                mix(vhPlaneEnd.y, vhPlaneMiddle.y, vhPlaneNarrow));
        // The second plane mirrors the sheet, so two planes do not show the same plant twice.
        vhPlaneUv = vec2(vhPlaneFarEnd != (vhFace == 7u) ? 1.0 : 0.0, vhLogicalCorner < 2u ? 1.0 : 0.0);
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
        vhBlockSlot = int(vhColor & 0x000FFFFFu);
        // One colour row alone. This is the quad's albedo - the averaged colour it carries
        // INSTEAD of its own, which every fragment needs whether or not the band asks for a
        // texture - so it is the one row that belongs at this stage. The atlas rectangle, the
        // mean and the overlay are read by the band, in the band.
        //
        // Row 1 for tops and bottoms, rows 8 and 9 for walls along X (West, East) and along Z
        // (North, South). A block's colour is its top's, and a standing log's top is the cut
        // end: its bark drew at the colour of its rings.
        int vhColorRow = vhFace < 2u ? 1 : (vhFace < 4u ? 8 : 9);
        vec4 vhAverage = texelFetch(blockTable, ivec2(vhBlockSlot, vhColorRow), 0);
        vertexColorIn = vec4(vhAverage.rgb, float(vhColor >> 24u) / 255.0);
        // Row 9's alpha above one marks leaves, whose sheet tiles up a plane of any height where
        // a plant's is one picture. A flat plane has no height to tile up, and already holds
        // its position. Leaves under other leaves sit in their shade (LodMesher.LeafBuried), and
        // a stack's foot in its own, the more the taller it is.
        if (vhFace >= 6u && vhAverage.a > 1.5)
        {
            vhFaceAxis = 5;
            vertexColorIn.rgb *= 1.0 - 0.03 * float((vhColor >> 20u) & 15u);
            if (vhY1 > vhY0)
            {
                vhPlaneUv.y *= (vhY1 - vhY0) / max(vhX1 - vhX0, 1.0);
                if (vhLogicalCorner < 2u)
                    vertexColorIn.rgb *= 1.0 - mix(0.16, 0.36, clamp((vhY1 - vhY0) / 12.0, 0.0, 1.0));
            }
        }
        if (vhFace >= 6u && plantTextureEnabled == 0) vhBlockSlot = -1;
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
    // The foot of a reed bed sits in its own shade, which is most of what gives it depth.
    if (vhFaceAxis == 4 && vhLogicalCorner < 2u) vertexColorIn.rgb *= 0.55;
#endif

    // The established expanded mesh has no spare vertex attribute, so an opaque textured
    // L0 vertex uses the otherwise-unused fourth alpha band: 192..255 means RGB is the
    // 24-bit block-table slot and alpha - 192 is the original tint slot. Decode it before
    // the common body sees the colour. Packed quads set vhBlockSlot above instead.
    vec4 vhSurfaceColor = vertexColorIn;
#ifndef VH_PACKED
    int vhEncodedAlpha = int(vertexColorIn.a * 255.0 + 0.5);
    vhBlockSlot = -1;
    vhFaceAxis = 0;
    vhPlaneUv = vec2(0.0);
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
