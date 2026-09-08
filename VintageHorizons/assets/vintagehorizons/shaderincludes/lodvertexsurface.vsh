    yLevel = vertexPositionIn.y;
    vertexColor = vhSurfaceColor;

    // The stable world position, set before the tint because the tint reads it. Geometry stays
    // camera-relative for precision, but appearance must not move with the camera: a fixed
    // terrain point has to sample the same variation from any distance. This is also exactly
    // what the engine hands its own colour maps - chunkopaque.vsh passes `truePos + playerpos`,
    // an absolute position, for the same reason.
    terrainPos = vec3(
        noiseOrigin.x + vertexPositionIn.x,
        vertexPositionIn.y,
        noiseOrigin.y + vertexPositionIn.z);

    int slotRaw = int(vhSurfaceColor.a * 255.0 + 0.5);
    int slot = clamp(slotRaw - (slotRaw / TINT_SLOTS) * TINT_SLOTS, 0, TINT_SLOTS - 1);
    // The flat fallback: the engine's own blended answer for this slot, already diluted by its
    // untinted share. This is what draws where the shader cannot finish the tint itself - no
    // paged climate map, the tropical season weight this mod does not reproduce, or no climate
    // field at all - and it is what every build before the field drew everywhere.
    tint = tintFallback[slot].rgb;

    // The climate field, where this slot can be finished from it. A page of 255 means it
    // cannot be, and the fallback above stands.
    int tintPage = 255;
    // Coarse sections take the flat fallback and skip the whole lookup - the four field
    // samples, the four map samples, the season row and the weight - rather than computing a
    // per-position climate for a voxel that already averages a neighbourhood.
    if (climateFieldEnabled == 1
        && (climateFadeBlocks <= 0.0 || columnBlocks <= climateFadeBlocks))
    {
        tintPage = int(tintPages[slot].r * 255.0 + 0.5);
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
        vec3 season = seasonRow.rgb;

        // The row the game would pick here, where this slot has a paged seasonal map. Faded
        // toward the averaged colour by the voxel's own size: at one block per voxel the noise
        // means what it means in vanilla, and by eight it is finer than anything a voxel can
        // represent, which is G47's reasoning stated in the units that actually govern it.
        vec4 pageRow = tintPages[slot];
        int seasonPage = int(pageRow.g * 255.0 + 0.5);

        // `amount` is decided BEFORE the branch so that a vertex which would blend none of the
        // row can skip computing it. It was inside, so from eight blocks per voxel upward the
        // noise was evaluated in full and then multiplied by zero - and columnBlocks is a
        // uniform outside the record path, which makes that whole draw call wasted work rather
        // than a scattering of wasted lanes.
        float coarse = clamp((columnBlocks - 1.0)
            / max(leafSeasonFadeBlocks - 1.0, 1.0), 0.0, 1.0);
        float amount = leafSeasonVariation * (1.0 - coarse);
        if (seasonPage < 255 && amount > 0.0)
        {
            bool leaves = pageRow.b > 0.5 / 255.0;
            season = mix(season, seasonAt(seasonPage, terrainPos, leaves), amount);
        }

        vec3 blended = mix(climateColor(tintPage, adjusted, rainfall),
                           season, seasonWeight * seasonRow.a);

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

    // 0 at the start of the LOD band (inside vanilla terrain), 1 at the far edge
    float distStart = viewDistance * 0.785;
    float radial = length(worldPos.xz);

    // Water takes vanilla's wave, faded out past the handoff ring. Applied to worldPos.y
    // alone and AFTER the radial distance is taken, so the fade cannot move with the wave
    // and neither can ownership addressing, which reads sectionLocal rather than this.
    if (slotRaw / TINT_SLOTS == 1 && vhWaveReach > 0.0)
    {
        float waveFade = clamp(
            1.0 - max(0.0, radial - viewDistance) / vhWaveReach, 0.0, 1.0);
        if (waveFade > 0.0)
        {
            // chunkliquid.vsh's own divisor: 5 for ordinary water, scaled by oceanity, and
            // floored at a fifth so the open sea does not divide by nothing.
            float div = 5.0 * max(0.2, 1.0 - clamp(vhOceanity, 0.0, 1.0));
            vec4 warped = applyLiquidWarping(true, worldPos, div);
            worldPos.y += (warped.y - worldPos.y) * waveFade;
        }
    }
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
