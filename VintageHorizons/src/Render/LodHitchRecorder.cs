using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using VintageHorizons.Net;

namespace VintageHorizons;

/// <summary>
/// Background work the server half of this process issued, counted so the client's hitch
/// report can say what was running beside a bad minute. Only meaningful in singleplayer,
/// where both halves share the process; a client of a remote server sees zeros.
/// </summary>
public static class LodHitchActivity
{
    static long sweepLoads;
    static long snowRefreshLoads;
    static long auditRecaptures;
    static long savegameRereads;

    public static void CountSweepLoad() => Interlocked.Increment(ref sweepLoads);
    public static void CountSnowRefreshLoad() => Interlocked.Increment(ref snowRefreshLoads);
    public static void CountAuditRecapture() => Interlocked.Increment(ref auditRecaptures);
    public static void CountSavegameReread() => Interlocked.Increment(ref savegameRereads);

    internal static void Read(long[] into)
    {
        into[0] = Interlocked.Read(ref sweepLoads);
        into[1] = Interlocked.Read(ref snowRefreshLoads);
        into[2] = Interlocked.Read(ref auditRecaptures);
        into[3] = Interlocked.Read(ref savegameRereads);
    }

    sealed class JobTally
    {
        public long Bytes;
        public long Runs;
    }

    static readonly ConcurrentDictionary<string, JobTally> jobs = new();

    /// <summary>
    /// Exactly what one of this mod's jobs allocated on its own thread. The allocation
    /// sampler attributes garbage to threads; this splits a shared thread - the client or
    /// server main thread, or the shared worker pool - into this mod's jobs and the rest.
    /// </summary>
    public static void AddJob(string job, long bytes)
    {
        JobTally tally = jobs.GetOrAdd(job, static _ => new JobTally());
        Interlocked.Add(ref tally.Bytes, bytes);
        Interlocked.Increment(ref tally.Runs);
    }

    /// <summary>The allocation mark to pass to <see cref="EndJob"/>.</summary>
    public static long StartJob() => GC.GetAllocatedBytesForCurrentThread();

    public static void EndJob(string job, long start) =>
        AddJob(job, GC.GetAllocatedBytesForCurrentThread() - start);

    /// <summary>A tick listener that measures what it allocates under <paramref name="job"/>.</summary>
    public static Action<float> Measured(string job, Action<float> body) => dt =>
    {
        long start = StartJob();
        try
        {
            body(dt);
        }
        finally
        {
            EndJob(job, start);
        }
    };

    internal static void ReadJobs(Dictionary<string, (long Bytes, long Runs)> into)
    {
        into.Clear();
        foreach (KeyValuePair<string, JobTally> job in jobs)
        {
            into[job.Key] = (Interlocked.Read(ref job.Value.Bytes), Interlocked.Read(ref job.Value.Runs));
        }
    }
}

/// <summary>
/// Brackets this mod's render callbacks in the game's own frame profiler, so a hitch report
/// can tell our drawing apart from vanilla's, and measures what each callback allocates.
/// Inert unless the hitch recorder has switched the profiler on.
///
/// The vanilla time since the previous profiler step is closed under a "before vh:" name
/// first; without that it would be folded into whatever step the game marks next.
/// </summary>
public static class LodHitchMarks
{
    static FrameProfilerUtil? profiler;

    internal static void Attach(FrameProfilerUtil? attached) => profiler = attached;

    /// <summary>
    /// An open range. It holds the profiler that opened it, so it always closes on that one
    /// even if recording is switched off in between; a default scope closes nothing.
    /// </summary>
    public readonly struct Scope
    {
        readonly FrameProfilerUtil? profiler;
        readonly string? job;
        readonly long startBytes;

        internal Scope(FrameProfilerUtil profiler, string job)
        {
            this.profiler = profiler;
            this.job = job;
            startBytes = GC.GetAllocatedBytesForCurrentThread();
        }

        public void Leave()
        {
            if (profiler == null || job == null) return;
            profiler.Leave();
            LodHitchActivity.AddJob(job, GC.GetAllocatedBytesForCurrentThread() - startBytes);
        }
    }

    /// <summary>Opens a range, or returns an empty scope when not recording.</summary>
    public static Scope Enter(string vanillaBefore, string ours)
    {
        FrameProfilerUtil? recording = profiler;
        if (recording == null) return default;
        recording.Mark(vanillaBefore);
        recording.Enter(ours);
        return new Scope(recording, ours);
    }
}

/// <summary>
/// Records frame-time hitches and what was unusually slow in them, and writes one report a
/// minute to the client log.
///
/// The owner's complaint is the 1% low: periodic frames far slower than their neighbours
/// while exploring. Watching a frame-time graph cannot say which of the candidates - the
/// seam fog, Farseer, the savegame sweep, the snow refresh, garbage collection or vanilla -
/// was responsible, and he cannot copy anything out of the game. So the attribution is done
/// here, from three sources:
///
/// - the game's own frame profiler, which the engine already brackets every frame with and
///   which names each step of it; this switches it on silently (it only prints when a frame
///   exceeds its threshold, and the threshold is set out of reach);
/// - this mod's render callbacks, marked separately inside that profile;
/// - .NET garbage-collection pause time, which stops every thread at once, and in
///   singleplayer is driven as much by the server half of the process as by the client.
///
/// Each step is judged against its own moving average, including frames where it did not
/// run, so a step that costs 5 ms once a second shows as a 5 ms excess on that frame rather
/// than averaging itself into invisibility.
///
/// The minute's report is written by a background task. Written on the render thread, its
/// lines cost a 14-58 ms frame at the start of every minute in the owner's 0.4.129-0.4.131
/// sessions - the game's logger flushes its file on every line. The render thread now only
/// takes the closing readings and swaps in a fresh window.
/// </summary>
public sealed class LodHitchRecorder : IRenderer
{
    const int WarmupFrames = 256;
    const double ReportSeconds = 60;
    const double AverageWeight = 1.0 / 64;
    const double BucketMs = 0.1;
    const int HistogramBuckets = 10_000;
    const int MaxRecords = 1 << 16;
    const int WorstKept = 5;
    const int MaxDepth = 6;
    const int MaxNames = 1024;

    const string OwnBefore = "before vh:hitchrecorder";
    const string OwnName = "vh:hitchrecorder";
    const string ReportJob = "hitch report writing";

    static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
    const double Mb = 1024.0 * 1024.0;

    // Null only in the offline checks, which feed frames directly.
    readonly ICoreClientAPI? capi;
    readonly ILogger logger;

    public double RenderOrder => 0;
    public int RenderRange => 0;

    bool enabled = true;
    bool armed;
    bool legendLogged;
    FrameProfilerUtil? profiler;
    bool savedPrintSlowTicks;
    int savedThreshold;

    ProfileEntryRange? pendingRoot;
    double pendingGcMs;
    int pendingGcGen;
    TimeSpan lastGcPause;
    readonly int[] lastGcCounts = new int[3];

    readonly Dictionary<string, int> ids = new();
    readonly List<string> names = new();
    double[] current = new double[128];
    double[] average = new double[128];
    readonly int idBetween;
    readonly int idUntracked;
    readonly int idGc;
    readonly int idOther;

    double averagePeriodMs;
    int warmup;

    double lastX, lastZ;
    bool hasLastPos;

    LodAllocationSampler? sampler;
    bool samplerUnavailable;

    struct FrameRecord
    {
        public float PeriodMs;
        public float BaselineMs;
        public float GcMs;
        public byte GcGen;
        public float SecondsIn;
        public short Id0, Id1, Id2;
        public float Excess0, Excess1, Excess2;
    }

    /// <summary>
    /// One report's frames and the readings that bracket them. The render thread fills the open
    /// window; a closed one belongs to its report until the report hands it back for reuse.
    /// </summary>
    sealed class Window
    {
        public readonly FrameRecord[] Records = new FrameRecord[MaxRecords];
        public int RecordCount;
        public readonly int[] Histogram = new int[HistogramBuckets];
        public int OverflowFrames;
        public double OverflowSumMs;
        public int Frames;
        public double PeriodSumMs;
        public readonly FrameRecord[] Worst = new FrameRecord[WorstKept];
        public int WorstCount;
        public double MovedBlocks;

        public long Start;
        public DateTime StartClock;
        public readonly long[] ActivityAtStart = new long[4];
        public readonly int[] GcAtStart = new int[3];
        public TimeSpan GcPauseAtStart;
        public long AllocatedAtStart;
        public readonly Dictionary<string, (long Bytes, long Runs)> JobsAtStart = new();
        public ChunkThreadRest.Counts ChunkAtStart;

        // Taken on the render thread as the window closes.
        public string? Reason;
        public double Seconds;
        public DateTime EndClock;
        public readonly long[] ActivityNow = new long[4];
        public readonly int[] GcNow = new int[3];
        public TimeSpan GcPauseNow;
        public long AllocatedNow;
        public readonly Dictionary<string, (long Bytes, long Runs)> JobsNow = new();
        public LodAllocationSampler.Window? Sampled;
        public string? ChunkThread;
        public string[] Names = Array.Empty<string>();
        public bool SinglePlayer;
    }

    Window window = new();
    Window? spare;
    Task reports = Task.CompletedTask;

    public LodHitchRecorder(ICoreClientAPI? capi, ILogger logger)
    {
        this.capi = capi;
        this.logger = logger;
        idBetween = Id("between frames");
        idUntracked = Id("untracked");
        idGc = Id("gc pause");
        idOther = Id("other");
    }

    public bool Enabled => enabled;

    /// <summary>Switches recording on or off. Off restores the profiler as it was found.</summary>
    public string SetEnabled(bool on)
    {
        if (on == enabled) return on ? "Hitch recorder is already on." : "Hitch recorder is already off.";
        if (!on)
        {
            EndWindow("recorder switched off", inBackground: false);
            Disarm();
            enabled = false;
            logger.Notification("[VintageHorizons] Hitch recorder off.");
            return "Hitch recorder off. The partial minute was written to the client log.";
        }
        enabled = true;
        logger.Notification("[VintageHorizons] Hitch recorder on; it starts with the next frame.");
        return "Hitch recorder on. It writes a report to the client log once a minute.";
    }

    /// <summary>Writes the current partial minute now, without waiting for the minute to end.</summary>
    public string ReportNow()
    {
        if (!enabled) return "Hitch recorder is off. '.vhhitch on' starts it.";
        if (window.Frames == 0) return "No frames recorded yet in this minute.";
        EndWindow("on request", inBackground: true);
        return "Hitch report written to the client log.";
    }

    /// <summary>The world is being left: write what this world's last minute held.</summary>
    public void Flush(string reason)
    {
        EndWindow(reason, inBackground: false);
        pendingRoot = null;
        warmup = 0;
        hasLastPos = false;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!enabled || capi == null) return;
        LodHitchMarks.Scope range = default;
        try
        {
            FrameProfilerUtil? engineProfiler = capi.World.FrameProfiler;
            if (engineProfiler == null) return;

            // Something else - vanilla's own '.debug logticks' toggle - can switch the profiler
            // off under us. Re-arm rather than record a breakdown that stopped updating.
            if (!armed || !(engineProfiler.PrintSlowTicks || engineProfiler.Enabled))
            {
                Arm(engineProfiler);
                return;
            }

            range = LodHitchMarks.Enter(OwnBefore, OwnName);
            Observe(engineProfiler);
        }
        catch (Exception e)
        {
            // A diagnostic must never take the game down, which 0.4.128's did. Say so once,
            // hand the profiler back as it was found, and stay off for the session.
            enabled = false;
            logger.Error(
                "[VintageHorizons] The hitch recorder failed and has switched itself off for this "
                + "session; the game itself is unaffected. '.vhhitch on' tries again. {0}", e);
            Disarm();
        }
        finally
        {
            range.Leave();
        }
    }

    void Arm(FrameProfilerUtil engineProfiler)
    {
        if (!armed)
        {
            savedPrintSlowTicks = engineProfiler.PrintSlowTicks;
            savedThreshold = engineProfiler.PrintSlowTicksThreshold;
        }

        bool wasRecording = engineProfiler.Enabled || engineProfiler.PrintSlowTicks;
        // Collect every frame, print none: the profiler prints only frames longer than
        // the threshold, and this does its own reporting.
        engineProfiler.PrintSlowTicksThreshold = int.MaxValue;
        engineProfiler.PrintSlowTicks = true;
        // Switched on mid-frame, the profiler has no open frame to end, and End would throw
        // on it. Vanilla's own toggle opens one the same way.
        if (!wasRecording) engineProfiler.Begin(null);

        profiler = engineProfiler;
        LodHitchMarks.Attach(engineProfiler);
        LodAllocationSampler.RegisterCurrentThread("client main thread");
        if (sampler == null && !samplerUnavailable)
        {
            try
            {
                sampler = new LodAllocationSampler();
            }
            catch (Exception e)
            {
                // The frame breakdown still works without it; only "who allocated" is lost.
                samplerUnavailable = true;
                logger.Warning(
                    "[VintageHorizons] The hitch recorder cannot sample allocations here ({0}); "
                    + "its reports will show pauses but not who caused them.", e.Message);
            }
        }
        pendingRoot = null;
        warmup = 0;
        lastGcPause = GC.GetTotalPauseDuration();
        for (int g = 0; g < 3; g++) lastGcCounts[g] = GC.CollectionCount(g);

        if (!armed) StartWindow();
        armed = true;

        if (!legendLogged)
        {
            legendLogged = true;
            logger.Notification(
                "[VintageHorizons] Hitch recorder on. Once a minute this log gets the frame-time "
                + "lows and, for the slowest 1% of frames, what was unusually slow in them. Names are "
                + "the game's own frame-profiler steps plus this mod's: 'vh:*' is this mod's drawing "
                + "and 'before vh:*' the vanilla work just ahead of it; 'gametick' is every tick "
                + "handler, the game's and all mods'; 'doneMTT' main-thread tasks; "
                + "'doneRender2Default' post-processing; 'end' the buffer swap, where the CPU waits "
                + "for the graphics card; 'sleep' the FPS limiter; 'between frames' time outside the "
                + "profiled frame; 'gc pause' .NET garbage collection, which stops every thread and in "
                + "singleplayer is driven by the server half too. Each report also says who allocated: "
                + "per thread (threads named vintagehorizons-* are this mod's), per kind of object, "
                + "why each collection ran, and exactly what this mod's own jobs allocated. "
                + "'.vhhitch off' stops it; '.vhhitch' writes the current minute now.");
            logger.Notification("[VintageHorizons] Garbage collector in this process: {0}", DescribeCollector());
        }
    }

    /// <summary>
    /// The collector's mode and gen0 allowance as the runtime applied them, plus any launch
    /// setting that asked for them. A session started from a launcher with a collector
    /// setting is otherwise indistinguishable in the log from an ordinary one, and a setting
    /// the runtime capped or ignored would pass for one it applied (Session 111: asking for a
    /// 256 MB gen0 allowance gets 128 MB).
    /// </summary>
    internal static string DescribeCollector()
    {
        try
        {
            IReadOnlyDictionary<string, object> config = GC.GetConfigurationVariables();
            bool server = config.TryGetValue("ServerGC", out object? s) && s is true;
            bool background = config.TryGetValue("ConcurrentGC", out object? b) && b is true;
            long allowance = config.TryGetValue("GCGen0MaxBudget", out object? g) ? Convert.ToInt64(g) : 0;
            var text = new StringBuilder(server ? "server (every core collects)" : "workstation (one core collects)");
            text.Append($", background full collections {(background ? "on" : "off")}");
            text.Append(allowance > 0 ? $", gen0 allowance {allowance >> 20} MB" : ", gen0 allowance not reported");
            if (server)
            {
                if (config.TryGetValue("HeapCount", out object? heaps)) text.Append($" per heap, up to {heaps} heaps");
                if (config.TryGetValue("GCDynamicAdaptationMode", out object? adapt))
                    text.Append(Convert.ToInt64(adapt) == 1 ? ", heap count adaptive" : ", heap count fixed");
            }
            var settings = Environment.GetEnvironmentVariables().Keys.Cast<string>()
                .Where(k => k.StartsWith("DOTNET_GC", StringComparison.OrdinalIgnoreCase)
                    || k.StartsWith("DOTNET_gcServer", StringComparison.OrdinalIgnoreCase)
                    || k.StartsWith("COMPlus_GC", StringComparison.OrdinalIgnoreCase)
                    || k.StartsWith("COMPlus_gcServer", StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .Select(k => $"{k}={Environment.GetEnvironmentVariable(k)}")
                .ToList();
            text.Append(settings.Count == 0 ? "; no launch settings" : "; launch settings " + string.Join(", ", settings));
            return text.ToString();
        }
        catch (Exception e)
        {
            return "not readable (" + e.Message + ")";
        }
    }

    void Disarm()
    {
        LodHitchMarks.Attach(null);
        try
        {
            sampler?.Dispose();
        }
        catch
        {
            // Stopping the runtime's event session is best effort; nothing depends on it.
        }
        sampler = null;
        if (armed && profiler != null)
        {
            profiler.PrintSlowTicks = savedPrintSlowTicks;
            profiler.PrintSlowTicksThreshold = savedThreshold;
        }
        armed = false;
        profiler = null;
        pendingRoot = null;
    }

    void Observe(FrameProfilerUtil engineProfiler)
    {
        TimeSpan pause = GC.GetTotalPauseDuration();
        double gcMs = (pause - lastGcPause).TotalMilliseconds;
        lastGcPause = pause;
        int gen = 0;
        for (int g = 2; g >= 0; g--)
        {
            int count = GC.CollectionCount(g);
            if (count != lastGcCounts[g] && gen == 0) gen = g + 1;
            lastGcCounts[g] = count;
        }
        TrackMovement();
        Feed(engineProfiler.PrevRootEntry, gcMs, gen);

        if (Stopwatch.GetElapsedTime(window.Start).TotalSeconds >= ReportSeconds)
        {
            EndWindow(null, inBackground: true);
        }
    }

    /// <summary>
    /// Takes the profile of the frame that just ended. A frame's length is only known once
    /// the next one has started, so each frame is closed one callback late: its period runs
    /// from its own start to the start of the frame after it. Called directly by the checks.
    /// </summary>
    internal void Feed(ProfileEntryRange? root, double gcMs, int gen)
    {
        if (root == null || ReferenceEquals(root, pendingRoot))
        {
            pendingGcMs += gcMs;
            pendingGcGen = Math.Max(pendingGcGen, gen);
            return;
        }

        if (pendingRoot != null)
        {
            long periodTicks = root.Start - pendingRoot.Start;
            if (periodTicks > 0) CloseFrame(pendingRoot, periodTicks, pendingGcMs, pendingGcGen);
        }
        pendingRoot = root;
        pendingGcMs = gcMs;
        pendingGcGen = gen;
    }

    /// <summary>For the checks: start a window without a profiler, and write it out.</summary>
    internal void BeginWindowForChecks() => StartWindow();
    internal void WriteReport(string reason) => EndWindow(reason, inBackground: false);

    /// <summary>For the checks: end the window the way the minute does, and wait for its report.</summary>
    internal void WriteReportInBackground(string reason) => EndWindow(reason, inBackground: true);
    internal bool WaitForReports(TimeSpan timeout) => reports.Wait(timeout);

    void TrackMovement()
    {
        var pos = capi?.World.Player?.Entity?.Pos;
        if (pos == null) return;
        if (hasLastPos)
        {
            double dx = pos.X - lastX;
            double dz = pos.Z - lastZ;
            double step = Math.Sqrt(dx * dx + dz * dz);
            // A teleport is not travel.
            if (step < 64) window.MovedBlocks += step;
        }
        lastX = pos.X;
        lastZ = pos.Z;
        hasLastPos = true;
    }

    void CloseFrame(ProfileEntryRange frame, long periodTicks, double gcMs, int gcGen)
    {
        double periodMs = periodTicks * TicksToMs;
        double profiledMs = frame.ElapsedTicks * TicksToMs;
        double leavesMs = Walk(frame, 0);
        current[idUntracked] += Math.Max(0, profiledMs - leavesMs);
        current[idBetween] += Math.Max(0, periodMs - profiledMs);
        current[idGc] += gcMs;

        // Every step against its own average, then the frame folded into the averages.
        short id0 = -1, id1 = -1, id2 = -1;
        double ex0 = 0, ex1 = 0, ex2 = 0;
        for (int i = 0; i < names.Count; i++)
        {
            double value = current[i];
            current[i] = 0;
            double excess = value - average[i];
            average[i] += (value - average[i]) * AverageWeight;
            if (excess <= ex2) continue;
            if (excess > ex0)
            {
                id2 = id1; ex2 = ex1;
                id1 = id0; ex1 = ex0;
                id0 = (short)i; ex0 = excess;
            }
            else if (excess > ex1)
            {
                id2 = id1; ex2 = ex1;
                id1 = (short)i; ex1 = excess;
            }
            else
            {
                id2 = (short)i; ex2 = excess;
            }
        }

        double baseline = averagePeriodMs;
        averagePeriodMs = baseline == 0 ? periodMs : baseline + (periodMs - baseline) * AverageWeight;

        // The averages start at zero, so the first frames would report every step as excess.
        if (warmup < WarmupFrames)
        {
            warmup++;
            return;
        }

        Window w = window;
        w.Frames++;
        w.PeriodSumMs += periodMs;
        int bucket = (int)(periodMs / BucketMs);
        if (bucket < HistogramBuckets) w.Histogram[bucket]++;
        else
        {
            w.OverflowFrames++;
            w.OverflowSumMs += periodMs;
        }

        var record = new FrameRecord
        {
            PeriodMs = (float)periodMs,
            BaselineMs = (float)baseline,
            GcMs = (float)gcMs,
            GcGen = (byte)gcGen,
            SecondsIn = (float)Stopwatch.GetElapsedTime(w.Start).TotalSeconds,
            Id0 = id0, Id1 = id1, Id2 = id2,
            Excess0 = (float)ex0, Excess1 = (float)ex1, Excess2 = (float)ex2,
        };
        if (w.RecordCount < MaxRecords) w.Records[w.RecordCount++] = record;
        KeepIfWorst(w, record);
    }

    /// <summary>
    /// Adds each step's time in this frame to <see cref="current"/> and returns their sum.
    /// A range with steps inside it is split into those steps plus its own unmarked tail, so
    /// nothing is counted twice.
    /// </summary>
    double Walk(ProfileEntryRange range, int depth)
    {
        double sum = 0;
        if (range.Marks != null)
        {
            foreach (KeyValuePair<string, ProfileEntry> mark in range.Marks)
            {
                double ms = mark.Value.ElapsedTicks * TicksToMs;
                Add(mark.Key, ms);
                sum += ms;
            }
        }
        if (range.ChildRanges != null)
        {
            foreach (ProfileEntryRange child in range.ChildRanges.Values)
            {
                double childMs = child.ElapsedTicks * TicksToMs;
                if (depth < MaxDepth && (child.Marks != null || child.ChildRanges != null))
                {
                    double inner = Walk(child, depth + 1);
                    if (childMs > inner) Add(child.Code, childMs - inner);
                }
                else
                {
                    Add(child.Code, childMs);
                }
                sum += childMs;
            }
        }
        return sum;
    }

    /// <summary>
    /// Adds a step's time to this frame's tally. The slot is looked up into a local first:
    /// <c>current[Id(code)] += ms</c> reads the array before <see cref="Id"/> can grow it,
    /// so the 129th step name wrote past the end of the old array. That crashed the owner's
    /// game in 0.4.128 the moment the world-edit menu added its steps to the frame.
    /// </summary>
    void Add(string? code, double ms)
    {
        int id = Id(code);
        current[id] += ms;
    }

    int Id(string? code)
    {
        code ??= "?";
        if (ids.TryGetValue(code, out int id)) return id;
        if (names.Count >= MaxNames) return idOther;
        id = names.Count;
        names.Add(code);
        ids[code] = id;
        if (id >= current.Length)
        {
            Array.Resize(ref current, current.Length * 2);
            Array.Resize(ref average, average.Length * 2);
        }
        return id;
    }

    static void KeepIfWorst(Window w, FrameRecord record)
    {
        if (w.WorstCount < WorstKept)
        {
            w.Worst[w.WorstCount++] = record;
            return;
        }
        int least = 0;
        for (int i = 1; i < WorstKept; i++)
        {
            if (w.Worst[i].PeriodMs < w.Worst[least].PeriodMs) least = i;
        }
        if (record.PeriodMs > w.Worst[least].PeriodMs) w.Worst[least] = record;
    }

    void StartWindow()
    {
        Window w = window;
        w.Start = Stopwatch.GetTimestamp();
        w.StartClock = DateTime.Now;
        Array.Clear(w.Histogram);
        w.OverflowFrames = 0;
        w.OverflowSumMs = 0;
        w.Frames = 0;
        w.PeriodSumMs = 0;
        w.RecordCount = 0;
        w.WorstCount = 0;
        w.MovedBlocks = 0;
        w.Reason = null;
        w.Sampled = null;
        w.ChunkThread = null;
        w.Names = Array.Empty<string>();
        LodHitchActivity.Read(w.ActivityAtStart);
        for (int g = 0; g < 3; g++) w.GcAtStart[g] = GC.CollectionCount(g);
        w.GcPauseAtStart = GC.GetTotalPauseDuration();
        sampler?.TakeWindow();
        w.AllocatedAtStart = GC.GetTotalAllocatedBytes(false);
        LodHitchActivity.ReadJobs(w.JobsAtStart);
        w.ChunkAtStart = ChunkThreadRest.Read();
    }

    /// <summary>
    /// Ends the open window. Its closing readings are taken here, on the calling thread, and a
    /// fresh window takes its place at once; the report is then written on a background task
    /// when <paramref name="inBackground"/> is set, or here otherwise. Leaving the world and
    /// closing the game write in place: they must not lose the report, and a hitch there is
    /// invisible.
    /// </summary>
    void EndWindow(string? reason, bool inBackground)
    {
        Window closed = window;
        closed.Reason = reason;
        closed.Seconds = Stopwatch.GetElapsedTime(closed.Start).TotalSeconds;
        closed.EndClock = DateTime.Now;
        LodHitchActivity.Read(closed.ActivityNow);
        for (int g = 0; g < 3; g++) closed.GcNow[g] = GC.CollectionCount(g);
        closed.GcPauseNow = GC.GetTotalPauseDuration();
        closed.AllocatedNow = GC.GetTotalAllocatedBytes(false);
        closed.Sampled = sampler?.TakeWindow();
        LodHitchActivity.ReadJobs(closed.JobsNow);
        closed.SinglePlayer = capi?.IsSinglePlayer == true;
        // The rest's state is read now, while it is still the state this window ran under.
        if (closed.SinglePlayer)
        {
            closed.ChunkThread = ChunkThreadRest.Describe(closed.ChunkAtStart, ChunkThreadRest.Read(), closed.Seconds);
        }
        closed.Names = names.ToArray();

        window = Interlocked.Exchange(ref spare, null) ?? new Window();
        StartWindow();

        if (closed.Frames == 0)
        {
            Recycle(closed);
            return;
        }
        if (inBackground)
        {
            reports = reports.ContinueWith(_ => Write(closed), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
            return;
        }
        try
        {
            // Not ahead of a report that is still being written.
            reports.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Write never throws; nothing to wait for then.
        }
        Write(closed);
    }

    void Recycle(Window used) => Interlocked.CompareExchange(ref spare, used, null);

    /// <summary>
    /// Formats and writes a closed window's report, then hands the window back for reuse.
    /// Reached from the render thread, the leave-world handler, the chat command and a
    /// background task; none of them may fail because a report could not be written.
    /// </summary>
    void Write(Window closed)
    {
        long start = LodHitchActivity.StartJob();
        try
        {
            foreach (string line in ReportLines(closed))
            {
                logger.Notification("[VintageHorizons]{0}", line);
            }
        }
        catch (Exception e)
        {
            logger.Warning("[VintageHorizons] The hitch report could not be written: {0}", e);
        }
        finally
        {
            LodHitchActivity.EndJob(ReportJob, start);
            Recycle(closed);
        }
    }

    /// <summary>
    /// This mod's own jobs, exactly: what each allocated on its own thread in this window.
    /// Drawing callbacks are summed; the recorder names itself, since it is overhead.
    /// </summary>
    static string DescribeJobs(Window w)
    {
        double drawing = 0;
        var parts = new List<(string Name, double Rate, long Runs)>();
        foreach ((string job, (long Bytes, long Runs) now) in w.JobsNow)
        {
            w.JobsAtStart.TryGetValue(job, out (long Bytes, long Runs) before);
            long bytes = now.Bytes - before.Bytes;
            if (bytes <= 0) continue;
            double rate = bytes / Mb / w.Seconds;
            if (job == OwnName) parts.Add(("hitch recorder", rate, now.Runs - before.Runs));
            else if (job.StartsWith("vh:", StringComparison.Ordinal)) drawing += rate;
            else parts.Add((job, rate, now.Runs - before.Runs));
        }
        if (drawing > 0) parts.Add(("drawing callbacks", drawing, 0));
        return parts.Count == 0
            ? "nothing measured"
            : string.Join(", ", parts
                .OrderByDescending(p => p.Rate)
                .Select(p => $"{p.Name} {p.Rate:0.00}"));
    }

    /// <summary>
    /// The frame length below which <paramref name="count"/> frames are slower, walking the
    /// histogram down from the top. Also returns their mean, which is what "1% low" means.
    /// </summary>
    static double SlowestMean(Window w, int count, out double thresholdMs)
    {
        thresholdMs = 0;
        if (count <= 0) return 0;
        double sum = 0;
        int taken = 0;
        if (w.OverflowFrames > 0)
        {
            int take = Math.Min(count, w.OverflowFrames);
            sum += w.OverflowSumMs / w.OverflowFrames * take;
            taken += take;
            thresholdMs = HistogramBuckets * BucketMs;
        }
        for (int b = HistogramBuckets - 1; b >= 0 && taken < count; b--)
        {
            if (w.Histogram[b] == 0) continue;
            int take = Math.Min(count - taken, w.Histogram[b]);
            sum += (b + 0.5) * BucketMs * take;
            taken += take;
            thresholdMs = b * BucketMs;
        }
        return taken == 0 ? 0 : sum / taken;
    }

    static double MedianMs(Window w)
    {
        int half = (w.Frames + 1) / 2;
        int seen = 0;
        for (int b = 0; b < HistogramBuckets; b++)
        {
            seen += w.Histogram[b];
            if (seen >= half) return (b + 0.5) * BucketMs;
        }
        return HistogramBuckets * BucketMs;
    }

    /// <summary>
    /// What a frame is blamed on: its largest excess, except that a garbage collection that
    /// accounts for half of it wins, because the pause also inflates whichever step it
    /// happened to land in.
    /// </summary>
    int Cause(in FrameRecord record)
    {
        if (record.Id0 < 0) return -1;
        if (record.GcMs >= 0.5 && record.GcMs >= record.Excess0 * 0.5) return idGc;
        return record.Id0;
    }

    static string Describe(in FrameRecord record, string[] names)
    {
        var text = new StringBuilder();
        text.Append($"{record.PeriodMs:0.0} ms frame at +{record.SecondsIn:0}s (recent average {record.BaselineMs:0.0} ms)");
        if (record.GcMs >= 0.1)
        {
            text.Append($"; gc pause {record.GcMs:0.0} ms");
            if (record.GcGen > 0) text.Append($" (gen{record.GcGen - 1})");
        }
        // Steps only a fraction of a millisecond over their average are noise beside a hitch.
        const float Noticeable = 0.3f;
        if (record.Id0 >= 0 && record.Excess0 >= Noticeable)
        {
            text.Append("; unusually slow: ").Append(names[record.Id0]).Append($" +{record.Excess0:0.0} ms");
            if (record.Id1 >= 0 && record.Excess1 >= Noticeable)
                text.Append(", ").Append(names[record.Id1]).Append($" +{record.Excess1:0.0}");
            if (record.Id2 >= 0 && record.Excess2 >= Noticeable)
                text.Append(", ").Append(names[record.Id2]).Append($" +{record.Excess2:0.0}");
        }
        else
        {
            text.Append("; no single step stood out");
        }
        return text.ToString();
    }

    /// <summary>
    /// The report's lines, each written after "[VintageHorizons]". Reads only the closed
    /// window, so it may run on any thread.
    /// </summary>
    List<string> ReportLines(Window w)
    {
        var lines = new List<string>();
        if (w.Frames == 0) return lines;

        double seconds = w.Seconds;
        double averageMs = w.PeriodSumMs / w.Frames;
        double medianMs = MedianMs(w);
        int onePercent = Math.Max(1, w.Frames / 100);
        int tenthPercent = Math.Max(1, w.Frames / 1000);
        double lowMean = SlowestMean(w, onePercent, out double lowThresholdMs);
        double tenthMean = SlowestMean(w, tenthPercent, out _);

        double worstMs = 0;
        for (int i = 0; i < w.WorstCount; i++) worstMs = Math.Max(worstMs, w.Worst[i].PeriodMs);

        int overDouble = 0, over50 = 0;
        var candidates = new List<int>();
        for (int i = 0; i < w.RecordCount; i++)
        {
            ref FrameRecord record = ref w.Records[i];
            if (record.PeriodMs >= medianMs * 2) overDouble++;
            if (record.PeriodMs >= 50) over50++;
            if (record.PeriodMs >= lowThresholdMs) candidates.Add(i);
        }

        // Exactly the slowest 1%: frames tied at the threshold would otherwise stretch it.
        candidates.Sort((a, b) => w.Records[b].PeriodMs.CompareTo(w.Records[a].PeriodMs));
        if (candidates.Count > onePercent) candidates.RemoveRange(onePercent, candidates.Count - onePercent);
        var causes = new Dictionary<int, (int Count, double Excess)>();
        int slowest = candidates.Count;
        foreach (int i in candidates)
        {
            ref FrameRecord record = ref w.Records[i];
            int cause = Cause(record);
            double excess = cause == idGc ? record.GcMs : record.Excess0;
            causes.TryGetValue(cause, out var tally);
            causes[cause] = (tally.Count + 1, tally.Excess + excess);
        }

        string span = $"{w.StartClock:HH:mm:ss}-{w.EndClock:HH:mm:ss}";
        lines.Add(string.Format(
            " hitches {0}{1}: {2} frames in {3:0}s, average {4:0} FPS, median frame "
            + "{5:0.0} ms; 1% low {6:0} FPS (slowest 1% averaged {7:0.0} ms), 0.1% low {8:0} FPS; "
            + "worst {9:0.0} ms; {10} frames over twice the median, {11} over 50 ms; moved {12:0} blocks",
            span, w.Reason == null ? "" : " (" + w.Reason + ")", w.Frames, seconds,
            1000.0 / averageMs, medianMs, lowMean > 0 ? 1000.0 / lowMean : 0, lowMean,
            tenthMean > 0 ? 1000.0 / tenthMean : 0, worstMs, overDouble, over50, w.MovedBlocks));

        if (slowest > 0)
        {
            string[] names = w.Names;
            var parts = causes
                .OrderByDescending(c => c.Value.Count)
                .Take(6)
                .Select(c => $"{(c.Key < 0 ? "nothing stood out" : names[c.Key])} {c.Value.Count} "
                    + $"(avg +{c.Value.Excess / c.Value.Count:0.0} ms)");
            lines.Add(string.Format(
                "   slowest 1% ({0} frames, {1:0.0} ms and longer), blamed on: {2}",
                slowest, w.Records[candidates[^1]].PeriodMs, string.Join(", ", parts)));
        }

        int gen0 = w.GcNow[0] - w.GcAtStart[0];
        int gen1 = w.GcNow[1] - w.GcAtStart[1];
        int gen2 = w.GcNow[2] - w.GcAtStart[2];
        double pauseMs = (w.GcPauseNow - w.GcPauseAtStart).TotalMilliseconds;
        lines.Add(string.Format(
            "   garbage collections gen0/1/2 {0}/{1}/{2}, {3:0} ms paused in total; {4}",
            gen0, gen1, gen2, pauseMs,
            w.SinglePlayer
                ? $"server work in this process: sweep loads {w.ActivityNow[0] - w.ActivityAtStart[0]}, "
                  + $"snow refresh loads {w.ActivityNow[1] - w.ActivityAtStart[1]}, "
                  + $"audit recaptures {w.ActivityNow[2] - w.ActivityAtStart[2]}, "
                  + $"savegame re-reads {w.ActivityNow[3] - w.ActivityAtStart[3]}; {w.ChunkThread}"
                : "server work is not visible from a remote server's client"));

        // Who made the garbage behind those collections.
        long allocated = w.AllocatedNow - w.AllocatedAtStart;
        if (w.Sampled != null)
        {
            lines.AddRange(LodAllocationSampler.Describe(w.Sampled, seconds, allocated));
        }
        else
        {
            lines.Add(string.Format("   allocated {0:0} MB/s in total", allocated / Mb / seconds));
        }
        lines.Add("   this mod's own jobs, exact MB/s (server jobs only in singleplayer): " + DescribeJobs(w));

        // Only frames that are real hitches get a line of their own, worst first.
        var ordered = w.Worst.Take(w.WorstCount)
            .Where(r => r.PeriodMs >= medianMs * 2)
            .OrderByDescending(r => r.PeriodMs);
        foreach (FrameRecord record in ordered)
        {
            lines.Add("   worst: " + Describe(record, w.Names));
        }
        return lines;
    }

    public void Dispose()
    {
        if (enabled && armed) EndWindow("game closing", inBackground: false);
        Disarm();
    }
}
