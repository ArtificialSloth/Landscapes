
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

// Distant anti-aliasing's per-frame nudge (LodTerrainRenderer.Taa.cs): a different fraction of a
// pixel every frame, applied only in proportion to how small a block is on screen at a vertex -
// the same ramp the resolve pass blends by - so near cached ground, the blend band and the vanilla
// seam never move. A block scale of zero switches it off.
//
// DECLARED here, in the inputs every program shares, but APPLIED only in lodterrainbody.vsh. The
// water programs receive the same frame uploads, and the engine's Uniform(name, ...) indexes a
// dictionary built from the names a program's source declares: a name water never declared would
// throw. Declared and unused, water gets location -1 and ignores the value. Water must not be
// nudged, because it is drawn after the blend and would wobble.
uniform float vhTaaBlockScale;
uniform float vhTaaJitterX;
uniform float vhTaaJitterY;
uniform float vhTaaStartPixels;
uniform float vhTaaFullPixels;

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

// Where each block's texture lives in the game's own block atlas, and the averaged colour to
// fall back to. Both the packed and established expanded mesh formats can carry a slot.
// Packed quads mark word 0's top bit; expanded vertices use otherwise-unused alpha 192..255.
// Row 1 restores the averaged colour before the shared vertex body shades the surface.
//
// A textured quad carries a block SLOT in the field its colour would occupy - they share one
// 24-bit field - so the colour is fetched here rather than read out of the vertex. That sharing
// is what lets real vanilla textures cost no extra memory. Fetched per vertex, not per fragment:
// both values are constant across a quad, so a flat varying carries the slot onward for free.
uniform sampler2D blockTable;

// The SLOT, and nothing unpacked from it. -1 means this quad carries a colour.
//
// This carried three unpacked rows - the atlas rectangle, the texture's mean, and the grass
// overlay's rectangle - eleven floats of flat varying per vertex, written on BOTH branches
// and therefore paid in full even with `.vhblend off`, plus three table fetches per vertex to
// produce them. The fragment stage reads them only inside the blend band, which is a thin
// ring of the screen against millions of vertices, so the whole bundle was being computed and
// carried for geometry that would never ask.
//
// One integer travels instead, and the band fetches its own rows where it actually needs
// them. The averaged colour stays here because it is this vertex's albedo and every fragment
// of the quad needs it, band or no band.
flat out int vhBlockSlot;

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

#define columnBlocks vhRecordNoise.z
#else
uniform mat4 modelMatrix;

// Stable world X/Z for this section. Geometry stays camera-relative for precision,
// but appearance must not move with the camera: a fixed terrain point must always
// sample the same colour variation.
uniform vec4 noiseOrigin;

// Blocks per column in the section being drawn, 1 at level 0 and doubling per level. The
// seasonal row below is picked from a 2-to-6 block noise, which is meaningless once one voxel
// is a neighbourhood, so this is what fades it out. Matches the fragment body's own uniform.
uniform float columnBlocks;

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

// The slot table's FALLBACK and PAGE rows, as uniform arrays rather than texels.
//
// Every vertex reads both - the fallback because it is what draws when the climate path
// cannot finish, the page to decide whether that path runs at all - so as texels they were
// two scattered dependent fetches per vertex on every vertex in the view. 0.3.127 read its
// tints from a uniform array and paid nothing for them; this restores that for the two rows
// that are not optional. The other two rows stay in the texture: they are read only inside
// the climate branch, and four arrays of this size would sit on the guaranteed vertex uniform
// budget. Mirrored from the same packed row the texture is built from; see
// LodTintRegistry.MirrorRow.
uniform vec4 tintFallback[TINT_SLOTS];
uniform vec4 tintPages[TINT_SLOTS];

// The seasonal maps, 128 columns of 16 rows each, one page per map stacked down Y.
//
// The column is the point in the year; the ROW the game picks per position from noise, and for
// LEAVES it draws that noise at a 2-to-6 block scale where ground uses 12-to-24. During the
// autumn turn the sixteen rows run from clearly green to clearly yellow, which is what makes a
// forest turn unevenly. Distant terrain averages them on purpose - one distant pixel covers many
// blocks (G47) - so both are carried and blended by the voxel's own size.
// Only the column of the year this frame draws from: 16 rows per page, at most 32 pages, so
// two kilobytes rather than the whole 136 KiB store. The column is a per-frame constant and
// only the ROW varies per vertex, so this is everything any vertex can reach - and it fits in
// texture cache, which the full map does not. Rebuilt on the CPU when the column moves.
uniform sampler2D seasonColumn;
uniform float leafSeasonVariation;

// The voxel size at which the per-position seasonal variation reaches zero, in blocks.
//
// It is a cost dial as much as a look one. Every section that draws any of the variation pays
// two value noises and a scattered texture fetch PER VERTEX, and sections are the same grid at
// every level - so what decides the bill is how many levels are inside this number, not how
// much area they cover. In a measured view of 110 drawn sections the split was L0:31 L1:57
// L2:9 L3-L6:13: fading out at 8 blocks bought in 97 of them, and L1 alone was more than L0.
// At 2 only level 0 carries it, where one voxel is one block and the noise means exactly what
// it means in vanilla.
uniform float leafSeasonFadeBlocks;

// Vanilla's own liquid warp, carried on cached water near the seam.
//
// The game does not draw its water flat: every liquid vertex is pushed up and down by noise,
// by 0.10 blocks on an inland lake and up to 0.96 on open ocean in wind. Ours was flat, so
// where the two meet vanilla's surface dips below ours and nothing fills the wedge - reported
// in play as an opening at the waterline.
//
// The displacement is the ENGINE'S OWN applyLiquidWarping out of vertexwarp.vsh, which this
// body already includes, so there is no transcription to drift from it (G144). It also means
// the warp follows the player's Waving Foliage setting: ShaderRegistry compiles WAVINGSTUFF
// into every program, and with waving off vanilla's water is flat too, so ours should be.
//
// vhOceanity is the game's own oceanity for this section, which sets the amplitude - it is a
// five-fold swing between a pond and the open sea and cannot be guessed.
uniform float vhOceanity;

// How many blocks past VANILLA'S OWN VIEW DISTANCE the wave fades to nothing.
//
// The geometry that can carry a wave is a one-block strip either side of every 32-block chunk
// plane - the only place a seam can be, since ownership is decided per vanilla chunk. Between
// those strips water is still merged into wide quads, and a wave applied there would displace
// their corners and interpolate flat across the middle, faceting the surface into a grid. So
// the amplitude is held to the ring where the seam actually is and faded to nothing beyond it,
// which is also the only place anyone is close enough to see it.
//
// Measured from `viewDistance` rather than a distance handed in from the CPU. The seam IS
// vanilla's edge, and this shader already knows where that is; the near-field floor that was
// tried first is the ownership cutoff, which is zero by default and would have put the wave
// around the player's feet instead.
uniform float vhWaveReach;

// MEASUREMENT INSTRUMENT, not a look setting. Non-zero replaces the engine's value noise with
// a cheap position hash of similar spread, leaving the scattered seasonMaps fetch and the whole
// blend exactly as they are.
//
// It exists to answer one question the feature's total cost cannot: of the frame time the
// per-tree variation spends, how much is the NOISE and how much is the FETCH. Baking the row
// into the mesh removes only the first, so if that half is small there is nothing to bake for.
// The picture it draws is wrong on purpose - the rows are a hash, not the game's - so this is
// session-only and never saved. See `.vhtint leafcheap`.
uniform int leafSeasonCheapRow;

// The voxel size, in blocks, past which a vertex stops asking for its own climate and takes
// the flat fallback instead. Zero means never - every level pays.
//
// The same argument `leafSeasonFadeBlocks` makes for the season row, applied to the whole
// climate lookup: what decides the bill is how many LEVELS are inside the number, not how
// much area they cover, because a section costs the same per vertex at every level and the
// near levels are not the small ones. The difference is what is being given up. The season
// row is a variation within one biome and vanishes into a distant pixel; the climate is the
// biome itself, and two sections either side of the cut-off would wear different ones. So
// this is deliberately set far enough out that the cut falls between sections whose climates
// have already converged, and it is a switch rather than a constant because only looking at
// the horizon can say where that is. See `.vhtint climatewide`.
uniform float climateFadeBlocks;
uniform int climateFieldEnabled;
uniform int climateTintEnabled;
uniform int climateMinCellX;
uniform int climateMinCellZ;
uniform int climateCapacity;
uniform float climateCellBlocks;
uniform float climateSeaLevel;
uniform int climateRainSeaLevel;
uniform float climateSnowMinY;
uniform float climateSnowStepY;
uniform float snowLineY;
// 1 while `.vhsnowline shader off` compares against 0.4.39's single player-local threshold.
uniform int snowLineUniformOnly;
// 1 while `.vhsnowline fetch off` restores 0.4.39's placement: only a vertex tinted from the
// climate field reads it.
uniform int climateFetchTintOnly;
uniform float seasonTemperature;

// The ENGINE'S own value noise, included rather than transcribed. colormap.vsh builds both the
// row and the column jitter out of it, and a transcription that drifted would put distant leaves
// in a different season from the ones beside them with nothing to say so. See G144.
#include noise3d.ash

// One cell's temperature and rainfall, in 0..255. The wrap is the field's own: a window of
// cells stored in a ring, so a cell address folds into the buffer the same way in both
// languages.
vec4 climateCell(int cellX, int cellZ)
{
    int wrap = climateCapacity - 1;
    return texelFetch(climateField, ivec2(cellX & wrap, cellZ & wrap), 0);
}

// The seasonal colour the game would draw at this position, from colormap.vsh:
//
//   b      = valuenoise(p) + valuenoise(p/2)                     column jitter, 0..2
//   noise  = (valuenoise(p/6)  + valuenoise(p/2)  - 0.55) * 1.25 the row, for LEAVES
//            (valuenoise(p/24) + valuenoise(p/12) - 0.55) * 1.25 the row, for everything else
//   column = clamp(seasonRel + b/40, 0.01, 0.99)
//
// The ROW is reproduced per vertex. The COLUMN JITTER is not, and that is the whole difference
// between this costing two value noises and four.
//
// Dropping the jitter leaves the column a per-frame constant, which is why there is no column
// axis here at all: the CPU takes it at the mean and uploads only that column's rows. See
// LodSeasonMapPages.LiveColumnTexels.
//
// They are not equally worth their price. The row is a choice between sixteen colours that
// during the autumn turn run from clearly green to clearly yellow - it is what makes a wood
// turn unevenly, and it is the variation that is visible at LOD range. The jitter moves the
// year by at most 0.05 and on average 0.025: about three columns of one hundred and twenty
// eight, a dither so fine that `tint` is a smoothly interpolated varying and the quad it is
// interpolated across is usually wider than the noise that produced it.
//
// Taking the column at that MEAN also fixes something the per-vertex version got slightly
// wrong. This colour is mixed with the slot's flat one, and LodSeasonColor builds that flat
// colour by integrating the jitter with triangular weights centred on exactly this value. The
// two colours being blended now sit at the same point in the year instead of drifting up to
// three columns apart depending on the vertex.
//
// Nearest on both axes: a row is a choice, not a gradient, and interpolating between two rows
// would draw a colour the map does not contain.
vec3 seasonAt(int page, vec3 p, bool leaves)
{
    float rowNoise;
    if (leafSeasonCheapRow != 0)
    {
        // A row that still varies across space at roughly the leaf noise's own scale, so the
        // texelFetch below stays as scattered as it really is, for about five instructions
        // instead of twelve perm() calls. Quantised to four blocks for the same reason: a
        // per-vertex hash would scatter the fetch MORE than the real thing and understate what
        // baking could win, which is the safe direction for this measurement to err in.
        vec3 cell = floor(p * 0.25);
        rowNoise = fract(dot(cell, vec3(0.1031, 0.0973, 0.1249)) * 13.0);
    }
    else
    {
        rowNoise = leaves
            ? (valuenoise(p / 6.0) + valuenoise(p / 2.0) - 0.55) * 1.25
            : (valuenoise(p / 24.0) + valuenoise(p / 12.0) - 0.55) * 1.25;
    }

    // Nearest, not interpolated: a row is a choice, and blending two of them would draw a
    // colour the map does not contain.
    int row = clamp(int(rowNoise * 16.0), 0, 15);
    return texelFetch(seasonColumn, ivec2(row, page), 0).rgb;
}

// One climate map page, bilinear. The engine's CPU path truncates to a whole texel; this
// interpolates, because the temperature axis moves one texel per 1.5 blocks of height and a
// nearest lookup would draw the map's own texel grid as steps up every slope. The two agree
// to within one texel either way.
vec4 climateColor(int page, float temperature, float rainfall)
{
    float u = clamp(temperature, 0.0, 255.0);
    float v = clamp(rainfall, 0.0, 255.0);
    int x0 = clamp(int(u), 0, 254);
    int y0 = clamp(int(v), 0, 254);
    int row = page * 256;
    vec4 c00 = texelFetch(climateMaps, ivec2(x0, row + y0), 0);
    vec4 c10 = texelFetch(climateMaps, ivec2(x0 + 1, row + y0), 0);
    vec4 c01 = texelFetch(climateMaps, ivec2(x0, row + y0 + 1), 0);
    vec4 c11 = texelFetch(climateMaps, ivec2(x0 + 1, row + y0 + 1), 0);
    float fx = u - float(x0);
    float fy = v - float(y0);
    return mix(mix(c00, c10, fx), mix(c01, c11, fx), fy);
}

out vec3 tint;
out float tintAlpha;
out float snowLineAtVertex;
out float frostAtVertex;
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
