using System.Text.RegularExpressions;

namespace VintageHorizons.Checks;

internal static class ModShaderSource
{
    // Expand repository includes for source-contract checks. Engine includes remain
    // directives here; TerrainSamplerChecks uses the actual engine expander for those.
    internal static string Read(string path) => Expand(path, new HashSet<string>());

    static string Expand(string path, HashSet<string> stack)
    {
        path = Path.GetFullPath(path);
        if (!stack.Add(path)) throw new InvalidOperationException("Shader include cycle: " + path);
        string includes = Path.Combine(GameAssemblies.RepoRoot, "VintageHorizons", "assets",
            "dodenslandscapes", "shaderincludes");
        string result = Regex.Replace(File.ReadAllText(path).Replace("\r\n", "\n"),
            @"(?m)^#include ([^\s]+)[ \t]*$", match =>
            {
                string include = Path.Combine(includes, match.Groups[1].Value);
                return File.Exists(include) ? Expand(include, stack).TrimEnd('\n') : match.Value;
            });
        stack.Remove(path);
        return result;
    }
}
