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
    public required int TaperedEnds;
    public required int VerticalLayers;
    public required int HorizontalLayers;

    public int Layers => VerticalLayers + HorizontalLayers;

    /// <summary>One edge strip whose two ends are both continued by a straight neighbour.</summary>
    public int VerticesPerEdge => Layers * 2 * LodSilhouetteFogVolume.VerticesPerColumn;
    public int IndicesPerEdge => Layers * LodSilhouetteFogVolume.IndicesPerSpan;

    /// <summary>The faded arc a strip grows past an unsupported end.</summary>
    public int VerticesPerTaper => Layers * LodSilhouetteFogVolume.ColumnsPerTaper
        * LodSilhouetteFogVolume.VerticesPerColumn;
    public int IndicesPerTaper => Layers * LodSilhouetteFogVolume.ColumnsPerTaper
        * LodSilhouetteFogVolume.IndicesPerSpan;
}

/// <summary>
/// Builds crossing translucent sheets around each exact ownership edge. Vertical sheets give the
/// fog depth from ordinary ground-level views; horizontal sheets give it body from hills and
/// aerial views. Their outer and upper vertices carry zero density, so the mesh itself has no
/// opaque chunk-shaped wall for the fragment noise to reveal.
///
/// A strip is described as a short run of cross-sections along its own edge. Where the frontier
/// carries straight on, the neighbouring strip continues the fog and the shared cross-section
/// keeps full density. Where it does not - a ninety-degree turn, or the last known edge of a
/// chain - the strip grows one more cross-section a spill distance further on and fades it to
/// zero, so the veil sweeps over the adjacent terrain instead of ending in a wall.
/// </summary>
internal static class LodSilhouetteFogVolume
{
    /// <summary>Lower rim, dense middle and upper rim of one cross-section.</summary>
    public const int VerticesPerColumn = 3;

    /// <summary>Two quads bridge one cross-section to the next.</summary>
    public const int IndicesPerSpan = 12;

    /// <summary>
    /// The cross-sections a strip grows past an unsupported end, as a fraction of the spill
    /// along its own axis and of its normal half-width across it. A square end would stand out
    /// past the neighbouring strips' own reach and read as a spike at every chunk-grid turn, so
    /// the end is closed as an arc instead: the footprint becomes a stadium, and a ninety-degree
    /// turn rounds off rather than growing a tooth.
    /// </summary>
    public static readonly (float Along, float Width)[] SweptEnd =
    {
        (0.556f, 0.831f),
        (1f, 0.12f),
    };

    public static int ColumnsPerTaper => SweptEnd.Length;

    const byte WestLeaves = 1 << 0;
    const byte EastLeaves = 1 << 1;
    const byte NorthLeaves = 1 << 2;
    const byte SouthLeaves = 1 << 3;

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
        int layers = checked(verticalLayers + horizontalLayers);
        IReadOnlyList<LodSilhouetteBoundaryEdge> edgeList = mask.Boundaries;
        int edges = edgeList.Count;

        // One pass to learn which ends the frontier itself continues, so the arrays below are
        // sized exactly and no strip has to guess at build time.
        Dictionary<(int X, int Z), byte> leaving = LeavingDirections(edgeList);
        var taperStart = new bool[edges];
        var taperEnd = new bool[edges];
        int taperedEnds = 0;
        for (int i = 0; i < edges; i++)
        {
            LodSilhouetteBoundaryEdge edge = edgeList[i];
            int stepX = Math.Sign(edge.EndGridX - edge.StartGridX);
            int stepZ = Math.Sign(edge.EndGridZ - edge.StartGridZ);
            taperStart[i] = !Continues(leaving, edge.StartGridX, edge.StartGridZ, -stepX, -stepZ);
            taperEnd[i] = !Continues(leaving, edge.EndGridX, edge.EndGridZ, stepX, stepZ);
            if (taperStart[i]) taperedEnds++;
            if (taperEnd[i]) taperedEnds++;
        }

        int columnsTotal = checked(edges * 2 + taperedEnds * ColumnsPerTaper);
        int spansTotal = checked(edges + taperedEnds * ColumnsPerTaper);
        int vertexCount = checked(layers * columnsTotal * VerticesPerColumn);
        int indexCount = checked(layers * spansTotal * IndicesPerSpan);
        var xyz = new float[checked(vertexCount * 3)];
        var weights = new float[checked(vertexCount * 2)];
        var indices = new int[indexCount];
        int originX = checked(mask.MinCellX * LodSilhouetteSeamMask.CellBlocks);
        int originZ = checked(mask.MinCellZ * LodSilhouetteSeamMask.CellBlocks);
        int vertex = 0, index = 0;
        float innerHeight = Math.Min(8f, heightBlocks * 0.16f);
        // The two real cross-sections plus an optional swept arc at each end.
        var columns = new (float X, float Z, float Y, float Scale, float Width)[
            2 + 2 * ColumnsPerTaper];

        for (int i = 0; i < edges; i++)
        {
            LodSilhouetteBoundaryEdge edge = edgeList[i];
            float startX = edge.StartGridX * LodSilhouetteSeamMask.CellBlocks;
            float startZ = edge.StartGridZ * LodSilhouetteSeamMask.CellBlocks;
            float endX = edge.EndGridX * LodSilhouetteSeamMask.CellBlocks;
            float endZ = edge.EndGridZ * LodSilhouetteSeamMask.CellBlocks;
            float startY = FiniteHeight(heightAtGrid(edge.StartGridX, edge.StartGridZ));
            float endY = FiniteHeight(heightAtGrid(edge.EndGridX, edge.EndGridZ));
            bool alongZ = edge.StartGridX == edge.EndGridX;
            float normalX = alongZ ? 1f : 0f;
            float normalZ = alongZ ? 0f : 1f;
            float stepX = alongZ ? 0f : MathF.Sign(endX - startX);
            float stepZ = alongZ ? MathF.Sign(endZ - startZ) : 0f;

            // A swept end keeps its neighbour's sampled ground height. Reading one cell further
            // would leave the mask and drop the whole cross-section to sea level.
            int columnCount = 0;
            if (taperStart[i])
                for (int a = ColumnsPerTaper - 1; a >= 0; a--)
                    columns[columnCount++] = (
                        startX - stepX * spillBlocks * SweptEnd[a].Along,
                        startZ - stepZ * spillBlocks * SweptEnd[a].Along,
                        startY, 0f, SweptEnd[a].Width);
            columns[columnCount++] = (startX, startZ, startY, 1f, 1f);
            columns[columnCount++] = (endX, endZ, endY, 1f, 1f);
            if (taperEnd[i])
                for (int a = 0; a < ColumnsPerTaper; a++)
                    columns[columnCount++] = (
                        endX + stepX * spillBlocks * SweptEnd[a].Along,
                        endZ + stepZ * spillBlocks * SweptEnd[a].Along,
                        endY, 0f, SweptEnd[a].Width);

            for (int layer = 0; layer < verticalLayers; layer++)
            {
                float across = verticalLayers == 1
                    ? 0f : layer / (verticalLayers - 1f) * 2f - 1f;
                float offset = across * spillBlocks * 0.78f;
                // More cards reduce the per-card weight. Their sum stays nearly stable while
                // higher detail makes individual planes much harder to perceive.
                float density = 0.16f * MathF.Exp(-2.4f * across * across)
                    * 7f / verticalLayers;
                int first = vertex;
                for (int c = 0; c < columnCount; c++)
                {
                    (float x, float z, float y, float scale, float width) = columns[c];
                    float side = offset * width;
                    AddVertex(x + normalX * side, y - floorDepthBlocks,
                        z + normalZ * side, density, 0f);
                    AddVertex(x + normalX * side, y + innerHeight,
                        z + normalZ * side, density, scale);
                    AddVertex(x + normalX * side, y + heightBlocks,
                        z + normalZ * side, density, 0f);
                }
                Stitch(first, columnCount);
            }

            for (int layer = 0; layer < horizontalLayers; layer++)
            {
                float up = horizontalLayers == 1 ? 0f : layer / (horizontalLayers - 1f);
                float height = up * up * heightBlocks * 0.80f;
                float density = (0.18f * MathF.Pow(1f - up, 1.2f) + 0.025f)
                    * 5f / horizontalLayers;
                int first = vertex;
                for (int c = 0; c < columnCount; c++)
                {
                    (float x, float z, float y, float scale, float width) = columns[c];
                    float side = spillBlocks * width;
                    AddVertex(x - normalX * side, y + height,
                        z - normalZ * side, density, 0f);
                    AddVertex(x, y + height, z, density, scale);
                    AddVertex(x + normalX * side, y + height,
                        z + normalZ * side, density, 0f);
                }
                Stitch(first, columnCount);
            }
        }

        return new LodSilhouetteFogMeshArrays
        {
            Xyz = xyz,
            FogWeights = weights,
            Indices = indices,
            VertexCount = vertex,
            IndexCount = index,
            OriginBlockX = originX,
            OriginBlockZ = originZ,
            BoundaryEdges = edges,
            TaperedEnds = taperedEnds,
            VerticalLayers = verticalLayers,
            HorizontalLayers = horizontalLayers,
        };

        // The layer's own density and, separately, how far across the layer this vertex sits: one
        // at its dense rail, zero at every rim it fades into, and zero along a swept end. Keeping
        // them apart lets the fragment shader bend the crossing profile into a soft shoulder and
        // wobble the rim, instead of the mesh baking in a straight linear ramp.
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

        void Stitch(int first, int columnCount)
        {
            for (int c = 0; c + 1 < columnCount; c++)
            {
                int near = first + c * VerticesPerColumn;
                int far = first + (c + 1) * VerticesPerColumn;
                AddQuad(near, far, near + 1, far + 1);
                AddQuad(near + 1, far + 1, near + 2, far + 2);
            }
        }

        // Split along the diagonal that joins the two corners furthest across the layer. Where a
        // strip fades to nothing, its one dense corner then lies on both triangles and the profile
        // decays over the whole quad; the other diagonal would cut the far triangle off at a flat
        // zero and leave a diagonal bite out of the swept corner. An ordinary strip has equal
        // profiles on both diagonals and keeps its original split.
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

    /// <summary>
    /// The set of directions in which a boundary edge leaves each frontier vertex. An edge only
    /// records the direction of its own far end, so a vertex knows exactly which of its four
    /// neighbours the frontier actually reaches.
    /// </summary>
    static Dictionary<(int X, int Z), byte> LeavingDirections(
        IReadOnlyList<LodSilhouetteBoundaryEdge> edges)
    {
        var directions = new Dictionary<(int X, int Z), byte>();
        foreach (LodSilhouetteBoundaryEdge edge in edges)
        {
            Add(edge.StartGridX, edge.StartGridZ,
                Math.Sign(edge.EndGridX - edge.StartGridX),
                Math.Sign(edge.EndGridZ - edge.StartGridZ));
            Add(edge.EndGridX, edge.EndGridZ,
                Math.Sign(edge.StartGridX - edge.EndGridX),
                Math.Sign(edge.StartGridZ - edge.EndGridZ));
        }

        return directions;

        void Add(int x, int z, int dx, int dz)
        {
            directions.TryGetValue((x, z), out byte present);
            directions[(x, z)] = (byte)(present | Leaving(dx, dz));
        }
    }

    /// <summary>True when another boundary edge carries the frontier straight on past a vertex.</summary>
    static bool Continues(
        Dictionary<(int X, int Z), byte> leaving, int gridX, int gridZ, int dx, int dz) =>
        leaving.TryGetValue((gridX, gridZ), out byte present)
        && (present & Leaving(dx, dz)) != 0;

    static byte Leaving(int dx, int dz) =>
        dx < 0 ? WestLeaves : dx > 0 ? EastLeaves : dz < 0 ? NorthLeaves : SouthLeaves;

    internal static (int Vertical, int Horizontal) LayerCounts(float detail)
    {
        detail = Dial(detail, DefaultLayerDetail, MinLayerDetail, MaxLayerDetail);
        int vertical = 3 + 2 * (int)MathF.Round(detail * 3f);
        int horizontal = 2 + (int)MathF.Round(detail * 4f);
        return (vertical, horizontal);
    }

    static float Dial(float value, float fallback, float min, float max) =>
        Math.Clamp(float.IsFinite(value) ? value : fallback, min, max);

    static float FiniteHeight(float value) => float.IsFinite(value) ? value : 0f;
}
