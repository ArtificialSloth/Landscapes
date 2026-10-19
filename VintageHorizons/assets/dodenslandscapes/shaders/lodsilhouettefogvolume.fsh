#version 330 core

// A single depth-aware integration through a smooth world-space field. Unlike crossing cards,
// this has no internal surfaces whose alpha can stack into lines at corners or height slices.
//
// It runs at half resolution (0.4.124): each pixel marches the ray of one texel of its 2x2
// block, alternately the farthest and the nearest, and records that ray's length so
// lodsilhouettefogupsample.fsh can rebuild full resolution without spreading fog across depth
// edges. Output is premultiplied: fog colour times opacity, and opacity.
//
// The medium has two parts (0.4.125). A strip only a few blocks deep lies on the seam itself,
// over the measured band the 32-block staircase strays across, and hides it from every angle;
// it hugs the surface each ray ends on rather than the coarse sampled height. Above and around
// it a natural bank (0.4.123's shape) is thin enough to see Farseer through: it thins
// exponentially from the seam, noise erodes its edges into strands, a faint haze carries the
// fade on, and sunlight shades it, never brighter than the horizon.
//
// When the seam moves, the field shown before the update keeps drawing as a residual and
// dissolves over the fade time instead of vanishing: its weight wears down, which thins,
// lowers and erodes its bank and fades its strip. Mirrored for the checks by
// LodSilhouetteFogVolume.
//
// Without Farseer (0.4.149) nothing lies beyond the edge, and the cached terrain's wall drops
// from the lip to the foot of its columns in open sky. A curtain hangs over that wall: mist
// pouring over the lip and down the whole face. Its tendrils scroll along that path - slowly out
// over the lip, then down - and take no part in the wind, which would carry them sideways
// (0.4.150). Beside empty sky the bank no longer stands out level past the edge: it curves down
// over it, so the fog rolls over the lip instead of ending in a shelf (0.4.151), and the dense
// strip ends at the band, where the curtain's rounded top takes over. Below the wall's foot the
// curtain breaks into tendrils that thin out and fade as they fall (0.4.153).

uniform sampler2D sceneDepth;
uniform sampler2D fogField;
// The previous field, on fogField's lattice. RGB as fogField; alpha is the weight each texel
// had when it began to fade, which fogResidualElapsed wears down (LodSilhouetteFogResidual).
uniform sampler2D fogResidual;
// Tiling value noise, 16 lattice cells per repeat, four independent channels
// (LodSilhouetteFogNoise). The engine's uniform collector does not know sampler3D, so the draw
// binds this unit with GL directly.
uniform sampler3D fogNoise;
uniform mat4 invProjection;
uniform mat4 invView;
uniform vec3 cameraInField;
uniform float fieldWorldOriginX;
uniform float fieldWorldOriginZ;
uniform float fieldWidth;
uniform float fieldHeight;
uniform float fieldTexelBlocks;
uniform float fieldMinY;
uniform float fieldMaxY;
uniform float farViewDistance;
uniform float globeStart;
uniform float globeEffect;
// Blocks one pixel covers per block of distance, so detail smaller than a pixel is filtered.
uniform float fogPixelScale;

uniform vec4 rgbaFogIn;
uniform vec3 sunPosition;
uniform vec3 sunColor;
uniform float dayLight;
// Density of the bank and its haze.
uniform float fogStrength;
// Density of the seam strip.
uniform float fogSeamStrength;
uniform float fogCoverage;
uniform float fogSpillBlocks;
uniform float fogHeightBlocks;
uniform float fogFloorDepthBlocks;
uniform float fogBillowSizeBlocks;
uniform float fogEdgeSoftness;
uniform float fogLayerDetail;
// Half-width of the band inside which the bank does not thin: the measured staircase plus a
// margin for slanted views through the bank.
uniform float fogCoreBlocks;
// Half-width of the strip: the measured staircase plus a small margin. A layer this shallow
// is crossed near where the ray lands, so it needs far less margin than the bank.
uniform float fogStripHalfBlocks;
// The strip's depth over the ground.
uniform float fogStripHeightBlocks;
// Distance the billows have already drifted, in blocks, integrated on the CPU.
uniform float fogDriftBlocks;
// Fade durations since the residual's weights were taken; 0 when fogResidualActive is 0.
uniform float fogResidualElapsed;
uniform int fogResidualActive;
// 1 while the fog follows the cached terrain's own edge, with empty sky beyond it: the curtain
// hangs down the wall there. Farseer's ground hides no wall, so with Farseer it is 0.
uniform int fogCurtainActive;
// Where the wall ends: the foot of the cached columns.
uniform float fogCurtainFloorY;
// Distance the curtain's tendrils have already flowed, in blocks, integrated on the CPU.
uniform float fogFallBlocks;

layout(location = 0) out vec4 outFog;
layout(location = 1) out float outDistance;

// The bank's optical depths are quoted along a level crossing through its ground layer, square
// to the seam: how see-through it is from the ground, which is how it is mostly seen. Its
// density per block does not depend on its height, so a taller bank is deeper from above.
const float BankCrossingOpticalDepth = 1.0;
const float HazeCrossingOpticalDepth = 0.15;
const float BankMeanDensity = 0.6;
const float ShoulderFraction = 0.4;
const float HazeFraction = 0.6;
const float FarseerReachScale = 1.6;
const float GroundLayerFraction = 0.3;
const float ScaleHeightFraction = 0.42;
// The strip's column over its band: 3.5 hides 97% of the seam straight down, and every
// slanted view crosses more of it.
const float StripOpticalDepth = 3.5;
// Its vertical profile's integral in units of its local top: full to 0.55, gone by 1.35.
const float StripColumnFraction = 0.95;
// Past its band the strip frays out over this many blocks, lobed by the billows. Its edge wanders
// by up to twice StripWanderBlocks, broad and billow-sized, within the band's margin.
const float StripFalloffBlocks = 12.0;
const float StripWanderBlocks = 3.0;
// The strip's band is the measured staircase plus this margin (LodSilhouetteFogVolume).
const float StripMarginBlocks = 6.0;
// Near the point a ray lands on, the strip rests on that surface, within this much of the
// sampled seam height; far from it, on the sampled height.
const float StripAnchorNearBlocks = 24.0;
const float StripAnchorFarBlocks = 64.0;
const float StripAnchorReachBlocks = 16.0;
const float NoisePeriod = 16.0;
const float NoiseTexelsPerCell = 4.0;
// Past this mip a 64-texel repeat is two texels or fewer: the lookup is the channel's mean.
const float NoiseFlatLod = 5.0;
const float SunProbeBlocks = 14.0;
// Steps through thin fog may grow to this fraction of the distance: far away the fog's detail
// is smaller than a pixel, and a ray skimming along the seam must still reach its far end
// within the step budget. Dense fog keeps short steps whatever the distance.
const float FarStepFraction = 0.02;
const int MaxFogSteps = 192;
// The curtain is a veil, not a wall (owner, 2026-10-18: less coverage looks more real and costs
// less): a sheet from a little behind the outermost stretch of wall out over the band's margin
// and fringe, dense in its falling streams and nearly clear between them, so the wall shows
// through the gaps. Its density is this times 0.35 + 1.3 x streak squared.
const float CurtainExtinction = 0.12;
// Mist gathers where the fall ends: within this many blocks of the wall's foot the veil
// thickens to this many times as dense, hiding the wall's lower edge and running on into the
// tendrils below it.
const float CurtainFootBlocks = 16.0;
const float CurtainFootBoost = 2.0;
// Beside the wall the dense strip ends this far below the lip: hanging down the face it drew a
// pale band with a hard lower edge through the veil.
const float CurtainStripDepthBlocks = 4.0;
// The sheet's inner side lies this far behind the outermost stretch of wall; deeper stretches
// stand behind the whole sheet.
const float CurtainSheetInsetBlocks = 6.0;
// Where only the curtain is, a step may cross this much optical depth: the veil is smooth
// along a ray.
const float CurtainStepDepth = 0.6;
// Past the band it frays out over this many blocks in tendrils, and its tongues reach out up to
// twice the wander, never in: only outward, so they cannot thin the band.
const float CurtainFalloffBlocks = 6.0;
const float CurtainWanderBlocks = 3.0;
// Where it pours over the lip it bulges out this far, settling back over this depth.
const float CurtainLipBlocks = 10.0;
const float CurtainLipDepthBlocks = 14.0;
// It starts this far above the edge's ground and fades out this far below the wall's foot.
const float CurtainLipRiseBlocks = 4.0;
// Below the wall's foot it breaks into tendrils over this many blocks: ever fewer survive as
// they fall, and the last are gone by the end.
const float CurtainBottomFadeBlocks = 48.0;
// Inside the cached terrain it ends this far past the band: there it is in the rock.
const float CurtainInnerFadeBlocks = 4.0;
// The tendrils are this many times longer along their path than they are wide. A streak much
// longer than this hides its own motion: sliding along its length, it barely changes.
const float CurtainStreakStretch = 3.0;
// Across the top the tendrils travel outward this many times slower than they fall: the path
// counts each block outward as this many blocks of it.
const float CurtainTopSlowdown = 3.0;
// Past the band, beside empty sky, the fog's floor curves down as a parabola of this radius:
// it has dropped half a radius one radius out, and falls ever more steeply beyond.
const float CurtainSpillRadiusBlocks = 12.0;
// The tendrils' path turns from outward to down over about this many blocks at the band's edge.
const float CurtainTurnBlocks = 4.0;
// Past the band, beside empty sky, the dense strip fades out over this many blocks.
const float CurtainStripFadeBlocks = 6.0;
// The curtain's top undulates by this much with its tendrils, so it is not a smooth dome.
const float CurtainTopBillowBlocks = 3.0;
// Near the dense curtain a step is at most this many half-resolution pixels deep, and never
// less than the minimum: a longer stride lands inside it at a jittered depth, which drew it as
// fine dashes. Further off a step may reach most of the way to it, but no further (0.4.154:
// short strides through the whole thin fringe cost more than Farseer).
const float CurtainStepPixels = 2.0;
const float CurtainMinStepBlocks = 2.5;
// The outward direction only moves the tendrils over the band and a little past it; beyond
// this many blocks past the band it would shift them by under a twentieth of a block, and its
// four field reads are skipped.
const float CurtainOutwardReachBlocks = 18.0;
// Beside empty sky, once a ray is half hidden each step may cross this much optical depth
// instead of 0.35: what lies behind adds little, and inside the dense curtain the short steps
// were most of a ray's samples.
const float CurtainHiddenStepDepth = 1.0;
// Below the foot each column of the curtain hangs on as a tendril of its own length, a quarter
// to the whole of the dissolve; the columns are this many blocks across, and their lengths
// drift as the fall goes on, this much of a cell per block fallen.
const float CurtainDripBlocks = 6.0;
const float CurtainDripDrift = 0.02;
// The tendrils' remap: tighter than the body's, so wisps and gaps are distinct enough for
// their flow to show on a curtain too dense to see through.
const float CurtainStreakLow = 0.3;
const float CurtainStreakHigh = 0.7;
// The gaps between wisps are shaded by this much optical depth toward the sun, as denser
// mist around them would shade them.
const float CurtainGapShade = 1.5;

// Every noise lookup at one point. They depend on the position only, so the current field and
// a dissolving one share them.
struct FogNoise
{
    vec4 broad;
    // The eroding pattern, remapped to spread over the whole range.
    float body;
    float thread;
    float detailWeight;
    float baseLod;
    // The curtain's flowing tendrils, remapped like the body; 0.5 where none was looked up.
    float streak;
    // How far below the wall's foot this column of the curtain hangs on, in [0, 1].
    float drip;
    // How far the curtain's tongues reach out, in [0, 1]: anchored to the world where the
    // tendrils were looked up, so its outline holds still; the drifting broad noise elsewhere.
    float tongue;
};

float vhFogHash(vec3 p)
{
    p = fract(p * 0.1031);
    p += dot(p, p.yzx + 33.33);
    return fract((p.x + p.y) * p.z);
}

bool vhFogSlab(float origin, float direction, float minimum, float maximum,
    inout float nearDistance, inout float farDistance)
{
    if (abs(direction) < 0.000001)
        return origin >= minimum && origin <= maximum;
    float first = (minimum - origin) / direction;
    float second = (maximum - origin) / direction;
    if (first > second)
    {
        float swap = first;
        first = second;
        second = swap;
    }
    nearDistance = max(nearDistance, first);
    farDistance = min(farDistance, second);
    return nearDistance <= farDistance;
}

// Mip level at which one noise texel covers a pixel, for a lookup scaled by cellsPerBlock.
float vhFogNoiseLod(float pixelBlocks, float cellsPerBlock)
{
    return log2(max(0.0001, pixelBlocks * cellsPerBlock * NoiseTexelsPerCell));
}

vec4 vhFogNoise(vec3 cells, float lod)
{
    return textureLod(fogNoise, cells * (1.0 / NoisePeriod), max(0.0, lod));
}

// Three octaves across three channels, so the octaves are independent patterns. An octave
// whose texels are all smaller than a pixel is its mean, 0.5, so its fetch is skipped.
float vhFogFbm(vec3 q, float lod)
{
    float second = lod + 1.02 < NoiseFlatLod
        ? vhFogNoise(q * 2.03 + vec3(11.7, 5.3, 17.1), lod + 1.02).g : 0.5;
    float third = lod + 2.03 < NoiseFlatLod
        ? vhFogNoise(q * 4.09 + vec3(3.1, 19.4, 7.8), lod + 2.03).b : 0.5;
    return 0.57 * vhFogNoise(q, lod).r + 0.29 * second + 0.14 * third;
}

// The coverage remap's mean over evenly spread noise. Where the billows are smaller than a
// pixel the filtered noise flattens toward its middle value, which would erase the thin fringe
// instead of averaging it; the mean keeps the fringe as the veil it would look like.
float vhFogExpectedDensity(float threshold)
{
    return threshold >= 0.0
        ? max(0.0, 0.5 * (1.0 - threshold))
        : (0.5 - threshold) / (1.0 - threshold);
}

float vhFogPhase(float cosine, float g)
{
    float g2 = g * g;
    return (1.0 - g2) / pow(1.0 + g2 - 2.0 * g * cosine, 1.5);
}

// How far the fog's floor has curved down at a signed distance: nothing over the band or with
// Farseer beyond, then a parabola out over empty sky.
float vhFogDroop(float signedDistance)
{
    if (fogCurtainActive == 0) return 0.0;
    float beyondBand = max(0.0, signedDistance - fogStripHalfBlocks);
    return beyondBand * beyondBand / (2.0 * CurtainSpillRadiusBlocks);
}

// Whether a point lies where only the curtain can be: beside empty sky, below the bank's floor
// however far it has curved down there, and below where the strip could rest.
bool vhFogBankless(float signedDistance, float height)
{
    return fogCurtainActive != 0
        && height + vhFogDroop(signedDistance) < -(fogFloorDepthBlocks + StripAnchorReachBlocks);
}

// The curtain's furthest reach out from the smoothed edge: its band, its lip, its longest
// tongue and its fringe.
float vhFogCurtainReach()
{
    return fogStripHalfBlocks + CurtainLipBlocks + 2.0 * CurtainWanderBlocks
        + 3.5 * CurtainFalloffBlocks;
}

// `cheap` is the sun probe's cut-down form: one octave and no threads. `bankless` skips the
// bank's and strip's noise where neither can be, below their floor beside empty sky; their
// values are then the noise's middle, which nothing there reads. `streak` looks up the
// curtain's tendrils, only where a curtain can be: `outward` is the unit direction out from the
// edge, `signedDistance` and `height` the point's distance out from it and above its ground.
FogNoise vhFogSampleNoise(vec3 fogWorld, float pixelBlocks, bool cheap, bool bankless,
    bool streak, vec2 outward, float signedDistance, float height)
{
    float billowBlocks = max(1.0, fogBillowSizeBlocks);
    float cellsPerBlock = 1.0 / billowBlocks;
    vec3 world = fogWorld + vec3(fogDriftBlocks, 0.0, fogDriftBlocks * -0.67);
    FogNoise n;
    n.broad = vec4(0.5);
    n.body = 0.5;
    n.baseLod = 0.0;
    n.thread = 0.5;
    n.detailWeight = 0.0;

    if (!bankless)
    {
        // Broad, vertically flattened lookup: the rim's wander, the swirl, the top's
        // undulation.
        vec3 broadCells = world * vec3(cellsPerBlock * 0.55, cellsPerBlock * 0.15,
            cellsPerBlock * 0.55);
        n.broad = vhFogNoise(broadCells, vhFogNoiseLod(pixelBlocks, cellsPerBlock * 0.55));

        // The swirl and a frame stretched along the wind and flattened vertically make the
        // eroded strands streaks rather than balls.
        vec3 wind = normalize(vec3(1.0, 0.0, -0.67));
        vec3 crosswind = vec3(-wind.z, 0.0, wind.x);
        vec3 swirled = world + vec3(n.broad.g - 0.5, 0.0, n.broad.b - 0.5) * (billowBlocks * 1.1);
        vec3 q = vec3(dot(swirled, wind) * 0.5, swirled.y * 2.2, dot(swirled, crosswind))
            * cellsPerBlock;
        n.baseLod = vhFogNoiseLod(pixelBlocks, cellsPerBlock);
        float body = cheap ? vhFogNoise(q, n.baseLod).r * 0.9 + 0.05 : vhFogFbm(q, n.baseLod);
        n.body = smoothstep(0.18, 0.82, body);

        if (!cheap)
        {
            // Finer thread-like noise, faded out where its threads would be narrower than a
            // pixel and sparkle.
            float detailLod = vhFogNoiseLod(pixelBlocks, cellsPerBlock * 3.3);
            n.detailWeight = 1.0 - smoothstep(-0.3, 1.4, detailLod);
            if (n.detailWeight > 0.0)
                n.thread = vhFogNoise(q * vec3(3.3, 2.7, 3.3) + vec3(5.2, 1.7, 9.4),
                    detailLod).a;
        }
    }

    n.streak = 0.5;
    n.drip = 1.0;
    n.tongue = n.broad.r;
    if (streak)
    {
        // The tendrils live in the edge's own frame: where along the edge and how far along the
        // spill's path - out across the top, slowly, then down. The pattern scrolls along that
        // path without ever starting again, so it neither pulses nor tears where the path turns
        // (0.4.150's two cross-faded copies did both). Anchored to the world, not the wind. Only over the
        // band does the path run outward; past it the tendrils only fall, so a ray crossing the
        // curtain's thickness meets one tendril, not a run of them averaged into a smear. A
        // soft minimum turns the one into the other without a crease.
        float streakBlocks = max(4.0, billowBlocks * 0.6);
        float streakLod = vhFogNoiseLod(pixelBlocks, 1.0 / streakBlocks);
        float acrossTop = clamp(signedDistance - fogStripHalfBlocks, -64.0, 64.0);
        acrossTop = -CurtainTurnBlocks * log(1.0 + exp(-acrossTop / CurtainTurnBlocks));
        // Over the band, where the tendrils travel outward, their place along the edge is held
        // still as they go; past it they only fall, and the world's own position serves.
        // Measuring along the edge everywhere fanned them out below every corner, where a whole
        // arc of points shares one nearest point (0.4.151).
        vec2 along = fogWorld.xz - outward * acrossTop;
        float path = CurtainTopSlowdown * acrossTop - height;
        vec3 pathCells = vec3(along.x, (path - fogFallBlocks) / CurtainStreakStretch, along.y)
            / streakBlocks;
        float fine = streakLod + 1.0 < NoiseFlatLod
            ? vhFogNoise(pathCells * 2.07 + vec3(13.3, 6.1, 2.9), streakLod + 1.0).b : 0.5;
        n.streak = smoothstep(CurtainStreakLow, CurtainStreakHigh,
            0.65 * vhFogNoise(pathCells + vec3(7.1, 3.3, 1.9), streakLod).g + 0.35 * fine);
        n.tongue = vhFogNoise(fogWorld * vec3(cellsPerBlock * 0.55, cellsPerBlock * 0.15,
            cellsPerBlock * 0.55) + vec3(5.9, 2.3, 8.1),
            vhFogNoiseLod(pixelBlocks, cellsPerBlock * 0.55)).r;
        // Only below the wall's foot does a column's length count.
        if (fogWorld.y < fogCurtainFloorY)
            n.drip = smoothstep(0.25, 0.75, vhFogNoise(vec3(fogWorld.x / CurtainDripBlocks,
                fogFallBlocks * CurtainDripDrift, fogWorld.z / CurtainDripBlocks)
                + vec3(2.7, 9.1, 4.4), vhFogNoiseLod(pixelBlocks, 1.0 / CurtainDripBlocks)).a);
    }
    return n;
}

// Extinction (optical depth per block) of one field's medium at one point: the bank with its
// haze, and the strip. `height` is the point's height above that field's seam ground and
// `stripHeight` its height above the surface the strip rests on. `weight` is the field's
// open-end fade, times what is left of its dissolve for the residual. The strip is normalized
// by its own column, so its vertical integral is its optical depth whatever its local top;
// `stripAbove` is about how much of that column lies above the point, for its shading.
// `wallDepth` is how far the wall drops below that field's seam ground, for the curtain.
void vhFogShape(vec4 field, float weight, float height, float stripHeight, float wallDepth,
    FogNoise n, out float bankExtinction, out float stripExtinction, out float stripAbove,
    out float top, out float curtainExtinction)
{
    float signedDistance = field.r;
    float lowerFade = max(2.0, min(10.0, fogFloorDepthBlocks * 0.35 + 2.0));
    // Beside empty sky the bank rests on a floor that curves down past the band, so it rolls
    // over the lip; the curtain's top follows the same curve. Its lip and foot keep to the real
    // heights. The dense strip does not bend - a thin opaque sheet curving down read as plastic
    // (0.4.151) - it ends past the band instead.
    float droop = vhFogDroop(signedDistance);
    float wallHeight = height;
    height += droop;

    // 1. Lateral: nothing thins inside the core; beyond it the bank fades exponentially,
    //    further over Farseer. The rim wanders before shaping (G225).
    float beyond = max(0.0, abs(signedDistance) - fogCoreBlocks);
    float wanderBlocks = clamp(fogEdgeSoftness, 0.0, 1.0) * 0.5 * fogSpillBlocks;
    float rim = max(0.0, beyond + (n.broad.r - 0.5) * 1.8 * wanderBlocks);
    float side = signedDistance < 0.0 ? fogSpillBlocks : fogSpillBlocks * FarseerReachScale;
    // Past one and a half shoulders nothing remains, which is what bounds the march.
    float cut = 1.0 - smoothstep(side, side * 1.5, rim);
    // Over empty sky nothing reaches past the curtain, so nothing hangs beyond its march bounds.
    if (fogCurtainActive != 0)
        cut *= 1.0 - smoothstep(0.6 * vhFogCurtainReach(), vhFogCurtainReach(), signedDistance);
    float envelope = exp(-rim / max(1.0, side * ShoulderFraction)) * weight * cut;

    // 2. Vertical: tallest over the seam and lower toward the rim, with an undulating top; a
    //    dense ground layer, then exponential thinning.
    top = fogHeightBlocks * (0.45 + 0.55 * envelope) * (0.7 + 0.6 * n.broad.a);
    float hs = max(0.0, height);
    float scaleHeight = max(1.0, ScaleHeightFraction * top);
    float heightDensity = exp(-max(0.0, hs - GroundLayerFraction * top) / scaleHeight);
    float under = smoothstep(-fogFloorDepthBlocks, -fogFloorDepthBlocks + lowerFade, height);
    float ceiling = 1.0 - smoothstep(0.35 * top, 1.1 * top, hs);

    // 3. Coverage erosion, not dimming: near the seam every noise value is fog; toward the rim
    //    only the peaks survive, as separate strands.
    float coverage = envelope * ceiling;
    float threshold = 1.0 - coverage * mix(1.0, 1.7, clamp(fogCoverage, 0.0, 1.0));
    float density = clamp((n.body - threshold) / max(0.001, 1.0 - threshold), 0.0, 1.0);
    density = mix(density, vhFogExpectedDensity(threshold), smoothstep(1.0, 3.0, n.baseLod));

    // 4. Feathery edges: the threads fray the thin parts.
    if (n.detailWeight > 0.0)
    {
        float ridge = 1.0 - abs(2.0 * n.thread - 1.0);
        float erosion = clamp(fogEdgeSoftness, 0.0, 1.0) * 0.8 * ridge * n.detailWeight
            * (1.0 - 0.7 * density);
        density = clamp((density - erosion) / max(0.001, 1.0 - erosion), 0.0, 1.0);
    }
    // Thin, translucent strands near the rim rather than opaque puffs.
    density *= pow(envelope, 0.75);

    // A level crossing covers the core and both exponential shoulders.
    float shoulders = fogSpillBlocks * (1.0 + FarseerReachScale);
    float bank = BankCrossingOpticalDepth
        / (BankMeanDensity * (2.0 * fogCoreBlocks + ShoulderFraction * shoulders))
        * density * heightDensity;
    // 5. Haze: real fog never ends at a line.
    float haze = HazeCrossingOpticalDepth / (2.0 * fogCoreBlocks + HazeFraction * shoulders)
        * exp(-rim / max(1.0, side * HazeFraction)) * weight * cut * heightDensity * ceiling;
    bankExtinction = (bank + haze) * max(0.0, fogStrength) * under;

    // 6. The strip: noise cannot open it over its band; past the band it frays out, its edge
    //    wandering broadly and in billow-sized lobes so from above it is not a painted ribbon.
    //    Its top rises and falls with the billows, and they vary its density a little without
    //    opening it.
    float stripRim = max(0.0, abs(signedDistance) - fogStripHalfBlocks
        + ((n.broad.r - 0.5) + (n.body - 0.5)) * 2.0 * StripWanderBlocks);
    float stripProfile = exp(-stripRim / StripFalloffBlocks)
        * mix(1.0, 0.15 + 0.85 * n.body, smoothstep(0.0, StripFalloffBlocks, stripRim))
        * (1.0 - smoothstep(2.0 * StripFalloffBlocks, 3.5 * StripFalloffBlocks, stripRim));
    if (fogCurtainActive != 0)
        stripProfile *= 1.0 - smoothstep(0.0, CurtainStripFadeBlocks,
            signedDistance - fogStripHalfBlocks);
    // Its fraying fingers lie lower than the band itself.
    float stripTop = max(0.5, fogStripHeightBlocks)
        * (0.8 + 0.4 * n.broad.a) * (0.8 + 0.4 * n.body)
        * (1.0 - 0.4 * smoothstep(0.0, 3.0 * StripFalloffBlocks, stripRim));
    float stripVertical = 1.0 - smoothstep(0.55 * stripTop, 1.35 * stripTop,
        max(0.0, stripHeight));
    float stripUnder = smoothstep(-fogFloorDepthBlocks, -fogFloorDepthBlocks + lowerFade,
        stripHeight);
    if (fogCurtainActive != 0)
    {
        float sheetInner = fogStripHalfBlocks - StripMarginBlocks - CurtainSheetInsetBlocks;
        stripUnder *= mix(1.0, smoothstep(-CurtainStripDepthBlocks, 0.0, stripHeight),
            smoothstep(sheetInner - CurtainInnerFadeBlocks, sheetInner, signedDistance));
    }
    // Squared, so a dissolving strip clears at an even pace instead of holding opaque until
    // its last moments.
    float stripDensity = StripOpticalDepth / (StripColumnFraction * stripTop)
        * stripProfile * stripUnder * (0.75 + 0.5 * n.body)
        * weight * weight * max(0.0, fogSeamStrength);
    stripExtinction = stripDensity * stripVertical;
    stripAbove = stripDensity * max(0.0, StripColumnFraction * stripTop - max(0.0, stripHeight));

    // 7. The curtain, with empty sky beyond the edge: whole over the band and its margin, so
    //    every stretch of the wall is behind it; bulging out where it pours over the lip;
    //    fraying outward in tendrils with tongues that only ever reach further out; ending in
    //    the rock on the inside and a little below the wall's foot.
    curtainExtinction = 0.0;
    if (fogCurtainActive != 0 && height < CurtainLipRiseBlocks + 0.5 * CurtainTopBillowBlocks)
    {
        // How far below the wall's foot, in tendril lengths. There the curtain thins to the
        // sheet off the wall's face - a full band's depth of tendrils overlapped into a blur -
        // and each column hangs on as a tendril of its own length, tapering to its end.
        float below = max(0.0, -wallDepth - wallHeight) / CurtainBottomFadeBlocks;
        float innerEdge = fogStripHalfBlocks - StripMarginBlocks
            - CurtainSheetInsetBlocks * (1.0 - smoothstep(0.0, 0.4, below));
        float lip = CurtainLipBlocks * exp(-max(0.0, -wallHeight) / CurtainLipDepthBlocks);
        float tongue = n.tongue * 2.0 * CurtainWanderBlocks;
        float curtainRim = max(0.0, signedDistance - fogStripHalfBlocks - lip - tongue);
        float lateral = exp(-curtainRim / CurtainFalloffBlocks)
            * mix(1.0, n.streak, smoothstep(0.0, CurtainFalloffBlocks, curtainRim))
            * (1.0 - smoothstep(2.0 * CurtainFalloffBlocks, 3.5 * CurtainFalloffBlocks,
                curtainRim))
            * smoothstep(innerEdge - CurtainInnerFadeBlocks, innerEdge, signedDistance);
        // Its top undulates with the tendrils; below the foot each column ends at its own
        // length, and what is left fades the further it hangs. Cutting the flowing pattern
        // instead opened holes that rose into the curtain (0.4.153 trial).
        float vertical = 1.0 - smoothstep(0.0, CurtainLipRiseBlocks,
            height + (n.streak - 0.5) * CurtainTopBillowBlocks);
        float hangs = 0.25 + 0.75 * n.drip;
        vertical *= (1.0 - smoothstep(hangs - 0.25, hangs, below))
            * (1.0 - smoothstep(0.4, 1.0, below));
        float foot = 1.0 - smoothstep(0.0, CurtainFootBlocks, abs(wallHeight + wallDepth));
        curtainExtinction = CurtainExtinction * lateral * vertical
            * (0.35 + 1.3 * n.streak * n.streak) * (1.0 + CurtainFootBoost * foot)
            * weight * weight * max(0.0, fogSeamStrength);
    }
}

// How far sideways a point lies outside the band beside the wall where the curtain can be: past
// its widest outward reach, or inside the sheet's inner side. Huge with no curtain.
float vhFogCurtainGap(float signedDistance, float curtainReach)
{
    return fogCurtainActive == 0 ? 1.0e6 : max(signedDistance - curtainReach,
        fogStripHalfBlocks - StripMarginBlocks - CurtainSheetInsetBlocks - CurtainInnerFadeBlocks
            - signedDistance);
}

// How far below the seam ground fog can reach at a point: down to the wall's foot beside the
// wall, else the bank's floor.
float vhFogFloorBelow(float curtainGap, float ground, float bankFloor)
{
    return curtainGap > 0.0 ? bankFloor
        : max(bankFloor, ground - fogCurtainFloorY + CurtainBottomFadeBlocks);
}

// Where the strip rests under a point: on the surface the ray lands on while the point is near
// it, within reach of the sampled seam height, else on the sampled height. The sampled height
// is a coarse seam average, several blocks off on a slope; a strip only a few blocks deep has
// to sit on the real ground there or it is buried on one side and floats on the other.
float vhFogStripGround(float sampledGround, float landing)
{
    return sampledGround + clamp(landing - sampledGround,
        -StripAnchorReachBlocks, StripAnchorReachBlocks);
}

void main()
{
    // One texel of this pixel's 2x2 block: the farthest on even checkerboard squares and the
    // nearest on odd ones, so the upsample finds a matching ray on both sides of a depth edge.
    ivec2 depthSize = textureSize(sceneDepth, 0);
    ivec2 lowTexel = ivec2(gl_FragCoord.xy);
    bool wantFar = ((lowTexel.x + lowTexel.y) & 1) == 0;
    ivec2 depthTexel = clamp(lowTexel * 2, ivec2(0), depthSize - ivec2(1));
    float opaqueDepth = texelFetch(sceneDepth, depthTexel, 0).r;
    for (int corner = 1; corner < 4; corner++)
    {
        ivec2 candidate = clamp(lowTexel * 2 + ivec2(corner & 1, corner >> 1),
            ivec2(0), depthSize - ivec2(1));
        float candidateDepth = texelFetch(sceneDepth, candidate, 0).r;
        if (wantFar ? candidateDepth > opaqueDepth : candidateDepth < opaqueDepth)
        {
            depthTexel = candidate;
            opaqueDepth = candidateDepth;
        }
    }
    vec2 screenUv = (vec2(depthTexel) + 0.5) / vec2(depthSize);
    // Pull an exact camera-relative endpoint from opaque depth. The tiny sky clamp avoids an
    // infinite-view projection producing w=0; the march is capped to Farseer's reach below.
    float reconstructDepth = min(opaqueDepth, 0.999999);
    vec4 viewPoint = invProjection
        * vec4(screenUv * 2.0 - 1.0, reconstructDepth * 2.0 - 1.0, 1.0);
    outFog = vec4(0.0);
    outDistance = 0.0;
    if (abs(viewPoint.w) < 0.0000001) return;
    viewPoint /= viewPoint.w;
    vec3 relativeEndpoint = (invView * viewPoint).xyz;
    outDistance = length(relativeEndpoint);
    float endpointDistance = min(outDistance, farViewDistance);
    if (endpointDistance <= 0.001) return;
    vec3 rayDirection = normalize(relativeEndpoint);
    // The surface this ray lands on, if it lands at all; the sky has nothing for the strip.
    bool lands = opaqueDepth < 1.0 && outDistance <= farViewDistance;
    float landingY = cameraInField.y + relativeEndpoint.y;

    float fieldBlocksX = fieldWidth * fieldTexelBlocks;
    float fieldBlocksZ = fieldHeight * fieldTexelBlocks;
    float marchStart = 0.0;
    float marchEnd = endpointDistance;
    if (!vhFogSlab(cameraInField.x, rayDirection.x, 0.0, fieldBlocksX,
            marchStart, marchEnd)
        || !vhFogSlab(cameraInField.y, rayDirection.y, fieldMinY, fieldMaxY,
            marchStart, marchEnd)
        || !vhFogSlab(cameraInField.z, rayDirection.z, 0.0, fieldBlocksZ,
            marchStart, marchEnd)
        || marchEnd <= max(0.0, marchStart)) return;

    float baseStep = mix(6.0, 2.5, clamp(fogLayerDetail, 0.0, 1.0));
    // Spatial dithering turns the residual fixed-step error into fine grain instead of coherent
    // view-facing bands. The field and density remain world anchored.
    float rayDistance = max(0.0, marchStart)
        + baseStep * vhFogHash(vec3(gl_FragCoord.xy, 0.731));
    float transmittance = 1.0;
    vec3 scattered = vec3(0.0);
    // The strip's widest sideways reach: its band, its wander and its cut-off fringe.
    float stripReach = fogStripHalfBlocks + 2.0 * StripWanderBlocks + 3.5 * StripFalloffBlocks;
    // The curtain's widest outward reach: its band, its lip, its longest tongue and its fringe.
    float curtainReach = fogCurtainActive != 0 ? vhFogCurtainReach() : 0.0;
    float conservativeReach = max(max(fogCoreBlocks + fogSpillBlocks * FarseerReachScale * 1.5
        + clamp(fogEdgeSoftness, 0.0, 1.0) * 0.5 * fogSpillBlocks, stripReach), curtainReach)
        + fieldTexelBlocks * 1.5;
    float stripDepth = max(0.5, fogStripHeightBlocks);
    // The highest bank top is 1.3 heights and its ceiling ends at 1.15 of that; the strip's
    // highest top is 1.44 of its depth, its profile ends at 1.35 of that, and it may rest up
    // to its anchor reach above the sampled ground.
    float bankCeiling = max(fogHeightBlocks * 1.5, StripAnchorReachBlocks + stripDepth * 1.95);
    float bankFloor = fogFloorDepthBlocks + StripAnchorReachBlocks;
    bool residualActive = fogResidualActive != 0;

    // Sunlight. A sun below the horizon still lights from just above it while the day fades.
    vec3 sunDirection = normalize(vec3(sunPosition.x, max(sunPosition.y, 0.08), sunPosition.z));
    float sunHeight = max(0.25, sunDirection.y);
    float daylight = smoothstep(0.02, 0.35, dayLight);
    // Fog glows a little toward the sun and dims a little away from it.
    float phase = mix(1.0, vhFogPhase(dot(rayDirection, sunDirection), 0.3), 0.3);
    vec3 sunTint = sunColor / max(0.001, max(sunColor.r, max(sunColor.g, sunColor.b)));
    sunTint = mix(vec3(1.0), sunTint, 0.6 * daylight);
    float previousExtinction = 0.0;
    // Faint steps reuse the last lit colour instead of paying for their own lighting.
    vec3 litColour = rgbaFogIn.rgb * 0.85;

    for (int stepIndex = 0; stepIndex < MaxFogSteps; stepIndex++)
    {
        if (rayDistance >= marchEnd || transmittance < 0.01) break;
        vec3 relative = rayDirection * rayDistance;
        vec3 fieldPosition = cameraInField + relative;
        vec2 fieldUv = fieldPosition.xz / vec2(fieldBlocksX, fieldBlocksZ);
        if (any(lessThan(fieldUv, vec2(0.0))) || any(greaterThan(fieldUv, vec2(1.0))))
            break;

        vec4 field = texture(fogField, fieldUv);
        float horizontal = length(relative.xz);
        float globe = clamp((horizontal - globeStart)
            / max(1.0, farViewDistance - globeStart), 0.0, 1.0);
        float globeDrop = globeEffect * globe * globe * farViewDistance;
        float ground = field.g - globeDrop;
        float heightAboveGround = fieldPosition.y - ground;
        float curtainGap = vhFogCurtainGap(field.r, curtainReach);
        float verticalOutside = max(0.0, max(
            -vhFogFloorBelow(curtainGap, ground, bankFloor) - heightAboveGround,
            heightAboveGround - bankCeiling));
        float horizontalOutside = max(0.0, field.b - conservativeReach);
        // How far outside the strip's widest reach this point lies, sideways.
        float stripBand = abs(field.r) - stripReach;
        if (residualActive)
        {
            // A dissolving field still holds fog wherever it has weight left.
            vec4 residual = texture(fogResidual, fieldUv);
            if (residual.a - fogResidualElapsed > 0.0)
            {
                float residualGround = residual.g - globeDrop;
                float residualHeight = fieldPosition.y - residualGround;
                float residualGap = vhFogCurtainGap(residual.r, curtainReach);
                horizontalOutside = min(horizontalOutside,
                    max(0.0, residual.b - conservativeReach));
                verticalOutside = min(verticalOutside, max(0.0, max(
                    -vhFogFloorBelow(residualGap, residualGround, bankFloor) - residualHeight,
                    residualHeight - bankCeiling)));
                stripBand = min(stripBand, abs(residual.r) - stripReach);
                curtainGap = min(curtainGap, residualGap);
            }
        }
        float landingNow = lands ? 1.0 - smoothstep(StripAnchorNearBlocks,
            StripAnchorFarBlocks, length(relative.xz - relativeEndpoint.xz)) : 0.0;
        // A ray that follows the seam while above or below its local terrain must still advance;
        // otherwise it can spend the whole fixed budget walking empty air in tiny steps.
        // Below the bank beside the curtain, a skip may not carry the ray sideways into it.
        float verticalSkip = verticalOutside * 0.35;
        if (curtainGap > 0.0) verticalSkip = min(verticalSkip, curtainGap);
        float outside = max(horizontalOutside, verticalSkip);
        float farStep = max(baseStep, rayDistance * FarStepFraction);
        // Inside the fog each segment stays optically thin, so a dense strip cannot become a
        // stepped surface, while thin fog far away is crossed in longer strides.
        float stepDepth = fogCurtainActive != 0 && transmittance < 0.5
            ? CurtainHiddenStepDepth
            : vhFogBankless(field.r, heightAboveGround) ? CurtainStepDepth : 0.35;
        float stepBlocks = outside > 0.0
            ? clamp(outside * 0.72, baseStep, 512.0)
            : previousExtinction > 0.0
                ? clamp(stepDepth / previousExtinction, 0.75, farStep) : farStep;
        // The strip is only a few blocks deep: a ray dropping onto it from the thin bank above
        // would step clean over it. Over its band, a step may descend at most to just inside
        // its highest top, and within it only a fraction of its depth.
        // Beside empty sky, below the strip's floor there is none to step over: the curtain is
        // crossed by its own extinction.
        float stripHeightNow = fieldPosition.y
            - mix(ground, vhFogStripGround(ground, landingY), landingNow);
        if (outside <= 0.0 && stripBand <= 0.0
            && (fogCurtainActive == 0 || stripHeightNow > -fogFloorDepthBlocks - 2.0))
        {
            float descent = max(0.02, -rayDirection.y);
            float stripRoom = max(0.0, stripHeightNow - stripDepth * 1.95);
            stepBlocks = min(stepBlocks, max(0.3, (stripRoom + 0.35 * stripDepth) / descent));
        }
        // Near the dense curtain a stride stays near a pixel's depth, so the ray finds it where
        // it starts rather than somewhere inside it; further off, a stride may cover most of the
        // way to it. The way is measured to the box the dense part can fill: its outer edge
        // sideways, its highest top above, its tendrils' end below. A step that long cannot
        // reach inside it, whichever way the ray runs.
        if (fogCurtainActive != 0 && outside <= 0.0 && curtainGap <= 0.0)
        {
            float coreGap = max(field.r - (fogStripHalfBlocks + CurtainLipBlocks
                    + 2.0 * CurtainWanderBlocks),
                max(heightAboveGround - (CurtainLipRiseBlocks + 0.5 * CurtainTopBillowBlocks),
                    -(ground - fogCurtainFloorY) - CurtainBottomFadeBlocks - heightAboveGround));
            stepBlocks = min(stepBlocks, max(max(CurtainMinStepBlocks,
                rayDistance * fogPixelScale * CurtainStepPixels), 0.8 * coreGap));
        }
        stepBlocks = min(stepBlocks, marchEnd - rayDistance);
        previousExtinction = 0.0;

        if (horizontalOutside <= 0.0 && verticalOutside <= 0.0)
        {
            // Sample the middle of the segment. Fog thickening along it, as toward the ground,
            // is then neither under- nor over-counted however long the step grows.
            float middle = rayDistance + stepBlocks * 0.5;
            relative = rayDirection * middle;
            fieldPosition = cameraInField + relative;
            vec2 middleUv = clamp(fieldPosition.xz / vec2(fieldBlocksX, fieldBlocksZ),
                vec2(0.0), vec2(1.0));
            field = texture(fogField, middleUv);
            globe = clamp((length(relative.xz) - globeStart)
                / max(1.0, farViewDistance - globeStart), 0.0, 1.0);
            globeDrop = globeEffect * globe * globe * farViewDistance;
            ground = field.g - globeDrop;
            heightAboveGround = fieldPosition.y - ground;
            // How close this point is to where the ray lands, for the strip's footing.
            float landingWeight = lands ? 1.0 - smoothstep(StripAnchorNearBlocks,
                StripAnchorFarBlocks, length(relative.xz - relativeEndpoint.xz)) : 0.0;
            float stripHeight = fieldPosition.y
                - mix(ground, vhFogStripGround(ground, landingY), landingWeight);
            // Noise is evaluated in the unwarped world so its scale and drift do not stretch as
            // the distant globe curve lowers geometry.
            vec3 fogWorldPosition = vec3(
                fieldWorldOriginX + fieldPosition.x,
                fieldPosition.y + globeDrop,
                fieldWorldOriginZ + fieldPosition.z);
            float pixelBlocks = max(0.001, middle * fogPixelScale);
            // The tendrils are looked up only where a curtain can be: below its top, which a
            // dissolving field's ground differs from by no more than the strip's anchor reach.
            // Nor anywhere sideways but beside the wall, where a curtain can be (0.4.154: looked
            // up over the whole bank near the ground, they were most of the curtain's cost).
            bool wantStreak = fogCurtainActive != 0
                && heightAboveGround < CurtainLipRiseBlocks + StripAnchorReachBlocks
                && (residualActive || vhFogCurtainGap(field.r, curtainReach) <= 0.0);
            // Out from the edge: up the field's signed distance. Central differences a texel
            // either side, so the direction turns smoothly instead of jumping at each texel of
            // the bilinear field, which would tear the tendrils along the texel lines.
            vec2 outward = vec2(0.0);
            if (wantStreak && field.r - fogStripHalfBlocks < CurtainOutwardReachBlocks)
            {
                vec2 fieldTexel = 1.0 / vec2(fieldWidth, fieldHeight);
                outward = vec2(
                    texture(fogField, middleUv + vec2(fieldTexel.x, 0.0)).r
                        - texture(fogField, middleUv - vec2(fieldTexel.x, 0.0)).r,
                    texture(fogField, middleUv + vec2(0.0, fieldTexel.y)).r
                        - texture(fogField, middleUv - vec2(0.0, fieldTexel.y)).r);
                float outwardLength = length(outward);
                outward = outwardLength > 0.00001 ? outward / outwardLength : vec2(0.0);
            }
            // Beside empty sky, far enough below the edge that neither the bank, curving down
            // past the band, nor the strip, resting up to its anchor reach off the ground, can
            // be there, only the curtain is: its samples skip their noise (0.4.155).
            bool bankless = vhFogBankless(field.r, heightAboveGround) && !residualActive;
            FogNoise pointNoise = vhFogSampleNoise(fogWorldPosition, pixelBlocks, false,
                bankless, wantStreak, outward, field.r, heightAboveGround);
            float bank;
            float strip;
            float stripAbove;
            float top;
            float curtain;
            vhFogShape(field, clamp(field.a, 0.0, 1.0), heightAboveGround, stripHeight,
                ground - fogCurtainFloorY, pointNoise, bank, strip, stripAbove, top, curtain);
            vec4 residual = vec4(0.0);
            float residualWeight = 0.0;
            if (residualActive)
            {
                residual = texture(fogResidual, middleUv);
                residualWeight = clamp(residual.a - fogResidualElapsed, 0.0, 1.0);
                if (residualWeight > 0.0)
                {
                    // Where the old and the new fog overlap, the thicker shows: an unchanged
                    // stretch neither doubles nor dips while its old copy dissolves.
                    float oldGround = residual.g - globeDrop;
                    float oldBank;
                    float oldStrip;
                    float oldAbove;
                    float oldTop;
                    float oldCurtain;
                    vhFogShape(residual, residualWeight, fieldPosition.y - oldGround,
                        fieldPosition.y - mix(oldGround, vhFogStripGround(oldGround, landingY),
                            landingWeight),
                        oldGround - fogCurtainFloorY, pointNoise,
                        oldBank, oldStrip, oldAbove, oldTop, oldCurtain);
                    if (oldBank > bank) top = oldTop;
                    bank = max(bank, oldBank);
                    strip = max(strip, oldStrip);
                    stripAbove = max(stripAbove, oldAbove);
                    curtain = max(curtain, oldCurtain);
                }
            }
            float extinction = bank + strip + curtain;
            previousExtinction = extinction;
            if (extinction > 0.0000001)
            {
                float segmentAlpha = 1.0 - exp(-extinction * stepBlocks);
                float contribution = transmittance * segmentAlpha;
                transmittance *= 1.0 - segmentAlpha;
                // A step too faint to change the pixel alone skips the lighting work.
                if (contribution >= 0.0005)
                {
                    // Fog between this point and the sun shades it: one cheap look toward the
                    // sun, plus this point's own share. Without the look the bank goes flat
                    // grey. The strip shades only through the few blocks of itself above, so
                    // its billowed top catches the light and its hollows do not. Below the lip,
                    // beside empty sky, the strip is no longer lying on ground under a bank: it
                    // is lit as the curtain is, or it draws a dark band under the lip.
                    float sunk = fogCurtainActive != 0
                        ? 1.0 - smoothstep(-4.0, 0.0, heightAboveGround) : 0.0;
                    float stripShade = stripAbove * 0.5 / sunHeight * (1.0 - sunk);
                    float towardSun = bank * max(1.0, ScaleHeightFraction * top) / sunHeight
                        + stripShade;
                    vec3 probePosition = fieldPosition + sunDirection * SunProbeBlocks;
                    vec2 probeUv = probePosition.xz / vec2(fieldBlocksX, fieldBlocksZ);
                    if (all(greaterThanEqual(probeUv, vec2(0.0)))
                        && all(lessThanEqual(probeUv, vec2(1.0))))
                    {
                        vec4 probeField = texture(fogField, probeUv);
                        float probeGround = probeField.g - globeDrop;
                        float probeHeight = probePosition.y - probeGround;
                        FogNoise probeNoise = vhFogSampleNoise(
                            fogWorldPosition + sunDirection * SunProbeBlocks, pixelBlocks, true,
                            vhFogBankless(probeField.r, probeHeight) && !residualActive,
                            false, vec2(0.0), 0.0, 0.0);
                        float probeBank;
                        float probeStrip;
                        float probeAbove;
                        float probeTop;
                        float probeCurtain;
                        vhFogShape(probeField, clamp(probeField.a, 0.0, 1.0), probeHeight,
                            probeHeight, probeGround - fogCurtainFloorY, probeNoise,
                            probeBank, probeStrip, probeAbove, probeTop, probeCurtain);
                        if (residualWeight > 0.0)
                        {
                            vec4 probeResidual = texture(fogResidual, probeUv);
                            float probeOldGround = probeResidual.g - globeDrop;
                            float probeOldHeight = probePosition.y - probeOldGround;
                            float oldBank;
                            float oldStrip;
                            float oldCurtain;
                            vhFogShape(probeResidual,
                                clamp(probeResidual.a - fogResidualElapsed, 0.0, 1.0),
                                probeOldHeight, probeOldHeight, probeOldGround - fogCurtainFloorY,
                                probeNoise, oldBank, oldStrip, probeAbove, probeTop, oldCurtain);
                            probeBank = max(probeBank, oldBank);
                            probeStrip = max(probeStrip, oldStrip);
                            probeCurtain = max(probeCurtain, oldCurtain);
                        }
                        // The curtain is dense: lit through its skin like the strip, so only a
                        // little of it, or of the strip, toward the sun counts for its mist or
                        // for the strip below the lip. A look that ends inside the cached
                        // terrain, under its ground, is in the cliff's shadow - also below the
                        // wall's foot, where the look runs up into the terrain's underside.
                        float curtainShare = max(curtain / extinction, sunk);
                        float rock = fogCurtainActive != 0
                            ? smoothstep(0.0, fogStripHalfBlocks, -probeField.r)
                                * smoothstep(0.0, 4.0, -probeHeight) : 0.0;
                        towardSun = (bank * 0.35 + probeBank
                                + probeStrip * mix(1.0, 0.15, curtainShare) + 0.15 * probeCurtain)
                            * SunProbeBlocks * 1.6 + stripShade + 4.0 * rock
                            + CurtainGapShade * (curtain / extinction) * (1.0 - pointNoise.streak);
                    }
                    // Beer's law plus a softer lobe standing in for light scattered many times.
                    float direct = (exp(-towardSun) + 0.6 * exp(-towardSun * 0.25)) / 1.6;
                    // Low fog sees less of the sky. The strip lies under only a thin bank, so it
                    // counts as standing about halfway up it: its billows' crests higher, the
                    // hollows between them lower.
                    float bankLift = clamp(max(0.0, heightAboveGround) / max(1.0, top), 0.0, 1.0);
                    float stripRise = (0.8 + 0.4 * pointNoise.broad.a)
                        * (0.8 + 0.4 * pointNoise.body);
                    // The curtain hangs in open sky. Its tendrils catch more of it than the
                    // gaps between them, so even where it is whole the fall shows.
                    float curtainLift = mix(0.05, 1.0, pointNoise.streak);
                    float stripLift = mix(mix(0.25, 0.75, clamp((stripRise - 0.64) / 0.8, 0.0, 1.0)),
                        curtainLift, sunk);
                    float lift = (bank * bankLift + strip * stripLift + curtain * curtainLift)
                        / extinction;
                    float sky = mix(0.55, 1.0, lift);
                    float sunlit = 0.62 * direct * phase + 0.38 * sky;
                    float light = clamp(mix(sky, sunlit, daylight), 0.0, 1.2);
                    // Never brighter than the horizon fog; shadows turn cool grey-blue.
                    vec3 tint = mix(vec3(0.90, 0.94, 1.04), sunTint, clamp(light, 0.0, 1.0));
                    litColour = rgbaFogIn.rgb * mix(0.64, 0.93, light) * tint;
                }
                scattered += litColour * contribution;
            }
        }
        rayDistance += max(0.25, stepBlocks);
    }

    // Premultiplied. The cap keeps a trace of what lies behind even the densest fog.
    float opacity = 1.0 - transmittance;
    float alpha = min(opacity, 0.99);
    if (alpha < 0.002) return;
    outFog = vec4(scattered * (alpha / max(opacity, 0.0001)), alpha);
}
