using System.Diagnostics;

namespace VintageHorizons.Checks;

/// <summary>
/// The hitch recorder's allocation sampler against the real runtime: a named thread that
/// allocates a known amount must come back under its own name, with its object type, its
/// large objects counted as such, and an induced collection reported as GC.Collect. What
/// must hold is that the owner's report can be trusted to name who made the garbage.
/// </summary>
public static class AllocationSamplerChecks
{
    public static void Run(Check c)
    {
        c.Eq("Byte[]", LodAllocationSampler.ShortType("System.Byte[]"), "a type loses its namespace");
        c.Eq("LodSection", LodAllocationSampler.ShortType("VintageHorizons.LodSection"),
            "this mod's types are shortened the same way");
        c.Eq("small allocations", LodAllocationSampler.ReasonName(0), "reason 0 is ordinary allocation");
        c.Eq("GC.Collect", LodAllocationSampler.ReasonName(1), "reason 1 is an induced collection");
        c.Eq("large allocations", LodAllocationSampler.ReasonName(4), "reason 4 is large-object pressure");

        using var sampler = new LodAllocationSampler();
        sampler.TakeWindow();

        const string ThreadName = "vh-check-allocator";
        var keep = new List<byte[]>();
        var worker = new Thread(() =>
        {
            // About 16 MB of small arrays and 8 MB of large ones.
            for (int i = 0; i < 16_000; i++) keep.Add(new byte[1000]);
            for (int i = 0; i < 40; i++) keep.Add(new byte[200_000]);
        }) { Name = ThreadName };
        var clock = Stopwatch.StartNew();
        worker.Start();
        worker.Join();
        GC.Collect(0);

        // Events arrive on the runtime's dispatch thread some time later. Wait for them to
        // stop arriving rather than for a fixed time.
        long seen = -1;
        while (clock.Elapsed.TotalSeconds < 20)
        {
            Thread.Sleep(500);
            long now = sampler.PeekEvents();
            if (now > 100 && now == seen) break;
            seen = now;
        }
        double seconds = Math.Max(1, clock.Elapsed.TotalSeconds);
        List<string> lines = LodAllocationSampler.Describe(sampler.TakeWindow(), seconds, 24L << 20);
        string text = string.Join("\n", lines);
        GC.KeepAlive(keep);

        bool windows = OperatingSystem.IsWindows();
        c.True(!windows || text.Contains(ThreadName, StringComparison.Ordinal),
            "a named thread's garbage is reported under its own name");
        c.True(text.Contains("Byte[]", StringComparison.Ordinal), "the object type is reported");
        c.True(text.Contains("large objects", StringComparison.Ordinal),
            "large objects are reported separately");
        c.True(text.Contains("GC.Collect", StringComparison.Ordinal),
            "an induced collection is reported as GC.Collect");
        c.True(!text.Contains("unreadable events", StringComparison.Ordinal),
            "every event's payload was readable");
        if (!text.Contains(ThreadName, StringComparison.Ordinal))
        {
            foreach (string line in lines) Console.WriteLine("      sampler: " + line);
        }
    }
}
