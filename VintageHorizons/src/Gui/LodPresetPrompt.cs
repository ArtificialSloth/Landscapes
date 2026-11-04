using Vintagestory.API.Client;

namespace VintageHorizons;

/// <summary>
/// The preset prompt (owner, 2026-10-19). A fresh install draws Quality so players see what the
/// mod can do; a player whose machine cannot afford it is the one who goes looking for settings,
/// and this puts the choice in front of every player at their first login, and again once when
/// the presets change (<see cref="PromptRound"/>).
///
/// One button per <see cref="LodPreset"/>, each with what it draws underneath, Quality marked as
/// the one to pick (owner, 2026-11-03: Max offered too). Closing it any other way keeps the
/// settings as they are. Either way it is not shown again until the next round.
/// </summary>
internal sealed class LodPresetPrompt : GuiDialog
{
    /// <summary>
    /// Which set of presets the prompt last asked about. 2 is 0.5.2's (Max added, every preset
    /// re-measured), so a player who answered 0.5.0's three-preset prompt is asked once more.
    /// </summary>
    internal const int PromptRound = 2;

    const double Width = 900;
    const double TitleBar = 30;
    const double Margin = 20;
    const double ColumnGap = 16;
    const double ButtonHeight = 36;
    const double SummaryHeight = 84;

    /// <summary>"Quality (Recommended)" is 183 wide at 16 points in the game's button face.</summary>
    const float ButtonFontSize = 16;

    readonly Action<LodPreset> choose;
    readonly Action closed;
    readonly bool updated;

    public LodPresetPrompt(ICoreClientAPI capi, Action<LodPreset> choose, Action closed, bool updated) : base(capi)
    {
        this.choose = choose;
        this.closed = closed;
        this.updated = updated;
        Compose();
    }

    public override string? ToggleKeyCombinationCode => null;

    public override string DebugName => "dodenslandscapes-preset-prompt";

    internal const string Title = "Doden's Landscapes: choose a preset";

    internal const string IntroText =
        "How much of the distant landscape should be drawn? Quality is recommended: the mod's full "
        + "look at a good frame rate. Max is the finest it draws, for a strong machine. If your frame "
        + "rate suffers, pick a lighter preset.\n\n"
        + "You can change this, or any single setting, any time in the game's Settings on the "
        + "Landscapes page, or by typing .dlconfig in chat.";

    internal const string UpdatedText =
        "The presets were redone in this version, so you are asked once more. Closing this keeps "
        + "your current settings.\n\n";

    internal static string ButtonLabel(LodPreset preset) =>
        preset == LodPreset.Quality ? preset.Name + " (Recommended)" : preset.Name;

    void Compose()
    {
        // Two more lines when it says why it is back.
        double introHeight = updated ? 170 : 120;
        int columns = LodPreset.FirstRun.Length;
        double columnWidth = (Width - 2 * Margin - (columns - 1) * ColumnGap) / columns;
        double buttonY = TitleBar + 10 + introHeight + 10;
        double summaryY = buttonY + ButtonHeight + 8;
        ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, Width, summaryY + SummaryHeight + Margin)
            .WithAlignment(EnumDialogArea.CenterMiddle);

        GuiComposer composer = capi.Gui
            .CreateCompo(DebugName, dialogBounds)
            .AddShadedDialogBG(ElementBounds.Fill, true)
            .AddDialogTitleBar(Title, () => TryClose())
            .BeginChildElements()
            .AddStaticText(updated ? UpdatedText + IntroText : IntroText, CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(Margin, TitleBar + 10, Width - 2 * Margin, introHeight));

        for (int i = 0; i < columns; i++)
        {
            LodPreset preset = LodPreset.FirstRun[i];
            double x = Margin + i * (columnWidth + ColumnGap);
            composer.AddButton(ButtonLabel(preset), () => OnChoose(preset),
                ElementBounds.Fixed(x, buttonY, columnWidth, ButtonHeight),
                CairoFont.ButtonText().WithFontSize(ButtonFontSize),
                EnumButtonStyle.Normal, "preset-" + preset.Code);
            composer.AddStaticText(preset.Summary, CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(x, summaryY, columnWidth, SummaryHeight));
        }

        SingleComposer = composer.EndChildElements().Compose();
    }

    bool OnChoose(LodPreset preset)
    {
        choose(preset);
        return TryClose();
    }

    public override void OnGuiClosed()
    {
        base.OnGuiClosed();
        closed();
    }
}
