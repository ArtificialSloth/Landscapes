using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VintageHorizons;

/// <summary>
/// One layout for both ShaderProgramBase.Use() and our explicit texture binds.
/// The engine assigns units by declaration order, including inactive #ifdef branches.
/// Reserving units only at our bind sites leaves its samplers pointing into our tables.
/// Configure each newly compiled terrain program before its first Use().
/// </summary>
internal static class LodTerrainSamplerLayout
{
    internal const int ReadinessMask = 6;
    internal const int SeamField = 7;
    internal const int BlockTable = 8;
    internal const int BlockAtlas = 9;
    internal const int VanillaWaterDepth = 10;
    internal const int SlotTable = 11;
    internal const int ClimateField = 12;
    internal const int ClimateMaps = 13;
    internal const int SeasonColumn = 14;
    internal const int VanillaOitRevealage = 15;

    internal static readonly (string Name, int Unit)[] Bindings =
    [
        ("shadowMapFar", 0), ("shadowMapNear", 1), ("glow", 2), ("sky", 3),
        ("liquidDepth", 4),
        ("readinessMask", ReadinessMask), ("seamField", SeamField),
        ("blockTable", BlockTable), ("blockAtlas", BlockAtlas),
        ("vanillaWaterDepth", VanillaWaterDepth),
        ("slotTable", SlotTable), ("climateField", ClimateField),
        ("climateMaps", ClimateMaps), ("seasonColumn", SeasonColumn),
        ("vanillaOitRevealage", VanillaOitRevealage)
    ];

    internal static void Configure(IShaderProgram program)
    {
        // Only our terrain, water-surface and water-depth programs are passed here. Engine binds
        // must use the same layout on every Use(), including mid-frame shader returns.
        if (program is not ShaderProgramBase native)
            throw new InvalidOperationException("Terrain shader has no engine sampler layout.");
        foreach (var binding in Bindings)
            if (native.textureLocations.ContainsKey(binding.Name))
                native.textureLocations[binding.Name] = binding.Unit;
    }
}
