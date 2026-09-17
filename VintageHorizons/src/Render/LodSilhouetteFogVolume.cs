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
    public required int VerticalLayers;
    public required int HorizontalLayers;

    public int VerticesPerEdge =>
        (VerticalLayers + HorizontalLayers) * LodSilhouetteFogVolume.VerticesPerLayer;
    public int IndicesPerEdge =>
        (VerticalLayers + HorizontalLayers) * LodSilhouetteFogVolume.IndicesPerLayer;
}

/// <summary>
/// Builds crossing translucent sheets around each exact ownership edge. Vertical sheets give the
/// fog depth from ordinary ground-level views; horizontal sheets give it body from hills and
/// aerial views. Their outer and upper vertices carry zero density, so the mesh itself has no
/// opaque chunk-shaped wall for the fragment noise to reveal.
/// </summary>
internal static class LodSilhouetteFogVolume
{
    public const int VerticesPerLayer = 6;
    public const int IndicesPerLayer = 12;

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
        int verticesPerEdge = checked((verticalLayers + horizontalLayers) * VerticesPerLayer);
        int indicesPerEdge = checked((verticalLayers + horizontalLayers) * IndicesPerLayer);
        int edges = mask.Boundaries.Count;
        int vertexCount = checked(edges * verticesPerEdge);
        int indexCount = checked(edges * indicesPerEdge);
        var xyz = new float[checked(vertexCount * 3)];
        var weights = new float[checked(vertexCount * 2)];
        var indices = new int[indexCount];
        int originX = checked(mask.MinCellX * LodSilhouetteSeamMask.CellBlocks);
        int originZ = checked(mask.MinCellZ * LodSilhouetteSeamMask.CellBlocks);
        int vertex = 0, index = 0;

        foreach (LodSilhouetteBoundaryEdge edge in mask.Boundaries)
        {
            float startX = edge.StartGridX * LodSilhouetteSeamMask.CellBlocks;
            float startZ = edge.StartGridZ * LodSilhouetteSeamMask.CellBlocks;
            float endX = edge.EndGridX * LodSilhouetteSeamMask.CellBlocks;
            float endZ = edge.EndGridZ * LodSilhouetteSeamMask.CellBlocks;
            float startY = FiniteHeight(heightAtGrid(edge.StartGridX, edge.StartGridZ));
            float endY = FiniteHeight(heightAtGrid(edge.EndGridX, edge.EndGridZ));
            bool alongZ = edge.StartGridX == edge.EndGridX;
            float normalX = alongZ ? 1f : 0f;
            float normalZ = alongZ ? 0f : 1f;

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
                AddVertex(startX + normalX * offset, startY - floorDepthBlocks,
                    startZ + normalZ * offset, 0f);
                AddVertex(endX + normalX * offset, endY - floorDepthBlocks,
                    endZ + normalZ * offset, 0f);
                float innerHeight = Math.Min(8f, heightBlocks * 0.16f);
                AddVertex(startX + normalX * offset, startY + innerHeight,
                    startZ + normalZ * offset, density);
                AddVertex(endX + normalX * offset, endY + innerHeight,
                    endZ + normalZ * offset, density);
                AddVertex(startX + normalX * offset, startY + heightBlocks,
                    startZ + normalZ * offset, 0f);
                AddVertex(endX + normalX * offset, endY + heightBlocks,
                    endZ + normalZ * offset, 0f);
                AddStrip(first);
            }

            for (int layer = 0; layer < horizontalLayers; layer++)
            {
                float up = horizontalLayers == 1 ? 0f : layer / (horizontalLayers - 1f);
                float height = up * up * heightBlocks * 0.80f;
                float density = (0.18f * MathF.Pow(1f - up, 1.2f) + 0.025f)
                    * 5f / horizontalLayers;
                int first = vertex;
                AddVertex(startX - normalX * spillBlocks, startY + height,
                    startZ - normalZ * spillBlocks, 0f);
                AddVertex(endX - normalX * spillBlocks, endY + height,
                    endZ - normalZ * spillBlocks, 0f);
                AddVertex(startX, startY + height, startZ, density);
                AddVertex(endX, endY + height, endZ, density);
                AddVertex(startX + normalX * spillBlocks, startY + height,
                    startZ + normalZ * spillBlocks, 0f);
                AddVertex(endX + normalX * spillBlocks, endY + height,
                    endZ + normalZ * spillBlocks, 0f);
                AddStrip(first);
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
            VerticalLayers = verticalLayers,
            HorizontalLayers = horizontalLayers,
        };

        void AddVertex(float x, float y, float z, float fogWeight)
        {
            int xyzAt = vertex * 3;
            xyz[xyzAt] = x - originX;
            xyz[xyzAt + 1] = y;
            xyz[xyzAt + 2] = z - originZ;
            weights[vertex * 2] = fogWeight;
            weights[vertex * 2 + 1] = 0f;
            vertex++;
        }

        void AddStrip(int first)
        {
            AddQuad(first, first + 1, first + 2, first + 3);
            AddQuad(first + 2, first + 3, first + 4, first + 5);
        }

        void AddQuad(int a, int b, int c, int d)
        {
            indices[index++] = a;
            indices[index++] = b;
            indices[index++] = c;
            indices[index++] = c;
            indices[index++] = b;
            indices[index++] = d;
        }
    }

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
