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
}

/// <summary>
/// One logarithmic distance map with six draggable LOD boundaries. The coloured spans answer the
/// question the old bare line did not: which LOD is used between any two markers. A separate white
/// marker shows the player's configured vanilla render distance on the same scale.
/// </summary>
internal sealed class LodThresholdScaleElement : GuiElementControl
{
    // L0 through L6. Labels carry the meaning too; colour is never the only distinction.
    static readonly Vec4f[] BandColors =
    {
        new(0.24f, 0.61f, 0.82f, 0.30f),
        new(0.31f, 0.76f, 0.69f, 0.30f),
        new(0.53f, 0.78f, 0.40f, 0.30f),
        new(0.88f, 0.74f, 0.29f, 0.30f),
        new(0.93f, 0.53f, 0.25f, 0.30f),
        new(0.75f, 0.39f, 0.82f, 0.30f),
        new(0.88f, 0.32f, 0.47f, 0.30f),
    };

    static readonly int[] MarkerColors =
    {
        ColorUtil.ToRgba(255, 104, 190, 255),
        ColorUtil.ToRgba(255, 108, 230, 170),
        ColorUtil.ToRgba(255, 190, 225, 95),
        ColorUtil.ToRgba(255, 245, 190, 75),
        ColorUtil.ToRgba(255, 245, 135, 75),
        ColorUtil.ToRgba(255, 220, 105, 145),
    };

    readonly Action<int, int> onChanged;
    readonly int vanillaViewDistance;
    readonly int[] values = new int[LodWorld.MaxLevel];
    readonly LoadedTexture[] markerLabels = new LoadedTexture[LodWorld.MaxLevel];
    readonly LoadedTexture[] bandLabels = new LoadedTexture[LodWorld.MaxLevel + 1];
    readonly LoadedTexture vanillaLabel;
    LoadedTexture solidTexture;
    int dragging = -1;

    public override bool Focusable => true;

    /// <summary>
    /// Shows the boundaries without letting them move. While automatic thresholds are on the
    /// values come from the screen, and a drag would be overwritten on the next frame.
    /// </summary>
    public bool ReadOnly { get; set; }

    public LodThresholdScaleElement(ICoreClientAPI api, IReadOnlyList<int> initialValues,
        int vanillaViewDistance, Action<int, int> onChanged, ElementBounds bounds) : base(api, bounds)
    {
        this.onChanged = onChanged;
        this.vanillaViewDistance = Math.Max(1, vanillaViewDistance);
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

        vanillaLabel = api.Gui.TextTexture.GenTextTexture(
            $"Vanilla {this.vanillaViewDistance:N0}", CairoFont.WhiteDetailText(),
            scaledi(104), scaledi(18), null!, EnumTextOrientation.Center, true);

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

    public override void RenderInteractiveElements(float deltaTime)
    {
        float left = (float)Bounds.renderX + (float)scaled(12);
        float width = Math.Max(1, (float)Bounds.InnerWidth - (float)scaled(24));
        float bandY = (float)Bounds.renderY + (float)scaled(54);
        float bandHeight = (float)scaled(34);
        const float z = 50;

        // Seven translucent spans turn the axis into a map: L0 before the first boundary,
        // L1 between the first two, and so on through L6 at the far end.
        for (int level = 0; level <= LodWorld.MaxLevel; level++)
        {
            (float start, float end) = LodThresholdScaleLayout.Band(values, level);
            float x = left + width * start;
            float segmentWidth = Math.Max(1, width * (end - start));
            api.Render.Render2DTexture(solidTexture.TextureId, x, bandY,
                segmentWidth, bandHeight, z, BandColors[level]);
            api.Render.RenderRectangle(x, bandY, z + 0.1f,
                segmentWidth, bandHeight, ColorUtil.ToRgba(150, 210, 215, 220));

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

        // The configured vanilla distance is context, not another editable threshold. White and
        // slightly heavier makes it legible over every band colour.
        float vanillaX = left + width * LodThresholdScaleLayout.FractionFor(vanillaViewDistance);
        int vanillaColor = ColorUtil.ToRgba(255, 245, 245, 235);
        api.Render.RenderRectangle(vanillaX - 1, bandY - (float)scaled(8), z + 2,
            3, bandHeight + (float)scaled(16), vanillaColor);
        float vanillaLabelX = Math.Clamp(vanillaX - vanillaLabel.Width / 2f,
            left, left + width - vanillaLabel.Width);
        float vanillaLabelY = bandY - (float)scaled(51);
        api.Render.Render2DTexture(solidTexture.TextureId,
            vanillaLabelX - (float)scaled(3), vanillaLabelY,
            vanillaLabel.Width + (float)scaled(6), vanillaLabel.Height, z + 1,
            new Vec4f(0.08f, 0.08f, 0.08f, 0.82f));
        api.Render.Render2DLoadedTexture(vanillaLabel, vanillaLabelX, vanillaLabelY, z + 2);

        for (int i = 0; i < values.Length; i++)
        {
            float x = left + width * LodThresholdScaleLayout.FractionFor(values[i]);
            bool above = (i & 1) == 0;
            float handleY = bandY + (above ? -(float)scaled(38) : (float)scaled(46));
            float connectorY = above ? handleY + (float)scaled(24) : bandY + bandHeight;
            float connectorHeight = Math.Abs(bandY - connectorY) + (float)scaled(1);
            int color = MarkerColors[i];

            api.Render.RenderRectangle(x, Math.Min(connectorY, bandY), z + 1,
                1, connectorHeight, color);
            api.Render.RenderRectangle(x - (float)scaled(17), handleY, z + 1,
                (float)scaled(34), (float)scaled(24), color);
            api.Render.RenderRectangle(x - (float)scaled(16), handleY + (float)scaled(1), z + 1,
                (float)scaled(32), (float)scaled(22), color);
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
        if (ReadOnly || args.Button != EnumMouseButton.Left) return;
        dragging = NearestMarker(args.X, args.Y);
        UpdateDragging(args.X);
        args.Handled = true;
    }

    public override void OnMouseMove(ICoreClientAPI api, MouseEvent args)
    {
        if (dragging < 0) return;
        UpdateDragging(args.X);
        args.Handled = true;
    }

    public override void OnMouseUp(ICoreClientAPI api, MouseEvent args)
    {
        if (dragging < 0) return;
        UpdateDragging(args.X);
        dragging = -1;
        args.Handled = true;
    }

    int NearestMarker(int mouseX, int mouseY)
    {
        double left = Bounds.renderX + scaled(12);
        double width = Math.Max(1, Bounds.InnerWidth - scaled(24));
        double bandY = Bounds.renderY + scaled(54);
        int nearest = 0;
        double nearestDistance = double.MaxValue;

        for (int i = 0; i < values.Length; i++)
        {
            double x = left + width * LodThresholdScaleLayout.FractionFor(values[i]);
            double y = bandY + (((i & 1) == 0) ? -scaled(26) : scaled(58));
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
        if (dragging < 0) return;

        double left = Bounds.renderX + scaled(12);
        double width = Math.Max(1, Bounds.InnerWidth - scaled(24));
        double fraction = Math.Clamp((mouseX - left) / width, 0, 1);
        double ratio = LodWorld.MaxLevelThreshold
            / (double)LodThresholdScaleLayout.MinScaleDistance;
        double raw = LodThresholdScaleLayout.MinScaleDistance * Math.Pow(ratio, fraction);
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
        vanillaLabel.Dispose();
        solidTexture?.Dispose();
        base.Dispose();
    }
}
