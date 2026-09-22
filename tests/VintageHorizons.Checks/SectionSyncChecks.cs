using Microsoft.Data.Sqlite;
using VintageHorizons.Net;

namespace VintageHorizons.Checks;

/// <summary>
/// Section sync (the terrain-change plan's Phase B): a server cache that numbers every row it
/// writes, a client that remembers how far it caught up and what it still owes itself, and the
/// spans between them.
///
/// The failures this guards against are silent. A version that goes backwards, a span that
/// skips a range, or a record that claims a version without the debts below it all end the same
/// way: a returning player keeps stale distant terrain for good, and nothing says so. So the
/// orderings are checked here directly, and the last check walks whole sessions end to end
/// against real cache files - the plan's acceptance tests 8, 9 and 10 at harness level.
/// </summary>
public static class SectionSyncChecks
{
    const long NoRecord = LodAssistSync.NoRecord;

    public static void Run(Check c)
    {
        ServerCacheNumbersWhatItWrites(c);
        AnExistingCacheGainsVersionsInPlace(c);
        AGenerationChangesOnlyWhenTheLineageDoes(c);
        AClientCacheDoesNotVersion(c);
        TheClientRecordRoundTrips(c);
        SpansAreLevelZeroOrderedAndBounded(c);
        LedgerKeepsEachPlayersSpansContiguous(c);
        TrackerOwesOnlyWhatItHolds(c);
        TrackerRecordsAVersionOnlyWithItsDebts(c);
        TrackerSettlesOnlyANewEnoughMerge(c);
        TrackerFetchesNearestFirstInsideTheRadius(c);
        TrackerStopsRecordingAfterAGap(c);
        TrackerOutsideItsRole(c);
        NetworkRefreshReportsHowItEnds(c);
        ClientRoutesVersionedSpans(c);
        ARejoinReceivesExactlyWhatChanged(c);
    }

    static long K(int level, int sx, int sz) => LodWorld.SectionKey(level, sx, sz);

    static void ServerCacheNumbersWhatItWrites(Check c)
    {
        string dir = TempDir("vh-sync-versions");
        string path = Path.Combine(dir, "server.db");
        try
        {
            string generation = "";
            WithStore(path, versioned: true, store =>
            {
                generation = store.SectionGeneration;
                c.True(generation.Length > 0, "a versioned cache has a generation");
                c.Eq(1L, store.SectionVersion, "a new versioned cache starts at the baseline 1");
                store.SaveBlob(0, 1, 1, new byte[] { 4, 1 }, applyToParent: false);
                store.SaveBlob(0, 2, 2, new byte[] { 4, 2 }, applyToParent: false);
                store.SaveBatch(new[]
                {
                    Fixtures.Snapshot(Fixtures.SolidSection(), level: 0, sx: 3, sz: 3),
                    Fixtures.Snapshot(Fixtures.SolidSection(), level: 1, sx: 0, sz: 0),
                });
                c.Eq(4L, store.SectionVersion, "each commit moves the version: two rows, then one checkpoint");
            });

            Dictionary<long, long> versions = ReadVersions(path);
            c.Eq(2L, versions[K(0, 1, 1)], "the first row is numbered above the baseline");
            c.Eq(3L, versions[K(0, 2, 2)], "the next row above that");
            c.Eq((4L, 4L), (versions[K(0, 3, 3)], versions[K(1, 0, 0)]),
                "every row in one checkpoint shares its version");

            WithStore(path, versioned: true, store =>
            {
                c.Eq(generation, store.SectionGeneration, "the generation survives a reopen");
                c.Eq(4L, store.SectionVersion, "and so does the version");
                store.SaveBlob(0, 1, 1, new byte[] { 4, 5 }, applyToParent: false);
            });
            versions = ReadVersions(path);
            c.Eq(5L, versions[K(0, 1, 1)], "a rewritten row takes the next version");
            c.Eq(3L, versions[K(0, 2, 2)], "an untouched row keeps its own");
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    /// <summary>
    /// The column is added in place, not by a schema bump, because a bump wipes the cache. Rows
    /// that predate it read as version 0, below the baseline, so a client that has never caught
    /// up is told about them and one that has is not.
    /// </summary>
    static void AnExistingCacheGainsVersionsInPlace(Check c)
    {
        string dir = TempDir("vh-sync-in-place");
        string path = Path.Combine(dir, "server.db");
        try
        {
            WithStore(path, versioned: false, store =>
            {
                store.SaveBlob(0, 1, 1, new byte[] { 4, 1 }, applyToParent: false);
                store.SaveBlob(2, 0, 0, new byte[] { 4, 2 }, applyToParent: false);
            });
            WithStore(path, versioned: true, store =>
            {
                c.Eq(1L, store.SectionVersion, "a cache whose rows predate versions starts at the baseline");
                c.True(store.SectionGeneration.Length > 0, "and is given a generation");
            });

            Dictionary<long, long> versions = ReadVersions(path);
            c.Eq(2, versions.Count, "no row was lost adding the column");
            c.True(versions.Values.All(v => v == 0), "rows from before versions read as version 0");

            WithStore(path, versioned: true, store =>
                store.SaveBlob(0, 5, 5, new byte[] { 4, 3 }, applyToParent: false));
            c.Eq(2L, ReadVersions(path)[K(0, 5, 5)], "the first new write is numbered above the baseline");
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    /// <summary>
    /// A generation names a lineage of rows whose versions can be trusted to be in order. Two
    /// things break that: a format purge, and a write by a build that does not stamp versions -
    /// its row reads as 0 and a client already past 0 would never hear of the change.
    /// </summary>
    static void AGenerationChangesOnlyWhenTheLineageDoes(Check c)
    {
        string dir = TempDir("vh-sync-generation");
        string path = Path.Combine(dir, "server.db");
        try
        {
            string first = "";
            WithStore(path, versioned: true, store =>
            {
                first = store.SectionGeneration;
                store.SaveBlob(0, 1, 1, new byte[] { 4, 1 }, applyToParent: false);
            });
            string reopened = "";
            WithStore(path, versioned: true, store => reopened = store.SectionGeneration);
            c.Eq(first, reopened, "this build's own writes never change the generation");

            // What an older build's upsert looks like: it names no version.
            ExecuteRaw(path, "INSERT OR REPLACE INTO Section (Detail, SX, SZ, Data, ApplyToParent, ModifiedMs) "
                + "VALUES (0, 9, 9, x'0401', 0, 0)");
            var logger = new CaptureLogger();
            string afterLegacy;
            using (var store = new LodStore(logger, versionSections: true))
            {
                c.True(store.Open(path), "the cache opens after an older build wrote to it");
                afterLegacy = store.SectionGeneration;
            }
            c.True(afterLegacy != first, "a write by a build without versions re-mints the generation");
            c.True(logger.Contains("older build wrote"), "and says so in the log");

            string settled = "";
            WithStore(path, versioned: true, store => settled = store.SectionGeneration);
            c.Eq(afterLegacy, settled, "once: the mark is cleared when the generation is re-minted");

            ExecuteRaw(path, "UPDATE Meta SET Value='1' WHERE Key='FormatVersion'");
            string afterPurge = "";
            WithStore(path, versioned: true, store => afterPurge = store.SectionGeneration);
            c.True(afterPurge != settled, "a format purge re-mints it too");
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    static void AClientCacheDoesNotVersion(Check c)
    {
        string dir = TempDir("vh-sync-client");
        string path = Path.Combine(dir, "client.db");
        try
        {
            WithStore(path, versioned: false, store =>
            {
                c.Eq(("", 0L), (store.SectionGeneration, store.SectionVersion),
                    "a client cache has no generation and no version");
                store.SaveBlob(0, 1, 1, new byte[] { 4, 1 }, applyToParent: false);
            });
            c.False(HasVersionColumn(path), "and no version column: nothing asks it what changed");
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    /// <summary>
    /// The client's record: the generation and version it caught up to, and the sections still
    /// owed below that. It survives a reopen, applies in order through the storage worker, and
    /// goes with the sections it describes when a format purge deletes them.
    /// </summary>
    static void TheClientRecordRoundTrips(Check c)
    {
        string dir = TempDir("vh-sync-record");
        string path = Path.Combine(dir, "client.db");
        long a = K(0, 1, 1), b = K(0, 2, 2), d = K(0, 3, 3);
        try
        {
            WithStore(path, versioned: false, store =>
            {
                LodAssistSyncState none = store.LoadAssistSync();
                c.Eq(("", NoRecord, 0), (none.Generation, none.Version, none.Owed.Count),
                    "a client cache with no record reads as none");

                var span = new LodAssistSyncWrite { Generation = "g", Version = 12 };
                span.Owed.Add((a, 10));
                span.Owed.Add((b, 12));
                c.True(store.SaveAssistSync(span), "a span's record commits");
                var settle = new LodAssistSyncWrite();
                settle.Settled.Add(a);
                c.True(store.SaveAssistSync(settle), "a settle commits");
            });

            WithStore(path, versioned: false, store =>
            {
                LodAssistSyncState back = store.LoadAssistSync();
                c.Eq(("g", 12L), (back.Generation, back.Version), "the record survives a reopen");
                c.SeqEq(new[] { (b, 12L) }, back.Owed, "with exactly the debts not yet settled");

                using var worker = new LodStorageThread(store);
                var restart = new LodAssistSyncWrite { ResetOwed = true, Generation = "h", Version = 3 };
                restart.Owed.Add((d, 1));
                var settleNew = new LodAssistSyncWrite();
                settleNew.Settled.Add(d);
                var oweAgain = new LodAssistSyncWrite { Version = 4 };
                oweAgain.Owed.Add((d, 4));
                worker.SaveAssistSync(new[] { restart, settleNew, oweAgain });
            });

            WithStore(path, versioned: false, store =>
            {
                LodAssistSyncState back = store.LoadAssistSync();
                c.Eq(("h", 4L), (back.Generation, back.Version),
                    "writes through the storage worker apply in order");
                c.SeqEq(new[] { (d, 4L) }, back.Owed,
                    "a reset replaces the whole list, and a section settled then owed again stays owed");
            });

            ExecuteRaw(path, "UPDATE Meta SET Value='1' WHERE Key='FormatVersion'");
            WithStore(path, versioned: false, store =>
            {
                LodAssistSyncState purged = store.LoadAssistSync();
                c.Eq(("", NoRecord, 0), (purged.Generation, purged.Version, purged.Owed.Count),
                    "a format purge forgets the record with the sections it described");
            });
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    static void SpansAreLevelZeroOrderedAndBounded(Check c)
    {
        string dir = TempDir("vh-sync-spans");
        string path = Path.Combine(dir, "server.db");
        try
        {
            WithStore(path, versioned: true, store =>
            {
                store.SaveBlob(0, 1, 1, new byte[] { 4, 1 }, applyToParent: false); // 2
                store.SaveBlob(1, 0, 0, new byte[] { 4, 2 }, applyToParent: false); // 3
                store.SaveBlob(0, 2, 2, new byte[] { 4, 3 }, applyToParent: false); // 4
            });

            using var reader = new LodAssistBlobReader(path, new CaptureLogger(), trackAllocations: false);
            reader.EnqueueSpan(new LodSectionSpanQuery("p", 1, 2));
            reader.EnqueueSpan(new LodSectionSpanQuery("p", 1, NoRecord));
            reader.EnqueueSpan(new LodSectionSpanQuery("p", 1, 99));
            reader.EnqueueSpan(new LodSectionSpanQuery(null, 0, 4));
            reader.EnqueueSpan(new LodSectionSpanQuery(null, 0, 1, BaselineOnly: true));
            var results = new List<LodSectionSpanResult>();
            for (int i = 0; i < 5; i++) results.Add(WaitForSpan(reader));

            c.SeqEq(new long[] { 2, NoRecord, 99, 4, 1 }, results.Select(r => r.Query.From).ToArray(),
                "span results come back in the order they were asked for");
            c.True(results.All(r => !r.Failed), "and none fails");

            c.Eq((2L, 4L), (results[0].From, results[0].To), "a join from 2 spans to the cache's version");
            c.SeqEq(new[] { K(0, 2, 2) }, results[0].Keys,
                "and holds only level-0 sections past its start - the parent is rebuilt by the client");
            c.SeqEq(new[] { 4L }, results[0].Versions, "each with its own version");

            c.SeqEq(new[] { K(0, 1, 1), K(0, 2, 2) }, results[1].Keys, "a span from no record holds every level-0 section");
            c.SeqEq(new[] { 2L, 4L }, results[1].Versions, "in ascending version order");

            c.Eq(NoRecord, results[2].From,
                "a record newer than the cache - an older copy restored - is answered as no record");
            c.Eq(2, results[2].Keys.Length, "so the client re-checks everything");

            c.Eq((4L, 0), (results[3].To, results[3].Keys.Length), "a broadcast with nothing new is empty");
            c.Eq((4L, 0), (results[4].To, results[4].Keys.Length), "a baseline reports the end and no rows");

            c.True(reader.TryEnqueue(new LodAssistBlobReadRequest("p", 1, K(0, 2, 2), Environment.TickCount64)),
                "a blob read is accepted beside span queries");
            LodAssistBlobReadResult blob = WaitForBlob(reader);
            c.Eq((K(0, 2, 2), 4L), (blob.Key, blob.Version), "a served blob carries the version of its row");
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    /// <summary>
    /// Every syncing player's spans must follow on from one another. The ledger relies on joins
    /// and broadcasts being applied in the order they were queried; given that, each player is
    /// sent exactly the rows past its own end, and its end moves to the broadcast's.
    /// </summary>
    static void LedgerKeepsEachPlayersSpansContiguous(Check c)
    {
        var ledger = new LodSectionSyncLedger();
        c.Eq(10L, ledger.EnsureBaseline(10), "the broadcast baseline is fixed the first time it is asked for");
        c.Eq(10L, ledger.EnsureBaseline(15), "and never moved by a later ask");

        c.Eq(7L, ledger.Join("a", 1, "g", 7, "g"), "a record in this generation continues from its version");
        c.Eq(NoRecord, ledger.Join("b", 2, "old", 7, "g"), "another generation's record re-checks everything");
        c.Eq(NoRecord, ledger.Join("c", 3, "", 0, "g"), "no record re-checks everything");

        c.Eq(0, ledger.Broadcast(12, new long[] { 11, 12 }).Count,
            "a broadcast applied before a player's join span is not that player's");
        c.False(ledger.Joined("a", 99, 14), "a join result for a replaced greeting is dropped");
        c.True(ledger.Joined("a", 1, 14), "the current one is taken");

        c.SeqEq(new[] { ("a", 14L, 2) }, ledger.Broadcast(20, new long[] { 13, 14, 15, 20 }),
            "a player is sent only the rows past its own end, starting from that end");
        c.Eq(0, ledger.Broadcast(25, Array.Empty<long>()).Count, "an empty broadcast sends nothing");
        c.SeqEq(new[] { ("a", 20L, 0) }, ledger.Broadcast(30, new long[] { 27 }),
            "the next span starts where the last one sent ended, across the empty one");

        ledger.Abandon("b", 2);
        c.False(ledger.IsSyncing("b"), "a player whose join could not be read is served without versions");
        ledger.Forget("a");
        c.False(ledger.IsSyncing("a"), "a player who left is forgotten");
        ledger.Clear();
        c.Eq((0, (long?)null), (ledger.Syncing, ledger.BroadcastThrough),
            "a replaced cache clears every player and the baseline");
    }

    /// <summary>A tracker that has completed a join span at <paramref name="version"/>.</summary>
    static LodAssistSync Established(long version, IReadOnlyList<(long Key, long Version)> owed,
        Func<long, bool> holds)
    {
        var sync = new LodAssistSync();
        sync.Load(new LodAssistSyncState("g", version, owed.ToList()));
        sync.Begin(participate: true);
        sync.OnWelcome("g");
        sync.OnSpan(version, version, Array.Empty<long>(), Array.Empty<long>(), last: true, holds);
        sync.TakeWrites();
        return sync;
    }

    static void TrackerOwesOnlyWhatItHolds(Check c)
    {
        long held1 = K(0, 1, 1), held2 = K(0, 2, 2), elsewhere = K(0, 3, 3);
        var holds = new HashSet<long> { held1, held2 };
        var sync = new LodAssistSync();
        sync.Load(new LodAssistSyncState("", NoRecord, new()));
        c.Eq(new LodAssistSyncRequest("", NoRecord), sync.Begin(participate: true),
            "a client with no record says so at join");

        c.True(sync.OnSpan(NoRecord, 12, new[] { held1 }, new long[] { 0 }, true, holds.Contains) == null,
            "a span before the welcome is not this tracker's");
        sync.OnWelcome("g");

        c.True(sync.OnSpan(NoRecord, 12, new[] { held1, elsewhere }, new long[] { 0, 5 },
            last: false, holds.Contains) == null, "a span is not done until its last chunk");
        c.True(sync.TakeWrites() == null, "and nothing is recorded before then");

        LodAssistSpanSummary? done = sync.OnSpan(NoRecord, 12, new[] { held2 }, new long[] { 12 },
            last: true, holds.Contains);
        c.True(done is { Joined: true, FromZero: true, Announced: 3, Owed: 2, Recorded: true },
            "a span from no record owes every held section it lists and none it does not");

        List<LodAssistSyncWrite> writes = sync.TakeWrites() ?? new();
        c.Eq(1, writes.Count, "the whole span is one write");
        LodAssistSyncWrite write = writes.Count > 0 ? writes[0] : new();
        c.True(write.ResetOwed, "a re-check from zero replaces the owed list");
        c.Eq(("g", (long?)12), (write.Generation, write.Version),
            "and records the generation and version in the same write as the debts");
        c.SeqEq(new[] { (held1, 0L), (held2, 12L) }, write.Owed.OrderBy(o => o.Key).ToArray(),
            "each debt keeps the version it was announced at");
        c.Eq((2, 12L), (sync.OwedCount, sync.RecordedVersion), "and the tracker agrees");
    }

    static void TrackerRecordsAVersionOnlyWithItsDebts(Check c)
    {
        long earlier = K(0, 1, 1), changed = K(0, 2, 2), notHeld = K(0, 4, 4);
        var holds = new HashSet<long> { earlier, changed };
        var sync = new LodAssistSync();
        sync.Load(new LodAssistSyncState("g", 12, new() { (earlier, 10) }));
        c.Eq(new LodAssistSyncRequest("g", 12), sync.Begin(participate: true),
            "a client with a record asks from it");
        sync.OnWelcome("g");

        LodAssistSpanSummary? join = sync.OnSpan(12, 20, new[] { changed, notHeld }, new long[] { 18, 19 },
            last: true, holds.Contains);
        c.True(join is { Joined: true, FromZero: false, Owed: 1, Recorded: true },
            "a join from the record owes only the held sections changed since");
        c.Eq(2, sync.OwedCount, "and keeps what was already owed");
        LodAssistSyncWrite write = (sync.TakeWrites() ?? new())[0];
        c.Eq((false, (string?)null, (long?)20), (write.ResetOwed, write.Generation, write.Version),
            "a continuing span moves the version and leaves the list and generation alone");
        c.SeqEq(new[] { (changed, 18L) }, write.Owed, "and adds only its own debts");

        // A settle queued before a span that owes the same section again must reach the record
        // first, or the record would lose a debt the tracker still has.
        sync.Absorbed(changed, 18);
        sync.OnSpan(20, 25, new[] { changed }, new long[] { 25 }, last: true, holds.Contains);
        List<LodAssistSyncWrite> ordered = sync.TakeWrites() ?? new();
        c.Eq(2, ordered.Count, "a settle and a later span are separate writes");
        c.True(ordered.Count == 2 && ordered[0].Settled.Contains(changed) && ordered[1].Owed.Contains((changed, 25L)),
            "in the order they happened");
        c.Eq(25L, sync.RecordedVersion, "a live span that follows on is recorded");
    }

    static void TrackerSettlesOnlyANewEnoughMerge(Check c)
    {
        long key = K(0, 5, 5);
        var holds = new HashSet<long> { key };
        LodAssistSync sync = Established(30, new[] { (key, 28L) }, holds.Contains);

        sync.Absorbed(key, 27);
        c.Eq(1, sync.OwedCount, "a merge of an older copy than was announced settles nothing (G7)");
        c.True(sync.TakeWrites() == null, "and writes nothing");
        sync.Absorbed(key, 29);
        c.Eq(0, sync.OwedCount, "a merge at least as new as the announcement settles it");
        c.SeqEq(new[] { key }, (sync.TakeWrites() ?? new())[0].Settled, "and removes it from the record");
        c.True(sync.TakeCaughtUp(), "the first time the list empties is reported");
        c.False(sync.TakeCaughtUp(), "once");

        // A debt owed by a span still arriving leaves no row behind when it settles.
        LodAssistSync open = Established(40, Array.Empty<(long, long)>(), holds.Contains);
        open.OnSpan(40, 45, new[] { key }, new long[] { 44 }, last: false, holds.Contains);
        open.Absorbed(key, 44);
        open.OnSpan(40, 45, Array.Empty<long>(), Array.Empty<long>(), last: true, holds.Contains);
        LodAssistSyncWrite commit = (open.TakeWrites() ?? new())[0];
        c.Eq((0, 0, (long?)45), (commit.Owed.Count, commit.Settled.Count, commit.Version),
            "a debt settled before its span completes is neither written nor deleted");

        // NotHeld: the client's copy is gone, so there is nothing stale left to correct.
        LodAssistSync gone = Established(50, new[] { (key, 49L) }, holds.Contains);
        gone.NotHeld(key);
        c.Eq(0, gone.OwedCount, "a section no longer held is settled");
    }

    static void TrackerFetchesNearestFirstInsideTheRadius(Check c)
    {
        var owed = new List<(long Key, long Version)>();
        for (int i = 0; i < 40; i++) owed.Add((K(0, 40 - i, 0), 5));
        var holds = new HashSet<long>(owed.Select(o => o.Key));
        LodAssistSync sync = Established(10, owed, holds.Contains);

        var queued = new List<long>();
        sync.Pump(1_000, 0, 0, serveRadiusBlocks: 0, (k, _) => { queued.Add(k); return true; });
        c.Eq(LodAssistSync.MaxFillsActive, queued.Count, "at most the active cap is handed over at once");
        c.SeqEq(Enumerable.Range(1, LodAssistSync.MaxFillsActive).Select(x => K(0, x, 0)).ToArray(), queued.ToArray(),
            "the nearest owed sections, nearest first");

        queued.Clear();
        sync.Pump(1_100, 0, 0, 0, (k, _) => { queued.Add(k); return true; });
        c.Eq(0, queued.Count, "nothing more while the cap is full");

        // One finishes, one is refused.
        sync.Absorbed(K(0, 1, 0), 5);
        sync.Failed(K(0, 2, 0), 1_600);
        sync.Pump(1_600, 0, 0, 0, (k, _) => { queued.Add(k); return true; });
        c.SeqEq(new[] { K(0, 33, 0), K(0, 34, 0) }, queued.ToArray(),
            "freed slots go to the next nearest, never to a section in its refusal cooldown");

        queued.Clear();
        sync.Failed(K(0, 33, 0), 2_200);
        sync.Failed(K(0, 34, 0), 2_200);
        sync.Pump(1_600 + LodAssistSync.FailureCooldownMs, 0, 0, 0, (k, _) => { queued.Add(k); return true; });
        c.True(queued.Contains(K(0, 2, 0)), "a refused section is asked for again after its cooldown");

        // The serving radius, nearest-edge: a 64-block section at sx 2 starts 128 blocks away.
        LodAssistSync ranged = Established(10, new[] { (K(0, 2, 0), 5L), (K(0, 30, 0), 5L) },
            _ => true);
        queued.Clear();
        ranged.Pump(1_000, 0, 0, serveRadiusBlocks: 1000, (k, _) => { queued.Add(k); return true; });
        c.SeqEq(new[] { K(0, 2, 0) }, queued.ToArray(), "a section beyond the server's radius waits");
        queued.Clear();
        ranged.Pump(2_000, 30 * 64, 0, serveRadiusBlocks: 1000, (k, _) => { queued.Add(k); return true; });
        c.SeqEq(new[] { K(0, 30, 0) }, queued.ToArray(), "and is fetched once the player comes within it");

        // Refused often enough, a section waits for the next session instead.
        LodAssistSync stubborn = Established(10, new[] { (K(0, 3, 0), 5L) }, _ => true);
        long now = 1_000;
        for (int i = 0; i < LodAssistSync.MaxFailuresPerSession; i++)
        {
            stubborn.Pump(now, 0, 0, 0, (_, _) => true);
            stubborn.Failed(K(0, 3, 0), now);
            now += LodAssistSync.FailureCooldownMs * 8;
        }
        queued.Clear();
        stubborn.Pump(now, 0, 0, 0, (k, _) => { queued.Add(k); return true; });
        c.Eq((0, 1), (queued.Count, stubborn.OwedCount),
            "a section refused too often waits for the next session, still owed");

        // The pipeline says the client no longer holds it.
        LodAssistSync lost = Established(10, new[] { (K(0, 4, 0), 5L) }, _ => true);
        lost.Pump(1_000, 0, 0, 0, (_, _) => false);
        c.Eq(0, lost.OwedCount, "a section the client no longer holds is settled when handed over");
    }

    static void TrackerStopsRecordingAfterAGap(Check c)
    {
        long key = K(0, 6, 6);
        var holds = new HashSet<long> { key };
        LodAssistSync sync = Established(20, Array.Empty<(long, long)>(), holds.Contains);

        LodAssistSpanSummary? gap = sync.OnSpan(22, 25, new[] { key }, new long[] { 24 }, true, holds.Contains);
        c.True(gap is { Recorded: false, Owed: 1 }, "a span that does not follow on is not recorded, but its debts are kept");
        LodAssistSyncWrite write = (sync.TakeWrites() ?? new())[0];
        c.Eq(((long?)null, 1), (write.Version, write.Owed.Count), "its debts are written without a version");
        sync.OnSpan(25, 30, Array.Empty<long>(), Array.Empty<long>(), true, holds.Contains);
        c.Eq(20L, sync.RecordedVersion, "and no later span this session moves the record past the gap");

        // A welcome naming another generation with a span that does not restart from zero.
        var mismatched = new LodAssistSync();
        mismatched.Load(new LodAssistSyncState("old", 20, new()));
        mismatched.Begin(participate: true);
        mismatched.OnWelcome("new");
        LodAssistSpanSummary? odd = mismatched.OnSpan(20, 21, Array.Empty<long>(), Array.Empty<long>(), true, holds.Contains);
        c.True(odd is { Recorded: false }, "a continuing span from another generation is never recorded");
    }

    static void TrackerOutsideItsRole(Check c)
    {
        var sync = new LodAssistSync();
        sync.Load(new LodAssistSyncState("g", 5, new() { (K(0, 1, 1), 5) }));
        c.True(sync.Begin(participate: false) == null,
            "integrated play does not sync by version: the sibling cache already says what changed");
        sync.OnWelcome("g");
        c.True(sync.OnSpan(5, 6, new[] { K(0, 1, 1) }, new long[] { 6 }, true, _ => true) == null,
            "and a span is not taken");
        c.True(sync.Describe().Contains("not used"), "the status says why");

        var legacyServer = new LodAssistSync();
        legacyServer.Begin(participate: true);
        legacyServer.OnWelcome("");
        c.False(legacyServer.Active, "a server without versions leaves the tracker idle");

        // Owed sections the server no longer lists at all.
        long listed = K(0, 1, 0), vanished = K(0, 2, 0);
        LodAssistSync pruned = Established(10, new[] { (listed, 5L), (vanished, 5L) }, _ => true);
        c.Eq(1, pruned.ForgetUnlisted(k => k == listed), "a debt the server no longer lists is dropped");
        c.SeqEq(new[] { vanished }, (pruned.TakeWrites() ?? new())[0].Settled, "and removed from the record");
    }

    /// <summary>The pipeline side: a server-sync fill reports how it ends, once its merge is durable.</summary>
    static void NetworkRefreshReportsHowItEnds(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);
        long held = K(0, 4, 4), missing = K(0, 5, 5);
        remote.AddLocalKey(held);
        world.InstallLoaded(held, new LodSection());

        c.False(remote.QueueNetworkRefresh(missing, 3), "a section this client does not hold is not a refresh");
        c.True(remote.RemoteOnly.Contains(missing), "it is offered instead");

        c.True(remote.QueueNetworkRefresh(held, 7), "a held section is queued");
        c.Eq(LodMergeMode.NewerWins, remote.MergeModeFor(held), "as a newer-wins merge");
        remote.PumpFills(16);
        c.SeqEq(new[] { held }, remote.FillWanted(), "and wanted at once: its target is in RAM");
        remote.MarkRequested(new[] { held });

        // A newer announcement arrives while the older copy is on its way.
        remote.QueueNetworkRefresh(held, 9);
        remote.NoteNetworkMerged(held, 7);
        c.Eq(1, remote.PendingFills, "merging the older copy leaves the fill waiting for the newer one");
        c.SeqEq(new[] { new LodNetworkRefreshOutcome(held, LodNetworkRefreshEnd.Durable, 7) },
            remote.TakeNetworkRefreshOutcomes() ?? new(), "the older merge is still reported, with its version");

        world.MarkChanged(held);
        remote.NoteNetworkMerged(held, 9);
        c.Eq(0, remote.PendingFills, "the newer copy finishes the fill");
        c.True(remote.TakeNetworkRefreshOutcomes() == null, "but a merge not yet saved is not reported (G16)");
        remote.MarkPersisted(held, world.PersistenceRevision(held));
        c.SeqEq(new[] { new LodNetworkRefreshOutcome(held, LodNetworkRefreshEnd.Durable, 9) },
            remote.TakeNetworkRefreshOutcomes() ?? new(), "the save that contains it releases the report");
        c.True(remote.TakeReadyMergeRecords() == null, "a server version never becomes a sibling merge record");

        remote.QueueNetworkRefresh(held, 11);
        remote.MarkUnavailable(held);
        c.SeqEq(new[] { new LodNetworkRefreshOutcome(held, LodNetworkRefreshEnd.Failed, 0) },
            remote.TakeNetworkRefreshOutcomes() ?? new(), "a refusal is reported as a failure");
        c.True(world.Sections.ContainsKey(held), "and leaves the client's own copy alone");

        long unreadable = K(0, 6, 6);
        remote.AddLocalKey(unreadable);
        world.InstallStoredKey(0, 6, 6, applyToParent: false);
        remote.QueueNetworkRefresh(unreadable, 2);
        world.LoadFailed.Add(unreadable);
        remote.PumpFills(16);
        c.SeqEq(new[] { new LodNetworkRefreshOutcome(unreadable, LodNetworkRefreshEnd.NotHeld, 0) },
            remote.TakeNetworkRefreshOutcomes() ?? new(),
            "a section whose own copy cannot be read is no longer held: nothing stale is left");
    }

    static void ClientRoutesVersionedSpans(Check c)
    {
        var client = new LodAssistClient(null!, new CaptureLogger(), "0.4.126");
        client.OnWelcome(new AssistWelcome { Protocol = LodAssist.Protocol, Enabled = true, CacheGeneration = "g" });
        c.Eq("g", client.ServerCacheGeneration, "the welcome's generation is kept");

        long listed = K(0, 1, 1), legacy = K(0, 2, 2);
        client.OnKeyManifest(new AssistKeyManifest { Keys = new[] { listed }, Last = true });
        client.OnKeyManifest(new AssistKeyManifest
        {
            Keys = Array.Empty<long>(), Last = true, Refresh = true, VersionFrom = 5, VersionTo = 5,
        });
        client.OnKeyManifest(new AssistKeyManifest
        {
            Keys = new[] { listed }, Versions = new long[] { 8 }, Last = true, Refresh = true,
            VersionFrom = 5, VersionTo = 8,
        });
        client.OnKeyManifest(new AssistKeyManifest { Keys = new[] { legacy }, Last = true, Refresh = true });

        var spans = new List<LodAssistSpanChunk>();
        var refreshed = new List<long>();
        for (int i = 0; i < 4; i++)
            client.Pump((_, _) => true, null, null, keys => refreshed.AddRange(keys), spans.Add);
        c.Eq(2, spans.Count, "versioned spans reach their own callback, the empty one included");
        c.True(spans.Count == 2 && spans[0].From == 5 && spans[0].To == 5 && spans[0].Keys.Length == 0 && spans[0].Last,
            "an empty span still arrives, so the client's record can move on");
        c.True(spans.Count == 2 && spans[1].Versions.SequenceEqual(new long[] { 8 }), "with each key's version");
        c.SeqEq(new[] { legacy }, refreshed, "an unversioned refresh still goes where it always went");

        long seenVersion = -1;
        client.OnSection(new AssistSection { Key = listed, Blob = new byte[] { 4, 1 }, Version = 8 });
        client.PumpAsync((_, _, version) => { seenVersion = version; return true; });
        c.Eq(8L, seenVersion, "an arrival's version reaches the versioned installer");
        client.CompleteInstall(listed, installed: true);

        client.OnSection(new AssistSection { Key = listed });
        client.Pump((_, _) => false);
        c.False(client.RemoteKeys.Contains(listed), "a refusal removes the offer");
        c.True(client.ServerListed(listed), "but the server still listed the key");
        client.ReviveForRefresh(listed);
        c.SeqEq(new[] { listed }, client.SelectRequestBatch(new[] { listed }),
            "a server-sync merge can ask for a key refused earlier in the session");

        client.Reset();
        c.Eq("", client.ServerCacheGeneration, "a new world forgets the generation");
    }

    /// <summary>
    /// Whole sessions against real cache files: a server cache, a client record on disk, the
    /// reader answering span queries, the ledger choosing where each join starts, and the tracker.
    /// The plan's acceptance tests 8, 9 and 10, without a game.
    /// </summary>
    static void ARejoinReceivesExactlyWhatChanged(Check c)
    {
        string dir = TempDir("vh-sync-rejoin");
        string server = Path.Combine(dir, "server.db");
        string client = Path.Combine(dir, "client.db");
        long a = K(0, 1, 1), b = K(0, 2, 2), elsewhere = K(0, 3, 3);
        var holds = new HashSet<long> { a, b };
        try
        {
            WithStore(server, versioned: true, store =>
            {
                store.SaveBlob(0, 1, 1, new byte[] { 4, 1 }, applyToParent: false);
                store.SaveBlob(0, 2, 2, new byte[] { 4, 2 }, applyToParent: false);
                store.SaveBlob(0, 3, 3, new byte[] { 4, 3 }, applyToParent: false);
                store.SaveBlob(1, 0, 0, new byte[] { 4, 4 }, applyToParent: false);
            });

            SessionOutcome first = Session(server, client, holds, absorb: true);
            c.True(first.Join is { FromZero: true, Owed: 2 },
                "a first join re-checks every section the client holds, and only those");
            c.SeqEq(new[] { a, b }.OrderBy(k => k).ToArray(), first.Queued, "fetching them");
            c.Eq(0, first.Record.Owed.Count, "merged and durable, nothing stays owed");

            SessionOutcome idle = Session(server, client, holds, absorb: true);
            c.True(idle.Join is { FromZero: false, Owed: 0, Announced: 0 },
                "test 9: a rejoin with nothing changed is told of nothing");
            c.Eq(0, idle.Queued.Length, "and fetches nothing");

            WithStore(server, versioned: true, store =>
            {
                store.SaveBlob(0, 2, 2, new byte[] { 4, 5 }, applyToParent: false); // held
                store.SaveBlob(0, 3, 3, new byte[] { 4, 6 }, applyToParent: false); // not held
                store.SaveBlob(1, 0, 0, new byte[] { 4, 7 }, applyToParent: false); // a parent
            });

            SessionOutcome away = Session(server, client, holds, absorb: false);
            c.True(away.Join is { FromZero: false, Announced: 2, Owed: 1 },
                "test 8: a rejoin is told of exactly the level-0 sections changed while away");
            c.SeqEq(new[] { b }, away.Queued, "and fetches exactly the one it holds");
            c.SeqEq(new[] { b }, away.Record.Owed.Select(o => o.Key).ToArray(),
                "quitting before the merge keeps it owed on disk");

            SessionOutcome resumed = Session(server, client, holds, absorb: true);
            c.True(resumed.Join is { FromZero: false, Announced: 0 }, "the next join has nothing new");
            c.SeqEq(new[] { b }, resumed.Queued, "but resumes the section still owed");
            c.Eq(0, resumed.Record.Owed.Count, "and settles it");

            // The server's cache is deleted and rebuilt: a new file, a new generation.
            string rebuilt = Path.Combine(dir, "server-rebuilt.db");
            WithStore(rebuilt, versioned: true, store =>
            {
                store.SaveBlob(0, 1, 1, new byte[] { 4, 8 }, applyToParent: false);
                store.SaveBlob(0, 3, 3, new byte[] { 4, 9 }, applyToParent: false);
            });
            SessionOutcome fresh = Session(rebuilt, client, holds, absorb: true);
            c.True(fresh.Join is { FromZero: true, Owed: 1 },
                "test 10: a rebuilt cache is a new generation, so the client re-checks from zero");
            c.SeqEq(new[] { a }, fresh.Queued, "every section it holds that the new cache has");
            c.True(fresh.Record.Generation.Length > 0 && fresh.Record.Generation != first.Record.Generation,
                "and records the new generation");

        }
        finally
        {
            DeleteDir(dir);
        }
    }

    readonly record struct SessionOutcome(LodAssistSpanSummary? Join, long[] Queued, LodAssistSyncState Record);

    /// <summary>
    /// One join: the client's record is read from its cache, the server answers from its own, every
    /// owed section is handed over, optionally merged, and the record is written back and re-read.
    /// </summary>
    static SessionOutcome Session(string serverPath, string clientPath, ISet<long> holds, bool absorb)
    {
        string generation = "";
        long committed = 0;
        WithStore(serverPath, versioned: true, store =>
        {
            generation = store.SectionGeneration;
            committed = store.SectionVersion;
        });

        LodAssistSyncState record = new("", NoRecord, new());
        WithStore(clientPath, versioned: false, store => record = store.LoadAssistSync());

        var sync = new LodAssistSync();
        sync.Load(record);
        LodAssistSyncRequest request = sync.Begin(participate: true)!.Value;
        sync.OnWelcome(generation);

        var ledger = new LodSectionSyncLedger();
        ledger.EnsureBaseline(committed);
        long from = ledger.Join("p", 1, request.Generation, request.Version, generation);
        LodSectionSpanResult span;
        using (var reader = new LodAssistBlobReader(serverPath, new CaptureLogger(), trackAllocations: false))
        {
            reader.EnqueueSpan(new LodSectionSpanQuery("p", 1, from));
            span = WaitForSpan(reader);
        }
        ledger.Joined("p", 1, span.To);
        LodAssistSpanSummary? join = sync.OnSpan(span.From, span.To, span.Keys, span.Versions, true, holds.Contains);

        var queued = new List<(long Key, long Version)>();
        sync.Pump(1_000, 0, 0, 0, (key, version) => { queued.Add((key, version)); return true; });
        if (absorb)
        {
            foreach ((long key, long version) in queued) sync.Absorbed(key, version);
        }

        List<LodAssistSyncWrite>? writes = sync.TakeWrites();
        WithStore(clientPath, versioned: false, store =>
        {
            foreach (LodAssistSyncWrite write in writes ?? new()) store.SaveAssistSync(write);
            record = store.LoadAssistSync();
        });
        return new SessionOutcome(join, queued.Select(q => q.Key).OrderBy(k => k).ToArray(), record);
    }

    static LodSectionSpanResult WaitForSpan(LodAssistBlobReader reader)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 5000)
        {
            if (reader.TryTakeSpanResult(out LodSectionSpanResult result)) return result;
            Thread.Sleep(2);
        }
        throw new TimeoutException("the assist reader did not answer a span query");
    }

    static LodAssistBlobReadResult WaitForBlob(LodAssistBlobReader reader)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 5000)
        {
            if (reader.TryTakeResult(out LodAssistBlobReadResult result)) return result;
            Thread.Sleep(2);
        }
        throw new TimeoutException("the assist reader did not answer a blob read");
    }

    static string TempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static void DeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
    }

    static void WithStore(string path, bool versioned, Action<LodStore> use)
    {
        using var store = new LodStore(new CaptureLogger(), versioned);
        if (!store.Open(path)) throw new InvalidOperationException("fixture cache did not open: " + path);
        use(store);
    }

    /// <summary>An edit behind the store's back, unpooled so no handle outlives it (G13).</summary>
    static void ExecuteRaw(string path, string sql)
    {
        using SqliteConnection connection = OpenRaw(path);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static Dictionary<long, long> ReadVersions(string path)
    {
        using SqliteConnection connection = OpenRaw(path);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Detail, SX, SZ, Version FROM Section";
        var versions = new Dictionary<long, long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            versions[K(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2))] = reader.GetInt64(3);
        return versions;
    }

    static bool HasVersionColumn(string path)
    {
        using SqliteConnection connection = OpenRaw(path);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Section') WHERE name='Version'";
        return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    static SqliteConnection OpenRaw(string path)
    {
        var options = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
        var connection = new SqliteConnection(options.ToString());
        connection.Open();
        return connection;
    }
}
