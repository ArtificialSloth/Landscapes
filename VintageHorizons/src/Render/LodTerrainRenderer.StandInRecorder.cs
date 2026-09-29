using System.Globalization;
using System.Text;
using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// TEMPORARY DIAGNOSTIC for the 0.4.135 stand-in fix (<c>.vhstandin</c>). Nothing here changes a
/// pixel; it only writes to the log.
///
/// <para>
/// Once a minute, when anything happened, one line says how many coarse stand-ins covered ground
/// the game is drawing - at what level, for how long, how close, and why the finer piece was
/// missing - and how long chunks the game had just announced waited before they were handed to
/// it. A stand-in over the game's ground lasting half a second or more also gets a line of its own
/// when it ends, so a sighting can be matched to a timestamp. Everything is written by a
/// background task: the game's logger flushes its file on every line, and doing that on the render
/// thread cost the hitch recorder 14-58 ms frames.
/// </para>
/// <para>
/// To remove it: delete this file and its three call sites in LodTerrainRenderer.cs -
/// <c>NoteStandIn</c> in <c>CollectDrawNodes</c>, <c>EndStandInFrame</c> after the walk, and
/// <c>RecordFreshChunkConfirmed</c> in <c>NoteFreshChunkConfirmed</c>.
/// </para>
/// </summary>
public partial class LodTerrainRenderer
{
    const long StandInReportMs = 60_000;
    const long StandInDetailMs = 500;
    const int StandInDetailLinesPerMinute = 20;
    const int FreshChunkWaitsKept = 8192;
    const long FreshChunkLateMs = 30_000;

    // Why a stand-in's finer piece was missing, as bit positions.
    const int MissingNewGround = 0;
    const int MissingLoading = 1;
    const int MissingMeshing = 2;
    const int MissingQueued = 3;
    const int MissingLoadFailed = 4;
    const int MissingNotRequested = 5;
    const int MissingReasonCount = 6;

    static readonly string[] MissingReasonText =
    {
        "new ground had not reached that level yet",
        "it was loading from disk",
        "it was being meshed",
        "it was waiting for a mesh slot",
        "its stored copy could not be read",
        "it had not been asked for yet",
    };

    sealed class StandInEpisode
    {
        public long StartMs;
        public long LastFrame;
        public int Level;
        public int Quarters;
        public bool OverGameGround;
        public double NearestBlocks;
        public int Reasons;
    }

    readonly Dictionary<long, StandInEpisode> standInEpisodes = new();
    readonly Stack<StandInEpisode> spareStandInEpisodes = new();
    readonly List<long> endedStandIns = new();
    readonly int[] standInsByLevel = new int[LodWorld.MaxLevel + 1];
    readonly int[] standInReasons = new int[MissingReasonCount];
    readonly List<int> freshChunkWaits = new();
    int standInsOverGround;
    long standInLongestMs;
    double standInNearestBlocks = double.MaxValue;
    int freshChunksConfirmed;
    int freshChunksLate;
    int standInDetailLines;
    long standInWindowStartMs;
    DateTime standInWindowStartClock;
    Task standInWrites = Task.CompletedTask;

    /// <summary>A section was drawn this frame in place of finer terrain that is not ready.</summary>
    void NoteStandIn(long key, int level, int uncovered, int quarters)
    {
        if (!standInEpisodes.TryGetValue(key, out StandInEpisode? episode))
        {
            episode = spareStandInEpisodes.Count > 0 ? spareStandInEpisodes.Pop() : new StandInEpisode();
            episode.StartMs = Environment.TickCount64;
            episode.Level = level;
            episode.OverGameGround = false;
            episode.NearestBlocks = double.MaxValue;
            episode.Reasons = 0;
            for (int qz = 0; qz < 2; qz++)
            {
                for (int qx = 0; qx < 2; qx++)
                {
                    if ((uncovered & LodStandInPolicy.QuarterBit(qx, qz)) == 0) continue;
                    episode.Reasons |= 1 << MissingReason(LodWorld.ChildKey(key, qx, qz));
                }
            }
            standInEpisodes[key] = episode;
        }

        episode.LastFrame = frameCounter;
        episode.Quarters = quarters;

        // What this frame drew: the whole section, or only the quarters it stands in for. Each
        // quarter is a child's footprint, so the game's cells under it are the child's.
        for (int qz = 0; qz < 2; qz++)
        {
            for (int qx = 0; qx < 2; qx++)
            {
                if (quarters != 0 && (quarters & LodStandInPolicy.QuarterBit(qx, qz)) == 0) continue;
                long part = LodWorld.ChildKey(key, qx, qz);
                if (!episode.OverGameGround && readinessMaskActive && readiness != null
                    && readiness.Classify(part) != VanillaSectionOwnership.CacheOnly)
                {
                    episode.OverGameGround = true;
                }
                double distance = Math.Sqrt(NearestDistanceSqTo(part));
                if (distance < episode.NearestBlocks) episode.NearestBlocks = distance;
            }
        }
    }

    int MissingReason(long child)
    {
        if (!world.AvailableDataSet.Contains(child)) return MissingNewGround;
        if (world.LoadFailed.Contains(child)) return MissingLoadFailed;
        if (world.LoadsInFlight.Contains(child)) return MissingLoading;
        if (meshJobInFlight.Contains(child)) return MissingMeshing;
        if (world.RenderDirty.Contains(child)) return MissingQueued;
        return MissingNotRequested;
    }

    /// <summary>A chunk the game announced was handed to it this many milliseconds later.</summary>
    void RecordFreshChunkConfirmed(long waitedMs)
    {
        if (waitedMs > FreshChunkLateMs)
        {
            freshChunksLate++;
            return;
        }
        freshChunksConfirmed++;
        if (freshChunkWaits.Count < FreshChunkWaitsKept) freshChunkWaits.Add((int)Math.Max(0, waitedMs));
    }

    /// <summary>After the walk: closes stand-ins not drawn this frame, and reports once a minute.</summary>
    void EndStandInFrame()
    {
        long now = Environment.TickCount64;
        if (standInEpisodes.Count > 0)
        {
            endedStandIns.Clear();
            foreach (KeyValuePair<long, StandInEpisode> entry in standInEpisodes)
            {
                if (entry.Value.LastFrame != frameCounter) endedStandIns.Add(entry.Key);
            }
            foreach (long key in endedStandIns)
            {
                StandInEpisode episode = standInEpisodes[key];
                standInEpisodes.Remove(key);
                CloseStandIn(key, episode, now);
                spareStandInEpisodes.Push(episode);
            }
        }

        if (standInWindowStartMs == 0)
        {
            standInWindowStartMs = now;
            standInWindowStartClock = DateTime.Now;
        }
        else if (now - standInWindowStartMs >= StandInReportMs)
        {
            ReportStandIns(now);
        }
    }

    void CloseStandIn(long key, StandInEpisode episode, long now)
    {
        if (!episode.OverGameGround) return;

        long duration = now - episode.StartMs;
        standInsOverGround++;
        standInsByLevel[episode.Level]++;
        for (int reason = 0; reason < MissingReasonCount; reason++)
        {
            if ((episode.Reasons & (1 << reason)) != 0) standInReasons[reason]++;
        }
        if (duration > standInLongestMs) standInLongestMs = duration;
        if (episode.NearestBlocks < standInNearestBlocks) standInNearestBlocks = episode.NearestBlocks;

        if (duration < StandInDetailMs || standInDetailLines >= StandInDetailLinesPerMinute) return;
        standInDetailLines++;

        // Copied out now: the episode goes back to the pool as soon as this returns and may be
        // reused by the render thread before the background writer formats the line.
        int footprint = LodWorld.KeyFootprintBlocks(key);
        long x = (long)LodWorld.KeySx(key) * footprint;
        long z = (long)LodWorld.KeySz(key) * footprint;
        int level = episode.Level;
        double nearest = episode.NearestBlocks;
        string quarters = LodStandInPolicy.Describe(episode.Quarters);
        string reasons = DescribeMissingReasons(episode.Reasons);
        string mode = StandInFix ? "on" : "off";
        WriteStandInLine(() => string.Format(CultureInfo.InvariantCulture,
            "[VintageHorizons] stand-in (.vhstandin {0}): a level-{1} section at {2},{3}, {4} blocks "
            + "across, covered ground the game draws for {5:0.0} s, {6:0} blocks from you at its "
            + "nearest, drawing {7}; its finer piece was missing because {8}.",
            mode, level, x, z, footprint, duration / 1000.0, nearest,
            quarters == "whole" ? "the whole section" : "only its " + quarters + " quarter(s)",
            reasons));
    }

    void ReportStandIns(long now)
    {
        bool anything = standInsOverGround > 0 || freshChunksConfirmed > 0 || freshChunksLate > 0;
        if (anything)
        {
            var report = new StandInReport(
                standInWindowStartClock, DateTime.Now, StandInFix, standInsOverGround,
                (int[])standInsByLevel.Clone(), (int[])standInReasons.Clone(),
                standInLongestMs, standInNearestBlocks,
                freshChunksConfirmed, freshChunksLate, freshChunkWaits.ToArray());
            WriteStandInLine(() => FormatStandInReport(report));
        }

        standInsOverGround = 0;
        Array.Clear(standInsByLevel);
        Array.Clear(standInReasons);
        standInLongestMs = 0;
        standInNearestBlocks = double.MaxValue;
        freshChunksConfirmed = 0;
        freshChunksLate = 0;
        freshChunkWaits.Clear();
        standInDetailLines = 0;
        standInWindowStartMs = now;
        standInWindowStartClock = DateTime.Now;
    }

    readonly record struct StandInReport(
        DateTime From, DateTime To, bool FixOn, int OverGround, int[] ByLevel, int[] Reasons,
        long LongestMs, double NearestBlocks, int Confirmed, int Late, int[] Waits);

    static string FormatStandInReport(StandInReport r)
    {
        var line = new StringBuilder(320);
        line.Append("[VintageHorizons] stand-in check ")
            .Append(r.From.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append('-')
            .Append(r.To.ToString("HH:mm:ss", CultureInfo.InvariantCulture))
            .Append(", .vhstandin ").Append(r.FixOn ? "on" : "off").Append(": ");

        if (r.OverGround == 0)
        {
            line.Append("no coarse stand-in covered ground the game draws");
        }
        else
        {
            line.Append(r.OverGround).Append(" coarse stand-in(s) covered ground the game draws (");
            bool first = true;
            for (int level = 0; level < r.ByLevel.Length; level++)
            {
                if (r.ByLevel[level] == 0) continue;
                if (!first) line.Append(", ");
                line.Append('L').Append(level).Append(' ').Append(r.ByLevel[level]);
                first = false;
            }
            line.Append("), longest ")
                .Append((r.LongestMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture))
                .Append(" s, nearest ")
                .Append(r.NearestBlocks.ToString("0", CultureInfo.InvariantCulture))
                .Append(" blocks away; the finer piece was missing because ");
            first = true;
            for (int reason = 0; reason < r.Reasons.Length; reason++)
            {
                if (r.Reasons[reason] == 0) continue;
                if (!first) line.Append(", ");
                line.Append(MissingReasonText[reason]).Append(" (").Append(r.Reasons[reason]).Append(')');
                first = false;
            }
        }

        line.Append(". Fresh chunks: ");
        if (r.Waits.Length == 0)
        {
            line.Append("none handed to the game");
        }
        else
        {
            Array.Sort(r.Waits);
            line.Append(r.Confirmed).Append(" handed to the game, half within ")
                .Append(Percentile(r.Waits, 0.5)).Append(" ms, 95% within ")
                .Append(Percentile(r.Waits, 0.95)).Append(" ms, slowest ")
                .Append(r.Waits[^1]).Append(" ms after the game announced them");
        }
        if (r.Late > 0) line.Append(", and ").Append(r.Late).Append(" more than 30 s after");
        line.Append('.');
        return line.ToString();
    }

    static int Percentile(int[] sorted, double fraction) =>
        sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Length) - 1, 0, sorted.Length - 1)];

    static string DescribeMissingReasons(int reasons)
    {
        var parts = new List<string>(MissingReasonCount);
        for (int reason = 0; reason < MissingReasonCount; reason++)
        {
            if ((reasons & (1 << reason)) != 0) parts.Add(MissingReasonText[reason]);
        }
        return parts.Count == 0 ? "of something unrecorded" : string.Join(" / ", parts);
    }

    /// <summary>Formats and writes on a background task, one after another, never on the render thread.</summary>
    void WriteStandInLine(Func<string> format)
    {
        ILogger logger = capi.Logger;
        standInWrites = standInWrites.ContinueWith(_ =>
        {
            try
            {
                logger.Notification("{0}", format());
            }
            catch
            {
                // A diagnostic that could not be written must not take anything else down.
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}
