using System.Collections.Concurrent;
using VintageHorizons.Net;

namespace VintageHorizons.Checks;

/// <summary>Durable-save identity, acknowledgement, coalescing, and retry invariants.</summary>
public static class PersistenceChecks
{
    public static void Run(Check c)
    {
        DirtyOrderAndAgeAreStable(c);
        SustainedInflowCannotStarveOldKeys(c);
        CheckpointCadenceAndBudget(c);
        BackToBackCheckpointBatches(c);
        ServerStatusNamesTheBacklogs(c);
        ExactAcknowledgements(c);
        PendingSnapshotsCoalesce(c);
        FailedWritesAcknowledgeAndRecover(c);
        GatedWorkerWaitsForCheckpoint(c);
        FullWorkerBacklogDrains(c);
        NewestRevisionSurvivesRestart(c);
        RetryDelayIsBounded(c);
    }

    static void DirtyOrderAndAgeAreStable(Check c)
    {
        long now = 1_000;
        var dirty = new LodSaveDirtySet(() => now);
        c.True(dirty.Add(11), "the first dirty key enters the ordered set");
        now = 1_100;
        c.True(dirty.Add(22), "a later dirty key enters behind it");
        now = 1_400;
        c.False(dirty.Add(11), "re-dirtying does not create a second obligation");
        c.SeqEq(new long[] { 11, 22 }, dirty.ToArray(),
            "re-dirtying preserves the key's original oldest-first position");
        c.Eq(400L, dirty.OldestAgeMilliseconds,
            "re-dirtying preserves the original first-dirty age");

        c.True(dirty.Remove(11), "a durable acknowledgement removes the oldest key");
        now = 1_500;
        c.True(dirty.Add(11), "a later mutation can create a new obligation");
        c.SeqEq(new long[] { 22, 11 }, dirty.ToArray(),
            "a newly dirty revision joins at the tail after the old one became durable");
        c.Eq(400L, dirty.OldestAgeMilliseconds,
            "oldest age advances to the next surviving obligation");
    }

    static void SustainedInflowCannotStarveOldKeys(Check c)
    {
        long now = 0;
        var dirty = new LodSaveDirtySet(() => now);
        const int originalCount = 600;
        for (long key = 0; key < originalCount; key++)
        {
            dirty.Add(key);
            now++;
        }

        var originalRemaining = new HashSet<long>(Enumerable.Range(0, originalCount)
            .Select(value => (long)value));
        long nextNewKey = 10_000;
        for (int checkpoint = 1; checkpoint <= 3; checkpoint++)
        {
            long[] batch = dirty.Take(LodPipeline.MaxStorageBacklog).ToArray();
            c.Eq(LodPipeline.MaxStorageBacklog, batch.Length,
                $"checkpoint {checkpoint} fills its bounded batch under sustained inflow");
            if (checkpoint == 1)
            {
                c.SeqEq(Enumerable.Range(0, LodPipeline.MaxStorageBacklog)
                        .Select(value => (long)value).ToArray(), batch,
                    "the first checkpoint takes the oldest keys in exact order");
            }
            foreach (long key in batch)
            {
                dirty.Remove(key);
                originalRemaining.Remove(key);
            }

            // More arrives than one checkpoint can save. The original tail must still
            // stay ahead of all of it and finish within ceil(600 / 256) checkpoints.
            for (int added = 0; added < 300; added++)
            {
                dirty.Add(nextNewKey++);
                now++;
            }
        }

        c.Eq(0, originalRemaining.Count,
            "inflow above the checkpoint cap cannot starve any pre-existing key");
    }

    static void CheckpointCadenceAndBudget(Check c)
    {
        const long now = 12_345;
        c.Eq(now, LodPipeline.NextPersistenceCheckpointStart(now, readyFollowUp: true),
            "a ready save backlog starts its next checkpoint immediately");
        c.Eq(now + LodPipeline.PersistenceCheckpointIntervalMs,
            LodPipeline.NextPersistenceCheckpointStart(now, readyFollowUp: false),
            "an idle pipeline retains the coarse thirty-second checkpoint cadence");
        c.True(LodPipeline.PersistenceSnapshotsPerTick > 1,
            "an owning-thread tick may freeze several sections");
    }

    static void BackToBackCheckpointBatches(Check c)
    {
        const int count = 600;
        var dirty = new LodSaveDirtySet();
        for (int sx = 0; sx < count; sx++) dirty.Add(LodWorld.SectionKey(0, sx, 0));

        var written = new ConcurrentQueue<long>();
        using var store = new LodStore(null!);
        using var worker = new LodStorageThread(store,
            snapshot => written.Enqueue(snapshot.Key), autoCommitSaves: false);
        LodSection section = Fixtures.SolidSection();
        int checkpoints = 0;

        while (dirty.Count > 0)
        {
            long[] batch = dirty.Take(LodPipeline.MaxStorageBacklog).ToArray();
            foreach (long key in batch)
            {
                c.True(worker.Enqueue(Fixtures.Snapshot(section,
                    sx: LodWorld.KeySx(key), revision: 1)),
                    "the fake storage owner accepts the ordered checkpoint snapshot");
            }
            worker.CommitPendingSaves();
            c.True(worker.Drain(5000),
                "a published checkpoint becomes durable before the next begins");

            int acknowledged = 0;
            while (worker.TryTakeSaveCompletion(out LodSaveCompletion completion))
            {
                c.True(completion.Succeeded,
                    "the fake storage owner acknowledges a checkpoint row");
                dirty.Remove(completion.Key);
                acknowledged++;
            }
            c.Eq(batch.Length, acknowledged,
                "every row in the checkpoint receives an exact acknowledgement");
            checkpoints++;
        }

        c.Eq(3, checkpoints, "six hundred rows drain as three back-to-back checkpoints");
        c.Eq((long)count, worker.SectionsWritten,
            "the fake storage owner writes the complete sustained backlog");
        c.SeqEq(Enumerable.Range(0, count)
                .Select(sx => LodWorld.SectionKey(0, sx, 0)).ToArray(),
            written.ToArray(), "back-to-back storage batches retain oldest-first order");
    }

    static void ServerStatusNamesTheBacklogs(Check c)
    {
        string line = LodServerCaptureSystem.FormatCacheWorkStatus(
            17, 125_000, 8.5, 901, 23, 4, 2);
        c.True(line.Contains("17 dirty", StringComparison.Ordinal)
            && line.Contains("oldest dirty 125.0s", StringComparison.Ordinal)
            && line.Contains("8.50 saves/s", StringComparison.Ordinal)
            && line.Contains("capture backlog 23", StringComparison.Ordinal)
            && line.Contains("unloaded-before-capture drops 4", StringComparison.Ordinal)
            && line.Contains("capture errors 2", StringComparison.Ordinal),
            "the server status line exposes every persistence and capture health signal");
    }

    static void ExactAcknowledgements(Check c)
    {
        var world = new LodWorld();
        long key = LodWorld.SectionKey(0, 4, 5);
        world.Sections[key] = Fixtures.SolidSection();

        world.MarkChanged(key);
        c.Eq(1L, world.PersistenceRevision(key), "a content change starts persistence revision one");
        c.True(world.TryQueueSave(key, out long first), "the dirty revision can be reserved");

        world.MarkChanged(key);
        c.Eq(2L, world.PersistenceRevision(key), "a repeated mutation advances the row revision");
        c.False(world.CompleteSave(key, first, succeeded: true),
            "a stale success cannot acknowledge newer content");
        c.True(world.SaveDirty.Contains(key), "newer content stays dirty after the stale success");

        c.True(world.TryQueueSave(key, out long newest), "the newer revision can be queued");
        c.Eq(2L, newest, "the replacement snapshot names the newest revision");
        c.False(world.CompleteSave(key, newest, succeeded: false),
            "a failed write never clears dirty state");
        c.True(world.SaveDirty.Contains(key), "a failed revision remains a durable obligation");
        c.Eq(0L, world.QueuedSaveRevision(key), "failure releases the revision for retry");

        c.True(world.TryQueueSave(key, out long retry), "the same revision can be retried");
        c.True(world.CompleteSave(key, retry, succeeded: true),
            "an exact successful acknowledgement clears the obligation");
        c.False(world.SaveDirty.Contains(key), "only the exact newest success clears dirty state");

        long contentRevision = world.ContentRevision(key);
        world.MarkSaveDirty(key);
        c.Eq(contentRevision, world.ContentRevision(key),
            "a stored flag change does not pretend terrain content changed");
        c.Eq(3L, world.PersistenceRevision(key),
            "a stored flag change still advances the persistence revision");
    }

    static void PendingSnapshotsCoalesce(Check c)
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var written = new ConcurrentQueue<(long Key, long Revision)>();
        using var store = new LodStore(null!);
        using var worker = new LodStorageThread(store, snapshot =>
        {
            written.Enqueue((snapshot.Key, snapshot.Revision));
            if (snapshot.SX == 1)
            {
                entered.Set();
                release.Wait(5000);
            }
        });

        worker.Enqueue(Fixtures.Snapshot(Fixtures.SolidSection(), sx: 1, revision: 1));
        c.True(entered.Wait(5000), "the fixture holds one save in execution");
        worker.Enqueue(Fixtures.Snapshot(Fixtures.SolidSection(), sx: 2, revision: 1));
        worker.Enqueue(Fixtures.Snapshot(Fixtures.SolidSection(), sx: 2, revision: 2));
        c.Eq(2L, worker.Backlog,
            "one executing key plus one coalesced pending key consume two backlog slots");

        release.Set();
        c.True(worker.Drain(5000), "the coalesced queue drains");
        var rows = written.ToArray();
        c.Eq(2, rows.Length, "the superseded pending snapshot is never written");
        c.Eq(2L, rows.Single(row => LodWorld.KeySx(row.Key) == 2).Revision,
            "the pending key writes only its newest revision");

        var completions = new List<LodSaveCompletion>();
        while (worker.TryTakeSaveCompletion(out LodSaveCompletion completion)) completions.Add(completion);
        c.Eq(2, completions.Count, "every executed write emits one acknowledgement");
        c.False(completions.Any(completion => completion.Key == LodWorld.SectionKey(0, 2, 0)
            && completion.Revision == 1), "a coalesced revision emits no false acknowledgement");
    }

    static void FailedWritesAcknowledgeAndRecover(Check c)
    {
        int attempts = 0;
        using var store = new LodStore(null!);
        using var worker = new LodStorageThread(store, _ =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("injected save failure");
        });
        LodSaveSnapshot snapshot = Fixtures.Snapshot(Fixtures.SolidSection(), sx: 7, revision: 3);

        c.True(worker.Enqueue(snapshot), "the injected-failure snapshot is accepted");
        c.True(worker.Drain(5000), "a failed write still completes its worker responsibility");
        c.True(worker.TryTakeSaveCompletion(out LodSaveCompletion failed),
            "a failed write emits an acknowledgement");
        c.False(failed.Succeeded, "the acknowledgement identifies the injected failure");
        c.Eq(3L, failed.Revision, "the failure names the exact revision");

        c.True(worker.Enqueue(snapshot), "the same revision can be submitted again");
        c.True(worker.Drain(5000), "the storage worker survives the exception");
        c.True(worker.TryTakeSaveCompletion(out LodSaveCompletion recovered),
            "the retry emits its own acknowledgement");
        c.True(recovered.Succeeded, "the retry succeeds after the injected failure");
        c.Eq(1L, worker.SectionsWritten, "only the durable retry counts as written");
    }

    static void GatedWorkerWaitsForCheckpoint(Check c)
    {
        int writes = 0;
        using var store = new LodStore(null!);
        using var worker = new LodStorageThread(store,
            _ => Interlocked.Increment(ref writes), autoCommitSaves: false);

        c.True(worker.Enqueue(Fixtures.Snapshot(Fixtures.SolidSection(), sx: 9, revision: 1)),
            "the gated storage worker accepts a RAM snapshot");
        Thread.Sleep(300); // longer than the worker's idle wake: polling must not commit it
        c.Eq(0, Volatile.Read(ref writes),
            "a queued snapshot does not write before its checkpoint is published");

        worker.CommitPendingSaves();
        c.True(worker.Drain(5000), "the explicit checkpoint drains");
        c.Eq(1, Volatile.Read(ref writes), "publishing the checkpoint writes the snapshot once");
    }

    static void FullWorkerBacklogDrains(Check c)
    {
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var store = new LodStore(null!);
        using var worker = new LodStorageThread(store, snapshot =>
        {
            if (snapshot.SX != 0) return;
            entered.Set();
            release.Wait(5000);
        });
        const int count = 300;
        worker.Enqueue(Fixtures.Snapshot(Fixtures.SolidSection(), sx: 0, revision: 1));
        if (!entered.Wait(5000)) throw new TimeoutException("storage backlog fixture did not start");
        for (int i = 1; i < count; i++)
            worker.Enqueue(Fixtures.Snapshot(Fixtures.SolidSection(), sx: i, revision: 1));

        c.Eq((long)count, worker.Backlog,
            "the fixture really exceeds the pipeline admission cap before shutdown draining");
        release.Set();
        c.True(worker.Drain(5000), "a backlog larger than the pipeline admission cap drains fully");
        int acknowledged = 0;
        while (worker.TryTakeSaveCompletion(out _)) acknowledged++;
        c.Eq(count, acknowledged, "shutdown-style draining acknowledges every distinct queued key");
    }

    static void NewestRevisionSurvivesRestart(Check c)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-save-revision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cache.db");
        try
        {
            using (var store = new LodStore(new CaptureLogger()))
            {
                c.True(store.Open(path), "the revision restart fixture opens");
                using var worker = new LodStorageThread(store);
                worker.Enqueue(Fixtures.Snapshot(Fixtures.SolidSection(yTop: 8),
                    sx: 11, sz: 12, revision: 1));
                worker.Enqueue(Fixtures.Snapshot(Fixtures.SolidSection(yTop: 20),
                    sx: 11, sz: 12, revision: 2));
                c.True(worker.Drain(5000), "both repeated mutations become durable before close");
            }

            using var reopened = new LodStore(new CaptureLogger());
            c.True(reopened.Open(path), "the cache reopens after the repeated mutation");
            LodSection? restored = reopened.LoadSection(0, 11, 12, null!, resolveBlockIds: false);
            c.True(restored != null, "the newest row remains readable after restart");
            if (restored != null)
                c.Eq(20, LodSection.RunYTop(restored.ColumnRuns(0)[0]),
                    "restart reads the newest repeated mutation, never its predecessor");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    static void RetryDelayIsBounded(Check c)
    {
        c.Eq(250, LodSaveRetry.DelayMilliseconds(1), "the first retry waits briefly");
        c.Eq(500, LodSaveRetry.DelayMilliseconds(2), "retry delay grows exponentially");
        c.Eq(30000, LodSaveRetry.DelayMilliseconds(30), "retry delay has a finite ceiling");
        c.Eq(30000, LodSaveRetry.DelayMilliseconds(int.MaxValue),
            "extreme failure counts cannot overflow the retry delay");
    }
}
