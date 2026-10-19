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

    internal static int SnapDrawDistance(double value) => LodSilhouetteSettings.ClampReach(
        (int)Math.Round(value));

    // Reach is independent of the cached draw edge. A shorter value still has a useful meaning:
    // Farseer fills otherwise-uncovered gaps inside that radius while the cache may continue
    // farther. Coupling the two made an 8,192-block cache pin this control at 8,192 forever.
    internal static int SnapSilhouetteReach(double value) =>
        LodSilhouetteSettings.ClampReach((int)Math.Round(value));
}

/// <summary>
/// One logarithmic distance map with six draggable LOD boundaries plus the real Vanilla, cached
/// draw, and silhouette-reach handles. The faint blue span is the silhouette's territory.
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
    readonly Action<int> onDrawDistanceChanged;
    readonly Action<int> onSilhouetteReachChanged;
    readonly int[] values = new int[LodWorld.MaxLevel];
    readonly LoadedTexture[] markerLabels = new LoadedTexture[LodWorld.MaxLevel];
    readonly LoadedTexture[] bandLabels = new LoadedTexture[LodWorld.MaxLevel + 1];
    readonly LoadedTexture vanillaBandLabel;
    LoadedTexture vanillaLabel;
    LoadedTexture drawLabel;
    LoadedTexture silhouetteLabel;
    LoadedTexture solidTexture;
    int vanillaViewDistance;
    int drawDistance;
    int silhouetteReach;
    bool silhouettesEnabled;
    int vanillaAtDragStart;
    int dragging = NoMarker;

    const int NoMarker = -1;
    const int VanillaMarker = LodWorld.MaxLevel;
    const int DrawMarker = LodWorld.MaxLevel + 1;
    const int SilhouetteMarker = LodWorld.MaxLevel + 2;
    // Four lanes of marker boxes around the 34-tall band: Vanilla and Cache on top, L1/L3/L5
    // above the band, L2/L4/L6 below it, Farseer at the bottom. A two-line LOD box is about 39
    // tall and a one-line box about 23, so each lane starts clear of the one before: at 46 and 84
    // the L4 box sat on the Farseer box (owner screenshot, 0.4.166). The element is 220 tall.
    const double BandOffsetY = 100;
    const double TopDistanceLabelOffsetY = -92;
    const double UpperLodLabelOffsetY = -46;
    const double LowerLodLabelOffsetY = 42;
    const double BottomDistanceLabelOffsetY = 90;

    public override bool Focusable => true;

    /// <summary>
    /// Shows the LOD boundaries without letting those six move. The vanilla handle stays live:
    /// automatic mode owns only the mod's thresholds, not the game's render-distance setting.
    /// </summary>
    public bool ReadOnly { get; set; }

    public LodThresholdScaleElement(ICoreClientAPI api, IReadOnlyList<int> initialValues,
        int vanillaViewDistance, int drawDistance, int silhouetteReach, bool silhouettesEnabled,
        Action<int, int> onChanged, Action<int> onVanillaChanged,
        Action<int> onDrawDistanceChanged, Action<int> onSilhouetteReachChanged,
        ElementBounds bounds) : base(api, bounds)
    {
        this.onChanged = onChanged;
        this.onVanillaChanged = onVanillaChanged;
        this.onDrawDistanceChanged = onDrawDistanceChanged;
        this.onSilhouetteReachChanged = onSilhouetteReachChanged;
        this.vanillaViewDistance = Math.Clamp(vanillaViewDistance,
            LodThresholdScaleLayout.MinVanillaDistance,
            LodThresholdScaleLayout.MaxVanillaDistance);
        this.drawDistance = LodThresholdScaleLayout.SnapDrawDistance(drawDistance);
        this.silhouetteReach = LodThresholdScaleLayout.SnapSilhouetteReach(silhouetteReach);
        this.silhouettesEnabled = silhouettesEnabled;
        SetValues(initialValues);
        MouseOverCursor = "move";

        int markerWidth = scaledi(64);
        int markerHeight = scaledi(38);
        for (int i = 0; i < markerLabels.Length; i++)
        {
            markerLabels[i] = api.Gui.TextTexture.GenTextTexture(
                MarkerText(i), CairoFont.WhiteDetailText(), markerWidth, markerHeight,
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
        drawLabel = NewDistanceLabel("Cache", this.drawDistance);
        silhouetteLabel = NewDistanceLabel("Farseer", this.silhouetteReach);

        solidTexture = new LoadedTexture(api, 0, 1, 1);
        api.Render.LoadOrUpdateTextureFromRgba(
            new[] { unchecked((int)0xFFFFFFFF) }, false, 0, ref solidTexture);
    }

    public void SetValues(IReadOnlyList<int> newValues)
    {
        int[] normalized = LodWorld.NormalizeLevelThresholds(
            newValues, LodWorld.DefaultLevelThresholds[0]);
        Array.Copy(normalized, values, values.Length);
        for (int i = 0; i < markerLabels.Length; i++)
            if (markerLabels[i] != null) RefreshMarkerLabel(i);
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

    public void SetDrawDistance(int value)
    {
        int next = LodThresholdScaleLayout.SnapDrawDistance(value);
        if (drawDistance == next) return;
        drawDistance = next;
        drawLabel.Dispose();
        drawLabel = NewDistanceLabel("Cache", drawDistance);
        SetSilhouette(silhouetteReach, silhouettesEnabled);
    }

    public void SetSilhouette(int reach, bool enabled)
    {
        int next = LodThresholdScaleLayout.SnapSilhouetteReach(reach);
        silhouettesEnabled = enabled;
        if (silhouetteReach == next) return;
        silhouetteReach = next;
        silhouetteLabel.Dispose();
        silhouetteLabel = NewDistanceLabel("Farseer", silhouetteReach);
    }

    public override void RenderInteractiveElements(float deltaTime)
    {
        float left = (float)Bounds.renderX + (float)scaled(12);
        float width = Math.Max(1, (float)Bounds.InnerWidth - (float)scaled(24));
        float bandY = (float)Bounds.renderY + (float)scaled(BandOffsetY);
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

        // Cached terrain stops at its handle. Keep its LOD bands visible beyond that point as a
        // planning reference, but dim them so the map does not claim they are drawn. When the
        // silhouette switch is on, its translucent span begins at exactly the same edge.
        float drawFraction = LodThresholdScaleLayout.FractionFor(drawDistance);
        float drawX = left + width * drawFraction;
        if (drawX < left + width)
            api.Render.Render2DTexture(solidTexture.TextureId, drawX, bandY,
                left + width - drawX, bandHeight, z + 1.2f,
                new Vec4f(0.02f, 0.02f, 0.03f, 0.34f));
        if (silhouettesEnabled)
        {
            float silhouetteX = left + width
                * LodThresholdScaleLayout.FractionFor(silhouetteReach);
            float silhouetteStart = Math.Min(drawX, silhouetteX);
            float silhouetteWidth = Math.Max(1, Math.Abs(silhouetteX - drawX));
            api.Render.Render2DTexture(solidTexture.TextureId, silhouetteStart, bandY,
                silhouetteWidth, bandHeight, z + 1.3f,
                new Vec4f(0.22f, 0.34f, 0.58f, 0.30f));
            RenderBandBorder(silhouetteStart, bandY, silhouetteWidth, bandHeight, z + 1.4f,
                ColorUtil.ToRgba(175, 112, 154, 218));
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
        float vanillaLabelX = Math.Clamp(vanillaX - vanillaLabel.Width / 2f,
            left, left + width - vanillaLabel.Width);
        float vanillaLabelY = bandY + (float)scaled(TopDistanceLabelOffsetY);
        float vanillaConnectorY = vanillaLabelY + vanillaLabel.Height;
        api.Render.RenderRectangle(vanillaX - 1, vanillaConnectorY, z + 2,
            3, bandY + bandHeight + (float)scaled(8) - vanillaConnectorY, vanillaColor);
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
            float handleY = bandY + (float)scaled(
                above ? UpperLodLabelOffsetY : LowerLodLabelOffsetY);
            LoadedTexture label = markerLabels[i];
            float connectorY = above ? handleY + label.Height : bandY + bandHeight;
            float connectorEndY = above ? bandY : handleY;
            float connectorHeight = Math.Abs(connectorEndY - connectorY) + (float)scaled(1);
            int color = LevelColors[i + 1].Border;

            api.Render.RenderRectangle(x, Math.Min(connectorY, connectorEndY), z + 1,
                1, connectorHeight, color);
            float labelX = Math.Clamp(x - label.Width / 2f, left, left + width - label.Width);
            api.Render.RenderRectangle(labelX - (float)scaled(3), handleY, z + 1,
                label.Width + (float)scaled(6), label.Height + (float)scaled(2), color);
            api.Render.Render2DTexture(solidTexture.TextureId,
                labelX - (float)scaled(2), handleY + (float)scaled(1),
                label.Width + (float)scaled(4), label.Height, z + 1.1f,
                new Vec4f(0.08f, 0.08f, 0.08f, 0.92f));
            api.Render.Render2DLoadedTexture(label, labelX, handleY, z + 2);
            if (dragging == i)
            {
                api.Render.RenderRectangle(labelX - (float)scaled(5),
                    handleY - (float)scaled(2), z + 0.9f,
                    label.Width + (float)scaled(10), label.Height + (float)scaled(6), color);
            }
        }


        RenderDistanceMarker(drawDistance, drawLabel, TopDistanceLabelOffsetY,
            ColorUtil.ToRgba(255, 238, 178, 72), left, width, bandY, bandHeight, z);
        if (silhouettesEnabled)
            RenderDistanceMarker(silhouetteReach, silhouetteLabel, BottomDistanceLabelOffsetY,
                ColorUtil.ToRgba(255, 126, 177, 242), left, width, bandY, bandHeight, z);
    }

    public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
    {
        if (args.Button != EnumMouseButton.Left) return;
        dragging = NearestMarker(args.X, args.Y);
        if (ReadOnly && dragging >= 0 && dragging < LodWorld.MaxLevel)
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
        double bandY = Bounds.renderY + scaled(BandOffsetY);
        int nearest = VanillaMarker;
        double nearestDistance = double.MaxValue;

        double vanillaX = left + width
            * LodThresholdScaleLayout.FractionFor(vanillaViewDistance);
        double vanillaY = bandY + scaled(TopDistanceLabelOffsetY) + vanillaLabel.Height / 2.0;
        double vanillaDx = mouseX - vanillaX;
        double vanillaDy = mouseY - vanillaY;
        nearestDistance = vanillaDx * vanillaDx + vanillaDy * vanillaDy;

        void Consider(int marker, int value, double labelOffsetY, LoadedTexture label)
        {
            double x = left + width * LodThresholdScaleLayout.FractionFor(value);
            double y = bandY + scaled(labelOffsetY) + label.Height / 2.0;
            double dx = mouseX - x, dy = mouseY - y;
            double distance = dx * dx + dy * dy;
            if (distance >= nearestDistance) return;
            nearestDistance = distance;
            nearest = marker;
        }

        Consider(DrawMarker, drawDistance, TopDistanceLabelOffsetY, drawLabel);
        if (silhouettesEnabled)
            Consider(SilhouetteMarker, silhouetteReach, BottomDistanceLabelOffsetY, silhouetteLabel);

        for (int i = 0; i < values.Length; i++)
        {
            double x = left + width * LodThresholdScaleLayout.FractionFor(values[i]);
            double y = bandY + scaled((i & 1) == 0
                ? UpperLodLabelOffsetY : LowerLodLabelOffsetY)
                + markerLabels[i].Height / 2.0;
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
        if (dragging == DrawMarker)
        {
            int next = LodThresholdScaleLayout.SnapDrawDistance(raw);
            if (next == drawDistance) return;
            int oldSilhouetteReach = silhouetteReach;
            SetDrawDistance(next);
            onDrawDistanceChanged(drawDistance);
            if (silhouetteReach != oldSilhouetteReach)
                onSilhouetteReachChanged(silhouetteReach);
            return;
        }
        if (dragging == SilhouetteMarker)
        {
            int next = LodThresholdScaleLayout.SnapSilhouetteReach(raw);
            if (next == silhouetteReach) return;
            SetSilhouette(next, silhouettesEnabled);
            onSilhouetteReachChanged(silhouetteReach);
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
        RefreshMarkerLabel(dragging);
        onChanged(dragging, value);
    }

    public override void Dispose()
    {
        foreach (LoadedTexture label in markerLabels) label?.Dispose();
        foreach (LoadedTexture label in bandLabels) label?.Dispose();
        vanillaBandLabel.Dispose();
        vanillaLabel.Dispose();
        drawLabel.Dispose();
        silhouetteLabel.Dispose();
        solidTexture?.Dispose();
        base.Dispose();
    }

    LoadedTexture NewVanillaLabel() => api.Gui.TextTexture.GenTextTexture(
        $"Vanilla {vanillaViewDistance:N0}", CairoFont.WhiteDetailText(),
        scaledi(112), scaledi(20), null!, EnumTextOrientation.Center, true);

    LoadedTexture NewDistanceLabel(string name, int value) => api.Gui.TextTexture.GenTextTexture(
        $"{name} {value:N0}", CairoFont.WhiteDetailText(), scaledi(132), scaledi(20),
        null!, EnumTextOrientation.Center, true);

    string MarkerText(int index) => $"L{index + 1}\n({values[index]})";

    void RefreshMarkerLabel(int index)
    {
        markerLabels[index]?.Dispose();
        markerLabels[index] = api.Gui.TextTexture.GenTextTexture(
            MarkerText(index), CairoFont.WhiteDetailText(), scaledi(64), scaledi(38),
            null!, EnumTextOrientation.Center, true);
    }

    void RenderDistanceMarker(int value, LoadedTexture label, double labelOffsetY, int color,
        float left, float width, float bandY, float bandHeight, float z)
    {
        float x = left + width * LodThresholdScaleLayout.FractionFor(value);
        float handleY = bandY + (float)scaled(labelOffsetY);
        bool below = labelOffsetY > 0;
        float connectorY = below ? bandY + bandHeight : handleY + label.Height;
        float connectorEndY = below ? handleY : bandY;
        api.Render.RenderRectangle(x, Math.Min(connectorY, connectorEndY), z + 2,
            2, Math.Abs(connectorEndY - connectorY) + (float)scaled(1), color);
        float labelX = Math.Clamp(x - label.Width / 2f, left, left + width - label.Width);
        api.Render.RenderRectangle(labelX - (float)scaled(3), handleY, z + 2,
            label.Width + (float)scaled(6), label.Height + (float)scaled(2), color);
        api.Render.Render2DTexture(solidTexture.TextureId, labelX - (float)scaled(2),
            handleY + (float)scaled(1), label.Width + (float)scaled(4), label.Height, z + 2.1f,
            new Vec4f(0.08f, 0.08f, 0.08f, 0.94f));
        api.Render.Render2DLoadedTexture(label, labelX, handleY, z + 3);
    }

    void RenderBandBorder(float x, float y, float width, float height, float z, int color)
    {
        api.Render.RenderRectangle(x, y, z, width, 1, color);
        api.Render.RenderRectangle(x, y + height - 1, z, width, 1, color);
        api.Render.RenderRectangle(x, y, z, 1, height, color);
        api.Render.RenderRectangle(x + width - 1, y, z, 1, height, color);
    }
}
