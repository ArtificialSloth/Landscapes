using System.Collections.Concurrent;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace VintageHorizons;

/// <summary>
/// What a fruit tree's foliage is showing, as a palette variant.
///
/// The game has one foliage block for every species. The species and the season's state live in
/// the block entity, which only the world holding the chunk can read, so a palette keyed by block
/// id drew every fruit tree as one thing: the type's own colour, which is its bark. Capture reads
/// the entity where the job is created, as it does for a chisel (LodChiselStandIn), and the runs
/// it builds carry a variant such as "cherry:flowering".
///
/// A variant says what was true when the column was last captured. The game cannot say what an
/// unloaded tree is doing, so a tree seen in blossom keeps its blossom until it is seen again.
/// </summary>
public static class LodFruitTrees
{
    const int ChunkSize = GlobalConstants.ChunkSize;

    static readonly ConcurrentDictionary<(string, EnumFoliageState), string> Variants = new();

    /// <summary>A variant's text; the same instance every time, so a chunk full of one tree allocates once.</summary>
    public static string VariantOf(string treeType, EnumFoliageState state) =>
        Variants.GetOrAdd((treeType, state), key => key.Item1 + ":" + StateName(key.Item2));

    public static string StateName(EnumFoliageState state) => state switch
    {
        EnumFoliageState.DormantNoLeaves => "bare",
        EnumFoliageState.Flowering => "flowering",
        EnumFoliageState.Fruiting => "fruiting",
        EnumFoliageState.Ripe => "ripe",
        EnumFoliageState.Dead => "dead",
        _ => "plain",
    };

    /// <summary>The species and state a variant names; false for one this build cannot read.</summary>
    public static bool TryParse(string variant, out string treeType, out EnumFoliageState state)
    {
        treeType = "";
        state = EnumFoliageState.Plain;
        int colon = variant.LastIndexOf(':');
        if (colon <= 0) return false;
        treeType = variant.Substring(0, colon);
        string name = variant.Substring(colon + 1);
        foreach (EnumFoliageState candidate in Enum.GetValues<EnumFoliageState>())
        {
            if (StateName(candidate) != name) continue;
            state = candidate;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Every fruit tree foliage block's variant in a column, by LodChiselStandIn.IndexOf. Call where
    /// the capture job is created, never on the worker: a chunk's block-entity dictionary belongs
    /// to the thread that owns the chunk. A dormant tree the game has not set up yet has no species
    /// and is left out, so it keeps the block's own look. Null when the column has none.
    /// </summary>
    public static Dictionary<int, (int BlockId, string Variant)>? Collect(
        IWorldChunk?[] chunks, int chunkX, int chunkZ)
    {
        Dictionary<int, (int, string)>? found = null;
        int baseX = chunkX * ChunkSize;
        int baseZ = chunkZ * ChunkSize;

        for (int cy = 0; cy < chunks.Length; cy++)
        {
            IWorldChunk? chunk = chunks[cy];
            if (chunk == null || chunk.Disposed) continue;
            var entities = chunk.BlockEntities;
            if (entities == null || entities.Count == 0) continue;

            foreach (BlockEntity entity in entities.Values)
            {
                if (entity is not BlockEntityFruitTreeFoliage foliage || foliage.Pos == null) continue;
                if (string.IsNullOrEmpty(foliage.TreeType)) continue;

                int lx = foliage.Pos.X - baseX;
                int lz = foliage.Pos.Z - baseZ;
                int y = foliage.Pos.Y;
                if (lx < 0 || lx >= ChunkSize || lz < 0 || lz >= ChunkSize || y / ChunkSize != cy) continue;

                // The id the worker will read here, so a block replaced since does not match.
                int blockId = chunk.UnpackAndReadBlock(
                    ((y % ChunkSize) * ChunkSize + lz) * ChunkSize + lx, BlockLayersAccess.FluidOrSolid);
                if (blockId <= 0) continue;

                (found ??= new Dictionary<int, (int, string)>())[LodChiselStandIn.IndexOf(lx, y, lz)] =
                    (blockId, VariantOf(foliage.TreeType, foliage.FoliageState));
            }
        }
        return found;
    }

    /// <summary>
    /// How much of a fruit-bearing tree's outline its fruit fills, and how much of a tree in bloom
    /// its bare branches do. Judged from the game's models, not measured: a tree in leaf is nearly
    /// all leaf, and a blossoming one shows no leaves unless it is evergreen.
    /// </summary>
    public const float FruitShare = 0.15f;
    public const float BranchShareInBloom = 0.2f;

    /// <summary>
    /// The parts a tree in this state shows. Leaves take the live tint and nothing else does,
    /// the same split the game makes: only leaf elements name a colour map. A tree with no leaves
    /// is untinted throughout, which <see cref="LodModelParts.TryCompose"/> answers with false.
    /// </summary>
    public static IEnumerable<LodModelParts.Part> PartsOf(EnumFoliageState state,
        (float R, float G, float B) leaf, (float R, float G, float B) blossom,
        (float R, float G, float B) fruit, (float R, float G, float B) bark)
    {
        switch (state)
        {
            case EnumFoliageState.Flowering:
                yield return new(blossom.R, blossom.G, blossom.B, 1f - BranchShareInBloom, false);
                yield return new(bark.R, bark.G, bark.B, BranchShareInBloom, false);
                break;
            case EnumFoliageState.DormantNoLeaves:
            case EnumFoliageState.Dead:
                yield return new(bark.R, bark.G, bark.B, 1f, false);
                break;
            case EnumFoliageState.Fruiting:
            case EnumFoliageState.Ripe:
                yield return new(leaf.R, leaf.G, leaf.B, 1f - FruitShare, true);
                yield return new(fruit.R, fruit.G, fruit.B, FruitShare, false);
                break;
            default:
                yield return new(leaf.R, leaf.G, leaf.B, 1f, true);
                break;
        }
    }
}
