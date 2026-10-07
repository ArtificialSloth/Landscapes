namespace VintageHorizons.Checks;

public static class WaterDepthRepairChecks
{
    // CPU reference for the shader contract. Runtime GLSL remains a human integration check.
    static float Repair(float[,] input, int x, int y)
    {
        if (input[y, x] < 1f) return input[y, x];
        for (int ry = 1; ry <= 2; ry++)
            for (int rx = 1; rx <= 2; rx++)
            {
                if (x < rx || y < ry || x + rx >= input.GetLength(1) || y + ry >= input.GetLength(0)) continue;
                float l = input[y, x - rx], r = input[y, x + rx];
                float u = input[y - ry, x], d = input[y + ry, x];
                if (Math.Max(Math.Max(l, r), Math.Max(u, d)) >= 1f) continue;
                float horizontal = (l + r) * .5f, vertical = (u + d) * .5f;
                if (Math.Abs(horizontal - vertical) > 2f / 16777215f) continue;
                return Math.Max(horizontal, vertical);
            }
        return input[y, x];
    }

    static float[,] Plane()
    {
        var p = new float[9, 9];
        for (int y = 0; y < 9; y++)
            for (int x = 0; x < 9; x++) p[y, x] = .9995f + y * .0000056f;
        return p;
    }

    public static void Run(Check c)
    {
        // Exact centre and neighbours around the blip at screen (1058,540), from the
        // owner's 0.4.14 capture 20260905-151117-425-825d9469, combined-depth image.
        float[,] observed = {
            { .9994706f, .9994706f, .9994706f, .99947053f },
            { .9994649f, .9994649f, 1f, .9994649f },
            { .9994593f, .9994593f, .9994593f, .9994593f },
            { .9994537f, .9994537f, .9994537f, .9994537f }
        };
        c.True(Repair(observed, 2, 1) < 1f, "the recorded isolated no-water texel gets a supported depth");
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                if (observed[y, x] < 1f)
                    c.Eq(observed[y, x], Repair(observed, x, y), "recorded real water depths stay bit-identical");

        var p = Plane();
        float expected = p[4, 4];
        p[4, 4] = p[4, 5] = 1f;
        c.True(Math.Abs(Repair(p, 4, 4) - expected) < 2f / 16777215f,
            "a two-texel hole reconstructs the surrounding sloped plane");
        c.Eq(1f, p[4, 4], "repair reads an immutable snapshot rather than growing from prior fills");

        p = Plane();
        for (int y = 0; y < 9; y++)
            for (int x = 4; x < 9; x++) p[y, x] = 1f;
        c.Eq(1f, Repair(p, 4, 4), "a shoreline with water only on one side stays empty");
        c.Eq(1f, Repair(p, 8, 8), "image borders cannot wrap into water on the other side");

        p = Plane();
        for (int y = 1; y < 8; y++)
            for (int x = 1; x < 8; x++) p[y, x] = 1f;
        c.Eq(1f, Repair(p, 4, 4), "broad missing water areas are not filled");

        p = Plane();
        p[4, 4] = 1f;
        p[4, 3] = p[4, 5] = p[4, 2] = p[4, 6] = .998f;
        c.Eq(1f, Repair(p, 4, 4), "surrounding surfaces at inconsistent depths cannot bridge a hole");

        string root = GameAssemblies.RepoRoot;
        string shader = File.ReadAllText(Path.Combine(root, "VintageHorizons/assets/dodenslandscapes/shaders/lodwaterrepair.fsh"));
        c.True(shader.Contains("if (texelFetch(sourceDepth, p, 0).r < 1.0) discard;")
            && shader.Contains("ry <= 2") && shader.Contains("rx <= 2")
            && shader.Contains("abs(horizontal - vertical) > 2.0 / 16777215.0")
            && shader.Contains("gl_FragDepth = max(horizontal, vertical);"),
            "GPU repair retains the tested clear-only, bounded, two-axis plane contract");
        string pass = File.ReadAllText(Path.Combine(root, "VintageHorizons/src/Render/LodTerrainRenderer.WaterRepair.cs"));
        c.True(pass.Contains("GL.BlitFramebuffer(")
            && pass.Contains("waterRepairProg.BindTexture2D(\"sourceDepth\", waterRepairTexture, 0);")
            && pass.Contains("GL.BindFramebuffer(FramebufferTarget.Framebuffer, target.FboId);")
            && !pass.Contains("GL.Clear("),
            "repair samples a separate snapshot and never clears the engine depth image");
    }
}
