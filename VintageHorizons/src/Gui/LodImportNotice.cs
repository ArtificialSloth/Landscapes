using System.Globalization;
using Vintagestory.API.Client;

namespace VintageHorizons;

/// <summary>
/// The popup about explored land still to import (owner, 2026-10-21). A world that already has
/// explored land when the mod arrives - or land the old spawn-centred sweep never reached - has
/// it imported in the background, and on a large world that takes hours. Every player it affects
/// is told once a session, including on multiplayer servers, but only about land inside their own
/// Cache distance and only when it would take ten minutes or more (<see cref="Net.LodImportPresenter"/>).
/// </summary>
internal sealed class LodImportNotice : GuiDialog
{
    const double Width = 600;
    const double TextHeight = 290;
    const double TitleBar = 30;
    const double Margin = 20;

    readonly string text;

    public LodImportNotice(ICoreClientAPI capi, string text) : base(capi)
    {
        this.text = text;
        Compose();
    }

    public override string? ToggleKeyCombinationCode => null;

    public override string DebugName => "dodenslandscapes-import-notice";

    internal const string Title = "Doden's Landscapes: importing explored land";

    /// <summary>The popup's text; the first sentence is the owner's wording (2026-10-21).</summary>
    internal static string Text(int columns, int cacheBlocks, string time, bool paceMeasured,
        bool multiplayer)
    {
        string count = columns.ToString("N0", CultureInfo.InvariantCulture);
        string where = cacheBlocks > 0
            ? $"your Cache distance is set to {cacheBlocks.ToString("N0", CultureInfo.InvariantCulture)} "
                + $"blocks - about {count} chunks within that distance need to be imported"
            : $"your Cache distance is unlimited - about {count} chunks across the world need to be imported";
        string pace = paceMeasured ? "At the current pace" : "At a typical pace";
        string shared = multiplayer
            ? " On a server the import is shared by everyone online, so it can take longer."
            : "";

        return $"This world has explored land that is not in your distant view yet: {where}.\n\n"
            + $"Doden's Landscapes is importing it now, nearest to {(multiplayer ? "the players" : "you")} "
            + $"first. {pace} that takes {time}. It slows down while you travel, so the game can load "
            + $"the land around you first.{shared}\n\n"
            + "You can play as normal meanwhile. Distant land fills in as it arrives, and a faint line "
            + "in the bottom-right corner of the screen shows how far along it is. Later visits skip "
            + "everything already imported.";
    }

    void Compose()
    {
        // Laid out like the other login notice: a fixed window, its background filling it, and
        // children placed from the window's top-left with the title bar's height allowed for.
        double buttonY = TitleBar + 10 + TextHeight + 10;
        ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, Width, buttonY + 34 + Margin)
            .WithAlignment(EnumDialogArea.CenterMiddle);
        ElementBounds textBounds = ElementBounds.Fixed(Margin, TitleBar + 10, Width - 2 * Margin, TextHeight);

        SingleComposer = capi.Gui
            .CreateCompo(DebugName, dialogBounds)
            .AddShadedDialogBG(ElementBounds.Fill, true)
            .AddDialogTitleBar(Title, () => TryClose())
            .BeginChildElements()
            .AddStaticText(text, CairoFont.WhiteSmallText(), textBounds)
            .AddButton("OK", () => TryClose(),
                ElementBounds.Fixed(Width - Margin - 140, buttonY, 140, 34), EnumButtonStyle.Normal, "ok")
            .EndChildElements()
            .Compose();
    }
}

/// <summary>
/// The faint progress line in the bottom-right corner while explored land nearby is imported. No
/// background and no input: a ghost over the game, redrawn only when its words or opacity change.
/// </summary>
internal sealed class LodImportHud : HudElement
{
    const double Width = 460;
    const double Height = 24;

    readonly CairoFont font;
    string shown = "";
    float shownAlpha = -1;

    public override string DebugName => "dodenslandscapes-import-progress";
    public override bool Focusable => false;
    public override double DrawOrder => 0.05;
    public override bool ShouldReceiveMouseEvents() => false;
    public override bool ShouldReceiveKeyboardEvents() => false;

    public LodImportHud(ICoreClientAPI capi) : base(capi)
    {
        font = CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Right);
        ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, Width, Height)
            .WithAlignment(EnumDialogArea.RightBottom)
            .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, -GuiStyle.DialogToScreenPadding);
        SingleComposer = capi.Gui
            .CreateCompo(DebugName, dialogBounds)
            .AddDynamicText("", font, ElementBounds.Fixed(0, 0, Width, Height), "line")
            .Compose();
    }

    /// <summary>Shows the line at that opacity; redraws only when either changed.</summary>
    public void Show(string text, float alpha)
    {
        if (text == shown && Math.Abs(alpha - shownAlpha) < 0.01f) return;
        shown = text;
        shownAlpha = alpha;
        font.Color[3] = alpha;
        SingleComposer.GetDynamicText("line").SetNewText(text, autoHeight: false, forceRedraw: true);
    }
}
