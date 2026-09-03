
// Fog handling, transition push-down and structure adapted from Farseer's region.vsh
// (github.com/ViciousBadger/VSMod-Farseer, MIT, (c) Badgerson).

#ifndef VH_PACKED
layout(location = 0) in vec3 vertexPositionIn;
layout(location = 1) in vec4 vertexColorIn;
#else
// Three tightly packed scalar words per greedy quad. Scalar uint[] is deliberate:
// an array of uvec3 has a sixteen-byte std430 stride and would silently throw away the
// format's 12-byte target. A reusable 0,1,2,0,2,3 index pattern plus each command's
// baseVertex makes gl_VertexID select one of four unique corners; the post-transform cache
// can therefore reuse corners instead of running this decoder six times per quad.
layout(std430, binding = 0) readonly buffer VhPackedQuadBuffer
{
    uint vhPackedWords[];
};
#endif

uniform mat4 viewMatrix;
uniform mat4 projectionMatrix;

uniform vec4 rgbaFogIn;
uniform float fogMinIn;
uniform float fogDensityIn;

uniform float farViewDistance;

// The packed path knows a quad's exact axis-aligned face before it does any material,
// tint, warp, fog or projection work. Fixed-function back-face culling happens after the
// complete vertex shader, so rejecting vertical faces here avoids paying that full price
// for walls OpenGL will discard anyway. The test is exact for a vertical plane even after
// global Y warping: the warp never changes which side of its X/Z plane the camera occupies.
// It follows the ordinary back-face switch so `.vhbackface off` remains a complete escape.
uniform int packedBackfaceCull;

// How far to lower solid cached terrain, in blocks. A constant global offset, NOT a ramp
// and NOT a function of distance: an earlier transition sink was eight blocks deep and
// ramped, so it slid visibly as the player walked toward it, and it was removed. A constant
// has no edge anywhere - every cached surface moves together, cached meets cached exactly as
// before, and the only place the offset can be observed is where cached terrain overlaps
// live vanilla terrain, which is the one place it is wanted.
//
// It exists for depth fighting, which is a precision problem between opaque surfaces, and it
// is worth far more underwater than on open land: a lake floor drawn twice fights between a
// pale bed and a dark water-tinted one, so every pixel that flips is a maximum-contrast flip.
// The same fight between two similarly lit land surfaces is nearly invisible.
//
// A fraction of a block by default. Whether that is enough separation at the distance the
// overlap actually sits is a depth-precision question no desk can answer, so `.vhband sink`
// moves it.
uniform float bandSinkBlocks;

#ifdef VH_PACKED
// Where each block's texture lives in the game's own block atlas, and the averaged colour to
// fall back to. Two rows: row 0 is the atlas rectangle, row 1 the colour.
//
// A textured quad carries a block SLOT in the field its colour would occupy - they share one
// 24-bit field and word 0's last spare bit says which - so the colour is fetched here rather
// than read out of the vertex. That sharing is what lets real vanilla textures cost no extra
// memory at all. Fetched per vertex, not per fragment: both values are constant across a
// quad, so a flat varying carries the rectangle onward for free.
uniform sampler2D blockTable;
flat out vec4 vhBlockRect;
// The sampled texture's own mean. The fragment stage divides by it so the texture becomes
// variation AROUND the block's colour rather than the colour itself - grass-covered ground
// has a brown soil texture under a green overlay, so taking the texture at face value drew a
// brown band across the world.
flat out vec3 vhBlockMean;
// The grass-style overlay's own rectangle, zero when the block has none. Grass-covered ground
// is soil under a grass texture composited by the grass's per-pixel alpha; sampling only the
// base drew correctly coloured ground wearing dirt's pattern.
flat out vec4 vhBlockOverlay;
#endif

// ---- Per-section facts ------------------------------------------------------
//
// Everything in this block describes the SECTION being drawn rather than the vertex.
// The established path sets each one as a uniform before that section's own draw. A
// multi-draw has no per-draw uniforms, so the indirect variant reads the same values
// out of LodGpuSectionRecord, delivered as instanced attributes selected by each
// command's base instance, and #defines them to the names the rest of this file
// already uses.
//
// Nothing below this block differs between the two variants, deliberately: the fast
// path's acceptance gate is that it draws pixels identical to the established path,
// and a gate like that is only worth something if the two cannot drift apart in the
// first place. The attribute layout must match LodGpuSectionRecord's offsets;
// GpuIndirectChecks and StaticAssetChecks hold the two together.
#ifdef VH_INDIRECT
layout(location = 2) in vec4 vhRecordOrigin;     // xyz camera-relative origin, w section size
layout(location = 3) in vec4 vhRecordNoise;      // xy stable world origin, z column blocks
layout(location = 4) in ivec4 vhRecordMask;      // xy section origin in whole vanilla chunks
layout(location = 5) in vec4 vhRecordOpenEdges;  // -X, +X, -Z, +Z, 1 = open

// The established modelMatrix is Identity().Translate(relX, -camPos.Y, relZ) and never
// anything else, so the record carries that translation alone and the matrix is rebuilt
// here. Rebuilt as a matrix rather than folded into an addition so the expression the
// body evaluates is the same one in both variants.
#define modelMatrix mat4(1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, vhRecordOrigin.x, vhRecordOrigin.y, vhRecordOrigin.z, 1.0)
#define noiseOrigin vhRecordNoise
#define openEdges vhRecordOpenEdges
#define sectionSize vhRecordOrigin.w

// Two per-section values are read by the fragment stage, which has no attributes of its
// own. Flat, because both are constant across the section: interpolating an integer
// chunk origin would be meaningless, and interpolating the column size would shimmer.
flat out float vhColumnBlocks;
flat out ivec2 vhMaskSectionOrigin;
#else
uniform mat4 modelMatrix;

// Stable world X/Z for this section. Geometry stays camera-relative for precision,
// but appearance must not move with the camera: a fixed terrain point must always
// sample the same colour variation.
uniform vec4 noiseOrigin;

// Which of this section's four sides border on area we have NO captured data for
// (-X, +X, -Z, +Z; 1 = open). Client-side-only means coverage is whatever the
// server has streamed us, so the cache genuinely runs out mid-air along the edges
// of wherever the player has been. Those boundaries are dissolved into the
// horizon rather than left standing as cliffs.
uniform vec4 openEdges;
uniform float sectionSize;
#endif
// -----------------------------------------------------------------------------

// Tint is resolved per VERTEX, not per fragment: the slot is constant across a quad and
// everything the tint depends on varies smoothly with position, so interpolating the result is
// equivalent and saves the lookups per fragment.
// Must equal LodTintRegistry.MaxSlots; LoadShader logs an error if it does not.
const int TINT_SLOTS = 64;

// The climate field, and what it takes to finish a tint from it.
//
// Until 0.3.179 there was one finished colour per slot, sampled where the PLAYER stood, so the
// whole visible world wore the player's own climate and repainted when they moved. These
// tables invert that: the climate of every 64-block cell is stored, and the colour is worked
// out here from the climate of the ground this vertex actually sits on.
//
// The pieces, in the order they combine:
//   climateField  temperature in red, rainfall in green, per cell, bilinear between cells.
//                 Wrapped like the seam field and the readiness mask; same addressing rule,
//                 held to LodClimateField.TexelIndex by a static check.
//   slotTable     per slot: row 0 the season colour, its alpha saying whether a season blend
//                 applies at all; row 1 the untinted share; row 2 red the climate map page,
//                 255 meaning this slot cannot be finished here; row 3 the flat fallback it
//                 falls back to when it cannot, which is the engine's own blended answer.
//   climateMaps   the game's own climate colour maps, one 256x256 page each, temperature
//                 across and rainfall down at one texel per count.
//
// Everything between them is arithmetic transcribed from the engine's own colormap.vsh and
// Climate.GetAdjustedTemperature: the lapse rate, and the season blend weight. Neither needs
// sampling, which is why height needs no ramp here - it is exact at every vertex.
uniform sampler2D slotTable;
uniform sampler2D climateField;
uniform sampler2D climateMaps;
uniform int climateFieldEnabled;
uniform int climateMinCellX;
uniform int climateMinCellZ;
uniform int climateCapacity;
uniform float climateCellBlocks;
uniform float climateSeaLevel;
uniform int climateRainSeaLevel;

// One cell's temperature and rainfall, in 0..255. The wrap is the field's own: a window of
// cells stored in a ring, so a cell address folds into the buffer the same way in both
// languages.
vec2 climateCell(int cellX, int cellZ)
{
    int wrap = climateCapacity - 1;
    return texelFetch(climateField, ivec2(cellX & wrap, cellZ & wrap), 0).rg * 255.0;
}

// One climate map page, bilinear. The engine's CPU path truncates to a whole texel; this
// interpolates, because the temperature axis moves one texel per 1.5 blocks of height and a
// nearest lookup would draw the map's own texel grid as steps up every slope. The two agree
// to within one texel either way.
vec3 climateColor(int page, float temperature, float rainfall)
{
    float u = clamp(temperature, 0.0, 255.0);
    float v = clamp(rainfall, 0.0, 255.0);
    int x0 = clamp(int(u), 0, 254);
    int y0 = clamp(int(v), 0, 254);
    int row = page * 256;
    vec3 c00 = texelFetch(climateMaps, ivec2(x0, row + y0), 0).rgb;
    vec3 c10 = texelFetch(climateMaps, ivec2(x0 + 1, row + y0), 0).rgb;
    vec3 c01 = texelFetch(climateMaps, ivec2(x0, row + y0 + 1), 0).rgb;
    vec3 c11 = texelFetch(climateMaps, ivec2(x0 + 1, row + y0 + 1), 0).rgb;
    float fx = u - float(x0);
    float fy = v - float(y0);
    return mix(mix(c00, c10, fx), mix(c01, c11, fx), fy);
}

out vec3 tint;
out vec4 worldPos;
out vec4 vertexColor;
out float yLevel;
out vec4 rgbaFog;
out float dist;
out float radialDistance;
out float fogAmount;
out float edgeFade;
out vec3 terrainPos;
// Section-local block position. Ownership must not be derived from a summed world
// coordinate: at 512k blocks a float32 rounds a fragment 0.03 blocks below a chunk edge
// onto the next chunk and it would take that chunk's ownership.
out vec3 sectionLocal;

#include vertexflagbits.ash
#include colorutil.ash
#include shadowcoords.vsh
#include fogandlight.vsh
#include vertexwarp.vsh

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
        int vhSlot = int(vhColor & 0x00FFFFFFu);
        vhBlockRect = texelFetch(blockTable, ivec2(vhSlot, 0), 0);
        vec4 vhAverage = texelFetch(blockTable, ivec2(vhSlot, 1), 0);
        vec4 vhMeanRow = texelFetch(blockTable, ivec2(vhSlot, 2), 0);
        vhBlockMean = vhMeanRow.rgb;
        vhBlockOverlay = vhMeanRow.a > 0.5
            ? texelFetch(blockTable, ivec2(vhSlot, 3), 0)
            : vec4(0.0);
        vertexColorIn = vec4(vhAverage.rgb, float(vhColor >> 24u) / 255.0);
    }
    else
    {
        vhBlockRect = vec4(0.0);
        vhBlockMean = vec3(1.0);
        vhBlockOverlay = vec4(0.0);
        vertexColorIn = vec4(
            float(vhColor & 255u),
            float((vhColor >> 8u) & 255u),
            float((vhColor >> 16u) & 255u),
            float(vhColor >> 24u)) / 255.0;
    }
#endif
    yLevel = vertexPositionIn.y;
    vertexColor = vertexColorIn;

    int slotRaw = int(vertexColorIn.a * 255.0 + 0.5);
    int slot = clamp(slotRaw - (slotRaw / TINT_SLOTS) * TINT_SLOTS, 0, TINT_SLOTS - 1);
    // The flat fallback: the engine's own blended answer for this slot, already diluted by its
    // untinted share. This is what draws where the shader cannot finish the tint itself - no
    // paged climate map, the tropical season weight this mod does not reproduce, or no climate
    // field at all - and it is what every build before the field drew everywhere.
    tint = texelFetch(slotTable, ivec2(slot, 3), 0).rgb;

    // The climate field, where this slot can be finished from it. A page of 255 means it
    // cannot be, and the fallback above stands.
    int tintPage = 255;
    if (climateFieldEnabled == 1)
    {
        tintPage = int(texelFetch(slotTable, ivec2(slot, 2), 0).r * 255.0 + 0.5);
    }
    if (tintPage < 255)
    {
        // Cell coordinates from the world position. The stored value for a cell is its
        // CENTRE, so the half-cell shift is what makes the interpolation land on the samples
        // rather than half a cell off them.
        float fx = (noiseOrigin.x + vertexPositionIn.x) / climateCellBlocks - 0.5;
        float fz = (noiseOrigin.y + vertexPositionIn.z) / climateCellBlocks - 0.5;

        // Terrain past the window clamps to its edge, which continues the spread outward
        // rather than drawing a line where the stored climate stops.
        int lo = climateMinCellX;
        int hi = climateMinCellX + climateCapacity - 2;
        int cx = clamp(int(floor(fx)), lo, hi);
        int loZ = climateMinCellZ;
        int hiZ = climateMinCellZ + climateCapacity - 2;
        int cz = clamp(int(floor(fz)), loZ, hiZ);

        vec2 c00 = climateCell(cx, cz);
        vec2 c10 = climateCell(cx + 1, cz);
        vec2 c01 = climateCell(cx, cz + 1);
        vec2 c11 = climateCell(cx + 1, cz + 1);
        vec2 climate = mix(mix(c00, c10, clamp(fx - float(cx), 0.0, 1.0)),
                           mix(c01, c11, clamp(fx - float(cx), 0.0, 1.0)),
                           clamp(fz - float(cz), 0.0, 1.0));

        // Climate.GetAdjustedTemperature: one count colder per 1.5 blocks above sea level,
        // and warmer below it, clamped to the range the maps are indexed by.
        float seaLevelDist = yLevel - climateSeaLevel;
        float adjusted = clamp(climate.x - seaLevelDist / 1.5, 0.0, 255.0);

        // Climate.GetRainFall. The field stores RAW rainfall; these are the height terms the
        // engine adds before anything reads a colour map. The first climbs steadily with
        // altitude, the second is a bonus of up to 40 that only applies within eight blocks of
        // sea level. Integer division, truncating toward zero, as the engine's does.
        //
        // Storing the rainfall a climate read returns instead of the raw value spends a sea
        // level rainfall on a hillside, and sea level is exactly where that bonus is largest.
        // The height ramp this replaced never had the fault, because it asked the engine for a
        // finished colour at each height and the engine applied this itself.
        int rainY = int(floor(yLevel));
        float rainfall = clamp(
            climate.y
            + float((rainY - climateRainSeaLevel) / 2)
            + 5.0 * float(clamp(8 + climateRainSeaLevel - rainY, 0, 8)), 0.0, 255.0);

        // colormap.vsh's season weight, including the engine's own asymmetry: the altitude
        // term ADDS 1.5 per block where the adjustment above subtracted 1/1.5, which does not
        // cancel and is not a mistake to correct here - it is what the game draws.
        float weightTemp = adjusted + max(0.0, seaLevelDist * 1.5);
        float seasonWeight = clamp(
            0.5 - cos(weightTemp / 42.0) / 2.3
            + max(0.0, 128.0 - weightTemp) / 512.0
            - max(0.0, weightTemp - 130.0) / 200.0, 0.0, 1.0);

        vec4 seasonRow = texelFetch(slotTable, ivec2(slot, 0), 0);
        vec3 blended = mix(climateColor(tintPage, adjusted, rainfall),
                           seasonRow.rgb, seasonWeight * seasonRow.a);

        // The share of the stored colour the tint must not touch - bare dirt showing through
        // a grass overlay - exactly as the ramp already carries it.
        vec3 share = texelFetch(slotTable, ivec2(slot, 1), 0).rgb;
        tint = clamp(share + (1.0 - share) * blended, 0.0, 1.0);
    }

    sectionLocal = vertexPositionIn;

    worldPos = modelMatrix * vec4(vertexPositionIn, 1.0);

    // Water surfaces are exempt. A lake surface is a perfectly flat reference plane, so an
    // offset applied to it and not to vanilla's would read as a lip running across the water
    // far more readily than the same offset reads on bumpy ground. Submerged ground is
    // exactly where the sink is needed, and it is solid, so this exemption costs nothing.
    //
    // Applied to worldPos alone: sectionLocal, terrainPos and yLevel keep the true surface
    // so ownership addressing, colour variation and the snow line cannot shift with it.
    if (slotRaw / TINT_SLOTS != 1) worldPos.y -= bandSinkBlocks;

    worldPos = applyGlobalWarping(worldPos);

    terrainPos = vec3(
        noiseOrigin.x + vertexPositionIn.x,
        vertexPositionIn.y,
        noiseOrigin.y + vertexPositionIn.z);

    // 0 at the start of the LOD band (inside vanilla terrain), 1 at the far edge
    float distStart = viewDistance * 0.785;
    float radial = length(worldPos.xz);
    radialDistance = radial;
    dist = (radial - distStart) / (farViewDistance - distStart - 512.0);

    // Distance into the section from each open side, as a 0..1 ramp over the outer
    // third. Vertex positions are section-local, so this is just the local x/z.
    float fadeWidth = max(8.0, sectionSize * 0.34);
    vec4 inset = vec4(
        vertexPositionIn.x,
        sectionSize - vertexPositionIn.x,
        vertexPositionIn.z,
        sectionSize - vertexPositionIn.z);
    vec4 nearness = clamp(1.0 - inset / fadeWidth, 0.0, 1.0) * openEdges;
    edgeFade = max(max(nearness.x, nearness.y), max(nearness.z, nearness.w));

    // Only past the transition ring: terrain beside the player is covered by real
    // chunks, and dissolving it there would read as a hole rather than haze.
    edgeFade *= clamp(dist * 4.0, 0.0, 1.0);

    fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);
    rgbaFog = rgbaFogIn;

    vec4 camPos = viewMatrix * worldPos;
    gl_Position = projectionMatrix * camPos;

#ifdef VH_INDIRECT
    vhColumnBlocks = vhRecordNoise.z;
    vhMaskSectionOrigin = vhRecordMask.xy;
#endif
}
