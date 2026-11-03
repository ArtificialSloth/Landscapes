using VintageHorizons.Silhouette;

namespace VintageHorizons.Checks;

public static class FarDistanceChecks
{
    public static void Run(Check c)
    {
        CachedBounds(c);
        EffectiveDistanceAndCap(c);
        TerrainFadeFollowsTheCap(c);
        OwnershipMatchesTheDraw(c);
        TheCapHasOneTest(c);
        ConservativeNearHandoff(c);
        WaterOwnershipRadius(c);
        StableProjection(c);
            NearHandoffHysteresis(c);
    }

    /// <summary>
    /// The two distances cached water answers to, and the properties that keep each of them
    /// from being the visible boundary.
    ///
    /// There is no near cutoff measured from the camera at all, and that is the point. Two
    /// builds tried one - a share of the view distance, then an absolute distance - and both
    /// drew a visible ring across every lake, because a per-pixel distance test is a circle
    /// however small you make it and the world contains no circles. What remains is an
    /// off-by-default count of whole chunk columns, whose boundary falls on chunk edges.
    ///
    /// And there is no outward allowance either. Ownership hides cached water at every
    /// distance, exactly as it hides ground, so the only boundary cached water has is the
    /// mask's own 32-block cells - vanilla chunk edges.
    /// </summary>
    static void WaterOwnershipRadius(Check c)
    {
        c.Eq(0, LodNearHandoff.WaterNearChunks,
            "there is no near rule for cached water by default: the mask is the boundary");

        // Nothing in the water path is a function of the view distance any more. The mask's
        // cells are whole vanilla chunks, so the boundary falls on chunk edges at every view
        // distance rather than scaling with one.

        // The near cutoff cannot be the fallback handoff radius instead: that one subtracts a
        // fixed 192-block overlap, so it is zero exactly at the low view distances this mod
        // exists to make usable - which is where cached water sits closest to the player.
        c.Eq(0f, LodNearHandoff.InnerDiscardRadius(128f),
            "the fallback handoff radius collapses at a low view distance");
    }

    static void CachedBounds(Check c)
    {
        var bounds = new LodMeshBounds();
        c.Eq(0.0, bounds.FarthestDistanceTo(0, 0), "empty mesh bounds require no distance");

        // L0 (2,3) occupies [128,192] x [192,256]. The farthest corner from the
        // origin is (192,256), exactly 320 blocks away.
        long first = LodWorld.SectionKey(0, 2, 3);
        bounds.Include(first);
        c.Near(320, bounds.FarthestDistanceTo(0, 0), 0.0001,
            "bounds use the farthest footprint corner rather than a section centre");

        // L1 (2,1) occupies [256,384] x [128,256], expanding only the east edge.
        long coarse = LodWorld.SectionKey(1, 2, 1);
        bounds.Include(coarse);
        c.Near(Math.Sqrt(384 * 384 + 256 * 256), bounds.FarthestDistanceTo(0, 0), 0.0001,
            "mixed LOD footprints expand cached world bounds correctly");

        var strip = new LodMeshBounds();
        long northWest = LodWorld.SectionKey(0, 0, 0);
        long northEast = LodWorld.SectionKey(0, 2, 0);
        long southWest = LodWorld.SectionKey(0, 0, 2);
        long southEast = LodWorld.SectionKey(0, 2, 2);
        long middle = LodWorld.SectionKey(0, 1, 1);
        strip.Include(northWest);
        strip.Include(northEast);
        strip.Include(southWest);
        strip.Include(southEast);
        strip.Include(middle);

        strip.Remove(middle);
        c.False(strip.Dirty, "removing an interior mesh leaves cached extremes valid");

        strip.Remove(southEast);
        c.True(strip.Dirty, "removing an extreme mesh requests one bounds rebuild");

        // The second sequence represents a water-only mesh; rebuild must include it as
        // well as opaque terrain without needing a merged allocation or HashSet.
        long waterOnly = LodWorld.SectionKey(0, 4, 1);
        strip.Rebuild(new[] { northWest }, new[] { waterOnly });
        c.False(strip.Dirty, "rebuild makes cached bounds valid again");
        c.Near(Math.Sqrt(320 * 320 + 128 * 128), strip.FarthestDistanceTo(0, 0), 0.0001,
            "rebuild includes water-only mesh footprints");

        strip.Clear();
        c.False(strip.HasBounds, "clearing a world clears its mesh bounds");
    }

    static void EffectiveDistanceAndCap(Check c)
    {
        c.Eq(2536f, LodFarDistance.Effective(0, 1000, 0),
            "vanilla view distance retains its safety margin without LOD meshes");
        c.Eq(10000f, LodFarDistance.Effective(10000, 1000, 0),
            "unlimited .dlfar behavior follows the cached terrain edge");
        c.Eq(4096f, LodFarDistance.Effective(10000, 1000, 4096),
            "an explicit .dlfar cap limits the LOD far edge");
        c.Eq(2536f, LodFarDistance.Effective(10000, 1000, 2048),
            "the .dlfar cap cannot clip the vanilla view-distance safety band");
        c.Eq(4608f, LodFarDistance.RequiredProjection(4096),
            "the camera projection retains room beyond the visible LOD edge");
    }

    /// <summary>
    /// The fade ends at the cap once the projection's floor would have carried it past, and no
    /// cap the floor never touched moves at all.
    /// </summary>
    static void TerrainFadeFollowsTheCap(Check c)
    {
        c.Eq(8192f, LodFarDistance.TerrainFade(8192, 256, 8192),
            "a long cap keeps its fade exactly where it was");
        c.Eq(10000f, LodFarDistance.TerrainFade(10000, 256, 0),
            "no cap keeps its fade exactly where it was");
        c.Eq(1792f, LodFarDistance.TerrainFade(1792, 256, 1536),
            "a cap the floor only just holds up keeps its fade too");
        c.Eq(1536f, LodFarDistance.TerrainFade(1792, 256, 1024),
            "a short cap brings the fade in");
        c.Eq(1024f, LodFarDistance.TerrainEnd(LodFarDistance.TerrainFade(1792, 256, 1024)),
            "and the terrain ends at the cap");
        c.Eq(384f, LodFarDistance.TerrainEnd(LodFarDistance.TerrainFade(1792, 256, 384)),
            "at the shortest caps too");

        float previous = float.MinValue;
        bool steady = true;
        bool continuous = true;
        for (int cap = 256; cap <= 4096; cap += 16)
        {
            float effective = LodFarDistance.Effective(32768, 256, cap);
            float end = LodFarDistance.TerrainEnd(LodFarDistance.TerrainFade(effective, 256, cap));
            steady &= end >= previous;
            continuous &= previous == float.MinValue || end - previous <= 16f;
            previous = end;
        }
        c.True(steady, "a longer cap never ends the terrain sooner");
        c.True(continuous, "and the slider has no step where the rule changes");

        for (int view = 32; view <= 2048; view += 32)
        {
            float fade = LodFarDistance.TerrainFade(
                LodFarDistance.Effective(32768, view, 256), view, 256);
            float length = fade - view * LodFarDistance.ShaderFadeStartFraction
                - LodFarDistance.ShaderFadeMargin;
            if (length >= LodFarDistance.MinimumFadeLength - 0.01f) continue;
            c.True(false, $"the shader's divisor stays positive at view {view}, got {length}");
        }
        c.True(LodFarDistance.TerrainFade(1792, 256, 8192) == 1792f,
            "a cap past the mesh edge changes nothing");

        string assets = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "assets",
            "dodenslandscapes", "shaderincludes");
        string vertex = File.ReadAllText(Path.Combine(assets, "lodvertexsurface.vsh"));
        string ownership = File.ReadAllText(Path.Combine(assets, "lodownershiptest.fsh"));
        c.True(vertex.Contains("float distStart = viewDistance * 0.785;", StringComparison.Ordinal)
                && vertex.Contains("dist = (radial - distStart) / (farViewDistance - distStart - 512.0);",
                    StringComparison.Ordinal),
            "the fade's arithmetic here is still the shader's");
        c.True(ownership.Contains("dist > 1.0) discard;", StringComparison.Ordinal),
            "the fade's end still discards, which is what makes it the terrain's end");
    }

    /// <summary>
    /// What Farseer is told is drawn. A cell it gives up with nothing drawn on it is the void, so
    /// a cell cut by the terrain's end stays Farseer's from anywhere inside the player's chunk.
    /// </summary>
    static void OwnershipMatchesTheDraw(Check c)
    {
        c.Eq(16.0, LodFarDistance.SnapCamera(0), "a camera snaps to its chunk's middle");
        c.Eq(16.0, LodFarDistance.SnapCamera(31.9), "from anywhere inside it");
        c.Eq(48.0, LodFarDistance.SnapCamera(32), "the next chunk starts on its line");
        c.Eq(-16.0, LodFarDistance.SnapCamera(-0.1), "and the snap floors below zero");

        c.True(LodFarDistance.CellInsideTerrain(0, 0, 32, 16, 16, 1024f),
            "the cell under the player is inside");
        c.False(LodFarDistance.CellInsideTerrain(2048, 0, 32, 16, 16, 1024f),
            "a cell past the terrain's end is not");
        c.False(LodFarDistance.CellInsideTerrain(0, 0, 32, 16, 16, 16f),
            "nothing is inside an end shorter than the slack");

        // Farseer skips a tile's coverage when the whole tile is outside: that must never skip
        // a tile holding a cell the rule above would grant, from anywhere in the player's chunk.
        bool skipSafe = true;
        int skipped = 0, kept = 0;
        for (double camX = 0; camX < 32; camX += 7.3)
        {
            double snapX = LodFarDistance.SnapCamera(camX);
            double snapZ = LodFarDistance.SnapCamera(31 - camX);
            for (int tileZ = -6; tileZ < 6; tileZ++)
            {
                for (int tileX = -6; tileX < 6; tileX++)
                {
                    if (!LodFarDistance.AreaOutsideTerrain(tileX * 512, tileZ * 512, 512,
                            snapX, snapZ, 1800f))
                    {
                        kept++;
                        continue;
                    }
                    skipped++;
                    for (int cz = 0; cz < 16; cz++)
                        for (int cx = 0; cx < 16; cx++)
                            if (LodFarDistance.CellInsideTerrain(tileX * 512 + cx * 32,
                                    tileZ * 512 + cz * 32, 32, snapX, snapZ, 1800f))
                                skipSafe = false;
                }
            }
        }
        c.True(skipSafe, "no tile skipped as outside the terrain holds a cell inside it");
        c.True(skipped > 0 && kept > 0, $"the skip test saw both kinds of tile ({skipped} skipped, {kept} kept)");
        c.True(LodFarDistance.AreaOutsideTerrain(0, 0, 512, 16, 16, 16f),
            "with an end shorter than the slack every tile is outside");

        // The rule's whole promise, from every position in a chunk: no cell granted past the
        // shader's discard, for a true camera the snap has moved by as much as it can.
        const float end = 1024f;
        const int cell = 32;
        bool heldInside = true;
        int granted = 0;
        for (double cameraX = 0; cameraX < 32; cameraX += 3.1)
        {
            for (double cameraZ = 0; cameraZ < 32; cameraZ += 3.1)
            {
                double snappedX = LodFarDistance.SnapCamera(cameraX);
                double snappedZ = LodFarDistance.SnapCamera(cameraZ);
                for (int z = -48; z < 48; z++)
                {
                    for (int x = -48; x < 48; x++)
                    {
                        if (!LodFarDistance.CellInsideTerrain(
                                x * cell, z * cell, cell, snappedX, snappedZ, end)) continue;
                        granted++;
                        double dx = Math.Max(Math.Abs(x * cell - cameraX),
                            Math.Abs(x * cell + cell - cameraX));
                        double dz = Math.Max(Math.Abs(z * cell - cameraZ),
                            Math.Abs(z * cell + cell - cameraZ));
                        heldInside &= Math.Sqrt(dx * dx + dz * dz) <= end;
                    }
                }
            }
        }
        c.True(granted > 0 && heldInside,
            "no granted cell reaches past the terrain's end from anywhere in the player's chunk");

        float radius = LodTerrainRenderer.TerrainFogRadius(1792f, 1536f);
        c.Eq(944f, radius, "the fog's disc stops short of a short cap's end");
        LodSilhouetteCoverage disc =
            LodTerrainRenderer.TerrainFogAvailableCells(0, 0, 4, 4, radius);
        bool grantable = true;
        for (int z = 0; z < LodSilhouetteField.CoverageCellsPerEdge; z++)
            for (int x = 0; x < LodSilhouetteField.CoverageCellsPerEdge; x++)
                if (disc.Covered(x, z))
                    grantable &= LodFarDistance.CellInsideTerrain(x * 32, z * 32, 32,
                        LodFarDistance.SnapCamera(4), LodFarDistance.SnapCamera(4), 1024f);
        c.True(grantable, "so it never holds a cell ownership cannot grant, and draws no ring");
        c.Eq(1200f, LodTerrainRenderer.TerrainFogRadius(1792f, 1792f),
            "a floor-held far distance stops the disc short of the terrain's end as well");
        c.Eq(6144f, LodTerrainRenderer.TerrainFogRadius(8192f, 8192f),
            "a long far distance keeps three quarters, as before");
        c.Eq(LodTerrainRenderer.TerrainFogMinimumRadius,
            LodTerrainRenderer.TerrainFogRadius(1792f, 640f),
            "and the shortest end keeps a small disc");
    }

    /// <summary>
    /// One rule in one place. The fault this guards against was the draw and the ownership
    /// answer each asking its own question about the cap.
    /// </summary>
    static void TheCapHasOneTest(Check c)
    {
        c.Eq(float.MaxValue, LodFarDistance.CullDistanceSquared(0), "no cap culls nothing");
        c.Eq(1088f * 1088f, LodFarDistance.CullDistanceSquared(1024),
            "a cap culls one level-0 section past itself");
        float cull = LodFarDistance.CullDistanceSquared(1024);
        c.False(LodFarDistance.SectionPastCull(0, 0, 64, 32, 32, cull),
            "a section under the camera draws");
        c.False(LodFarDistance.SectionPastCull(1024, 0, 64, 0, 0, cull),
            "as does one centred inside the limit");
        c.True(LodFarDistance.SectionPastCull(1088, 0, 64, 0, 0, cull),
            "one centred past it does not");
        c.False(LodFarDistance.SectionPastCull(0, -1024, 2048, 0, 0, cull),
            "a coarse section is judged by its centre, however far its edge reaches");
        c.True(LodFarDistance.SectionPastCull(1024, 1024, 1024, 0, 0, cull),
            "and one whose near corner is inside but whose centre is not stays out");

        string src = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src", "Render");
        string renderer = File.ReadAllText(Path.Combine(src, "LodTerrainRenderer.cs"));
        // Coverage is worked out in its own file since dev.30, which remembers the answers.
        string pass = File.ReadAllText(Path.Combine(src, "LodTerrainRenderer.Silhouette.cs"))
            + File.ReadAllText(Path.Combine(src, "LodTerrainRenderer.SilhouetteCoverage.cs"));
        string depth = File.ReadAllText(Path.Combine(src, "LodTerrainRenderer.WaterDepth.cs"));
        c.True(renderer.Contains("if (LodFarDistance.SectionPastCull(originX, originZ, footprint,",
                    StringComparison.Ordinal)
                && pass.Contains("if (LodFarDistance.SectionPastCull(originX, originZ, footprint,",
                    StringComparison.Ordinal),
            "the draw and Farseer's ownership ask the cap the same question");
        c.True(pass.Contains("!LodFarDistance.CellInsideTerrain(cellMinX, cellMinZ,",
                    StringComparison.Ordinal),
            "and ownership stops at the terrain's end");
        c.True(renderer.Contains("prog.Uniform(\"farViewDistance\", TerrainFadeDistance);",
                    StringComparison.Ordinal)
                && renderer.Contains("LodFarDistance.RequiredProjection(EffectiveFarDistance)",
                    StringComparison.Ordinal),
            "the fade takes the cap, and the projection keeps its floor (G5)");
        c.False(renderer.Contains("FarViewDistanceCap + LodSection.SectionBlocks", StringComparison.Ordinal)
                || depth.Contains("FarViewDistanceCap + LodSection.SectionBlocks", StringComparison.Ordinal),
            "no pass works the cull distance out for itself");
    }

    static void ConservativeNearHandoff(Check c)
    {
        c.Eq(0f, LodNearHandoff.InnerDiscardRadius(0),
            "no vanilla radius keeps all available cached fallback");
        c.Eq(0f, LodNearHandoff.InnerDiscardRadius(128),
            "a tiny view retains the minimum fallback overlap");
        c.Eq(64f, LodNearHandoff.InnerDiscardRadius(256),
            "a 256-block view hands off only in its close 64-block core");
        c.Eq(256f, LodNearHandoff.InnerDiscardRadius(512),
            "a normal view keeps cached fallback through its inner half");
        c.Eq(512f, LodNearHandoff.InnerDiscardRadius(1024),
            "a large view still hands off no farther out than half radius");

        foreach (float view in new[] { 192f, 256f, 512f, 1024f, 4096f })
        {
            float inner = LodNearHandoff.InnerDiscardRadius(view);
            c.True(view - inner >= LodNearHandoff.MinimumFallbackOverlap,
                $"view distance {view} retains at least the minimum fallback overlap");
            c.True(inner <= view * LodNearHandoff.MaximumInnerFraction,
                $"view distance {view} never hands off outside half radius");
        }
    }

    static void StableProjection(Check c)
    {
        var state = new LodFarPlaneState();

        LodFarPlaneUpdate update = state.Update(3512, 0);
        c.True(update.Changed, "the first projection distance is applied");
        c.Eq(3584f, update.Distance, "projection distance rounds up to a safe 512-block step");

        update = state.Update(3585, 100);
        c.True(update.Changed, "crossing a distance step grows the projection immediately");
        c.Eq(4096f, update.Distance, "growth selects the next complete distance step");

        update = state.Update(3600, 200);
        c.False(update.Changed, "movement within one distance band does not reset projection");
        c.Eq(4096f, update.Distance, "the applied projection remains stable within a band");

        update = state.Update(3500, 1000);
        c.False(update.Changed, "a lower band starts a shrink cooldown instead of resetting immediately");
        update = state.Update(3500, 5999);
        c.False(update.Changed, "projection does not shrink before the cooldown expires");
        update = state.Update(3500, 6000);
        c.True(update.Changed, "projection shrinks after a stable full cooldown");
        c.Eq(3584f, update.Distance, "shrink lands on the safe upper edge of the lower band");

        update = state.Update(3000, 7000);
        c.False(update.Changed, "another lower band starts a new cooldown");
        update = state.Update(3580, 7100);
        c.False(update.Changed, "returning to the applied band cancels pending shrink");
        update = state.Update(3000, 12000);
        c.False(update.Changed, "a cancelled shrink must earn a fresh cooldown");

        update = state.Update(5000, 12001);
        c.True(update.Changed, "required growth interrupts a shrink and applies immediately");
        c.Eq(5120f, update.Distance, "immediate growth still rounds upward safely");

        state.Reset();
        update = state.Update(3000, 20000);
        c.True(update.Changed, "a new world applies a fresh projection after state reset");
        c.Eq(3072f, update.Distance, "reset does not retain the previous world's far plane");
    }
    /// <summary>
    /// The readiness-derived handoff must restore cached coverage the instant measured
    /// ownership shrinks, and must make a player wait before it hides cached terrain that
    /// vanilla may not have drawn yet. A hole is worse than an overlap in every ordering
    /// the plan states, so the asymmetry is the whole point of this state.
    /// </summary>
    static void NearHandoffHysteresis(Check c)
    {
        var state = new LodNearHandoffState();
        c.Eq(0f, state.Update(200, 0), "the first grow waits rather than suppressing cache immediately");
        c.Eq(0f, state.Update(200, 499), "growth is withheld for the whole hold period");
        c.Eq(192f, state.Update(200, 500),
            "growth applies after the hold, quantized down to a whole vanilla chunk");

        c.Eq(64f, state.Update(64, 501), "a measured shrink restores cached coverage in the same frame");
        c.Eq(0f, state.Update(0, 502), "losing all measured ownership hands everything back to the cache");

        // The smallest radius seen during a hold is what applies, so continuous movement
        // neither stalls growth forever nor lets a transient peak through.
        c.Eq(0f, state.Update(300, 600), "a new grow candidate starts its own hold");
        c.Eq(0f, state.Update(128, 700), "a lower reading during the hold does not apply early");
        c.Eq(128f, state.Update(300, 1100),
            "the hold applies the smallest radius it saw rather than the largest");

        var settled = new LodNearHandoffState();
        settled.Update(96, 0);
        c.Eq(96f, settled.Update(96, 1000), "a stable measurement settles at its quantized value");
        c.Eq(96f, settled.Update(127, 3000), "a sub-chunk increase does not move the applied radius");
    }
}
