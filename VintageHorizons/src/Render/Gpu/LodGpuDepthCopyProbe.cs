using OpenTK.Graphics.OpenGL4;

namespace VintageHorizons;

internal readonly record struct LodGpuDepthCopyDimensions(
    int FrameWidth,
    int FrameHeight,
    int ViewportX,
    int ViewportY,
    int ViewportWidth,
    int ViewportHeight,
    int TextureWidth,
    int TextureHeight)
{
    public bool ViewportMatchesFrame =>
        ViewportX == 0 && ViewportY == 0
        && ViewportWidth == FrameWidth && ViewportHeight == FrameHeight;

    public string Describe() =>
        $"frame {FrameWidth}x{FrameHeight}, viewport {ViewportX},{ViewportY} "
        + $"{ViewportWidth}x{ViewportHeight}, depth texture {TextureWidth}x{TextureHeight}";
}

internal readonly record struct LodGpuDepthCopySizeDecision(
    bool Succeeded,
    bool Retryable,
    string FailureReason);

internal static class LodGpuFrameSizePolicy
{
    public static (int Width, int Height) Select(
        int apiFrameWidth,
        int apiFrameHeight,
        int primaryFramebufferWidth,
        int primaryFramebufferHeight) =>
        primaryFramebufferWidth > 0 && primaryFramebufferHeight > 0
            ? (primaryFramebufferWidth, primaryFramebufferHeight)
            : (apiFrameWidth, apiFrameHeight);
}

internal readonly record struct LodGpuDepthCopyValidation(
    bool CopyValidated,
    bool MipAllocationValidated,
    bool StateRestored,
    int Width,
    int Height,
    int MipLevels,
    int InternalFormat,
    string FailureReason,
    string Advisory = "",
    LodGpuDepthCopyDimensions Dimensions = default,
    bool Retryable = false)
{
    public bool Succeeded => CopyValidated && MipAllocationValidated && StateRestored;
}

/// <summary>
/// One-time disposable proof that the active single-sample depth texture can be blitted
/// into a private, same-format texture with a complete mip allocation. It does not retain
/// the texture, build a conservative HZB, or influence drawing.
/// </summary>
internal static class LodGpuDepthCopyProbe
{
    // Sized depth formats accepted by TexStorage2D. Depth/stencil sources are deliberately
    // deferred until their attachment and copy semantics receive a dedicated probe.
    const int DepthComponent16 = 0x81A5;
    const int DepthComponent24 = 0x81A6;
    const int DepthComponent32 = 0x81A7;
    const int DepthComponent32F = 0x8CAC;

    public static LodGpuDepthCopyValidation Probe(
        LodGpuDepthFacts depth,
        int frameWidth,
        int frameHeight)
    {
        var stateApi = new LodOpenGlStateApi();
        LodGlStateSnapshot state = default;
        bool stateCaptured = false;
        int privateTexture = 0;
        int privateFramebuffer = 0;
        int width = 0;
        int height = 0;
        int levels = 0;
        int internalFormat = 0;
        string advisory = "";
        LodGpuDepthCopyDimensions dimensions = new(
            frameWidth, frameHeight, 0, 0, 0, 0, 0, 0);
        var result = Failure("private depth-copy probe did not run", dimensions: dimensions);

        try
        {
            if (!LodGlErrorIsolation.TryEstablishBaseline(
                    () => (int)GL.GetError(), out advisory, out string baselineFailure))
                throw new InvalidOperationException(baselineFailure);

            int[] viewport = new int[4];
            GL.GetInteger(GetPName.Viewport, viewport);
            dimensions = dimensions with
            {
                ViewportX = viewport[0],
                ViewportY = viewport[1],
                ViewportWidth = viewport[2],
                ViewportHeight = viewport[3],
            };

            if (frameWidth <= 0 || frameHeight <= 0)
            {
                LodGpuDepthCopySizeDecision frameDecision = EvaluateCopySize(dimensions);
                result = Failure(
                    frameDecision.FailureReason,
                    advisory: advisory,
                    dimensions: dimensions,
                    retryable: frameDecision.Retryable);
                return result;
            }

            if (!depth.AttachmentPresent)
                return Failure("the active depth attachment was not observed",
                    advisory: advisory, dimensions: dimensions);
            if (!depth.AttachmentType.Equals("Texture", StringComparison.OrdinalIgnoreCase))
                return Failure("the active depth attachment is not texture-backed",
                    advisory: advisory, dimensions: dimensions);
            if (depth.Samples != 1)
                return Failure("multisample depth resolve is not validated by this probe",
                    advisory: advisory, dimensions: dimensions);

            state = LodGlStateGuard.Capture(
                stateApi,
                LodGlStateMask.Framebuffers | LodGlStateMask.Texture2DUnit0);
            stateCaptured = true;
            GL.ActiveTexture(TextureUnit.Texture0);

            width = frameWidth;
            height = frameHeight;

            GL.BindTexture(TextureTarget.Texture2D, depth.AttachmentName);
            GL.GetTexLevelParameter(
                TextureTarget.Texture2D, 0, GetTextureParameter.TextureWidth, out int sourceWidth);
            GL.GetTexLevelParameter(
                TextureTarget.Texture2D, 0, GetTextureParameter.TextureHeight, out int sourceHeight);
            GL.GetTexLevelParameter(
                TextureTarget.Texture2D, 0, GetTextureParameter.TextureInternalFormat,
                out internalFormat);
            dimensions = dimensions with
            {
                TextureWidth = sourceWidth,
                TextureHeight = sourceHeight,
            };
            LodGpuDepthCopySizeDecision copyDecision = EvaluateCopySize(dimensions);
            if (!copyDecision.Succeeded)
            {
                result = Failure(
                    copyDecision.FailureReason,
                    width,
                    height,
                    levels,
                    internalFormat,
                    advisory,
                    dimensions,
                    copyDecision.Retryable);
            }
            else if (!IsSupportedDepthFormat(internalFormat))
            {
                throw new InvalidOperationException(
                    $"depth internal format 0x{internalFormat:X} is not yet validated");
            }
            else
            {
                levels = CalculateMipLevels(width, height);
                privateTexture = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, privateTexture);
                GL.TexStorage2D(
                    TextureTarget2d.Texture2D,
                    levels,
                    (SizedInternalFormat)internalFormat,
                    width,
                    height);
                GL.TexParameter(
                    TextureTarget.Texture2D,
                    TextureParameterName.TextureBaseLevel,
                    0);
                GL.TexParameter(
                    TextureTarget.Texture2D,
                    TextureParameterName.TextureMaxLevel,
                    levels - 1);

                GL.GetTexLevelParameter(
                    TextureTarget.Texture2D,
                    levels - 1,
                    GetTextureParameter.TextureWidth,
                    out int finalWidth);
                GL.GetTexLevelParameter(
                    TextureTarget.Texture2D,
                    levels - 1,
                    GetTextureParameter.TextureHeight,
                    out int finalHeight);
                if (finalWidth != 1 || finalHeight != 1)
                {
                    throw new InvalidOperationException(
                        $"private mip chain ended at {finalWidth}x{finalHeight}, expected 1x1");
                }

                privateFramebuffer = GL.GenFramebuffer();
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, privateFramebuffer);
                GL.FramebufferTexture2D(
                    FramebufferTarget.Framebuffer,
                    FramebufferAttachment.DepthAttachment,
                    TextureTarget.Texture2D,
                    privateTexture,
                    0);
                // A new FBO initially targets colour attachment zero. A depth-only FBO must
                // explicitly disable those FBO-local read/draw colour buffers before its
                // completeness status is meaningful.
                GL.DrawBuffer(DrawBufferMode.None);
                GL.ReadBuffer(ReadBufferMode.None);
                FramebufferErrorCode framebufferStatus =
                    GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
                if (framebufferStatus != FramebufferErrorCode.FramebufferComplete)
                {
                    throw new InvalidOperationException(
                        "private depth framebuffer is incomplete: " + framebufferStatus);
                }

                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, depth.DrawFramebuffer);
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, privateFramebuffer);
                GL.BlitFramebuffer(
                    0,
                    0,
                    width,
                    height,
                    0,
                    0,
                    width,
                    height,
                    ClearBufferMask.DepthBufferBit,
                    BlitFramebufferFilter.Nearest);

                ErrorCode error = GL.GetError();
                if (error != ErrorCode.NoError)
                    throw new InvalidOperationException("private depth copy raised " + error);

                result = new LodGpuDepthCopyValidation(
                    CopyValidated: true,
                    MipAllocationValidated: true,
                    StateRestored: false,
                    width,
                    height,
                    levels,
                    internalFormat,
                    FailureReason: "",
                    Advisory: advisory,
                    Dimensions: dimensions);
            }
        }
        catch (Exception e)
        {
            result = Failure("private depth-copy probe failed: " + e.Message,
                width, height, levels, internalFormat, advisory, dimensions);
        }
        finally
        {
            string restoreFailure = "";
            bool restored = !stateCaptured || LodGlStateGuard.TryRestore(
                stateApi, state, out restoreFailure);
            if (!restored)
            {
                result = Failure("private depth-copy state restore failed: " + restoreFailure,
                    width, height, levels, internalFormat, advisory, dimensions);
            }
            else if (result.CopyValidated && result.MipAllocationValidated)
            {
                result = result with { StateRestored = true };
            }

            try { if (privateFramebuffer != 0) GL.DeleteFramebuffer(privateFramebuffer); }
            catch { /* Context teardown must continue. */ }
            try { if (privateTexture != 0) GL.DeleteTexture(privateTexture); }
            catch { /* Context teardown must continue. */ }
        }

        return result;
    }

    internal static LodGpuDepthCopySizeDecision EvaluateCopySize(
        LodGpuDepthCopyDimensions dimensions)
    {
        if (dimensions.FrameWidth <= 0 || dimensions.FrameHeight <= 0)
        {
            return new(false, true,
                "the rendered frame has no area (" + dimensions.Describe() + ")");
        }

        if (dimensions.TextureWidth < dimensions.FrameWidth
            || dimensions.TextureHeight < dimensions.FrameHeight)
        {
            return new(false, true,
                "the depth texture does not contain the rendered frame ("
                + dimensions.Describe() + ")");
        }

        return new(true, false, "");
    }

    internal static int CalculateMipLevels(int width, int height)
    {
        if (width <= 0 || height <= 0) return 0;
        int largest = Math.Max(width, height);
        int levels = 1;
        while (largest > 1)
        {
            largest >>= 1;
            levels++;
        }
        return levels;
    }

    internal static bool IsSupportedDepthFormat(int internalFormat) =>
        internalFormat is DepthComponent16
            or DepthComponent24
            or DepthComponent32
            or DepthComponent32F;

    static LodGpuDepthCopyValidation Failure(
        string reason,
        int width = 0,
        int height = 0,
        int levels = 0,
        int internalFormat = 0,
        string advisory = "",
        LodGpuDepthCopyDimensions dimensions = default,
        bool retryable = false) =>
        new(false, false, false, width, height, levels, internalFormat, reason, advisory,
            dimensions, retryable);
}
