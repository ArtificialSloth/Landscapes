
// Fog application and far sky-fade structure adapted from Farseer's region.fsh
// (github.com/ViciousBadger/VSMod-Farseer, MIT, (c) Badgerson). Unlike Farseer we
// have real per-vertex surface colors, shaded with screen-space-derivative normals.

in vec4 worldPos;
in vec4 vertexColor;
in float yLevel;
in vec4 rgbaFog;
in float dist;
in float radialDistance;
in float fogAmount;
in float edgeFade;
in vec3 tint;
in vec3 terrainPos;
in vec3 sectionLocal;

uniform float fogDensityIn;
uniform float fogMinIn;
uniform float horizonFog;
uniform vec3 sunPosition;
uniform vec3 sunColor;
uniform float dayLight;
uniform float cacheHandoffDistance;

// Per-cell vanilla ownership. One texel per 32x32x32 vanilla render chunk, stored as Y
// slices stacked down a single 2D texture because the public client API binds 2D textures
// only. maskEnabled stays 0 until the chunk mask is both requested and uploaded, so the
// default path is exactly the distance handoff above. Addressing must match
// VanillaReadinessMask.TexelIndex; a static check holds the two together.
uniform sampler2D readinessMask;
uniform int maskEnabled;
uniform int maskMinX;
uniform int maskMinZ;
uniform int maskWidth;
uniform int maskDepth;
uniform int maskCapacity;
uniform int maskVerticalChunks;

// 1 paints hidden pixels red instead of hiding them. Diagnostic only.
uniform int maskDebug;

// ---- Cached water ------------------------------------------------------------
//
// An optional near-field cutoff for cached WATER alone, measured in whole vanilla CHUNKS
// around the camera's own chunk, and OFF by default.
//
// No radius. A per-pixel distance test is a circle, a circle is a shape the world does not
// contain, and it reads as an effect painted over the terrain rather than as terrain - the
// same rule the blend band is built on. 0.3.141 used a radius and drew a hard ring across
// every lake; 0.3.144 shrank it and drew a smaller one. So this is a Chebyshev distance in
// 32-block chunk columns: its boundary can only ever run along chunk edges, in the game's own
// grid, and at the default of zero it does not exist at all.
//
// The boundary between cached water and vanilla's is the ownership mask below - vanilla's
// real chunk footprint - exactly as it is for solid ground. This is only a fallback for the
// near field, where the mask can be slow to commit and the engine's own water is transparent
// enough for a cached sheet under it to tint it. See .vhwater.
uniform int waterNearChunks;

// The camera's own vanilla chunk column, as two integer scalars. Never a Vec2i: the client's
// only integer-pair setter reaches glUniform2f, which an integer uniform rejects outright, so
// the value would silently stay at zero. See G42.
uniform int camChunkX;
uniform int camChunkZ;

// Cached water obeys the ownership mask exactly as solid ground does, at every distance.
// There is no outward allowance and no radius: an allowance measured from the camera is a
// circle, and a circle drew cached water straight into chunks vanilla was already drawing.
// The boundary is the mask's own 32-block cells, so it falls on vanilla chunk edges and
// nowhere else.

// 1 gives cached water the engine's own opacity instead of a flat constant. chunkliquid.vsh
// grades water by view angle - about 16% opaque looking straight down at a pond at your feet,
// clamped fully opaque by roughly 25 blocks out - so a flat 0.66 is simultaneously too opaque
// up close and too transparent at the seam, where it let the cached lakebed show through a
// surface vanilla draws solid. This is a property of the surface, not a correction, so it is
// applied everywhere rather than on the band: band-weighting it would create a fresh seam of
// its own.
uniform int waterFresnel;

// The two surface terms vanilla's water has and cached water did not, both weighted by the
// SAME blend band the ground uses - full at the vanilla seam, relaxing outward in the band's
// own 8-block steps.
//
// glint is the specular sun reflection on a noise-perturbed surface normal. It is the loudest
// single difference on a sunny day: the game's water is shiny to the horizon and ours went
// matte at the line. foam is vanilla's open-water surface noise, which is the same argument
// bandSpeckle makes for ground - vanilla's water is never one flat colour.
//
// Band-weighted rather than global because these are the expensive per-pixel terms, because
// past the band fog dominates and the detail is sub-pixel anyway, and because the whole point
// is to match vanilla where vanilla is standing next to us.
//
// What is NOT reproduced here is vanilla's shoreline rim, which it derives by sampling the
// depth texture during OIT. This shader runs in the opaque stage where that same depth buffer
// is the live render target, and sampling an attachment while it is bound is undefined - so
// shore foam has to come from mesh data instead, and does not exist yet.
uniform float waterGlint;
uniform float waterFoam;

// Engine state the liquid shader uses, uploaded under mod-owned names so nothing here can
// collide with a uniform the engine manages on its own programs. All are public API:
// ShaderUniforms.SunPosition3D / SunSpecularIntensity / WaterWaveCounter / WindWaveCounter /
// WaterFlowCounter / WindSpeed / PlayerPosForFoam, and Calendar.ReflectColor.
uniform vec3 vhSunPosRel;
uniform vec3 vhReflectColor;
uniform float vhSunSpecular;
uniform float vhWaterWave;
uniform float vhWindWave;
uniform float vhWaterFlow;
uniform float vhWindSpeed;

// The engine's own foam anchor: an absolute world position re-based to a reference point that
// only moves when the player leaves a 200-block radius. Using it rather than a raw world
// coordinate is what keeps the noise both stable under the player and precise at map edges -
// and it puts our noise in exactly the phase vanilla's water is in.
uniform vec3 vhFoamOrigin;
// -------------------------------------------------------------------------------

// ---- Blend band --------------------------------------------------------------
//
// Cached terrain immediately outside the vanilla seam is made to resemble vanilla, relaxing
// back to its own appearance over a band, so the eye has no single line to find.
//
// The difference is a TEXTURE difference, not a colour one, and two attempts to treat it as a
// colour failed in play before that stuck. Vanilla terrain is never a single flat colour:
// every block shows a texture tile. Cached terrain is one averaged colour per block. Matching
// the average cannot close a difference that is not in the average, which is why a per-channel
// gain and then a darken/saturate pair each looked right on the terrain they were tuned on and
// worse than nothing on the next one. bandSpeckle below is the term that addresses the actual
// defect; darken and saturate remain, defaulting to neutral, because the average may still be
// slightly off once the variation is there.
//
// The band spreads what remains over many small steps; it does not delete it.
//
// One texel per 8x8 block cell, holding distance from the nearest vanilla-owned ground in
// cells. 8 blocks is a quarter of a vanilla chunk: fine enough to read as a smaller
// continuation of the game's blocky idiom, and never finer than one world block, which is
// the rule an earlier smooth radial transition broke - a gradient below the world's own grid
// reads as a GPU effect painted over the terrain rather than as terrain. Nothing here is a
// function of distance from the camera. Addressing must match
// VanillaSeamDistanceField.TexelIndex.
uniform sampler2D seamField;
uniform int bandEnabled;
uniform int bandMinCellX;
uniform int bandMinCellZ;
uniform int bandWidthCells;
uniform int bandDepthCells;
uniform int bandCapacity;

// Band reach in blocks, the ramp's step size in blocks, and how much of the correction to
// apply at the seam. Step is quantised no finer than the stored cell.
uniform float bandWidthBlocks;
uniform float bandStepBlocks;
uniform float bandStrength;

// How far the measured seam sits inside the seam a player can actually see, in blocks.
// Ownership is deliberately denied for the outermost chunk and a half of live vanilla
// terrain - the engine range-culls whole meshes and can stop drawing a chunk it still
// reports - so the field's zero is that much short of where vanilla's ground really ends.
// Without this the full correction would land underneath live vanilla, where nothing can
// see it, and the visible seam would get only part of it.
uniform float bandInnerBlocks;

// Two operators applied at full weight, both functions of the pixel rather than assertions
// about it. Darken is the measured luminance ratio at the seam; saturate is how much further
// from grey vanilla sits once both sides are normalised by their own luminance. A per-channel
// red/green/blue gain was tried first and failed in play: three multipliers tuned on grass
// encode grassland's own colour, so carrying them to sand applied grassland's correction to a
// colour that was never grassland.
uniform float bandDarken;
uniform float bandSaturate;

// The one that matters. Vanilla terrain is never a single flat colour: every block shows a
// texture tile, and at seam distances that reads as fine per-block speckle. Cached terrain is
// one averaged colour per block with nothing on top of it, so the transition is speckled to
// perfectly flat, and no adjustment to the average can close a difference that is not in the
// average. This puts the variation back - not by sampling vanilla's textures, which cached
// meshes carry no identity to look up, but by reproducing their statistics: one random value
// per world block, faded out across the band so it hands over to flat cached terrain
// gradually instead of at a line.
//
// Per BLOCK, never finer. Vanilla's own texture detail is below a pixel at this range anyway,
// and anything varying below the world's grid reads as an effect painted over the terrain.
uniform float bandSpeckle;

// 1 paints the band's weight instead of correcting it, so its shape can be judged from above
// before any colour decision is made. Diagnostic only.
uniform int bandDebug;

#ifdef VH_PACKED
// The game's own block atlas, sampled directly. Not a recreation of vanilla's textures and
// not another approximation of their average - the real texels, on cached ground, faded in
// across the seam band by the same weight above.
//
// The rectangle arrives flat from the vertex stage; it is constant across a quad, and a quad
// can span forty blocks, so texture coordinates are derived from world position here rather
// than stored. That is also why the wrap needs explicit gradients: fract() tears once per
// block, and a GPU left to infer the derivative from it picks the coarsest mip on every
// block boundary and draws a grid of grey lines.
uniform sampler2D blockAtlas;
uniform float blockAtlasSize;
uniform int blockTextureEnabled;
uniform float blockTextureStrength;
uniform float blockTextureMipLimit;

// How many mip levels SHARPER than the distance warrants to sample.
//
// Correct minification is why a faithful texture does nothing here: shrink a 32-pixel tile
// into three screen pixels and the hardware hands back that tile's average, which - since the
// texture is applied as variation around the block's colour - multiplies by one and changes
// nothing at all. The detail is thrown away before it can be seen, and this reaches back up
// the mip chain for it. Zero is physically correct and invisible; higher is deliberately
// wrong, trading aliasing for detail the eye can actually resolve.
uniform float blockTextureDetail;

// The block table, read here rather than in the vertex stage. Its rows are needed only inside
// the blend band, and the band is a ring of the screen against millions of vertices, so the
// fetches belong on the side that actually asks. -1 in the slot means the quad carries a
// plain colour and there is nothing to read.
uniform sampler2D blockTable;
flat in int vhBlockSlot;
#endif
// ------------------------------------------------------------------------------

// 1 applies vanilla's own rule that an up-facing surface never darkens with the sun.
uniform int flatTopLight;

// The four terms below each close one measured gap between this shader and vanilla's
// chunkopaque pair, and each has its own switch so a single dusk-to-night sweep can
// attribute what it sees. See .vhlight. All default to on.
//
// 1 shades by the engine's own light vector, which follows the sun by day and swings
// to the moon once moonlight beats sunlight; 0 keeps shading by the sun all night.
// lightPosition is declared by fogandlight.fsh and uploaded by the engine on every
// Use(), so it costs nothing to read here.
uniform int lightMoonDir;

// 1 uses vanilla's ramp and floor, max(0.45, 0.5 + 0.5 * dot); 0 keeps the mod's
// 0.55 + 0.45 * max(0, dot), which is 22% too bright on every away-facing slope.
uniform int lightVanillaRamp;

// 1 lights by the engine's blended ambient colour, which is what vanilla's terrain
// actually multiplies by; 0 keeps sunColor * dayLight. This is the dominant term:
// vanilla builds its ambient from ReflectColor, which is floored at a blue night
// colour once the sun is down, while sunColor stays on the orange end of the sunlight
// ramp. Measured offline, the two hues invert after sundown - vanilla goes blue-grey
// and this shader goes orange.
uniform int lightAmbientColor;
uniform vec3 rgbaAmbientIn;

// 1 applies vanilla's daylight brightening, 1 + max(0, shadowIntensity * 2 - 1.66)/1.5.
// shadowIntensity is the engine's DropShadowIntensity, which is 1 while the sun is high
// and falls to 0 as it sets, so this is worth 22.7% extra brightness at midday and
// nothing at dusk. Also declared and uploaded by fogandlight.fsh.
uniform int lightDayBoost;

// Vanilla's baked SUN LIGHT LEVEL, and the largest single error this shader has carried.
//
// applyLight multiplies by `lightColor.a`, the sun light level baked into every terrain
// vertex. The derivation that produced the ambient term below read the LOD case as "full
// sky light" and cancelled that factor as if it were 1. It is not: `bMax` renormalisation
// cancels the ambient colour's own mean, not the sun level, so the level survives and
// multiplies the pixel. Open sky is not level 31 either - it is the world's `sunBrightness`,
// 22 in the base game, which is 0.806 on the engine's ramp and 206/255 = 0.808 once the
// engine has quantised it into the vertex byte the shader actually reads.
//
// So every cached surface was 1/0.808 = 1.24x too bright, at every hour, on every block.
// Measured on a flat two-block test patch at the seam: cached granite gravel came out 1.39x
// vanilla's, of which 1.07x was the stored colour and the remaining 1.29x was this.
//
// Uploaded as 1.0 when `.vhlight sun` is off, which is exactly the old behaviour and costs
// no branch. Only the vanilla-ambient path takes it: the sunColor fallback beside it is the
// pre-0.3.51 model kept for comparison, and scaling that would make it a third thing that
// was never either.
uniform float sunLightLevel;

// 1 mixes the far dissolve band toward the sky the engine actually draws. Every engine
// shader that calls getSkyColorAt passes SkyDaylight, not DayLightStrength; the two
// differ by a factor of 1.25 and by a night floor, so the band this shader fades terrain
// into did not match the sky behind it, and the mismatch moved with the time of day.
// SkyDaylight is internal to the engine, but every input to it is public, so the renderer
// reconstructs it. See .vhlight sky.
uniform int lightSkyDayLight;
uniform float skyDayLight;

// ---- Per-section facts ------------------------------------------------------
//
// The two values below describe the section rather than the fragment. The established
// path sets each as a uniform before that section's draw; the indirect variant has no
// per-draw uniforms and receives them from the vertex stage as flat varyings, filled
// from LodGpuSectionRecord. The #defines keep the body below identical in both
// variants. See the matching block in lodterrainbody.vsh.
#ifdef VH_INDIRECT
flat in float vhColumnBlocks;
flat in ivec2 vhMaskSectionOrigin;

#define columnBlocks vhColumnBlocks
#define maskSectionOriginX vhMaskSectionOrigin.x
#define maskSectionOriginZ vhMaskSectionOrigin.y
#else
// This section's origin in whole vanilla chunks. Section origins are multiples of the
// chunk size, so this is exact, and adding a small local offset to it cannot round.
//
// Two scalars rather than one ivec2 on purpose. The client's only vector setter for a
// pair of integers is Uniform(name, Vec2i), which calls glUniform2f; against an integer
// uniform that is GL_INVALID_OPERATION and the value never arrives at all. See G42.
uniform int maskSectionOriginX;
uniform int maskSectionOriginZ;

// Blocks per column in the section being drawn (1 at level 0, doubling per level).
// Coarse sections merge whole neighbourhoods into one colour, and greedy meshing
// then fuses them into large single-colour quads; a little world-space variation
// scaled to the column size breaks those plates up without inventing detail.
// Scaling by column size is what keeps the pattern roughly constant on screen
// instead of aliasing into shimmer at distance.
uniform float columnBlocks;
#endif
// -----------------------------------------------------------------------------

// Live tint table. The alpha byte carries a tint SLOT plus a blend band:
//   0..63    opaque,     slot = alpha
//   64..127  water,      slot = alpha - 64
//   128..191 thin plant, slot = alpha - 128
// Slot 0 is the identity tint. One slot per distinct (climate map, season map) pair,
// because leaves pick a seasonal map per species and water has its own -- a single
// shared foliage tint left every tree the same colour and water untinted grey.
// Sampled at two heights and blended by vertex height: the climate maps are indexed
// by temperature, which drops with altitude, so one sample at the player's feet gave
// mountaintops the same lush green as the valley floor.
// Must equal LodTintRegistry.MaxSlots; LoadShader logs an error if it does not.
const int TINT_SLOTS = 64;
uniform float snowLineY;

// Blend factors per band, now that alpha carries the slot instead of an opacity.
// Flowers are crossed quads in vanilla; as a solid cube they read as a grey blob, so
// they are drawn mostly see-through and the ground shows through them.
const float WATER_ALPHA = 0.66;
const float THIN_ALPHA = 0.50;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outGlow;
#if SSAOLEVEL > 0
layout(location = 2) out vec4 outGNormal;
layout(location = 3) out vec4 outGPosition;
#endif

#include noise3d.ash
#include dither.fsh
#include fogandlight.fsh
#include skycolor.fsh
#include underwatereffects.fsh

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

void main()
{
    // Which blend band this fragment is in: 0 opaque, 1 water, 2 thin cover. Needed before
    // the near cutoff because water has its own.
    int ownershipBand = int(vertexColor.a * 255.0 + 0.5) / TINT_SLOTS;

    // Retain a broad fallback overlap for chunks that are still streaming, then give
    // the close field entirely to ready vanilla terrain so the approximate surfaces do
    // not mix. This boundary is deliberately much nearer than the old 78.5% cutoff.
    //
    // Water takes the larger of the two cutoffs. Its own is a small absolute distance
    // shared with solid ground. Water gets nothing extra here: its own near rule is a chunk
    // count, applied below, and it is off by default.
    if (radialDistance < cacheHandoffDistance || dist > 1.0) discard;

    // This fragment's vanilla chunk cell. Hoisted out of the ownership block because the
    // water near rule needs the same address: both must be an integer section origin plus a
    // section-local offset, never a summed world coordinate, or a fragment at a chunk edge
    // rounds onto its neighbour at large world coordinates.
    //
    // A WALL DOES NOT SIT INSIDE A CELL. It sits in the plane between two of them, and
    // floor() cannot say which one holds the block it belongs to. The mesher emits each wall
    // at its own block's face, so a west wall lands on that block's near edge and an east
    // wall lands on its FAR edge - and an east wall standing on a chunk boundary is therefore
    // drawn at the next chunk's origin, where floor() charges it to that neighbour. Where the
    // neighbour was owned and the wall's own chunk was not, the wall was discarded and vanilla
    // drew nothing in its place: a missing vertical face at the seam, on the -X and -Z sides
    // only, because on the other two the mis-charged face points away and is back-face culled
    // anyway. The plane is recorded here; the ownership test below asks every candidate.
    float planeOffsetX = maskPlaneOffset(sectionLocal.x);
    float planeOffsetZ = maskPlaneOffset(sectionLocal.z);
    int edgeX = abs(planeOffsetX) < MASK_PLANE_SLACK ? 1 : 0;
    int edgeZ = abs(planeOffsetZ) < MASK_PLANE_SLACK ? 1 : 0;
    // Snapped onto the plane before the divide, so a last-bit interpolation difference
    // cannot decide which PAIR of cells is asked. Off a plane this is the same floor as
    // before, to the bit.
    int cellX = maskSectionOriginX + int(floor((sectionLocal.x - planeOffsetX * float(edgeX)) / 32.0));
    // Y needs no second candidate. A top face is drawn at its block's ceiling, so it charges
    // to the cell ABOVE the block - and an air cell is never marked owned (G40), so that error
    // can only ever leave cached ground drawn, which is the safe direction. A floor face and
    // every wall fragment away from a ceiling already address their own cell.
    int cellY = int(floor(sectionLocal.y / 32.0));
    int cellZ = maskSectionOriginZ + int(floor((sectionLocal.z - planeOffsetZ * float(edgeZ)) / 32.0));

    // Cached water immediately around the camera, by CHUNK rather than by distance, so its
    // boundary runs along chunk edges instead of drawing a circle. Off at zero.
    if (ownershipBand == 1 && waterNearChunks > 0
        && max(abs(cellX - camChunkX), abs(cellZ - camChunkZ)) < waterNearChunks)
    {
        discard;
    }

    // Ownership is decided before normals, noise, tint, lighting, fog, sky and water work,
    // so an owned fragment costs one point fetch rather than a whole shaded pixel. Every
    // address outside the tracked window, above or below the world, or reading a
    // cache-owned texel falls through to normal cached drawing: the mask can only take
    // ground away from the cache where vanilla has proven it draws there.
    // WATER IS UNDER THIS RULE TOO, at every distance and with no exception of any kind.
    //
    // Water carried an outward allowance for two builds: ownership was barred from hiding it
    // past a fraction of the view distance, on the argument that the engine's liquid alpha can
    // reach zero while its ground keeps drawing, leaving a ring where vanilla draws a bed and
    // no river (G124). Whatever that argument was worth, the allowance was a RADIUS, and past
    // it cached water drew straight into chunks vanilla was already drawing - measured in play
    // as cached water inside vanilla territory, which is the one thing this rule exists to
    // prevent. A boundary that ignores the chunk grid is not a boundary; it is an effect.
    //
    // The mask's cells are whole 32-block vanilla chunks, so this edge falls on chunk edges
    // and nowhere else. If G124's dry riverbeds return they will be visible as a bed with no
    // water at distance, and the answer will be a better ownership signal, never a radius.
    if (maskEnabled == 1)
    {
        // EVERY cell that could hold the block behind this fragment has to say yes before
        // anything is hidden. One cell for an ordinary surface, which is the only lookup the
        // overwhelming majority of fragments pay; two for a wall lying in a chunk plane; four
        // on the line where two such walls meet at a chunk corner. If any candidate is not
        // owned then the block may be that one, vanilla may be drawing nothing there, and
        // hiding the face would remove the only geometry covering that ground.
        //
        // Conservative on purpose, and the conservatism is cheap: the extra candidate can
        // only keep a cached wall drawn one chunk further in than before, over ground vanilla
        // is drawing too, which is overlap. The other direction is a hole, and overlap
        // outranks holes.
        bool owned = vanillaOwnsCell(cellX, cellY, cellZ, ownershipBand);
        if (owned && edgeX == 1) owned = vanillaOwnsCell(cellX - 1, cellY, cellZ, ownershipBand);
        if (owned && edgeZ == 1) owned = vanillaOwnsCell(cellX, cellY, cellZ - 1, ownershipBand);
        if (owned && edgeX == 1 && edgeZ == 1)
        {
            owned = vanillaOwnsCell(cellX - 1, cellY, cellZ - 1, ownershipBand);
        }

        if (owned)
        {
            // Debug mode paints instead of discarding, which turns an ambiguous gap
            // into a yes/no question: red means the mask hid this pixel, and a gap
            // that stays empty in this mode was never the mask's doing.
            if (maskDebug == 1)
            {
                // MAGENTA, not red. The band's shape view paints red too, and a red
                // region in a screenshot could not be attributed to either one.
                outColor = vec4(1.0, 0.0, 1.0, 1.0);
                outGlow = vec4(0.0);
#if SSAOLEVEL > 0
                outGNormal = vec4(0.0);
                outGPosition = vec4(0.0);
#endif
                return;
            }
            discard;
        }
    }

    // How far this fragment is from vanilla's own ground, as a 0..1 ramp: 1 at the seam,
    // 0 at the far edge of the band. Derived from the same integer section origin plus
    // section-local offset the ownership test above uses, for the same reason - a summed
    // world coordinate rounds a fragment near a cell edge onto its neighbour at large world
    // coordinates. Cells outside the field's window are ordinary cached terrain.
    float bandWeight = 0.0;
    if (bandEnabled == 1)
    {
        int fieldX = maskSectionOriginX * 4 + int(floor(sectionLocal.x / 8.0));
        int fieldZ = maskSectionOriginZ * 4 + int(floor(sectionLocal.z / 8.0));
        if (fieldX >= bandMinCellX && fieldX < bandMinCellX + bandWidthCells
            && fieldZ >= bandMinCellZ && fieldZ < bandMinCellZ + bandDepthCells)
        {
            int fieldWrap = bandCapacity - 1;
            ivec2 fieldTexel = ivec2(fieldX & fieldWrap, fieldZ & fieldWrap);
            float seamCells = floor(texelFetch(seamField, fieldTexel, 0).r * 255.0 + 0.5);
            // Measured from the ownership edge, then moved out to the edge a player sees.
            float seamBlocks = max(0.0, seamCells * 8.0 - bandInnerBlocks);

            // Quantise to whole steps so the ramp reads as terrain rather than as a smooth
            // gradient. Never finer than the stored cell: below one world block is the
            // failure mode this whole design exists to avoid. The subtraction above cannot
            // break that - the distance itself is constant across each 8-block cell, so this
            // only decides which step a whole cell falls into.
            float bandStep = max(8.0, bandStepBlocks);
            float stepped = floor(seamBlocks / bandStep) * bandStep;
            bandWeight = clamp(1.0 - stepped / max(1.0, bandWidthBlocks), 0.0, 1.0);
        }
    }

    // Shape before colour. Painting the weight answers "is the ring the right shape and does
    // the stepping read as blocky" without any correction being involved, so a bad shape and
    // a bad correction cannot be mistaken for one another.
    if (bandDebug == 1 && bandWeight > 0.0)
    {
        // Two palettes, because "the red thing is a square" and "the band is a circle" were
        // both true at once and there was no way to tell which surface each belonged to.
        // GROUND ramps blue-yellow-red. WATER ramps dark-green to bright green. Any solid
        // block of one colour is a weight that is not varying, and its hue says which pass
        // drew it - which is the question two builds of screenshots could not answer.
        vec3 ramp = ownershipBand == 1
            ? mix(vec3(0.0, 0.25, 0.0), vec3(0.2, 1.0, 0.2), bandWeight)
            : (bandWeight > 0.5
                ? mix(vec3(1.0, 1.0, 0.0), vec3(1.0, 0.0, 0.0), (bandWeight - 0.5) * 2.0)
                : mix(vec3(0.0, 0.6, 1.0), vec3(1.0, 1.0, 0.0), bandWeight * 2.0));
        outColor = vec4(ramp, 1.0);
        outGlow = vec4(0.0);
#if SSAOLEVEL > 0
        outGNormal = vec4(0.0);
        outGPosition = vec4(0.0);
#endif
        return;
    }

    // Flat-shaded facet normal from position derivatives - no normals in the mesh.
    vec3 normal = normalize(cross(dFdx(worldPos.xyz), dFdy(worldPos.xyz)));

    // sunPosition arrives as Calendar.SunPositionNormalized, so it is already a unit
    // vector. The call below passes it to getSkyColorAt unnormalized for the same reason.
    //
    // lightPosition is the engine's own shading vector: the sun position lerped toward
    // the moon as moonlight overtakes sunlight. Vanilla terrain shades by that, not by
    // the sun, so at night vanilla lights slopes from the moon while this shader was
    // still lighting them from a sun below the horizon.
    vec3 lightVec = lightMoonDir == 1 ? lightPosition : sunPosition;
    float sunAngle = dot(normal, lightVec);
    float shade = lightVanillaRamp == 1
        ? max(0.45, 0.5 + 0.5 * sunAngle)
        : 0.55 + 0.45 * max(0.0, sunAngle);

    // Vanilla never lets an up-facing surface darken as the sun drops. Its
    // getBrightnessFromNormal floors the shade at normal.y * 0.95, with a comment in the
    // engine's own source saying that block tops coming out darker than block sides looks
    // uncanny; its liquid shader does not shade by normal at all. Without the same floor,
    // cached ground fell to 0.55 at dawn and dusk while the vanilla ground beside it stayed
    // near 0.95, and the two only matched around midday - reported as the colour matching
    // well at some times of day and not others, after the albedo itself was already exact.
    //
    // A max, so this can only ever brighten: cliffs and side faces keep the shading they
    // had, and only surfaces that actually face upwards are affected.
    //
    // Behind a switch because it is a judgement call about how distant ground should look at
    // dawn and dusk, and the only way to settle that is to flip it while looking at the
    // ground. See .vhtoplight.
    if (flatTopLight == 1) shade = max(shade, clamp(normal.y, 0.0, 1.0) * 0.95);

    // Decode the tint slot, then snow line on up-facing terrain.
    // Only the blend band is needed here; the tint itself arrives interpolated.
    int band = int(vertexColor.a * 255.0 + 0.5) / TINT_SLOTS;  // 0 opaque, 1 water, 2 thin
    bool translucent = band > 0;

    vec3 albedo = vertexColor.rgb * tint;
    float outAlpha = band == 2 ? THIN_ALPHA : (band == 1 ? WATER_ALPHA : 1.0);

    // A cached water surface faces up unless it is a wall; the derivative normal's SIGN is
    // whichever way the triangle happened to be wound on screen, so only its magnitude can be
    // trusted here. Vanilla splits on exactly the same threshold, fragNormal.y < 0.5.
    bool waterTop = abs(normal.y) > 0.5;

    // Vanilla's water opacity, transcribed from chunkliquid.vsh. The eye vector is biased
    // 3.5 blocks up, as vanilla's is, and the surface normal is up: the result is a grazing
    // -angle term that is nearly transparent at your feet and clamps opaque within a few tens
    // of blocks. Cached water was a flat 0.66 at every distance, which is why the cached
    // lakebed showed through at the seam where vanilla's water is solid.
    if (band == 1 && waterFresnel == 1)
    {
        vec3 eyeFresnel = normalize(vec3(worldPos.x, worldPos.y - 3.5, worldPos.z));
        // max() rather than vanilla's bare add: a dot product one ulp below -1 would make
        // the cube of a negative number, which is undefined for pow and free here.
        float rise = max(0.0, 1.0 + eyeFresnel.y);
        float fresnel = max(0.2, 0.01 + 4.5 * rise * rise * rise);
        outAlpha = clamp(0.8 * fresnel, 0.0, 1.0);
        if (!waterTop) outAlpha *= 0.3333333;
    }

#ifdef VH_PACKED
    // Real vanilla texels, where the band asks for them.
    //
    // The table rows are read HERE rather than in the vertex stage. They are wanted only by a
    // fragment that is inside the band, on an opaque quad that carries a slot, with textures
    // switched on - and the band is a ring of the screen, against millions of vertices. Read
    // per vertex they were three dependent fetches and eleven floats of flat varying paid by
    // every vertex in the view, textures on or off. See lodterrainbody.vsh's vhBlockSlot.
    if (!translucent && blockTextureEnabled == 1 && bandWeight > 0.0 && vhBlockSlot >= 0)
    {
        // Row 0 is the atlas rectangle. An empty one means the slot describes no texture,
        // which is the same "nothing to draw here" the vertex stage used to answer with a
        // zeroed rectangle. Row 1 is the averaged colour and was consumed as this quad's
        // albedo already.
        vec4 vhBlockRect = texelFetch(blockTable, ivec2(vhBlockSlot, 0), 0);
        if (vhBlockRect.z > vhBlockRect.x)
        {
            // Row 2 is the sampled texture's own mean, with alpha flagging whether a
            // grass-style overlay exists; row 3 is that overlay's rectangle. The fragment
            // stage divides by the mean so the texture becomes variation AROUND the block's
            // colour rather than the colour itself - grass-covered ground has a brown soil
            // texture under a green overlay, so taking the texture at face value drew a brown
            // band across the world.
            vec4 vhMeanRow = texelFetch(blockTable, ivec2(vhBlockSlot, 2), 0);
            vec3 vhBlockMean = vhMeanRow.rgb;
            vec4 vhBlockOverlay = vhMeanRow.a > 0.5
                ? texelFetch(blockTable, ivec2(vhBlockSlot, 3), 0)
                : vec4(0.0);

            // Tile the block's own texture once per world block, on whichever plane this face
            // lies in. terrainPos is the stable world position, so the pattern is anchored to
            // the ground rather than to the camera.
            vec2 tile = abs(normal.y) > 0.5
                ? terrainPos.xz
                : (abs(normal.x) > 0.5 ? vec2(terrainPos.z, -terrainPos.y)
                                       : vec2(terrainPos.x, -terrainPos.y));
            vec2 rectSize = vhBlockRect.zw - vhBlockRect.xy;
            vec2 blockUv = vhBlockRect.xy + fract(tile) * rectSize;
            vec2 duvdx = dFdx(tile) * rectSize;
            vec2 duvdy = dFdy(tile) * rectSize;

            // How far into the mip chain this fragment is asking to go. Deep mips blend a
            // block's tile into its neighbours' in the shared atlas, which would put another
            // block's colour on this ground, so past a limit the texture bows out and the
            // averaged colour - what a fully minified texture converges to anyway - takes
            // over. That is also free performance: the far field never pays for a fetch.
            //
            // The limit cannot be cautious. The seam sits hundreds of blocks out, where one
            // block covers a few pixels and a 32-pixel tile is already asking for mip three
            // or four, so a low limit would remove the texture at exactly the distance it
            // exists for.
            vec2 footprint = max(abs(duvdx), abs(duvdy)) * blockAtlasSize;
            float mipWanted = log2(max(max(footprint.x, footprint.y), 1.0));
            float mipFade = clamp(blockTextureMipLimit - mipWanted, 0.0, 1.0);

            float textureAmount = bandWeight * clamp(blockTextureStrength, 0.0, 1.0) * mipFade;
            if (textureAmount > 0.0)
            {
                // Variation around the block's colour, never the colour itself. Dividing the
                // texel by its own texture's mean leaves a number that is 1 where the texture
                // is average and swings either side of it where it has detail, so the ground
                // keeps exactly the colour it had and gains the pattern. Taking the texel
                // directly instead drew grass as bare soil: the green is an overlay
                // composited over a brown base, and the base is what the atlas holds.
                //
                // Explicit level, not the inferred one. fract() tears once per block, so a
                // GPU left to derive the level itself picks the coarsest mip on every block
                // boundary and draws a grid of grey lines; and the whole point here is to
                // choose a level sharper than the footprint asks for anyway.
                float sampleLod = max(mipWanted - blockTextureDetail, 0.0);
                vec3 texel = textureLod(blockAtlas, blockUv, sampleLod).rgb;

                // Grass over soil, composited exactly the way the engine's own top-soil
                // shader does it: soil * (1 - grass.a) + grass * grass.a, per pixel. The
                // overlay tiles on the same block grid, so it reuses the same coordinate.
                if (vhBlockOverlay.z > vhBlockOverlay.x)
                {
                    vec2 overlaySize = vhBlockOverlay.zw - vhBlockOverlay.xy;
                    vec4 overlayTexel = textureLod(
                        blockAtlas, vhBlockOverlay.xy + fract(tile) * overlaySize, sampleLod);
                    texel = mix(texel, overlayTexel.rgb, overlayTexel.a);
                }
                vec3 detail = texel / max(vhBlockMean, vec3(0.02));
                albedo *= mix(vec3(1.0), detail, textureAmount);
            }
        }
    }
#endif


    if (!translucent) {
        float upness = clamp(normal.y, 0.0, 1.0);
        float snowMix = smoothstep(snowLineY, snowLineY + 24.0, yLevel) * upness;
        albedo = mix(albedo, vec3(0.93, 0.94, 0.97), snowMix);
    }

    // Water is a smooth surface; only break up land.
    if (!translucent) {
        float period = max(4.0, columnBlocks * 6.0);
        float n = valuenoise(terrainPos / period);
        albedo *= 1.0 + 0.10 * (n - 0.5);
    }

    // Wear vanilla's colouring at the seam and relax outward. Land only: the owner reports
    // cached water surrounded by cached water already looks right, and the water defect at
    // the transition is depth fighting on the bed rather than the surface's colour.
    // Applied to albedo rather than to the finished pixel so it passes through lighting and
    // fog exactly as the surface's own colour does.
    if (!translucent && bandWeight > 0.0) {
        float bandAmount = bandWeight * clamp(bandStrength, 0.0, 1.0);

        // One value per world block. The coordinate is wrapped to a 256-block period first:
        // terrainPos runs to hundreds of thousands of blocks, where a float hash loses its
        // low bits and collapses into visible banding, and a 256-block repeat of random
        // per-block speckle is not something an eye can find.
        vec3 speckleCell = mod(floor(terrainPos), 256.0);
        float speckle = fract(
            sin(dot(speckleCell, vec3(127.1, 311.7, 74.7))) * 43758.5453);
        albedo *= 1.0 + bandSpeckle * (speckle - 0.5) * 2.0 * bandAmount;

        // Rec. 709 luminance, so the split into "how bright" and "how far from grey" matches
        // what the eye weights rather than a flat channel average.
        float bandLuma = dot(albedo, vec3(0.2126, 0.7152, 0.0722));
        vec3 saturated = vec3(bandLuma) + (albedo - vec3(bandLuma)) * bandSaturate;
        albedo = mix(albedo, saturated, bandAmount) * mix(1.0, bandDarken, bandAmount);
    }

    // Vanilla's light term, derived by reading applyLight in fogandlight.vsh for the
    // case a LOD section is in: sky light, no baked block light, no glow. Every scale
    // factor there cancels except the contrast constant and the sun light level, leaving
    // the pixel multiplied by 1.05 * lightColor.a * ambientColor. The ambient colour
    // already carries the sunset hue, the night blue, and the scene-brightness dimming,
    // so nothing here has to re-derive any of them; sunLightLevel is the sun level, and
    // the comment on it says why treating it as 1 was worth 24% brightness everywhere.
    vec3 light = lightAmbientColor == 1
        ? 1.05 * sunLightLevel * rgbaAmbientIn
        : clamp(sunColor * dayLight, 0.02, 1.0);
    if (lightDayBoost == 1) light *= 1.0 + max(0.0, shadowIntensity * 2.0 - 1.66) / 1.5;

    vec4 terraColor = vec4(albedo, outAlpha);
    terraColor.rgb *= shade * light;

    // Vanilla's own two water surface terms, on the same band the ground's corrections use.
    // Both are transcribed from chunkliquid.fsh's cold-liquid branch for the case a distant
    // LOD lake is in: still water, so its flow vector is zero and the flow counter passes
    // through; open water, so its depth-difference term is zero and drops out of the foam
    // exactly. Applied AFTER lighting and before fog, where vanilla applies them.
    // Every cached water fragment, NOT the blend band.
    //
    // These were band-weighted for cost, and that was a mistake with a visible edge: the two
    // terms faded out over the band and then stopped, so distant water carried a line across
    // it exactly where the band ended. Vanilla's water is shiny and grainy everywhere it is
    // drawn, out to its own horizon, so cached water carrying the same terms everywhere is
    // MORE faithful than fading them, not less - and a term that ends nowhere cannot draw an
    // edge anywhere. waterGlint and waterFoam are the strengths; zero turns each off.
    float waterAmount = band == 1 ? 1.0 : 0.0;
    if (waterAmount > 0.0 && (waterFoam > 0.0 || waterGlint > 0.0))
    {
        float upness = clamp(abs(normal.y), 0.0, 1.0);
        vec3 foamPos = worldPos.xyz + vhFoamOrigin;
        float a = foamPos.x + foamPos.y;
        float b = foamPos.z;
        float noise1 = gnoise(vec3(a * 15.0, -abs(b * 15.0), vhWaterFlow))
            + gnoise(vec3(a * 5.0, b * 5.0, vhWaterFlow));
        float noise2 = gnoise(vec3(a, b, vhWaterFlow));

        if (waterFoam > 0.0)
        {
            float foamAmount = waterAmount * waterFoam;
            float diff = upness * clamp(1.0 - noise1 / 2.0 + noise2 / 2.0, 0.0, 1.0);
            float grain = upness
                * (gnoise(vec3(a * 0.4, b * 0.4, vhWaterFlow)) / 2.0 + noise2 / 2.0 + 0.5) / 2.0;
            float bright = (terraColor.r + terraColor.g + terraColor.b) / 3.0;
            terraColor.rgb += vec3(max(0.0, bright * (diff * 0.3 + grain / 10.0)) * foamAmount);
            terraColor.a += max(0.0,
                diff / 16.0 + grain / (12.0 - 8.0 * min(1.0, vhWindSpeed))) * foamAmount;
        }

        // Specular sun glint. Vanilla also scales this by a shadow-map sample; distant open
        // water is unshadowed, and this shader has no shadow lookup, so that factor is 1.
        if (waterGlint > 0.0 && vhSunSpecular > 0.0)
        {
            vec3 noisepos = vec3(foamPos.x, foamPos.z, vhWaterWave / 8.0 + vhWindWave / 6.0);
            float dy = noise2 / 20.0 + clamp(gnoise(noisepos) / 10.0, 0.0, 0.6);
            vec3 waveNormal = normalize(vec3(dy, 1.0, -dy));
            vec3 eye = normalize(vec3(worldPos.x, worldPos.y - 2.0, worldPos.z));
            float p = dot(reflect(vhSunPosRel, waveNormal), eye);
            if (p > 0.0)
            {
                float sunb = clamp(vhSunPosRel.y * 10.0, 0.0, 1.0)
                    * clamp(1.5 - vhSunPosRel.y, 0.0, 1.0) * vhSunSpecular;
                float specular = pow(p, 50.0) * sunb;
                float weight = upness
                    * clamp(specular * clamp(1.5 * shadowIntensity, 0.0, 1.0), 0.0, 1.0)
                    * waterAmount * waterGlint;
                // The reflection colour is left unfogged on purpose: applyFog runs on the
                // finished pixel two lines down, so fogging it here would apply twice.
                terraColor.rgb = mix(terraColor.rgb, vhReflectColor + noise1 * 0.2, weight);
                terraColor.a = mix(terraColor.a, terraColor.a + specular / 2.0, weight);
            }
        }

        terraColor.a = clamp(terraColor.a, 0.0, 1.0);
    }

    terraColor = applyFog(terraColor, fogAmount);
    terraColor = applySpheresFog(terraColor, fogAmount, worldPos.xyz);

    // Dissolve both the far edge of the cache and the edges of the explored area
    // into the sky, so neither ends in a visible wall.
    float fade = max(smoothstep(0.75, 1.0, dist), edgeFade);

    // Only work out the sky where it is actually mixed in. mix(x, y, 0.0) is x, so
    // skipping this where fade is zero cannot change a pixel, and fade is zero across
    // the inner part of the band, which is most of the terrain on screen.
    //
    // It is worth skipping. getSkyColorAt costs several texture fetches, three
    // normalize calls, a pow, and a noise chain of eight sin calls, per fragment,
    // and all of it was being multiplied by zero.
    //
    // The branch is coherent: fade is a function of distance, so the fragments that
    // take it are a ring near the horizon rather than a speckle across the screen.
    if (fade > 0.0) {
        vec4 skyColor = vec4(1.0);
        vec4 skyGlow = vec4(1.0);
        vec3 worldPosInSky = normalize(worldPos.xyz) * 250.0;
        // Only the sky colour takes the corrected value. The glow clamp below is this
        // mod's own night dimming, not a transcription of vanilla, and its 0.05 constant
        // is calibrated against DayLightStrength - SkyDaylight never falls below 0.0625,
        // so feeding it here would leave a faint glow burning all night.
        float skyLight = lightSkyDayLight == 1 ? skyDayLight : dayLight;
        getSkyColorAt(worldPosInSky, sunPosition, 0.25, clamp(skyLight, 0.0, 1.0), horizonFog, skyColor, skyGlow);
        float murkiness = max(0.0, getSkyMurkiness() - 14.0 * fogDensityIn);
        skyColor.rgb = applyUnderwaterEffects(skyColor.rgb, murkiness);
        skyGlow.y *= clamp((dayLight - 0.05) * 2.0 - 50.0 * murkiness, 0.0, 1.0);

        outColor = mix(terraColor, skyColor, fade);
        outGlow = mix(vec4(0.0), skyGlow, fade);
    } else {
        outColor = terraColor;
        outGlow = vec4(0.0);
    }

#if SSAOLEVEL > 0
    outGPosition = vec4(0.0);
    outGNormal = vec4(0.0);
#endif
}
