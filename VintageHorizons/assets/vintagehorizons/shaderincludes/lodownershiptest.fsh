    // Which blend band this fragment is in: 0 opaque, 1 water, 2 thin cover. Needed before
    // the near cutoff because water has its own.
    int ownershipBand = int(vertexColor.a * 255.0 + 0.5) / TINT_SLOTS;
    if (ownershipBand == 1 && waterSurfaceVisible == 0) discard;

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

#if defined(VH_WATER_DEPTH) || defined(VH_WATER_OIT)
        // Chunk ownership is not proof of raster coverage. At the junction a moving
        // vanilla edge can leave a pixel empty while this cached water mesh covers it.
        // Supply both depth and the matching OIT surface for clear pixels in an owned
        // column touching the cache. Using this rule only in the depth pass produced the
        // moving blips it was meant to remove: murk saw repaired water while OIT still
        // discarded its surface.
        // Existing vanilla depths and the interior of its footprint remain authoritative.
        // No neighbour depth interpolation, geometry expansion or surface overlap.
        bool vanillaWaterClear;
#ifdef VH_WATER_DEPTH
        vanillaWaterClear = texelFetch(vanillaWaterDepth, ivec2(gl_FragCoord.xy), 0).r == 1.0;
#else
        // OIT is full resolution while the engine liquid target is quarter resolution.
        // This is the same normalized lookup underwatereffects.fsh uses for liquidDepth.
        vanillaWaterClear = texture(vanillaWaterDepth, gl_FragCoord.xy / frameSize.xy).r == 1.0;
#endif
        bool junctionCoverageMissing = vanillaWaterClear;
#ifdef VH_WATER_OIT
        // Liquid depth is quarter resolution. It can say either "covered" for a four-by-four
        // area where vanilla missed this full-resolution pixel, or "clear" where another
        // pixel in the same area did receive vanilla water. The latter disagreement produced
        // 960 double-drawn pixels in the owner's capture, exactly tracing the pale broken seam.
        // When the immutable full-resolution revealage copy is available it therefore REPLACES
        // the depth answer for surface coverage: admit cached water only where vanilla
        // contributed no transparent surface at all.
        if (waterJunctionSurface == 1)
        {
            float vanillaRevealage = texelFetch(
                vanillaOitRevealage, ivec2(gl_FragCoord.xy), 0).r;
            junctionCoverageMissing = vanillaRevealage >= 0.9999;
        }
#endif
        if (owned && ownershipBand == 1 && waterJunctionDepth == 1
            && junctionCoverageMissing)
        {
            for (int dz = -1; dz <= 1 && owned; dz++)
            {
                for (int dx = -1; dx <= 1 && owned; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    if (!vanillaOwnsCell(cellX + dx, cellY, cellZ + dz, ownershipBand))
                        owned = false;
                }
            }
        }
#endif

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
#ifdef VH_WATER_OIT
                OIT(outColor, vhWaterGlow.r);
#endif
                return;
            }
            discard;
        }
    }

