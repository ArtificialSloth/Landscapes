using System.Diagnostics;
using VintageHorizons.Net;
using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>What one completed savegame index pass learned. Published to the owning thread once.</summary>
public sealed class LodSavegameIndexResult
{
    /// <summary>Every column the savegame holds, whole-world rather than a sweep-sized square.</summary>
    public required LodColumnMap Exists { get; init; }

    /// <summary>Sealed content fingerprint per column, keyed by <see cref="LodColumnMap.Key"/>.</summary>
    public required Dictionary<long, ulong> Fingerprints { get; init; }

    /// <summary>The savegame's change counter, or null under WAL or mid-write.</summary>
    public required uint? ChangeCounter { get; init; }

    public required bool IsWal { get; init; }
    public required double ElapsedSeconds { get; init; }
}

/// <summary>
/// Runs one whole-world savegame index on a thread of its own and publishes the answer to
/// the owning thread (terrain-change plan phase A3's input).
///
/// This replaces the sweep's probe pass. That pass is not slow because of its access
/// pattern - random point lookups measured 13,990/s against a sequential scan's 12,536/s -
/// but because it is deliberately paced to about 320/s through the engine's chunk thread,
/// which is the same thread that loads terrain for the player. 70,225 positions at that
/// rate is the 5m35s the owner's world spends every single start, capturing nothing while
/// it runs.
///
/// Failure is not an error. Any problem disables the read path for the session and the
/// caller falls back to probing exactly as before.
/// </summary>
public sealed class LodSavegameIndex : IDisposable
{
    readonly ILogger logger;
    readonly LodSavegameReader reader;
    readonly object gate = new();

    Thread? thread;
    LodSavegameIndexResult? result;
    bool finished;
    bool failed;
    volatile bool stopping;

    /// <summary>True once the pass has ended, whether it produced an answer or gave up.</summary>
    public bool Finished { get { lock (gate) return finished; } }

    /// <summary>True when the caller must fall back to the engine probe pass.</summary>
    public bool Failed { get { lock (gate) return failed; } }

    public LodSavegameIndex(ILogger logger, string savegamePath)
    {
        this.logger = logger;
        reader = new LodSavegameReader(logger, savegamePath);
    }

    public void Start()
    {
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "vh-savegame-index",
            // Below normal: this is startup work that must never take a slice from the
            // engine's own threads, and it has no deadline.
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    /// <summary>
    /// Takes the finished index, or false while it is still running or if it failed. Call
    /// from the owning thread; the result is handed over exactly once.
    /// </summary>
    public bool TryTake(out LodSavegameIndexResult? taken)
    {
        lock (gate)
        {
            taken = result;
            result = null;
            return taken != null;
        }
    }

    public void Stop() => stopping = true;

    void Run()
    {
        var clock = Stopwatch.StartNew();
        try
        {
            if (!reader.Open())
            {
                Fail();
                return;
            }

            var scan = new LodSavegameScan(reader);
            var exists = new LodColumnMap();
            if (!reader.Available || !scan.ReadExistence(exists, ShouldStop))
            {
                Fail();
                return;
            }

            var raw = new Dictionary<long, LodColumnFingerprint>(exists.Count);
            if (!scan.ReadFingerprints(raw, ShouldStop))
            {
                Fail();
                return;
            }

            var prints = new Dictionary<long, ulong>(raw.Count);
            foreach ((long key, LodColumnFingerprint column) in raw)
            {
                prints[key] = LodSavegameScan.Seal(column);
            }

            var finishedResult = new LodSavegameIndexResult
            {
                Exists = exists,
                Fingerprints = prints,
                ChangeCounter = reader.ReadChangeCounter(),
                IsWal = reader.IsWal,
                ElapsedSeconds = clock.Elapsed.TotalSeconds,
            };

            lock (gate)
            {
                result = finishedResult;
                finished = true;
            }

            logger.Notification(
                "[VintageHorizons] Read the savegame directly in {0:0.0}s: {1} columns exist, "
                + "{2} fingerprinted. That replaces {3} paced engine probes, which are served by "
                + "the same thread that loads terrain for players.",
                finishedResult.ElapsedSeconds, exists.Count, prints.Count, exists.Count);
        }
        catch (Exception e)
        {
            logger.Notification(
                "[VintageHorizons] Reading the savegame directly did not finish ({0}). "
                + "The sweep falls back to probing the engine, exactly as before.", e.Message);
            Fail();
        }
        finally
        {
            reader.Dispose();
        }
    }

    bool ShouldStop() => stopping;

    void Fail()
    {
        lock (gate)
        {
            failed = true;
            finished = true;
            result = null;
        }
    }

    public void Dispose()
    {
        Stop();
        // Never join: a whole-world read can be mid-batch, and shutdown must not wait for
        // it. The thread is background, checks ShouldStop between rows, and owns nothing
        // the rest of the mod can see until it publishes.
        reader.Dispose();
    }
}
