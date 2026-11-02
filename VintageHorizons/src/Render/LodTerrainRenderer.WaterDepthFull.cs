using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VintageHorizons;

/// <summary>
/// Cached water's share of the engine's quarter-resolution liquid depth, decided at full
/// resolution.
///
/// <para>
/// Drawn straight into the quarter image, each texel took cached water from its centre sample
/// alone, so water narrower than a texel - a pond, a ragged shore - was in or out by where it
/// happened to land. Two captures a one-pixel turn apart (2026-11-02) measured about 870 texels
/// a frame claiming water over a block that was mostly dry, and 750 mostly-water blocks with no
/// depth; only 98 of the first kind survived the turn. Everything that darkens through water
/// reads that image blurred, so each became a soft square switching on and off: the sky, the
/// clouds, cached ground's murk and Farseer.
/// </para>
/// <para>
/// So cached water is drawn at the primary framebuffer's size into an image of our own, and each
/// quarter texel takes it only where at least half of the pixels it covers are water. Vanilla's
/// own water in the image is never touched: the resolve composes under the same depth test as
/// the old direct draw.
/// </para>
/// </summary>
public partial class LodTerrainRenderer
{
    /// <summary>`.dlwater majority`: full-resolution coverage resolved by majority. Off draws the
    /// quarter image directly, as every build before 0.5.2-dev.25 did.</summary>
    public bool WaterDepthMajority { get; set; } = true;

    IShaderProgram? waterResolveProg;
    bool waterResolveShaderOk;
    bool waterResolveFailed;
    int waterFullDepthTexture;
    int waterFullDepthFramebuffer;
    int waterResolveVao;
    int waterFullDepthWidth;
    int waterFullDepthHeight;

    void LoadWaterResolveShader()
    {
        waterResolveProg = capi.Shader.NewShaderProgram();
        waterResolveProg.AssetDomain = "dodenslandscapes";
        waterResolveProg.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        waterResolveProg.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
        capi.Shader.RegisterFileShaderProgram("lodwaterresolve", waterResolveProg);
        waterResolveShaderOk = waterResolveProg.Compile();
        waterResolveFailed = false;
        if (!waterResolveShaderOk)
            capi.Logger.Error(
                "[Landscapes] water-depth majority shader unavailable; cached water will be "
                + "suppressed while majority coverage is on (.dlwater majority off draws the old way).");
    }

    /// <summary>
    /// Binds a cleared full-resolution depth target for the cached water draw and returns the
    /// factor that takes one of its pixels to the quarter texel holding it.
    /// </summary>
    Vec2f BindFullWaterDepth(FrameBufferRef quarter)
    {
        var buffers = capi.Render.FrameBuffers;
        var primary = buffers[(int)EnumFrameBuffer.Primary];
        if (primary == null || primary.Width <= 0 || primary.Height <= 0)
            throw new InvalidOperationException("primary framebuffer unavailable for full-resolution water depth");
        if (waterFullDepthTexture == 0 || waterFullDepthWidth != primary.Width
            || waterFullDepthHeight != primary.Height)
        {
            DisposeWaterFullDepthResources();
            waterFullDepthTexture = GL.GenTexture();
            waterFullDepthFramebuffer = GL.GenFramebuffer();
            waterResolveVao = GL.GenVertexArray();
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, waterFullDepthTexture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent32f,
                primary.Width, primary.Height, 0, PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode,
                (int)TextureCompareMode.None);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, waterFullDepthFramebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                TextureTarget.Texture2D, waterFullDepthTexture, 0);
            GL.DrawBuffer(DrawBufferMode.None);
            GL.ReadBuffer(ReadBufferMode.None);
            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
                throw new InvalidOperationException("full-resolution water depth framebuffer incomplete");
            waterFullDepthWidth = primary.Width;
            waterFullDepthHeight = primary.Height;
        }
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, waterFullDepthFramebuffer);
        GL.Viewport(0, 0, waterFullDepthWidth, waterFullDepthHeight);
        GL.DepthMask(true);
        GL.ClearDepth(1.0);
        GL.Clear(ClearBufferMask.DepthBufferBit);
        return new Vec2f(quarter.Width / (float)waterFullDepthWidth,
            quarter.Height / (float)waterFullDepthHeight);
    }

    // Runs inside the water-depth pass's framebuffer, texture, VAO and pipeline guard, with its
    // depth test (Less) and depth writes still set: vanilla water already in the image stays.
    void ResolveWaterDepthMajority(FrameBufferRef quarter)
    {
        if (!waterResolveShaderOk || waterResolveFailed || waterResolveProg == null
            || waterResolveProg.LoadError)
            throw new InvalidOperationException("water-depth majority shader unavailable");
        try
        {
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, quarter.FboId);
            GL.Viewport(0, 0, quarter.Width, quarter.Height);
            waterResolveProg.Use();
            waterResolveProg.BindTexture2D("cachedWaterDepth", waterFullDepthTexture, 0);
            waterResolveProg.Uniform("blockSize", new Vec2f(
                waterFullDepthWidth / (float)quarter.Width, waterFullDepthHeight / (float)quarter.Height));
            GL.BindVertexArray(waterResolveVao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }
        catch (Exception e)
        {
            waterResolveFailed = true;
            throw new InvalidOperationException("water-depth majority resolve failed", e);
        }
        finally
        {
            if (ShaderProgramBase.CurrentShaderProgram == waterResolveProg) waterResolveProg.Stop();
        }
    }

    void DisposeWaterFullDepthResources()
    {
        if (waterFullDepthFramebuffer != 0) GL.DeleteFramebuffer(waterFullDepthFramebuffer);
        if (waterFullDepthTexture != 0) GL.DeleteTexture(waterFullDepthTexture);
        if (waterResolveVao != 0) GL.DeleteVertexArray(waterResolveVao);
        waterFullDepthFramebuffer = waterFullDepthTexture = waterResolveVao = 0;
        waterFullDepthWidth = waterFullDepthHeight = 0;
    }
}
