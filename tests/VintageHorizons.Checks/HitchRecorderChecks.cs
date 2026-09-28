using System.Diagnostics;
using System.Text.RegularExpressions;
using Vintagestory.API.Common;

namespace VintageHorizons.Checks;

/// <summary>
/// The hitch recorder's attribution, fed synthetic frame profiles shaped like the engine's.
///
/// What must hold is the thing the owner will act on: when one step makes periodic slow
/// frames, the report blames that step, not the steps that are merely large every frame;
/// when a garbage collection is what stretched a frame, the collection is blamed rather than
/// whichever step it happened to land in; and a steady run reports no hitches at all.
/// </summary>
public static class HitchRecorderChecks
{
    static readonly long TicksPerMs = Stopwatch.Frequency / 1000;

    public static void Run(Check c)
    {
        BlamesThePeriodicStep(c);
        BlamesGarbageCollection(c);
        SteadyRunReportsNoHitches(c);
        SurvivesManyStepNames(c);
        WritesTheMinuteOffTheCallingThread(c);
    }

    /// <summary>
    /// The minute's report is written off the render thread. 0.4.129-0.4.131 lost a 14-58 ms
    /// frame at the start of every minute writing it in place, the game's logger flushing its
    /// file on every line. Ending the window must return at once however slow the log is; the
    /// report must still arrive and say what a report written in place says; and reports must
    /// arrive in the order their windows closed, an in-place one included.
    /// </summary>
    static void WritesTheMinuteOffTheCallingThread(Check c)
    {
        var inPlaceLog = new CaptureLogger();
        var inPlace = new LodHitchRecorder(null, inPlaceLog);
        long inPlaceStart = 1;
        FeedPeriodicHitches(inPlace, ref inPlaceStart);
        inPlace.WriteReport("check");

        var slowLog = new CaptureLogger { Delay = TimeSpan.FromMilliseconds(50) };
        var background = new LodHitchRecorder(null, slowLog);
        long start = 1;
        FeedPeriodicHitches(background, ref start);
        var clock = Stopwatch.StartNew();
        background.WriteReportInBackground("check");
        double returnedMs = clock.Elapsed.TotalMilliseconds;
        c.True(returnedMs < 25,
            $"ending the window does not wait for a slow log ({returnedMs:0.0} ms; each line takes 50)");
        c.True(background.WaitForReports(TimeSpan.FromSeconds(10)), "the report is written in the background");
        c.SeqEq(Comparable(inPlaceLog), Comparable(slowLog),
            "and says what the same frames reported in place say");

        FeedPeriodicHitches(background, ref start);
        background.WriteReportInBackground("first");
        FeedPeriodicHitches(background, ref start);
        background.WriteReportInBackground("second");
        FeedPeriodicHitches(background, ref start);
        background.WriteReport("third");
        List<string> lines;
        lock (slowLog.Lines) lines = slowLog.Lines.ToList();
        int first = lines.FindIndex(l => l.Contains("(first)", StringComparison.Ordinal));
        int second = lines.FindIndex(l => l.Contains("(second)", StringComparison.Ordinal));
        int third = lines.FindIndex(l => l.Contains("(third)", StringComparison.Ordinal));
        c.True(first >= 0 && first < second && second < third,
            $"reports arrive in the order their windows closed (lines {first}, {second}, {third})");
    }

    /// <summary>The periodic-hitch run, fed as one window.</summary>
    static void FeedPeriodicHitches(LodHitchRecorder recorder, ref long start)
    {
        recorder.BeginWindowForChecks();
        for (int frame = 0; frame < 3000; frame++)
        {
            bool slow = frame % 20 == 19;
            var marks = new List<(string, double)> { ("gametick", 2), ("vh:lod", 1), ("end", 5) };
            if (slow) marks.Add(("gmleslowhandler", 30));
            ProfileEntryRange root = Frame(start, marks);
            recorder.Feed(root, 0, 0);
            start += root.ElapsedTicks;
        }
    }

    /// <summary>
    /// The lines that depend only on the frames: the summary, the blame and the worst frames,
    /// with the wall-clock readings taken out. Collection counts and job allocation are live
    /// process readings and differ between any two reports.
    /// </summary>
    static List<string> Comparable(CaptureLogger log)
    {
        lock (log.Lines)
        {
            return log.Lines
                .Where(l => l.Contains(" hitches ", StringComparison.Ordinal)
                    || l.Contains("slowest 1%", StringComparison.Ordinal)
                    || l.Contains("worst:", StringComparison.Ordinal))
                .Select(l => Regex.Replace(l, @"\d\d:\d\d:\d\d-\d\d:\d\d:\d\d", "<span>"))
                .Select(l => Regex.Replace(l, @"frames in \d+s", "frames in <s>"))
                .Select(l => Regex.Replace(l, @"frame at \+\d+s", "frame at +<s>"))
                .ToList();
        }
    }

    /// <summary>
    /// The owner's 0.4.128 crash (2026-09-28): opening the world-edit menu added new steps to
    /// the game's frame profile, the 129th distinct name made the recorder grow its arrays,
    /// and the write that triggered the growth went into the old, smaller array. Hundreds of
    /// names, arriving a few at a time as menus open, must be absorbed without a throw.
    /// </summary>
    static void SurvivesManyStepNames(Check c)
    {
        var log = new CaptureLogger();
        var recorder = new LodHitchRecorder(null, log);
        recorder.BeginWindowForChecks();

        long start = 1;
        Exception? thrown = null;
        try
        {
            for (int frame = 0; frame < 1200; frame++)
            {
                var marks = new List<(string, double)> { ("gametick", 2), ("end", 5) };
                // Three new names every frame for the first 200 frames: 600 in all, past every
                // growth step, and several in the same frame as a growth.
                if (frame < 200)
                {
                    for (int n = 0; n < 3; n++) marks.Add(($"gmlemenu.step{frame * 3 + n}", 0.01));
                }
                ProfileEntryRange root = Frame(start, marks);
                recorder.Feed(root, 0, 0);
                start += root.ElapsedTicks;
            }
            recorder.WriteReport("check");
        }
        catch (Exception e)
        {
            thrown = e;
        }

        c.True(thrown == null, "hundreds of new step names are absorbed without an exception"
            + (thrown == null ? "" : $" (threw {thrown.GetType().Name}: {thrown.Message})"));
        c.True(log.Contains("frames in"), "and the recorder still reports after absorbing them");
    }

    static void BlamesThePeriodicStep(Check c)
    {
        var log = new CaptureLogger();
        var recorder = new LodHitchRecorder(null, log);
        recorder.BeginWindowForChecks();

        // Every frame spends 5 ms in the buffer swap - large, but ordinary. Every twentieth
        // frame one tick handler takes 30 ms.
        long start = 1;
        for (int frame = 0; frame < 3000; frame++)
        {
            bool slow = frame % 20 == 19;
            var marks = new List<(string, double)> { ("gametick", 2), ("vh:lod", 1), ("end", 5) };
            if (slow) marks.Add(("gmleslowhandler", 30));
            ProfileEntryRange root = Frame(start, marks);
            recorder.Feed(root, 0, 0);
            start += root.ElapsedTicks;
        }
        recorder.WriteReport("check");

        c.True(log.Contains("slowest 1%"), "a run with hitches reports its slowest 1%");
        string? blamed = log.Lines.FirstOrDefault(l => l.Contains("blamed on", StringComparison.Ordinal));
        c.True(blamed != null && blamed.Contains("gmleslowhandler 1", StringComparison.Ordinal)
                || blamed != null && blamed.Contains("blamed on: gmleslowhandler", StringComparison.Ordinal),
            "the periodic slow step is blamed for the slowest frames");
        c.False(blamed != null && blamed.Contains("end ", StringComparison.Ordinal),
            "a step that is large on every frame is not blamed for the hitches");
        c.True(log.Lines.Any(l => l.Contains("worst:", StringComparison.Ordinal)
                && l.Contains("unusually slow: gmleslowhandler +", StringComparison.Ordinal)),
            "each worst frame names the step that was unusually slow in it");
    }

    static void BlamesGarbageCollection(Check c)
    {
        var log = new CaptureLogger();
        var recorder = new LodHitchRecorder(null, log);
        recorder.BeginWindowForChecks();

        // Every fiftieth frame a 25 ms collection lands inside the tick handlers. The
        // handlers look slow on that frame too, but they are not the cause.
        long start = 1;
        for (int frame = 0; frame < 3000; frame++)
        {
            bool collected = frame % 50 == 49;
            var marks = new List<(string, double)>
            {
                ("gametick", collected ? 27 : 2), ("vh:lod", 1), ("end", 5),
            };
            ProfileEntryRange root = Frame(start, marks);
            // The recorder closes each frame one call late and pairs it with the collection
            // measured on the call that delivered it.
            recorder.Feed(root, collected ? 25 : 0, collected ? 1 : 0);
            start += root.ElapsedTicks;
        }
        recorder.WriteReport("check");

        string? blamed = log.Lines.FirstOrDefault(l => l.Contains("blamed on", StringComparison.Ordinal));
        c.True(blamed != null && blamed.Contains("blamed on: gc pause", StringComparison.Ordinal),
            "a collection that stretched the frame is blamed, not the step it landed in");
    }

    static void SteadyRunReportsNoHitches(Check c)
    {
        var log = new CaptureLogger();
        var recorder = new LodHitchRecorder(null, log);
        recorder.BeginWindowForChecks();

        long start = 1;
        for (int frame = 0; frame < 2000; frame++)
        {
            // Small jitter only.
            double jitter = (frame % 7) * 0.05;
            ProfileEntryRange root = Frame(start,
                new List<(string, double)> { ("gametick", 2 + jitter), ("end", 5) });
            recorder.Feed(root, 0, 0);
            start += root.ElapsedTicks;
        }
        recorder.WriteReport("check");

        c.True(log.Contains("0 frames over twice the median, 0 over 50 ms"),
            "a steady run has no frames over twice the median");
        c.False(log.Contains("worst:"), "a steady run lists no worst frames");
    }

    /// <summary>A root profile as the engine leaves it: top-level marks under "all".</summary>
    static ProfileEntryRange Frame(long start, List<(string Code, double Ms)> marks)
    {
        var root = new ProfileEntryRange
        {
            Code = "all",
            Start = start,
            Marks = new Dictionary<string, ProfileEntry>(),
        };
        long total = 0;
        foreach ((string code, double ms) in marks)
        {
            int ticks = (int)(ms * TicksPerMs);
            root.Marks[code] = new ProfileEntry(ticks, 1);
            total += ticks;
        }
        root.ElapsedTicks = total;
        return root;
    }
}
