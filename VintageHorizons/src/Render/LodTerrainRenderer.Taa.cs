using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VintageHorizons;

/// <summary>
/// Distant anti-aliasing, and the automatic LOD thresholds that share its screen arithmetic.
///
/// <para>
/// The shimmer: past about a thousand blocks one rendered pixel covers several blocks but can
/// show only the one under its centre, so a camera movement smaller than a pixel swaps which
/// block that is. Snow is the worst case - white tops between bare walls, and a coarse level's
/// snow is a yes/no vote - so the pixel flips between white and dark. Measured from the owner's
/// capture on 2026-09-11: past 512 blocks, 79-89% of snow pixels were one row tall. At 20
/// degrees FOV, which makes every block three times bigger on screen while depth precision
/// barely moves, the owner saw it greatly diminish, which is what ruled out depth fighting.
/// </para>
/// <para>
/// The fix is temporal. The terrain shader nudges distant geometry a different fraction of a
/// pixel every frame (lodterrainbody.vsh), and after cached terrain has drawn, each of its pixels
/// is blended with where the same ground was in earlier frames. Over a few frames a pixel settles
/// on the average of what it covers instead of on whichever block happened to be under it.
/// </para>
/// <para>
/// Once the whole camera has held still for a few frames, a pixel's history lands exactly on
/// itself, so it is allowed to settle: the safety check is let go and the memory rises to the
/// still setting. Otherwise history may only say what the current frame's 3x3 neighbourhood
/// could also say, which is what stops it smearing. Sharpening is applied on the way to the
/// screen, never to the memory. Every trade-off is a saved `.dltaa` dial; how each came to be is
/// told in lodtaaresolve.fsh.
/// </para>
/// <para>
/// Nothing here is a distance. Which pixels are touched is decided by which pass drew them - a
/// depth snapshot is taken before cached terrain draws, and a pixel is ours if its depth moved
/// nearer - and how strongly by how many pixels one block spans there, from the live projection
/// and render resolution, against the player's reach setting. Vanilla terrain, water and cached
/// ground nearer than the reach keep the exact pixels they were drawn with, at any vanilla view
/// distance, FOV or resolution scale; of the sky, only the pixels right beside cached terrain are
/// blended, to average the edge between the two.
/// </para>
/// </summary>
public partial class LodTerrainRenderer
{
    // ---- Settings -------------------------------------------------------------------------

    /// <summary>Saved. The window's "Distant Anti-Aliasing" row and .dltaa write it.</summary>
    public bool DistantAntiAliasing
    {
        get => distantAntiAliasing;
        set
        {
            if (distantAntiAliasing == value) return;
            distantAntiAliasing = value;
            taaHistoryValid = false;
        }
    }
    bool distantAntiAliasing = true;

    /// <summary>Session diagnostic: tint every anti-aliased pixel magenta by its strength.</summary>
    public bool TaaShowWeight { get; set; }

    // The tuning dials. All saved and all set by .dltaa; LodTaaMath says what each one trades.

    /// <summary>Memory while the view turns. `.dltaa blend`.</summary>
    public float TaaFeedback
    {
        get => taaFeedback;
        set => taaFeedback = LodTaaMath.ClampDial(value, LodTaaMath.DefaultFeedback,
            0f, LodTaaMath.MaxFeedback);
    }
    float taaFeedback = LodTaaMath.DefaultFeedback;

    /// <summary>Memory once the whole camera has held still. `.dltaa still`.</summary>
    public float TaaStillFeedback
    {
        get => taaStillFeedback;
        set => taaStillFeedback = LodTaaMath.ClampDial(value, LodTaaMath.DefaultStillFeedback,
            0f, LodTaaMath.MaxStillFeedback);
    }
    float taaStillFeedback = LodTaaMath.DefaultStillFeedback;

    /// <summary>How far distant terrain is nudged, as a share of half a pixel. `.dltaa nudge`.</summary>
    public float TaaNudge
    {
        get => taaNudge;
        set => taaNudge = LodTaaMath.ClampDial(value, LodTaaMath.DefaultNudge, 0f, 1f);
    }
    float taaNudge = LodTaaMath.DefaultNudge;

    /// <summary>Sharpening on the way to the screen, 0 to 1. `.dltaa sharpen`.</summary>
    public float TaaSharpen
    {
        get => taaSharpen;
        set => taaSharpen = LodTaaMath.ClampDial(value, LodTaaMath.DefaultSharpen, 0f, 1f);
    }
    float taaSharpen = LodTaaMath.DefaultSharpen;

    /// <summary>Width of the safety check, in standard deviations. `.dltaa clip`.</summary>
    public float TaaClip
    {
        get => taaClip;
        set => taaClip = LodTaaMath.ClampDial(value, LodTaaMath.DefaultClip,
            LodTaaMath.MinClip, LodTaaMath.MaxClip);
    }
    float taaClip = LodTaaMath.DefaultClip;

    /// <summary>Every tuning dial, reach included, back to its default. The on/off switch stays.</summary>
    public void ResetTaaDials() => TaaDials = LodTaaDials.Default;

    /// <summary>Every tuning dial at once, for the settings window's live preview and revert.</summary>
    internal LodTaaDials TaaDials
    {
        get => new(taaReachPixels, taaNudge, taaSharpen, taaFeedback, taaStillFeedback, taaClip);
        set
        {
            TaaReachPixels = value.Reach;
            TaaNudge = value.Nudge;
            TaaSharpen = value.Sharpen;
            TaaFeedback = value.Blend;
            TaaStillFeedback = value.Still;
            TaaClip = value.Clip;
        }
    }

    /// <summary>
    /// Pixels one block spans at one block of view depth, from the last perspective frame; 0
    /// before there has been one. The settings window turns the reach into distances with it.
    /// </summary>
    public float ScreenBlockScale => LodScreenScale.BlockScale(screenRenderHeight, screenYScale);

    /// <summary>Resolved frames with the still memory fully in force, for the report.</summary>
    public long TaaFramesStill { get; private set; }

    /// <summary>Consecutive frames the whole camera has held still, up to the ease-in length.</summary>
    int taaStillFrames;

    /// <summary>
    /// Saved. Where anti-aliasing begins, as the on-screen width of one block in pixels; it is
    /// full strength at half that. Larger reaches closer. `.dltaa reach`.
    /// </summary>
    public float TaaReachPixels
    {
        get => taaReachPixels;
        set => taaReachPixels = LodTaaMath.ClampReach(value);
    }
    float taaReachPixels = LodTaaMath.DefaultReachPixels;

    /// <summary>Saved. Choose L1-L6 from the screen; see <see cref="LodAutoThresholds"/>.</summary>
    public bool AutoLodThresholds
    {
        get => autoLodThresholds;
        set
        {
            if (autoLodThresholds == value) return;
            autoLodThresholds = value;
            autoAppliedThresholds = null;
            autoInputsKey = default;
        }
    }
    bool autoLodThresholds;

    /// <summary>Saved. The on-screen column width, in pixels, every level hands over at.</summary>
    public float LodTargetPixels
    {
        get => lodTargetPixels;
        set
        {
            float clamped = Math.Clamp(
                float.IsFinite(value) ? value : LodAutoThresholds.DefaultTargetPixels,
                LodAutoThresholds.MinTargetPixels, LodAutoThresholds.MaxTargetPixels);
            if (clamped == lodTargetPixels) return;
            lodTargetPixels = clamped;
            autoInputsKey = default;
        }
    }
    float lodTargetPixels = LodAutoThresholds.DefaultTargetPixels;

    /// <summary>GPU time of the resolve and composite passes together.</summary>
    public LodPhaseCost GpuTaaCost => gpuTelemetry.TaaGpuCost;

    /// <summary>GPU time of the before-terrain depth snapshot.</summary>
    public LodPhaseCost GpuTaaCopyCost => gpuTelemetry.TaaCopyGpuCost;

    public long TaaFramesResolved { get; private set; }
    public long TaaHistoryResets { get; private set; }

    // ---- Programs and targets ---------------------------------------------------------------

    IShaderProgram? taaResolveProg;
    IShaderProgram? taaCompositeProg;
    bool taaShaderOk;

    /// <summary>Non-empty once the feature has stopped for this session, and why.</summary>
    string taaFailure = "";

    readonly LodOpenGlStateApi taaStateApi = new();
    int taaVao;
    int taaBeforeDepthTexture;
    int taaBeforeDepthFramebuffer;
    readonly int[] taaHistoryTexture = new int[2];
    readonly int[] taaHistoryFramebuffer = new int[2];
    int taaCompositeFramebuffer;
    int taaCompositeAttached;
    int taaWidth;
    int taaHeight;
    int taaSourceDepthTexture;

    /// <summary>
    /// Frames still checked for GL errors. Only the first frames after the targets are made:
    /// that is when a wrong format or an incomplete target shows, and reading an error consumes
    /// it, so checking forever would hide other code's errors from the engine's own report.
    /// </summary>
    int taaErrorChecksLeft;
    bool taaReportedPendingError;
    const int TaaErrorCheckFrames = 30;

    // ---- Per-frame state --------------------------------------------------------------------

    bool taaActiveThisFrame;
    float taaBlockScale;
    float taaJitterX;
    float taaJitterY;
    int taaCurrent;
    bool taaHistoryValid;
    long taaJitterFrame;
    long taaLastResolvedFrame = long.MinValue;
    long taaHistoryEpoch = long.MinValue;
    double taaPrevCamX;
    double taaPrevCamY;
    double taaPrevCamZ;
    string taaLastResetReason = "";
    readonly float[] taaPrevView = new float[16];
    readonly float[] taaPrevProjection = new float[16];
    readonly float[] taaInvProjection = new float[16];
    readonly float[] taaInvView = new float[16];
    readonly float[] taaPrevViewProjection = new float[16];
    readonly Vec3f taaCameraDelta = new();

    // Pipeline state the resolve changes, captured into preallocated storage: this runs every
    // frame and must not allocate (G3).
    readonly int[] taaViewport = new int[4];
    readonly bool[] taaColorMask = new bool[4];
    readonly int[] taaUnitBindings = new int[4];
    bool taaDepthTestWas;
    bool taaDepthMaskWas;
    bool taaBlendWas;
    bool taaCullWas;
    bool taaScissorWas;

    /// <summary>History older than this many prepared frames is not reused.</summary>
    const int TaaMaxFrameGap = 30;

    /// <summary>A camera step longer than this in one frame is a teleport, not a movement.</summary>
    const double TaaMaxCameraStep = 64.0;

    // ---- Screen scale and automatic thresholds -----------------------------------------------

    /// <summary>Last perspective projection seen: rendered rows and the projection's Y scale.</summary>
    int screenRenderHeight;
    float screenYScale;

    (int Height, float YScale, int Floor, float Target) autoInputsKey;
    int[]? autoAppliedThresholds;
    long autoLastApplyMs;
    const long AutoThresholdMinIntervalMs = 1000;

    /// <summary>What automatic thresholds last applied, for reports.</summary>
    public string AutoThresholdSummary { get; private set; } = "";

    void LoadTaaShaders()
    {
        taaResolveProg = NewTaaProgram("lodtaaresolve");
        taaCompositeProg = NewTaaProgram("lodtaacomposite");
        bool resolveOk = taaResolveProg.Compile();
        bool compositeOk = taaCompositeProg.Compile();
        taaShaderOk = resolveOk && compositeOk;

        // A reload is a fresh chance, as it is for the water repair: a failure on a
        // half-registered include table usually compiles on the reload that follows.
        taaFailure = "";
        taaHistoryValid = false;
        taaErrorChecksLeft = TaaErrorCheckFrames;
        if (!ShaderLoadIsProvisional && !taaShaderOk)
            capi.Logger.Warning(
                "[Landscapes] the distant anti-aliasing shaders did not compile; distant "
                + "terrain draws without it and nothing else changes.");
    }

    IShaderProgram NewTaaProgram(string name)
    {
        IShaderProgram program = capi.Shader.NewShaderProgram();
        program.AssetDomain = "dodenslandscapes";
        program.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        program.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
        capi.Shader.RegisterFileShaderProgram(name, program);
        return program;
    }

    FrameBufferRef? PrimaryFrameBuffer()
    {
        var buffers = capi.Render.FrameBuffers;
        if (buffers == null || buffers.Count() <= (int)EnumFrameBuffer.Primary) return null;
        FrameBufferRef? primary = buffers[(int)EnumFrameBuffer.Primary];
        if (primary == null || primary.FboId == 0 || primary.Width <= 0 || primary.Height <= 0
            || primary.DepthTextureId == 0 || primary.ColorTextureIds == null
            || primary.ColorTextureIds.Length == 0 || primary.ColorTextureIds[0] == 0) return null;
        return primary;
    }

    /// <summary>
    /// First thing in the opaque callback, before a single cached draw: decide whether this
    /// frame is anti-aliased, and if so freeze the depth buffer as it stands - vanilla's own
    /// terrain and nothing of ours - so the resolve can tell exactly which pixels we drew.
    /// </summary>
    void BeginTaaFrame()
    {
        taaActiveThisFrame = false;
        taaBlockScale = 0f;
        taaJitterX = taaJitterY = 0f;
        if (!distantAntiAliasing || !taaShaderOk || taaFailure.Length > 0
            || taaResolveProg == null || taaCompositeProg == null
            || taaResolveProg.LoadError || taaCompositeProg.LoadError)
        {
            taaHistoryValid = false;
            return;
        }

        FrameBufferRef? primary = PrimaryFrameBuffer();
        if (primary == null)
        {
            taaHistoryValid = false;
            return;
        }

        LodGlStateSnapshot state = LodGlStateGuard.Capture(taaStateApi,
            LodGlStateMask.Framebuffers | LodGlStateMask.Texture2DUnit0);
        bool scissor = GL.IsEnabled(EnableCap.ScissorTest);
        try
        {
            DrainPendingTaaErrors("before the anti-aliasing depth snapshot");
            EnsureTaaResources(primary);

            // A blit honours the scissor test and nothing else in the fragment pipeline.
            if (scissor) GL.Disable(EnableCap.ScissorTest);
            bool timed = gpuTelemetry.BeginTaaCopy();
            try
            {
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, primary.FboId);
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, taaBeforeDepthFramebuffer);
                GL.BlitFramebuffer(0, 0, taaWidth, taaHeight, 0, 0, taaWidth, taaHeight,
                    ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
            }
            finally
            {
                if (timed) gpuTelemetry.EndTaaCopy();
            }
            ThrowOnTaaError("the anti-aliasing depth snapshot");

            taaBlockScale = LodScreenScale.BlockScale(taaHeight, capi.Render.CurrentProjectionMatrix[5]);
            (float x, float y) = LodTaaMath.JitterPixels(taaJitterFrame);
            taaJitterX = 2f * x * taaNudge / taaWidth;
            taaJitterY = 2f * y * taaNudge / taaHeight;
            taaActiveThisFrame = taaBlockScale > 0f;
        }
        catch (Exception e)
        {
            StopTaa("the depth snapshot failed: " + e.Message);
        }
        finally
        {
            if (scissor) GL.Enable(EnableCap.ScissorTest);
            if (!LodGlStateGuard.TryRestore(taaStateApi, state, out string restoreFailure))
                StopTaa("state restore after the depth snapshot failed: " + restoreFailure);
        }
    }

    /// <summary>
    /// Last thing in the opaque callback, after cached terrain and thin cover have drawn.
    /// Blends into the private history, then writes the result back over our own distant pixels.
    /// </summary>
    void ResolveTaa()
    {
        if (!taaActiveThisFrame) return;
        taaActiveThisFrame = false;

        FrameBufferRef? primary = PrimaryFrameBuffer();
        if (primary == null || primary.Width != taaWidth || primary.Height != taaHeight
            || primary.DepthTextureId != taaSourceDepthTexture)
        {
            taaHistoryValid = false;
            return;
        }

        float[] projection = capi.Render.CurrentProjectionMatrix;
        float[] view = capi.Render.CameraMatrixOriginf;
        if (Mat4f.Invert(taaInvProjection, projection) == null
            || Mat4f.Invert(taaInvView, view) == null)
        {
            // A degenerate matrix for one frame. Nothing to blend against; try again next frame.
            taaHistoryValid = false;
            return;
        }

        bool historyUsable = TaaHistoryUsable(projection);
        if (historyUsable)
            Mat4f.Multiply(taaPrevViewProjection, taaPrevProjection, taaPrevView);

        // The still memory waits until the whole camera has held still for a few frames: not
        // while walking, which barely moves distant ground on screen, and not the instant a turn
        // stops, while the history still carries the turn's blur.
        taaStillFrames = historyUsable && TaaCameraUnchanged(view)
            ? Math.Min(taaStillFrames + 1, LodTaaMath.StillEaseFrames) : 0;
        float stillness = LodTaaMath.Stillness(taaStillFrames);
        taaCameraDelta.X = (float)(camPos.X - taaPrevCamX);
        taaCameraDelta.Y = (float)(camPos.Y - taaPrevCamY);
        taaCameraDelta.Z = (float)(camPos.Z - taaPrevCamZ);

        int previous = taaCurrent;
        int next = taaCurrent ^ 1;

        LodGlStateSnapshot state = LodGlStateGuard.Capture(taaStateApi,
            LodGlStateMask.Program | LodGlStateMask.Framebuffers | LodGlStateMask.Texture2DUnit0
            | LodGlStateMask.VertexArray);
        IShaderProgram? incoming = ShaderProgramBase.CurrentShaderProgram;
        CaptureTaaPipeline();
        bool resolved = false;
        try
        {
            DrainPendingTaaErrors("before distant anti-aliasing");

            // Use() and Stop() are a pair: the engine keeps its own record of the program in
            // use, which the state guard knows nothing about (see LodGpuDepthPyramid).
            incoming?.Stop();
            bool timed = gpuTelemetry.BeginTaa();
            try
            {
                GL.Disable(EnableCap.DepthTest);
                GL.DepthMask(false);
                GL.Disable(EnableCap.Blend);
                GL.Disable(EnableCap.CullFace);
                GL.Disable(EnableCap.ScissorTest);
                GL.ColorMask(true, true, true, true);
                GL.Viewport(0, 0, taaWidth, taaHeight);
                GL.BindVertexArray(taaVao);

                // Into the private history. The scene's colour and depth are read here, so the
                // draw target has to be one they are not attached to.
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, taaHistoryFramebuffer[next]);
                IShaderProgram resolve = taaResolveProg!;
                resolve.Use();
                try
                {
                    resolve.BindTexture2D("sceneColor", primary.ColorTextureIds[0], 0);
                    resolve.BindTexture2D("sceneDepth", primary.DepthTextureId, 1);
                    resolve.BindTexture2D("beforeDepth", taaBeforeDepthTexture, 2);
                    resolve.BindTexture2D("history", taaHistoryTexture[previous], 3);
                    resolve.UniformMatrix("invProjection", taaInvProjection);
                    resolve.UniformMatrix("invView", taaInvView);
                    resolve.UniformMatrix("prevViewProjection", taaPrevViewProjection);
                    resolve.Uniform("cameraDelta", taaCameraDelta);
                    resolve.Uniform("projectionA", projection[10]);
                    resolve.Uniform("projectionB", projection[14]);
                    resolve.Uniform("blockScale", taaBlockScale);
                    resolve.Uniform("startPixels", taaReachPixels);
                    resolve.Uniform("fullPixels", LodTaaMath.FullPixels(taaReachPixels));
                    resolve.Uniform("feedback", taaFeedback);
                    resolve.Uniform("stillFeedback", taaStillFeedback);
                    resolve.Uniform("stillMotion", LodTaaMath.StillMotionPixels);
                    resolve.Uniform("movingMotion", LodTaaMath.MovingMotionPixels);
                    resolve.Uniform("fastMotion", LodTaaMath.FastMotionPixels);
                    resolve.Uniform("fastShare", LodTaaMath.FastShare);
                    resolve.Uniform("stillness", stillness);
                    resolve.Uniform("clipWidth", taaClip);
                    resolve.Uniform("historyValid", historyUsable ? 1 : 0);
                    GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                }
                finally
                {
                    resolve.Stop();
                }

                // Back over the scene - cached terrain inside the ramp and the sky pixels beside
                // it - through a target that holds only its colour texture, so the engine's own
                // framebuffer and its draw-buffer list are never touched. The composite reads
                // where to write from the resolve's alpha and needs no ramp of its own.
                AttachTaaComposite(primary);
                // Alpha stays exactly as the terrain wrote it.
                GL.ColorMask(true, true, true, false);
                IShaderProgram composite = taaCompositeProg!;
                composite.Use();
                try
                {
                    composite.BindTexture2D("resolved", taaHistoryTexture[next], 0);
                    composite.Uniform("sharpenLobe", taaSharpen * LodTaaMath.MaxSharpenLobe);
                    composite.Uniform("showWeight", TaaShowWeight ? 1 : 0);
                    GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                }
                finally
                {
                    composite.Stop();
                }
            }
            finally
            {
                if (timed) gpuTelemetry.EndTaa();
            }
            ThrowOnTaaError("distant anti-aliasing");
            resolved = true;
        }
        catch (Exception e)
        {
            StopTaa("the resolve failed: " + e.Message);
        }
        finally
        {
            RestoreTaaPipeline();
            if (!LodGlStateGuard.TryRestore(taaStateApi, state, out string restoreFailure))
                StopTaa("state restore after the resolve failed: " + restoreFailure);
            if (incoming != null && ShaderProgramBase.CurrentShaderProgram != incoming)
            {
                incoming.Use();
                // Use() rebinds the engine's samplers and can land on ours (G130).
                if (ReferenceEquals(incoming, prog)) RebindOwnSamplers(prog!);
            }
        }
        if (!resolved) return;

        taaCurrent = next;
        taaHistoryValid = true;
        taaLastResolvedFrame = frameCounter;
        taaHistoryEpoch = currentWorldEpoch();
        Array.Copy(projection, taaPrevProjection, 16);
        Array.Copy(view, taaPrevView, 16);
        taaPrevCamX = camPos.X;
        taaPrevCamY = camPos.Y;
        taaPrevCamZ = camPos.Z;
        taaJitterFrame++;
        TaaFramesResolved++;
        if (stillness >= 1f) TaaFramesStill++;
        if (taaErrorChecksLeft > 0) taaErrorChecksLeft--;
    }

    /// <summary>
    /// Whether the camera is where it was last frame and looking the same way. Tolerances, not
    /// equality, so float noise in an idle camera cannot keep it from ever counting as still.
    /// </summary>
    bool TaaCameraUnchanged(float[] view)
    {
        double dx = camPos.X - taaPrevCamX;
        double dy = camPos.Y - taaPrevCamY;
        double dz = camPos.Z - taaPrevCamZ;
        if (dx * dx + dy * dy + dz * dz > LodTaaMath.StillStepBlocks * LodTaaMath.StillStepBlocks)
            return false;
        for (int i = 0; i < 16; i++)
        {
            if (Math.Abs(view[i] - taaPrevView[i]) > LodTaaMath.StillTurn) return false;
        }
        return true;
    }

    /// <summary>Whether last frame's result can be blended in, and if not, why not.</summary>
    bool TaaHistoryUsable(float[] projection)
    {
        if (!taaHistoryValid) return false;

        string? reason = null;
        if (taaHistoryEpoch != currentWorldEpoch()) reason = "the world changed";
        else if (frameCounter - taaLastResolvedFrame > TaaMaxFrameGap) reason = "frames were skipped";
        else if (Math.Abs(projection[0] - taaPrevProjection[0]) > 1e-4f * Math.Abs(projection[0])
            || Math.Abs(projection[5] - taaPrevProjection[5]) > 1e-4f * Math.Abs(projection[5]))
            reason = "the field of view or window shape changed";
        else
        {
            double dx = camPos.X - taaPrevCamX;
            double dy = camPos.Y - taaPrevCamY;
            double dz = camPos.Z - taaPrevCamZ;
            if (dx * dx + dy * dy + dz * dz > TaaMaxCameraStep * TaaMaxCameraStep)
                reason = "the camera jumped";
        }
        if (reason == null) return true;

        taaHistoryValid = false;
        NoteTaaReset(reason);
        return false;
    }

    void NoteTaaReset(string reason)
    {
        TaaHistoryResets++;
        taaLastResetReason = reason;
    }

    void EnsureTaaResources(FrameBufferRef primary)
    {
        if (taaVao == 0) taaVao = GL.GenVertexArray();
        if (taaBeforeDepthTexture != 0 && taaWidth == primary.Width && taaHeight == primary.Height
            && taaSourceDepthTexture == primary.DepthTextureId) return;

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, primary.DepthTextureId);
        GL.GetTexLevelParameter(TextureTarget.Texture2D, 0,
            GetTextureParameter.TextureInternalFormat, out int format);

        DisposeTaaTargets();
        taaWidth = primary.Width;
        taaHeight = primary.Height;
        taaSourceDepthTexture = primary.DepthTextureId;

        // The same depth format as the scene's, or the blit is refused.
        taaBeforeDepthTexture = GL.GenTexture();
        taaBeforeDepthFramebuffer = GL.GenFramebuffer();
        AllocateWaterDepthSnapshot(primary, format, taaBeforeDepthTexture,
            taaBeforeDepthFramebuffer, "anti-aliasing");

        for (int i = 0; i < taaHistoryTexture.Length; i++)
        {
            taaHistoryTexture[i] = GL.GenTexture();
            taaHistoryFramebuffer[i] = GL.GenFramebuffer();
            AllocateTaaHistory(taaHistoryTexture[i], taaHistoryFramebuffer[i], taaWidth, taaHeight);
        }
        taaCompositeFramebuffer = GL.GenFramebuffer();
        taaCompositeAttached = 0;

        taaHistoryValid = false;
        taaErrorChecksLeft = TaaErrorCheckFrames;
        taaReportedPendingError = false;
        NoteTaaReset("the render target was (re)allocated");
    }

    /// <summary>
    /// Half-float colour, because the result is blended into itself every frame and eight bits
    /// would band. Alpha carries each pixel's kind and ramp, signed (lodtaaresolve.fsh): 1 + ramp
    /// on cached terrain, -(1 + ramp) on sky, 0 anywhere else. Filtered linearly for the history
    /// colour fetch, which lands between texels; the kind is read unfiltered.
    /// </summary>
    static void AllocateTaaHistory(int texture, int framebuffer, int width, int height)
    {
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba16f, width, height, 0,
            PixelFormat.Rgba, PixelType.HalfFloat, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, texture, 0);
        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
        FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException("anti-aliasing history framebuffer incomplete: " + status);
    }

    void AttachTaaComposite(FrameBufferRef primary)
    {
        int colour = primary.ColorTextureIds[0];
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, taaCompositeFramebuffer);
        // Re-attached every frame. The engine rebuilds its framebuffers on a settings change and
        // a texture name can come back for a different texture; an attachment is to the object,
        // not the name, so a stale one would draw into something nobody displays.
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, colour, 0);
        if (taaCompositeAttached == colour) return;

        GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
        GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
        FramebufferErrorCode status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != FramebufferErrorCode.FramebufferComplete)
            throw new InvalidOperationException("anti-aliasing composite framebuffer incomplete: " + status);
        taaCompositeAttached = colour;
    }

    void CaptureTaaPipeline()
    {
        taaDepthTestWas = GL.IsEnabled(EnableCap.DepthTest);
        GL.GetBoolean(GetPName.DepthWritemask, out taaDepthMaskWas);
        taaBlendWas = GL.IsEnabled(EnableCap.Blend);
        taaCullWas = GL.IsEnabled(EnableCap.CullFace);
        taaScissorWas = GL.IsEnabled(EnableCap.ScissorTest);
        GL.GetInteger(GetPName.Viewport, taaViewport);
        GL.GetBoolean(GetPName.ColorWritemask, taaColorMask);

        // Units 1-3 are ours for the resolve; unit 0 and the active unit belong to the guard.
        int active = GL.GetInteger(GetPName.ActiveTexture);
        for (int unit = 1; unit < taaUnitBindings.Length; unit++)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
            taaUnitBindings[unit] = GL.GetInteger(GetPName.TextureBinding2D);
        }
        GL.ActiveTexture((TextureUnit)active);
    }

    void RestoreTaaPipeline()
    {
        int active = GL.GetInteger(GetPName.ActiveTexture);
        for (int unit = 1; unit < taaUnitBindings.Length; unit++)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
            GL.BindTexture(TextureTarget.Texture2D, taaUnitBindings[unit]);
        }
        GL.ActiveTexture((TextureUnit)active);

        if (taaDepthTestWas) GL.Enable(EnableCap.DepthTest); else GL.Disable(EnableCap.DepthTest);
        GL.DepthMask(taaDepthMaskWas);
        if (taaBlendWas) GL.Enable(EnableCap.Blend); else GL.Disable(EnableCap.Blend);
        if (taaCullWas) GL.Enable(EnableCap.CullFace); else GL.Disable(EnableCap.CullFace);
        if (taaScissorWas) GL.Enable(EnableCap.ScissorTest); else GL.Disable(EnableCap.ScissorTest);
        GL.Viewport(taaViewport[0], taaViewport[1], taaViewport[2], taaViewport[3]);
        GL.ColorMask(taaColorMask[0], taaColorMask[1], taaColorMask[2], taaColorMask[3]);
    }

    void DrainPendingTaaErrors(string where)
    {
        if (taaErrorChecksLeft <= 0) return;
        for (int i = 0; i < 8; i++)
        {
            ErrorCode pending = GL.GetError();
            if (pending == ErrorCode.NoError) return;
            if (taaReportedPendingError) continue;
            taaReportedPendingError = true;
            capi.Logger.Warning(
                "[Landscapes] a GL error ({0}) was already pending {1}. It is not from "
                + "anti-aliasing; it is logged here because reading it cleared it.", pending, where);
        }
    }

    void ThrowOnTaaError(string what)
    {
        if (taaErrorChecksLeft <= 0) return;
        ErrorCode error = GL.GetError();
        if (error != ErrorCode.NoError)
            throw new InvalidOperationException(what + " raised " + error);
    }

    void StopTaa(string reason)
    {
        taaActiveThisFrame = false;
        taaHistoryValid = false;
        if (taaFailure.Length > 0) return;
        taaFailure = reason;
        capi.Logger.Error(
            "[Landscapes] distant anti-aliasing stopped for this session; distant terrain "
            + "draws exactly as it did without it. Cause: {0}", reason);
    }

    void DisposeTaaTargets()
    {
        if (taaBeforeDepthFramebuffer != 0) GL.DeleteFramebuffer(taaBeforeDepthFramebuffer);
        if (taaBeforeDepthTexture != 0) GL.DeleteTexture(taaBeforeDepthTexture);
        for (int i = 0; i < taaHistoryTexture.Length; i++)
        {
            if (taaHistoryFramebuffer[i] != 0) GL.DeleteFramebuffer(taaHistoryFramebuffer[i]);
            if (taaHistoryTexture[i] != 0) GL.DeleteTexture(taaHistoryTexture[i]);
            taaHistoryFramebuffer[i] = taaHistoryTexture[i] = 0;
        }
        if (taaCompositeFramebuffer != 0) GL.DeleteFramebuffer(taaCompositeFramebuffer);
        taaBeforeDepthFramebuffer = taaBeforeDepthTexture = taaCompositeFramebuffer = 0;
        taaCompositeAttached = 0;
        taaWidth = taaHeight = 0;
        taaSourceDepthTexture = 0;
        taaHistoryValid = false;
    }

    void DisposeTaaResources()
    {
        try
        {
            DisposeTaaTargets();
            if (taaVao != 0) GL.DeleteVertexArray(taaVao);
        }
        catch
        {
            // Teardown must continue over objects the driver already released.
        }
        taaVao = 0;
    }

    /// <summary>
    /// Once per prepared frame: remember the screen scale, and while automatic thresholds are on,
    /// re-derive L1-L6 when the render height, field of view, vanilla edge or target changed.
    /// Nothing is recomputed or allocated on a frame where none of those moved.
    /// </summary>
    void UpdateAutoThresholds()
    {
        FrameBufferRef? primary = PrimaryFrameBuffer();
        float[] projection = capi.Render.CurrentProjectionMatrix;
        // Only a perspective projection says anything about blocks on screen. This runs under
        // the frame's own, but a stray orthographic matrix must never re-select the world.
        if (primary != null && projection != null && projection.Length >= 16
            && projection[11] == -1f && projection[5] > 0.05f && projection[5] < 50f)
        {
            screenRenderHeight = primary.Height;
            screenYScale = projection[5];
        }

        if (!autoLodThresholds)
        {
            autoAppliedThresholds = null;
            return;
        }
        if (screenRenderHeight <= 0) return;

        var key = (screenRenderHeight, screenYScale, AutoThresholdFloorBlocks(), lodTargetPixels);
        if (autoAppliedThresholds != null && key == autoInputsKey) return;

        long now = capi.ElapsedMilliseconds;
        if (autoAppliedThresholds != null && now - autoLastApplyMs < AutoThresholdMinIntervalMs)
            return;
        autoInputsKey = key;
        autoLastApplyMs = now;

        int[] wanted = LodAutoThresholds.Compute(
            LodScreenScale.BlockScale(key.screenRenderHeight, key.screenYScale),
            lodTargetPixels, key.Item3);
        if (autoAppliedThresholds != null && wanted.AsSpan().SequenceEqual(autoAppliedThresholds))
            return;

        autoAppliedThresholds = wanted;
        LodWorld.SetLevelThresholds(wanted);
        AutoThresholdSummary = DescribeAutoValues(wanted);
        capi.Logger.Notification("[Landscapes] automatic LOD thresholds applied: {0}",
            AutoThresholdSummary);
    }

    /// <summary>
    /// Level 0 has to reach past the vanilla edge and the whole blend band beyond it: the band's
    /// real block textures live on level-0 quads only, and a level-1 section inside the band would
    /// show the band ending early. Taken from the live vanilla distance and band width, so it
    /// follows the player's settings rather than assuming them.
    /// </summary>
    int AutoThresholdFloorBlocks()
    {
        float band = blendPipelineEnabled && BandEnabled ? Math.Max(0f, BandWidthBlocks) : 0f;
        return (int)Math.Ceiling(ApprovedViewDistance() + band + LodSection.SectionBlocks);
    }

    /// <summary>What automatic thresholds would be right now, for the settings window.</summary>
    public int[] PreviewAutoThresholds() => LodAutoThresholds.Compute(
        LodScreenScale.BlockScale(screenRenderHeight, screenYScale), lodTargetPixels,
        AutoThresholdFloorBlocks());

    public string DescribeAutoThresholds() => DescribeAutoValues(PreviewAutoThresholds());

    string DescribeAutoValues(int[] values)
    {
        float scale = LodScreenScale.BlockScale(screenRenderHeight, screenYScale);
        double fov = screenYScale > 0f ? 2.0 * Math.Atan(1.0 / screenYScale) * 180.0 / Math.PI : 0.0;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{string.Join("/", values)} blocks - every level hands over at {lodTargetPixels:0.00} px "
            + $"on screen ({screenRenderHeight} rendered rows, {fov:0.#} deg FOV: one block is one "
            + $"pixel at {scale:0} blocks); level 0 is held out to at least "
            + $"{AutoThresholdFloorBlocks()} blocks for the vanilla edge and blend band");
    }

    /// <summary>
    /// Where anti-aliasing works at this reach, and in blocks at the current FOV and resolution,
    /// for `.dltaa reach` and the report. One clause, without a closing full stop.
    /// </summary>
    public string DescribeTaaReach()
    {
        float scale = LodScreenScale.BlockScale(screenRenderHeight, screenYScale);
        float full = LodTaaMath.FullPixels(taaReachPixels);
        string blocks = scale > 0f
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $" - at this FOV and resolution it begins about "
                + $"{LodScreenScale.DepthForBlockPixels(scale, taaReachPixels):0} blocks out and is "
                + $"full strength past about {LodScreenScale.DepthForBlockPixels(scale, full):0}")
            : " (distances show once a frame has been drawn)";
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"anti-aliasing reach {taaReachPixels:0.#} px: it fades in where a block is "
            + $"{taaReachPixels:0.#} px wide on screen and is full strength at {full:0.#} px{blocks}; "
            + $"cached terrain only, vanilla terrain is never touched");
    }

    /// <summary>The tuning dials in one clause, for the report and every dial command's reply.</summary>
    public string DescribeTaaDials() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"dials: nudge {taaNudge:0.00}, sharpen {taaSharpen:0.00}, blend {taaFeedback:0.00} while "
        + $"turning, still {taaStillFeedback:0.00}, clip {taaClip:0.00}");

    /// <summary>One line for chat and the log: state, reach, dials, activity and GPU cost.</summary>
    public string DescribeDistantAntiAliasing()
    {
        // Arms the delayed timers from now on, so the next report carries a real cost rather
        // than a zero that reads like "free" (G72).
        gpuTelemetry.RequestTiming();

        string state = !distantAntiAliasing ? "off"
            : taaFailure.Length > 0 ? "on but STOPPED this session: " + taaFailure
            : !taaShaderOk ? "on but its shaders are unavailable"
            : "on";
        string resets = taaLastResetReason.Length > 0
            ? $" (last: {taaLastResetReason})" : "";
        string cost = gpuTelemetry.TimingActive
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"GPU resolve p95 {GpuTaaCost.P95Us:0} us over {GpuTaaCost.Calls} samples, depth "
                + $"copy p95 {GpuTaaCopyCost.P95Us:0} us over {GpuTaaCopyCost.Calls}")
            : "GPU timing " + gpuTelemetry.DescribeTiming();
        // How often the still memory was in force: near 0 with the camera parked would mean an
        // idle camera still moves a little every frame and never counts as still.
        double stillPercent = TaaFramesResolved > 0 ? 100.0 * TaaFramesStill / TaaFramesResolved : 0.0;
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"distant anti-aliasing {state}; {DescribeTaaReach()}; {DescribeTaaDials()}; "
            + $"{TaaFramesResolved} frames resolved, {stillPercent:0}% of them with the camera "
            + $"fully still, {TaaHistoryResets} history resets{resets}; {cost}.");
    }
}
