namespace VintageHorizons.Checks;

public static class WaterJunctionChecks
{
    // CPU raster fixture for the depth ownership contract. Geometry coverage is an
    // input, not inferred from neighbouring image pixels. GLSL still needs playtesting.
    static float Draw(float vanilla, float geometryDepth, bool rasterized, int band,
        bool owned, bool[,] neighbours, bool enabled)
    {
        if (!rasterized || band != 1) return vanilla;
        bool reject = owned;
        if (owned && enabled && vanilla == 1f)
            for (int z = 0; z < 3; z++)
                for (int x = 0; x < 3; x++)
                    if ((x != 1 || z != 1) && !neighbours[z, x]) reject = false;
        return reject ? vanilla : Math.Min(vanilla, geometryDepth);
    }

    static bool[,] Interior()
    {
        var cells = new bool[3, 3];
        for (int z = 0; z < 3; z++)
            for (int x = 0; x < 3; x++) cells[z, x] = true;
        return cells;
    }

    // OIT uses the same ownership exception, but augments the quarter-resolution
    // depth signal with an immutable full-resolution revealage signal. A cached
    // surface is kept only when its geometry really rasterized at a boundary.
    static bool KeepCachedSurface(bool rasterized, int band, bool owned,
        bool[,] neighbours, bool depthClear, bool revealageClear,
        bool depthEnabled, bool surfaceEnabled)
    {
        if (!rasterized || band != 1) return false;
        bool reject = owned;
        bool coverageMissing = depthClear || (surfaceEnabled && revealageClear);
        if (owned && depthEnabled && coverageMissing)
            for (int z = 0; z < 3; z++)
                for (int x = 0; x < 3; x++)
                    if ((x != 1 || z != 1) && !neighbours[z, x]) reject = false;
        return !reject;
    }

    public static void Run(Check c)
    {
        var interior = Interior();
        c.Eq(1f, Draw(1f, .9992f, true, 1, true, interior, true),
            "a missing lake inside vanilla territory cannot be filled by stale cached water");
        for (int z = 0; z < 3; z++)
            for (int x = 0; x < 3; x++)
            {
                if (x == 1 && z == 1) continue;
                var edge = Interior();
                edge[z, x] = false;
                c.Eq(.9992f, Draw(1f, .9992f, true, 1, true, edge, true),
                    "each edge and corner allows actual cached geometry into a clear junction pixel");
                c.Eq(1f, Draw(1f, .9992f, true, 1, true, edge, false),
                    "comparison off reproduces the old chunk-mask hole");
                c.Eq(1f, Draw(1f, .9992f, false, 1, true, edge, true),
                    "an actual opening without cached water geometry stays empty");
                foreach (int band in new[] { 0, 2 })
                    c.Eq(1f, Draw(1f, .9992f, true, band, true, edge, true),
                        "ground and thin vegetation cannot supply liquid depth");
                foreach (float realDepth in new[] { 0f, .99f, .9992796f, MathF.BitDecrement(1f) })
                    c.Eq(realDepth, Draw(realDepth, .5f, true, 1, true, edge, true),
                        "every existing vanilla depth is preserved even if cached water is nearer");
            }

        var junction = Interior();
        junction[0, 1] = false;
        // Simulate changing water coverage across a stationary boundary. Existing
        // animated depths remain exact; newly uncovered samples accept cached depth.
        for (int frame = 0; frame < 32; frame++)
        {
            float vanilla = frame % 3 == 0 ? 1f : .999f + frame * .000001f;
            float result = Draw(vanilla, .9992f, true, 1, true, junction, true);
            c.True(result < 1f, "changing vanilla edge coverage leaves no empty sample when cached geometry covers it");
            if (vanilla < 1f) c.Eq(vanilla, result, "moving real water remains authoritative");
        }

        c.True(KeepCachedSurface(true, 1, true, junction,
                depthClear: false, revealageClear: true,
                depthEnabled: true, surfaceEnabled: true),
            "a full-resolution OIT hole is filled even when quarter-resolution depth is covered");
        c.False(KeepCachedSurface(true, 1, true, junction,
                depthClear: false, revealageClear: false,
                depthEnabled: true, surfaceEnabled: true),
            "an existing vanilla OIT surface remains authoritative at the boundary");
        c.False(KeepCachedSurface(true, 1, true, junction,
                depthClear: false, revealageClear: true,
                depthEnabled: true, surfaceEnabled: false),
            "the full-resolution exception is inactive when its immutable copy is unavailable");
        c.False(KeepCachedSurface(true, 1, true, interior,
                depthClear: false, revealageClear: true,
                depthEnabled: true, surfaceEnabled: true),
            "a full-resolution hole inside vanilla territory cannot reveal stale cached water");
        c.False(KeepCachedSurface(false, 1, true, junction,
                depthClear: false, revealageClear: true,
                depthEnabled: true, surfaceEnabled: true),
            "a revealage hole without cached geometry remains empty");

        string root = GameAssemblies.RepoRoot;
        string ownership = File.ReadAllText(Path.Combine(root,
            "VintageHorizons/assets/vintagehorizons/shaderincludes/lodownershiptest.fsh"));
        c.True(ownership.Contains("#if defined(VH_WATER_DEPTH) || defined(VH_WATER_OIT)")
            && ownership.Contains("owned && ownershipBand == 1 && waterJunctionDepth == 1")
            && ownership.Contains("texelFetch(vanillaWaterDepth, ivec2(gl_FragCoord.xy), 0).r == 1.0")
            && ownership.Contains("texture(vanillaWaterDepth, gl_FragCoord.xy / frameSize.xy).r == 1.0")
            && ownership.Contains("vanillaOwnsCell(cellX + dx, cellY, cellZ + dz, ownershipBand)"),
            "GLSL gives depth and OIT the same clear, water-only boundary exception");
        c.True(ownership.Contains("texelFetch(")
            && ownership.Contains("vanillaOitRevealage, ivec2(gl_FragCoord.xy), 0).r")
            && ownership.Contains("waterJunctionSurface == 1")
            && ownership.Contains("vanillaRevealage >= 0.9999"),
            "OIT augments quarter-resolution depth with exact full-resolution vanilla coverage");
        c.True(!ownership.Contains("liquidDepth,") && !ownership.Contains("gl_FragDepth")
            && ownership.Contains("dz = -1; dz <= 1") && ownership.Contains("dx = -1; dx <= 1"),
            "the exception uses immutable coverage and adjacent cells without synthesizing depth");

        string pass = File.ReadAllText(Path.Combine(root, "VintageHorizons/src/Render/LodTerrainRenderer.WaterDepth.cs"));
        c.True(pass.IndexOf("SnapshotWaterDepth(target, vanillaOnly: true);") < pass.IndexOf("DrawWaterDepthMeshes(cullSq);")
            && pass.Contains("prog.BindTexture2D(\"vanillaWaterDepth\", waterVanillaDepthTexture,")
            && pass.Contains("GL.BindTexture(TextureTarget.Texture2D, previousJunctionTexture)"),
            "the pass freezes vanilla coverage first and restores the new sampler binding");
        string oit = File.ReadAllText(Path.Combine(root, "VintageHorizons/src/Render/LodTerrainRenderer.WaterOit.cs"));
        c.True(oit.Contains("waterJunctionSnapshotReady")
            && oit.Contains("prog.BindTexture2D(\"vanillaWaterDepth\", waterVanillaDepthTexture,")
            && oit.Contains("prog.BindTexture2D(\"vanillaOitRevealage\", waterVanillaRevealageTexture,"),
            "OIT binds frozen depth and full-resolution surface coverage before drawing cached water");
        string repair = File.ReadAllText(Path.Combine(root, "VintageHorizons/src/Render/LodTerrainRenderer.WaterRepair.cs"));
        c.True(repair.Contains("waterVanillaDepthTexture")
            && repair.Contains("waterVanillaDepthFramebuffer")
            && repair.Contains("SnapshotWaterDepth(target, vanillaOnly: false);"),
            "repair has a second snapshot and cannot overwrite vanilla coverage before OIT");
        c.True(pass.Contains("junctionReady && waterCaptureDirectory != null")
            && pass.Contains("02-mask-only-liquid-depth")
            && pass.Contains("02a-unrepaired-liquid-depth"),
            "explicit captures distinguish mask-only, junction and later repair in the same frame");
    }
}
