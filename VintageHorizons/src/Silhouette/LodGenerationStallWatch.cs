namespace VintageHorizons.Silhouette;

/// <summary>
/// Notices when the world-generation thread stops answering the silhouette builder.
///
/// The builder's existence probes and previews are all served by the engine's single
/// world-generation thread, which answers at least one preview per thread tick even while it
/// generates real chunks. In 0.4.77, on the owner's modded world, that thread stopped in the
/// middle of the builder's previews and never answered again, and leaving the world then hung
/// while the game waited for it (Session 92). Nothing here can restart it. The watch only
/// notices, so the builder stops adding work and the log says what happened, rather than the
/// only symptom being a hang at exit.
///
/// Silence counts only while the builder has work outstanding on that thread and its own tick
/// runs on time. The server clock stops while the game is paused, and a long gap between the
/// builder's ticks (a save, a hitch) restarts the count instead of charging it to the generator.
/// Pure, so the fast tier drives it with a fake clock.
/// </summary>
public sealed class LodGenerationStallWatch
{
    /// <summary>Silence with work outstanding that is declared a stall.</summary>
    public const long DefaultSilenceMs = 20_000;

    /// <summary>A gap between builder ticks longer than this restarts the count.</summary>
    public const long DefaultTickGapMs = 5_000;

    readonly long silenceMs;
    readonly long tickGapMs;
    long quietSinceMs;
    long lastTickMs;
    bool ticked;

    public LodGenerationStallWatch(long silenceMs = DefaultSilenceMs, long tickGapMs = DefaultTickGapMs)
    {
        this.silenceMs = silenceMs;
        this.tickGapMs = tickGapMs;
    }

    /// <summary>Latched: once declared, the stall stands for the session.</summary>
    public bool Stalled { get; private set; }

    /// <summary>When the generator was last heard from, as of the declaration.</summary>
    public long SilentSinceMs { get; private set; }

    public long StalledAtMs { get; private set; }

    /// <summary>Answers after the declaration: the generator was slow rather than stuck.</summary>
    public int LateAnswers { get; private set; }

    /// <summary>A probe or a preview came back.</summary>
    public void Answered(long nowMs)
    {
        quietSinceMs = nowMs;
        if (Stalled) LateAnswers++;
    }

    /// <summary>Once per builder tick. True only on the tick the stall is declared.</summary>
    public bool Tick(long nowMs, bool workOutstanding)
    {
        bool onTime = ticked && nowMs - lastTickMs <= tickGapMs;
        ticked = true;
        lastTickMs = nowMs;
        if (Stalled) return false;
        if (!workOutstanding || !onTime)
        {
            // Nothing asked, or the builder itself was not running: the generator owes nothing.
            quietSinceMs = nowMs;
            return false;
        }
        if (nowMs - quietSinceMs < silenceMs) return false;
        Stalled = true;
        SilentSinceMs = quietSinceMs;
        StalledAtMs = nowMs;
        return true;
    }
}
