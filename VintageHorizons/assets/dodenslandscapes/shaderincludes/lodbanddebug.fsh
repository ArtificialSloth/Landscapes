    float bandWeight = 0.0;
    if (bandEnabled == 1)
    {
        // Snapped first: a wall standing on an 8-block line otherwise flickers between the two
        // steps either side of it (see snapToPlane).
        int fieldX = maskSectionOriginX * 4 + int(floor(snapToPlane(sectionLocal.x, 8.0) / 8.0));
        int fieldZ = maskSectionOriginZ * 4 + int(floor(snapToPlane(sectionLocal.z, 8.0) / 8.0));
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
#ifdef VH_WATER_OIT
        OIT(outColor, vhWaterGlow.r);
#endif
        return;
    }

