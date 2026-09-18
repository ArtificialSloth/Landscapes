namespace VintageHorizons;

/// <summary>
/// Client-owned horizon appearance and reach. Defaults are Farseer 1.4.0's classic values;
/// every value is persisted, and normalization keeps old or hand-edited config files finite.
/// </summary>
internal readonly record struct LodSilhouetteSettings(
    bool Enabled,
    int ReachBlocks,
    float SkyTint,
    float TintRed,
    float TintGreen,
    float TintBlue,
    float TintStrength,
    float LightLevelBias,
    float FadeBias,
    float GlobeEffect,
    float UnderlayDepthBlocks,
    float UnderlayWidthBlocks,
    float OutlineDetail,
    float HillSoftness,
    float FogStrength,
    float FogCoverage,
    float FogSpillBlocks,
    float FogHeightBlocks,
    float FogFloorDepthBlocks,
    float FogBillowSizeBlocks,
    float FogDriftSpeed,
    float FogLayerDetail,
    float FogEdgeSoftness)
{
    public const int MinReachBlocks = 1024;
    public const int MaxReachBlocks = 32768;
    public const int ReachStepBlocks = 512;

    public static LodSilhouetteSettings Default => new(
        Enabled: true,
        ReachBlocks: 8192,
        SkyTint: 5f,
        TintRed: 0.26f,
        TintGreen: 0.29f,
        TintBlue: 0.45f,
        TintStrength: 0.40f,
        LightLevelBias: 0.70f,
        FadeBias: 0.40f,
        GlobeEffect: 0.05f,
        UnderlayDepthBlocks: Silhouette.LodSilhouetteField.DefaultUnderlayDepthBlocks,
        UnderlayWidthBlocks: Silhouette.LodSilhouetteField.DefaultUnderlayWidthBlocks,
        OutlineDetail: Silhouette.LodSilhouetteField.DefaultOutlineDetail,
        HillSoftness: Silhouette.LodSilhouetteField.DefaultHillSoftness,
        FogStrength: LodSilhouetteFogVolume.DefaultStrength,
        FogCoverage: LodSilhouetteFogVolume.DefaultCoverage,
        FogSpillBlocks: LodSilhouetteFogVolume.DefaultSpillBlocks,
        FogHeightBlocks: LodSilhouetteFogVolume.DefaultHeightBlocks,
        FogFloorDepthBlocks: LodSilhouetteFogVolume.DefaultFloorDepthBlocks,
        FogBillowSizeBlocks: LodSilhouetteFogVolume.DefaultBillowSizeBlocks,
        FogDriftSpeed: LodSilhouetteFogVolume.DefaultDriftSpeed,
        FogLayerDetail: LodSilhouetteFogVolume.DefaultLayerDetail,
        FogEdgeSoftness: LodSilhouetteFogVolume.DefaultEdgeSoftness);

    public static int ClampReach(int blocks)
    {
        double safe = Math.Clamp((double)blocks, MinReachBlocks, MaxReachBlocks);
        int snapped = (int)(Math.Round(safe / ReachStepBlocks) * ReachStepBlocks);
        return Math.Clamp(snapped, MinReachBlocks, MaxReachBlocks);
    }

    public LodSilhouetteSettings Normalize() => new(
        Enabled,
        ClampReach(ReachBlocks),
        Dial(SkyTint, Default.SkyTint, 0f, 10f),
        Dial(TintRed, Default.TintRed, 0f, 1f),
        Dial(TintGreen, Default.TintGreen, 0f, 1f),
        Dial(TintBlue, Default.TintBlue, 0f, 1f),
        Dial(TintStrength, Default.TintStrength, 0f, 1f),
        Dial(LightLevelBias, Default.LightLevelBias, 0.51f, 0.99f),
        Dial(FadeBias, Default.FadeBias, 0.05f, 0.95f),
        Dial(GlobeEffect, Default.GlobeEffect, 0f, 0.2f),
        Dial(UnderlayDepthBlocks, Default.UnderlayDepthBlocks,
            Silhouette.LodSilhouetteField.MinUnderlayDepthBlocks,
            Silhouette.LodSilhouetteField.MaxUnderlayDepthBlocks),
        Dial(UnderlayWidthBlocks, Default.UnderlayWidthBlocks,
            Silhouette.LodSilhouetteField.MinUnderlayWidthBlocks,
            Silhouette.LodSilhouetteField.MaxUnderlayWidthBlocks),
        Silhouette.LodSilhouetteField.NormalizeOutlineDetail(OutlineDetail),
        Silhouette.LodSilhouetteField.NormalizeHillSoftness(HillSoftness),
        Dial(FogStrength, Default.FogStrength,
            LodSilhouetteFogVolume.MinStrength, LodSilhouetteFogVolume.MaxStrength),
        Dial(FogCoverage, Default.FogCoverage,
            LodSilhouetteFogVolume.MinCoverage, LodSilhouetteFogVolume.MaxCoverage),
        Dial(FogSpillBlocks, Default.FogSpillBlocks,
            LodSilhouetteFogVolume.MinSpillBlocks, LodSilhouetteFogVolume.MaxSpillBlocks),
        Dial(FogHeightBlocks, Default.FogHeightBlocks,
            LodSilhouetteFogVolume.MinHeightBlocks, LodSilhouetteFogVolume.MaxHeightBlocks),
        Dial(FogFloorDepthBlocks, Default.FogFloorDepthBlocks,
            LodSilhouetteFogVolume.MinFloorDepthBlocks,
            LodSilhouetteFogVolume.MaxFloorDepthBlocks),
        Dial(FogBillowSizeBlocks, Default.FogBillowSizeBlocks,
            LodSilhouetteFogVolume.MinBillowSizeBlocks,
            LodSilhouetteFogVolume.MaxBillowSizeBlocks),
        Dial(FogDriftSpeed, Default.FogDriftSpeed,
            LodSilhouetteFogVolume.MinDriftSpeed, LodSilhouetteFogVolume.MaxDriftSpeed),
        Dial(FogLayerDetail, Default.FogLayerDetail,
            LodSilhouetteFogVolume.MinLayerDetail, LodSilhouetteFogVolume.MaxLayerDetail),
        Dial(FogEdgeSoftness, Default.FogEdgeSoftness,
            LodSilhouetteFogVolume.MinEdgeSoftness, LodSilhouetteFogVolume.MaxEdgeSoftness));

    static float Dial(float value, float fallback, float min, float max) =>
        Math.Clamp(float.IsFinite(value) ? value : fallback, min, max);
}

/// <summary>Live preview and status hooks used by both settings entry points.</summary>
internal sealed record SilhouetteTuning(
    LodSilhouetteSettings Initial,
    Action<LodSilhouetteSettings> Preview,
    Func<string> Status);
