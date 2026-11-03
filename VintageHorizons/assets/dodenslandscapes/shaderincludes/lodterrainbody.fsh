#include lodfragmentinputs.fsh
#include lodownership.fsh
#ifndef VH_WATER_DEPTH
#define VH_BLOCK_LIGHT 1
in vec2 vhLightTexel;
flat in vec2 vhLightTile;
#endif
void main()
{
#include lodownershiptest.fsh
    // How far this fragment is from vanilla's own ground, as a 0..1 ramp: 1 at the seam,
    // 0 at the far edge of the band. Derived from the same integer section origin plus
    // section-local offset the ownership test above uses, for the same reason - a summed
    // world coordinate rounds a fragment near a cell edge onto its neighbour at large world
    // coordinates. Cells outside the field's window are ordinary cached terrain.
#ifdef VH_WATER_DEPTH
    // The same water mesh, warping and ownership as the colour pass. Thin plants
    // share the CPU buffer but must never become water in the liquid-depth image.
    if (ownershipBand != 1) discard;
    outColor = vec4(1.0);
#else
#include lodbanddebug.fsh
#include lodsurfacecolor.fsh
    // A reed's crossed plane wears the sheet the game's own far model wears, at every distance
    // the planes reach rather than only in the band. The game's reeds take no shading by facing.
    // Leaves (5, or 6 for a flat top) tile their sheet. With the vertex stage's shade under other
    // leaves, which averages 0.84 over real forest, these come out about 11% under the plain 0.78
    // and 0.95 they had: those read 5-13% over the game's own trees, with bright ground between.
    if (vhFaceAxis >= 4)
    {
        shade = vhFaceAxis >= 5 ? 0.82 : 1.0;
        vec2 vhSheetUv = vhFaceAxis >= 5 ? fract(vhPlaneUv) : vhPlaneUv;
        // Nearly edge-on a flat top's cut-outs blur into a solid bar across the tree. How much of
        // the sheet faces the camera is the ratio of its two footprints on screen, and the top
        // dissolves between 30 and 14 degrees, in a pattern the anti-aliasing smooths over.
        if (vhFaceAxis == 6)
        {
            vec2 vhAcross = dFdx(vhPlaneUv);
            vec2 vhDown = dFdy(vhPlaneUv);
            float vhFacing = abs(vhAcross.x * vhDown.y - vhAcross.y * vhDown.x)
                / max(max(dot(vhAcross, vhAcross), dot(vhDown, vhDown)), 1e-9);
            float vhDither = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
            if (vhFacing < mix(0.25, 0.5, vhDither)) discard;
        }
        // A standing leaf tile is a rectangle, and with Dense Leaves its straight edges showed as
        // square canopies (owner, 2026-11-03). Its corners are rounded off, unevenly, so each tile
        // reads as a puff and stacked tiles notch between them, as the game's overlapping leaves
        // do. Only the corners: the ragged cut fades to nothing along an edge, or every tile
        // boundary up a stack would open a seam. Flat tops stay whole, or a canopy would be dots.
        if (vhFaceAxis == 5)
        {
            const float vhCornerRadius = 0.6;
            vec2 vhCorner = max(abs(vhSheetUv * 2.0 - 1.0) - (1.0 - vhCornerRadius), 0.0) / vhCornerRadius;
            float vhRagged = fract(sin(dot(floor(vhSheetUv * 16.0), vec2(12.9898, 78.233))) * 43758.5453);
            if (dot(vhCorner, vhCorner) > 1.0 - 0.6 * vhRagged * min(vhCorner.x, vhCorner.y)) discard;
        }
        vec4 vhSheet = vhBlockSlot >= 0
            ? texelFetch(blockTable, ivec2(vhBlockSlot, 0), 0)
            : vec4(0.0);
        if (vhSheet.z > vhSheet.x)
        {
            vec2 vhSheetSize = vhSheet.zw - vhSheet.xy;
            vec2 vhSheetFootprint = max(abs(dFdx(vhPlaneUv)), abs(dFdy(vhPlaneUv)))
                * vhSheetSize * blockAtlasSize;
            float vhSheetMip = log2(max(max(vhSheetFootprint.x, vhSheetFootprint.y), 1.0));
            // Past three levels a tile starts to borrow its neighbours in the shared atlas.
            float vhSheetLod = min(max(vhSheetMip - blockTextureDetail, 0.0), 3.0);
            vec4 vhFoliage = textureLod(blockAtlas, vhSheet.xy + vhSheetUv * vhSheetSize, vhSheetLod);
            vec4 vhMeanRow = texelFetch(blockTable, ivec2(vhBlockSlot, 2), 0);
            vec4 vhCob = vec4(0.0);
            if (vhMeanRow.a > 0.5)
            {
                vec4 vhCobs = texelFetch(blockTable, ivec2(vhBlockSlot, 3), 0);
                vhCob = textureLod(blockAtlas,
                    vhCobs.xy + vhSheetUv * (vhCobs.zw - vhCobs.xy), vhSheetLod);
            }

            // Mips average thin stems with the clear pixels around them, so a fixed test would
            // lose the plant with distance. The test falls with them to keep it standing.
            float vhCoverage = max(vhFoliage.a, vhCob.a);
            if (vhCoverage < mix(0.5, 0.12, vhSheetLod / 3.0)) discard;

            // Clear texels store a colour the game never shows, and mips stir it into their
            // neighbours, so the pattern bows out where the mean is the better answer (G302).
            // A crop's clear texels hold its own mean, so it keeps its picture (row 1's alpha).
            float vhSheetHold = texelFetch(blockTable, ivec2(vhBlockSlot, 1), 0).a > 1.5
                ? 1.0 : clamp(2.0 - vhSheetLod, 0.0, 1.0);
            float vhSheetDetail = clamp(blockTextureMipLimit - vhSheetMip, 0.0, 1.0) * vhSheetHold;
            vec3 vhFoliageTexel = mix(vhMeanRow.rgb, vhFoliage.rgb, vhFoliage.a);
            albedo *= mix(vec3(1.0), vhFoliageTexel / max(vhMeanRow.rgb, vec3(0.02)), vhSheetDetail);
            albedo = mix(albedo, vhCob.rgb, vhCob.a * vhSheetDetail);
        }
    }

    // Real vanilla texels, where the band asks for them.
    //
    // The table rows are read HERE rather than in the vertex stage. They are wanted only by a
    // fragment that is inside the band, on an opaque quad that carries a slot, with textures
    // switched on - and the band is a ring of the screen, against millions of vertices. Read
    // per vertex they were three dependent fetches and eleven floats of flat varying paid by
    // every vertex in the view, textures on or off. See lodterrainbody.vsh's vhBlockSlot.
    if (!translucent && blockTextureEnabled == 1 && bandWeight > 0.0 && vhBlockSlot >= 0
        && vhFaceAxis < 4)
    {
        // Row 0 is the atlas rectangle. An empty one means the slot describes no texture,
        // which is the same "nothing to draw here" the vertex stage used to answer with a
        // zeroed rectangle. Row 1 is the averaged colour and was consumed as this quad's
        // albedo already.
        vec4 vhBlockRect = texelFetch(blockTable, ivec2(vhBlockSlot, 0), 0);
        int vhMeanRowIndex = 2;
        // Which face this is: the vertex stage's exact answer for packed quads, the derivative
        // normal's guess otherwise. A wrong guess here puts the side texture on a top, laid
        // along the block, which is what NVIDIA showed on grass (2026-10-21).
        bool vhWall = vhFaceAxis != 0 ? vhFaceAxis != 1 : abs(normal.y) <= 0.5;
        bool vhAlongX = vhFaceAxis != 0 ? vhFaceAxis == 2 : abs(normal.x) > 0.5;
        // Walls read their own rectangle and mean: rows 4 and 5 for faces along X, rows 6 and
        // 7 for faces along Z. The engine draws a cube's sides from the textures named for
        // them - bark on a standing log, whose top and bottom are the cut end - and one
        // texture for every face put rings up the side of every trunk. A block that names
        // nothing different for its sides has the top's rows copied there.
        if (vhWall)
        {
            int vhWallRow = vhAlongX ? 4 : 6;
            vhBlockRect = texelFetch(blockTable, ivec2(vhBlockSlot, vhWallRow), 0);
            vhMeanRowIndex = vhWallRow + 1;
        }
        if (vhBlockRect.z > vhBlockRect.x)
        {
            // Row 2, or the wall's mean row, is the sampled texture's own mean, with alpha
            // flagging whether a grass-style overlay exists; row 3 is that overlay's
            // rectangle. The fragment stage divides by the mean so the texture becomes
            // variation AROUND the block's colour rather than the colour itself - grass-covered
            // ground has a brown soil texture under a green overlay, so taking the texture at
            // face value drew a brown band across the world.
            vec4 vhMeanRow = texelFetch(blockTable, ivec2(vhBlockSlot, vhMeanRowIndex), 0);
            vec3 vhBlockMean = vhMeanRow.rgb;
            vec4 vhBlockOverlay = vhMeanRow.a > 0.5
                ? texelFetch(blockTable, ivec2(vhBlockSlot, 3), 0)
                : vec4(0.0);

            // Tile the block's own texture once per world block, on whichever plane this face
            // lies in. terrainPos is the stable world position, so the pattern is anchored to
            // the ground rather than to the camera.
            vec2 tile = !vhWall
                ? terrainPos.xz
                : (vhAlongX ? vec2(terrainPos.z, -terrainPos.y)
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
            // Each axis in its own pixels: U by the atlas width, V by its height.
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
                // A see-through texel shows nothing in the game, but it still stores a colour,
                // and dividing that by the mean is not variation - it is whatever the file kept.
                // Cattails' block-colour texture is 97% clear pixels stored as pure white over a
                // dark-brown cob mean, so the cube blew out to a glowing lime, day and night
                // (owner screenshots, 2026-10-22). Read clear texels as the mean itself: no
                // pattern where the game draws nothing. Leaves and crops store about their mean
                // there already, and opaque ground is untouched.
                vec4 baseTexel = textureLod(blockAtlas, blockUv, sampleLod);
                vec3 texel = mix(vhBlockMean, baseTexel.rgb, baseTexel.a);

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
    if (!translucent && band != 3 && frostAtVertex > 0.0) {
        // Vanilla frosts the colour-map tint, then lets the block texture continue to
        // provide its detail. Applying the ratio preserves that same separation here:
        // seasonal leaf/grass colour remains live underneath a white winter overlay.
        vec3 frostedTint = mix(tint, vec3(1.0), clamp(frostAtVertex, 0.0, 1.0));
        albedo *= frostedTint / max(tint, vec3(0.02));
    }
    if (!translucent) {
        float upness = clamp(normal.y, 0.0, 1.0);
        // Band 3 is vanilla-observed coverage carried by this existing top face. Unknown
        // terrain keeps the climate/season snow-line fallback; no extra snow geometry is
        // created in either case.
        float snowMix = (band == 3
            ? 1.0
            : smoothstep(snowLineAtVertex, snowLineAtVertex + 24.0, yLevel)) * upness;
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
        //
        // Addressed like ownership, from the integer section origin plus the section-local
        // position, with the face's own plane snapped (see snapToPlane): the same block as
        // floor(terrainPos) everywhere, except that a top or wall can no longer flicker
        // between the block it belongs to and its neighbour.
        ivec3 speckleBlock = ivec3(
            maskSectionOriginX * 32 + int(floor(snapToPlane(sectionLocal.x, 1.0))),
            int(floor(snapToPlane(sectionLocal.y, 1.0))),
            maskSectionOriginZ * 32 + int(floor(snapToPlane(sectionLocal.z, 1.0))));
        vec3 speckleCell = vec3(speckleBlock & ivec3(255));
        float speckle = fract(
            sin(dot(speckleCell, vec3(127.1, 311.7, 74.7))) * 43758.5453);
        albedo *= 1.0 + bandSpeckle * (speckle - 0.5) * 2.0 * bandAmount;

        // Rec. 709 luminance, so the split into "how bright" and "how far from grey" matches
        // what the eye weights rather than a flat channel average.
        float bandLuma = dot(albedo, vec3(0.2126, 0.7152, 0.0722));
        vec3 saturated = vec3(bandLuma) + (albedo - vec3(bandLuma)) * bandSaturate;
        albedo = mix(albedo, saturated, bandAmount) * mix(1.0, bandDarken, bandAmount);
    }

#include lodsurfacefinish.fsh

#endif
}
