using Vintagestory.API.Client;

namespace VintageHorizons;

/// <summary>
/// The login notice about other LOD mods (owner, 2026-10-10). This mod never steps aside for
/// another LOD mod, so the player is told instead, and the mod list stays the player's job.
///
/// Two forms. When a listed LOD mod is switched on (<see cref="OtherLodMods"/>), the notice
/// names it at every login and offers only OK: hiding it would hide the very conflict it is
/// about. Otherwise it is a general reminder that this mod does not step aside, shown at every
/// login until the player chooses "Don't show again".
/// </summary>
internal sealed class LodModNotice : GuiDialog
{
    const double Width = 600;
    const double TextHeight = 230;
    const double TitleBar = 30;
    const double Margin = 20;

    readonly string[] conflicts;
    readonly bool offerHide;
    readonly Action onHide;

    public LodModNotice(ICoreClientAPI capi, string[] conflicts, bool offerHide, Action onHide) : base(capi)
    {
        this.conflicts = conflicts;
        this.offerHide = offerHide && conflicts.Length == 0;
        this.onHide = onHide;
        Compose();
    }

    public override string? ToggleKeyCombinationCode => null;

    public override string DebugName => "dodenslandscapes-lod-notice";

    internal static string Title(string[] conflicts) => conflicts.Length == 0
        ? "Doden's Landscapes: one LOD mod at a time"
        : "Doden's Landscapes: another LOD mod is installed";

    /// <summary>The general reminder, shown while no listed LOD mod is switched on.</summary>
    internal const string GeneralText =
        "Doden's Landscapes always draws distant terrain. It does not step aside for other LOD mods "
        + "such as Farseer, Distant Vistas, ChunkLOD or the original Vintage Horizons.\n\n"
        + "If another LOD mod is also switched on, the two draw over each other: the horizon "
        + "flickers, and you pay the frame-rate cost twice. Keep only one LOD mod switched on in "
        + "the Mod Manager.";

    /// <summary>The conflict form, also written to the log. Owner-approved wording, 2026-10-07.</summary>
    internal static string ConflictText(string[] conflicts)
    {
        string names = JoinNames(conflicts);
        bool one = conflicts.Length == 1;
        return $"{names} {(one ? "is" : "are")} switched on. {(one ? "It also draws" : "They also draw")} "
            + $"distant terrain, so {(one ? "the two" : "they")} overlap: the horizon flickers, and you pay the "
            + $"frame-rate cost {(one ? "twice" : "several times over")}.\n\n"
            + $"Doden's Landscapes keeps drawing. For the best picture, switch {names} off in the Mod "
            + "Manager and restart the game.";
    }

    internal static string JoinNames(string[] names) => names.Length switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names, 0, names.Length - 1) + " and " + names[^1],
    };

    void Compose()
    {
        // Laid out like the settings window: a fixed window, its background filling it, and
        // children placed from the window's top-left with the title bar's height allowed for.
        double buttonY = TitleBar + 10 + TextHeight + 10;
        ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, Width, buttonY + 34 + Margin)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        ElementBounds text = ElementBounds.Fixed(Margin, TitleBar + 10, Width - 2 * Margin, TextHeight);

        GuiComposer composer = capi.Gui
            .CreateCompo(DebugName, dialogBounds)
            .AddShadedDialogBG(ElementBounds.Fill, true)
            .AddDialogTitleBar(Title(conflicts), () => TryClose())
            .BeginChildElements()
            .AddStaticText(conflicts.Length == 0 ? GeneralText : ConflictText(conflicts),
                CairoFont.WhiteSmallText(), text);

        if (offerHide)
        {
            composer.AddButton("Don't show again", OnHide,
                ElementBounds.Fixed(Width - Margin - 140 - 10 - 200, buttonY, 200, 34),
                EnumButtonStyle.Normal, "hide");
        }
        composer.AddButton("OK", () => TryClose(),
            ElementBounds.Fixed(Width - Margin - 140, buttonY, 140, 34), EnumButtonStyle.Normal, "ok");

        SingleComposer = composer.EndChildElements().Compose();
    }

    bool OnHide()
    {
        onHide();
        return TryClose();
    }
}
