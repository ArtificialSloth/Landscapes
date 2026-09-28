using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics.Tracing;
using System.Runtime.InteropServices;

namespace VintageHorizons;

/// <summary>
/// Which threads, and which kinds of object, produce the garbage behind the hitch recorder's
/// collection pauses.
///
/// The owner's 0.4.129 session showed the 1% low is mostly .NET collections - 160 to 330
/// mid-sized ones a minute while exploring, 10-20 ms each - but a pause says nothing about
/// who allocated. In singleplayer the server shares the process, so the garbage can be the
/// client's, the server's, vanilla's or this mod's.
///
/// The runtime samples allocation itself: each time roughly 100 KB has been allocated it
/// raises an allocation-tick event naming the allocating thread and the type of the object
/// that crossed the line. Summed per thread and type that is an unbiased estimate of who
/// allocates, at one event per 100 KB, delivered on the runtime's own dispatch thread rather
/// than on the thread that allocated. Collection starts are counted with their reason, which
/// separates ordinary allocation pressure from large-object pressure and from code that
/// calls GC.Collect.
/// </summary>
public sealed class LodAllocationSampler : EventListener
{
    const string RuntimeSourceName = "Microsoft-Windows-DotNETRuntime";
    const EventKeywords GcKeyword = (EventKeywords)0x1;
    const int GcStartId = 1;
    const int AllocationTickId = 10;
    const int MaxTypesPerThread = 256;
    const string OtherTypes = "(other types)";
    const double Mb = 1024.0 * 1024.0;

    /// <summary>
    /// Names for threads the operating system knows no name for, registered from the thread
    /// itself: the game names neither its client main thread nor, in singleplayer, the
    /// server's. Keyed by operating-system thread id, which is what the events carry.
    /// </summary>
    static readonly ConcurrentDictionary<ulong, string> knownThreads = new();

    readonly object gate = new();
    Dictionary<ulong, ThreadTally> tallies = new();
    Dictionary<(int Generation, int Reason), int> collections = new();
    long events;
    int failures;
    volatile bool stopped;
    bool dispatchThreadNamed;

    internal sealed class ThreadTally
    {
        public string Name = "";
        public long Bytes;
        public long LargeBytes;
        public readonly Dictionary<string, long> Types = new();
        public readonly Dictionary<string, long> LargeTypes = new();
    }

    /// <summary>What one window of sampling saw, handed to the owning thread for reporting.</summary>
    public sealed class Window
    {
        internal Dictionary<ulong, ThreadTally> Tallies = new();
        internal Dictionary<(int Generation, int Reason), int> Collections = new();
        public long Events;
        public int Failures;
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        // Runs from the base constructor for sources that already exist, before this
        // class's constructor body; it touches nothing but the base class.
        if (source.Name == RuntimeSourceName)
        {
            EnableEvents(source, EventLevel.Verbose, GcKeyword);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (stopped) return;
        try
        {
            if (!dispatchThreadNamed)
            {
                // Every event arrives on the runtime's dispatch thread, so its own garbage is
                // this sampler's cost and should say so.
                dispatchThreadNamed = true;
                RegisterCurrentThread("the hitch recorder's own sampler");
            }
            if (e.EventId == AllocationTickId) OnAllocationTick(e);
            else if (e.EventId == GcStartId) OnGcStart(e);
        }
        catch
        {
            // A diagnostic never takes anything down. A handful of odd events is tolerated;
            // a steady stream means the payload changed shape, and sampling stops.
            if (Interlocked.Increment(ref failures) > 20) stopped = true;
        }
    }

    void OnAllocationTick(EventWrittenEventArgs e)
    {
        ReadOnlyCollection<object?>? payload = e.Payload;
        ReadOnlyCollection<string>? names = e.PayloadNames;
        if (payload == null || names == null) return;

        long amount = ReadLong(payload, names, "AllocationAmount64");
        if (amount <= 0) amount = ReadLong(payload, names, "AllocationAmount");
        if (amount <= 0) return;
        bool large = ReadLong(payload, names, "AllocationKind") == 1;
        string type = ReadString(payload, names, "TypeName") ?? "?";
        ulong thread = (ulong)e.OSThreadId;

        lock (gate)
        {
            events++;
            if (!tallies.TryGetValue(thread, out ThreadTally? tally))
            {
                tally = new ThreadTally { Name = NameOf(thread) };
                tallies[thread] = tally;
            }
            tally.Bytes += amount;
            Count(tally.Types, type, amount);
            if (large)
            {
                tally.LargeBytes += amount;
                Count(tally.LargeTypes, type, amount);
            }
        }
    }

    void OnGcStart(EventWrittenEventArgs e)
    {
        ReadOnlyCollection<object?>? payload = e.Payload;
        ReadOnlyCollection<string>? names = e.PayloadNames;
        if (payload == null || names == null) return;
        int generation = (int)ReadLong(payload, names, "Depth");
        int reason = (int)ReadLong(payload, names, "Reason");
        lock (gate)
        {
            collections.TryGetValue((generation, reason), out int count);
            collections[(generation, reason)] = count + 1;
        }
    }

    static void Count(Dictionary<string, long> types, string type, long amount)
    {
        if (!types.ContainsKey(type) && types.Count >= MaxTypesPerThread) type = OtherTypes;
        types.TryGetValue(type, out long bytes);
        types[type] = bytes + amount;
    }

    static long ReadLong(ReadOnlyCollection<object?> payload, ReadOnlyCollection<string> names, string name)
    {
        int index = names.IndexOf(name);
        if (index < 0 || index >= payload.Count || payload[index] == null) return -1;
        return Convert.ToInt64(payload[index]);
    }

    static string? ReadString(ReadOnlyCollection<object?> payload, ReadOnlyCollection<string> names, string name)
    {
        int index = names.IndexOf(name);
        return index < 0 || index >= payload.Count ? null : payload[index] as string;
    }

    /// <summary>Samples seen so far in this window; lets the checks wait for delivery to settle.</summary>
    internal long PeekEvents()
    {
        lock (gate) return events;
    }

    /// <summary>Takes everything sampled since the last call, and starts a new window.</summary>
    public Window TakeWindow()
    {
        lock (gate)
        {
            var window = new Window
            {
                Tallies = tallies,
                Collections = collections,
                Events = events,
                Failures = failures,
            };
            tallies = new Dictionary<ulong, ThreadTally>();
            collections = new Dictionary<(int Generation, int Reason), int>();
            events = 0;
            return window;
        }
    }

    /// <summary>
    /// The report lines for one window: allocation by thread, large objects, and collections
    /// by cause. <paramref name="exactBytes"/> is the process's own allocation count for the
    /// same span, which the sampled per-thread figures are shares of.
    /// </summary>
    public static List<string> Describe(Window window, double seconds, long exactBytes)
    {
        var lines = new List<string>();
        if (seconds <= 0) return lines;

        // Threads with the same name - the shared pool's workers, for one - are one line.
        var groups = new Dictionary<string, (long Bytes, long Large, Dictionary<string, long> Types, Dictionary<string, long> LargeTypes)>();
        long sampled = 0;
        foreach (ThreadTally tally in window.Tallies.Values)
        {
            if (!groups.TryGetValue(tally.Name, out var group))
            {
                group = (0, 0, new Dictionary<string, long>(), new Dictionary<string, long>());
            }
            group.Bytes += tally.Bytes;
            group.Large += tally.LargeBytes;
            foreach ((string type, long bytes) in tally.Types) Add(group.Types, type, bytes);
            foreach ((string type, long bytes) in tally.LargeTypes) Add(group.LargeTypes, type, bytes);
            groups[tally.Name] = group;
            sampled += tally.Bytes;
        }

        const int ThreadsShown = 8;
        var ordered = groups.OrderByDescending(g => g.Value.Bytes).ToList();
        var shown = ordered.Take(ThreadsShown).Select(g =>
            $"{g.Key} {g.Value.Bytes / Mb / seconds:0.0} ({TopTypes(g.Value.Types, g.Value.Bytes, 2)})");
        long rest = ordered.Skip(ThreadsShown).Sum(g => g.Value.Bytes);
        lines.Add(string.Format(
            "  allocated {0:0} MB/s in total (the samples account for {1:0}%); by thread, MB/s: {2}{3}",
            exactBytes / Mb / seconds,
            exactBytes > 0 ? 100.0 * sampled / exactBytes : 0,
            ordered.Count == 0 ? "no samples" : string.Join("; ", shown),
            rest > 0 ? $"; {ordered.Count - ThreadsShown} other threads {rest / Mb / seconds:0.0}" : ""));

        long large = groups.Values.Sum(g => g.Large);
        if (large > 0)
        {
            var largest = groups
                .SelectMany(g => g.Value.LargeTypes.Select(t => (Thread: g.Key, Type: t.Key, Bytes: t.Value)))
                .OrderByDescending(t => t.Bytes)
                .Take(4)
                .Select(t => $"{t.Thread} {ShortType(t.Type)} {t.Bytes / Mb / seconds:0.0}");
            lines.Add(string.Format(
                "  large objects (85 KB and up, freed only by a full collection): {0:0.0} MB/s: {1}",
                large / Mb / seconds, string.Join("; ", largest)));
        }

        var byGeneration = window.Collections
            .GroupBy(c => c.Key.Generation)
            .OrderBy(g => g.Key)
            .Select(g => $"gen{g.Key} {g.Sum(c => c.Value)} ("
                + string.Join(", ", g.OrderByDescending(c => c.Value).Select(c => $"{ReasonName(c.Key.Reason)} {c.Value}"))
                + ")");
        lines.Add(string.Format(
            "  collections by cause: {0}; sampler: {1:0} samples/s{2}",
            window.Collections.Count == 0 ? "none seen" : string.Join(", ", byGeneration),
            window.Events / seconds,
            window.Failures > 0 ? $", {window.Failures} unreadable events" : ""));
        return lines;
    }

    static void Add(Dictionary<string, long> into, string key, long bytes)
    {
        into.TryGetValue(key, out long existing);
        into[key] = existing + bytes;
    }

    static string TopTypes(Dictionary<string, long> types, long total, int count)
    {
        if (total <= 0) return "";
        return string.Join(", ", types
            .OrderByDescending(t => t.Value)
            .Take(count)
            .Select(t => $"{ShortType(t.Key)} {100.0 * t.Value / total:0}%"));
    }

    /// <summary>"System.Byte[]" as "Byte[]": the namespace is noise beside the name.</summary>
    internal static string ShortType(string name)
    {
        int cut = name.IndexOfAny(new[] { '[', '`', '<', '+' });
        string head = cut < 0 ? name : name[..cut];
        int dot = head.LastIndexOf('.');
        return dot < 0 ? name : name[(dot + 1)..];
    }

    /// <summary>The runtime's gc_reason values, in words.</summary>
    internal static string ReasonName(int reason) => reason switch
    {
        0 => "small allocations",
        1 => "GC.Collect",
        2 => "low memory",
        3 => "empty",
        4 => "large allocations",
        5 => "out of space",
        6 => "out of large-object space",
        7 => "GC.Collect, optimized",
        9 => "low memory, blocking",
        10 => "GC.Collect, compacting",
        11 or 13 => "host low memory",
        17 => "GC.Collect, aggressive",
        _ => $"reason {reason}",
    };

    /// <summary>
    /// Gives the calling thread a name in the report. For threads the operating system has
    /// no name for; a named thread is found by its own name.
    /// </summary>
    public static void RegisterCurrentThread(string name)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            knownThreads[GetCurrentThreadId()] = name;
        }
        catch
        {
            // Only the label is lost.
        }
    }

    static string NameOf(ulong thread)
    {
        if (knownThreads.TryGetValue(thread, out string? known)) return known;
        string? description = OperatingSystem.IsWindows() ? WindowsThreadDescription(thread) : null;
        return string.IsNullOrEmpty(description) ? $"unnamed thread {thread}" : description;
    }

    /// <summary>
    /// The name .NET gave a thread: on Windows, setting Thread.Name also sets the operating
    /// system's thread description, which is what the events can be matched against.
    /// </summary>
    static string? WindowsThreadDescription(ulong thread)
    {
        try
        {
            IntPtr handle = OpenThread(ThreadQueryLimitedInformation, false, (uint)thread);
            if (handle == IntPtr.Zero) return null;
            try
            {
                if (GetThreadDescription(handle, out IntPtr text) < 0 || text == IntPtr.Zero) return null;
                try
                {
                    return Marshal.PtrToStringUni(text);
                }
                finally
                {
                    LocalFree(text);
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch
        {
            // Windows before 10 1607 has no GetThreadDescription; the thread goes unnamed.
            return null;
        }
    }

    const uint ThreadQueryLimitedInformation = 0x0800;

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll")]
    static extern int GetThreadDescription(IntPtr thread, out IntPtr description);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
