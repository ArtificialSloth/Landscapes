using System.Reflection;
using System.Runtime;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VintageHorizons;

/// <summary>
/// The opt-in all-cores garbage collector (Session 111): .NET's Server GC, switched on for the
/// game by one property in the game's own start-up file, <c>Vintagestory.runtimeconfig.json</c>.
///
/// The owner's stutter while exploring is garbage collection. With the game's default
/// collector, one core does every collection after each 48 MB of new objects: 450-750
/// collections a minute while exploring, a 10-20 ms freeze each, and the 1% low stuck at
/// 40-57 FPS. Started with the Server GC (adaptive heap count, the .NET 10 default for it),
/// every core works on each collection and they run 15-25 times less often: 25-38 a minute,
/// 0.3-0.4 s frozen a minute instead of 2.7-5.2 s, the 1% low 103-138 FPS, and the owner
/// described it as much smoother. The collector is fixed when the process starts, so a mod
/// cannot switch it in a running game; the start-up file is the one place that sets it for
/// every later start without a launcher.
///
/// The file belongs to the game, so the change is opt-in, reversible and marked as ours: the
/// mod adds the collector property together with a marker property, keeps a one-time backup
/// of the file as the game shipped it, and removes only what it added. A collector property
/// set by someone else is left alone either way. A game update replaces the file; the mod
/// notices on the next start and sets it again.
/// </summary>
internal static class GameCollectorSetting
{
    internal const string ServerProperty = "System.GC.Server";
    internal const string OwnerProperty = "VintageHorizons.AllCoresCollector";
    internal const string BackupSuffix = ".vintagehorizons-backup";

    internal enum FileState
    {
        /// <summary>The start-up file asks for the default collector.</summary>
        Off,
        /// <summary>This mod set the all-cores collector.</summary>
        OnByThisMod,
        /// <summary>Something other than this mod set it; the mod leaves it as it is.</summary>
        OnElsewhere,
        /// <summary>No start-up file, or one this mod cannot read.</summary>
        Unavailable,
    }

    /// <summary>Whether this session's process is running the all-cores collector.</summary>
    internal static bool InEffect => GCSettings.IsServerGC;

    /// <summary>
    /// The game client's start-up file: beside the game, named after the program that
    /// started. Null when there is none, as for a client started some other way.
    /// </summary>
    internal static string? RuntimeConfigPath()
    {
        string? name = Assembly.GetEntryAssembly()?.GetName().Name;
        if (string.IsNullOrEmpty(name)) return null;
        string path = Path.Combine(AppContext.BaseDirectory, name + ".runtimeconfig.json");
        return File.Exists(path) ? path : null;
    }

    internal static FileState ReadState(string json)
    {
        JsonObject? properties = ConfigProperties(json, create: false, out _);
        if (properties == null) return json.Length > 0 && TryParse(json) != null ? FileState.Off : FileState.Unavailable;
        bool server = properties[ServerProperty] is JsonValue value && value.TryGetValue(out bool on) && on;
        if (!server) return FileState.Off;
        return properties.ContainsKey(OwnerProperty) ? FileState.OnByThisMod : FileState.OnElsewhere;
    }

    /// <summary>
    /// The start-up file's text with the all-cores collector set or cleared, or null when
    /// nothing needs to change (already as asked, or set by someone else). Keeps the file's
    /// other contents and its line endings. Pure, so the checks can drive it without a game.
    /// </summary>
    internal static string? Edit(string json, bool on, out string outcome)
    {
        JsonNode? root = TryParse(json);
        if (root is not JsonObject)
        {
            outcome = "the game's start-up file could not be read";
            return null;
        }
        JsonObject? properties = ConfigProperties(json, create: true, out root);
        if (properties == null || root == null)
        {
            outcome = "the game's start-up file has an unexpected layout";
            return null;
        }

        FileState state = ReadState(json);
        if (on)
        {
            if (state == FileState.OnByThisMod)
            {
                outcome = "already set";
                return null;
            }
            if (state == FileState.OnElsewhere)
            {
                outcome = "already set outside Doden's Landscapes";
                return null;
            }
            properties[ServerProperty] = true;
            properties[OwnerProperty] = true;
            outcome = "set";
        }
        else
        {
            if (state == FileState.OnElsewhere)
            {
                outcome = "set outside Doden's Landscapes, so it was left as it is";
                return null;
            }
            if (state != FileState.OnByThisMod)
            {
                outcome = "already off";
                return null;
            }
            properties.Remove(ServerProperty);
            properties.Remove(OwnerProperty);
            outcome = "removed";
        }

        // The writer's own line ending depends on the platform; the file keeps the one it had.
        string text = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        return json.Contains("\r\n", StringComparison.Ordinal)
            ? text.Replace("\n", "\r\n", StringComparison.Ordinal)
            : text;
    }

    /// <summary>
    /// Sets or clears the all-cores collector in the file at <paramref name="path"/>, keeping a
    /// one-time backup of the file as it was before this mod first changed it. False when the
    /// file could not be read or written; <paramref name="message"/> says what happened in words
    /// a player can read. Never throws.
    /// </summary>
    internal static bool Apply(string path, bool on, out string message)
    {
        try
        {
            string json = File.ReadAllText(path);
            if (TryParse(json) is not JsonObject)
            {
                message = "the game's start-up file could not be read, so nothing changed";
                return false;
            }
            string? edited = Edit(json, on, out string outcome);
            message = Describe(on, outcome);
            if (edited == null) return true;

            string backup = path + BackupSuffix;
            if (!File.Exists(backup)) File.Copy(path, backup);
            // Written beside the file and moved over it, so a failure halfway leaves the old file.
            string temporary = path + ".dodenslandscapes-tmp";
            File.WriteAllText(temporary, edited);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception e)
        {
            message = $"the game's start-up file could not be changed ({e.Message}), so nothing changed";
            return false;
        }
    }

    static string Describe(bool on, string outcome) => on
        ? $"all-cores garbage collection {outcome}; it applies from the next time you start the game"
        : $"all-cores garbage collection {outcome}; the default collector applies from the next start";

    static JsonNode? TryParse(string json)
    {
        try
        {
            return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>runtimeOptions.configProperties, optionally created; null when absent or malformed.</summary>
    static JsonObject? ConfigProperties(string json, bool create, out JsonNode? root)
    {
        root = TryParse(json);
        if (root is not JsonObject top) return null;
        if (top["runtimeOptions"] is not JsonObject options)
        {
            if (!create || top.ContainsKey("runtimeOptions")) return null;
            options = new JsonObject();
            top["runtimeOptions"] = options;
        }
        if (options["configProperties"] is JsonObject properties) return properties;
        if (!create || options.ContainsKey("configProperties")) return null;
        properties = new JsonObject();
        options["configProperties"] = properties;
        return properties;
    }
}
