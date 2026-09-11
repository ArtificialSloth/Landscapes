using Vintagestory.API.Client;

namespace VintageHorizons;

/// <summary>
/// Opt-in development side panel. It deliberately has no close action: use `.vhdiag off` so
/// the command state and the visible window cannot disagree. Hold Alt to release the mouse,
/// then drag the title bar.
/// </summary>
internal sealed class VintageHorizonsDiagnosticsHud : HudElement
{
    const double Width = 400;
    const double PreferredHeight = 800;
    const double ScreenMargin = 8;
    const double Margin = 12;
    const double ContentTop = 38;
    const double ColumnGap = 10;
    const int ColumnCount = 2;
    const int MaxVisibleRows = 72;
    const float MaximumFontSize = 7.5f;

    readonly Func<string[]> snapshot;
    readonly string[] previous = new string[ColumnCount];
    long nextRefreshAt;

    public override string DebugName => "vintagehorizons-development-config";
    public override string? ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => false;
    public override bool Focusable => false;
    public override double DrawOrder => 0.08;

    public VintageHorizonsDiagnosticsHud(ICoreClientAPI capi, Func<string[]> snapshot)
        : base(capi)
    {
        this.snapshot = snapshot;
        ComposeDialog();
        RefreshNow();
    }

    void ComposeDialog()
    {
        double scale = Math.Max(0.1, GuiElement.scaled(1));
        double screenHeight = capi.Render.FrameHeight > 0
            ? capi.Render.FrameHeight / scale
            : 720;
        double height = Math.Max(300,
            Math.Min(PreferredHeight, Math.Floor(screenHeight - ScreenMargin * 2)));
        double contentHeight = height - ContentTop - Margin;
        double fontSize = Math.Min(MaximumFontSize,
            contentHeight / (MaxVisibleRows * 1.35));

        ElementBounds dialogBounds = ElementBounds.Fixed(0, 0, Width, height)
            .WithAlignment(EnumDialogArea.RightMiddle)
            .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0);
        ElementBounds clipBounds = ElementBounds.Fixed(
            Margin, ContentTop, Width - Margin * 2, contentHeight);
        ElementBounds insetBounds = clipBounds.FlatCopy().FixedGrow(3);
        double columnWidth = (clipBounds.fixedWidth - ColumnGap) / ColumnCount;
        CairoFont font = CairoFont.WhiteDetailText().WithFontSize((float)fontSize);

        GuiComposer composer = capi.Gui
            .CreateCompo(DebugName, dialogBounds)
            .AddShadedDialogBG(ElementBounds.Fill, true)
            .AddDialogTitleBar("VH DIAGNOSTICS — Alt+drag", OnTitleClose)
            .AddInset(insetBounds, 2)
            .BeginClip(clipBounds);

        for (int i = 0; i < ColumnCount; i++)
        {
            ElementBounds textBounds = ElementBounds.Fixed(
                Margin + i * (columnWidth + ColumnGap), ContentTop,
                columnWidth, contentHeight);
            composer.AddDynamicText("loading…", font, textBounds, $"column-{i}");
        }

        SingleComposer = composer.EndClip().Compose();
        SingleComposer.UnfocusOwnElements();
    }

    /// <summary>The title bar remains draggable but its close glyph is intentionally inert.</summary>
    void OnTitleClose() { }

    public void RefreshNow()
    {
        string[] columns;
        try
        {
            columns = snapshot();
        }
        catch (Exception e)
        {
            columns = new[]
            {
                "Vintage Horizons\n\nConfiguration panel error = ON\n" + e.GetType().Name,
                "",
            };
        }

        if (columns.Length != ColumnCount)
            columns = new[] { "Vintage Horizons\n\nConfiguration panel error = ON", "" };

        // The snapshot contains settings only, so it normally never changes. More importantly,
        // an unchanged poll performs no Cairo text rebuild and no bounds/layout recalculation.
        for (int i = 0; i < ColumnCount; i++)
        {
            string text = Sanitize(columns[i]);
            if (text == previous[i]) continue;
            previous[i] = text;
            SingleComposer.GetDynamicText($"column-{i}")
                .SetNewText(text, autoHeight: false, forceRedraw: true, async: false);
        }
    }

    public override void OnRenderGUI(float deltaTime)
    {
        long now = Environment.TickCount64;
        if (now >= nextRefreshAt)
        {
            nextRefreshAt = now + 1000;
            RefreshNow();
        }
        base.OnRenderGUI(deltaTime);
    }

    static string Sanitize(string text) => text
        .Replace('<', '[')
        .Replace('>', ']')
        .Replace('\r', ' ');
}
