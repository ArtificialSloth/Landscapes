namespace VintageHorizons;

// One prepared selection, one chosen surface stage and at most one deferred draw.
// Kept independent of GL so missing/repeated/out-of-order callbacks are testable.
internal sealed class LodWaterFrameState
{
    long frame = -1;
    long world = -1;
    bool opaqueFinished;
    bool claimed;
    public bool UseOit { get; private set; }

    public void Invalidate()
    {
        frame = world = -1;
        UseOit = opaqueFinished = claimed = false;
    }

    public void Prepare(long frameId, long worldId, bool oitRequested, bool transparentEnabled, bool oitAvailable)
    {
        frame = frameId;
        world = worldId;
        UseOit = oitRequested && transparentEnabled && oitAvailable;
        opaqueFinished = claimed = false;
    }

    public bool IsPrepared(long frameId, long worldId) => frame == frameId && world == worldId && frame >= 0;

    public void FinishOpaque(long frameId, long worldId)
    {
        if (IsPrepared(frameId, worldId)) opaqueFinished = true;
    }

    public bool TryClaimOit(long frameId, long worldId)
    {
        if (!IsPrepared(frameId, worldId) || !UseOit || !opaqueFinished || claimed) return false;
        claimed = true;
        return true;
    }
}
