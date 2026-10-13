using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VintageHorizons;

public partial class LodTerrainRenderer
{
    public bool WaterDepthRepair { get; set; } = true;
    IShaderProgram? waterRepairProg;
    bool waterRepairShaderOk;
    bool waterRepairFailed;
    int waterRepairTexture;
    int waterRepairFramebuffer;
    int waterVanillaDepthTexture;
    int waterVanillaDepthFramebuffer;
    int waterRepairVao;
    int waterRepairWidth;
    int waterRepairHeight;
    int waterRepairFormat;

    void LoadWaterRepairShader()
    {
        waterRepairProg = capi.Shader.NewShaderProgram();
        waterRepairProg.AssetDomain = "dodenslandscapes";
        waterRepairProg.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        waterRepairProg.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
        capi.Shader.RegisterFileShaderProgram("lodwaterrepair", waterRepairProg);
        waterRepairShaderOk = waterRepairProg.Compile();
        waterRepairFailed = false;
        if (!waterRepairShaderOk)
            capi.Logger.Error(
                "[Landscapes] requested water-depth repair shader unavailable; cached "
                + "water will be suppressed while repair is enabled.");
    }

    // Runs inside the water-depth pass's framebuffer, texture, VAO and pipeline guard.
    // Read an immutable copy: repaired pixels can never become evidence for more repairs.
    void RepairWaterDepth(FrameBufferRef target)
    {
        if (!WaterDepthRepair || !waterRepairShaderOk || waterRepairFailed
            || waterRepairProg == null || waterRepairProg.LoadError) return;
        try
        {
            SnapshotWaterDepth(target, vanillaOnly: false);
            GL.DepthFunc(DepthFunction.Always);
            waterRepairProg.Use();
            waterRepairProg.BindTexture2D("sourceDepth", waterRepairTexture, 0);
            GL.BindVertexArray(waterRepairVao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }
        catch (Exception e)
        {
            waterRepairFailed = true;
            throw new InvalidOperationException("water-depth repair draw failed", e);
        }
        finally
        {
            if (ShaderProgramBase.CurrentShaderProgram == waterRepairProg) waterRepairProg.Stop();
        }
    }

    // Keep the vanilla-only and combined snapshots separate. The first remains live until
    // OIT so surface ownership can make the same decision as the earlier depth pass; the
    // second is the immutable input to the depth repair shader. Neither is sampled attached.
    void SnapshotWaterDepth(FrameBufferRef target, bool vanillaOnly)
    {
        ErrorCode incoming = GL.GetError();
        if (incoming != ErrorCode.NoError)
            throw new InvalidOperationException("GL error already present before water-depth snapshot: " + incoming);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, target.DepthTextureId);
        GL.GetTexLevelParameter(TextureTarget.Texture2D, 0,
            GetTextureParameter.TextureInternalFormat, out int format);
        if (waterRepairTexture == 0 || waterRepairWidth != target.Width
            || waterRepairHeight != target.Height || waterRepairFormat != format)
        {
            DisposeWaterRepairResources();
            waterRepairTexture = GL.GenTexture();
            waterRepairFramebuffer = GL.GenFramebuffer();
            waterVanillaDepthTexture = GL.GenTexture();
            waterVanillaDepthFramebuffer = GL.GenFramebuffer();
            waterRepairVao = GL.GenVertexArray();
            AllocateWaterDepthSnapshot(target, format, waterRepairTexture, waterRepairFramebuffer,
                "water repair");
            AllocateWaterDepthSnapshot(target, format, waterVanillaDepthTexture,
                waterVanillaDepthFramebuffer, "vanilla water");
            waterRepairWidth = target.Width;
            waterRepairHeight = target.Height;
            waterRepairFormat = format;
        }
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, target.FboId);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer,
            vanillaOnly ? waterVanillaDepthFramebuffer : waterRepairFramebuffer);
        GL.BlitFramebuffer(0, 0, target.Width, target.Height, 0, 0, target.Width, target.Height,
            ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, target.FboId);
        ErrorCode copied = GL.GetError();
        if (copied != ErrorCode.NoError)
            throw new InvalidOperationException("water-depth snapshot copy failed: " + copied);
    }

    static void AllocateWaterDepthSnapshot(FrameBufferRef target, int format, int texture,
        int framebuffer, string purpose)
    {
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, (PixelInternalFormat)format,
            target.Width, target.Height, 0, PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode,
            (int)TextureCompareMode.None);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, texture, 0);
        GL.DrawBuffer(DrawBufferMode.None);
        GL.ReadBuffer(ReadBufferMode.None);
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException(purpose + " snapshot framebuffer incomplete");
    }

    void DisposeWaterRepairResources()
    {
        if (waterRepairFramebuffer != 0) GL.DeleteFramebuffer(waterRepairFramebuffer);
        if (waterRepairTexture != 0) GL.DeleteTexture(waterRepairTexture);
        if (waterVanillaDepthFramebuffer != 0) GL.DeleteFramebuffer(waterVanillaDepthFramebuffer);
        if (waterVanillaDepthTexture != 0) GL.DeleteTexture(waterVanillaDepthTexture);
        if (waterRepairVao != 0) GL.DeleteVertexArray(waterRepairVao);
        waterRepairFramebuffer = waterRepairTexture = waterRepairVao = 0;
        waterVanillaDepthFramebuffer = waterVanillaDepthTexture = 0;
    }
}
