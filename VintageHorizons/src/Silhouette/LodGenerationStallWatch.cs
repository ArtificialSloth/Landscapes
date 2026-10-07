namespace VintageHorizons.Silhouette;

/// <summary>What one builder tick concluded about the world-generation thread.</summary>
public enum LodGeneratorVerdict
{
    /// <summary>Nothing to report.</summary>
    Fine,

    /// <summary>
    /// Silent for the whole window while the thread itself kept making passes: what is
    /// outstanding was lost, not stuck. The builder asks again.
    /// </summary>
    Lost,

    /// <summary>Silent, and the thread made no passes (or none could be seen): declared once.</summary>
    Stalled,
}

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
/// Silence is not proof of a stuck thread, though. The engine (1.22.7) drops a preview without
/// answering when it is dequeued in the first chunk-thread tick after a pause: the thread runs
/// that tick before it releases the world-generation hold it took when pausing, so the preview's
/// own attempt to take the hold fails and the request is discarded. On 2026-10-07 that turned an
/// Escape-menu pause into "previews stopped for this session" while the thread kept generating.
/// So when the caller can see the thread's own pass count (the chunk thread rest counts every
/// pass over its queue), silence with passes still going is <see cref="LodGeneratorVerdict.Lost"/>,
/// and only silence with the thread standing still is a stall.
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

    /// <summary>
    /// Passes over the queue during the silence that prove the thread is alive. A live thread
    /// makes hundreds a second; a stuck one is inside a single pass and makes none.
    /// </summary>
    public const long AlivePasses = 100;

    readonly long silenceMs;
    readonly long tickGapMs;
    long quietSinceMs;
    long lastTickMs;
    bool ticked;
    long? lastPasses;
    long? quietSincePasses;

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

    /// <summary>Times the silence was judged lost work while the thread kept going.</summary>
    public int LostVerdicts { get; private set; }

    /// <summary>A probe or a preview came back.</summary>
    public void Answered(long nowMs)
    {
        Restart(nowMs);
        if (Stalled) LateAnswers++;
    }

    /// <summary>
    /// Once per builder tick. <paramref name="threadPasses"/> is the chunk thread's running pass
    /// count, or null when it cannot be seen; without it every silence is a stall, as before.
    /// </summary>
    public LodGeneratorVerdict Tick(long nowMs, bool workOutstanding, long? threadPasses = null)
    {
        bool onTime = ticked && nowMs - lastTickMs <= tickGapMs;
        ticked = true;
        lastTickMs = nowMs;
        lastPasses = threadPasses;
        if (Stalled) return LodGeneratorVerdict.Fine;
        if (!workOutstanding || !onTime)
        {
            // Nothing asked, or the builder itself was not running: the generator owes nothing.
            Restart(nowMs);
            return LodGeneratorVerdict.Fine;
        }
        if (nowMs - quietSinceMs < silenceMs) return LodGeneratorVerdict.Fine;

        if (threadPasses is long passes && quietSincePasses is long since && passes - since >= AlivePasses)
        {
            LostVerdicts++;
            Restart(nowMs);
            return LodGeneratorVerdict.Lost;
        }

        Stalled = true;
        SilentSinceMs = quietSinceMs;
        StalledAtMs = nowMs;
        return LodGeneratorVerdict.Stalled;
    }

    void Restart(long nowMs)
    {
        quietSinceMs = nowMs;
        quietSincePasses = lastPasses;
    }
}
