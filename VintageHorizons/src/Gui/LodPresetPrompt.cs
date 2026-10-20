using Vintagestory.API.Client;

namespace VintageHorizons;

/// <summary>
/// The first-run preset prompt (owner, 2026-10-19). A fresh install draws Quality so players see
/// what the mod can do; a player whose machine cannot afford it is the one who goes looking for
/// settings, and this puts the choice in front of every player once, at their first login.
///
/// Three buttons, one per <see cref="LodPreset"/>, each with what it draws underneath. Closing it
/// any other way keeps the settings as they are. Either way it is not shown again.
/// </summary>
internal sealed class LodPresetPrompt : GuiDialog
{
    const double Width = 720;
    const double TitleBar = 30;
    const double Margin = 20;
    const double IntroHeight = 110;
    const double ColumnGap = 16;
    const double ButtonHeight = 36;
    const double SummaryHeight = 66;

    readonly Action<LodPreset> choose;
    readonly Action closed;

    public LodPresetPrompt(ICoreClientAPI capi, Action<LodPreset> choose, Action closed) : base(capi)
    {
        this.choose = choose;
        this.closed = closed;
        Compose();
    }

    public override string? ToggleKeyCombinationCode => null;

    public override string DebugName => "dodenslandscapes-preset-prompt";

    internal const string Title = "Doden's Landscapes: choose a preset";

    internal const string IntroText =
        "How much of the distant landscape should be drawn? Quality shows everything the mod can "
        + "do. If your frame rate suffers, pick a lighter preset.\n\n"
        + "You can change this, or any single setting, any time in the game's Settings on the "
        + "Landscapes page, or by typing .dlconfig in chat.";

    void Compose()
    {
        double columnWidth = (Width - 2 * Margin - 2 * ColumnGap) / 3;
        double buttonY = TitleBar + 10 + IntroHeight + 10;
        double summaryY = buttonY + ButtonHeight + 8;
        ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, Width, summaryY + SummaryHeight + Margin)
            .WithAlignment(EnumDialogArea.CenterMiddle);

        GuiComposer composer = capi.Gui
            .CreateCompo(DebugName, dialogBounds)
            .AddShadedDialogBG(ElementBounds.Fill, true)
            .AddDialogTitleBar(Title, () => TryClose())
            .BeginChildElements()
            .AddStaticText(IntroText, CairoFont.WhiteSmallText(),
                ElementBounds.Fixed(Margin, TitleBar + 10, Width - 2 * Margin, IntroHeight));

        for (int i = 0; i < LodPreset.All.Length; i++)
        {
            LodPreset preset = LodPreset.All[i];
            double x = Margin + i * (columnWidth + ColumnGap);
            string label = preset == LodPreset.Quality ? preset.Name + " (default)" : preset.Name;
            composer.AddButton(label, () => OnChoose(preset),
                ElementBounds.Fixed(x, buttonY, columnWidth, ButtonHeight),
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
