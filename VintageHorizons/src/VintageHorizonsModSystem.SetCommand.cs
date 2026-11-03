using System.Globalization;
using System.Text;
using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// `.dlset`: any settings-window row by name, so a benchmark route can change one setting at a
/// time (Session 123: the Plants tab, leaf shape and Distant Lights had no command at all).
///
/// <para>
/// A window setting is changed the way the window's Save changes it: the whole state the window
/// would hold is read back, the one row is changed, and it goes through
/// <see cref="ApplyGuiConfig"/>. There is no second path that could drift from the window's,
/// so a setting re-meshes, saves and lights the preset readout exactly as clicking it does.
/// Values use the window's own units: on/off for switches, the slider's numbers for dials.
/// </para>
/// <para>
/// Three rows are not the window's: shore foam and the water depth pass, which stay for this
/// session as their `.dlwater` commands do, and the all-cores collector, which edits the
/// game's start-up file through General's own Save.
/// </para>
/// </summary>
public partial class VintageHorizonsModSystem
{
    /// <summary>Everything the settings window's Save hands over, as one value.</summary>
    readonly record struct WindowState(int[] Thresholds, bool Automatic, int FarCap,
        bool LoadWhilePaused, LodVisualSettings Visuals, LodTaaDials Taa,
        LodSilhouetteSettings Silhouettes, float TargetPixels);

    /// <summary>
    /// One named setting. <c>Apply</c> returns null when the value was taken, or why it was not.
    /// </summary>
    readonly record struct SettingRow(string Key, string Title, string Values, bool Saved,
        System.Func<string> Read, System.Func<string, string?> Apply);

    SettingRow[]? settingRows;

    void RegisterSetCommand()
    {
        capi.ChatCommands.Create("dlset")
            .WithDescription("Change any settings-window row by name, as the window's Save does. "
                + "No argument lists every setting and its value (also written to the client log); "
                + "a name alone shows that one. Names are the window's titles with dashes, "
                + "such as plant-textures or far-biome-colors.")
            .WithArgs(capi.ChatCommands.Parsers.OptionalWord("setting"),
                      capi.ChatCommands.Parsers.OptionalAll("value"))
            .HandleWith(args =>
            {
                if (renderer == null)
                    return TextCommandResult.Success("[Landscapes] the distant-terrain renderer did not start; the client log says why.");

                SettingRow[] rows = settingRows ??= BuildSettingRows();
                if (args.Parsers[0].IsMissing)
                {
                    var list = new StringBuilder("[Landscapes] settings (.dlset <name> <value>):");
                    foreach (SettingRow row in rows)
                        list.Append("\n  ").Append(row.Key).Append(" = ").Append(row.Read());
                    capi.Logger.Notification(list.ToString());
                    return TextCommandResult.Success(list.ToString());
                }

                string name = ((string)args[0]).Trim().ToLowerInvariant();
                SettingRow? found = null;
                foreach (SettingRow row in rows)
                {
                    if (row.Key == name || SettingSlug(row.Title) == name) { found = row; break; }
                }
                if (found is not { } setting)
                    return TextCommandResult.Error($"[Landscapes] no setting called '{name}'. "
                        + ".dlset with nothing lists them.");

                string about = $"{setting.Title} ({setting.Key}): {setting.Values}"
                    + (setting.Saved ? "" : ", this session only");
                if (args.Parsers[1].IsMissing || string.IsNullOrWhiteSpace((string)args[1]))
                    return TextCommandResult.Success($"[Landscapes] {about}. Now {setting.Read()}.");

                if (setting.Apply(((string)args[1]).Trim()) is { } refused)
                    return TextCommandResult.Error($"[Landscapes] {refused}. {about}.");

                string reply = $"[Landscapes] {setting.Title} set to {setting.Read()}"
                    + (setting.Saved ? " (saved)." : " (this session only).");
                capi.Logger.Notification(reply);
                return TextCommandResult.Success(reply);
            });
    }

    /// <summary>A window title as a command name: "Far Biome Colors" is far-biome-colors.</summary>
    static string SettingSlug(string title)
    {
        var slug = new StringBuilder();
        foreach (char c in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '-') slug.Append('-');
        }
        return slug.ToString().TrimEnd('-');
    }

    /// <summary>What the settings window would open with, read from the renderer.</summary>
    WindowState CurrentWindowState() => new(
        renderer.AutoLodThresholds
            ? LodWorld.NormalizeLevelThresholds(config.LodThresholds, config.DetailDistance)
            : LodWorld.GetLevelThresholds(),
        renderer.AutoLodThresholds, renderer.FarViewDistanceCap,
        renderer.ContinueLoadingWhilePaused, CurrentVisualSettings(), renderer.TaaDials,
        CurrentSilhouetteSettings(), renderer.LodTargetPixels);

    void ApplyWindowState(WindowState s) => ApplyGuiConfig(s.Thresholds, s.Automatic, s.FarCap,
        s.LoadWhilePaused, s.Visuals, s.Taa, s.Silhouettes, s.TargetPixels);

    /// <summary>A window row: read from the window's state, written back through its Save.</summary>
    SettingRow WindowRow(string key, string title, string values,
        System.Func<WindowState, string> read, System.Func<WindowState, string, WindowState?> write) =>
        new(key, title, values, true, () => read(CurrentWindowState()), value =>
        {
            if (write(CurrentWindowState(), value) is not { } changed) return $"'{value}' is not one of those";
            ApplyWindowState(changed);
            return null;
        });

    static bool? ParseSwitch(string value) => value.ToLowerInvariant() switch
    {
        "on" or "true" or "1" or "yes" => true,
        "off" or "false" or "0" or "no" => false,
        _ => null,
    };

    static string OnOff(bool on) => on ? "on" : "off";

    static int? ParseWhole(string value, int min, int max) =>
        TryParseSetting(value, out float number) && float.IsFinite(number)
            ? Math.Clamp((int)MathF.Round(number), min, max)
            : null;

    static string Number(float value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);

    SettingRow[] BuildSettingRows()
    {
        var rows = new List<SettingRow>();

        // General: the preset, the distances and Farseer.
        rows.Add(WindowRow("preset", "Preset", "performance, balanced, quality or max",
            s => LodPreset.Matching(s.Visuals, s.FarCap, s.Silhouettes, s.Automatic, s.TargetPixels)?.Code ?? "custom",
            (s, v) => Array.Find(LodPreset.All, p => p.Code == v.ToLowerInvariant()) is { } preset
                // As the window's drop-down does: Automatic on, so the target sets the distances.
                ? s with
                {
                    Automatic = true,
                    TargetPixels = preset.TargetPixels,
                    Visuals = preset.Visuals,
                    Silhouettes = preset.ApplyTo(s.Silhouettes),
                    FarCap = preset.DrawDistance,
                }
                : null));
        rows.Add(WindowRow("draw-distance", "Cache distance",
            $"blocks, {LodThresholdScaleLayout.MinDrawDistance} to {LodThresholdScaleLayout.MaxDrawDistance}",
            s => s.FarCap <= 0 ? "unlimited" : s.FarCap.ToString(CultureInfo.InvariantCulture),
            (s, v) => ParseWhole(v, LodThresholdScaleLayout.MinDrawDistance, LodThresholdScaleLayout.MaxDrawDistance)
                is int blocks ? s with { FarCap = blocks } : null));
        rows.Add(WindowRow("auto-thresholds", "Automatic", "on or off",
            s => OnOff(s.Automatic),
            (s, v) => ParseSwitch(v) is bool on ? s with { Automatic = on } : null));
        rows.Add(WindowRow("detail-target", "LOD detail target",
            $"pixels, {Number(LodAutoThresholds.MinTargetPixels, "0.##")} to {Number(LodAutoThresholds.MaxTargetPixels, "0.##")}",
            s => Number(s.TargetPixels, "0.00"),
            (s, v) => TryParseSetting(v, out float px) && float.IsFinite(px)
                ? s with { TargetPixels = Math.Clamp(px, LodAutoThresholds.MinTargetPixels, LodAutoThresholds.MaxTargetPixels) }
                : null));
        rows.Add(WindowRow("silhouette-enabled", "Farseer", "on or off",
            s => OnOff(s.Silhouettes.Enabled),
            (s, v) => ParseSwitch(v) is bool on ? s with { Silhouettes = s.Silhouettes with { Enabled = on } } : null));
        rows.Add(WindowRow("silhouette-fog", "Seam Fog", "on or off",
            s => OnOff(s.Silhouettes.FogEnabled),
            (s, v) => ParseSwitch(v) is bool on ? s with { Silhouettes = s.Silhouettes with { FogEnabled = on } } : null));
        rows.Add(WindowRow("silhouette-reach", "Farseer reach",
            $"blocks, {LodSilhouetteSettings.MinReachBlocks} to {LodSilhouetteSettings.MaxReachBlocks}",
            s => s.Silhouettes.ReachBlocks.ToString(CultureInfo.InvariantCulture),
            (s, v) => ParseWhole(v, LodSilhouetteSettings.MinReachBlocks, LodSilhouetteSettings.MaxReachBlocks) is int blocks
                ? s with { Silhouettes = (s.Silhouettes with { ReachBlocks = blocks }).Normalize() }
                : null));
        rows.Add(WindowRow("load-while-paused", "Load While Paused", "on or off",
            s => OnOff(s.LoadWhilePaused),
            (s, v) => ParseSwitch(v) is bool on ? s with { LoadWhilePaused = on } : null));

        // General's feature switches, then the Plants tab's, each read and written as its row is.
        foreach (VintageHorizonsConfigDialog.VisualRow row in
                 VintageHorizonsConfigDialog.Rows.Concat(VintageHorizonsConfigDialog.PlantRows))
        {
            rows.Add(WindowRow(row.Key, row.Title, "on or off",
                s => OnOff(row.Read(s.Visuals)),
                (s, v) => ParseSwitch(v) is bool on ? s with { Visuals = row.Write(s.Visuals, on) } : null));
        }
        rows.Add(WindowRow("plant-detail", "Plant Detail", "on or off",
            s => OnOff(s.Visuals.PlantDetail),
            (s, v) => ParseSwitch(v) is bool on ? s with { Visuals = s.Visuals with { PlantDetail = on } } : null));
        rows.Add(WindowRow("leaf-shape", "Leaf Shape",
            string.Join(", ", Enum.GetNames<LodLeafShape>().Select(n => n.ToLowerInvariant())),
            s => s.Visuals.LeafShape.ToString().ToLowerInvariant(),
            (s, v) => Enum.TryParse(v.Replace("-", "").Replace(" ", ""), true, out LodLeafShape shape)
                && Enum.IsDefined(shape)
                ? s with { Visuals = s.Visuals with { LeafShape = shape } }
                : null));

        // The Developer tab: the seam band, anti-aliasing, and Farseer's and the fog's dials.
        rows.Add(WindowRow("band-reach", "Band reach", "percent of level 0, 0 to 100",
            s => ((int)MathF.Round(s.Visuals.BandReach * 100)).ToString(CultureInfo.InvariantCulture),
            (s, v) => ParseWhole(v, 0, 100) is int percent
                ? s with { Visuals = s.Visuals with { BandReach = percent / 100f } }
                : null));
        rows.Add(WindowRow("level0-target", "Level 0 detail target",
            $"pixels, {Number(LodAutoThresholds.MinTargetPixels, "0.##")} to {Number(LodAutoThresholds.MaxTargetPixels, "0.##")}; at or above the detail target it follows it",
            s => Number(s.Visuals.Level0Pixels, "0.00"),
            (s, v) => TryParseSetting(v, out float px) && float.IsFinite(px)
                ? s with { Visuals = s.Visuals with { Level0Pixels = Math.Clamp(px, LodAutoThresholds.MinTargetPixels, LodAutoThresholds.MaxTargetPixels) } }
                : null));
        foreach (LodTaaSliders.Row row in LodTaaSliders.All)
        {
            rows.Add(WindowRow(row.Key, row.Title,
                $"{row.Label(row.FromSlider(row.Min))} to {row.Label(row.FromSlider(row.Max))}",
                s => row.Label(row.Read(s.Taa)),
                // Through the slider's own steps, so the value is one the window can show.
                (s, v) => TryParseSetting(row.Suffix.Trim() is { Length: > 0 } unit ? v.Replace(unit, "") : v,
                        out float value) && float.IsFinite(value)
                    ? s with { Taa = row.Write(s.Taa, row.FromSlider(row.ToSlider(value))) }
                    : null));
        }
        foreach (VintageHorizonsConfigDialog.SilhouetteRow row in VintageHorizonsConfigDialog.SilhouetteRows)
        {
            rows.Add(WindowRow(row.Key, row.Title, $"{row.Min} to {row.Max} on the slider",
                s => $"{row.Read(s.Silhouettes)} ({row.Label(row.Read(s.Silhouettes))})",
                (s, v) => ParseWhole(v, row.Min, row.Max) is int value
                    ? s with { Silhouettes = row.Write(s.Silhouettes, value).Normalize() }
                    : null));
        }

        // Not window state. The collector is General's switch but edits the game's start-up file
        // through the same Save; the water two are .dlwater's and stay for this session.
        rows.Add(new SettingRow("all-cores-gc", "All-Cores Garbage Collection",
            "on or off, from the next game start", true,
            () => OnOff(config.AllCoresGarbageCollection),
            value =>
            {
                if (ParseSwitch(value) is not bool on) return $"'{value}' is not on or off";
                SaveAllCoresGarbageCollection(on);
                SaveConfig();
                return null;
            }));
        rows.Add(new SettingRow("shore-foam", "Shore foam", "0 to 2, 1 is the game's own", false,
            () => Number(renderer.WaterShoreFoam, "0.00"),
            value =>
            {
                if (!TryParseSetting(value, out float shore) || !float.IsFinite(shore))
                    return $"'{value}' is not a number";
                renderer.WaterShoreFoam = Math.Clamp(shore, 0f, 2f);
                renderer.WaterShorePaint = false;
                return null;
            }));
        rows.Add(new SettingRow("water-majority", "Water depth pass", "on or off", false,
            () => OnOff(renderer.WaterDepthMajority),
            value =>
            {
                if (ParseSwitch(value) is not bool on) return $"'{value}' is not on or off";
                renderer.WaterDepthMajority = on;
                return null;
            }));

        return rows.ToArray();
    }
}
