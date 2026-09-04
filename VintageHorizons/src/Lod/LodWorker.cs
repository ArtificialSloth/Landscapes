using System.Collections.Concurrent;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace VintageHorizons;

/// <summary>Immutable view of a section for off-thread meshing. Arrays are never edited in place, only swapped.</summary>
public class SectionSnapshot
{
    public required ulong[] Runs;
    public required int[] ColumnStart;
    public required bool[] Captured;
    public required int[] PaletteColors;
    public required byte[] PaletteFlags;
    public required byte[] PaletteTintSlots;
    public required ushort[] PaletteTextureSlots;

    /// <summary>
    /// Array payload retained by a mesh job. Runs and column offsets are immutable shared
    /// arrays, but they still count: replacing either live array cannot release the old
    /// one while a queued snapshot references it.
    /// </summary>
    public long EstimatedRetainedBytes => EstimateRetainedBytes(
        Runs.LongLength, ColumnStart.LongLength, Captured.LongLength,
        PaletteColors.LongLength, PaletteFlags.LongLength, PaletteTintSlots.LongLength,
        PaletteTextureSlots.LongLength);

    public Span<ulong> ColumnRuns(int col) =>
        Runs.AsSpan(ColumnStart[col], ColumnStart[col + 1] - ColumnStart[col]);

    public static SectionSnapshot Of(LodSection s, bool includeTextureSlots = true)
    {
        var colors = new int[s.Palette.Count];
        var flags = new byte[s.Palette.Count];
        var slots = new byte[s.Palette.Count];
        var textureSlots = includeTextureSlots
            ? new ushort[s.Palette.Count]
            : Array.Empty<ushort>();
        for (int i = 0; i < s.Palette.Count; i++)
        {
            colors[i] = s.Palette[i].Color;
            flags[i] = s.Palette[i].Flags;
            slots[i] = s.Palette[i].TintSlot;
            if (includeTextureSlots) textureSlots[i] = s.Palette[i].TextureSlot;
        }
        return new SectionSnapshot
        {
            Runs = s.Runs,
            ColumnStart = s.ColumnStart,
            Captured = (bool[])s.Captured.Clone(),
            PaletteColors = colors,
            PaletteFlags = flags,
            PaletteTintSlots = slots,
            PaletteTextureSlots = textureSlots,
        };
    }

    public static long EstimateRetainedBytes(LodSection s, bool includeTextureSlots = true) => EstimateRetainedBytes(
        s.Runs.LongLength, s.ColumnStart.LongLength, s.Captured.LongLength,
        s.Palette.Count, s.Palette.Count, s.Palette.Count,
        includeTextureSlots ? s.Palette.Count : 0);

    static long EstimateRetainedBytes(long runs, long columnStarts, long captured,
        long paletteColors, long paletteFlags, long paletteTintSlots, long paletteTextureSlots)
    {
        long bytes = SaturatingMultiply(runs, sizeof(ulong));
        bytes = SaturatingAdd(bytes, SaturatingMultiply(columnStarts, sizeof(int)));
        bytes = SaturatingAdd(bytes, captured);
        bytes = SaturatingAdd(bytes, SaturatingMultiply(paletteColors, sizeof(int)));
        bytes = SaturatingAdd(bytes, paletteFlags);
        bytes = SaturatingAdd(bytes, paletteTintSlots);
        return SaturatingAdd(bytes, SaturatingMultiply(paletteTextureSlots, sizeof(ushort)));
    }

    static long SaturatingMultiply(long value, int factor) =>
        value > long.MaxValue / factor ? long.MaxValue : value * factor;

    internal static long SaturatingAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;
}

public class CaptureJob
{
    public long Epoch;
    public int Cx, Cz;
    public required IWorldChunk?[] Chunks; // indexed by chunkY
    public required ushort[] RainMap;      // copied on the main thread
}

/// <summary>Runs carry raw BLOCK ids (not palette ids); the main thread remaps on apply.</summary>
public class CaptureResult
{
    public long Epoch;
    public long SectionKey;
    public int Cx, Cz;
    public required ulong[]?[] RunsByColumn; // GridSize² entries, only this chunk column's 16×16 filled
    public long EstimatedBytes;
    public long ReadyAtMilliseconds;
}

public class MeshJob
{
    /// <summary>
    /// World identity captured before this immutable snapshot leaves the render thread.
    /// Queue clearing cannot stop a job already owned by a worker, so publication must reject
    /// a result whose epoch no longer matches before it touches any new-world bookkeeping.
    /// </summary>
    public long WorldEpoch;

    public long Key;
    public required SectionSnapshot Self;
    public required SectionSnapshot?[] Neighbors; // W, E, N, S, NW, NE, SW, SE
    public long EstimatedRetainedBytes;
    public long ReadyAtMilliseconds;

    /// <summary>
    /// Whether this immutable job may replace an L0 packed quad's colour with a block-texture
    /// slot. This belongs to the job, never a process-wide static: changing the saved blend
    /// setting while workers are busy must not change the format half way through a mesh.
    /// </summary>
    public bool EmitTextureSlots;

    /// <summary>
    /// Renderer-owned identity for <see cref="EmitTextureSlots"/>. A result built for an old
    /// setting is discarded and re-queued instead of being published after the player has
    /// crossed the toggle boundary.
    /// </summary>
    public long TextureModeRevision;

    /// <summary>
    /// Bit per side (W, E, N, S) whose neighbour section holds captured data we have
    /// simply not loaded into RAM yet. Those sides are NOT the frontier and must not be
    /// walled off; see LodMesher.CollectSide. The renderer re-meshes the section when
    /// the missing neighbour lands, which is what restores the real wall.
    /// </summary>
    public byte AssumedCoveredSides;

    /// <summary>
    /// Daylight reach for cave culling, in world blocks; zero leaves every cavity alone.
    /// Carried on the job rather than read from a static so that a switch flipped mid-flight
    /// cannot change the rule half way through a batch of meshes and leave the world built
    /// two different ways. See <see cref="LodCaveCull"/>.
    /// </summary>
    public int CaveCullReach;

    /// <summary>
    /// How many detail levels draw terrain slabs at their real half-block height, counting
    /// from the finest: 0 is off, 1 is level 0 alone, and anything past the deepest level is
    /// everywhere. Carried on the job for the same reason the cave reach is.
    ///
    /// Not free, and the cost lands as geometry rather than as shading. A slab beside a full
    /// block exposes the top half of that block's wall, and a half-height plane between two
    /// whole ones splits both of them - measured on the owner's slab-generated world at
    /// +32% opaque vertices at level 0 and +23% to +41% at coarser levels when every run is
    /// exact. The surface-only level-0 default measured +7.95% ordinary geometry and +7.84%
    /// clustered geometry instead. It buys the near landscape, where a half block is a step
    /// the eye can resolve and where the seam with vanilla's own smoothed ground actually is,
    /// and declines to pay for it in distant caves or where columns already span many blocks.
    /// </summary>
    public int HalfBlockLevels;

    /// <summary>
    /// Whether a block the GAME draws see-through - a crop, tall grass, a vine - may still
    /// hide the faces around it here. False is correct and is the default; true restores the
    /// behaviour before 0.3.163 for comparison.
    ///
    /// Carried on the job for the same reason the cave reach and the slab levels are: a
    /// switch flipped mid-flight must not build one half of the world by one rule and the
    /// other half by another.
    ///
    /// The cost is geometry - a top face under every crop and a wall between neighbouring
    /// ones - and all of it is sealed inside solid ground except at the vanilla seam, which
    /// is the one place it is needed. See <see cref="LodPaletteEntry.FlagCutout"/>.
    /// </summary>
    public bool CutoutBlocksCover;

    /// <summary>
    /// Brightness of a surface under n whole blocks of water, as a share of the same surface in
    /// open air, indexed by depth. Null leaves every surface at open-air brightness, which is
    /// what every build before this one drew.
    ///
    /// Handed in rather than derived here because the ramp is the world's, not a constant: a
    /// server may replace <c>SunLightLevels</c> or the maximum sun level, and both are public.
    /// The mesher runs off-thread with no world access, so the renderer resolves it once and
    /// passes the finished table. See LodMesher.SubmergedColor for why a fixed factor is exact
    /// at every time of day.
    /// </summary>
    public float[]? WaterAbsorption;

    /// <summary>
    /// How many blocks either side of a 32-block vanilla chunk plane keep one water quad per
    /// block, so the surface can carry vanilla's wave where a seam can appear. 0 merges water
    /// as before. See <see cref="LodMesher.SeamCellFor"/> for why the chunk grid is the right
    /// place and why it needs no view.
    /// </summary>
    public int WaterSeamStripBlocks;

    /// <summary>
    /// Never emit a water wall against a neighbour whose column was not captured, rather than
    /// only against an absent section. See <see cref="LodMesher.CollectSide"/>; false is the
    /// established behaviour.
    /// </summary>
    public bool SuppressUnknownWaterWalls;

    /// <summary>
    /// Apply half-block detail only to the highest solid run in each column. Water and
    /// thin cover do not disqualify the ground beneath them. This preserves outdoor terrain
    /// slabs while declining the much larger geometry cost of distant cave and overhang
    /// slabs; false retains the exact all-runs behaviour.
    /// </summary>
    public bool SurfaceHalfBlocksOnly;
}

public class MeshResult
{
    /// <summary>The originating job's world identity; see <see cref="MeshJob.WorldEpoch"/>.</summary>
    public long WorldEpoch;

    public long Key;
    public required float[] Xyz;
    public required byte[] Rgba;
    public required int[] Indices;
    public int VertexCount;
    public int IndexCount;

    // Exact worker-produced opaque quads for the optional Phase 7 renderer. Kept beside
    // the expanded arrays until the packed path has passed its visual/performance gates.
    public uint[] PackedOpaqueQuads = Array.Empty<uint>();
    public int PackedOpaqueQuadCount;

    /// <summary>Whether any packed opaque quad stores a texture slot instead of RGB.</summary>
    public bool HasTextureSlots;

    /// <summary>The originating job's texture-mode identity, used for stale-result rejection.</summary>
    public long TextureModeRevision;

    // Phase 8 comparison stream. Quads are split at a 4x4 section grid and stored
    // contiguously per populated cell; the accepted whole-section stream above remains
    // the control and same-frame fallback.
    public uint[] ClusteredPackedOpaqueQuads = Array.Empty<uint>();
    public LodPackedCluster[] PackedOpaqueClusters = Array.Empty<LodPackedCluster>();

    // Water/translucent geometry, drawn in a second blended pass.
    public float[]? WaterXyz;
    public byte[]? WaterRgba;
    public int[]? WaterIndices;
    public int WaterVertexCount;
    public int WaterIndexCount;

    /// <summary>
    /// How tall the emitted geometry actually is, per pass. Derived in RAM from work the
    /// mesher already does, so it costs no cache blob, protocol or schema change and an
    /// old cache produces it on load like any other. See <see cref="LodHeightSpan"/>.
    /// </summary>
    public LodSectionHeights Heights;

    public long ReadyAtMilliseconds;

    /// <summary>
    /// The job's <see cref="MeshJob.AssumedCoveredSides"/>, carried back so the renderer
    /// records what this mesh guessed at against the mesh it actually installs.
    /// </summary>
    public byte AssumedCoveredSides;

    /// <summary>Bytes passed to the GPU, excluding unused pooled-array capacity.</summary>
    public long EstimatedUploadBytes => SectionSnapshot.SaturatingAdd(
        UploadBytes(VertexCount, IndexCount),
        UploadBytes(WaterVertexCount, WaterIndexCount));

    public long EstimatedPackedOpaqueBytes =>
        LodPackedQuadFormat.Bytes(PackedOpaqueQuadCount);

    public long EstimatedClusteredPackedOpaqueBytes =>
        LodPackedQuadFormat.Bytes(
            ClusteredPackedOpaqueQuads.Length / LodPackedQuadFormat.WordsPerQuad);

    static long UploadBytes(int vertexCount, int indexCount)
    {
        long vertices = Math.Max(0, vertexCount);
        long indices = Math.Max(0, indexCount);
        long vertexBytes = vertices > long.MaxValue / 16 ? long.MaxValue : vertices * 16;
        long indexBytes = indices > long.MaxValue / sizeof(int)
            ? long.MaxValue : indices * sizeof(int);
        return SectionSnapshot.SaturatingAdd(vertexBytes, indexBytes);
    }
}

/// <summary>
/// The background thread: converts chunk block data into RLE columns (capture) and
/// sections into vertex data (meshing). Capture jobs take priority - meshes are only
/// as good as the data beneath them. All game-state access is via refs the main
/// thread handed over; chunk reads are guarded against concurrent disposal.
/// </summary>
public class LodWorker : IDisposable
{
    const int ChunkSize = GlobalConstants.ChunkSize;

    readonly ConcurrentQueue<CaptureJob> captureJobs = new();
    readonly ConcurrentQueue<MeshJob> meshJobs = new();
    readonly ConcurrentQueue<MipJob> mipJobs = new();
    readonly ConcurrentQueue<CaptureResult> captureResults = new();
    public readonly ConcurrentQueue<MeshResult> MeshResults = new();
    public readonly ConcurrentQueue<MipResult> MipResults = new();
    int activeCaptures;

    /// <summary>Wakes the capture thread. One job, one thread, so auto-reset is right.</summary>
    readonly AutoResetEvent captureSignal = new(false);

    /// <summary>
    /// One permit per queued mesh job, so N waiting threads wake for N jobs. An
    /// AutoResetEvent would wake exactly one however many were queued.
    /// </summary>
    readonly SemaphoreSlim meshSignal = new(0);
    readonly AutoResetEvent mipSignal = new(false);

    readonly Thread captureThread;
    readonly Thread mipThread;
    readonly Thread[] meshThreads;
    volatile bool running = true;

    /// <summary>
    /// Mesh builders. Meshing reads only immutable SectionSnapshots - the reason the
    /// snapshot discipline exists - so it parallelises with no locking. Capture does not
    /// get the same treatment: it reads live IWorldChunk objects the engine owns, and
    /// multiplying that by a thread count multiplies the risk for no comparable gain.
    ///
    /// Capture and mip each have one dedicated worker as well. On machines with at
    /// least six logical processors, reserve two more for the game's render/simulation
    /// work; smaller machines still get one mesh worker so the pipeline can progress.
    /// </summary>
    static int MeshThreadCount => Math.Clamp(Environment.ProcessorCount - 4, 1, 4);

    public int MeshThreads => meshThreads.Length;

    // Include the job currently owned by the worker so producer backpressure cannot
    // briefly admit a 25th retained capture while that job is between the two queues.
    public int PendingCaptures => captureJobs.Count + Volatile.Read(ref activeCaptures);
    public int PendingCaptureResults => captureResults.Count;
    public long PendingCaptureResultBytes =>
        captureResults.Sum(result => result.EstimatedBytes);
    public long OldestCaptureResultAgeMs => captureResults.TryPeek(out CaptureResult? oldest)
        ? Math.Max(0, Environment.TickCount64 - oldest.ReadyAtMilliseconds)
        : 0;
    public int PendingMeshes => meshJobs.Count;
    public long PendingMeshBytes => meshJobs.Sum(job => job.EstimatedRetainedBytes);
    public long OldestMeshAgeMs => meshJobs.TryPeek(out MeshJob? oldestMesh)
        ? Math.Max(0, Environment.TickCount64 - oldestMesh.ReadyAtMilliseconds)
        : 0;
    public int PendingMeshResults => MeshResults.Count;
    public long PendingMeshResultBytes => MeshResults.Sum(result => result.EstimatedUploadBytes);
    public long OldestMeshResultAgeMs => MeshResults.TryPeek(out MeshResult? oldestResult)
        ? Math.Max(0, Environment.TickCount64 - oldestResult.ReadyAtMilliseconds)
        : 0;
    public int PendingMips => mipJobs.Count;

    public int CaptureErrors;
    public int MeshErrors;
    public int MipErrors;

    /// <summary>First swallowed exception of each kind, for soak-log diagnosis.</summary>
    public string? FirstCaptureError;
    public string? FirstMeshError;
    public string? FirstMipError;

    public LodWorker()
    {
        captureThread = new Thread(CaptureLoop)
        {
            Name = "vintagehorizons-capture",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        captureThread.Start();

        // Mip construction is CPU-heavy but reads only immutable snapshots. Keeping it
        // off both capture and mesh queues prevents a propagation burst from delaying
        // newly explored columns or every visible mesh behind it.
        mipThread = new Thread(MipLoop)
        {
            Name = "vintagehorizons-mip",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        mipThread.Start();

        meshThreads = new Thread[MeshThreadCount];
        for (int i = 0; i < meshThreads.Length; i++)
        {
            meshThreads[i] = new Thread(MeshLoop)
            {
                Name = "vintagehorizons-mesh-" + i,
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };
            meshThreads[i].Start();
        }
    }

    public void EnqueueCapture(CaptureJob job)
    {
        captureJobs.Enqueue(job);
        captureSignal.Set();
    }

    public void EnqueueMesh(MeshJob job)
    {
        meshJobs.Enqueue(job);
        meshSignal.Release();
    }

    public void EnqueueMip(MipJob job)
    {
        mipJobs.Enqueue(job);
        mipSignal.Set();
    }

    public bool TryPeekCaptureResult(out CaptureResult result) =>
        captureResults.TryPeek(out result!);

    public bool TryTakeCaptureResult(out CaptureResult result)
    {
        return captureResults.TryDequeue(out result!);
    }

    public void ClearCaptureResults() => captureResults.Clear();

    /// <summary>
    /// Drop queued/results from the world being closed. A job already executing may still
    /// publish later; its world epoch makes the next world reject it.
    /// </summary>
    public void ClearMeshWork()
    {
        meshJobs.Clear();
        MeshResults.Clear();
    }

    /// <summary>
    /// Drop queued/results from the world being closed. A job already executing may
    /// still publish later; its epoch makes the next world reject it.
    /// </summary>
    public void ClearMipWork()
    {
        mipJobs.Clear();
        MipResults.Clear();
    }

    // Separate loops, not one. The old shared loop drained EVERY queued capture before
    // taking a single mesh job, so exploring - which is exactly when new terrain most needs
    // drawing - starved meshing and left coarse parents on screen for minutes.

    void CaptureLoop()
    {
        while (running)
        {
            bool didWork = false;
            while (captureJobs.TryDequeue(out CaptureJob? job))
            {
                didWork = true;
                Interlocked.Increment(ref activeCaptures);
                try
                {
                    CaptureResult? result = Capture(job);
                    if (result != null) captureResults.Enqueue(result);
                }
                catch (Exception e)
                {
                    // Chunk disposed mid-read or similar; the column re-enqueues on its next ChunkDirty.
                    Interlocked.Increment(ref CaptureErrors);
                    Interlocked.CompareExchange(ref FirstCaptureError, e.ToString(), null);
                }
                finally
                {
                    Interlocked.Decrement(ref activeCaptures);
                }
            }

            if (!didWork) captureSignal.WaitOne(250);
        }
    }

    void MeshLoop()
    {
        while (running)
        {
            // Timed wait rather than indefinite, so shutdown never depends on a permit.
            if (!meshSignal.Wait(250)) continue;
            if (!meshJobs.TryDequeue(out MeshJob? job)) continue;

            try
            {
                MeshResults.Enqueue(LodMesher.BuildMesh(job));
            }
            catch (Exception e)
            {
                // Snapshot inconsistency; section will re-mesh on its next change.
                Interlocked.Increment(ref MeshErrors);
                Interlocked.CompareExchange(ref FirstMeshError, e.ToString(), null);
            }
        }
    }

    void MipLoop()
    {
        while (running)
        {
            bool didWork = false;
            while (mipJobs.TryDequeue(out MipJob? job))
            {
                didWork = true;
                try
                {
                    MipResults.Enqueue(LodMip.BuildResult(job));
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref MipErrors);
                    Interlocked.CompareExchange(ref FirstMipError, e.ToString(), null);
                    // Always return the identity so the owning thread releases the
                    // in-flight slot and leaves the dirty obligation retryable.
                    MipResults.Enqueue(new MipResult
                    {
                        Epoch = job.Epoch,
                        ChildKey = job.ChildKey,
                        ChildRevision = job.ChildRevision,
                        RunsByParentColumn = new ulong[]?[LodSection.GridSize * LodSection.GridSize],
                        ChildPalette = job.Palette,
                        Failed = true,
                    });
                }
            }

            if (!didWork) mipSignal.WaitOne(250);
        }
    }

    // ---- Capture: chunk column → RLE columns with raw block ids ----

    static CaptureResult? Capture(CaptureJob job)
    {
        const int step = LodSection.ColumnStepBlocks;
        const int colsPerChunk = ChunkSize / step;

        int baseX = job.Cx * ChunkSize;
        int baseZ = job.Cz * ChunkSize;
        int sectionX = baseX / LodSection.SectionBlocks;
        int sectionZ = baseZ / LodSection.SectionBlocks;
        int colOffsetX = (baseX % LodSection.SectionBlocks) / step;
        int colOffsetZ = (baseZ % LodSection.SectionBlocks) / step;

        var batch = new ulong[]?[LodSection.GridSize * LodSection.GridSize];
        var runs = new List<ulong>(24);
        bool anyColumn = false;
        long estimatedBytes = 0;

        // Rain map values can sit at/above map height on freshly streamed columns
        // (uninitialized sentinel) - clamp so the y walk stays inside the chunk stack.
        int maxY = job.Chunks.Length * ChunkSize - 1;

        for (int cz = 0; cz < colsPerChunk; cz++)
        {
            for (int cx = 0; cx < colsPerChunk; cx++)
            {
                int lx = cx * step;
                int lz = cz * step;
                int startY = Math.Min(job.RainMap[lz * ChunkSize + lx], maxY);
                if (startY <= 0) continue;

                runs.Clear();
                int currentBlock = 0;
                int runTop = 0;
                bool complete = true;

                for (int y = startY; y >= 1; y--)
                {
                    IWorldChunk? chunk = job.Chunks[y / ChunkSize];
                    if (chunk == null || chunk.Disposed)
                    {
                        complete = false;
                        break;
                    }

                    int blockId = chunk.UnpackAndReadBlock(
                        ((y % ChunkSize) * ChunkSize + lz) * ChunkSize + lx,
                        BlockLayersAccess.FluidOrSolid);

                    if (blockId != currentBlock)
                    {
                        if (currentBlock != 0) runs.Add(LodSection.PackRun(currentBlock, runTop, y + 1));
                        currentBlock = blockId;
                        runTop = y + 1;
                    }
                }

                if (!complete) continue;
                if (currentBlock != 0) runs.Add(LodSection.PackRun(currentBlock, runTop, 1));

                ulong[] capturedRuns = runs.ToArray();
                batch[LodSection.ColumnIndex(colOffsetX + cx, colOffsetZ + cz)] = capturedRuns;
                estimatedBytes += capturedRuns.LongLength * sizeof(ulong);
                anyColumn = true;
            }
        }

        if (!anyColumn) return null;

        return new CaptureResult
        {
            Epoch = job.Epoch,
            SectionKey = LodWorld.SectionKey(0, sectionX, sectionZ),
            Cx = job.Cx,
            Cz = job.Cz,
            RunsByColumn = batch,
            EstimatedBytes = estimatedBytes,
            ReadyAtMilliseconds = Environment.TickCount64,
        };
    }

    public void Dispose()
    {
        running = false;
        captureSignal.Set();
        mipSignal.Set();
        meshSignal.Release(meshThreads.Length);

        captureThread.Join(2000);
        mipThread.Join(2000);
        foreach (Thread t in meshThreads) t.Join(2000);

        captureSignal.Dispose();
        mipSignal.Dispose();
        meshSignal.Dispose();
    }
}
