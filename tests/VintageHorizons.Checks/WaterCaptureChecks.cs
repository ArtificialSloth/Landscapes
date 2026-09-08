using System.Text;

namespace VintageHorizons.Checks;

public static class WaterCaptureChecks
{
    public static void Run(Check c)
    {
        string path = Path.Combine(Path.GetTempPath(), "vh-water-preview-" + Guid.NewGuid().ToString("N") + ".ppm");
        try
        {
            WaterCapturePreview.Write(path,
                [1, 0, 0, 0, 0, 1, 0, 1, 0, 0, 1, 1, 2, -1, 0.5f, 1], 2, 2);
            byte[] output = File.ReadAllBytes(path);
            byte[] header = Encoding.ASCII.GetBytes("P6\n2 2\n255\n");
            c.True(output.AsSpan(0, header.Length).SequenceEqual(header), "water preview records its dimensions and binary RGB format");
            byte[] expected = [0, 0, 255, 255, 0, 127, 255, 0, 0, 0, 255, 0];
            c.True(output.AsSpan(header.Length).SequenceEqual(expected),
                "water preview flips GPU rows, drops alpha and clamps HDR values without changing colour channels");
            bool refused = false;
            try { WaterCapturePreview.Write(path, [0, 0, 0, 0], 1, 1); }
            catch (IOException) { refused = true; }
            c.True(refused, "a diagnostic never overwrites an existing capture");
        }
        finally { File.Delete(path); }

        string root = GameAssemblies.RepoRoot;
        string renderer = File.ReadAllText(Path.Combine(root, "VintageHorizons/src/Render/LodTerrainRenderer.cs"));
        c.True(renderer.IndexOf("02b-before-cached-ground-depth") < renderer.IndexOf("renderPaths.DrawOpaque();")
            && renderer.IndexOf("02c-before-cached-ground-color") < renderer.IndexOf("renderPaths.DrawOpaque();")
            && renderer.IndexOf("renderPaths.DrawOpaque();") < renderer.IndexOf("04-before-cached-water")
            && renderer.IndexOf("04-before-cached-water") < renderer.IndexOf("renderPaths.DrawWater();")
            && renderer.IndexOf("renderPaths.DrawWater();") < renderer.IndexOf("05-after-cached-water"),
            "capture separates vanilla ground, cached ground and cached water within the same rendered frame");
        string capture = File.ReadAllText(Path.Combine(root, "VintageHorizons/src/Render/LodTerrainRenderer.WaterCapture.cs"));
        c.True(capture.Contains("if (waterCaptureDirectory == null) return;")
            && capture.Contains("if (!waterCaptureRequested) return;")
            && capture.Contains("waterCaptureRequested = false;"),
            "readback and file IO require an explicit single-frame capture request");
        string oit = File.ReadAllText(Path.Combine(root,
            "VintageHorizons/src/Render/LodTerrainRenderer.WaterOit.cs"));
        c.True(capture.Contains("target.ColorTextureIds[1]", StringComparison.Ordinal)
            && capture.Contains("PixelFormat.Red", StringComparison.Ordinal)
            && oit.IndexOf("05a-vanilla-oit-revealage", StringComparison.Ordinal)
                < oit.IndexOf("foreach (long key in drawList)", StringComparison.Ordinal)
            && oit.IndexOf("foreach (long key in drawList)", StringComparison.Ordinal)
                < oit.IndexOf("05b-combined-oit-revealage", StringComparison.Ordinal),
            "a requested OIT capture brackets cached water with single-channel revealage readbacks");
    }
}
