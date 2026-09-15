using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace VintageHorizons.Silhouette;

/// <summary>
/// Another mod's height sampler, used when it is installed (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER
/// 3.2, D13). Horizons never requires one and never copies its code: it is found and called
/// through reflection only.
/// </summary>
internal interface ILodHeightAccelerator
{
    string Name { get; }

    /// <summary>
    /// Heights at the given block positions, in order. Never called on the main thread: it runs
    /// another mod's world-generation code. Throws when the sampler fails or is not ready; the
    /// caller counts that as a failure, never as a height.
    /// </summary>
    int[] Sample(IReadOnlyList<(int X, int Z)> positions);
}

/// <summary>Finds the accelerators this build knows how to call.</summary>
internal static class LodHeightAccelerators
{
    /// <summary>Main thread: every installed accelerator this build can call, once the world runs.</summary>
    public static List<ILodHeightAccelerator> Find(ICoreServerAPI sapi)
    {
        var found = new List<ILodHeightAccelerator>();
        if (AlgernonsTerrainSampler.TryCreate(sapi) is { } algernon) found.Add(algernon);
        if (WatershedsTerrainSampler.TryCreate(sapi) is { } watersheds) found.Add(watersheds);
        return found;
    }

    static string Version(ICoreServerAPI sapi, string modId) => sapi.ModLoader.GetMod(modId)?.Info?.Version ?? "?";

    static Type? FindType(string fullName) => AppDomain.CurrentDomain.GetAssemblies()
        .Select(assembly => assembly.GetType(fullName, throwOnError: false))
        .FirstOrDefault(type => type != null);

    /// <summary>
    /// Algernon's Terrain Sampler (github.com/Algernon733/AlgernonsTerrainSampler). It recomputes
    /// vanilla GenTerra heights from the region maps without generating chunks. With Watersheds
    /// loaded it answers Watersheds' GetPreWatershedsBlockColumnHeight, which runs the same steps as
    /// Watersheds' own sampler, river valleys included (G205). It answers sea level, without saying
    /// so, when its own generator throws.
    /// </summary>
    sealed class AlgernonsTerrainSampler : ILodHeightAccelerator
    {
        readonly ModSystem system;
        readonly System.Func<int, int, int> height;
        readonly PropertyInfo? generator;

        AlgernonsTerrainSampler(ModSystem system, System.Func<int, int, int> height, PropertyInfo? generator, string name)
        {
            this.system = system;
            this.height = height;
            this.generator = generator;
            Name = name;
        }

        public string Name { get; }

        public static AlgernonsTerrainSampler? TryCreate(ICoreServerAPI sapi)
        {
            if (!sapi.ModLoader.IsModEnabled("algernonsterrainsampler")) return null;
            ModSystem? system = sapi.ModLoader.GetModSystem("AlgernonsTerrainSampler.TerrainSamplerMod");
            MethodInfo? method = system?.GetType().GetMethod("GetBlockColumnHeight",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int), typeof(int) }, null);
            if (system == null || method == null || method.ReturnType != typeof(int)) return null;
            var height = (System.Func<int, int, int>)method.CreateDelegate(typeof(System.Func<int, int, int>), system);
            bool viaWatersheds = system.GetType().GetProperty("WatershedsLoaded")?.GetValue(system) is true;
            return new AlgernonsTerrainSampler(system, height, system.GetType().GetProperty("GenTerra"),
                "Algernon's Terrain Sampler " + Version(sapi, "algernonsterrainsampler")
                + (viaWatersheds ? " (answering through Watersheds)" : ""));
        }

        public int[] Sample(IReadOnlyList<(int X, int Z)> positions)
        {
            if (generator != null && generator.GetValue(system) == null)
                throw new InvalidOperationException("its generator has not started yet");
            var heights = new int[positions.Count];
            for (int i = 0; i < positions.Count; i++) heights[i] = height(positions[i].X, positions[i].Z);
            return heights;
        }
    }

    /// <summary>
    /// Watersheds' own GenTerraSampler. It runs Watersheds' generation height steps for the given
    /// positions without generating chunks: river valleys included, but not the river channels real
    /// generation carves last, nor erosion, which is a separate step (G205).
    /// </summary>
    sealed class WatershedsTerrainSampler : ILodHeightAccelerator
    {
        readonly PropertyInfo instance;
        readonly FieldInfo samplerField;
        readonly MethodInfo batch;

        WatershedsTerrainSampler(PropertyInfo instance, FieldInfo samplerField, MethodInfo batch, string name)
        {
            this.instance = instance;
            this.samplerField = samplerField;
            this.batch = batch;
            Name = name;
        }

        public string Name { get; }

        public static WatershedsTerrainSampler? TryCreate(ICoreServerAPI sapi)
        {
            if (!sapi.ModLoader.IsModEnabled("watersheds")) return null;
            Type? mod = FindType("Watersheds.WatershedsMod");
            PropertyInfo? instance = mod?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            FieldInfo? samplerField = mod?.GetField("terrainSampler", BindingFlags.Public | BindingFlags.Instance);
            MethodInfo? batch = samplerField?.FieldType.GetMethod("SampleTerrainHeightsBatch",
                BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(IEnumerable<(int, int)>), typeof(bool) }, null);
            if (instance == null || samplerField == null || batch == null
                || batch.ReturnType != typeof(Dictionary<(int, int), int>)) return null;
            return new WatershedsTerrainSampler(instance, samplerField, batch,
                "Watersheds' own sampler " + Version(sapi, "watersheds"));
        }

        public int[] Sample(IReadOnlyList<(int X, int Z)> positions)
        {
            object mod = instance.GetValue(null)
                ?? throw new InvalidOperationException("Watersheds has not started yet");
            object sampler = samplerField.GetValue(mod)
                ?? throw new InvalidOperationException("its sampler has not been created yet");
            var list = new List<(int, int)>(positions.Count);
            foreach ((int x, int z) in positions) list.Add((x, z));
            var result = (Dictionary<(int, int), int>)batch.Invoke(sampler, new object[] { list, false })!;
            var heights = new int[positions.Count];
            for (int i = 0; i < positions.Count; i++)
            {
                heights[i] = result.TryGetValue((positions[i].X, positions[i].Z), out int h) ? h
                    : throw new InvalidOperationException("it returned no height for a requested position");
            }
            return heights;
        }
    }
}
