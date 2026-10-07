using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

namespace VintageHorizons;

public partial class LodTerrainRenderer
{
    bool waterCaptureRequested;
    string? waterCaptureDirectory;
    BeforeStageRenderer? waterCaptureEndRenderer;

    public void RequestWaterCapture() => waterCaptureRequested = true;

    void BeginWaterCapture()
    {
        if (waterCaptureDirectory != null)
        {
            capi.Logger.Warning("[Landscapes] water capture incomplete: AfterOIT did not finish the previous frame.");
            waterCaptureDirectory = null;
        }
        if (!waterCaptureRequested) return;
        waterCaptureRequested = false;
        try
        {
            string root = capi.GetOrCreateDataPath("Doden's Landscapes/water-captures");
            waterCaptureDirectory = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")
                + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(waterCaptureDirectory);
            var state = new
            {
                version = typeof(LodTerrainRenderer).Assembly.GetName().Version?.ToString(),
                frameCounter, WaterGroundMurk, WaterDepthCoverage, WaterSurfaceVisible, WaterDepthRepair,
                WaterSurfaceTexture, WaterSurfaceTextureStatus,
                WaterSurfaceTextureSlot, WaterSurfaceTintSlot,
                WaterSeamStripBlocks, waterDepthShaderOk, waterDepthFailed, WaterJunctionDepth,
                WaterJunctionSurfaceStatus,
                DedicatedWaterShader, WaterShaderStatus,
                WaterOitEnabled, WaterPipelineStatus,
                waterFresnel = WaterFresnel, waterGlint, waterFoam,
                WaterMaxAlpha, WaterClarity, waterDepthShading,
                vanillaViewDistance = ApprovedViewDistance(), EffectiveFarDistance,
                camera = new[] { camPos.X, camPos.Y, camPos.Z },
                projection = capi.Render.CurrentProjectionMatrix,
                view = capi.Render.CameraMatrixOriginf,
                waterWaveCounter = capi.Render.ShaderUniforms.WaterWaveCounter,
                waterStillCounter = capi.Render.ShaderUniforms.WaterStillCounter,
                windWaveCounter = capi.Render.ShaderUniforms.WindWaveCounter,
                waterWaveIntensity = capi.Render.ShaderUniforms.WaterWaveIntensity,
                windWaveIntensity = capi.Render.ShaderUniforms.WindWaveIntensity,
                format = "f32 files: little-endian float32, bottom row first; depth has one channel, colour RGBA. PPM previews: top row first, RGB, linear colour clamped to 0..1. Each image has its own JSON dimensions."
            };
            File.WriteAllText(Path.Combine(waterCaptureDirectory, "frame.json"),
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { FailWaterCapture(e); }
    }

    void CaptureWaterDepthStage(float dt)
    {
        CaptureWaterImage("01-vanilla-liquid-depth", EnumFrameBuffer.LiquidDepth, depth: true);
        DrawWaterDepthBefore(dt);
        CaptureWaterImage("02-combined-liquid-depth", EnumFrameBuffer.LiquidDepth, depth: true);
    }

    void FinishWaterCapture(float dt)
    {
        if (waterCaptureDirectory == null) return;
        CaptureWaterImage("06-after-water-composition", EnumFrameBuffer.Primary, depth: false);
        if (waterCaptureDirectory == null) return;
        try
        {
            File.WriteAllText(Path.Combine(waterCaptureDirectory, "result.json"),
                JsonSerializer.Serialize(new { frameCounter, WaterDepthDraws, WaterDepthSkipReason,
                    WaterJunctionStatus, WaterJunctionSurfaceStatus, waterJunctionFailed,
                    WaterShaderStatus, waterSurfaceFailed, WaterPipelineStatus,
                    WaterOitDraws, WaterOitSkipReason, waterOitFailed },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { FailWaterCapture(e); return; }
        capi.Logger.Notification("[Landscapes] water capture saved: {0}; cached depth draws: {1}",
            waterCaptureDirectory, WaterDepthDraws);
        waterCaptureDirectory = null;
    }

    void FailWaterCapture(Exception e)
    {
        capi.Logger.Warning("[Landscapes] water capture failed: {0}", e.Message);
        waterCaptureDirectory = null;
    }

    void CaptureWaterImage(string name, EnumFrameBuffer buffer, bool depth)
    {
        if (waterCaptureDirectory == null) return;
        try
        {
            FrameBufferRef target = capi.Render.FrameBuffers[(int)buffer];
            int texture = depth ? target.DepthTextureId : target.ColorTextureIds[0];
            int channels = depth ? 1 : 4;
            CaptureWaterTexture(name, target, texture, channels,
                depth ? PixelFormat.DepthComponent : PixelFormat.Rgba,
                preview: !depth);
        }
        catch (Exception e) { FailWaterCapture(e); }
    }

    /// <summary>
    /// Capture the ordinary OIT revealage product around our draw. With one liquid layer,
    /// one minus the first image is vanilla's alpha. Dividing the second revealage by the
    /// first recovers cached water's alpha even where another transparent layer exists.
    /// </summary>
    void CaptureWaterOitRevealage(string name, FrameBufferRef target)
    {
        if (waterCaptureDirectory == null) return;
        try
        {
            if (target.ColorTextureIds == null || target.ColorTextureIds.Length < 2)
                throw new InvalidOperationException("OIT revealage attachment is unavailable.");
            CaptureWaterTexture(name, target, target.ColorTextureIds[1], 1,
                PixelFormat.Red, preview: false);
        }
        catch (Exception e) { FailWaterCapture(e); }
    }

    void CaptureWaterTexture(string name, FrameBufferRef target, int texture, int channels,
        PixelFormat format, bool preview)
    {
        if (waterCaptureDirectory == null) return;
        try
        {
            int count = checked(target.Width * target.Height * channels);
            // Explicit command only. Bound the allocation even with an unusual framebuffer.
            if (texture == 0 || count <= 0 || count > 64 * 1024 * 1024)
                throw new InvalidOperationException("Water capture framebuffer is unavailable or too large.");
            float[] pixels = new float[count];
            int active = GL.GetInteger(GetPName.ActiveTexture);
            int packBuffer = GL.GetInteger(GetPName.PixelPackBufferBinding);
            GetPName[] getPack = [GetPName.PackAlignment, GetPName.PackRowLength,
                GetPName.PackSkipRows, GetPName.PackSkipPixels, GetPName.PackSwapBytes];
            PixelStoreParameter[] setPack = [PixelStoreParameter.PackAlignment,
                PixelStoreParameter.PackRowLength, PixelStoreParameter.PackSkipRows,
                PixelStoreParameter.PackSkipPixels, PixelStoreParameter.PackSwapBytes];
            int[] previousPack = getPack.Select(p => GL.GetInteger(p)).ToArray();
            GL.ActiveTexture(TextureUnit.Texture0);
            int previousTexture = GL.GetInteger(GetPName.TextureBinding2D);
            int compareMode;
            try
            {
                GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
                for (int i = 0; i < setPack.Length; i++) GL.PixelStore(setPack[i], i == 0 ? 4 : 0);
                GL.BindTexture(TextureTarget.Texture2D, texture);
                GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureWidth, out int width);
                GL.GetTexLevelParameter(TextureTarget.Texture2D, 0, GetTextureParameter.TextureHeight, out int height);
                if (width != target.Width || height != target.Height)
                    throw new InvalidOperationException("Water capture texture dimensions disagree with its framebuffer.");
                GL.GetTexParameter(TextureTarget.Texture2D, GetTextureParameter.TextureCompareMode, out compareMode);
                GL.GetTexImage(TextureTarget.Texture2D, 0, format, PixelType.Float, pixels);
            }
            finally
            {
                GL.BindTexture(TextureTarget.Texture2D, previousTexture);
                GL.ActiveTexture((TextureUnit)active);
                for (int i = 0; i < setPack.Length; i++) GL.PixelStore(setPack[i], previousPack[i]);
                GL.BindBuffer(BufferTarget.PixelPackBuffer, packBuffer);
            }
            string path = Path.Combine(waterCaptureDirectory, name);
            using (var file = new FileStream(path + ".f32", FileMode.CreateNew))
                file.Write(MemoryMarshal.AsBytes(pixels.AsSpan()));
            File.WriteAllText(path + ".json", JsonSerializer.Serialize(new
            {
                target.Width, target.Height, texture, channels, compareMode,
                WaterDepthDraws, min = pixels.Min(), max = pixels.Max(), nonFinite = pixels.Count(p => !float.IsFinite(p))
            }, new JsonSerializerOptions
            {
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            }));
            if (preview) WaterCapturePreview.Write(path + ".ppm", pixels, target.Width, target.Height);
        }
        catch (Exception e) { FailWaterCapture(e); }
    }
}

internal static class WaterCapturePreview
{
    internal static void Write(string path, float[] rgba, int width, int height)
    {
        using var file = new FileStream(path, FileMode.CreateNew);
        file.Write(Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
        byte[] row = new byte[checked(width * 3)];
        for (int y = height - 1; y >= 0; y--)
        {
            for (int x = 0; x < width; x++)
                for (int c = 0; c < 3; c++)
                    row[x * 3 + c] = (byte)Math.Clamp(rgba[(y * width + x) * 4 + c] * 255f, 0f, 255f);
            file.Write(row);
        }
    }
}
