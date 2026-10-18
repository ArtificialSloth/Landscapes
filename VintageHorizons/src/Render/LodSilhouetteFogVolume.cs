namespace VintageHorizons;

/// <summary>
/// CPU-authored description of the seam fog. RGBA stores effective signed distance to the
/// rounded ownership frontier, frontier ground height, unsigned distance, and open-end fade.
/// The texture is only a compact spatial field; the actual medium is integrated in screen space.
/// <see cref="CoreBlocks"/> comes from how far the real 32-block staircase strays from that
/// rounded frontier, measured here, so the shader knows how wide a band must stay hidden.
/// </summary>
internal sealed class LodSilhouetteFogFieldArrays
{
    public required float[] Texels;

    /// <summary>The same texels as half floats, converted on the worker for the upload.</summary>
    public required ushort[] HalfTexels;
    public required int Width;
    public required int Height;
    public required int OriginBlockX;
    public required int OriginBlockZ;
    public required float TexelBlocks;
    public required float MinGroundY;
    public required float MaxGroundY;
    public required int BoundaryEdges;
    public required int PathCount;
    public required int OpenPathEnds;
    public required int PathSamples;
    public required int CenterlineSpans;
    public required int DiagonalCenterlineSpans;
    public required float CoreBlocks;
    public required float StaircaseDeviationBlocks;

    internal float SampleChannel(float worldX, float worldZ, int channel)
    {
        if ((uint)channel >= 4u) throw new ArgumentOutOfRangeException(nameof(channel));
        float px = (worldX - OriginBlockX) / TexelBlocks - 0.5f;
        float pz = (worldZ - OriginBlockZ) / TexelBlocks - 0.5f;
        int x0 = Math.Clamp((int)MathF.Floor(px), 0, Width - 1);
        int z0 = Math.Clamp((int)MathF.Floor(pz), 0, Height - 1);
        int x1 = Math.Min(x0 + 1, Width - 1);
        int z1 = Math.Min(z0 + 1, Height - 1);
        float tx = Math.Clamp(px - MathF.Floor(px), 0f, 1f);
        float tz = Math.Clamp(pz - MathF.Floor(pz), 0f, 1f);
        float north = Lerp(Texels[(z0 * Width + x0) * 4 + channel],
            Texels[(z0 * Width + x1) * 4 + channel], tx);
        float south = Lerp(Texels[(z1 * Width + x0) * 4 + channel],
            Texels[(z1 * Width + x1) * 4 + channel], tx);
        return Lerp(north, south, tz);
    }

    static float Lerp(float from, float to, float amount) => from + (to - from) * amount;

    /// <summary>The uploaded texels and their lattice, as the next residual reads them.</summary>
    internal LodSilhouetteFogLayer Layer => new(HalfTexels, Width, Height, OriginBlockX,
        OriginBlockZ, TexelBlocks, MinGroundY, MaxGroundY);
}

/// <summary>
/// A published field's texels and lattice, kept so the next field can carry its fog on while it
/// dissolves. The half floats are the uploaded texture's own; nothing is copied.
/// </summary>
internal sealed record LodSilhouetteFogLayer(ushort[] HalfTexels, int Width, int Height,
    int OriginBlockX, int OriginBlockZ, float TexelBlocks, float MinGroundY, float MaxGroundY);

/// <summary>
/// The fog a moved seam leaves behind (0.4.125), on the new field's lattice so the shader reads
/// both with one coordinate. RGB are the old field's (signed distance, ground, unsigned distance)
/// and alpha the weight it had when the build began: for fog shown until then its open-end
/// fade plus a head start for the time the build takes; for an earlier residual what it had
/// left. The shader subtracts the fade durations elapsed since the build began, so the old fog
/// thins, lowers and frays away instead of vanishing in one frame. Where the new field already
/// draws the same fog the weight is zero, so the shader does no second evaluation there.
/// </summary>
internal sealed class LodSilhouetteFogResidual
{
    public required LodSilhouetteFogLayer Layer;

    /// <summary>The largest weight, in fade durations: once elapsed, nothing remains.</summary>
    public required float MaxWeight;

    /// <summary>Texels that carry fog.</summary>
    public required int LiveTexels;
}

/// <summary>
/// Converts the exact 32-block ownership frontier into a smooth, directed distance field. The
/// field has no rendered surfaces: it tells a full-screen raymarch where the bank is, which side
/// is Farseer, the terrain height beneath it, and how true path endpoints taper away. Because
/// opacity is integrated once along the camera ray, curves cannot produce card intersections,
/// doubled alpha, triangular fans, or visible horizontal strata.
/// </summary>
internal static class LodSilhouetteFogVolume
{
    /// <summary>Eight blocks retains sub-chunk curvature without making wide fields excessive.</summary>
    public const float BaseFieldTexelBlocks = 8f;

    /// <summary>Bound both driver allocation and temporary CPU distance-transform storage.</summary>
    public const int MaxFieldTextureEdge = 2048;
    public const int MaxFieldTexels = 2048 * 2048;

    /// <summary>Half-float-safe range; the shader never needs a larger conservative skip.</summary>
    public const float StoredDistanceLimit = 2048f;

    /// <summary>
    /// The fog model a saved config's dials were tuned for. 2 is the natural bank of 0.4.123;
    /// 3 splits its density into seam and outer dials (0.4.124); 4 gives the seam its own
    /// shallow strip and makes the bank see-through (0.4.125). A file written for model 1 has
    /// every fog dial reset once, because the same numbers no longer describe the same fog; a
    /// model-2 or model-3 file has only its densities reset, the dials whose meaning changed.
    /// </summary>
    public const int FogModel = 4;

    // The shader's medium, mirrored here for the checks (lodsilhouettefogvolume.fsh). Optical
    // depths are quoted through one vertical column. The strip hides the seam; the bank is
    // scenery, thin enough to see Farseer through (scripts/fog-mockup measures both).

    /// <summary>
    /// Column optical depth of the seam strip over its band at full seam density, whatever its
    /// depth or the noise: 3.5 hides 97% of the seam straight down, and every slanted view
    /// crosses more of it.
    /// </summary>
    public const float StripOpticalDepth = 3.5f;

    /// <summary>The strip's vertical profile's integral in units of its local top.</summary>
    public const float StripColumnFraction = 0.95f;

    /// <summary>
    /// Blocks over which the strip frays out past its band, and its edge's wander: up to twice
    /// this, broad and billow-sized, which the band's margin absorbs.
    /// </summary>
    public const float StripFalloffBlocks = 12f;
    public const float StripWanderBlocks = 3f;

    /// <summary>
    /// Near where a ray lands the strip rests on that surface, within this reach of the
    /// sampled seam height; from the near to the far distance it returns to the sampled height.
    /// </summary>
    public const float StripAnchorNearBlocks = 24f;
    public const float StripAnchorFarBlocks = 64f;
    public const float StripAnchorReachBlocks = 16f;

    /// <summary>
    /// The curtain (0.4.149), with empty sky beyond the cached terrain's edge: optical depth per
    /// block at full seam density and its thinnest billow's three quarters, over the strip's
    /// band and its margin. The wall strays across the band, so even its outermost stretch has
    /// the margin and the fringe in front of it (<see cref="CurtainCoverDepth"/>).
    /// </summary>
    public const float CurtainExtinction = 0.5f;

    /// <summary>
    /// Past the band the curtain frays out over this many blocks in tendrils, gone by 3.5 of
    /// them; its tongues reach out up to twice the wander and never in.
    /// </summary>
    public const float CurtainFalloffBlocks = 10f;
    public const float CurtainWanderBlocks = 5f;

    /// <summary>Where it pours over the lip it bulges out this far, settling over this depth.</summary>
    public const float CurtainLipBlocks = 10f;
    public const float CurtainLipDepthBlocks = 14f;

    /// <summary>It starts this far above the edge's ground and fades out this far below the wall's foot.</summary>
    public const float CurtainLipRiseBlocks = 4f;
    public const float CurtainBottomFadeBlocks = 16f;

    /// <summary>Inside the cached terrain it ends this far past the band, in the rock.</summary>
    public const float CurtainInnerFadeBlocks = 4f;

    /// <summary>
    /// Its tendrils are this many times taller than wide. Much taller and they hide their own
    /// motion: a streak sliding down its length barely changes (0.4.150).
    /// </summary>
    public const float CurtainStreakStretch = 3f;

    /// <summary>
    /// The tendrils flow as two copies, each travelling this far before it fades out and starts
    /// again while the other is at its strongest (a flow map).
    /// </summary>
    public const float CurtainFlowCycleBlocks = 24f;

    /// <summary>Over the lip they flow outward; this far below it they fall straight down.</summary>
    public const float CurtainFlowTurnBlocks = 10f;

    /// <summary>
    /// The tendrils' remap, tighter than the body's so wisps and gaps are distinct enough for
    /// their flow to show on a curtain too dense to see through; and how much the gaps are
    /// shaded, as optical depth toward the sun.
    /// </summary>
    public const float CurtainStreakLow = 0.3f;
    public const float CurtainStreakHigh = 0.7f;
    public const float CurtainGapShade = 1.5f;

    /// <summary>
    /// Mean optical depth of a level crossing through the bank's ground layer, square to the
    /// seam, at full density: how see-through it is from the ground. Its density per block does
    /// not depend on its height, so a taller bank is deeper from above, not from the side.
    /// </summary>
    public const float BankCrossingOpticalDepth = 1f;

    /// <summary>The faint halo that carries the fade past the last strands, the same way.</summary>
    public const float HazeCrossingOpticalDepth = 0.15f;

    /// <summary>Mean eroded noise density inside the core, so the bank meets its target.</summary>
    public const float BankMeanDensity = 0.6f;

    /// <summary>Exponential fade lengths beyond the core, as fractions of that side's reach.</summary>
    public const float ShoulderFraction = 0.4f;
    public const float HazeFraction = 0.6f;

    /// <summary>The Farseer side reaches this much further than the spread dial.</summary>
    public const float FarseerReachScale = 1.6f;

    /// <summary>
    /// Vertical profile, as fractions of the local bank top: a dense ground layer, then an
    /// exponential thinning with this scale height, under a soft eroded ceiling.
    /// </summary>
    public const float GroundLayerFraction = 0.3f;
    public const float ScaleHeightFraction = 0.42f;

    /// <summary>
    /// The bank's core covers the measured staircase plus this margin, within these bounds: a
    /// slanted view through a tall bank crosses mostly its shoulder near the core's edge (G247).
    /// </summary>
    public const float CoreMarginBlocks = 10f;
    public const float MinCoreBlocks = 12f;
    public const float MaxCoreBlocks = 48f;

    /// <summary>
    /// The strip covers the measured staircase plus this smaller margin. A layer a few blocks
    /// deep is crossed close to where a ray lands, so a slanted view of the band's edge still
    /// passes through the band.
    /// </summary>
    public const float StripMarginBlocks = 6f;
    public const float MinStripHalfBlocks = 8f;
    public const float MaxStripHalfBlocks = 44f;

    /// <summary>
    /// A true open path grows a rounded, fading cap past its endpoint. Width contracts the bank
    /// laterally while Profile guarantees that the final sample remains invisible under wobble.
    /// </summary>
    public static readonly (float Along, float Width, float Profile)[] SweptEnd =
    {
        (0.38f, 0.925f, 0.72f),
        (0.72f, 0.690f, 0.30f),
        (1.00f, 0.120f, 0.00f),
    };

    public static int ColumnsPerOpenEnd => SweptEnd.Length;

    public const float MinStrength = 0f;
    public const float MaxStrength = 2f;

    /// <summary>
    /// Density of the bank and its haze, everywhere above and around the strip. At 100% a
    /// level view across the bank still shows the terrain behind it, veiled.
    /// </summary>
    public const float DefaultStrength = 1f;

    /// <summary>Density of the seam strip. At 100% it hides the staircase from every angle.</summary>
    public const float DefaultSeamStrength = 1f;

    /// <summary>
    /// How deep the seam strip lies over the ground. The owner asked for no more than 2-3
    /// blocks (2026-09-21): the seam itself is a ground-level edge.
    /// </summary>
    public const float MinStripHeightBlocks = 1f;
    public const float MaxStripHeightBlocks = 16f;
    public const float DefaultStripHeightBlocks = 3f;

    /// <summary>
    /// Seconds over which fog left behind by a moved seam dissolves, instead of vanishing in
    /// one frame when the field is replaced. Zero restores the instant change.
    /// </summary>
    public const float MinFadeSeconds = 0f;
    public const float MaxFadeSeconds = 3f;
    public const float DefaultFadeSeconds = 0.8f;
    // The remaining defaults are the owner's in-game tuning of 0.4.125 (2026-09-21).
    public const float MinCoverage = 0f;
    public const float MaxCoverage = 1f;
    public const float DefaultCoverage = 1f;
    public const float MinSpillBlocks = 8f;
    public const float MaxSpillBlocks = 128f;
    public const float DefaultSpillBlocks = 36f;
    public const float MinHeightBlocks = 16f;
    public const float MaxHeightBlocks = 192f;
    public const float DefaultHeightBlocks = 24f;
    public const float MinFloorDepthBlocks = 0f;
    public const float MaxFloorDepthBlocks = 64f;
    public const float DefaultFloorDepthBlocks = 20f;
    public const float MinBillowSizeBlocks = 12f;
    public const float MaxBillowSizeBlocks = 160f;
    public const float DefaultBillowSizeBlocks = 12f;
    public const float MinDriftSpeed = 0f;
    public const float MaxDriftSpeed = 3f;
    public const float DefaultDriftSpeed = 1f;
    public const float MinEdgeSoftness = 0f;
    public const float MaxEdgeSoftness = 1f;
    public const float DefaultEdgeSoftness = 0.35f;
    public const float MinLayerDetail = 0f;
    public const float MaxLayerDetail = 1f;
    public const float DefaultLayerDetail = 0.52f;

    const int InfiniteCellDistance = 1_000_000_000;

    readonly record struct GridPoint(int X, int Z);
    readonly record struct PlanPoint(float X, float Z);
    readonly record struct GridPath(List<GridPoint> Points, bool Closed);
    internal readonly record struct FogColumn(
        float X, float Z, float Y, float Scale, float Width, float NormalX, float NormalZ);
    internal readonly record struct FogPath(
        List<FogColumn> Columns, int CenterlineSpans, int DiagonalCenterlineSpans, bool Closed);
    internal readonly record struct FogSample(
        float X, float Z, float Y, float Scale, float Width, float NormalX, float NormalZ);

    /// <summary>
    /// The temporary storage of one field build, kept by the renderer so a rebuild while
    /// exploring stops allocating ~50 MB of large arrays (0.4.131; the owner's 0.4.130 log
    /// measured 94-112 MB/s from seam-fog builds alone). One build at a time may hold it: the
    /// renderer hands it out exclusively, and a build that finds it taken allocates as before.
    /// Arrays only ever grow and are used up to the current field's size, so nothing a
    /// previous build left behind is read: every element used is written first.
    /// </summary>
    internal sealed class Scratch
    {
        internal readonly List<FogSample> Samples = new();
        int[] seedByCell = Array.Empty<int>();
        int[] verticalDistance = Array.Empty<int>();
        int[] verticalSeed = Array.Empty<int>();
        float[] texels = Array.Empty<float>();
        int[] candidates = Array.Empty<int>();
        float[] intersections = Array.Empty<float>();

        internal int[] SeedByCell(int count) => Grow(ref seedByCell, count);
        internal int[] VerticalDistance(int count) => Grow(ref verticalDistance, count);
        internal int[] VerticalSeed(int count) => Grow(ref verticalSeed, count);
        internal float[] Texels(int count) => Grow(ref texels, count);
        internal int[] Candidates(int count) => Grow(ref candidates, count);
        internal float[] Intersections(int count) => Grow(ref intersections, count);

        static T[] Grow<T>(ref T[] array, int count)
        {
            if (array.Length < count) array = new T[PooledLength(count)];
            return array;
        }
    }

    /// <summary>
    /// Half-float arrays the renderer returns once a newer field has replaced them on screen:
    /// the published field's texels and its residual's. Thread-safe, because a build rents on
    /// a worker while the render thread returns. A pooled array can be longer than its field;
    /// every reader goes by the field's width and height, never by the array's length.
    /// </summary>
    internal sealed class HalfPool
    {
        const int MaxSpare = 4;
        readonly object gate = new();
        readonly List<ushort[]> spare = new();

        /// <summary>Rents reused from the pool, and rents that had to allocate.</summary>
        internal long Reused, Allocated;

        internal ushort[] Rent(int length)
        {
            lock (gate)
            {
                int best = -1;
                for (int i = 0; i < spare.Count; i++)
                {
                    if (spare[i].Length < length) continue;
                    if (best < 0 || spare[i].Length < spare[best].Length) best = i;
                }
                if (best >= 0)
                {
                    ushort[] found = spare[best];
                    spare.RemoveAt(best);
                    Reused++;
                    return found;
                }
                Allocated++;
            }
            return new ushort[PooledLength(length)];
        }

        internal void Return(ushort[]? array)
        {
            if (array == null || array.Length == 0) return;
            lock (gate)
            {
                foreach (ushort[] held in spare)
                {
                    if (ReferenceEquals(held, array)) return;
                }
                spare.Add(array);
                if (spare.Count <= MaxSpare) return;
                int smallest = 0;
                for (int i = 1; i < spare.Count; i++)
                {
                    if (spare[i].Length < spare[smallest].Length) smallest = i;
                }
                spare.RemoveAt(smallest);
            }
        }

        /// <summary>Lets the memory go, for when the fog is switched off or stopped.</summary>
        internal void Clear()
        {
            lock (gate) spare.Clear();
        }
    }

    /// <summary>
    /// A pooled array's size, with headroom so a field that grows a little as the seam moves
    /// still fits the array the last one used.
    /// </summary>
    internal static int PooledLength(int count) => LodMeshArrays.PooledLength(count);

    internal sealed class Prepared
    {
        internal required List<FogPath> Paths;
        internal required LodSilhouetteBoundaryEdge[] Boundaries;
        internal required float SpillBlocks;
        internal required float MinimumTexelBlocks;
        internal required float MinX;
        internal required float MinZ;
        internal required float MaxX;
        internal required float MaxZ;
        internal required float MinGround;
        internal required float MaxGround;
        internal required int BoundaryEdges;
        internal required int OpenPathEnds;
        internal required int CenterlineSpans;
        internal required int DiagonalCenterlineSpans;
    }

    public static LodSilhouetteFogFieldArrays Build(
        LodSilhouetteSeamMask mask, Func<int, int, float> heightAtGrid,
        float spillBlocks = DefaultSpillBlocks)
        => Build(Prepare(mask, heightAtGrid, spillBlocks));

    /// <summary>
    /// The field's texel size for everything within a reach. It depends on the reach and spread
    /// only, never on the momentary tile window, so moving about cannot flip the field between
    /// resolutions and nudge the fog.
    /// </summary>
    internal static float TexelBlocksForReach(float reachBlocks, float spillBlocks)
    {
        float extent = 2f * (MathF.Max(0f, reachBlocks) + Silhouette.LodSilhouetteTile.TileBlocks
            + FogReach(spillBlocks, MaxEdgeSoftness, MaxCoreBlocks, MaxStripHalfBlocks)
            + BaseFieldTexelBlocks * 2f);
        float texel = BaseFieldTexelBlocks;
        while (extent / texel > MaxFieldTextureEdge && texel < 4096f) texel *= 2f;
        return texel;
    }

    /// <summary>
    /// Owning-thread snapshot. This is the only phase that calls back into live GPU-mesh metadata;
    /// the expensive rasterization and distance transform consume this immutable result on a worker.
    /// </summary>
    internal static Prepared Prepare(
        LodSilhouetteSeamMask mask, Func<int, int, float> heightAtGrid,
        float spillBlocks = DefaultSpillBlocks, float minimumTexelBlocks = BaseFieldTexelBlocks)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(heightAtGrid);
        spillBlocks = Dial(spillBlocks, DefaultSpillBlocks, MinSpillBlocks, MaxSpillBlocks);

        List<GridPath> traced = TracePaths(mask.Boundaries);
        var paths = new List<FogPath>(traced.Count);
        int openPathEnds = 0;
        int centerlineSpans = 0;
        int diagonalCenterlineSpans = 0;
        foreach (GridPath path in traced)
        {
            FogPath geometry = BuildPath(path, heightAtGrid, spillBlocks);
            if (geometry.Columns.Count < 2) continue;
            paths.Add(geometry);
            if (!geometry.Closed) openPathEnds += 2;
            centerlineSpans = checked(centerlineSpans + geometry.CenterlineSpans);
            diagonalCenterlineSpans = checked(
                diagonalCenterlineSpans + geometry.DiagonalCenterlineSpans);
        }
        if (paths.Count == 0)
            throw new InvalidOperationException("a seam-fog field requires at least one path");

        float minX = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
        float minGround = float.PositiveInfinity, maxGround = float.NegativeInfinity;
        foreach (FogPath path in paths)
        {
            foreach (FogColumn column in path.Columns)
            {
                minX = MathF.Min(minX, column.X);
                minZ = MathF.Min(minZ, column.Z);
                maxX = MathF.Max(maxX, column.X);
                maxZ = MathF.Max(maxZ, column.Z);
                minGround = MathF.Min(minGround, column.Y);
                maxGround = MathF.Max(maxGround, column.Y);
            }
        }

        return new Prepared
        {
            Paths = paths,
            Boundaries = mask.Boundaries.ToArray(),
            SpillBlocks = spillBlocks,
            MinimumTexelBlocks = MathF.Max(BaseFieldTexelBlocks, minimumTexelBlocks),
            MinX = minX,
            MinZ = minZ,
            MaxX = maxX,
            MaxZ = maxZ,
            MinGround = minGround,
            MaxGround = maxGround,
            BoundaryEdges = mask.Boundaries.Count,
            OpenPathEnds = openPathEnds,
            CenterlineSpans = centerlineSpans,
            DiagonalCenterlineSpans = diagonalCenterlineSpans,
        };
    }

    /// <param name="scratch">
    /// Temporary storage to reuse, held exclusively by this build; null allocates. The returned
    /// field's <see cref="LodSilhouetteFogFieldArrays.Texels"/> then live in it, so they are
    /// valid only until the scratch is handed to the next build.
    /// </param>
    /// <param name="halves">Where the field's half-float texels are rented; null allocates.</param>
    internal static LodSilhouetteFogFieldArrays Build(
        Prepared prepared, CancellationToken cancellationToken = default,
        Scratch? scratch = null, HalfPool? halves = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        cancellationToken.ThrowIfCancellationRequested();
        List<FogPath> paths = prepared.Paths;
        float spillBlocks = prepared.SpillBlocks;
        float minX = prepared.MinX, minZ = prepared.MinZ;
        float maxX = prepared.MaxX, maxZ = prepared.MaxZ;
        // The march stops where the field ends, so it must hold the whole fade: the widest
        // core, the Farseer shoulder, and the rim's largest wander, or the strip's fringe.
        float padding = FogReach(spillBlocks, MaxEdgeSoftness, MaxCoreBlocks, MaxStripHalfBlocks)
            + BaseFieldTexelBlocks * 2f;
        float texelBlocks = prepared.MinimumTexelBlocks;
        int originX, originZ, width, height;
        while (true)
        {
            originX = checked((int)(MathF.Floor((minX - padding) / texelBlocks) * texelBlocks));
            originZ = checked((int)(MathF.Floor((minZ - padding) / texelBlocks) * texelBlocks));
            width = Math.Max(2, checked((int)MathF.Ceiling(
                (maxX + padding - originX) / texelBlocks)));
            height = Math.Max(2, checked((int)MathF.Ceiling(
                (maxZ + padding - originZ) / texelBlocks)));
            if (width <= MaxFieldTextureEdge && height <= MaxFieldTextureEdge
                && (long)width * height <= MaxFieldTexels) break;
            texelBlocks *= 2f;
            if (texelBlocks > 4096f)
                throw new InvalidOperationException("the seam-fog field exceeds its safe texture bounds");
        }

        List<FogSample> samples = RasterSamples(paths, texelBlocks, scratch?.Samples);
        cancellationToken.ThrowIfCancellationRequested();
        int cellCount = checked(width * height);
        int texelCount = checked(cellCount * 4);
        int[] seedByCell = scratch?.SeedByCell(cellCount) ?? new int[cellCount];
        Array.Fill(seedByCell, -1, 0, cellCount);
        for (int seed = 0; seed < samples.Count; seed++)
        {
            if ((seed & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            FogSample sample = samples[seed];
            int x = Math.Clamp((int)MathF.Floor((sample.X - originX) / texelBlocks), 0, width - 1);
            int z = Math.Clamp((int)MathF.Floor((sample.Z - originZ) / texelBlocks), 0, height - 1);
            int cell = z * width + x;
            int previous = seedByCell[cell];
            if (previous < 0 || DistanceToCellCenterSquared(sample, x, z) <
                    DistanceToCellCenterSquared(samples[previous], x, z))
                seedByCell[cell] = seed;
        }

        // First pass of the exact squared Euclidean distance transform. Along a column all seeds
        // have zero cost, so two linear scans produce its nearest occupied row and squared cost.
        int[] verticalDistance = scratch?.VerticalDistance(cellCount) ?? new int[cellCount];
        int[] verticalSeed = scratch?.VerticalSeed(cellCount) ?? new int[cellCount];
        Array.Fill(verticalDistance, InfiniteCellDistance, 0, cellCount);
        Array.Fill(verticalSeed, -1, 0, cellCount);
        for (int x = 0; x < width; x++)
        {
            if ((x & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            int last = -1;
            for (int z = 0; z < height; z++)
            {
                int cell = z * width + x;
                if (seedByCell[cell] >= 0) last = z;
                if (last < 0) continue;
                int d = z - last;
                verticalDistance[cell] = d * d;
                verticalSeed[cell] = seedByCell[last * width + x];
            }
            last = -1;
            for (int z = height - 1; z >= 0; z--)
            {
                int cell = z * width + x;
                if (seedByCell[cell] >= 0) last = z;
                if (last < 0) continue;
                int d = last - z;
                int cost = d * d;
                if (cost >= verticalDistance[cell]) continue;
                verticalDistance[cell] = cost;
                verticalSeed[cell] = seedByCell[last * width + x];
            }
        }

        // The horizontal lower envelope completes Felzenszwalb's linear-time 2D transform. It
        // carries the path sample identity through the transform, so all four channels describe
        // the same nearest point and remain coherent when the GPU filters them.
        // Every texel is written below, and every candidate and intersection is written before
        // it is read, so reused storage needs no clearing.
        float[] texels = scratch?.Texels(texelCount) ?? new float[texelCount];
        int[] candidates = scratch?.Candidates(width) ?? new int[width];
        float[] intersections = scratch?.Intersections(width + 1) ?? new float[width + 1];
        for (int row = 0; row < height; row++)
        {
            if ((row & 15) == 0) cancellationToken.ThrowIfCancellationRequested();
            int candidateCount = 0;
            for (int q = 0; q < width; q++)
            {
                int cost = verticalDistance[row * width + q];
                if (cost >= InfiniteCellDistance) continue;
                float intersection = float.NegativeInfinity;
                while (candidateCount > 0)
                {
                    int p = candidates[candidateCount - 1];
                    int previousCost = verticalDistance[row * width + p];
                    intersection = ((cost + (long)q * q) - (previousCost + (long)p * p))
                        / (2f * (q - p));
                    if (intersection > intersections[candidateCount - 1]) break;
                    candidateCount--;
                }
                if (candidateCount == 0) intersection = float.NegativeInfinity;
                candidates[candidateCount] = q;
                intersections[candidateCount] = intersection;
                candidateCount++;
                intersections[candidateCount] = float.PositiveInfinity;
            }
            if (candidateCount == 0)
                throw new InvalidOperationException("the seam-fog distance transform lost every seed");

            int envelope = 0;
            for (int x = 0; x < width; x++)
            {
                while (envelope + 1 < candidateCount && intersections[envelope + 1] < x)
                    envelope++;
                int sourceColumn = candidates[envelope];
                int seed = verticalSeed[row * width + sourceColumn];
                if (seed < 0)
                    throw new InvalidOperationException("the seam-fog distance transform lost a seed identity");
                FogSample nearest = samples[seed];
                float worldX = originX + (x + 0.5f) * texelBlocks;
                float worldZ = originZ + (row + 0.5f) * texelBlocks;
                float dx = worldX - nearest.X;
                float dz = worldZ - nearest.Z;
                float unsigned = MathF.Sqrt(dx * dx + dz * dz);
                float signed = (dx * nearest.NormalX + dz * nearest.NormalZ)
                    / MathF.Max(0.08f, nearest.Width);
                int at = (row * width + x) * 4;
                texels[at] = Math.Clamp(signed, -StoredDistanceLimit, StoredDistanceLimit);
                texels[at + 1] = nearest.Y;
                texels[at + 2] = MathF.Min(unsigned, StoredDistanceLimit);
                texels[at + 3] = Math.Clamp(nearest.Scale, 0f, 1f);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        ushort[] halfTexels = halves?.Rent(texelCount) ?? new ushort[texelCount];
        for (int i = 0; i < texelCount; i++)
            halfTexels[i] = BitConverter.HalfToUInt16Bits((Half)texels[i]);
        var field = new LodSilhouetteFogFieldArrays
        {
            Texels = texels,
            HalfTexels = halfTexels,
            Width = width,
            Height = height,
            OriginBlockX = originX,
            OriginBlockZ = originZ,
            TexelBlocks = texelBlocks,
            MinGroundY = prepared.MinGround,
            MaxGroundY = prepared.MaxGround,
            BoundaryEdges = prepared.BoundaryEdges,
            PathCount = paths.Count,
            OpenPathEnds = prepared.OpenPathEnds,
            PathSamples = samples.Count,
            CenterlineSpans = prepared.CenterlineSpans,
            DiagonalCenterlineSpans = prepared.DiagonalCenterlineSpans,
            CoreBlocks = MinCoreBlocks,
            StaircaseDeviationBlocks = 0f,
        };

        // How far the real staircase strays from the rounded centreline, in the same lateral
        // distance the shader reads. Faded open ends are excluded: they taper on purpose.
        float deviation = 0f;
        foreach (LodSilhouetteBoundaryEdge edge in prepared.Boundaries)
        {
            for (int part = 0; part <= 2; part++)
            {
                float t = part * 0.5f;
                float x = (edge.StartGridX + (edge.EndGridX - edge.StartGridX) * t)
                    * LodSilhouetteSeamMask.CellBlocks;
                float z = (edge.StartGridZ + (edge.EndGridZ - edge.StartGridZ) * t)
                    * LodSilhouetteSeamMask.CellBlocks;
                if (field.SampleChannel(x, z, 3) < 0.99f) continue;
                deviation = MathF.Max(deviation, MathF.Abs(field.SampleChannel(x, z, 0)));
            }
        }
        field.StaircaseDeviationBlocks = deviation;
        field.CoreBlocks = CoreHalfWidth(deviation);
        return field;

        float DistanceToCellCenterSquared(FogSample sample, int x, int z)
        {
            float dx = sample.X - (originX + (x + 0.5f) * texelBlocks);
            float dz = sample.Z - (originZ + (z + 0.5f) * texelBlocks);
            return dx * dx + dz * dz;
        }
    }

    static List<FogSample> RasterSamples(List<FogPath> paths, float texelBlocks,
        List<FogSample>? reuse = null)
    {
        List<FogSample> result = reuse ?? new List<FogSample>();
        result.Clear();
        float maxStep = MathF.Max(1f, texelBlocks * 0.45f);
        foreach (FogPath path in paths)
        {
            List<FogColumn> columns = path.Columns;
            result.Add(ToSample(columns[0]));
            for (int i = 0; i + 1 < columns.Count; i++)
            {
                FogColumn start = columns[i], end = columns[i + 1];
                float dx = end.X - start.X, dz = end.Z - start.Z;
                int steps = Math.Max(1,
                    (int)MathF.Ceiling(MathF.Sqrt(dx * dx + dz * dz) / maxStep));
                for (int step = 1; step <= steps; step++)
                {
                    float t = step / (float)steps;
                    PlanPoint normal = Unit(
                        Lerp(start.NormalX, end.NormalX, t),
                        Lerp(start.NormalZ, end.NormalZ, t));
                    result.Add(new FogSample(
                        Lerp(start.X, end.X, t), Lerp(start.Z, end.Z, t),
                        Lerp(start.Y, end.Y, t), Lerp(start.Scale, end.Scale, t),
                        Lerp(start.Width, end.Width, t), normal.X, normal.Z));
                }
            }
        }
        return result;

        static FogSample ToSample(FogColumn column) => new(
            column.X, column.Z, column.Y, column.Scale, column.Width,
            column.NormalX, column.NormalZ);
    }

    static FogPath BuildPath(
        GridPath path, Func<int, int, float> heightAtGrid, float spillBlocks)
    {
        // Every point of the curve is a weighted average of the staircase within a fixed arc
        // window around it, then corner-cut once, so the curve anywhere depends only on the
        // ownership nearby. A change far along the frontier, or where the tile window ends,
        // cannot bend it. 0.4.123's whole-path simplification could, which moved the fog as
        // the player moved.
        List<PlanPoint> centreline = SmoothLocally(path, SmoothingWindowBlocks(spillBlocks));
        centreline = Smooth(centreline, path.Closed, 0.25f);
        float fieldStep = Math.Clamp(spillBlocks * 0.25f, 6f, 12f);
        centreline = Resample(centreline, path.Closed, fieldStep);

        int diagonalSpans = 0;
        for (int i = 0; i + 1 < centreline.Count; i++)
        {
            float dx = Math.Abs(centreline[i + 1].X - centreline[i].X);
            float dz = Math.Abs(centreline[i + 1].Z - centreline[i].Z);
            if (dx > 0.001f && dz > 0.001f) diagonalSpans++;
        }

        var centres = new List<(PlanPoint Point, float Y, float Scale, float Width)>(
            centreline.Count + (path.Closed ? 0 : 2 * ColumnsPerOpenEnd));
        foreach (PlanPoint point in centreline)
            centres.Add((point, HeightAt(point, heightAtGrid), 1f, 1f));

        if (!path.Closed && centreline.Count >= 2)
        {
            PlanPoint start = centreline[0];
            PlanPoint startTangent = Unit(centreline[1].X - start.X, centreline[1].Z - start.Z);
            float startY = centres[0].Y;
            for (int i = 0; i < SweptEnd.Length; i++)
            {
                (float along, float width, float profile) = SweptEnd[i];
                centres.Insert(0, (new PlanPoint(
                    start.X - startTangent.X * spillBlocks * along,
                    start.Z - startTangent.Z * spillBlocks * along),
                    startY, profile, width));
            }

            int endAt = centres.Count - 1;
            PlanPoint end = centreline[^1];
            PlanPoint endTangent = Unit(
                end.X - centreline[^2].X, end.Z - centreline[^2].Z);
            float endY = centres[endAt].Y;
            foreach ((float along, float width, float profile) in SweptEnd)
                centres.Add((new PlanPoint(
                    end.X + endTangent.X * spillBlocks * along,
                    end.Z + endTangent.Z * spillBlocks * along),
                    endY, profile, width));
        }

        var columns = new List<FogColumn>(centres.Count);
        for (int i = 0; i < centres.Count; i++)
        {
            int previous = i == 0 ? (path.Closed ? centres.Count - 2 : 0) : i - 1;
            int next = i == centres.Count - 1 ? (path.Closed ? 1 : i) : i + 1;
            PlanPoint tangent = Unit(
                centres[next].Point.X - centres[previous].Point.X,
                centres[next].Point.Z - centres[previous].Point.Z);
            // Directed ownership edges keep Farseer on the right-hand normal.
            float normalX = -tangent.Z;
            float normalZ = tangent.X;
            var centre = centres[i];
            columns.Add(new FogColumn(
                centre.Point.X, centre.Point.Z, centre.Y, centre.Scale, centre.Width,
                normalX, normalZ));
        }

        return new FogPath(
            columns, Math.Max(0, centreline.Count - 1), diagonalSpans, path.Closed);
    }

    /// <summary>Trace directed ownership edges into maximal continuous open paths and loops.</summary>
    static List<GridPath> TracePaths(IReadOnlyList<LodSilhouetteBoundaryEdge> edges)
    {
        var outgoing = new Dictionary<GridPoint, List<int>>();
        var incoming = new Dictionary<GridPoint, int>();
        for (int i = 0; i < edges.Count; i++)
        {
            LodSilhouetteBoundaryEdge edge = edges[i];
            var start = new GridPoint(edge.StartGridX, edge.StartGridZ);
            var end = new GridPoint(edge.EndGridX, edge.EndGridZ);
            if (!outgoing.TryGetValue(start, out List<int>? starts))
                outgoing[start] = starts = new List<int>();
            starts.Add(i);
            incoming[end] = incoming.GetValueOrDefault(end) + 1;
        }

        var used = new bool[edges.Count];
        var paths = new List<GridPath>();
        for (int i = 0; i < edges.Count; i++)
        {
            var start = new GridPoint(edges[i].StartGridX, edges[i].StartGridZ);
            if (incoming.GetValueOrDefault(start) == 0) Trace(i);
        }
        // Closed loops and the second arm of a checkerboard saddle have no zero-indegree start.
        for (int i = 0; i < edges.Count; i++) Trace(i);
        return paths;

        void Trace(int first)
        {
            if (used[first]) return;
            LodSilhouetteBoundaryEdge firstEdge = edges[first];
            var firstPoint = new GridPoint(firstEdge.StartGridX, firstEdge.StartGridZ);
            var points = new List<GridPoint> { firstPoint };
            int edgeAt = first;
            while (!used[edgeAt])
            {
                used[edgeAt] = true;
                LodSilhouetteBoundaryEdge edge = edges[edgeAt];
                var end = new GridPoint(edge.EndGridX, edge.EndGridZ);
                points.Add(end);
                int inX = edge.EndGridX - edge.StartGridX;
                int inZ = edge.EndGridZ - edge.StartGridZ;
                if (!outgoing.TryGetValue(end, out List<int>? candidates)) break;
                int next = -1;
                int bestDot = int.MinValue;
                foreach (int candidate in candidates)
                {
                    if (used[candidate]) continue;
                    LodSilhouetteBoundaryEdge possible = edges[candidate];
                    int dot = inX * (possible.EndGridX - possible.StartGridX)
                        + inZ * (possible.EndGridZ - possible.StartGridZ);
                    if (dot > bestDot || dot == bestDot && candidate < next)
                    {
                        bestDot = dot;
                        next = candidate;
                    }
                }
                if (next < 0) break;
                edgeAt = next;
            }

            bool closed = points.Count > 2 && points[^1] == points[0];
            paths.Add(new GridPath(points, closed));
        }
    }

    /// <summary>The arc window each curve point averages over: wider for a wider bank.</summary>
    internal static float SmoothingWindowBlocks(float spillBlocks) =>
        Math.Clamp(Dial(spillBlocks, DefaultSpillBlocks, MinSpillBlocks, MaxSpillBlocks) * 1.5f,
            48f, 144f);

    /// <summary>
    /// A biweight moving average over the staircase's own vertices. Every traced edge is one
    /// 32-block cell, so the window is a fixed count of neighbours on each side; a closed loop
    /// wraps, and an open path's ends simply see fewer neighbours.
    /// </summary>
    static List<PlanPoint> SmoothLocally(GridPath path, float windowBlocks)
    {
        List<GridPoint> points = path.Points;
        int count = path.Closed ? points.Count - 1 : points.Count;
        var result = new List<PlanPoint>(count + 1);
        if (count < 2)
        {
            foreach (GridPoint point in points)
                result.Add(new PlanPoint(point.X * LodSilhouetteSeamMask.CellBlocks,
                    point.Z * LodSilhouetteSeamMask.CellBlocks));
            return result;
        }
        int reach = Math.Max(1, (int)MathF.Ceiling(windowBlocks / LodSilhouetteSeamMask.CellBlocks));
        for (int i = 0; i < count; i++)
        {
            float sumX = 0f, sumZ = 0f, sumWeight = 0f;
            for (int k = -reach; k <= reach; k++)
            {
                int j = i + k;
                if (path.Closed) j = ((j % count) + count) % count;
                else if (j < 0 || j >= count) continue;
                float t = MathF.Abs(k) * LodSilhouetteSeamMask.CellBlocks / windowBlocks;
                if (t >= 1f) continue;
                float weight = (1f - t * t) * (1f - t * t);
                sumX += weight * points[j].X;
                sumZ += weight * points[j].Z;
                sumWeight += weight;
            }
            result.Add(new PlanPoint(sumX / sumWeight * LodSilhouetteSeamMask.CellBlocks,
                sumZ / sumWeight * LodSilhouetteSeamMask.CellBlocks));
        }
        if (path.Closed) result.Add(result[0]);
        return result;
    }

    static List<PlanPoint> Resample(List<PlanPoint> points, bool closed, float maxStep)
    {
        if (points.Count < 2) return points;
        var result = new List<PlanPoint> { points[0] };
        for (int i = 0; i + 1 < points.Count; i++)
        {
            PlanPoint start = points[i], end = points[i + 1];
            float dx = end.X - start.X, dz = end.Z - start.Z;
            int steps = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(dx * dx + dz * dz) / maxStep));
            for (int step = 1; step <= steps; step++)
            {
                float t = step / (float)steps;
                result.Add(new PlanPoint(Lerp(start.X, end.X, t), Lerp(start.Z, end.Z, t)));
            }
        }
        if (closed && result[^1] != result[0]) result.Add(result[0]);
        return result;
    }

    static List<PlanPoint> Smooth(List<PlanPoint> points, bool closed, float cut)
    {
        if (points.Count < 2 || cut <= 0f) return points;
        var result = new List<PlanPoint>(points.Count * 2);
        int spans = points.Count - 1;
        if (!closed) result.Add(points[0]);
        for (int i = 0; i < spans; i++)
        {
            PlanPoint start = points[i], end = points[i + 1];
            result.Add(new PlanPoint(
                Lerp(start.X, end.X, cut), Lerp(start.Z, end.Z, cut)));
            result.Add(new PlanPoint(
                Lerp(start.X, end.X, 1f - cut), Lerp(start.Z, end.Z, 1f - cut)));
        }
        if (closed) result.Add(result[0]);
        else result.Add(points[^1]);
        return result;
    }

    static float HeightAt(PlanPoint point, Func<int, int, float> heightAtGrid)
    {
        float gridX = point.X / LodSilhouetteSeamMask.CellBlocks;
        float gridZ = point.Z / LodSilhouetteSeamMask.CellBlocks;
        int x0 = (int)MathF.Floor(gridX), z0 = (int)MathF.Floor(gridZ);
        float tx = gridX - x0, tz = gridZ - z0;
        if (tx < 0.00001f) tx = 0f;
        if (tz < 0.00001f) tz = 0f;
        if (tx == 0f && tz == 0f) return FiniteHeight(heightAtGrid(x0, z0));
        if (tx == 0f)
            return Lerp(FiniteHeight(heightAtGrid(x0, z0)),
                FiniteHeight(heightAtGrid(x0, z0 + 1)), tz);
        if (tz == 0f)
            return Lerp(FiniteHeight(heightAtGrid(x0, z0)),
                FiniteHeight(heightAtGrid(x0 + 1, z0)), tx);
        float north = Lerp(FiniteHeight(heightAtGrid(x0, z0)),
            FiniteHeight(heightAtGrid(x0 + 1, z0)), tx);
        float south = Lerp(FiniteHeight(heightAtGrid(x0, z0 + 1)),
            FiniteHeight(heightAtGrid(x0 + 1, z0 + 1)), tx);
        return Lerp(north, south, tz);
    }

    static PlanPoint Unit(float x, float z)
    {
        float length = MathF.Sqrt(x * x + z * z);
        return length > 0.000001f ? new PlanPoint(x / length, z / length) : new PlanPoint(1f, 0f);
    }

    /// <summary>The seam band that must stay hidden: measured staircase plus a margin.</summary>
    internal static float CoreHalfWidth(float staircaseDeviationBlocks) => Math.Clamp(
        (float.IsFinite(staircaseDeviationBlocks) ? staircaseDeviationBlocks : MaxCoreBlocks)
            + CoreMarginBlocks, MinCoreBlocks, MaxCoreBlocks);

    /// <summary>The strip's band: measured staircase plus its smaller margin.</summary>
    internal static float StripHalfWidth(float staircaseDeviationBlocks) => Math.Clamp(
        (float.IsFinite(staircaseDeviationBlocks) ? staircaseDeviationBlocks : MaxStripHalfBlocks)
            + StripMarginBlocks, MinStripHalfBlocks, MaxStripHalfBlocks);

    /// <summary>
    /// How far from the centreline the strip can reach: its band, its edge's largest wander
    /// and the fringe, which the shader cuts off at 3.5 fall-offs.
    /// </summary>
    internal static float StripReach(float stripHalfBlocks) =>
        stripHalfBlocks + 2f * StripWanderBlocks + 3.5f * StripFalloffBlocks;

    /// <summary>How far from the centreline any fog can exist: the bank's reach or the strip's.</summary>
    internal static float FogReach(float spillBlocks, float edgeSoftness, float coreBlocks,
        float stripHalfBlocks) =>
        MathF.Max(BankReach(spillBlocks, edgeSoftness, coreBlocks), StripReach(stripHalfBlocks));

    /// <summary>
    /// How far from the centreline the bank can exist: the core, one and a half Farseer shoulder
    /// reaches (the bank and haze are cut to zero there), and the rim's largest wander.
    /// </summary>
    internal static float BankReach(float spillBlocks, float edgeSoftness, float coreBlocks)
    {
        spillBlocks = Dial(spillBlocks, DefaultSpillBlocks, MinSpillBlocks, MaxSpillBlocks);
        edgeSoftness = Dial(edgeSoftness, DefaultEdgeSoftness, MinEdgeSoftness, MaxEdgeSoftness);
        return coreBlocks + spillBlocks * FarseerReachScale * 1.5f
            + edgeSoftness * 0.5f * spillBlocks;
    }

    internal static float MarchStepBlocks(float detail)
    {
        detail = Dial(detail, DefaultLayerDetail, MinLayerDetail, MaxLayerDetail);
        return Lerp(6f, 2.5f, detail);
    }

    /// <summary>Lateral reach on each side of the centreline; Farseer's is wider.</summary>
    internal static float SideReach(float signedDistance, float spillBlocks)
    {
        spillBlocks = Dial(spillBlocks, DefaultSpillBlocks, MinSpillBlocks, MaxSpillBlocks);
        return signedDistance < 0f ? spillBlocks : spillBlocks * FarseerReachScale;
    }

    /// <summary>
    /// The bank's vertical profile at a height above the seam ground: full through the ground
    /// layer, then exponential. The shader multiplies it by the bank's ceiling.
    /// </summary>
    internal static float HeightProfile(float heightAboveGround, float bankTop)
    {
        float top = MathF.Max(1f, bankTop);
        float lift = MathF.Max(0f, heightAboveGround - GroundLayerFraction * top);
        return MathF.Exp(-lift / MathF.Max(1f, ScaleHeightFraction * top));
    }

    /// <summary>
    /// CPU counterpart of the shader's strip over its band, without the billows' quarter either
    /// way: extinction per block at a height above the surface it rests on, for a local top and
    /// a weight (open-end fade, times what is left of a dissolve). Its vertical column is
    /// <see cref="StripOpticalDepth"/> times the weight squared, for any top.
    /// </summary>
    internal static float StripExtinction(float heightAboveGround, float stripTop,
        float weight = 1f)
    {
        float top = MathF.Max(0.5f, stripTop);
        float w = Math.Clamp(weight, 0f, 1f);
        float vertical = 1f - SmoothStep(0.55f * top, 1.35f * top, MathF.Max(0f, heightAboveGround));
        return StripOpticalDepth / (StripColumnFraction * top) * vertical * w * w;
    }

    /// <summary>
    /// The strip's sideways profile beyond its band, before the billows lobe it: whole over
    /// the band, fraying out exponentially, gone by 3.5 fall-offs.
    /// </summary>
    internal static float StripLateral(float beyondBandBlocks)
    {
        float rim = MathF.Max(0f, beyondBandBlocks);
        return MathF.Exp(-rim / StripFalloffBlocks)
            * (1f - SmoothStep(2f * StripFalloffBlocks, 3.5f * StripFalloffBlocks, rim));
    }

    /// <summary>
    /// CPU counterpart of the shader's curtain at a signed distance from the smoothed edge (empty
    /// sky positive) and a height above the edge's ground: extinction per block for a tendril
    /// value (0 the thinnest, 1 the thickest), a lip bulge and tongue reach in blocks, the
    /// wall's depth below the ground, and a weight.
    /// </summary>
    internal static float CurtainExtinctionAt(float signedDistance, float heightAboveGround,
        float stripHalfBlocks, float wallDepthBlocks, float streak, float tongueBlocks = 0f,
        float weight = 1f)
    {
        if (heightAboveGround >= CurtainLipRiseBlocks) return 0f;
        float lip = CurtainLipBlocks * MathF.Exp(-MathF.Max(0f, -heightAboveGround) / CurtainLipDepthBlocks);
        float rim = MathF.Max(0f, signedDistance - stripHalfBlocks - lip - MathF.Max(0f, tongueBlocks));
        float lateral = MathF.Exp(-rim / CurtainFalloffBlocks)
            * Lerp(1f, streak, SmoothStep(0f, CurtainFalloffBlocks, rim))
            * (1f - SmoothStep(2f * CurtainFalloffBlocks, 3.5f * CurtainFalloffBlocks, rim))
            * SmoothStep(-stripHalfBlocks - CurtainInnerFadeBlocks, -stripHalfBlocks, signedDistance);
        float vertical = (1f - SmoothStep(0f, CurtainLipRiseBlocks, heightAboveGround))
            * SmoothStep(-wallDepthBlocks - CurtainBottomFadeBlocks, -wallDepthBlocks, heightAboveGround);
        float w = Math.Clamp(weight, 0f, 1f);
        return CurtainExtinction * lateral * vertical * (0.75f + 0.5f * streak) * w * w;
    }

    /// <summary>How far out from the smoothed edge the curtain can reach.</summary>
    internal static float CurtainReach(float stripHalfBlocks) =>
        stripHalfBlocks + CurtainLipBlocks + 2f * CurtainWanderBlocks + 3.5f * CurtainFalloffBlocks;

    /// <summary>
    /// Optical depth in front of a stretch of wall at a signed distance, along a level line
    /// straight out from it, at a height: the curtain between that wall and open sky.
    /// </summary>
    internal static float CurtainCoverDepth(float wallSignedDistance, float heightAboveGround,
        float stripHalfBlocks, float wallDepthBlocks, float streak)
    {
        const float step = 0.25f;
        float depth = 0f;
        float reach = CurtainReach(stripHalfBlocks);
        for (float d = wallSignedDistance + step * 0.5f; d < reach; d += step)
            depth += CurtainExtinctionAt(d, heightAboveGround, stripHalfBlocks, wallDepthBlocks,
                streak) * step;
        return depth;
    }

    /// <summary>
    /// Worker thread. Carries the fog shown until now, and what is left of an earlier residual,
    /// onto the next field's lattice (see <see cref="LodSilhouetteFogResidual"/>). Null when
    /// nothing is left to dissolve, or when the lattices differ, which only a reach change
    /// causes; the old fog then goes at once, as before 0.4.125.
    /// </summary>
    /// <param name="shown">The field on screen when the build began.</param>
    /// <param name="fading">The residual on screen then, if it still had weight.</param>
    /// <param name="fadingElapsed">Fade durations that residual had already dissolved.</param>
    /// <param name="headStart">
    /// Fade durations the build is expected to take. The shown fog stays whole until the new
    /// field replaces it, so its weight starts that much above one.
    /// </param>
    /// <param name="halves">Where the residual's texels are rented; null allocates.</param>
    internal static LodSilhouetteFogResidual? BuildResidual(LodSilhouetteFogFieldArrays next,
        LodSilhouetteFogLayer? shown, LodSilhouetteFogLayer? fading, float fadingElapsed,
        float headStart, float spillBlocks, CancellationToken cancellationToken = default,
        HalfPool? halves = null)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (shown == null && fading == null) return null;
        if (shown != null && !SameLattice(next, shown)) shown = null;
        if (fading != null && !SameLattice(next, fading)) fading = null;
        if (shown == null && fading == null) return null;
        spillBlocks = Dial(spillBlocks, DefaultSpillBlocks, MinSpillBlocks, MaxSpillBlocks);
        headStart = float.IsFinite(headStart) ? Math.Clamp(headStart, 0f, 1f) : 0f;
        fadingElapsed = float.IsFinite(fadingElapsed) ? MathF.Max(0f, fadingElapsed) : float.MaxValue;
        // Which candidate a texel keeps: the one whose fog there is heaviest, judged by weight
        // and by how far past the core it lies, on the bank's Farseer shoulder scale.
        float shoulder = MathF.Max(1f, ShoulderFraction * FarseerReachScale * spillBlocks);
        float core = next.CoreBlocks;

        int width = next.Width, height = next.Height;
        // Every texel of the field's extent is written below, so a reused array needs no clearing.
        int texelCount = checked(width * height * 4);
        ushort[] half = halves?.Rent(texelCount) ?? new ushort[texelCount];
        float maxWeight = 0f;
        float minGround = float.PositiveInfinity, maxGround = float.NegativeInfinity;
        int live = 0;
        Span<float> chosen = stackalloc float[4];
        for (int z = 0; z < height; z++)
        {
            if ((z & 15) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int at = (z * width + x) * 4;
                float bestPresence = 0f;
                bool have = false;

                if (shown != null && TexelIndex(next, shown, x, z) is int shownAt)
                {
                    float r = Half(shown, shownAt), g = Half(shown, shownAt + 1);
                    float b = Half(shown, shownAt + 2), a = Half(shown, shownAt + 3);
                    // Where the new field already draws the same fog, nothing needs to dissolve.
                    // Compared as uploaded, so half-float rounding far out cannot differ.
                    bool same = MathF.Abs(r - NextHalf(at)) < 0.5f
                        && MathF.Abs(g - NextHalf(at + 1)) < 0.5f
                        && MathF.Abs(a - NextHalf(at + 3)) < 0.02f;
                    float weight = same ? 0f : a + headStart;
                    chosen[0] = r;
                    chosen[1] = g;
                    chosen[2] = b;
                    chosen[3] = weight;
                    bestPresence = Presence(weight, r);
                    have = true;
                }
                if (fading != null && TexelIndex(next, fading, x, z) is int fadingAt)
                {
                    float r = Half(fading, fadingAt);
                    float weight = Half(fading, fadingAt + 3) - fadingElapsed;
                    float presence = Presence(weight, r);
                    if (!have || presence > bestPresence)
                    {
                        chosen[0] = r;
                        chosen[1] = Half(fading, fadingAt + 1);
                        chosen[2] = Half(fading, fadingAt + 2);
                        chosen[3] = MathF.Max(0f, weight);
                        bestPresence = presence;
                        have = true;
                    }
                }
                if (!have)
                {
                    // Outside both old lattices: no old fog, on the new field's own geometry
                    // so filtering toward a live neighbour stays smooth.
                    chosen[0] = next.Texels[at];
                    chosen[1] = next.Texels[at + 1];
                    chosen[2] = next.Texels[at + 2];
                    chosen[3] = 0f;
                }
                for (int channel = 0; channel < 4; channel++)
                    half[at + channel] = BitConverter.HalfToUInt16Bits((Half)chosen[channel]);
                if (chosen[3] <= 0f) continue;
                live++;
                maxWeight = MathF.Max(maxWeight, chosen[3]);
                minGround = MathF.Min(minGround, chosen[1]);
                maxGround = MathF.Max(maxGround, chosen[1]);
            }
        }
        if (live == 0)
        {
            halves?.Return(half);
            return null;
        }
        return new LodSilhouetteFogResidual
        {
            Layer = new LodSilhouetteFogLayer(half, width, height, next.OriginBlockX,
                next.OriginBlockZ, next.TexelBlocks, minGround, maxGround),
            MaxWeight = maxWeight,
            LiveTexels = live,
        };

        float Presence(float weight, float signed) => weight <= 0f ? 0f
            : weight * MathF.Exp(-MathF.Max(0f, MathF.Abs(signed) - core) / shoulder);

        float NextHalf(int index) => (float)BitConverter.UInt16BitsToHalf(next.HalfTexels[index]);
    }

    static bool SameLattice(LodSilhouetteFogFieldArrays next, LodSilhouetteFogLayer layer) =>
        layer.TexelBlocks == next.TexelBlocks
        // At least: a pooled array (0.4.131) can be longer than its layer.
        && layer.HalfTexels.Length >= layer.Width * layer.Height * 4
        && (layer.OriginBlockX - next.OriginBlockX) % (int)next.TexelBlocks == 0
        && (layer.OriginBlockZ - next.OriginBlockZ) % (int)next.TexelBlocks == 0;

    /// <summary>The texel of <paramref name="layer"/> at the next field's texel, if it has one.</summary>
    static int? TexelIndex(LodSilhouetteFogFieldArrays next, LodSilhouetteFogLayer layer, int x, int z)
    {
        int texel = (int)next.TexelBlocks;
        int lx = x + (next.OriginBlockX - layer.OriginBlockX) / texel;
        int lz = z + (next.OriginBlockZ - layer.OriginBlockZ) / texel;
        if ((uint)lx >= (uint)layer.Width || (uint)lz >= (uint)layer.Height) return null;
        return (lz * layer.Width + lx) * 4;
    }

    static float Half(LodSilhouetteFogLayer layer, int index) =>
        (float)BitConverter.UInt16BitsToHalf(layer.HalfTexels[index]);

    /// <summary>The bank's lateral coverage envelope at a rim distance beyond the core.</summary>
    internal static float Envelope(float rimBlocks, float sideReach) =>
        MathF.Exp(-MathF.Max(0f, rimBlocks) / MathF.Max(1f, sideReach * ShoulderFraction));

    static float Lerp(float from, float to, float amount) => from + (to - from) * amount;

    static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    static float Dial(float value, float fallback, float min, float max) =>
        Math.Clamp(float.IsFinite(value) ? value : fallback, min, max);

    static float FiniteHeight(float value) => float.IsFinite(value) ? value : 0f;
}
