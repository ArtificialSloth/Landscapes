using System.Diagnostics;
using System.Globalization;
using System.Text;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VintageHorizons;

/// <summary>
/// `.dlsky`: a witness for the handoff flash that looks at the picture rather than at anyone's
/// beliefs about it.
///
/// <para>
/// Every measurement before this compared the mod's CPU ownership with the engine's CPU state
/// (`.dlgap`), and a same-frame release of every gap it found left the flash unchanged. What
/// the graphics card actually drew was never measured. This counts it. Straight after cached
/// opaque terrain is drawn, one full-screen triangle on the far plane runs with the depth test
/// ahead of its shader, so only pixels nothing opaque has covered reach it. A pixel whose view
/// ray points down, and would meet the lowest plausible ground well inside the distance cached
/// terrain is drawn to, should be showing ground. The count, a bounding box and a coarse screen
/// grid go to a small buffer that is read back two or three frames later, never waited for.
/// </para>
/// <para>
/// A frame whose count jumps above the running level is written to the client log (G72) with
/// what the CPU knew about that frame: the mask window and texels the terrain was drawn with,
/// whether they had changed, a chunk crossing, and the depth split. The ray through the busiest
/// part of that sky is then walked through the loaded world from where the camera was, to find
/// the ground it should have shown, and that chunk's ownership in the captured mask is printed
/// beside the engine's current answer for it.
/// </para>
/// <para>
/// `verify` separately reads the mask texture back after every upload and compares it with the
/// CPU texels, noting the pixel-unpack state and any GL error at that moment. It is a
/// synchronous readback, so it can change the timing it measures, which is why it is its own
/// switch.
/// </para>
/// <para>Session-only and off by default. It draws nothing: every fragment discards.</para>
/// </summary>
public partial class LodTerrainRenderer
{
    const int SkyLinesPerSecond = 20;
    const long SkySummaryMilliseconds = 1_000;
    const long SkyHeartbeatMilliseconds = 10_000;
    const double SkyRayBlocks = 640;
    const int SkyTilesReported = 2;

    public bool SkyWitness { get; private set; }
    public bool SkyMaskVerify { get; private set; }

    IShaderProgram? skyProg;
    bool skyShaderOk;
    bool skyFailed;
    int skyVao;
    readonly int[] skyBuffers = new int[SkyWitnessLayout.Slots];
    readonly IntPtr[] skyFences = new IntPtr[SkyWitnessLayout.Slots];
    readonly SkyWitnessFrame[] skyFrames = CreateSkyFrames();
    readonly uint[] skyReadback = new uint[SkyWitnessLayout.WordCount];
    readonly uint[] skyClear = SkyWitnessLayout.ClearWords();
    readonly SkyWitnessBaseline skyBaseline = new();
    readonly float[] skyViewProjection = new float[16];
    readonly int[] skyViewport = new int[4];
    readonly BlockPos skyRayPos = new(0, 0, 0);
    int[] skyVerifyTexels = Array.Empty<int>();

    long skyLastRevision = -1;
    long skyLastUploads = -1;
    int skyLastChunkX = int.MinValue;
    int skyLastChunkZ = int.MinValue;
    long skyEventChanges;
    long skyEventUploads;
    bool skyEventCrossing;

    long skyIntervalStartMs;
    long skyLastSummaryMs;
    long skyLineWindowMs;
    int skyLinesInWindow;
    int skyLinesSuppressed;
    int skyCalls;
    int skyFramesDrawn;
    int skyFramesRead;
    int skyFramesBusy;
    int skyFramesBeforeVanilla;
    int skyFramesWithSky;
    int skyFlashes;
    uint skyPixelsPeak;
    uint skyBeyondPeak;
    long skyLatencySum;
    long skyLatencyMax;
    double skyCpuMsSum;
    double skyCpuMsMax;
    int skyVerifyUploads;
    int skyVerifyMismatches;
    int skyVerifyErrors;
    string? skyUnpackReported;
    long skyLateSweepsSeen;
    long skyLateLossesSeen;

    static SkyWitnessFrame[] CreateSkyFrames()
    {
        var frames = new SkyWitnessFrame[SkyWitnessLayout.Slots];
        for (int i = 0; i < frames.Length; i++) frames[i] = new SkyWitnessFrame();
        return frames;
    }

    public string SetSkyWitness(bool on)
    {
        long now = capi.ElapsedMilliseconds;
        if (!on && SkyWitness) WriteSkySummary(now, force: true);
        SkyWitness = on;
        if (!on)
        {
            SkyMaskVerify = false;
            DisposeSkyWitnessResources();
        }
        skyFailed = false;
        ResetSkyWitness(now);
        string line = "[Landscapes] sky witness " + (on ? "on" : "off") + " at frame "
            + frameCounter.ToString(CultureInfo.InvariantCulture) + ". " + DescribeSkyWitness();
        capi.Logger.Notification("{0}", line);
        return line;
    }

    public string SetSkyMaskVerify(bool on)
    {
        if (on && !SkyWitness) SetSkyWitness(true);
        SkyMaskVerify = on;
        skyVerifyUploads = skyVerifyMismatches = skyVerifyErrors = 0;
        skyUnpackReported = null;
        string line = "[Landscapes] sky witness mask verify " + (on ? "on" : "off")
            + " at frame " + frameCounter.ToString(CultureInfo.InvariantCulture) + ". "
            + DescribeSkyWitness();
        capi.Logger.Notification("{0}", line);
        return line;
    }

    public string DescribeSkyWitness()
    {
        if (!skyShaderOk) return "The sky witness shader is not available, so .dlsky cannot run.";
        return "Sky witness " + (SkyWitness ? "on" : "off") + ", mask verify "
            + (SkyMaskVerify ? "on" : "off") + ". On counts sky showing below the horizon after "
            + "distant terrain is drawn and writes each flash to the client log with where it was "
            + "and how the chunk mask stood there; verify also reads the mask back from the "
            + "graphics card after every upload, which stalls it briefly.";
    }

    void ResetSkyWitness(long now)
    {
        skyBaseline.Reset();
        skyIntervalStartMs = skyLastSummaryMs = skyLineWindowMs = now;
        skyLinesInWindow = skyLinesSuppressed = 0;
        skyCalls = skyFramesDrawn = skyFramesRead = skyFramesBusy = 0;
        skyFramesBeforeVanilla = skyFramesWithSky = skyFlashes = 0;
        skyPixelsPeak = skyBeyondPeak = 0;
        skyLatencySum = skyLatencyMax = 0;
        skyCpuMsSum = skyCpuMsMax = 0;
        skyVerifyUploads = skyVerifyMismatches = skyVerifyErrors = 0;
        skyLastRevision = skyLastUploads = -1;
        skyLastChunkX = skyLastChunkZ = int.MinValue;
        skyEventChanges = skyEventUploads = 0;
        skyEventCrossing = false;
    }

    void LoadSkyWitnessShader()
    {
        skyProg = capi.Shader.NewShaderProgram();
        skyProg.AssetDomain = "dodenslandscapes";
        skyProg.VertexShader = capi.Shader.NewShader(EnumShaderType.VertexShader);
        skyProg.FragmentShader = capi.Shader.NewShader(EnumShaderType.FragmentShader);
        capi.Shader.RegisterFileShaderProgram("lodsky", skyProg);
        skyShaderOk = skyProg.Compile();
        skyFailed = false;
        if (!ShaderLoadIsProvisional && !skyShaderOk)
            capi.Logger.Warning(
                "[Landscapes] the sky witness shader did not compile; .dlsky is unavailable "
                + "and nothing else changes.");
    }

    /// <summary>
    /// Once per frame, straight after cached opaque terrain, while the terrain program is
    /// current. Reads back whatever earlier frames have finished, then witnesses this one.
    /// </summary>
    void RunSkyWitness()
    {
        if (!SkyWitness) return;
        long started = Stopwatch.GetTimestamp();
        skyCalls++;
        try
        {
            CollectSkyWitness();
            AccumulateSkyEvents();
            if (skyFailed || !skyShaderOk || skyProg == null || skyProg.LoadError) return;

            // Drawn before vanilla, the depth buffer holds none of vanilla's terrain yet, and
            // every chunk the engine is about to draw would count as sky.
            if (!postVanillaDepthCulling)
            {
                skyFramesBeforeVanilla++;
                return;
            }

            int slot = FreeSkySlot();
            if (slot < 0)
            {
                skyFramesBusy++;
                return;
            }
            if (!CaptureSkyFrame(skyFrames[slot])) return;
            DrawSkyWitness(slot);
            skyFramesDrawn++;
        }
        catch (Exception e)
        {
            skyFailed = true;
            capi.Logger.Warning(
                "[Landscapes] the sky witness stopped for this session; rendering is unchanged: {0}",
                e.Message);
        }
        finally
        {
            double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            skyCpuMsSum += ms;
            if (ms > skyCpuMsMax) skyCpuMsMax = ms;
            WriteSkySummary(capi.ElapsedMilliseconds, force: false);
        }
    }

    int FreeSkySlot()
    {
        for (int i = 0; i < skyFences.Length; i++)
        {
            if (skyFences[i] == IntPtr.Zero) return i;
        }
        return -1;
    }

    /// <summary>
    /// Every frame, witnessed or not: what changed since the last frame. A frame skipped while
    /// the card was behind hands its events to the next one witnessed.
    /// </summary>
    void AccumulateSkyEvents()
    {
        VanillaReadinessMask? mask = readinessMask;
        if (mask != null)
        {
            // A replaced mask starts its counters again; that step is not a change.
            if (skyLastRevision >= 0 && mask.Revision >= skyLastRevision)
                skyEventChanges += mask.Revision - skyLastRevision;
            if (skyLastUploads >= 0 && mask.Uploads >= skyLastUploads)
                skyEventUploads += mask.Uploads - skyLastUploads;
            skyLastRevision = mask.Revision;
            skyLastUploads = mask.Uploads;
        }

        int chunkX = VanillaRenderReadiness.ChunkCoordinate(camPos.X);
        int chunkZ = VanillaRenderReadiness.ChunkCoordinate(camPos.Z);
        if (skyLastChunkX != int.MinValue && (chunkX != skyLastChunkX || chunkZ != skyLastChunkZ))
            skyEventCrossing = true;
        skyLastChunkX = chunkX;
        skyLastChunkZ = chunkZ;
    }

    bool CaptureSkyFrame(SkyWitnessFrame frame)
    {
        GL.GetInteger(GetPName.Viewport, skyViewport);
        if (skyViewport[2] <= 0 || skyViewport[3] <= 0) return false;
        frustum.CopyViewProjection(skyViewProjection);
        if (Mat4f.Invert(frame.InverseViewProjection, skyViewProjection) == null) return false;

        frame.Frame = frameCounter;
        frame.CameraX = camPos.X;
        frame.CameraY = camPos.Y;
        frame.CameraZ = camPos.Z;
        frame.ViewportWidth = skyViewport[2];
        frame.ViewportHeight = skyViewport[3];

        // The window and texels the terrain shader was just given, so a report read back later
        // describes this frame's ownership rather than whatever the mask has become since.
        VanillaRenderReadiness? tracker = readiness;
        VanillaReadinessMask? mask = readinessMask;
        if (readinessMaskActive && tracker != null && mask != null && readinessMaskTexture != null)
        {
            frame.Mask = new SkyMaskWindow(true, tracker.ActiveMinChunkX, tracker.ActiveMinChunkZ,
                tracker.ActiveWidth, tracker.ActiveDepth, mask.Width, mask.VerticalChunks);
            if (frame.Texels.Length != mask.Texels.Length) frame.Texels = new int[mask.Texels.Length];
            Array.Copy(mask.Texels, frame.Texels, mask.Texels.Length);
            frame.MaskDirty = mask.Dirty;
        }
        else
        {
            frame.Mask = default;
            frame.MaskDirty = false;
        }

        frame.MaskChanges = skyEventChanges;
        frame.MaskUploads = skyEventUploads;
        frame.ChunkCrossing = skyEventCrossing;
        skyEventChanges = skyEventUploads = 0;
        skyEventCrossing = false;
        frame.DepthSplit = depthSplitThisFrame;
        frame.GpuCull = GpuCullEnabled && LastHzbBuild.Built;
        return true;
    }

    float SkyReachBlocks() => Math.Max(256f, EffectiveFarDistance - 512f) * 0.9f;

    void DrawSkyWitness(int slot)
    {
        SkyWitnessFrame frame = skyFrames[slot];

        // Everything this touches is put back exactly: the engine's opaque pass continues after
        // it, and so does this renderer's water.
        int genericBuffer = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
        GL.GetInteger(GetIndexedPName.ShaderStorageBufferBinding, SkyWitnessLayout.Binding,
            out int indexedBuffer);
        int vertexArray = GL.GetInteger(GetPName.VertexArrayBinding);
        bool depthTest = GL.IsEnabled(EnableCap.DepthTest);
        bool cullFace = GL.IsEnabled(EnableCap.CullFace);
        int depthFunc = GL.GetInteger(GetPName.DepthFunc);
        GL.GetBoolean(GetPName.DepthWritemask, out bool depthWrites);
        IShaderProgram? previous = ShaderProgramBase.CurrentShaderProgram;

        try
        {
            EnsureSkyResources();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, skyBuffers[slot]);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero,
                SkyWitnessLayout.WordCount * sizeof(uint), skyClear);

            previous?.Stop();
            skyProg!.Use();
            skyProg.UniformMatrix("inverseViewProjection", frame.InverseViewProjection);
            skyProg.Uniform("viewportRect",
                new Vec4f(skyViewport[0], skyViewport[1], skyViewport[2], skyViewport[3]));
            skyProg.Uniform("floorRelY", (float)(capi.World.SeaLevel - 64 - frame.CameraY));
            skyProg.Uniform("reachBlocks", SkyReachBlocks());
            skyProg.Uniform("tilesX", SkyWitnessLayout.TilesX);
            skyProg.Uniform("tilesY", SkyWitnessLayout.TilesY);

            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, SkyWitnessLayout.Binding,
                skyBuffers[slot]);
            GL.BindVertexArray(skyVao);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.DepthMask(false);
            GL.Disable(EnableCap.CullFace);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);

            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit
                | MemoryBarrierFlags.BufferUpdateBarrierBit);
            IntPtr fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
            if (fence == IntPtr.Zero)
                throw new InvalidOperationException("the driver refused a fence for the witness");
            skyFences[slot] = fence;
        }
        finally
        {
            GL.DepthMask(depthWrites);
            GL.DepthFunc((DepthFunction)depthFunc);
            if (!depthTest) GL.Disable(EnableCap.DepthTest);
            if (cullFace) GL.Enable(EnableCap.CullFace);
            GL.BindVertexArray(vertexArray);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, SkyWitnessLayout.Binding,
                indexedBuffer);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, genericBuffer);
            if (ShaderProgramBase.CurrentShaderProgram == skyProg) skyProg!.Stop();
            if (previous != null)
            {
                previous.Use();
                // Use() rebinds the engine's samplers and can land on ours (G130).
                if (ReferenceEquals(previous, prog)) RebindOwnSamplers(prog!);
            }
        }
    }

    void EnsureSkyResources()
    {
        if (skyVao == 0) skyVao = GL.GenVertexArray();
        for (int i = 0; i < skyBuffers.Length; i++)
        {
            if (skyBuffers[i] != 0) continue;
            skyBuffers[i] = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, skyBuffers[i]);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, SkyWitnessLayout.WordCount * sizeof(uint),
                skyClear, BufferUsageHint.DynamicRead);
        }
    }

    /// <summary>
    /// Reads every finished frame, oldest first. The card finishes in order, so the first one
    /// still pending means the rest are too; nothing here ever waits.
    /// </summary>
    void CollectSkyWitness()
    {
        while (true)
        {
            int slot = -1;
            for (int i = 0; i < skyFences.Length; i++)
            {
                if (skyFences[i] == IntPtr.Zero) continue;
                if (slot < 0 || skyFrames[i].Frame < skyFrames[slot].Frame) slot = i;
            }
            if (slot < 0) return;

            WaitSyncStatus status = GL.ClientWaitSync(skyFences[slot], ClientWaitSyncFlags.None, 0);
            if (status == WaitSyncStatus.WaitFailed)
                throw new InvalidOperationException("waiting for a witness fence failed");
            if (status != WaitSyncStatus.AlreadySignaled && status != WaitSyncStatus.ConditionSatisfied)
                return;

            GL.DeleteSync(skyFences[slot]);
            skyFences[slot] = IntPtr.Zero;
            int generic = GL.GetInteger(GetPName.ShaderStorageBufferBinding);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, skyBuffers[slot]);
            GL.GetBufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero,
                SkyWitnessLayout.WordCount * sizeof(uint), skyReadback);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, generic);
            ReportSkyFrame(skyFrames[slot], skyReadback);
        }
    }

    void ReportSkyFrame(SkyWitnessFrame frame, uint[] words)
    {
        long latency = frameCounter - frame.Frame;
        skyFramesRead++;
        skyLatencySum += latency;
        if (latency > skyLatencyMax) skyLatencyMax = latency;

        uint total = words[SkyWitnessLayout.Total];
        uint beyond = words[SkyWitnessLayout.BeyondReach];
        if (total > 0) skyFramesWithSky++;
        if (total > skyPixelsPeak) skyPixelsPeak = total;
        if (beyond > skyBeyondPeak) skyBeyondPeak = beyond;

        double level = skyBaseline.Level;
        if (!skyBaseline.IsFlash(total)) return;
        skyFlashes++;
        if (!TakeSkyLine(capi.ElapsedMilliseconds)) return;

        int width = frame.ViewportWidth;
        int height = frame.ViewportHeight;
        var line = new StringBuilder(640);
        line.Append("[Landscapes] sky: frame ").Append(frame.Frame)
            .Append(" (read ").Append(latency).Append(" frames later): ").Append(total)
            .Append(" px of sky below the horizon where cached terrain reaches, usual level ")
            .Append(level.ToString("0", CultureInfo.InvariantCulture)).Append(" px");

        uint minX = words[SkyWitnessLayout.MinX];
        uint minY = words[SkyWitnessLayout.MinY];
        uint maxX = words[SkyWitnessLayout.MaxX];
        uint maxY = words[SkyWitnessLayout.MaxY];
        if (minX <= maxX && minY <= maxY)
        {
            // gl_FragCoord counts up from the bottom; the log counts down from the top, as a
            // screenshot does.
            line.Append("; box x ").Append(minX).Append('-').Append(maxX)
                .Append(", y ").Append(height - 1 - (int)maxY).Append('-').Append(height - 1 - (int)minY)
                .Append(" of ").Append(width).Append('x').Append(height);
        }

        line.Append("; that frame: ");
        AppendSkyFrameEvents(line, frame);
        foreach (SkyTile tile in SkyWitnessReport.TopTiles(words, SkyTilesReported))
        {
            line.Append(" | ");
            AppendSkyTile(line, frame, tile);
        }
        capi.Logger.Notification("{0}", line.ToString());
    }

    static void AppendSkyFrameEvents(StringBuilder line, SkyWitnessFrame frame)
    {
        if (!frame.Mask.Enabled)
        {
            line.Append("mask off");
        }
        else
        {
            line.Append(frame.MaskChanges == 0
                ? "mask unchanged"
                : "mask changed " + frame.MaskChanges.ToString(CultureInfo.InvariantCulture) + " texel(s)");
            if (frame.MaskUploads > 0) line.Append(", uploaded");
            if (frame.MaskDirty) line.Append(", changed again after its upload");
        }
        if (frame.ChunkCrossing) line.Append(", chunk crossing");
        if (frame.DepthSplit) line.Append(", depth split");
        line.Append(frame.GpuCull ? ", GPU cull" : ", no GPU cull");
    }

    void AppendSkyTile(StringBuilder line, SkyWitnessFrame frame, SkyTile tile)
    {
        int width = frame.ViewportWidth;
        int height = frame.ViewportHeight;
        double pixelX = (tile.X + 0.5) * width / SkyWitnessLayout.TilesX;
        double pixelY = (tile.Y + 0.5) * height / SkyWitnessLayout.TilesY;
        line.Append(tile.Pixels).Append(" px near x ").Append((int)pixelX)
            .Append(", y ").Append(height - 1 - (int)pixelY).Append(": ");

        if (!SkyWitnessReport.TryRay(frame.InverseViewProjection, pixelX, pixelY, width, height,
            out double dx, out double dy, out double dz))
        {
            line.Append("no ray");
            return;
        }

        // Walk the loaded world from where the camera was. The first solid block is the ground
        // this pixel should have shown; past the loaded chunks only the cache could draw it.
        IBlockAccessor blocks = capi.World.BlockAccessor;
        int lastX = int.MinValue;
        int lastY = int.MinValue;
        int lastZ = int.MinValue;
        for (double distance = 0.5; distance <= SkyRayBlocks; distance += 0.5)
        {
            double x = frame.CameraX + dx * distance;
            double y = frame.CameraY + dy * distance;
            double z = frame.CameraZ + dz * distance;
            if (y < 0 || y >= worldHeight)
            {
                line.Append("left the world at ").Append((int)distance).Append(" blocks");
                return;
            }

            int bx = (int)Math.Floor(x);
            int by = (int)Math.Floor(y);
            int bz = (int)Math.Floor(z);
            if (bx == lastX && by == lastY && bz == lastZ) continue;
            lastX = bx;
            lastY = by;
            lastZ = bz;

            if (blocks.GetChunkAtBlockPos(bx, by, bz) == null)
            {
                line.Append("no loaded chunk from ").Append((int)distance).Append(" blocks");
                AppendSkyCell(line, frame, bx, by, bz);
                return;
            }

            skyRayPos.Set(bx, by, bz);
            Block? block = blocks.GetBlock(skyRayPos, BlockLayersAccess.Solid);
            if (block == null || block.BlockId == 0) continue;
            line.Append(block.Code?.ToShortString() ?? "a block").Append(" at ")
                .Append((int)distance).Append(" blocks");
            AppendSkyCell(line, frame, bx, by, bz);
            return;
        }
        line.Append("no ground within ").Append((int)SkyRayBlocks).Append(" blocks");
    }

    void AppendSkyCell(StringBuilder line, SkyWitnessFrame frame, int bx, int by, int bz)
    {
        var cell = new VanillaChunkCell(
            VanillaRenderReadiness.ChunkCoordinate(bx),
            VanillaRenderReadiness.ChunkCoordinate(by),
            VanillaRenderReadiness.ChunkCoordinate(bz));
        double offsetX = (cell.X + 0.5) * 32.0 - frame.CameraX;
        double offsetZ = (cell.Z + 0.5) * 32.0 - frame.CameraZ;
        line.Append(", chunk ").Append(cell.X).Append(',').Append(cell.Y).Append(',').Append(cell.Z)
            .Append(' ').Append((int)Math.Sqrt(offsetX * offsetX + offsetZ * offsetZ))
            .Append(" blocks out");

        SkyMaskWindow window = frame.Mask;
        if (!window.Enabled)
        {
            line.Append(" (mask off)");
        }
        else if (cell.X < window.MinX || cell.X >= window.MinX + window.Width
            || cell.Z < window.MinZ || cell.Z >= window.MinZ + window.Depth)
        {
            line.Append(": outside the mask window, so the cache owned it");
        }
        else
        {
            bool owned = SkyWitnessReport.GroundOwned(frame.Texels, window, cell.X, cell.Y, cell.Z);
            line.Append(owned ? ": the mask gave it to VANILLA" : ": the mask gave it to the CACHE")
                .Append(" (vanilla layers in that column: ")
                .Append(SkyWitnessReport.OwnedLayers(frame.Texels, window, cell.X, cell.Z)).Append(')');
        }

        bool renderedNow;
        try
        {
            readinessProbePos.Dimension = 0;
            readinessProbePos.SetPos((cell.X + 0.5) * 32.0, (cell.Y + 0.5) * 32.0, (cell.Z + 0.5) * 32.0);
            renderedNow = capi.IsChunkRendered(readinessProbePos);
        }
        catch
        {
            renderedNow = false;
        }
        line.Append("; vanilla renders it now: ").Append(renderedNow ? "yes" : "no");
        if (HandoffGapLog && gapTracker.IsOpen(cell)) line.Append("; a .dlgap episode is open for it");
    }

    bool TakeSkyLine(long now)
    {
        if (now - skyLineWindowMs >= 1000)
        {
            skyLineWindowMs = now;
            skyLinesInWindow = 0;
        }
        if (skyLinesInWindow >= SkyLinesPerSecond)
        {
            skyLinesSuppressed++;
            return false;
        }
        skyLinesInWindow++;
        return true;
    }

    void WriteSkySummary(long now, bool force)
    {
        if (!SkyWitness) return;
        bool busy = skyFlashes > 0 || skyVerifyMismatches > 0 || skyVerifyErrors > 0;
        long interval = busy ? SkySummaryMilliseconds : SkyHeartbeatMilliseconds;
        if (!force && now - skyLastSummaryMs < interval) return;

        CultureInfo invariant = CultureInfo.InvariantCulture;
        double seconds = Math.Max(0.001, (now - skyIntervalStartMs) / 1000.0);
        var s = new StringBuilder(512);
        s.Append("[Landscapes] sky summary, ").Append(seconds.ToString("0.0", invariant))
            .Append(" s: ").Append(skyFramesDrawn).Append(" frames witnessed (")
            .Append(skyFramesBusy).Append(" skipped while the card was behind), ")
            .Append(skyFramesRead).Append(" read, ").Append(skyFramesWithSky)
            .Append(" with any sky below the horizon, ").Append(skyFlashes).Append(" reported as flashes");
        if (skyLinesSuppressed > 0)
            s.Append(" (").Append(skyLinesSuppressed).Append(" over the line limit)");
        if (skyFramesBeforeVanilla > 0)
            s.Append("; ").Append(skyFramesBeforeVanilla)
                .Append(" frames not witnessed because cached terrain is drawn before vanilla (.dlocclusion off)");
        s.Append("; peak ").Append(skyPixelsPeak).Append(" px, usual level ")
            .Append(skyBaseline.Level.ToString("0", invariant))
            .Append(" px, horizon past the cache's reach peak ").Append(skyBeyondPeak)
            .Append(" px; read back ")
            .Append(skyFramesRead > 0 ? (skyLatencySum / (double)skyFramesRead).ToString("0.0", invariant) : "-")
            .Append(" frames later on average, ").Append(skyLatencyMax).Append(" at most; CPU ")
            .Append(skyCalls > 0 ? (skyCpuMsSum / skyCalls).ToString("0.000", invariant) : "-")
            .Append(" ms mean/").Append(skyCpuMsMax.ToString("0.000", invariant)).Append(" ms peak");
        if (SkyMaskVerify)
        {
            s.Append("; mask verify: ").Append(skyVerifyUploads).Append(" uploads read back, ")
                .Append(skyVerifyMismatches).Append(" differed from the CPU copy, ")
                .Append(skyVerifyErrors).Append(" GL errors");
        }
        s.Append("; late range check ").Append(LateRangeCheck ? "on" : "off").Append(": ")
            .Append(LateRangeSweeps - skyLateSweepsSeen).Append(" re-sweeps, ")
            .Append(LateRangeLosses - skyLateLossesSeen).Append(" chunks handed over in the frame vanilla dropped them");
        skyLateSweepsSeen = LateRangeSweeps;
        skyLateLossesSeen = LateRangeLosses;
        if (skyFailed) s.Append("; STOPPED after an error (see the warning above)");
        capi.Logger.Notification("{0}", s.ToString());

        skyIntervalStartMs = skyLastSummaryMs = now;
        skyLinesSuppressed = 0;
        skyCalls = skyFramesDrawn = skyFramesRead = skyFramesBusy = 0;
        skyFramesBeforeVanilla = skyFramesWithSky = skyFlashes = 0;
        skyPixelsPeak = skyBeyondPeak = 0;
        skyLatencySum = skyLatencyMax = 0;
        skyCpuMsSum = skyCpuMsMax = 0;
        skyVerifyUploads = skyVerifyMismatches = skyVerifyErrors = 0;
    }

    /// <summary>
    /// `.dlsky verify`: straight after the mask texture upload, read it back and compare. Also
    /// records the pixel-unpack state the upload ran under, because a buffer left bound to the
    /// unpack target, or a stray row length, turns a correct CPU copy into a wrong texture with
    /// no error at all.
    /// </summary>
    void VerifySkyMaskUpload(LoadedTexture texture, int[] expected, int unit)
    {
        long now = capi.ElapsedMilliseconds;
        try
        {
            ErrorCode error = GL.GetError();
            int unpackBuffer = GL.GetInteger(GetPName.PixelUnpackBufferBinding);
            int rowLength = GL.GetInteger(GetPName.UnpackRowLength);
            int skipRows = GL.GetInteger(GetPName.UnpackSkipRows);
            int skipPixels = GL.GetInteger(GetPName.UnpackSkipPixels);
            int alignment = GL.GetInteger(GetPName.UnpackAlignment);
            string unpack = "unpack buffer " + unpackBuffer + ", row length " + rowLength
                + ", skip " + skipRows + "/" + skipPixels + ", alignment " + alignment;
            if ((unpackBuffer != 0 || rowLength != 0 || skipRows != 0 || skipPixels != 0)
                && unpack != skyUnpackReported)
            {
                skyUnpackReported = unpack;
                capi.Logger.Warning(
                    "[Landscapes] sky verify: the mask was uploaded under non-default pixel "
                    + "unpack state at frame {0}: {1}", frameCounter, unpack);
            }
            if (error != ErrorCode.NoError)
            {
                skyVerifyErrors++;
                if (TakeSkyLine(now))
                    capi.Logger.Warning(
                        "[Landscapes] sky verify: the mask upload at frame {0} left GL error {1} ({2})",
                        frameCounter, error, unpack);
            }

            if (skyVerifyTexels.Length != expected.Length) skyVerifyTexels = new int[expected.Length];
            int packBuffer = GL.GetInteger(GetPName.PixelPackBufferBinding);
            int packRowLength = GL.GetInteger(GetPName.PackRowLength);
            int packAlignment = GL.GetInteger(GetPName.PackAlignment);
            GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
            GL.PixelStore(PixelStoreParameter.PackRowLength, 0);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
            GL.ActiveTexture(TextureUnit.Texture0 + unit);
            GL.BindTexture(TextureTarget.Texture2D, texture.TextureId);
            GL.GetTexImage(TextureTarget.Texture2D, 0, PixelFormat.Bgra, PixelType.UnsignedByte,
                skyVerifyTexels);
            GL.PixelStore(PixelStoreParameter.PackAlignment, packAlignment);
            GL.PixelStore(PixelStoreParameter.PackRowLength, packRowLength);
            GL.BindBuffer(BufferTarget.PixelPackBuffer, packBuffer);
            skyVerifyUploads++;

            int differing = 0;
            int first = -1;
            for (int i = 0; i < expected.Length; i++)
            {
                if (skyVerifyTexels[i] == expected[i]) continue;
                if (first < 0) first = i;
                differing++;
            }
            if (differing == 0) return;

            skyVerifyMismatches++;
            if (!TakeSkyLine(now)) return;
            int capacity = readinessMask?.Width ?? 1;
            int row = first / capacity;
            capi.Logger.Warning(
                "[Landscapes] sky verify: after the upload at frame {0} the card holds {1} "
                + "texel(s) that differ from the CPU copy; first at ring x {2}, z {3}, layer {4}: "
                + "card 0x{5:X8}, CPU 0x{6:X8} ({7})",
                frameCounter, differing, first % capacity, row % capacity, row / capacity,
                skyVerifyTexels[first], expected[first], unpack);
        }
        catch (Exception e)
        {
            SkyMaskVerify = false;
            capi.Logger.Warning("[Landscapes] sky verify stopped: {0}", e.Message);
        }
    }

    void DisposeSkyWitnessResources()
    {
        for (int i = 0; i < skyFences.Length; i++)
        {
            if (skyFences[i] != IntPtr.Zero)
            {
                try { GL.DeleteSync(skyFences[i]); }
                catch { /* context teardown must continue */ }
                skyFences[i] = IntPtr.Zero;
            }
            if (skyBuffers[i] != 0)
            {
                try { GL.DeleteBuffer(skyBuffers[i]); }
                catch { /* context teardown must continue */ }
                skyBuffers[i] = 0;
            }
        }
        if (skyVao != 0)
        {
            try { GL.DeleteVertexArray(skyVao); }
            catch { /* context teardown must continue */ }
            skyVao = 0;
        }
    }
}
