using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageHorizons;

/// <summary>
/// The stable arithmetic behind the distance map. The editable LOD thresholds still begin at
/// <see cref="LodWorld.MinDetailDistance"/>, but the map begins closer to the player so an ordinary
/// vanilla render distance has a real place on it instead of falling off its left edge.
/// </summary>
internal static class LodThresholdScaleLayout
{
    internal const int MinScaleDistance = 32;
    internal const int MinVanillaDistance = 32;
    internal const int MaxVanillaDistance = 1536;
    internal const int VanillaDistanceStep = 32;

    internal static float FractionFor(double value)
    {
        double clamped = Math.Clamp(value, MinScaleDistance, LodWorld.MaxLevelThreshold);
        double ratio = LodWorld.MaxLevelThreshold / (double)MinScaleDistance;
        return (float)Math.Log(clamped / MinScaleDistance, ratio);
    }

    internal static (float Start, float End) Band(
        IReadOnlyList<int> thresholds, int level)
    {
        if (level < 0 || level > LodWorld.MaxLevel)
            throw new ArgumentOutOfRangeException(nameof(level));

        float start = level == 0 ? 0f : FractionFor(thresholds[level - 1]);
        float end = level == LodWorld.MaxLevel ? 1f : FractionFor(thresholds[level]);
        return (start, end);
    }

    /// <summary>
    /// The picture gives vanilla exclusive ownership of everything up to its marker. L0 begins
    /// at that edge, even though the real renderer may overlap the two briefly for its seam.
    /// Empty spans are kept empty when a hand-made LOD threshold falls inside vanilla's range.
    /// </summary>
    internal static (float Start, float End) VisibleBand(
        IReadOnlyList<int> thresholds, int level, int vanillaViewDistance)
    {
        (float start, float end) = Band(thresholds, level);
        float vanillaEnd = FractionFor(vanillaViewDistance);
        return (Math.Max(start, vanillaEnd), Math.Max(end, vanillaEnd));
    }

    internal static int SnapVanillaDistance(double value)
    {
        int snapped = (int)Math.Round(value / VanillaDistanceStep) * VanillaDistanceStep;
        return Math.Clamp(snapped, MinVanillaDistance, MaxVanillaDistance);
    }
}

/// <summary>
/// One logarithmic distance map with six draggable LOD boundaries. The coloured spans answer the
/// question the old bare line did not: which LOD is used between any two markers. A separate white
/// handle adjusts the player's vanilla render distance on the same scale.
/// </summary>
internal sealed class LodThresholdScaleElement : GuiElementControl
{
    readonly record struct LevelColor(byte R, byte G, byte B)
    {
        internal Vec4f Fill => new(R / 255f, G / 255f, B / 255f, 0.30f);
        internal int Border => ColorUtil.ToRgba(255, R, G, B);
    }

    // L0 through L6. A boundary's box takes the colour of the region to its right: the L1
    // marker is therefore exactly the L1 colour, rather than the old one-place-shifted palette.
    static readonly LevelColor[] LevelColors =
    {
        new(61, 156, 209),
        new(79, 194, 176),
        new(135, 199, 102),
        new(224, 189, 74),
        new(237, 135, 64),
        new(191, 99, 209),
        new(224, 82, 120),
    };

    readonly Action<int, int> onChanged;
    readonly Action<int> onVanillaChanged;
    readonly int[] values = new int[LodWorld.MaxLevel];
    readonly LoadedTexture[] markerLabels = new LoadedTexture[LodWorld.MaxLevel];
    readonly LoadedTexture[] bandLabels = new LoadedTexture[LodWorld.MaxLevel + 1];
    readonly LoadedTexture vanillaBandLabel;
    LoadedTexture vanillaLabel;
    LoadedTexture solidTexture;
    int vanillaViewDistance;
    int vanillaAtDragStart;
    int dragging = NoMarker;

    const int NoMarker = -1;
    const int VanillaMarker = LodWorld.MaxLevel;

    public override bool Focusable => true;

    /// <summary>
    /// Shows the LOD boundaries without letting those six move. The vanilla handle stays live:
    /// automatic mode owns only the mod's thresholds, not the game's render-distance setting.
    /// </summary>
    public bool ReadOnly { get; set; }

    public LodThresholdScaleElement(ICoreClientAPI api, IReadOnlyList<int> initialValues,
        int vanillaViewDistance, Action<int, int> onChanged, Action<int> onVanillaChanged,
        ElementBounds bounds) : base(api, bounds)
    {
        this.onChanged = onChanged;
        this.onVanillaChanged = onVanillaChanged;
        this.vanillaViewDistance = Math.Clamp(vanillaViewDistance,
            LodThresholdScaleLayout.MinVanillaDistance,
            LodThresholdScaleLayout.MaxVanillaDistance);
        SetValues(initialValues);
        MouseOverCursor = "move";

        int markerWidth = scaledi(34);
        int markerHeight = scaledi(24);
        for (int i = 0; i < markerLabels.Length; i++)
        {
            markerLabels[i] = api.Gui.TextTexture.GenTextTexture(
                $"L{i + 1}", CairoFont.WhiteSmallText(), markerWidth, markerHeight,
                null!, EnumTextOrientation.Center, true);
        }

        int bandWidth = scaledi(30);
        int bandHeight = scaledi(20);
        for (int i = 0; i < bandLabels.Length; i++)
        {
            bandLabels[i] = api.Gui.TextTexture.GenTextTexture(
                $"L{i}", CairoFont.WhiteDetailText(), bandWidth, bandHeight,
                null!, EnumTextOrientation.Center, true);
        }

        vanillaBandLabel = api.Gui.TextTexture.GenTextTexture(
            "Vanilla", CairoFont.WhiteDetailText(), scaledi(54), scaledi(20),
            null!, EnumTextOrientation.Center, true);
        vanillaLabel = NewVanillaLabel();

        solidTexture = new LoadedTexture(api, 0, 1, 1);
        api.Render.LoadOrUpdateTextureFromRgba(
            new[] { unchecked((int)0xFFFFFFFF) }, false, 0, ref solidTexture);
    }

    public void SetValues(IReadOnlyList<int> newValues)
    {
        int[] normalized = LodWorld.NormalizeLevelThresholds(
            newValues, LodWorld.DefaultLevelThresholds[0]);
        Array.Copy(normalized, values, values.Length);
    }

    public int[] GetValues() => (int[])values.Clone();

    public void SetVanillaDistance(int value)
    {
        int clamped = Math.Clamp(value, LodThresholdScaleLayout.MinVanillaDistance,
            LodThresholdScaleLayout.MaxVanillaDistance);
        if (vanillaViewDistance == clamped) return;
        vanillaViewDistance = clamped;
        vanillaLabel.Dispose();
        vanillaLabel = NewVanillaLabel();
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        float left = (float)Bounds.renderX + (float)scaled(12);
        float width = Math.Max(1, (float)Bounds.InnerWidth - (float)scaled(24));
        float bandY = (float)Bounds.renderY + (float)scaled(54);
        float bandHeight = (float)scaled(34);
        const float z = 50;

        // Vanilla owns a separate neutral span. The coloured L0-L6 spans begin only at its
        // edge, so the diagram never implies that L0 and vanilla cover the same area.
        float vanillaEnd = LodThresholdScaleLayout.FractionFor(vanillaViewDistance);
        float vanillaWidth = Math.Max(1, width * vanillaEnd);
        api.Render.Render2DTexture(solidTexture.TextureId, left, bandY,
            vanillaWidth, bandHeight, z, new Vec4f(0.32f, 0.34f, 0.38f, 0.48f));
        RenderBandBorder(left, bandY, vanillaWidth, bandHeight, z + 0.1f,
            ColorUtil.ToRgba(180, 205, 210, 218));
        if (vanillaWidth >= vanillaBandLabel.Width + scaled(6))
        {
            api.Render.Render2DLoadedTexture(vanillaBandLabel,
                left + (vanillaWidth - vanillaBandLabel.Width) / 2f,
                bandY + (bandHeight - vanillaBandLabel.Height) / 2f, z + 1);
        }

        for (int level = 0; level <= LodWorld.MaxLevel; level++)
        {
            (float start, float end) = LodThresholdScaleLayout.VisibleBand(
                values, level, vanillaViewDistance);
            if (end <= start) continue;
            float x = left + width * start;
            float segmentWidth = Math.Max(1, width * (end - start));
            api.Render.Render2DTexture(solidTexture.TextureId, x, bandY,
                segmentWidth, bandHeight, z, LevelColors[level].Fill);
            RenderBandBorder(x, bandY, segmentWidth, bandHeight, z + 0.1f,
                ColorUtil.ToRgba(150, 210, 215, 220));

            LoadedTexture label = bandLabels[level];
            if (segmentWidth >= label.Width + scaled(6))
            {
                api.Render.Render2DLoadedTexture(label,
                    x + (segmentWidth - label.Width) / 2f,
                    bandY + (bandHeight - label.Height) / 2f, z + 1);
            }
        }

        // Powers of two remain useful landmarks, but are deliberately quieter than the LOD
        // boundaries. The expanded 32-block near end gives vanilla render distance room.
        for (int value = LodThresholdScaleLayout.MinScaleDistance;
             value <= LodWorld.MaxLevelThreshold; value *= 2)
        {
            float x = left + width * LodThresholdScaleLayout.FractionFor(value);
            api.Render.RenderRectangle(x, bandY + bandHeight, z + 0.2f,
                1, (float)scaled(5), ColorUtil.ToRgba(210, 125, 130, 140));
        }

        // Vanilla is a real slider too. Its value follows the handle while dragging, while the
        // game's setting changes once on mouse-up just like vanilla's own Graphics slider.
        float vanillaX = left + width * LodThresholdScaleLayout.FractionFor(vanillaViewDistance);
        int vanillaColor = ColorUtil.ToRgba(255, 245, 245, 235);
        api.Render.RenderRectangle(vanillaX - 1, bandY - (float)scaled(8), z + 2,
            3, bandHeight + (float)scaled(16), vanillaColor);
        float vanillaLabelX = Math.Clamp(vanillaX - vanillaLabel.Width / 2f,
            left, left + width - vanillaLabel.Width);
        float vanillaLabelY = bandY - (float)scaled(52);
        api.Render.RenderRectangle(vanillaLabelX - (float)scaled(3), vanillaLabelY,
            z + 1, vanillaLabel.Width + (float)scaled(6),
            vanillaLabel.Height + (float)scaled(2), vanillaColor);
        api.Render.Render2DTexture(solidTexture.TextureId,
            vanillaLabelX - (float)scaled(2), vanillaLabelY + (float)scaled(1),
            vanillaLabel.Width + (float)scaled(4), vanillaLabel.Height, z + 1.1f,
            new Vec4f(0.08f, 0.08f, 0.08f, 0.92f));
        api.Render.Render2DLoadedTexture(vanillaLabel, vanillaLabelX, vanillaLabelY, z + 2);

        for (int i = 0; i < values.Length; i++)
        {
            float x = left + width * LodThresholdScaleLayout.FractionFor(values[i]);
            bool above = (i & 1) == 0;
            float handleY = bandY + (above ? -(float)scaled(30) : (float)scaled(46));
            float connectorY = above ? handleY + (float)scaled(24) : bandY + bandHeight;
            float connectorHeight = Math.Abs(bandY - connectorY) + (float)scaled(1);
            int color = LevelColors[i + 1].Border;

            api.Render.RenderRectangle(x, Math.Min(connectorY, bandY), z + 1,
                1, connectorHeight, color);
            api.Render.RenderRectangle(x - (float)scaled(17), handleY, z + 1,
                (float)scaled(34), (float)scaled(24), color);
            api.Render.RenderRectangle(x - (float)scaled(16), handleY + (float)scaled(1), z + 1,
                (float)scaled(32), (float)scaled(22), ColorUtil.ToRgba(235, 28, 31, 35));
            api.Render.Render2DLoadedTexture(markerLabels[i],
                x - markerLabels[i].Width / 2f, handleY, z + 2);
            if (dragging == i)
            {
                api.Render.RenderRectangle(x - (float)scaled(19), handleY - (float)scaled(2), z + 1,
                    (float)scaled(38), (float)scaled(28), color);
            }
        }
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (args.Button != EnumMouseButton.Left) return;
        dragging = NearestMarker(args.X, args.Y);
        if (ReadOnly && dragging != VanillaMarker)
        {
            dragging = NoMarker;
            return;
        }
        vanillaAtDragStart = vanillaViewDistance;
        UpdateDragging(args.X);
        args.Handled = true;
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (dragging == NoMarker) return;
        UpdateDragging(args.X);
        args.Handled = true;
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        if (dragging == NoMarker) return;
        UpdateDragging(args.X);
        bool vanillaChanged = dragging == VanillaMarker
            && vanillaViewDistance != vanillaAtDragStart;
        dragging = NoMarker;
        if (vanillaChanged) onVanillaChanged(vanillaViewDistance);
        args.Handled = true;
    }

    int NearestMarker(int mouseX, int mouseY)
    {
        double left = Bounds.renderX + scaled(12);
        double width = Math.Max(1, Bounds.InnerWidth - scaled(24));
        double bandY = Bounds.renderY + scaled(54);
        int nearest = VanillaMarker;
        double nearestDistance = double.MaxValue;

        double vanillaX = left + width
            * LodThresholdScaleLayout.FractionFor(vanillaViewDistance);
        double vanillaY = bandY - scaled(41);
        double vanillaDx = mouseX - vanillaX;
        double vanillaDy = mouseY - vanillaY;
        nearestDistance = vanillaDx * vanillaDx + vanillaDy * vanillaDy;

        for (int i = 0; i < values.Length; i++)
        {
            double x = left + width * LodThresholdScaleLayout.FractionFor(values[i]);
            double y = bandY + (((i & 1) == 0) ? -scaled(18) : scaled(58));
            double dx = mouseX - x;
            double dy = mouseY - y;
            double distance = dx * dx + dy * dy;
            if (distance >= nearestDistance) continue;
            nearestDistance = distance;
            nearest = i;
        }
        return nearest;
    }

    void UpdateDragging(int mouseX)
    {
        if (dragging == NoMarker) return;

        double left = Bounds.renderX + scaled(12);
        double width = Math.Max(1, Bounds.InnerWidth - scaled(24));
        double fraction = Math.Clamp((mouseX - left) / width, 0, 1);
        double ratio = LodWorld.MaxLevelThreshold
            / (double)LodThresholdScaleLayout.MinScaleDistance;
        double raw = LodThresholdScaleLayout.MinScaleDistance * Math.Pow(ratio, fraction);
        if (dragging == VanillaMarker)
        {
            SetVanillaDistance(LodThresholdScaleLayout.SnapVanillaDistance(raw));
            return;
        }

        int value = (int)Math.Round(raw / LodWorld.ThresholdStepBlocks)
            * LodWorld.ThresholdStepBlocks;

        int min = dragging == 0
            ? LodWorld.MinDetailDistance
            : values[dragging - 1] + LodWorld.ThresholdStepBlocks;
        int max = dragging == values.Length - 1
            ? LodWorld.MaxLevelThreshold
            : values[dragging + 1] - LodWorld.ThresholdStepBlocks;
        value = Math.Clamp(value, min, max);
        if (values[dragging] == value) return;

        values[dragging] = value;
        onChanged(dragging, value);
    }

    public override void Dispose()
    {
        foreach (LoadedTexture label in markerLabels) label?.Dispose();
        foreach (LoadedTexture label in bandLabels) label?.Dispose();
        vanillaBandLabel.Dispose();
        vanillaLabel.Dispose();
        solidTexture?.Dispose();
        base.Dispose();
    }

    LoadedTexture NewVanillaLabel() => api.Gui.TextTexture.GenTextTexture(
        $"Vanilla {vanillaViewDistance:N0}", CairoFont.WhiteDetailText(),
        scaledi(112), scaledi(20), null!, EnumTextOrientation.Center, true);

    void RenderBandBorder(float x, float y, float width, float height, float z, int color)
    {
        api.Render.RenderRectangle(x, y, z, width, 1, color);
        api.Render.RenderRectangle(x, y + height - 1, z, width, 1, color);
        api.Render.RenderRectangle(x, y, z, 1, height, color);
        api.Render.RenderRectangle(x + width - 1, y, z, 1, height, color);
    }
}
