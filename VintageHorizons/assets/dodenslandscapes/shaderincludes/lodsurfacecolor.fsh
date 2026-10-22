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
    // ground. See .dltoplight.
    if (flatTopLight == 1) shade = max(shade, clamp(normal.y, 0.0, 1.0) * 0.95);

    // Decode the tint slot, then snow line on up-facing terrain.
    // Only the blend band is needed here; the tint itself arrives interpolated.
    int band = int(vertexColor.a * 255.0 + 0.5) / TINT_SLOTS;  // 0 opaque, 1 water, 2 thin, 3 observed snow
    bool translucent = band == 1 || band == 2;

    // WATER IS NOT SHADED BY ITS NORMAL, because vanilla's is not - chunkliquid.fsh has no
    // normal term at all, which the comment above already says. It made no difference while
    // cached water was a flat plane and every normal was the same, and it made a very visible
    // one the moment the surface started carrying vanilla's wave: a tilted quad picked up a
    // different shade from a flat one, and the boundary between them drew a bright crease.
    // Measured off an owner screenshot at a chunk edge, a 20-pixel band went from luma 29 to
    // 49 and back - a 70% step across three rows, where the geometry either side is the same
    // water at the same depth.
    if (band == 1) shade = 1.0;

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
    float waterFresnelValue = 1.0;
    if (band == 1)
    {
        vec3 eyeFresnel = normalize(vec3(worldPos.x, worldPos.y - 3.5, worldPos.z));
        // max() rather than vanilla's bare add: a dot product one ulp below -1 would make
        // the cube of a negative number, which is undefined for pow and free here.
        float rise = max(0.0, 1.0 + eyeFresnel.y);
        waterFresnelValue = max(0.2, 0.01 + 4.5 * rise * rise * rise);
        if (waterFresnel == 1)
        {
            outAlpha = clamp(
                0.8 * waterFresnelValue, 0.0, max(0.0, waterMaxAlpha));
            if (!waterTop) outAlpha *= 0.3333333;
        }
    }

#ifdef VH_WATER_SURFACE
    // Vanilla water is the terrain-atlas tile as well as the view-angle opacity above. The
    // cached surface carried only that tile's mean RGB and no texture alpha, so even identical
    // seabed and murk became a different final colour as soon as the two water surfaces were
    // composited. Sample the live standard-water tile once per world block, globally: ending
    // this term at a quality band would merely move the seam to the band's far edge.
    int waterTintSlot = int(vertexColor.a * 255.0 + 0.5) - TINT_SLOTS;
    if (band == 1 && waterTop && waterTextureEnabled == 1
        && waterTextureSlot > 0 && waterTintSlot == waterTextureTintSlot)
    {
        vec4 waterRect = texelFetch(blockTable, ivec2(waterTextureSlot, 0), 0);
        if (waterRect.z > waterRect.x)
        {
            vec2 rectSize = waterRect.zw - waterRect.xy;
            vec2 tile = terrainPos.xz;
            vec2 localUv = fract(tile);

            // Keep both samples one atlas texel inside the tile, matching vanilla's clamp.
            // Explicit gradients come from the unwrapped world coordinate so fract() cannot
            // choose a coarse mip at every block edge.
            vec2 atlasPixel = 1.0 / max(blockAtlasSize, vec2(1.0));
            vec2 inset = min(atlasPixel / max(rectSize, atlasPixel), vec2(0.49));
            localUv = clamp(localUv, inset, vec2(1.0) - inset);
            vec2 dx = dFdx(tile) * rectSize;
            vec2 dy = dFdy(tile) * rectSize;
            vec4 forwardTexel = textureGrad(
                blockAtlas, waterRect.xy + localUv * rectSize, dx, dy);
            vec4 reverseTexel = textureGrad(
                blockAtlas, waterRect.xy + (vec2(1.0) - localUv) * rectSize, -dx, -dy);

            // chunkliquid.vsh moves between the tile and its inverse with this exact phase.
            float phase = mod(vhWaterStill + length(terrainPos.xz) * 0.3333333, 2.0);
            float stillBlend = smoothstep(0.0, 1.0, abs(phase - 1.0));
            vec4 waterTexel = mix(reverseTexel, forwardTexel, stillBlend);

            // The palette colour is already the texture's mean with climate tint applied, so
            // RGB contributes variation around that mean. Vanilla's getColorMapped multiplies
            // the sampled texture's alpha by climateWaterTint alpha as well; omitting that one
            // channel made cached water about 1.5x as opaque at the measured handoff. The
            // climate-mapped alpha then multiplies the sum of vertex opacity and the foam-mode
            // addition, exactly where chunkliquid.fsh applies it.
            vec3 waterMean = texelFetch(blockTable, ivec2(waterTextureSlot, 2), 0).rgb;
            albedo *= waterTexel.rgb / max(waterMean, vec3(0.02));
            float mappedWaterAlpha = waterTexel.a * tintAlpha;
            float textureAlphaAddition = mappedWaterAlpha;
#if FOAMEFFECT > 0
            textureAlphaAddition = max(0.0, mappedWaterAlpha - 0.4);
#endif
            outAlpha = clamp(
                mappedWaterAlpha * (outAlpha + textureAlphaAddition), 0.0, 1.0);
        }
    }
#endif
