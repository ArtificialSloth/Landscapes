using System.Reflection;
using VintageHorizons.Render;
using Vintagestory.Client.NoObf;

namespace VintageHorizons.Checks;

public static class TerrainSamplerChecks
{
    public static void Run(Check c)
    {
        // Run the installed engine's actual include expansion and sampler collector.
        // No shader compilation or GL call: the defect exists before either is needed.
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        FieldInfo includes = typeof(ShaderRegistry).GetField("includes", flags)!;
        MethodInfo expand = typeof(ShaderRegistry).GetMethod("HandleIncludes", flags)!;
        MethodInfo collect = typeof(ShaderProgram).GetMethod("collectUniformNames",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        object? previous = includes.GetValue(null);
        var sources = new Dictionary<string, string>();
        foreach (string directory in new[]
        {
            Path.Combine(GameAssemblies.GamePath, "assets", "game", "shaderincludes"),
            Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "assets", "dodenslandscapes", "shaderincludes")
        })
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                sources[Path.GetFileName(file)] = File.ReadAllText(file);

        try
        {
            includes.SetValue(null, sources);
            foreach (string variant in new[] { "lodterrain", "lodterrainindirect", "lodterrainpacked", "lodwaterdepth", "lodwater", "lodwateroit" })
            {
                var program = new ShaderProgram();
                var names = new HashSet<string>();
                foreach (string stage in new[] { "vsh", "fsh" })
                {
                    string path = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "assets",
                        "dodenslandscapes", "shaders", variant + "." + stage);
                    string code = (string)expand.Invoke(null, [program, File.ReadAllText(path), null])!;
                    collect.Invoke(program, [code, names]);
                }

                int originalLiquid = program.textureLocations["liquidDepth"];
                c.True(LodTerrainSamplerLayout.Bindings.Any(binding =>
                        binding.Name == "climateField" && binding.Unit == originalLiquid),
                    variant + " reproduces the original liquid-depth/climate-field collision");
                Console.WriteLine($"      {variant}: engine liquidDepth unit {originalLiquid} conflicts with climateField");

                LodTerrainSamplerLayout.Configure(program);
                c.Eq(program.textureLocations.Count, program.textureLocations.Values.Distinct().Count(),
                    variant + " has no sampler aliases after layout configuration");
                foreach (var binding in LodTerrainSamplerLayout.Bindings)
                {
                    c.Eq(binding.Unit, program.textureLocations[binding.Name],
                        variant + " assigns " + binding.Name + " consistently for engine and explicit binds");
                    c.True(binding.Unit < 16, variant + " stays within the baseline fragment-unit limit");
                }
                var before = program.textureLocations.ToDictionary(pair => pair.Key, pair => pair.Value);
                LodTerrainSamplerLayout.Configure(program);
                c.True(before.All(pair => program.textureLocations[pair.Key] == pair.Value),
                    variant + " layout is stable across repeated configuration");
            }
        }
        finally
        {
            includes.SetValue(null, previous);
        }

        string renderer = File.ReadAllText(Path.Combine(GameAssemblies.RepoRoot,
            "VintageHorizons", "src", "Render", "LodTerrainRenderer.cs"));
        foreach (var (variable, success) in new[]
        {
            ("prog", "shaderOk"), ("indirectProg", "indirectShaderOk"), ("packedProg", "packedShaderOk")
        })
        {
            string code = renderer.Replace("\r\n", "\n");
            c.True(code.Contains($"{success} = {variable}.Compile();\n        if ({success}) LodTerrainSamplerLayout.Configure({variable});"),
                variable + " configures every successfully compiled replacement before first use");
        }
    }
}
