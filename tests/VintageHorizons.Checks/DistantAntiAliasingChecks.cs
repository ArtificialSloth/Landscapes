namespace VintageHorizons.Checks;

/// <summary>
/// Distant anti-aliasing and automatic LOD thresholds: the screen arithmetic both share, the
/// ramp that decides where terrain is nudged and blended, and the wiring that keeps water,
/// vanilla terrain and near cached ground out of it.
///
/// What this was built against, measured from the owner's capture on 2026-09-11: at 60 degrees
/// and 1080 rendered rows one block is one pixel at 935 blocks, which is where the owner saw the
/// shimmer begin, and past 512 blocks 79-89% of snow pixels were one row tall. A 20-degree FOV,
/// three times the on-screen size with depth precision unchanged, greatly diminished it in play.
/// </summary>
public static class DistantAntiAliasingChecks
{
    public static void Run(Check c)
    {
        ScreenScale(c);
        Ramp(c);
        Jitter(c);
        AutomaticThresholds(c);
        Defaults(c);
        ShaderWiring(c);
        RendererWiring(c);
    }

    /// <summary>The projection's Y scale for a vertical field of view in degrees.</summary>
    static float YScale(double degrees) => (float)(1.0 / Math.Tan(degrees * Math.PI / 360.0));

    static void ScreenScale(Check c)
    {
        c.Near(935.3, LodScreenScale.BlockScale(1080, YScale(60)), 0.2,
            "one block is one rendered pixel at ~935 blocks at 60 degrees and 1080 rows");
        c.Near(1247.1, LodScreenScale.BlockScale(1440, YScale(60)), 0.2,
            "and at ~1247 blocks once all 1440 rows are rendered");
        c.Eq(0f, LodScreenScale.BlockScale(0, YScale(60)), "no render target means no scale");
        c.Eq(0f, LodScreenScale.BlockScale(1080, 0f),
            "and neither does a projection without a Y scale");
        c.Near(467.6, LodScreenScale.DepthForBlockPixels(935.3f, 2f), 0.1,
            "a block is two pixels wide at half that distance");
    }

    static void Ramp(Check c)
    {
        const float scale = 1000f;
        c.Eq(0f, LodTaaMath.Weight(scale, 400f), "a block 2.5 px across is left exactly as drawn");
        c.Eq(0f, LodTaaMath.Weight(scale, 500f), "and so is one exactly 2 px across");
        c.Near(0.5, LodTaaMath.Weight(scale, 2000f / 3f), 1e-4, "1.5 px is half strength");
        c.Eq(1f, LodTaaMath.Weight(scale, 1000f), "1 px is full strength");
        c.Eq(1f, LodTaaMath.Weight(scale, 8000f), "and so is anything smaller");
        c.Eq(0f, LodTaaMath.Weight(0f, 8000f), "a zero scale switches it off everywhere");
        c.Eq(0f, LodTaaMath.Weight(scale, 0f), "and a zero depth is never weighted");
        c.True(LodTaaMath.StartBlockPixels > LodTaaMath.FullBlockPixels,
            "the ramp runs from larger blocks to smaller ones");
        c.True(LodTaaMath.DefaultFeedback < LodTaaMath.MaxFeedback,
            "the default history share leaves room to raise it");
    }

    static void Jitter(Check c)
    {
        var seen = new HashSet<(float, float)>();
        double sumX = 0, sumY = 0;
        bool inRange = true;
        for (long frame = 0; frame < LodTaaMath.JitterSamples; frame++)
        {
            (float x, float y) = LodTaaMath.JitterPixels(frame);
            inRange &= x >= -0.5f && x < 0.5f && y >= -0.5f && y < 0.5f;
            seen.Add((x, y));
            sumX += x;
            sumY += y;
        }
        c.True(inRange, "every nudge stays within half a pixel of the pixel centre");
        c.Eq(LodTaaMath.JitterSamples, seen.Count,
            "the nudge visits a different position every frame of its cycle");
        c.True(Math.Abs(sumX / LodTaaMath.JitterSamples) < 0.07
                && Math.Abs(sumY / LodTaaMath.JitterSamples) < 0.07,
            "and centres on the pixel, so blending cannot drift the image");
        c.Eq(LodTaaMath.JitterPixels(3), LodTaaMath.JitterPixels(3 + LodTaaMath.JitterSamples),
            "the pattern repeats with its period");
        c.Eq(LodTaaMath.JitterPixels(LodTaaMath.JitterSamples - 1), LodTaaMath.JitterPixels(-1),
            "and a negative frame number cannot index outside it");
    }

    static void AutomaticThresholds(Check c)
    {
        float scale = LodScreenScale.BlockScale(1080, YScale(60));
        int[] owner = LodAutoThresholds.Compute(scale, 1.5f, 320);
        c.SeqEq(new[] { 624, 1248, 2496, 4992, 9984, 19968 }, owner,
            "the owner's settings give a doubling sequence from 624 blocks");
        for (int level = 1; level <= LodWorld.MaxLevel; level++)
        {
            double columnBlocks = 1 << (level - 1);
            c.Near(1.5, columnBlocks * scale / owner[level - 1], 0.02,
                $"level {level - 1} hands over at 1.5 px on screen");
        }

        c.Eq(832, LodAutoThresholds.Compute(LodScreenScale.BlockScale(1440, YScale(60)), 1.5f, 320)[0],
            "rendering all 1440 rows moves L1 out to 832 blocks");
        c.True(LodAutoThresholds.Compute(LodScreenScale.BlockScale(1080, YScale(90)), 1.5f, 320)[0]
                < owner[0],
            "a wider field of view brings the levels in");

        int[] held = LodAutoThresholds.Compute(scale, 1.5f, 1000);
        c.True(held[0] >= 1000, "level 0 is held out past a far vanilla edge and its blend band");
        c.Eq(held[0] * 2, held[1], "and the levels after it still double");

        c.Eq(LodWorld.MinDetailDistance, LodAutoThresholds.Compute(1f, 1.5f, 0)[0],
            "a degenerate scale cannot pull L1 below the configurable minimum");
        int[] huge = LodAutoThresholds.Compute(1e9f, 1.5f, 0);
        bool increasing = true, within = true;
        for (int i = 0; i < huge.Length; i++)
        {
            within &= huge[i] >= LodWorld.MinDetailDistance && huge[i] <= LodWorld.MaxLevelThreshold;
            if (i > 0) increasing &= huge[i] > huge[i - 1];
        }
        c.True(within && increasing,
            "an absurd scale still yields six ordered thresholds inside the allowed range");
        c.SeqEq(owner, LodAutoThresholds.Compute(scale, 1.5f, 320),
            "the same inputs always give the same thresholds");
        c.SeqEq(LodAutoThresholds.Compute(scale, LodAutoThresholds.MaxTargetPixels, 320),
            LodAutoThresholds.Compute(scale, 99f, 320), "the target is clamped to its range");
    }

    static void Defaults(Check c)
    {
        var config = new VintageHorizonsConfig();
        c.True(config.DistantAntiAliasing, "distant anti-aliasing is on for a fresh install");
        c.True(config.AutoLodThresholds, "and so are automatic LOD thresholds");
        c.Eq(LodAutoThresholds.DefaultTargetPixels, config.LodTargetPixels,
            "aiming at the default on-screen size");
        c.True(LodVisualSettings.Default.DistantAntiAliasing, "the Defaults button keeps it on");
        c.True(LodVisualSettings.Quality.DistantAntiAliasing
                && LodVisualSettings.Balanced.DistantAntiAliasing,
            "so do Quality and Balanced");
        c.False(LodVisualSettings.Performance.DistantAntiAliasing,
            "and Performance gives it up with everything else optional");
    }

    static string Asset(string folder, string file) => File.ReadAllText(Path.Combine(
        GameAssemblies.RepoRoot, "VintageHorizons", "assets", "vintagehorizons", folder, file))
        .Replace("\r\n", "\n");

    static void ShaderWiring(Check c)
    {
        string body = Asset("shaderincludes", "lodterrainbody.vsh");
        int surface = body.IndexOf("#include lodvertexsurface.vsh", StringComparison.Ordinal);
        int nudge = body.IndexOf("gl_Position.xy += vec2(vhTaaJitterX, vhTaaJitterY)",
            StringComparison.Ordinal);
        c.True(surface >= 0 && nudge > surface,
            "the terrain body nudges the finished position, after the shared surface code sets it");
        c.True(body.Contains("vhTaaBlockScale / gl_Position.w", StringComparison.Ordinal)
                && body.Contains("(vhTaaStartPixels - vhBlockPixels)", StringComparison.Ordinal),
            "and only in proportion to how small a block is on screen at that vertex");

        foreach (string shared in new[] { "lodvertexsurface.vsh", "lodwaterbody.vsh" })
        {
            c.False(Asset("shaderincludes", shared).Contains("vhTaa", StringComparison.Ordinal),
                $"{shared} carries no nudge: water shares it and is never blended, so it would wobble");
        }

        // The declarations do live in the shared inputs, and must. The engine's Uniform(name, ...)
        // indexes a dictionary built by matching the uniform declarations in a program's source,
        // so an upload naming a uniform the program never declared throws - and the water
        // programs receive the same frame uploads as the terrain. The first build of this put the
        // declarations in the terrain body alone, which would have thrown in every water pass.
        string inputs = Asset("shaderincludes", "lodvertexinputs.vsh");
        string rendererSource = File.ReadAllText(Path.Combine(GameAssemblies.RepoRoot,
            "VintageHorizons", "src", "Render", "LodTerrainRenderer.cs"));
        string[] uploaded = System.Text.RegularExpressions.Regex
            .Matches(rendererSource, @"prog\.Uniform\(""(?<name>vhTaa\w+)""")
            .Select(match => match.Groups["name"].Value)
            .Distinct()
            .ToArray();
        c.Eq(5, uploaded.Length, "the frame upload sets all five nudge uniforms");
        foreach (string name in uploaded)
        {
            c.True(inputs.Contains($"uniform float {name};", StringComparison.Ordinal),
                $"{name} is declared in the vertex inputs every frame-uniform program shares");
        }
        c.False(inputs.Contains("gl_Position.xy += vec2(vhTaaJitterX", StringComparison.Ordinal),
            "and the shared inputs only declare them; the nudge is the terrain body's alone");

        string resolve = Asset("shaders", "lodtaaresolve.fsh");
        c.True(resolve.Contains("if (!(depth < before))", StringComparison.Ordinal),
            "the resolve touches only pixels cached terrain drew this frame");
        c.True(resolve.Contains("abs(storedDepth - previousClip.w)", StringComparison.Ordinal),
            "rejects history where different ground stood last frame");
        c.True(resolve.Contains("clamp(texture(history, previousUv).rgb, lo, hi)",
                StringComparison.Ordinal),
            "and clamps history to what this frame's neighbourhood can show");
        c.True(Asset("shaders", "lodtaacomposite.fsh").Contains("discard", StringComparison.Ordinal),
            "the composite leaves every other pixel exactly as drawn");
    }

    static void RendererWiring(Check c)
    {
        string src = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "src");
        string renderer = File.ReadAllText(Path.Combine(src, "Render", "LodTerrainRenderer.cs"));
        string taa = File.ReadAllText(Path.Combine(src, "Render", "LodTerrainRenderer.Taa.cs"));
        string mod = File.ReadAllText(Path.Combine(src, "VintageHorizonsModSystem.cs"));

        int begin = renderer.IndexOf("BeginTaaFrame();", StringComparison.Ordinal);
        int pyramid = begin < 0 ? -1
            : renderer.IndexOf("BuildDepthPyramid();", begin, StringComparison.Ordinal);
        int water = renderer.IndexOf("renderPaths.DrawWater();", StringComparison.Ordinal);
        int resolve = renderer.IndexOf("ResolveTaa();", StringComparison.Ordinal);
        c.True(begin >= 0 && pyramid > begin,
            "the before-terrain depth snapshot is taken before a single cached draw");
        c.True(water >= 0 && resolve > water,
            "and the resolve runs after thin cover, which the terrain program also nudged");
        c.True(renderer.Contains("prog.Uniform(\"vhTaaStartPixels\", LodTaaMath.StartBlockPixels);",
                StringComparison.Ordinal)
            && taa.Contains("resolve.Uniform(\"startPixels\", LodTaaMath.StartBlockPixels);",
                StringComparison.Ordinal),
            "the nudge and the blend read one ramp");
        c.True(taa.Contains("GL.ColorMask(true, true, true, false);", StringComparison.Ordinal),
            "the composite never writes alpha");
        c.True(renderer.Contains("UpdateAutoThresholds();", StringComparison.Ordinal),
            "automatic thresholds are re-evaluated every prepared frame");
        c.True(mod.Contains("if (!renderer.AutoLodThresholds)", StringComparison.Ordinal),
            "saving with automatic thresholds on keeps the player's own distances in the file");
    }
}
