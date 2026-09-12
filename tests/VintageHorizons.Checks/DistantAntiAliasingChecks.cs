namespace VintageHorizons.Checks;

/// <summary>
/// Distant anti-aliasing and automatic LOD thresholds: the screen arithmetic both share, the
/// ramp that decides where terrain is blended and nudged, the tuning dials, and the wiring that
/// keeps water, vanilla terrain and near cached ground out of it.
///
/// What this was built against, measured from the owner's capture on 2026-09-11: at 60 degrees
/// and 1080 rendered rows one block is one pixel at 935 blocks, which is where the owner saw the
/// shimmer begin, and past 512 blocks 79-89% of snow pixels were one row tall. A 20-degree FOV,
/// three times the on-screen size with depth precision unchanged, greatly diminished it in play.
/// 0.4.57 looked cleaner but vibrated with the camera still; the checks marked 0.4.58 keep its
/// three measured causes shut. 0.4.58 was much steadier but smeared, even standing still; the
/// checks marked 0.4.59 pin what answered that.
/// </summary>
public static class DistantAntiAliasingChecks
{
    public static void Run(Check c)
    {
        ScreenScale(c);
        Ramp(c);
        Dials(c);
        Jitter(c);
        AutomaticThresholds(c);
        Defaults(c);
        ShaderWiring(c);
        RendererWiring(c);
        SettingsWindow(c);
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
        const float reach = LodTaaMath.DefaultReachPixels;
        c.Eq(0f, LodTaaMath.Ramp(scale, 400f, reach), "a block 2.5 px across is left exactly as drawn");
        c.Eq(0f, LodTaaMath.Ramp(scale, 500f, reach), "and so is one exactly 2 px across");
        c.Near(0.5, LodTaaMath.Ramp(scale, 2000f / 3f, reach), 1e-4, "1.5 px is the middle of the fade");
        c.Eq(1f, LodTaaMath.Ramp(scale, 1000f, reach), "1 px is full strength");
        c.Eq(1f, LodTaaMath.Ramp(scale, 8000f, reach), "and so is anything smaller");
        c.Eq(0f, LodTaaMath.Ramp(0f, 8000f, reach), "a zero scale switches it off everywhere");
        c.Eq(0f, LodTaaMath.Ramp(scale, 0f, reach), "and a zero depth is never weighted");

        // 0.4.58: history first, then the nudge. 0.4.57 faded both in together, and the faded
        // zone showed most of its nudge; the owner saw it vibrate with the camera still.
        c.Eq(1f, LodTaaMath.HistoryStrength(0.5f), "history is at full share by the middle of the fade");
        c.Eq(0f, LodTaaMath.NudgeStrength(0.5f), "where the nudge has not begun");
        c.Eq(1f, LodTaaMath.NudgeStrength(1f), "and the nudge is full at the end of it");
        bool historyLeads = true;
        for (float t = 0f; t <= 1f; t += 1f / 64f)
            historyLeads &= LodTaaMath.NudgeStrength(t) <= 0f || LodTaaMath.HistoryStrength(t) >= 1f;
        c.True(historyLeads, "no pixel is ever nudged before its history is at full share");

        // The vertex shader computes the nudge from its own two endpoints. They must describe
        // exactly the second half of the resolve's ramp, at any reach.
        bool same = true;
        foreach (float r in new[] { 1f, 2f, 5f, 12f, 32f })
        {
            float start = LodTaaMath.NudgeStartPixels(r);
            float full = LodTaaMath.FullPixels(r);
            for (float depth = 10f; depth < 4000f; depth *= 1.07f)
            {
                float blockPixels = scale / depth;
                float vertex = Math.Clamp((start - blockPixels) / Math.Max(start - full, 0.001f), 0f, 1f);
                same &= Math.Abs(vertex - LodTaaMath.NudgeStrength(LodTaaMath.Ramp(scale, depth, r))) < 1e-4f;
            }
        }
        c.True(same, "the vertex shader's nudge ramp is the second half of the resolve's, at any reach");

        c.True(LodScreenScale.DepthForBlockPixels(scale, 12f)
                < LodScreenScale.DepthForBlockPixels(scale, reach),
            "a larger reach begins closer");
        c.Near(0.5, LodTaaMath.FullPixels(12f) / 12f, 1e-6,
            "and is full strength at half its width, one doubling of distance further out");
        c.Eq(LodTaaMath.MaxReachPixels, LodTaaMath.ClampReach(1000f), "the reach is kept to its range");
        c.Eq(LodTaaMath.MinReachPixels, LodTaaMath.ClampReach(0f), "at both ends");
        c.Eq(LodTaaMath.DefaultReachPixels, LodTaaMath.ClampReach(float.NaN),
            "and a value that is not a number is the default");
    }

    static void Dials(Check c)
    {
        c.True(LodTaaMath.DefaultFeedback < LodTaaMath.MaxFeedback,
            "the default turning memory leaves room to raise it");
        c.True(LodTaaMath.DefaultStillFeedback > LodTaaMath.DefaultFeedback
                && LodTaaMath.DefaultStillFeedback < LodTaaMath.MaxStillFeedback,
            "a still camera keeps more memory by default, with room to raise it further");
        c.True(LodTaaMath.StillMotionPixels < LodTaaMath.MovingMotionPixels
                && LodTaaMath.MovingMotionPixels < LodTaaMath.FastMotionPixels,
            "still, moving and fast are told apart by ramps, in that order");
        c.True(LodTaaMath.FastShare > 0f && LodTaaMath.FastShare < 1f,
            "a fast turn keeps less memory, but never none");
        c.True(LodTaaMath.MinClip < LodTaaMath.DefaultClip && LodTaaMath.DefaultClip < LodTaaMath.MaxClip,
            "the safety check's default sits inside its range");
        c.True(LodTaaMath.DefaultNudge > 0f && LodTaaMath.DefaultNudge <= 1f
                && LodTaaMath.DefaultSharpen >= 0f && LodTaaMath.DefaultSharpen <= 1f,
            "nudge and sharpening default inside 0 to 1");
        c.True(LodTaaMath.MaxSharpenLobe > 0f && LodTaaMath.MaxSharpenLobe < 0.25f,
            "full sharpening keeps the composite's divisor, 1 - 4 x lobe, above zero");
        c.Eq(LodTaaMath.MaxClip, LodTaaMath.ClampDial(99f, LodTaaMath.DefaultClip,
                LodTaaMath.MinClip, LodTaaMath.MaxClip),
            "every dial is kept to its range");
        c.Eq(LodTaaMath.DefaultSharpen, LodTaaMath.ClampDial(float.NaN, LodTaaMath.DefaultSharpen, 0f, 1f),
            "and a value that is not a number falls back to the default");

        // 0.4.59: the still memory waits for the whole camera and eases in. 0.4.58 decided per
        // pixel, so walking counted as still and a turn's blur was held the moment it stopped.
        c.Eq(0f, LodTaaMath.Stillness(0), "the still memory is not in force the frame the camera stops");
        c.Near(0.5, LodTaaMath.Stillness(LodTaaMath.StillEaseFrames / 2), 1e-6, "it eases in");
        c.Eq(1f, LodTaaMath.Stillness(LodTaaMath.StillEaseFrames * 4),
            "and is fully in force once the camera has held still long enough");
        c.True(LodTaaMath.StillStepBlocks < 0.004,
            "a camera sneaking along at a thousand frames a second still counts as moving");
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
        c.Eq(LodTaaMath.DefaultReachPixels, config.TaaReachPixels,
            "anti-aliasing begins where a block is two pixels wide unless the player says otherwise");
        c.True(config.TaaBlend == LodTaaMath.DefaultFeedback
                && config.TaaStillBlend == LodTaaMath.DefaultStillFeedback
                && config.TaaNudge == LodTaaMath.DefaultNudge
                && config.TaaSharpen == LodTaaMath.DefaultSharpen
                && config.TaaClip == LodTaaMath.DefaultClip,
            "and every tuning dial starts at its default");
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

    static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { GameAssemblies.RepoRoot, "VintageHorizons", "src" }.Concat(parts).ToArray()));

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
        string rendererSource = Source("Render", "LodTerrainRenderer.cs");
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
        string composite = Asset("shaders", "lodtaacomposite.fsh");
        c.True(resolve.Contains("bool ours = depth < before;", StringComparison.Ordinal),
            "the resolve blends the pixels cached terrain drew this frame");
        c.True(resolve.Contains("bool sky = !ours && before >= 1.0;", StringComparison.Ordinal),
            "0.4.58: and the sky beside them, so the terrain's edge against the sky is averaged too");
        c.False(resolve.Contains("storedDepth", StringComparison.Ordinal),
            "0.4.58: without a per-pixel depth test - the nudge swaps a tree top for the ground "
            + "behind it in about one distant pixel in five, and refusing their history made the "
            + "image jitter with the camera still");
        c.True(resolve.Contains("abs(texelFetch(history, previousTexel, 0).a) >= 1.0",
                StringComparison.Ordinal),
            "history is taken only from cached terrain or sky, never from vanilla terrain or a "
            + "creature, which would leave a trail behind anything that moves");
        c.True(resolve.Contains("vec3 stored = max(HistoryCatmullRom(previousUv, vec2(size)), vec3(0.0));",
                StringComparison.Ordinal),
            "0.4.59: history is re-sampled by Catmull-Rom; bilinear filtering softened it a little "
            + "every frame of a turn and that built up into a smear");
        c.True(resolve.Contains("vec3 boxLo = max(lo, mean - clipWidth * deviation);", StringComparison.Ordinal)
                && resolve.Contains("vec3 boxHi = min(hi, mean + clipWidth * deviation);", StringComparison.Ordinal),
            "0.4.59: the safety check uses the typical colours around a pixel, never wider than "
            + "those present; on snow the full range let nearly any history through");
        c.True(resolve.Contains("vec3 previous = mix(stored, ClipToBox(stored, boxLo, boxHi), moving);",
                StringComparison.Ordinal),
            "the check holds history while anything moves and lets it settle once still");
        c.True(resolve.Contains("float moving = max(1.0 - stillness,", StringComparison.Ordinal),
            "0.4.59: and still means the whole camera, eased in, not just this pixel's ground");
        c.True(resolve.Contains("float share = mix(stillFeedback, feedback, moving)", StringComparison.Ordinal)
                && resolve.Contains("* clamp(2.0 * ramp, 0.0, 1.0);", StringComparison.Ordinal),
            "0.4.58: history reaches full share over the first half of the ramp, before the nudge");
        c.False(resolve.Contains("sharpenLobe", StringComparison.Ordinal),
            "0.4.59: sharpening never reaches the history, so it cannot build up frame on frame");
        c.True(composite.Contains("vec3 lobe = -amount * (sharpenLobe * ramp);", StringComparison.Ordinal),
            "the composite sharpens, adaptively and only as far as the ramp reaches");
        c.True(composite.Contains("float ramp = abs(value.a) - 1.0;", StringComparison.Ordinal)
                && composite.Contains("if (ramp <= 0.0) discard;", StringComparison.Ordinal),
            "the composite leaves vanilla pixels and everything outside the ramp exactly as drawn");

        // Every uniform and sampler the two passes are handed must be declared by their shader,
        // for the same reason as the frame uploads above.
        string taa = Source("Render", "LodTerrainRenderer.Taa.cs");
        foreach ((string pass, string shader) in new[] { ("resolve", resolve), ("composite", composite) })
        {
            string[] names = System.Text.RegularExpressions.Regex
                .Matches(taa, pass + @"\.(?:Uniform|UniformMatrix|BindTexture2D)\(""(?<name>\w+)""")
                .Select(match => match.Groups["name"].Value)
                .Distinct()
                .ToArray();
            string[] missing = names
                .Where(name => !System.Text.RegularExpressions.Regex.IsMatch(shader,
                    @"uniform\s+\w+\s+" + name + @"\s*;"))
                .ToArray();
            c.True(names.Length > 0 && missing.Length == 0,
                $"every uniform the {pass} pass is handed is declared by its shader"
                + (missing.Length > 0 ? " (missing: " + string.Join(", ", missing) + ")" : ""));
        }
    }

    static void RendererWiring(Check c)
    {
        string renderer = Source("Render", "LodTerrainRenderer.cs");
        string taa = Source("Render", "LodTerrainRenderer.Taa.cs");
        string mod = Source("VintageHorizonsModSystem.cs");

        int begin = renderer.IndexOf("BeginTaaFrame();", StringComparison.Ordinal);
        int pyramid = begin < 0 ? -1
            : renderer.IndexOf("BuildDepthPyramid();", begin, StringComparison.Ordinal);
        int water = renderer.IndexOf("renderPaths.DrawWater();", StringComparison.Ordinal);
        int resolve = renderer.IndexOf("ResolveTaa();", StringComparison.Ordinal);
        c.True(begin >= 0 && pyramid > begin,
            "the before-terrain depth snapshot is taken before a single cached draw");
        c.True(water >= 0 && resolve > water,
            "and the resolve runs after thin cover, which the terrain program also nudged");
        c.True(renderer.Contains(
                "prog.Uniform(\"vhTaaStartPixels\", LodTaaMath.NudgeStartPixels(taaReachPixels));",
                StringComparison.Ordinal)
            && renderer.Contains(
                "prog.Uniform(\"vhTaaFullPixels\", LodTaaMath.FullPixels(taaReachPixels));",
                StringComparison.Ordinal)
            && taa.Contains("resolve.Uniform(\"startPixels\", taaReachPixels);", StringComparison.Ordinal)
            && taa.Contains("resolve.Uniform(\"fullPixels\", LodTaaMath.FullPixels(taaReachPixels));",
                StringComparison.Ordinal),
            "the nudge and the blend are both derived from the one reach setting");
        c.True(taa.Contains("taaJitterX = 2f * x * taaNudge / taaWidth;", StringComparison.Ordinal)
                && taa.Contains("taaJitterY = 2f * y * taaNudge / taaHeight;", StringComparison.Ordinal),
            "the nudge dial scales how far terrain moves each frame");
        c.True(taa.Contains("taaStillFrames = historyUsable && TaaCameraUnchanged(view)",
                StringComparison.Ordinal),
            "stillness is counted from the whole camera, frame by frame");
        c.True(taa.Contains("GL.ColorMask(true, true, true, false);", StringComparison.Ordinal),
            "the composite never writes alpha");
        c.True(renderer.Contains("UpdateAutoThresholds();", StringComparison.Ordinal),
            "automatic thresholds are re-evaluated every prepared frame");
        c.True(mod.Contains("if (!renderer.AutoLodThresholds)", StringComparison.Ordinal),
            "saving with automatic thresholds on keeps the player's own distances in the file");
        foreach ((string saved, string live) in new[]
        {
            ("TaaReachPixels", "TaaReachPixels"), ("TaaBlend", "TaaFeedback"),
            ("TaaStillBlend", "TaaStillFeedback"), ("TaaNudge", "TaaNudge"),
            ("TaaSharpen", "TaaSharpen"), ("TaaClip", "TaaClip"),
        })
        {
            c.True(mod.Contains($"renderer.{live} = config.{saved};", StringComparison.Ordinal)
                    && mod.Contains($"config.{saved} = renderer.{live};", StringComparison.Ordinal),
                $"{saved} is restored at startup and saved with everything else");
        }
    }

    /// <summary>0.4.60: the settings window's Anti-Aliasing tab, asked for to tune by eye.</summary>
    static void SettingsWindow(Check c)
    {
        // Each slider spans exactly its dial's range and every default lands on one of its
        // steps, so opening the window and saving without touching anything changes nothing.
        var expected = new (string Key, float Min, float Max, float Default)[]
        {
            ("taa-reach", LodTaaMath.MinReachPixels, LodTaaMath.MaxReachPixels, LodTaaMath.DefaultReachPixels),
            ("taa-nudge", 0f, 1f, LodTaaMath.DefaultNudge),
            ("taa-sharpen", 0f, 1f, LodTaaMath.DefaultSharpen),
            ("taa-blend", 0f, LodTaaMath.MaxFeedback, LodTaaMath.DefaultFeedback),
            ("taa-still", 0f, LodTaaMath.MaxStillFeedback, LodTaaMath.DefaultStillFeedback),
            ("taa-clip", LodTaaMath.MinClip, LodTaaMath.MaxClip, LodTaaMath.DefaultClip),
        };
        c.Eq(expected.Length, LodTaaSliders.All.Length, "the Anti-Aliasing tab has one slider per dial");
        LodTaaDials defaults = LodTaaDials.Default;
        foreach ((string key, float min, float max, float fallback) in expected)
        {
            LodTaaSliders.Row row = Array.Find(LodTaaSliders.All, r => r.Key == key);
            bool found = row.Key == key;
            c.True(found && Math.Abs(row.FromSlider(row.Min) - min) < 1e-5f
                    && Math.Abs(row.FromSlider(row.Max) - max) < 1e-5f,
                $"{key} spans exactly its dial's range");
            int slot = found ? row.ToSlider(fallback) : -1;
            c.True(found && (slot - row.Min) % row.Step == 0
                    && Math.Abs(row.FromSlider(slot) - fallback) < 1e-5f
                    && Math.Abs(row.Read(defaults) - fallback) < 1e-6f,
                $"{key}'s default lands on one of its steps");

            float moved = found ? row.FromSlider(row.Min + row.Step) : 0f;
            LodTaaDials written = found ? row.Write(defaults, moved) : defaults;
            bool othersKept = LodTaaSliders.All.Where(other => other.Key != key)
                .All(other => other.Read(written) == other.Read(defaults));
            c.True(found && Math.Abs(row.Read(written) - moved) < 1e-6f && othersKept,
                $"{key} moves its own dial and no other");
        }

        string dialog = Source("Gui", "VintageHorizonsConfigDialog.cs");
        string mod = Source("VintageHorizonsModSystem.cs");
        c.True(dialog.Contains("taa.Preview(pendingTaa, pendingShowTaa);", StringComparison.Ordinal),
            "a slider shows its effect at once, so it can be judged by eye");
        c.True(dialog.Contains("if (!saved) taa.Preview(taa.Initial, taa.ShowInitial);",
                StringComparison.Ordinal),
            "and Cancel, or closing the window, puts back what was in force when it opened");
        c.True(dialog.Contains(
                "save(pendingThresholds, pendingAutomatic, pendingFarCap, pendingVisuals, pendingTaa);",
                StringComparison.Ordinal),
            "Save keeps them with every other setting");
        c.True(mod.Contains("renderer.TaaDials = taaDials;", StringComparison.Ordinal)
                && mod.Contains("WriteTaaDials(taaDials);", StringComparison.Ordinal),
            "and they reach the renderer, or the file while another LOD mod is drawing");
    }
}
