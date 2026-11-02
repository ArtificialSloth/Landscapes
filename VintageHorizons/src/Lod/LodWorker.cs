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
    public ulong[] SnowStateObservedBits = Array.Empty<ulong>();
    public ulong[] SnowCoveredBits = Array.Empty<ulong>();
    public required int[] PaletteColors;
    public required byte[] PaletteFlags;
    public required byte[] PaletteTintSlots;
    public required ushort[] PaletteTextureSlots;

    /// <summary>
    /// The colour each palette entry's walls draw at, along X and along Z, where it differs
    /// from <see cref="PaletteColors"/>; zero, or an empty array, means the block's own colour.
    /// A block's colour is its TOP's texture, which for a standing log is the cut end, so
    /// without these every distant trunk wore the colour of its rings. Read through
    /// <see cref="WallColor"/>.
    /// </summary>
    public int[] PaletteWallXColors = Array.Empty<int>();
    public int[] PaletteWallZColors = Array.Empty<int>();

    /// <summary>
    /// How each palette entry stands if it is a crossed plant (<see cref="LodBlockPolicy"/>'s
    /// CrossedShape values); an empty array means by its flags. Read through
    /// <see cref="CrossedShape"/>.
    /// </summary>
    public byte[] PaletteCrossedShapes = Array.Empty<byte>();

    public byte CrossedShape(int pid) => (uint)pid < (uint)PaletteCrossedShapes.Length
        ? PaletteCrossedShapes[pid]
        : LodBlockPolicy.CrossedShapeByFlags;

    const byte CanopyLeaf = 1;
    const byte CanopyWood = 2;
    const byte CanopyResin = 3;
    const byte CanopyBush = 4;

    /// <summary>
    /// Which palette entries are leaf planes or the wood of a fruit tree, only where leaves are
    /// drawn as planes; otherwise empty. A neighbour's leaves are asked about too, which
    /// <see cref="PaletteCrossedShapes"/> cannot answer, since neighbours carry none. Logs holding
    /// resin are marked here too, where they are drawn.
    /// </summary>
    public byte[] PaletteCanopy = Array.Empty<byte>();

    public bool IsLeafPlane(int pid) => (uint)pid < (uint)PaletteCanopy.Length && PaletteCanopy[pid] == CanopyLeaf;

    public bool IsCanopyWood(int pid) => (uint)pid < (uint)PaletteCanopy.Length && PaletteCanopy[pid] == CanopyWood;

    public bool IsResinLog(int pid) => (uint)pid < (uint)PaletteCanopy.Length && PaletteCanopy[pid] == CanopyResin;

    public bool IsBushSheet(int pid) => (uint)pid < (uint)PaletteCanopy.Length && PaletteCanopy[pid] == CanopyBush;

    /// <summary>The colour a wall of this palette entry draws at, along X or along Z.</summary>
    public int WallColor(int pid, bool xWall)
    {
        int[] walls = xWall ? PaletteWallXColors : PaletteWallZColors;
        int wall = (uint)pid < (uint)walls.Length ? walls[pid] : 0;
        return wall != 0 ? wall : PaletteColors[pid];
    }

    /// <summary>
    /// Array payload retained by a mesh job. Runs and column offsets are immutable shared
    /// arrays, but they still count: replacing either live array cannot release the old
    /// one while a queued snapshot references it.
    /// </summary>
    public long EstimatedRetainedBytes => EstimateRetainedBytes(
        Runs.LongLength, ColumnStart.LongLength, Captured.LongLength,
        SnowStateObservedBits.LongLength, SnowCoveredBits.LongLength,
        PaletteColors.LongLength + PaletteWallXColors.LongLength + PaletteWallZColors.LongLength,
        PaletteFlags.LongLength + PaletteCrossedShapes.LongLength + PaletteCanopy.LongLength,
        PaletteTintSlots.LongLength, PaletteTextureSlots.LongLength);

    public Span<ulong> ColumnRuns(int col) =>
        Runs.AsSpan(ColumnStart[col], ColumnStart[col + 1] - ColumnStart[col]);

    /// <param name="wallColors">
    /// A block id's wall colours (see <see cref="PaletteWallXColors"/>), or null to leave every
    /// wall at its block's colour. Only the section being meshed needs them: neighbours are
    /// asked what covers what, never what colour anything is.
    /// </param>
    /// <param name="crossedShapes">
    /// A block id's <see cref="PaletteCrossedShapes"/> value, or null to stand every crossed plant
    /// by its flags. Only the section being meshed needs them, as with wall colours.
    /// </param>
    /// <param name="leafPlanes">
    /// Block ids that are leaves, when this level draws them as planes (<see cref="LodLeafShape"/>);
    /// otherwise null. A leaf then reads as a crossed plant here and only here, which is the rule
    /// wanted for cover as well as for drawing, so neighbours need it too. A leaf with no texture
    /// slot has nothing to cut its planes out with, and stays a cube.
    /// </param>
    /// <param name="canopyWood">
    /// Block ids that are a fruit tree's wood, which stop at the canopy when leaves are planes.
    /// Only the section being meshed needs them.
    /// </param>
    /// <param name="resinLogs">
    /// Block ids that are logs holding resin, where this level marks them; otherwise null.
    /// </param>
    /// <param name="bushSheets">
    /// Block ids that are berry bushes, where this level stands them up as sheets; otherwise null.
    /// Like a leaf plane a bush then covers nothing, so neighbours need it too, and one with no
    /// texture slot stays a cube.
    /// </param>
    public static SectionSnapshot Of(LodSection s, bool includeTextureSlots = true,
        System.Func<int, (int X, int Z)>? wallColors = null,
        System.Func<int, byte>? crossedShapes = null, bool[]? leafPlanes = null,
        bool[]? canopyWood = null, bool[]? resinLogs = null, bool[]? bushSheets = null)
    {
        var colors = new int[s.Palette.Count];
        var flags = new byte[s.Palette.Count];
        var slots = new byte[s.Palette.Count];
        var textureSlots = includeTextureSlots
            ? new ushort[s.Palette.Count]
            : Array.Empty<ushort>();
        int[] wallX = wallColors != null ? new int[s.Palette.Count] : Array.Empty<int>();
        int[] wallZ = wallColors != null ? new int[s.Palette.Count] : Array.Empty<int>();
        byte[] shapes = crossedShapes != null ? new byte[s.Palette.Count] : Array.Empty<byte>();
        byte[] canopy = leafPlanes != null || resinLogs != null || bushSheets != null
            ? new byte[s.Palette.Count]
            : Array.Empty<byte>();
        for (int i = 0; i < s.Palette.Count; i++)
        {
            colors[i] = s.Palette[i].Color;
            flags[i] = s.Palette[i].Flags;
            slots[i] = s.Palette[i].TintSlot;
            if (includeTextureSlots) textureSlots[i] = s.Palette[i].TextureSlot;
            if (wallColors != null) (wallX[i], wallZ[i]) = wallColors(s.Palette[i].BlockId);
            if (bushSheets != null && includeTextureSlots && textureSlots[i] != 0
                && (uint)s.Palette[i].BlockId < (uint)bushSheets.Length && bushSheets[s.Palette[i].BlockId])
            {
                flags[i] |= LodPaletteEntry.FlagCrossed;
                canopy[i] = CanopyBush;
            }
            else if (leafPlanes != null && includeTextureSlots && textureSlots[i] != 0
                && (uint)s.Palette[i].BlockId < (uint)leafPlanes.Length && leafPlanes[s.Palette[i].BlockId])
            {
                flags[i] |= LodPaletteEntry.FlagCrossed;
                canopy[i] = CanopyLeaf;
                if (crossedShapes != null) shapes[i] = LodBlockPolicy.CrossedShapeLeaf;
            }
            // Most sections hold no crossed plant, and flags are the cheap way to know.
            else if (crossedShapes != null && (flags[i] & LodPaletteEntry.FlagCrossed) != 0)
            {
                shapes[i] = crossedShapes(s.Palette[i].BlockId);
            }
            else if (leafPlanes != null && canopyWood != null
                && (uint)s.Palette[i].BlockId < (uint)canopyWood.Length && canopyWood[s.Palette[i].BlockId])
            {
                // It stops at the foliage, and low foliage stops it at its foot, so it covers
                // nothing: the ground under it keeps its top, or the sky shows through there.
                flags[i] |= LodPaletteEntry.FlagCutout;
                canopy[i] = CanopyWood;
            }
            else if (resinLogs != null
                && (uint)s.Palette[i].BlockId < (uint)resinLogs.Length && resinLogs[s.Palette[i].BlockId])
            {
                canopy[i] = CanopyResin;
            }
        }
        return new SectionSnapshot
        {
            Runs = s.Runs,
            ColumnStart = s.ColumnStart,
            Captured = (bool[])s.Captured.Clone(),
            SnowStateObservedBits = (ulong[])s.SnowStateObservedBits.Clone(),
            SnowCoveredBits = (ulong[])s.SnowCoveredBits.Clone(),
            PaletteColors = colors,
            PaletteFlags = flags,
            PaletteTintSlots = slots,
            PaletteTextureSlots = textureSlots,
            PaletteWallXColors = wallX,
            PaletteWallZColors = wallZ,
            PaletteCrossedShapes = shapes,
            PaletteCanopy = canopy,
        };
    }

    public static long EstimateRetainedBytes(LodSection s, bool includeTextureSlots = true,
        bool includeWallColors = false, bool includeCrossedShapes = false) => EstimateRetainedBytes(
        s.Runs.LongLength, s.ColumnStart.LongLength, s.Captured.LongLength,
        s.SnowStateObservedBits.LongLength, s.SnowCoveredBits.LongLength,
        includeWallColors ? 3L * s.Palette.Count : s.Palette.Count,
        includeCrossedShapes ? 2L * s.Palette.Count : s.Palette.Count, s.Palette.Count,
        includeTextureSlots ? s.Palette.Count : 0);

    static long EstimateRetainedBytes(long runs, long columnStarts, long captured,
        long snowStateObserved, long snowCovered,
        long paletteColors, long paletteFlags, long paletteTintSlots, long paletteTextureSlots)
    {
        long bytes = SaturatingMultiply(runs, sizeof(ulong));
        bytes = SaturatingAdd(bytes, SaturatingMultiply(columnStarts, sizeof(int)));
        bytes = SaturatingAdd(bytes, captured);
        bytes = SaturatingAdd(bytes, SaturatingMultiply(snowStateObserved, sizeof(ulong)));
        bytes = SaturatingAdd(bytes, SaturatingMultiply(snowCovered, sizeof(ulong)));
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
    public bool SnowStateObserved;
    public required IWorldChunk?[] Chunks; // indexed by chunkY
    public required ushort[] RainMap;      // copied on the main thread

    /// <summary>
    /// What the column's chiselled blocks are made of, copied where the job was created
    /// (LodChiselStandIn.Collect); null when it has none. The worker records each one as its
    /// most visible material instead of as the chisel.
    /// </summary>
    public List<LodChiselRecord>? Chisels;

    /// <summary>Block ids that are reeds (<see cref="LodBlockPolicy.ReedTable"/>), shared and read-only.</summary>
    public bool[]? Reeds;

    /// <summary>
    /// What the column's fruit tree foliage is showing, copied where the job was created
    /// (LodFruitTrees.Collect), by column-local index; null when it has none.
    /// </summary>
    public Dictionary<int, (int BlockId, string Variant)>? Foliage;
}

/// <summary>Runs carry raw BLOCK ids (not palette ids); the main thread remaps on apply.</summary>
public class CaptureResult
{
    public long Epoch;
    public long SectionKey;
    public int Cx, Cz;
    public bool SnowStateObserved;
    public required ulong[]?[] RunsByColumn; // GridSize² entries, only this chunk column's 16×16 filled
    public long EstimatedBytes;
    public long ReadyAtMilliseconds;

    /// <summary>
    /// For each run whose top block is a chisel recorded as its material, the chisel's own
    /// block id, keyed by LodChiselStandIn.RunKey. Apply reads the chisel's flags for the
    /// column's snow, because a snowy chisel variant is not its material. Null when none.
    /// </summary>
    public Dictionary<int, int>? StandInOriginals;

    /// <summary>
    /// The palette variant of each run that has one (LodFruitTrees), keyed by
    /// LodChiselStandIn.RunKey. Null when none.
    /// </summary>
    public Dictionary<int, string>? RunVariants;

    /// <summary>
    /// What block light reaches each column's top (<see cref="LodBlockLight"/>), by section column,
    /// or null with none. Read only with <see cref="SnowStateObserved"/>: a generated peek has no
    /// lighting yet, and must not read as every lamp having gone out.
    /// </summary>
    public ushort[]? BlockLight;

    /// <summary>Blocks recorded as their material, and read chisels with no material to use.</summary>
    public int ChiselStandIns;
    public int ChiselsWithoutMaterial;
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

    /// <summary>
    /// Content identity of <see cref="Self"/> when the snapshot was taken, carried through
    /// the worker for diagnostics and correlation. Publication deliberately gates on
    /// <see cref="RenderRevision"/> instead: provenance-only snow changes advance content
    /// revisions for mip correctness without changing the mesh.
    /// </summary>
    public long ContentRevision;

    /// <summary>
    /// Identity of the complete render obligation, including neighbour-edge and render-mode
    /// changes that do not alter this section's own content.
    /// </summary>
    public long RenderRevision;

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

    /// <summary>
    /// How leaves the snapshots mark as planes are laid out. Carried on the job for the same reason
    /// the cave reach is: a mesh built half way through a change must not mix two.
    /// </summary>
    public LodLeafShape LeafShape;
}

public class MeshResult
{
    /// <summary>The originating job's world identity; see <see cref="MeshJob.WorldEpoch"/>.</summary>
    public long WorldEpoch;

    public long Key;
    public long ContentRevision;
    public long RenderRevision;
    public required float[] Xyz;
    public required byte[] Rgba;
    public required int[] Indices;
    public int VertexCount;
    public int IndexCount;

    /// <summary>
    /// Exact 32-block cells spanned by opaque top surfaces in this result. Kept with the mesh
    /// through publication so Farseer can yield to what is actually resident, at every level.
    /// </summary>
    internal LodMeshGroundCoverage OpaqueGroundCoverage;

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

    /// <summary>
    /// Whether <see cref="Xyz"/>, <see cref="Rgba"/>, <see cref="Indices"/>,
    /// <see cref="PackedOpaqueQuads"/> and the water arrays were rented from the shared array
    /// pool (0.4.131): the game's mesh threads build this way, so a mesh's output stops being
    /// garbage the moment it is uploaded. They are then longer than their counts, which every
    /// reader already goes by, and the renderer hands them back through
    /// <see cref="ReturnPooledArrays"/> once the upload has copied them.
    /// </summary>
    internal bool ArraysPooled;

    /// <summary>
    /// Returns rented arrays to the pool and leaves this result holding empty ones, so nothing
    /// can read an array another mesh is already reusing. Owning thread, once per result.
    /// </summary>
    internal void ReturnPooledArrays()
    {
        if (!ArraysPooled) return;
        ArraysPooled = false;
        LodMeshArrays.Return(Xyz);
        LodMeshArrays.Return(Rgba);
        LodMeshArrays.Return(Indices);
        LodMeshArrays.Return(PackedOpaqueQuads);
        LodMeshArrays.Return(WaterXyz);
        LodMeshArrays.Return(WaterRgba);
        LodMeshArrays.Return(WaterIndices);
        Xyz = Array.Empty<float>();
        Rgba = Array.Empty<byte>();
        Indices = Array.Empty<int>();
        PackedOpaqueQuads = Array.Empty<uint>();
        WaterXyz = null;
        WaterRgba = null;
        WaterIndices = null;
    }

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
            Name = "dodenslandscapes-capture",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        captureThread.Start();

        // Mip construction is CPU-heavy but reads only immutable snapshots. Keeping it
        // off both capture and mesh queues prevents a propagation burst from delaying
        // newly explored columns or every visible mesh behind it.
        mipThread = new Thread(MipLoop)
        {
            Name = "dodenslandscapes-mip",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        mipThread.Start();

        meshThreads = new Thread[MeshThreadCount];
        for (int i = 0; i < meshThreads.Length; i++)
        {
            meshThreads[i] = new Thread(MeshLoop)
            {
                Name = "dodenslandscapes-mesh-" + i,
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
                MeshResults.Enqueue(LodMesher.BuildMesh(job, pooledOutput: true));
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
                        SnowStateObservedBits = new ulong[LodSection.SnowStateObservedWordCount],
                        SnowCoveredBits = new ulong[LodSection.SnowStateObservedWordCount],
                        ChildPalette = job.Palette,
                        Failed = true,
                    });
                }
            }

            if (!didWork) mipSignal.WaitOne(250);
        }
    }

    // ---- Capture: chunk column → RLE columns with raw block ids ----

    /// <summary>Internal so the fast checks can drive it over proxy chunks; nothing else calls it.</summary>
    internal static CaptureResult? Capture(CaptureJob job)
    {
        const int step = LodSection.ColumnStepBlocks;
        const int colsPerChunk = ChunkSize / step;

        int baseX = job.Cx * ChunkSize;
        int baseZ = job.Cz * ChunkSize;
        int sectionX = baseX / LodSection.SectionBlocks;
        int sectionZ = baseZ / LodSection.SectionBlocks;
        int colOffsetX = (baseX % LodSection.SectionBlocks) / step;
        int colOffsetZ = (baseZ % LodSection.SectionBlocks) / step;

        // Rented (0.4.131): the owning thread hands it back once the capture is applied.
        ulong[]?[] batch = LodMeshArrays.RentColumnBatch();
        var runs = new List<ulong>(24);
        bool anyColumn = false;
        long estimatedBytes = 0;

        // A chiselled block is recorded as the material that shows most (LodChiselStandIn),
        // here where runs are built, so a run splits wherever the material changes and the
        // palette never sees the chisel at all.
        Dictionary<int, (int Chisel, int StandIn)>? standIns =
            LodChiselStandIn.Resolve(job.Chisels, out int chiselsWithoutMaterial);
        Dictionary<int, int>? standInOriginals = null;
        int chiselStandIns = 0;
        Dictionary<int, string>? runVariants = null;
        ushort[]? blockLight = null;

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

                // The rain map ends under a reed; the reed is one block taller than the ground.
                if (startY < maxY && job.Chunks[(startY + 1) / ChunkSize] is { Disposed: false } above
                    && IsReed(job, above.UnpackAndReadBlock(
                        (((startY + 1) % ChunkSize) * ChunkSize + lz) * ChunkSize + lx,
                        BlockLayersAccess.Solid)))
                {
                    startY++;
                }

                int col = LodSection.ColumnIndex(colOffsetX + cx, colOffsetZ + cz);

                // The cell above the top block is the one its top face is lit from.
                int lightY = Math.Min(startY + 1, maxY);
                if (job.SnowStateObserved && job.Chunks[lightY / ChunkSize] is { Disposed: false } lit)
                {
                    ushort word = lit.Unpack_AndReadLight(
                        ((lightY % ChunkSize) * ChunkSize + lz) * ChunkSize + lx, out int saturation);
                    ushort light = LodBlockLight.FromChunkLight(word, saturation);
                    if (light != 0)
                        (blockLight ??= new ushort[LodSection.GridSize * LodSection.GridSize])[col] = light;
                }

                runs.Clear();
                int currentBlock = 0;
                int currentChisel = 0; // the chisel under the run's top block, when it stands in
                string? currentVariant = null;
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

                    // A reed in water shares its cell with the water, and that read answers the
                    // water. A run holds one block per cell, so the reed goes in the air above
                    // and the water keeps its cell: replacing it would cut a hole in the surface.
                    // Asked only where a run changes, so a deep sea costs one extra read.
                    if (job.Reeds != null && blockId != currentBlock && currentBlock == 0)
                    {
                        int solid = chunk.UnpackAndReadBlock(
                            ((y % ChunkSize) * ChunkSize + lz) * ChunkSize + lx,
                            BlockLayersAccess.Solid);
                        if (solid != blockId && IsReed(job, solid))
                        {
                            AddRun(runs, solid, y + 2, y + 1, col, 0, null,
                                ref standInOriginals, ref runVariants);
                        }
                    }

                    // Only while the block is still the chisel that was read: one replaced
                    // since the job was created keeps whatever is there now.
                    int chisel = 0;
                    if (standIns != null
                        && standIns.TryGetValue(LodChiselStandIn.IndexOf(lx, y, lz), out var standIn)
                        && standIn.Chisel == blockId)
                    {
                        chisel = blockId;
                        blockId = standIn.StandIn;
                        chiselStandIns++;
                    }

                    // Two trees touching are two runs of one block. A block replaced since the job
                    // was created keeps whatever is there now.
                    string? variant = job.Foliage != null
                        && job.Foliage.TryGetValue(LodChiselStandIn.IndexOf(lx, y, lz), out var foliage)
                        && foliage.BlockId == blockId
                            ? foliage.Variant
                            : null;

                    if (blockId != currentBlock || !ReferenceEquals(variant, currentVariant))
                    {
                        if (currentBlock != 0)
                        {
                            AddRun(runs, currentBlock, runTop, y + 1, col, currentChisel, currentVariant,
                                ref standInOriginals, ref runVariants);
                        }
                        currentBlock = blockId;
                        currentChisel = chisel;
                        currentVariant = variant;
                        runTop = y + 1;
                    }
                }

                if (!complete) continue;
                if (currentBlock != 0)
                {
                    AddRun(runs, currentBlock, runTop, 1, col, currentChisel, currentVariant,
                        ref standInOriginals, ref runVariants);
                }

                ulong[] capturedRuns = runs.ToArray();
                batch[col] = capturedRuns;
                estimatedBytes += capturedRuns.LongLength * sizeof(ulong);
                anyColumn = true;
            }
        }

        if (!anyColumn)
        {
            LodMeshArrays.ReturnColumnBatch(batch);
            return null;
        }

        return new CaptureResult
        {
            Epoch = job.Epoch,
            SectionKey = LodWorld.SectionKey(0, sectionX, sectionZ),
            Cx = job.Cx,
            Cz = job.Cz,
            SnowStateObserved = job.SnowStateObserved,
            RunsByColumn = batch,
            EstimatedBytes = estimatedBytes,
            ReadyAtMilliseconds = Environment.TickCount64,
            StandInOriginals = standInOriginals,
            RunVariants = runVariants,
            BlockLight = blockLight,
            ChiselStandIns = chiselStandIns,
            ChiselsWithoutMaterial = chiselsWithoutMaterial,
        };
    }

    static bool IsReed(CaptureJob job, int blockId) =>
        job.Reeds is { } reeds && blockId > 0 && blockId < reeds.Length && reeds[blockId];

    /// <summary>
    /// Append a run, remembering the chisel beneath its top block when that block stands in
    /// for one: the apply step reads the chisel's own flags for the column's snow. A run's
    /// palette variant travels the same way, runs holding only a block id.
    /// </summary>
    static void AddRun(List<ulong> runs, int blockId, int yTop, int yBottom, int col, int chisel,
        string? variant, ref Dictionary<int, int>? standInOriginals,
        ref Dictionary<int, string>? runVariants)
    {
        runs.Add(LodSection.PackRun(blockId, yTop, yBottom));
        if (chisel != 0)
        {
            (standInOriginals ??= new Dictionary<int, int>())[LodChiselStandIn.RunKey(col, yTop)] = chisel;
        }
        if (variant != null)
        {
            (runVariants ??= new Dictionary<int, string>())[LodChiselStandIn.RunKey(col, yTop)] = variant;
        }
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
