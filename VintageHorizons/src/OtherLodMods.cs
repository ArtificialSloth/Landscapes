namespace VintageHorizons;

/// <summary>
/// The one field we need out of another LOD mod's own config file. Nothing else is
/// declared on purpose: the JSON reader leaves unknown fields alone, and a file without
/// an "Enabled" field keeps this default, so anything we do not recognise reads as on.
/// </summary>
public class OtherLodModSwitch
{
    public bool Enabled = true;
}

/// <summary>
/// Which other LOD mods are actually drawing distant terrain.
///
/// This mod never steps aside for them (owner, 2026-10-10: the player manages their own mod
/// list). What it finds is named in the log and in the login notice, which tells the player to
/// keep only one LOD mod switched on. Being loaded is not the same as drawing: Farseer,
/// ChunkLOD and TopoHorizon are Universal with requiredOnClient, so a server that runs one
/// makes every client download it, and a player may have switched it off in its own settings.
/// Where a mod keeps its switch somewhere we can read, we read it, so a switched-off mod does
/// not raise the notice.
/// </summary>
public static class OtherLodMods
{
    /// <summary>
    /// Mod id, and the mod's own client config file when its switch can be read from one.
    ///
    /// Farseer's dialog writes "Enabled" into farseer-client.json, and Farseer rewrites
    /// that file every time it loads, so the value on disk is always current. Verified
    /// against Farseer 1.4.0 by writing a one-field file and reading back what it stored.
    ///
    /// The others expose no config switch we could find, so being switched on in the Mod
    /// Manager counts as drawing. Distant Vistas (1.1.3, checked 2026-10-10) and the original
    /// Vintage Horizons both extend the camera far plane and draw cached terrain.
    ///
    /// Every entry must name a mod that actually RENDERS distant terrain. Vistas Beyond
    /// was listed here once, guessed from its name, and it does not belong: it is a
    /// server-side worldgen config mod (it adjusts landforms.json) and draws nothing.
    /// A wrong entry now costs a false notice at every login, so verify before adding.
    /// </summary>
    public static readonly (string ModId, string? SwitchFile)[] Known =
    {
        ("farseer", "farseer-client.json"),
        ("chunklod", null),
        ("topohorizon", null),
        ("distantvistas", null),
        (ModIdentity.LegacyModId, null),
    };

    /// <summary>
    /// Every loaded listed mod that is still drawing, plus every one found switched off.
    /// <paramref name="switchedOn"/> takes a config file name and returns null when the file
    /// is missing or unreadable, which counts as on.
    /// </summary>
    public static (string[] Drawing, string[] SwitchedOff) Inspect(
        Func<string, bool> isLoaded, Func<string, bool?> switchedOn)
    {
        var drawing = new List<string>();
        var switchedOff = new List<string>();

        foreach ((string modid, string? switchFile) in Known)
        {
            if (!isLoaded(modid)) continue;

            if (switchFile != null && switchedOn(switchFile) == false)
            {
                switchedOff.Add(modid);
                continue;
            }

            drawing.Add(modid);
        }

        return (drawing.ToArray(), switchedOff.ToArray());
    }
}
