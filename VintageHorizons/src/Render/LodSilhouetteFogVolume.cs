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
}

/// <summary>
/// Builds crossing translucent sheets around each exact ownership edge. Vertical sheets give the
/// fog depth from ordinary ground-level views; horizontal sheets give it body from hills and
/// aerial views. Their outer and upper vertices carry zero density, so the mesh itself has no
/// opaque chunk-shaped wall for the fragment noise to reveal.
/// </summary>
internal static class LodSilhouetteFogVolume
{
    public const int VerticalLayers = 5;
    public const int HorizontalLayers = 4;
    public const int VerticesPerLayer = 6;
    public const int IndicesPerLayer = 12;
    public const int VerticesPerEdge =
        (VerticalLayers + HorizontalLayers) * VerticesPerLayer;
    public const int IndicesPerEdge =
        (VerticalLayers + HorizontalLayers) * IndicesPerLayer;
    public const float SpillBlocks = 48f;
    public const float TopBlocks = 88f;

    static readonly float[] VerticalOffsets = [-36f, -18f, 0f, 18f, 36f];
    static readonly float[] VerticalWeights = [0.04f, 0.14f, 0.30f, 0.14f, 0.04f];
    static readonly float[] HorizontalHeights = [0f, 16f, 38f, 68f];
    static readonly float[] HorizontalWeights = [0.20f, 0.14f, 0.08f, 0.035f];

    public static LodSilhouetteFogMeshArrays Build(
        LodSilhouetteSeamMask mask, Func<int, int, float> heightAtGrid)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(heightAtGrid);
        int edges = mask.Boundaries.Count;
        int vertexCount = checked(edges * VerticesPerEdge);
        int indexCount = checked(edges * IndicesPerEdge);
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

            for (int layer = 0; layer < VerticalLayers; layer++)
            {
                float offset = VerticalOffsets[layer];
                float density = VerticalWeights[layer];
                int first = vertex;
                AddVertex(startX + normalX * offset, startY - 16f,
                    startZ + normalZ * offset, 0f);
                AddVertex(endX + normalX * offset, endY - 16f,
                    endZ + normalZ * offset, 0f);
                AddVertex(startX + normalX * offset, startY + 8f,
                    startZ + normalZ * offset, density);
                AddVertex(endX + normalX * offset, endY + 8f,
                    endZ + normalZ * offset, density);
                AddVertex(startX + normalX * offset, startY + TopBlocks,
                    startZ + normalZ * offset, 0f);
                AddVertex(endX + normalX * offset, endY + TopBlocks,
                    endZ + normalZ * offset, 0f);
                AddStrip(first);
            }

            for (int layer = 0; layer < HorizontalLayers; layer++)
            {
                float height = HorizontalHeights[layer];
                float density = HorizontalWeights[layer];
                int first = vertex;
                AddVertex(startX - normalX * SpillBlocks, startY + height,
                    startZ - normalZ * SpillBlocks, 0f);
                AddVertex(endX - normalX * SpillBlocks, endY + height,
                    endZ - normalZ * SpillBlocks, 0f);
                AddVertex(startX, startY + height, startZ, density);
                AddVertex(endX, endY + height, endZ, density);
                AddVertex(startX + normalX * SpillBlocks, startY + height,
                    startZ + normalZ * SpillBlocks, 0f);
                AddVertex(endX + normalX * SpillBlocks, endY + height,
                    endZ + normalZ * SpillBlocks, 0f);
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

    static float FiniteHeight(float value) => float.IsFinite(value) ? value : 0f;
}
