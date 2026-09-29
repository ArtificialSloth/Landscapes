using System.Text.Json.Nodes;

namespace VintageHorizons.Checks;

/// <summary>
/// The opt-in all-cores garbage collector edits a file that belongs to the game, so what must
/// hold is that it adds exactly its two properties, takes back exactly those, keeps everything
/// else in the file as it was - line endings included - never touches a collector setting that
/// someone else made, keeps one backup of the file as the game shipped it, and reports a file it
/// cannot use instead of damaging it. Driven against a copy of the installed game's own file.
/// </summary>
public static class GameCollectorChecks
{
    const string Sample =
        "{\r\n  \"runtimeOptions\": {\r\n    \"tfm\": \"net10.0\",\r\n    \"frameworks\": [\r\n"
        + "      {\r\n        \"name\": \"Microsoft.NETCore.App\",\r\n        \"version\": \"10.0.0\"\r\n"
        + "      }\r\n    ],\r\n    \"configProperties\": {\r\n"
        + "      \"System.Runtime.TieredPGO\": true\r\n    }\r\n  }\r\n}";

    public static void Run(Check c)
    {
        string original = GameFileOrSample();
        EditsOnlyItsOwnProperties(c, original);
        LeavesOtherSettingsAlone(c, original);
        AppliesWithOneBackup(c, original);
        RefusesAFileItCannotRead(c);
    }

    static string GameFileOrSample()
    {
        string path = Path.Combine(GameAssemblies.GamePath, "Vintagestory.runtimeconfig.json");
        return File.Exists(path) ? File.ReadAllText(path) : Sample;
    }

    static void EditsOnlyItsOwnProperties(Check c, string original)
    {
        c.Eq(GameCollectorSetting.FileState.Off, GameCollectorSetting.ReadState(original),
            "the game's start-up file as shipped asks for the default collector");

        string? on = GameCollectorSetting.Edit(original, on: true, out string outcome);
        c.True(on != null && outcome == "set", "switching on changes the file: " + outcome);
        c.Eq(GameCollectorSetting.FileState.OnByThisMod, GameCollectorSetting.ReadState(on ?? ""),
            "and the file then asks for the all-cores collector, marked as this mod's");
        c.True(on != null && on.Contains("\"System.GC.Server\": true", StringComparison.Ordinal),
            "in the property the runtime reads");
        c.Eq(original.Contains("\r\n", StringComparison.Ordinal),
            on != null && on.Contains("\r\n", StringComparison.Ordinal),
            "keeping the file's line endings");
        c.True(on != null && WithoutOwnProperties(on) == Canonical(original),
            "and everything else in the file exactly as it was");

        string? again = GameCollectorSetting.Edit(on ?? "", on: true, out string againOutcome);
        c.True(again == null && againOutcome == "already set", "switching on twice changes nothing");

        string? off = GameCollectorSetting.Edit(on ?? "", on: false, out string offOutcome);
        c.True(off != null && offOutcome == "removed", "switching off takes it back out: " + offOutcome);
        c.True(off != null && Canonical(off) == Canonical(original),
            "leaving the file meaning exactly what the game shipped");
        c.Eq(GameCollectorSetting.FileState.Off, GameCollectorSetting.ReadState(off ?? ""),
            "with the default collector");
    }

    static void LeavesOtherSettingsAlone(Check c, string original)
    {
        JsonObject root = (JsonObject)JsonNode.Parse(original)!;
        JsonObject properties = (JsonObject)root["runtimeOptions"]!["configProperties"]!;
        properties["System.GC.Server"] = true;
        string elsewhere = root.ToJsonString();

        c.Eq(GameCollectorSetting.FileState.OnElsewhere, GameCollectorSetting.ReadState(elsewhere),
            "a collector property without this mod's marker is recognised as someone else's");
        c.True(GameCollectorSetting.Edit(elsewhere, on: true, out _) == null,
            "switching on leaves someone else's setting as it is");
        c.True(GameCollectorSetting.Edit(elsewhere, on: false, out string outcome) == null
                && outcome.Contains("left as it is", StringComparison.Ordinal),
            "and switching off does not remove it: " + outcome);
    }

    static void AppliesWithOneBackup(Check c, string original)
    {
        string folder = Path.Combine(Path.GetTempPath(), "vh-collector-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "Vintagestory.runtimeconfig.json");
            string backup = path + GameCollectorSetting.BackupSuffix;
            File.WriteAllText(path, original);

            bool on = GameCollectorSetting.Apply(path, on: true, out string onMessage);
            c.True(on && GameCollectorSetting.ReadState(File.ReadAllText(path))
                    == GameCollectorSetting.FileState.OnByThisMod,
                "applying writes the file: " + onMessage);
            c.True(File.Exists(backup) && File.ReadAllText(backup) == original,
                "keeping a backup of the file as it was before");

            bool off = GameCollectorSetting.Apply(path, on: false, out _);
            c.True(off && Canonical(File.ReadAllText(path)) == Canonical(original),
                "applying off puts the file's meaning back");
            GameCollectorSetting.Apply(path, on: true, out _);
            c.True(File.ReadAllText(backup) == original,
                "and a second change never overwrites the first backup");
            c.False(Directory.GetFiles(folder).Any(f => f.EndsWith("-tmp", StringComparison.Ordinal)),
                "no temporary file is left beside it");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    static void RefusesAFileItCannotRead(Check c)
    {
        string folder = Path.Combine(Path.GetTempPath(), "vh-collector-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "Vintagestory.runtimeconfig.json");
            File.WriteAllText(path, "{ this is not json");
            bool applied = GameCollectorSetting.Apply(path, on: true, out string message);
            c.False(applied, "a start-up file that cannot be read is not changed: " + message);
            c.Eq("{ this is not json", File.ReadAllText(path), "and is left exactly as it was");
            c.Eq(GameCollectorSetting.FileState.Unavailable, GameCollectorSetting.ReadState("{ nope"),
                "and reads as unavailable");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    static string Canonical(string json) => JsonNode.Parse(json)!.ToJsonString();

    static string WithoutOwnProperties(string json)
    {
        JsonObject root = (JsonObject)JsonNode.Parse(json)!;
        JsonObject properties = (JsonObject)root["runtimeOptions"]!["configProperties"]!;
        properties.Remove(GameCollectorSetting.ServerProperty);
        properties.Remove(GameCollectorSetting.OwnerProperty);
        return root.ToJsonString();
    }
}
