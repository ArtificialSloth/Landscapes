using System.Text.RegularExpressions;

namespace VintageHorizons.Checks;

/// <summary>
/// `.dlsky`: the word layout the shader and the renderer share, the rule that separates a flash
/// from sky that is always there, the ray a report is walked along, and the mask reading it
/// prints. Also that every vertex reads the climate field, now the retired snow-line comparison is gone.
/// </summary>
public static class SkyWitnessChecks
{
    public static void Run(Check c)
    {
        TheShaderWritesTheLayoutTheRendererReads(c);
        ClearedSlotsStartWithAnEmptyBox(c);
        AFlashIsReportedAndSteadySkyStopsBeing(c);
        TheBusiestTilesComeFirst(c);
        RaysLeaveTheCameraWhereItLooks(c);
        GroundOwnershipReadsTheMaskAsTheShaderDoes(c);
        EveryVertexReadsTheClimateField(c);
    }

    static string Asset(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { GameAssemblies.RepoRoot, "VintageHorizons", "assets", "dodenslandscapes" }
            .Concat(parts).ToArray()));

    static void TheShaderWritesTheLayoutTheRendererReads(Check c)
    {
        string fsh = Asset("shaders", "lodsky.fsh");
        string vsh = Asset("shaders", "lodsky.vsh");

        c.True(fsh.Contains($"binding = {SkyWitnessLayout.Binding})"),
            "the shader's buffer binding is the one the renderer binds");
        c.True(fsh.Contains("layout(early_fragment_tests) in;"),
            "the depth test runs before the shader, so covered pixels never reach the counters");
        c.True(!Regex.IsMatch(fsh, @"^\s*out\s", RegexOptions.Multiline),
            "the witness declares no colour output");
        c.True(Regex.Matches(fsh, @"\bdiscard;").Count >= 3,
            "every path through the witness discards, so the picture is never touched");
        c.True(!fsh.Contains("return;"), "no path leaves the shader without discarding");

        int[] fixedWords = Regex.Matches(fsh, @"skyWords\[(\d+)\]")
            .Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(i => i).ToArray();
        c.SeqEq(new[]
            {
                SkyWitnessLayout.Total, SkyWitnessLayout.BeyondReach, SkyWitnessLayout.MinX,
                SkyWitnessLayout.MinY, SkyWitnessLayout.MaxX, SkyWitnessLayout.MaxY,
            },
            fixedWords, "the shader's fixed words are the layout's, in the same places");
        c.True(fsh.Contains($"skyWords[{SkyWitnessLayout.FirstTile} + ty * tilesX + tx]"),
            "the tile grid starts where the layout says, row by row");
        c.Eq(SkyWitnessLayout.FirstTile + SkyWitnessLayout.TilesX * SkyWitnessLayout.TilesY,
            SkyWitnessLayout.WordCount, "the buffer holds the header and one word per tile");
        c.True(vsh.Contains("gl_Position = vec4(p, 1.0, 1.0);"),
            "the triangle sits on the far plane, so LEQUAL passes only where nothing was drawn");
    }

    static void ClearedSlotsStartWithAnEmptyBox(Check c)
    {
        uint[] words = SkyWitnessLayout.ClearWords();
        c.Eq(SkyWitnessLayout.WordCount, words.Length, "a cleared slot is the whole buffer");
        c.Eq(uint.MaxValue, words[SkyWitnessLayout.MinX], "the box's left edge starts above any pixel");
        c.Eq(uint.MaxValue, words[SkyWitnessLayout.MinY], "and so does its bottom edge");
        c.Eq(0u, words[SkyWitnessLayout.Total], "no pixels counted");
        c.Eq(0u, words[SkyWitnessLayout.MaxX], "the box's right edge starts at zero");
        c.True(words.Skip(SkyWitnessLayout.FirstTile).All(w => w == 0), "every tile starts empty");
    }

    static void AFlashIsReportedAndSteadySkyStopsBeing(Check c)
    {
        var baseline = new SkyWitnessBaseline();
        int flashes = 0;
        for (int i = 0; i < 200; i++)
        {
            if (baseline.IsFlash(0)) flashes++;
        }
        c.Eq(0, flashes, "ground everywhere is never a flash");
        c.True(baseline.IsFlash(3000), "a chunk-sized patch of sky in one frame is a flash");
        c.True(!baseline.IsFlash(10), "the next frame without it is not");
        c.True(!baseline.IsFlash(SkyWitnessBaseline.MinimumPixels - 1), "a few stray pixels are not");

        // Sky that stays - an unfinished edge of the cache - must stop being reported, or it
        // would bury the flashes the log exists for.
        int reported = 0;
        int lastReported = -1;
        for (int i = 0; i < 400; i++)
        {
            if (!baseline.IsFlash(1500)) continue;
            reported++;
            lastReported = i;
        }
        c.True(reported > 0, "steady sky is reported when it first appears");
        c.True(lastReported < 60, $"and stops being reported within a few dozen frames (last at {lastReported})");
        c.True(baseline.IsFlash(6000), "a flash on top of steady sky is still a flash");
    }

    static void TheBusiestTilesComeFirst(Check c)
    {
        uint[] words = SkyWitnessLayout.ClearWords();
        words[SkyWitnessLayout.FirstTile + 3 * SkyWitnessLayout.TilesX + 5] = 40;
        words[SkyWitnessLayout.FirstTile + 0 * SkyWitnessLayout.TilesX + 1] = 900;
        words[SkyWitnessLayout.FirstTile + 8 * SkyWitnessLayout.TilesX + 15] = 120;

        List<SkyTile> top = SkyWitnessReport.TopTiles(words, 2);
        c.Eq(2, top.Count, "as many tiles as asked for");
        c.Eq(new SkyTile(1, 0, 900), top[0], "the busiest tile first, with its column and row");
        c.Eq(new SkyTile(15, 8, 120), top[1], "then the next");
        c.Eq(0, SkyWitnessReport.TopTiles(SkyWitnessLayout.ClearWords(), 2).Count,
            "a frame with no sky names no tiles");
    }

    static void RaysLeaveTheCameraWhereItLooks(Check c)
    {
        // A plain OpenGL perspective looking down -Z with the eye at the origin, which is what
        // the camera-relative terrain matrices are once the view rotation is identity.
        const int width = 1920;
        const int height = 1080;
        double f = 1.0 / Math.Tan(70.0 * Math.PI / 180.0 / 2.0);
        const float near = 0.1f;
        const float far = 1000f;
        var projection = new float[16];
        projection[0] = (float)(f / (width / (double)height));
        projection[5] = (float)f;
        projection[10] = (far + near) / (near - far);
        projection[11] = -1f;
        projection[14] = 2f * far * near / (near - far);
        float[]? inverse = Vintagestory.API.MathTools.Mat4f.Invert(new float[16], projection);
        c.True(inverse != null, "the projection inverts");
        if (inverse == null) return;

        c.True(SkyWitnessReport.TryRay(inverse, width / 2.0, height / 2.0, width, height,
            out double cx, out double cy, out double cz), "a ray through the centre");
        c.True(Math.Abs(cx) < 1e-3 && Math.Abs(cy) < 1e-3 && cz < -0.999,
            $"looks straight ahead ({cx:0.000}, {cy:0.000}, {cz:0.000})");

        SkyWitnessReport.TryRay(inverse, width / 2.0, 0, width, height, out _, out double by, out _);
        c.True(by < -0.5, $"the bottom row looks down, as gl_FragCoord's y = 0 is the bottom ({by:0.000})");

        SkyWitnessReport.TryRay(inverse, 0, height / 2.0, width, height, out double lx, out _, out _);
        c.True(lx < -0.5, $"the left edge looks left ({lx:0.000})");

        c.True(!SkyWitnessReport.TryRay(new float[16], 10, 10, width, height, out _, out _, out _),
            "a degenerate matrix gives no ray rather than a wrong one");
    }

    static void GroundOwnershipReadsTheMaskAsTheShaderDoes(Check c)
    {
        var mask = new VanillaReadinessMask(8, 4);
        mask.Set(new VanillaChunkCell(10, 2, 20), true);
        mask.Set(new VanillaChunkCell(11, 1, 20), true);
        mask.Set(new VanillaChunkCell(11, 1, 20), false);
        int[] texels = (int[])mask.Texels.Clone();
        var window = new SkyMaskWindow(true, 8, 18, 6, 6, mask.Width, mask.VerticalChunks);

        c.True(SkyWitnessReport.GroundOwned(texels, window, 10, 2, 20),
            "a ready cell hands its ground to vanilla");
        c.True(!SkyWitnessReport.GroundOwned(texels, window, 11, 1, 20),
            "a cell that lost ground but kept water leaves the ground with the cache");
        c.True(!SkyWitnessReport.GroundOwned(texels, window, 10, 1, 20),
            "another layer of the same column is not owned");
        c.True(!SkyWitnessReport.GroundOwned(texels, window with { MinX = 11 }, 10, 2, 20),
            "outside the window the answer is no, as in the shader");
        c.True(!SkyWitnessReport.GroundOwned(texels, window with { Enabled = false }, 10, 2, 20),
            "with the mask off nothing is vanilla's");
        c.True(!SkyWitnessReport.GroundOwned(texels, window with { Width = 20 }, 18, 2, 20)
                == !SkyWitnessReport.GroundOwned(texels, window with { Width = 20 }, 10, 2, 20),
            "the ring aliases: a cell one capacity away shares the texel, and only the window "
            + "keeps the two apart");
        c.True(!SkyWitnessReport.GroundOwned(texels, window, 18, 2, 20),
            "which is why the aliased cell outside the window reads no");
        c.Eq("2", SkyWitnessReport.OwnedLayers(texels, window, 10, 20), "the column's vanilla layers");
        c.Eq("none", SkyWitnessReport.OwnedLayers(texels, window, 11, 20),
            "a water-only column lists no ground layers");
    }

    /// <summary>
    /// Every vertex reads the climate field, which carries the snow line. The `.vhsnowline`
    /// comparison that could gate that lookup or fall back to one player-local threshold was
    /// retired in 0.4.70; its uniforms must leave the shader and the renderer's upload together,
    /// because the engine throws on an uploaded name no program declared (G174).
    /// </summary>
    static void EveryVertexReadsTheClimateField(Check c)
    {
        string inputs = Asset("shaderincludes", "lodvertexinputs.vsh");
        string surface = Asset("shaderincludes", "lodvertexsurface.vsh");
        string renderer = File.ReadAllText(Path.Combine(
            GameAssemblies.RepoRoot, "VintageHorizons", "src", "Render", "LodTerrainRenderer.cs"));

        c.True(surface.Contains("if (climateFieldEnabled == 1)"),
            "the climate field lookup is gated only by the field being enabled");
        c.True(surface.Contains("snowLineAtVertex = climateSnowMinY"),
            "and the snow line always comes from it");
        foreach (string retired in new[] { "climateFetchTintOnly", "snowLineUniformOnly" })
        {
            c.False(inputs.Contains(retired) || surface.Contains(retired),
                $"the shader no longer declares or reads {retired}");
            c.False(renderer.Contains($"\"{retired}\""), $"the renderer no longer uploads {retired}");
        }
    }
}
