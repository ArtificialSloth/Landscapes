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
    /// a file written for an older model has its fog dials reset once, because the same numbers
    /// no longer describe the same fog.
    /// </summary>
    public const int FogModel = 2;

    // The shader's medium, mirrored here for the checks (lodsilhouettefogvolume.fsh). Optical
    // depths are quoted through one vertical column. A slanted view of ground near the core's
    // edge crosses mostly the thinner shoulder outside it instead, which is what the core margin
    // and the seal's gentle falloff are for (scripts/fog-mockup measures every angle).

    /// <summary>
    /// Column optical depth the seal guarantees over the measured core, whatever the bank's
    /// height or noise: 3.5 hides 97% of the seam. Sized like the bank, so it fills its holes
    /// rather than standing out as a second, denser body (G245's 0.4.122 form).
    /// </summary>
    public const float SealOpticalDepth = 3.5f;

    /// <summary>Mean column optical depth of the noisy bank over the core.</summary>
    public const float BankOpticalDepth = 3.2f;

    /// <summary>The faint halo that carries the fade past the last strands.</summary>
    public const float HazeOpticalDepth = 0.25f;

    /// <summary>Mean eroded noise density inside the core, so the bank meets its target.</summary>
    public const float BankMeanDensity = 0.6f;

    /// <summary>Exponential fade lengths beyond the core, as fractions of that side's reach.</summary>
    public const float ShoulderFraction = 0.4f;
    public const float HazeFraction = 0.6f;
    public const float SealFalloffFraction = 0.3f;

    /// <summary>The Farseer side reaches this much further than the spread dial.</summary>
    public const float FarseerReachScale = 1.6f;

    /// <summary>
    /// Vertical profile, as fractions of the local bank top: a dense ground layer, then an
    /// exponential thinning with this scale height, under a soft eroded ceiling.
    /// </summary>
    public const float GroundLayerFraction = 0.3f;
    public const float ScaleHeightFraction = 0.42f;

    /// <summary>
    /// The vertical profile's integral in units of bank top, computed numerically: under the
    /// seal's ceiling (0.5 to 1.15 of the top) and under the bank's (0.35 to 1.1).
    /// </summary>
    public const float SealColumnFraction = 0.5923f;
    public const float BankColumnFraction = 0.5548f;

    /// <summary>
    /// The core covers the measured staircase plus this margin, within these bounds. Six blocks
    /// left a slanted view of the staircase's outermost corners 13-17% visible in the mock-up;
    /// ten, with the seal fading over 0.3 of the spread, holds every tested angle under 4%.
    /// </summary>
    public const float CoreMarginBlocks = 10f;
    public const float MinCoreBlocks = 12f;
    public const float MaxCoreBlocks = 48f;

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
    public const float DefaultStrength = 1f;
    public const float MinCoverage = 0f;
    public const float MaxCoverage = 1f;
    public const float DefaultCoverage = 0.5f;
    public const float MinSpillBlocks = 8f;
    public const float MaxSpillBlocks = 128f;
    public const float DefaultSpillBlocks = 48f;
    public const float MinHeightBlocks = 16f;
    public const float MaxHeightBlocks = 192f;
    public const float DefaultHeightBlocks = 40f;
    public const float MinFloorDepthBlocks = 0f;
    public const float MaxFloorDepthBlocks = 64f;
    public const float DefaultFloorDepthBlocks = 16f;
    public const float MinBillowSizeBlocks = 12f;
    public const float MaxBillowSizeBlocks = 160f;
    public const float DefaultBillowSizeBlocks = 48f;
    public const float MinDriftSpeed = 0f;
    public const float MaxDriftSpeed = 3f;
    public const float DefaultDriftSpeed = 1f;
    public const float MinEdgeSoftness = 0f;
    public const float MaxEdgeSoftness = 1f;
    public const float DefaultEdgeSoftness = 0.6f;
    public const float MinLayerDetail = 0f;
    public const float MaxLayerDetail = 1f;
    public const float DefaultLayerDetail = 0.67f;

    const int InfiniteCellDistance = 1_000_000_000;

    readonly record struct GridPoint(int X, int Z);
    readonly record struct PlanPoint(float X, float Z);
    readonly record struct GridPath(List<GridPoint> Points, bool Closed);
    internal readonly record struct FogColumn(
        float X, float Z, float Y, float Scale, float Width, float NormalX, float NormalZ);
    internal readonly record struct FogPath(
        List<FogColumn> Columns, int CenterlineSpans, int DiagonalCenterlineSpans, bool Closed);
    readonly record struct FogSample(
        float X, float Z, float Y, float Scale, float Width, float NormalX, float NormalZ);

    internal sealed class Prepared
    {
        internal required List<FogPath> Paths;
        internal required LodSilhouetteBoundaryEdge[] Boundaries;
        internal required float SpillBlocks;
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
    /// Owning-thread snapshot. This is the only phase that calls back into live GPU-mesh metadata;
    /// the expensive rasterization and distance transform consume this immutable result on a worker.
    /// </summary>
    internal static Prepared Prepare(
        LodSilhouetteSeamMask mask, Func<int, int, float> heightAtGrid,
        float spillBlocks = DefaultSpillBlocks)
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

    internal static LodSilhouetteFogFieldArrays Build(
        Prepared prepared, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        cancellationToken.ThrowIfCancellationRequested();
        List<FogPath> paths = prepared.Paths;
        float spillBlocks = prepared.SpillBlocks;
        float minX = prepared.MinX, minZ = prepared.MinZ;
        float maxX = prepared.MaxX, maxZ = prepared.MaxZ;
        // The march stops where the field ends, so it must hold the whole fade: the widest
        // core, the Farseer shoulder, and the rim's largest wander.
        float padding = BankReach(spillBlocks, MaxEdgeSoftness, MaxCoreBlocks)
            + BaseFieldTexelBlocks * 2f;
        float texelBlocks = BaseFieldTexelBlocks;
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

        List<FogSample> samples = RasterSamples(paths, texelBlocks);
        cancellationToken.ThrowIfCancellationRequested();
        int cellCount = checked(width * height);
        var seedByCell = new int[cellCount];
        Array.Fill(seedByCell, -1);
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
        var verticalDistance = new int[cellCount];
        var verticalSeed = new int[cellCount];
        Array.Fill(verticalDistance, InfiniteCellDistance);
        Array.Fill(verticalSeed, -1);
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
        var texels = new float[checked(cellCount * 4)];
        var candidates = new int[width];
        var intersections = new float[width + 1];
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

        var field = new LodSilhouetteFogFieldArrays
        {
            Texels = texels,
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

    static List<FogSample> RasterSamples(List<FogPath> paths, float texelBlocks)
    {
        var result = new List<FogSample>();
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
        float tolerance = MathF.Min(24f, spillBlocks * 0.5f);
        List<PlanPoint> centreline = Simplify(path, tolerance);
        // Bound control-point spacing before corner cutting so macro corners round over a useful
        // distance. The distance field then removes the offset-ribbon self-intersection that wide
        // geometry used to create inside those same corners.
        float curveStep = Math.Clamp(spillBlocks * 1.33f, 16f, 96f);
        centreline = Resample(centreline, path.Closed, curveStep);
        float cornerCut = MathF.Min(0.25f,
            spillBlocks / (4f * LodSilhouetteSeamMask.CellBlocks));
        centreline = Smooth(centreline, path.Closed, cornerCut);
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

    static List<PlanPoint> Simplify(GridPath path, float tolerance)
    {
        var points = path.Points.Select(point => new PlanPoint(
            point.X * LodSilhouetteSeamMask.CellBlocks,
            point.Z * LodSilhouetteSeamMask.CellBlocks)).ToList();
        if (!path.Closed) return SimplifyOpen(points, tolerance);

        int ringCount = points.Count - 1;
        if (ringCount < 4) return points;
        var ring = points.Take(ringCount).ToList();
        int opposite = Farthest(ring, 0);
        int other = Farthest(ring, opposite);
        List<PlanPoint> arcA = SimplifyOpen(Arc(ring, opposite, other), tolerance);
        List<PlanPoint> arcB = SimplifyOpen(Arc(ring, other, opposite), tolerance);
        var simplified = new List<PlanPoint>(arcA.Count + arcB.Count - 1);
        simplified.AddRange(arcA);
        simplified.AddRange(arcB.Skip(1));
        if (simplified.Take(simplified.Count - 1).Distinct().Count() < 3) return points;
        return simplified;
    }

    static List<PlanPoint> SimplifyOpen(List<PlanPoint> points, float tolerance)
    {
        if (points.Count <= 2) return new List<PlanPoint>(points);
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var ranges = new Stack<(int First, int Last)>();
        ranges.Push((0, points.Count - 1));
        float toleranceSquared = tolerance * tolerance;
        while (ranges.Count > 0)
        {
            (int first, int last) = ranges.Pop();
            float furthest = -1f;
            int furthestAt = -1;
            for (int i = first + 1; i < last; i++)
            {
                float distance = SegmentDistanceSquared(points[i], points[first], points[last]);
                if (distance <= furthest) continue;
                furthest = distance;
                furthestAt = i;
            }
            if (furthestAt < 0 || furthest <= toleranceSquared) continue;
            keep[furthestAt] = true;
            ranges.Push((first, furthestAt));
            ranges.Push((furthestAt, last));
        }

        var result = new List<PlanPoint>();
        for (int i = 0; i < points.Count; i++)
            if (keep[i]) result.Add(points[i]);
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

    static int Farthest(List<PlanPoint> points, int from)
    {
        int result = from;
        float furthest = -1f;
        for (int i = 0; i < points.Count; i++)
        {
            float dx = points[i].X - points[from].X;
            float dz = points[i].Z - points[from].Z;
            float distance = dx * dx + dz * dz;
            if (distance <= furthest) continue;
            furthest = distance;
            result = i;
        }
        return result;
    }

    static List<PlanPoint> Arc(List<PlanPoint> ring, int first, int last)
    {
        var result = new List<PlanPoint> { ring[first] };
        int at = first;
        while (at != last)
        {
            at = (at + 1) % ring.Count;
            result.Add(ring[at]);
        }
        return result;
    }

    static float SegmentDistanceSquared(PlanPoint point, PlanPoint start, PlanPoint end)
    {
        float dx = end.X - start.X, dz = end.Z - start.Z;
        float lengthSquared = dx * dx + dz * dz;
        if (lengthSquared <= 0.000001f)
        {
            dx = point.X - start.X;
            dz = point.Z - start.Z;
            return dx * dx + dz * dz;
        }
        float t = Math.Clamp(
            ((point.X - start.X) * dx + (point.Z - start.Z) * dz) / lengthSquared,
            0f, 1f);
        float offsetX = point.X - (start.X + dx * t);
        float offsetZ = point.Z - (start.Z + dz * t);
        return offsetX * offsetX + offsetZ * offsetZ;
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

    /// <summary>
    /// How far from the centreline any fog can exist: the core, one and a half Farseer shoulder
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
    /// The vertical profile at a height above the seam ground: full through the ground layer,
    /// then exponential. The shader multiplies it by the seal's or the bank's ceiling.
    /// </summary>
    internal static float HeightProfile(float heightAboveGround, float bankTop)
    {
        float top = MathF.Max(1f, bankTop);
        float lift = MathF.Max(0f, heightAboveGround - GroundLayerFraction * top);
        return MathF.Exp(-lift / MathF.Max(1f, ScaleHeightFraction * top));
    }

    /// <summary>
    /// CPU counterpart of the shader's seal: extinction per block at a lateral distance beyond
    /// the core and a height above the seam ground, for a bank of the given top. Its vertical
    /// column over the core integrates to <see cref="SealOpticalDepth"/> for any top. Outside
    /// the core the shader also thins it by the bank's billows; this is that fade's envelope.
    /// </summary>
    internal static float SealExtinction(float beyondCore, float heightAboveGround,
        float bankTop, float spillBlocks, float endFade = 1f)
    {
        spillBlocks = Dial(spillBlocks, DefaultSpillBlocks, MinSpillBlocks, MaxSpillBlocks);
        float top = MathF.Max(1f, bankTop);
        float hs = MathF.Max(0f, heightAboveGround);
        float profile = MathF.Exp(-MathF.Max(0f, beyondCore)
            / MathF.Max(1f, SealFalloffFraction * spillBlocks)) * Math.Clamp(endFade, 0f, 1f);
        float ceiling = 1f - SmoothStep(0.5f * top, 1.15f * top, hs);
        return SealOpticalDepth / (SealColumnFraction * top) * profile
            * HeightProfile(hs, top) * ceiling;
    }

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
