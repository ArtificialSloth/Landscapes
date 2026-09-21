namespace VintageHorizons;

/// <summary>Plain arrays for the one-draw layered fog volume around a Farseer frontier.</summary>
internal sealed class LodSilhouetteFogMeshArrays
{
    public required float[] Xyz;
    public required float[] FogWeights;
    public required int[] Indices;
    public required int VertexCount;
    public required int IndexCount;
    public required int OriginBlockX;
    public required int OriginBlockZ;
    public required int BoundaryEdges;
    public required int PathCount;
    public required int OpenPathEnds;
    public required int PathColumns;
    public required int PathSpans;
    public required int CenterlineSpans;
    public required int DiagonalCenterlineSpans;
    public required int VerticalLayers;
    public required int HorizontalLayers;
}

/// <summary>
/// Turns the exact ownership frontier into continuous translucent ribbons. The ownership mask
/// remains on its 32-block grid, but its visual centreline is simplified within the fog's reach,
/// rounded and resampled before any sheets are built. A staircase therefore becomes one flowing
/// bank instead of a row of independently capped chunk-edge strips.
///
/// Every boundary edge is directed with Farseer on its right. Horizontal sheets use that fact to
/// carry a wider shoulder over Farseer terrain, while a negative-profile guard band gives the
/// fragment shader room to move the visible rim without clipping it against mesh geometry.
/// </summary>
internal static class LodSilhouetteFogVolume
{
    /// <summary>Lower rim, dense middle and upper rim of a vertical-sheet cross-section.</summary>
    public const int VerticalVerticesPerColumn = 3;

    /// <summary>Two quads bridge a vertical-sheet cross-section to the next.</summary>
    public const int VerticalIndicesPerSpan = 12;

    /// <summary>Guard, rim, plateau, plateau, rim and guard across a horizontal sheet.</summary>
    public const int HorizontalVerticesPerColumn = 6;

    /// <summary>Five quads bridge a horizontal-sheet cross-section to the next.</summary>
    public const int HorizontalIndicesPerSpan = 30;

    /// <summary>
    /// The shader's largest possible positive rim displacement is 0.52 profile units. Keeping
    /// the outer rail below that makes it mathematically transparent even at full softness.
    /// </summary>
    public const float GuardProfile = -0.55f;

    /// <summary>
    /// A true open path grows a rounded, fading cap past its endpoint. Grid turns are part of the
    /// same path and never receive caps. Profile controls the longitudinal fade independently of
    /// the cap's rounded width.
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
    public const float DefaultCoverage = 0.25f;
    public const float MinSpillBlocks = 8f;
    public const float MaxSpillBlocks = 128f;
    public const float DefaultSpillBlocks = 48f;
    public const float MinHeightBlocks = 16f;
    public const float MaxHeightBlocks = 192f;
    public const float DefaultHeightBlocks = 88f;
    public const float MinFloorDepthBlocks = 0f;
    public const float MaxFloorDepthBlocks = 64f;
    public const float DefaultFloorDepthBlocks = 16f;
    public const float MinBillowSizeBlocks = 12f;
    public const float MaxBillowSizeBlocks = 160f;
    public const float DefaultBillowSizeBlocks = 50f;
    public const float MinDriftSpeed = 0f;
    public const float MaxDriftSpeed = 3f;
    public const float DefaultDriftSpeed = 1f;
    public const float MinEdgeSoftness = 0f;
    public const float MaxEdgeSoftness = 1f;
    public const float DefaultEdgeSoftness = 0.5f;
    public const float MinLayerDetail = 0f;
    public const float MaxLayerDetail = 1f;
    public const float DefaultLayerDetail = 0.67f;

    readonly record struct GridPoint(int X, int Z);
    readonly record struct PlanPoint(float X, float Z);
    readonly record struct GridPath(List<GridPoint> Points, bool Closed);
    readonly record struct FogColumn(
        float X, float Z, float Y, float Scale, float Width, float NormalX, float NormalZ);
    readonly record struct FogPath(
        List<FogColumn> Columns, int CenterlineSpans, int DiagonalCenterlineSpans, bool Closed);

    public static LodSilhouetteFogMeshArrays Build(
        LodSilhouetteSeamMask mask, Func<int, int, float> heightAtGrid,
        float spillBlocks = DefaultSpillBlocks,
        float heightBlocks = DefaultHeightBlocks,
        float floorDepthBlocks = DefaultFloorDepthBlocks,
        float layerDetail = DefaultLayerDetail)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(heightAtGrid);
        spillBlocks = Dial(spillBlocks, DefaultSpillBlocks, MinSpillBlocks, MaxSpillBlocks);
        heightBlocks = Dial(heightBlocks, DefaultHeightBlocks, MinHeightBlocks, MaxHeightBlocks);
        floorDepthBlocks = Dial(floorDepthBlocks, DefaultFloorDepthBlocks,
            MinFloorDepthBlocks, MaxFloorDepthBlocks);
        layerDetail = Dial(layerDetail, DefaultLayerDetail, MinLayerDetail, MaxLayerDetail);
        (int verticalLayers, int horizontalLayers) = LayerCounts(layerDetail);

        List<GridPath> traced = TracePaths(mask.Boundaries);
        var paths = new List<FogPath>(traced.Count);
        int openPathEnds = 0;
        int pathColumns = 0;
        int pathSpans = 0;
        int centerlineSpans = 0;
        int diagonalCenterlineSpans = 0;
        foreach (GridPath path in traced)
        {
            FogPath geometry = BuildPath(path, heightAtGrid, spillBlocks);
            if (geometry.Columns.Count < 2) continue;
            paths.Add(geometry);
            if (!geometry.Closed) openPathEnds += 2;
            pathColumns = checked(pathColumns + geometry.Columns.Count);
            pathSpans = checked(pathSpans + geometry.Columns.Count - 1);
            centerlineSpans = checked(centerlineSpans + geometry.CenterlineSpans);
            diagonalCenterlineSpans = checked(
                diagonalCenterlineSpans + geometry.DiagonalCenterlineSpans);
        }

        int verticesPerColumn = checked(
            verticalLayers * VerticalVerticesPerColumn
            + horizontalLayers * HorizontalVerticesPerColumn);
        int indicesPerSpan = checked(
            verticalLayers * VerticalIndicesPerSpan
            + horizontalLayers * HorizontalIndicesPerSpan);
        int vertexCount = checked(pathColumns * verticesPerColumn);
        int indexCount = checked(pathSpans * indicesPerSpan);
        var xyz = new float[checked(vertexCount * 3)];
        var weights = new float[checked(vertexCount * 2)];
        var indices = new int[indexCount];
        int originX = checked(mask.MinCellX * LodSilhouetteSeamMask.CellBlocks);
        int originZ = checked(mask.MinCellZ * LodSilhouetteSeamMask.CellBlocks);
        int vertex = 0, index = 0;
        float innerHeight = Math.Min(8f, heightBlocks * 0.16f);

        float lodVisible = spillBlocks;
        float farseerExtra = MathF.Min(32f, spillBlocks * 0.67f);
        float farseerVisible = spillBlocks + farseerExtra;
        float guardWidth = Math.Clamp(spillBlocks * 0.33f, 6f, 24f);
        float lodPlateau = -MathF.Min(14f, spillBlocks * 0.29f);
        float farseerPlateau = MathF.Min(32f, spillBlocks * 0.67f);
        (float Offset, float Profile)[] horizontalSection =
        {
            (-lodVisible - guardWidth, GuardProfile),
            (-lodVisible, 0f),
            (lodPlateau, 1f),
            (farseerPlateau, 1f),
            (farseerVisible, 0f),
            (farseerVisible + guardWidth, GuardProfile),
        };

        foreach (FogPath path in paths)
        {
            List<FogColumn> columns = path.Columns;
            for (int layer = 0; layer < verticalLayers; layer++)
            {
                float t = verticalLayers == 1 ? 0.5f : layer / (verticalLayers - 1f);
                float offset = Lerp(-lodVisible * 0.78f, farseerVisible * 0.78f, t);
                float sideReach = offset < 0f ? lodVisible * 0.78f : farseerVisible * 0.78f;
                float across = sideReach <= 0f ? 0f : offset / sideReach;
                // More cards reduce the per-card weight. Their sum stays nearly stable while
                // higher detail makes individual planes much harder to perceive.
                float density = 0.16f * MathF.Exp(-2.4f * across * across)
                    * 7f / verticalLayers;
                int first = vertex;
                foreach (FogColumn column in columns)
                {
                    float side = offset * column.Width;
                    float x = column.X + column.NormalX * side;
                    float z = column.Z + column.NormalZ * side;
                    AddVertex(x, column.Y - floorDepthBlocks, z, density,
                        FadeProfile(0f, column.Scale));
                    AddVertex(x, column.Y + innerHeight, z, density,
                        FadeProfile(1f, column.Scale));
                    AddVertex(x, column.Y + heightBlocks, z, density,
                        FadeProfile(0f, column.Scale));
                }
                Stitch(first, columns.Count, VerticalVerticesPerColumn);
            }

            for (int layer = 0; layer < horizontalLayers; layer++)
            {
                float up = horizontalLayers == 1 ? 0f : layer / (horizontalLayers - 1f);
                float height = up * up * heightBlocks * 0.80f;
                float density = (0.18f * MathF.Pow(1f - up, 1.2f) + 0.025f)
                    * 5f / horizontalLayers;
                int first = vertex;
                foreach (FogColumn column in columns)
                {
                    foreach ((float offset, float profile) in horizontalSection)
                    {
                        float side = offset * column.Width;
                        AddVertex(
                            column.X + column.NormalX * side,
                            column.Y + height,
                            column.Z + column.NormalZ * side,
                            density, FadeProfile(profile, column.Scale));
                    }
                }
                Stitch(first, columns.Count, HorizontalVerticesPerColumn);
            }
        }

        if (vertex != vertexCount || index != indexCount)
            throw new InvalidOperationException("seam-fog path sizing no longer matches its mesh");

        return new LodSilhouetteFogMeshArrays
        {
            Xyz = xyz,
            FogWeights = weights,
            Indices = indices,
            VertexCount = vertex,
            IndexCount = index,
            OriginBlockX = originX,
            OriginBlockZ = originZ,
            BoundaryEdges = mask.Boundaries.Count,
            PathCount = paths.Count,
            OpenPathEnds = openPathEnds,
            PathColumns = pathColumns,
            PathSpans = pathSpans,
            CenterlineSpans = centerlineSpans,
            DiagonalCenterlineSpans = diagonalCenterlineSpans,
            VerticalLayers = verticalLayers,
            HorizontalLayers = horizontalLayers,
        };

        // Density and crossing profile remain separate. The profile is one on the broad inner
        // plateau, zero at the visible rim, and negative at the geometry-only guard rail.
        void AddVertex(float x, float y, float z, float density, float profile)
        {
            int xyzAt = vertex * 3;
            xyz[xyzAt] = x - originX;
            xyz[xyzAt + 1] = y;
            xyz[xyzAt + 2] = z - originZ;
            weights[vertex * 2] = density;
            weights[vertex * 2 + 1] = profile;
            vertex++;
        }

        void Stitch(int first, int columnCount, int verticesInColumn)
        {
            for (int c = 0; c + 1 < columnCount; c++)
            {
                int near = first + c * verticesInColumn;
                int far = first + (c + 1) * verticesInColumn;
                for (int across = 0; across + 1 < verticesInColumn; across++)
                    AddQuad(near + across, far + across, near + across + 1, far + across + 1);
            }
        }

        // Choose the diagonal whose endpoints carry the larger combined profile. That keeps a
        // fading cap's remaining density on both triangles instead of cutting a diagonal bite.
        void AddQuad(int a, int b, int c, int d)
        {
            if (weights[b * 2 + 1] + weights[c * 2 + 1]
                >= weights[a * 2 + 1] + weights[d * 2 + 1])
            {
                AddTriangle(a, b, c);
                AddTriangle(c, b, d);
            }
            else
            {
                AddTriangle(a, b, d);
                AddTriangle(a, d, c);
            }
        }

        void AddTriangle(int a, int b, int c)
        {
            indices[index++] = a;
            indices[index++] = b;
            indices[index++] = c;
        }
    }

    static FogPath BuildPath(
        GridPath path, Func<int, int, float> heightAtGrid, float spillBlocks)
    {
        float tolerance = MathF.Min(24f, spillBlocks * 0.5f);
        List<PlanPoint> centreline = Simplify(path, tolerance);
        // Bound the control-point spacing before corner cutting so a macro corner can round over
        // a useful distance without ever allowing a very long simplified segment to move it by
        // hundreds of blocks. A second, denser resample keeps curved ribbon triangles smooth.
        float curveStep = Math.Clamp(spillBlocks * 1.33f, 16f, 96f);
        centreline = Resample(centreline, path.Closed, curveStep);
        float cornerCut = MathF.Min(0.25f,
            spillBlocks / (4f * LodSilhouetteSeamMask.CellBlocks));
        centreline = Smooth(centreline, path.Closed, cornerCut);
        float meshStep = Math.Clamp(spillBlocks * 0.33f, 8f, 16f);
        centreline = Resample(centreline, path.Closed, meshStep);

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
            // Insert near-to-far at index zero so the final path order is far-to-near-to-start.
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
            // The directed ownership edge keeps Farseer on the right-hand normal.
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

    // A cap must fade below the full possible shader wobble, not merely to profile zero: a
    // positive wobble can make zero visible again. Interpolating every rail toward the guard
    // profile makes the final cap column provably transparent.
    static float FadeProfile(float profile, float scale) =>
        GuardProfile + (profile - GuardProfile) * scale;

    internal static (int Vertical, int Horizontal) LayerCounts(float detail)
    {
        detail = Dial(detail, DefaultLayerDetail, MinLayerDetail, MaxLayerDetail);
        int vertical = 3 + 2 * (int)MathF.Round(detail * 3f);
        int horizontal = 2 + (int)MathF.Round(detail * 4f);
        return (vertical, horizontal);
    }

    static float Lerp(float from, float to, float amount) => from + (to - from) * amount;

    static float Dial(float value, float fallback, float min, float max) =>
        Math.Clamp(float.IsFinite(value) ? value : fallback, min, max);

    static float FiniteHeight(float value) => float.IsFinite(value) ? value : 0f;
}
