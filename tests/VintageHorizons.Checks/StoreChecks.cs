using VintageHorizons.Net;
using System.IO.Compression;

namespace VintageHorizons.Checks;

/// <summary>
/// The on-disk blob format, round-tripped with no database.
///
/// LodStore extends the game's SQLite base class, but Serialize is static and the base
/// constructor only stores its logger - so the format can be exercised without opening a
/// file, and DeserializeForeign explicitly accepts a null world to defer block-id lookup
/// to the main thread. That same door is what the network path uses for foreign sections.
/// </summary>
public static class StoreChecks
{
    public static void Run(Check c)
    {
        RoundTrip(c);
        LegacySnowProvenanceTail(c);
        DeferredPalette(c);
        AFailedLookupIsNotRemembered(c);
        AColourlessCacheIsRepaired(c);
        AStoredColourIsRefreshedFromTheLiveBlock(c);
        DisposingTheOfferReaderReleasesItsFileHandle(c);
        AStoreRewriteGetsANewRowid(c);
        SiblingMergeRecordsFollowTheirSections(c);
        LocalOfferMissHookRetriesTheSameBlob(c);
        AssistBlobReaderIsBoundedOrderedAndReadOnly(c);
        BackgroundForeignDecodeIsBoundedAndIsolated(c);
        InterruptionMarkerFollowsDurableMipWrite(c);
        Rejection(c);
        PurgeKeepsMatchingData(c);
        ColumnFingerprintsSurviveAReopen(c);
    }

    /// <summary>
    /// The fingerprint table is a new table rather than a schema bump, so an existing cache
    /// gains it empty and keeps everything it already held. A missing entry means "assume
    /// changed", which is today's behaviour, so there is no migration and nothing to undo.
    /// </summary>
    static void ColumnFingerprintsSurviveAReopen(Check c)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-fingerprint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "cache.db");
            var logger = new CaptureLogger();
            var store = new LodStore(logger);
            c.True(store.Open(path), "a cache with the fingerprint table opens");
            c.Eq(0, store.LoadColumnFingerprints().Count,
                "a new cache has no fingerprints, so every column reads as changed");

            store.SaveColumnFingerprint(100, 200, 0xDEADBEEFCAFEF00DUL, 12);
            store.SaveColumnFingerprint(-3, -4, 1, 1);
            store.Dispose();

            var reopenLogger = new CaptureLogger();
            var reopened = new LodStore(reopenLogger);
            c.True(reopened.Open(path), "the cache reopens");
            Dictionary<long, ulong> found = reopened.LoadColumnFingerprints();
            c.Eq(2, found.Count, "both fingerprints survived the reopen");
            // A fingerprint uses the whole 64-bit range, and SQLite integers are signed, so
            // the value has to survive the round trip through a negative stored number.
            c.Eq(0xDEADBEEFCAFEF00DUL, found[LodColumnMap.Key(100, 200)],
                "a fingerprint above long.MaxValue round-trips unchanged");
            c.Eq(1UL, found[LodColumnMap.Key(-3, -4)],
                "negative column coordinates key correctly");

            reopened.ForgetColumnFingerprint(100, 200);
            c.Eq(1, reopened.LoadColumnFingerprints().Count,
                "forgetting a column removes exactly its own fingerprint");
            c.False(reopened.LoadColumnFingerprints().ContainsKey(LodColumnMap.Key(100, 200)),
                "the forgotten column is the one that went");
            reopened.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* a temp directory */ }
        }
    }

    /// <summary>
    /// Reopening a cache at the same format version must keep every row.
    ///
    /// This is the one place the mod deletes data a player accumulated over weeks, so it
    /// gets a check that opens a real file rather than reasoning about the SQL. The
    /// nearby bug was the reverse of this: a brand-new cache announced that it was
    /// discarding data it never had, because a missing FormatVersion row compares
    /// unequal to the current version.
    /// </summary>
    static void PurgeKeepsMatchingData(Check c)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-purge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cache.db");
        try
        {
            var logger = new CaptureLogger();
            var store = new LodStore(logger);
            c.True(store.Open(path), "a new cache file opens");

            // A first-ever cache holds nothing, so it must not claim to discard anything.
            c.False(logger.Contains("discarding"),
                "a brand new cache does not announce that it is discarding data");

            store.SaveBlob(0, 1, 1, new byte[] { 1, 2, 3, 4 }, applyToParent: false);
            store.SaveBlob(0, 2, 2, new byte[] { 5, 6, 7, 8 }, applyToParent: true);
            store.Dispose();

            // Reopen at the SAME version. Every row survives.
            var reopenLogger = new CaptureLogger();
            var reopened = new LodStore(reopenLogger);
            c.True(reopened.Open(path), "an existing cache reopens");
            int kept = reopened.LoadAllKeys((_, _, _, _) => { });
            c.Eq(2, kept, "reopening at the same format version keeps every section");
            c.False(reopenLogger.Contains("discarding"),
                "reopening at the same version does not discard anything");
            reopened.Dispose();

            // Now make the stored version disagree. The purge must fire, and say how
            // much it took - silent destruction of a player's cache is the thing to
            // avoid, not the destruction itself.
            // Pooling is deliberately off. Dispose on a pooled connection only returns
            // its native handle to the process-wide pool; on Windows that keeps this
            // temporary file open and makes LodStore's writable reopen fail. The fixture
            // is testing schema invalidation, not connection-pool lifetime.
            var editOptions = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            };
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(editOptions.ToString()))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE Meta SET Value='1' WHERE Key='FormatVersion'";
                cmd.ExecuteNonQuery();
            }

            var staleLogger = new CaptureLogger();
            var stale = new LodStore(staleLogger);
            c.True(stale.Open(path), "a cache in an older format still opens");
            c.Eq(0, stale.LoadAllKeys((_, _, _, _) => { }),
                "an older format is discarded, not read");
            c.True(staleLogger.Contains("discarding"), "the purge says that it discarded data");
            c.True(staleLogger.Contains("2"), "the purge reports how many sections it took");
            stale.Dispose();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
        }
    }

    static void RoundTrip(Check c)
    {
        var section = new LodSection();
        int stone = section.FindOrAddPaletteEntry(blockId: 11, color: 0x00445566, flags: 0);
        int water = section.FindOrAddPaletteEntry(blockId: 22, color: 0x00112233,
            flags: LodPaletteEntry.FlagWater, tintSlot: 9);

        section.SetColumn(0, new[] { LodSection.PackRun(stone, 30, 0) });
        section.SetColumn(1, new[] { LodSection.PackRun(water, 40, 30), LodSection.PackRun(stone, 30, 0) });
        section.SetColumn(Fixtures.Total - 1, new[] { LodSection.PackRun(stone, 12, 0) });
        section.SetSnowStateObserved(0, observed: true);
        section.SetSnowStateObserved(Fixtures.Total - 1, observed: true);
        section.SetSnowCovered(0, covered: true);

        LodSection back = Restore(c, section);
        if (back == null!) return;

        c.SeqEq(section.Runs, back.Runs, "runs survive the round trip");
        c.SeqEq(section.ColumnStart, back.ColumnStart, "column offsets survive the round trip");
        c.SeqEq(section.Captured, back.Captured, "the captured bitmask survives the round trip");
        c.SeqEq(section.SnowStateObservedBits, back.SnowStateObservedBits,
            "the observed-snow-state bitmask survives the round trip");
        c.SeqEq(section.SnowCoveredBits, back.SnowCoveredBits,
            "the observed snow-coverage bitmask survives the round trip");
        c.Eq(section.CapturedColumns, back.CapturedColumns, "the captured count is rebuilt");
        c.Eq(section.Palette.Count, back.Palette.Count, "the palette keeps its length");

        for (int i = 0; i < section.Palette.Count; i++)
        {
            c.Eq(section.Palette[i].Color, back.Palette[i].Color, $"palette[{i}] colour survives");
            c.Eq(section.Palette[i].Flags, back.Palette[i].Flags, $"palette[{i}] flags survive");
        }

        // The last column is the one an off-by-one in the 4096 run counts or the 512-byte
        // captured bitmask would lose, and losing it is invisible until terrain has a seam.
        c.True(back.Captured[Fixtures.Total - 1], "the final column survives the bitmask");
        c.SeqEq(section.ColumnRuns(Fixtures.Total - 1).ToArray(),
            back.ColumnRuns(Fixtures.Total - 1).ToArray(), "the final column's runs survive");
    }

    /// <summary>
    /// The provenance bitset is an optional tail inside format 4, so months of existing
    /// terrain remain readable. Missing means unknown, which safely retains climate snow
    /// until a real chunk recapture upgrades the column.
    /// </summary>
    static void LegacySnowProvenanceTail(Check c)
    {
        LodSection section = Fixtures.SolidSection();
        section.SetSnowStateObserved(0, observed: true);
        byte[] current = LodStore.Serialize(Fixtures.Snapshot(section));

        byte[] payload;
        using (var input = new MemoryStream(current, 1, current.Length - 1))
        using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            deflate.CopyTo(output);
            payload = output.ToArray();
        }
        payload = payload[..^LodSection.SnowStateObservedByteCount];

        byte[] legacy;
        using (var output = new MemoryStream())
        {
            output.WriteByte(current[0]);
            using (var deflate = new DeflateStream(
                output, CompressionLevel.Fastest, leaveOpen: true))
                deflate.Write(payload);
            legacy = output.ToArray();
        }

        LodSection? back = new LodStore(null!).DeserializeForeign(legacy, null);
        c.True(back != null, "a format-4 blob without the provenance tail still loads");
        c.True(back != null && back.CapturedColumns == Fixtures.Total,
            "legacy terrain keeps all captured columns");
        c.True(back != null && back.SnowStateObservedBits.All(word => word == 0),
            "legacy terrain safely treats snow state as unknown");
    }

    /// <summary>
    /// Two fields deliberately do NOT round-trip, and asserting full equality would lock in
    /// the wrong thing:
    ///   - TintSlot is never written, so an existing cache picks up corrected per-species
    ///     tints without re-capturing, and stays right when a game update remaps them.
    ///   - BlockId cannot be resolved off the main thread, so a null world defers the codes
    ///     into PendingPaletteCodes for the main thread to resolve on install.
    /// </summary>
    static void DeferredPalette(Check c)
    {
        var section = new LodSection();
        section.FindOrAddPaletteEntry(blockId: 11, color: 0x00445566, flags: 0, tintSlot: 5);
        section.SetColumn(0, new[] { LodSection.PackRun(0, 10, 0) });

        string[] codes = { "game:rock-granite" };
        LodSection back = Restore(c, section, codes);
        if (back == null!) return;

        c.True(back.PendingPaletteCodes != null, "a null world defers palette codes for the main thread");
        c.SeqEq(codes, back.PendingPaletteCodes!, "the deferred codes are the ones written");
        c.Eq(0, back.Palette[0].BlockId, "block ids stay unresolved until the main thread runs");
        c.Eq((byte)0, back.Palette[0].TintSlot, "tint slots are re-derived, never persisted");
        c.Eq(0x00445566, back.Palette[0].Color, "colour is persisted and comes back");
    }

    /// <summary>
    /// A block code that does not resolve must not become a permanent answer.
    ///
    /// The lookup runs on the storage thread against whatever the registry holds at that
    /// moment, and sections start loading from cache before a world has finished coming
    /// up. Caching the failure alongside the successes meant a code that lost that race
    /// answered 0 for the rest of the session, and an entry with no block id keeps the
    /// flags the capturing side worked out - so it was still drawn, as terrain, with the
    /// colour a server leaves at zero. Black ground, correctly shaped, and nothing logged.
    /// </summary>
    static void AFailedLookupIsNotRemembered(Check c)
    {
        var section = new LodSection();
        section.FindOrAddPaletteEntry(blockId: 0, color: 0, flags: 0, tintSlot: 0);
        section.SetColumn(0, new[] { LodSection.PackRun(0, 10, 0) });
        section.PendingPaletteCodes = new[] { "game:rock-granite" };

        var store = new LodStore(null!);

        // The registry is not ready yet, which is the race.
        int calls = 0;
        store.ResolvePendingPalette(section, _ => { calls++; return 0; });
        c.Eq(1, calls, "the first resolve asks the registry");
        c.Eq(0, section.Palette[0].BlockId, "and gets nothing, because nothing is registered yet");
        c.SeqEq(new[] { "game:rock-granite" }, store.UnresolvedCodes(),
            "the code that did not resolve is recorded, so it can be named rather than guessed at");

        // Same code, same store, registry now up. Asking again is the whole point.
        section.PendingPaletteCodes = new[] { "game:rock-granite" };
        store.ResolvePendingPalette(section, _ => { calls++; return 42; });
        c.Eq(2, calls, "a code that failed is asked again rather than answered from cache");
        c.Eq(42, section.Palette[0].BlockId, "and resolves once the registry has it");
        c.Eq(0, store.UnresolvedCodes().Length, "and stops being reported as unresolved");

        // A code that DID resolve is cached, because that is the hot path.
        section.PendingPaletteCodes = new[] { "game:rock-granite" };
        store.ResolvePendingPalette(section, _ => { calls++; return 99; });
        c.Eq(2, calls, "a code that resolved is answered from cache and not looked up again");
        c.Eq(42, section.Palette[0].BlockId, "keeping the id it first resolved to");
    }

    /// <summary>
    /// A colour captured before it was section-independent must be corrected on load.
    ///
    /// Block.GetColorWithoutTint is not a function of the block: grass-covered ground
    /// answers it with a RANDOM pixel of the grass texture, and a palette entry is
    /// registered once per section. Every 64-block section therefore drew its whole
    /// surface with a different random pixel - measured at 38 stored colours for
    /// soil-low-normal alone across 1,041 cached sections - which reads in game as flat
    /// green and flat brown tiles meeting at a hard edge. Fixing capture reaches no cache
    /// that already exists, and those caches are worth weeks of exploration, so the live
    /// block is the authority on load for colour exactly as it already is for flags.
    ///
    /// Zero means "keep what is stored", and that is not a spare value: it is how a block
    /// whose colour genuinely depends on where it stands - a chiselled block averaging the
    /// materials in its block entity - keeps the colour capture worked out for it, which
    /// nothing on the load path has the position to recompute.
    /// </summary>
    static void AStoredColourIsRefreshedFromTheLiveBlock(Check c)
    {
        var section = new LodSection();
        section.FindOrAddPaletteEntry(blockId: 7, color: 0x00112233, flags: 0, tintSlot: 0);
        section.FindOrAddPaletteEntry(blockId: 8, color: 0x00445566, flags: 0, tintSlot: 0);
        section.PendingPaletteCodes = new[] { "game:soil-low-normal", "game:microblock" };

        var store = new LodStore(null!);
        store.ClassifyBlock = blockId => blockId == 7
            ? ((byte)LodPaletteEntry.FlagWater, (byte)3, 0x00AABBCC, (ushort)11)  // ordinary block
            : ((byte)0, (byte)5, 0, (ushort)0);                                   // colour is positional

        store.ResolvePendingPalette(section, code => code.EndsWith("soil-low-normal") ? 7 : 8);

        c.Eq(0x00AABBCC, section.Palette[0].Color,
            "an ordinary block takes the live colour, so every section agrees on it");
        c.Eq((byte)LodPaletteEntry.FlagWater, section.Palette[0].Flags, "flags still refresh");
        c.Eq((byte)3, section.Palette[0].TintSlot, "and so does the tint slot");

        c.Eq(0x00445566, section.Palette[1].Color,
            "a block whose colour is positional keeps what capture stored for it");
        c.Eq((byte)5, section.Palette[1].TintSlot,
            "while still re-deriving everything that is not the colour");
    }

    /// <summary>
    /// A cache holding sections with no palette colour must repair itself as it loads.
    ///
    /// A capturing server stores 0 for every colour, because it has no texture atlas, and
    /// the receiving client fills them in. A client also PERSISTS what it received, so
    /// anything that stopped the fill-in was written to disk and stayed there. Measured on
    /// a real world afterwards: 7 sections entirely uncoloured and 59 partly, on ground as
    /// ordinary as soil-low-normal and rock-slate. Colour 0 draws as pure black, and no
    /// amount of fixing the cause reaches a cache that already has it.
    /// </summary>
    static void AColourlessCacheIsRepaired(Check c)
    {
        c.True(LodPaletteRepair.NeedsColor(0), "colour 0 is what a writer leaves when it cannot answer");
        c.False(LodPaletteRepair.NeedsColor(unchecked((int)0xFF000000)),
            "opaque black is a real colour and is left alone");
        c.False(LodPaletteRepair.NeedsColor(1), "so is anything else");

        var section = new LodSection();
        section.FindOrAddPaletteEntry(blockId: 11, color: 0, flags: 0, tintSlot: 0);
        section.FindOrAddPaletteEntry(blockId: 12, color: unchecked((int)0xFF336699), flags: 0, tintSlot: 0);
        section.FindOrAddPaletteEntry(blockId: 0, color: 0, flags: 0, tintSlot: 0);

        int asked = 0;
        int repaired = LodPaletteRepair.Fill(section, id =>
        {
            asked++;
            return id == 11 ? unchecked((int)0xFF112233) : 0;
        });

        c.Eq(2, repaired, "both uncoloured entries are repaired");
        c.Eq(2, asked, "and the entry that already had a colour is not asked about");
        c.Eq(unchecked((int)0xFF112233), section.Palette[0].Color, "a known block takes its real colour");
        c.Eq(unchecked((int)0xFF336699), section.Palette[1].Color, "an already-coloured entry is untouched");

        // The provider answered 0 for the unknown block. Storing that would leave the
        // entry needing repair for ever, and repairing marks the section dirty, so the
        // cache would be rewritten on every single load.
        c.Eq(LodPaletteRepair.UnknownBlockColor, section.Palette[2].Color,
            "a block nothing can colour becomes grey rather than staying black");
        c.False(LodPaletteRepair.NeedsColor(section.Palette[2].Color),
            "so the repair finishes instead of running again every load");

        c.Eq(0, LodPaletteRepair.Fill(section, _ => 0), "a repaired section needs no second pass");
    }

    /// <summary>
    /// Disposing the local offer reader must release its file handle, not park it.
    ///
    /// Microsoft.Data.Sqlite pools connections by default: Dispose returns the native
    /// handle to a process-wide pool keyed by connection string, and the file stays open
    /// until the process exits. In singleplayer that handle points at the server side's
    /// cache, and it survived leaving the world - so the next load of the same world had
    /// the integrated server's LodStore.Open refused with "it seems to be not writable",
    /// every time, on the platform whose file sharing blocks it. Reported from the field
    /// against 0.2.0, the release that introduced this reader; 0.1.0 had nothing to leak.
    ///
    /// Proven through /proc/self/fd, which is why the assertion is Linux-only: the leak is
    /// a handle held by this process, and that is where a process's handles are listed.
    /// </summary>
    static void DisposingTheOfferReaderReleasesItsFileHandle(Check c)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-offer-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string clientDb = Path.Combine(dir, "world.db");
            string serverDb = Path.Combine(dir, "world-server.db");

            // A throwaway unpooled writer builds the fixture file with the store's real Section
            // schema, so INSERT OR REPLACE behaves as the store's own writes do. The only
            // connection that can linger afterwards is the one under test.
            var writer = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = serverDb,
                Pooling = false,
            };
            using (var w = new Microsoft.Data.Sqlite.SqliteConnection(writer.ToString()))
            {
                w.Open();
                using var cmd = w.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE Section (Detail INTEGER NOT NULL, SX INTEGER NOT NULL, "
                    + "SZ INTEGER NOT NULL, Data BLOB NOT NULL, ApplyToParent INTEGER NOT NULL "
                    + "DEFAULT 0, ModifiedMs INTEGER NOT NULL, PRIMARY KEY (Detail, SX, SZ));"
                    + "INSERT OR REPLACE INTO Section VALUES (0, 1, 2, x'00', 0, 0);";
                cmd.ExecuteNonQuery();
            }

            var logger = new CaptureLogger();
            LodLocalOfferSource? offers = LodLocalOfferSource.TryOpen(clientDb, logger);
            c.True(offers != null, "the server-side cache beside a client path opens");
            if (offers == null) return;

            long first = LodWorld.SectionKey(0, 1, 2);
            LodSiblingRow[] initial = WaitForRows(offers);
            c.SeqEq(new[] { first }, initial.Select(row => row.Key).ToArray(),
                "the reader publishes its initial key scan from the worker");
            long firstRowid = initial.Length > 0 ? initial[0].Rowid : -1;
            c.True(offers.RequestBlob(first),
                "a sibling-cache blob read is accepted by the I/O worker");
            LodLocalOfferSource.BlobResult asyncBlob = WaitForBlobResult(offers);
            c.Eq(first, asyncBlob.Key, "the background blob result retains its section key");
            c.SeqEq(new byte[] { 0 }, asyncBlob.Blob ?? Array.Empty<byte>(),
                "the background worker returns the stored blob without a game-thread query");
            c.Eq(firstRowid, asyncBlob.Rowid,
                "a blob read reports the rowid it was read from, for the merge record");

            // A new parent and a rewrite of the level-0 row, in one transaction as the store
            // writes a checkpoint. Both are published at their own levels, in write order,
            // the rewrite under a rowid above everything seen, which is how it is recognised.
            using (var w = new Microsoft.Data.Sqlite.SqliteConnection(writer.ToString()))
            {
                w.Open();
                using var cmd = w.CreateCommand();
                cmd.CommandText = "BEGIN;"
                    + "INSERT OR REPLACE INTO Section VALUES (1, 3, 4, x'00', 0, 1);"
                    + "INSERT OR REPLACE INTO Section VALUES (0, 1, 2, x'01', 0, 2);"
                    + "COMMIT;";
                cmd.ExecuteNonQuery();
            }

            LodSiblingRow[] changed = WaitForRows(offers);
            c.SeqEq(new[] { LodWorld.SectionKey(1, 3, 4), first },
                changed.Select(row => row.Key).ToArray(),
                "a new parent and a rewritten level-0 row are both published, in write order");
            c.True(changed.Length == 2 && changed[1].Rowid > firstRowid,
                "the rewritten row carries a new, higher rowid");

            offers.RequestDiscovery();
            Thread.Sleep(100);
            c.False(offers.TryTakeChangedRows(out _),
                "an unchanged table publishes no owning-thread work");
            c.True(offers.FullScans >= 1 && offers.IncrementalScans >= 1,
                "the first scan read every key and the later one only the new rows");
            offers.Dispose();

            if (OperatingSystem.IsLinux())
            {
                c.False(ProcessHoldsHandleTo(serverDb),
                    "Dispose releases the file handle instead of pooling it for the rest of the process");
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir; best effort */ }
        }
    }

    static void InterruptionMarkerFollowsDurableMipWrite(Check c)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-mip-interrupt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cache.db");
        string marker = Path.Combine(dir, "persisted.marker");
        string release = Path.Combine(dir, "release.marker");
        string? oldMarker = Environment.GetEnvironmentVariable("VINTAGEHORIZONS_INTERRUPT_MIP_MARKER");
        string? oldRelease = Environment.GetEnvironmentVariable("VINTAGEHORIZONS_INTERRUPT_MIP_RELEASE");

        try
        {
            Environment.SetEnvironmentVariable("VINTAGEHORIZONS_INTERRUPT_MIP_MARKER", marker);
            Environment.SetEnvironmentVariable("VINTAGEHORIZONS_INTERRUPT_MIP_RELEASE", release);

            using var store = new LodStore(new CaptureLogger());
            c.True(store.Open(path), "the interruption fixture cache opens");
            using var worker = new LodStorageThread(store);
            worker.Enqueue(Fixtures.Snapshot(Fixtures.SolidSection(),
                level: 0, sx: 7, sz: 9, applyToParent: true));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!File.Exists(marker) && clock.ElapsedMilliseconds < 5000) Thread.Sleep(10);
            c.True(File.Exists(marker),
                "the storage worker publishes its interruption marker after a flagged row write");
            c.Eq(1L, worker.Backlog,
                "the marked write stays outstanding until the harness releases it");

            File.WriteAllText(release, "release");
            worker.Drain();

            bool restoredObligation = false;
            store.LoadAllKeys((level, sx, sz, applyToParent) =>
            {
                if (level == 0 && sx == 7 && sz == 9) restoredObligation = applyToParent;
            });
            c.True(restoredObligation,
                "the row named by the interruption marker carries ApplyToParent on disk");

            File.Delete(marker);
            using var serverStore = new LodStore(new CaptureLogger());
            c.True(serverStore.Open(Path.Combine(dir, "server-cache.db")),
                "the integrated-server interruption fixture opens");
            using var serverWorker = new LodStorageThread(
                serverStore, enableMipInterruptionHook: false);
            serverWorker.Enqueue(Fixtures.Snapshot(Fixtures.SolidSection(),
                level: 0, sx: 10, sz: 11, applyToParent: true));
            serverWorker.Drain();
            c.False(File.Exists(marker),
                "an integrated-server write cannot win the client interruption marker race");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VINTAGEHORIZONS_INTERRUPT_MIP_MARKER", oldMarker);
            Environment.SetEnvironmentVariable("VINTAGEHORIZONS_INTERRUPT_MIP_RELEASE", oldRelease);
            try { File.WriteAllText(release, "cleanup"); } catch { }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    static void LocalOfferMissHookRetriesTheSameBlob(Check c)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-offer-miss-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string clientDb = Path.Combine(dir, "world.db");
        string serverDb = Path.Combine(dir, "world-server.db");
        string marker = Path.Combine(dir, "miss.marker");
        string? oldMarker = Environment.GetEnvironmentVariable(
            "VINTAGEHORIZONS_TEST_LOCAL_OFFER_MISS_MARKER");

        try
        {
            var writer = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = serverDb,
                Pooling = false,
            };
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(writer.ToString()))
            {
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText =
                    "CREATE TABLE Section (Detail INTEGER, SX INTEGER, SZ INTEGER, "
                    + "Data BLOB, ApplyToParent INTEGER, ModifiedMs INTEGER);"
                    + "INSERT INTO Section VALUES (0, 11, 12, x'010203', 0, 0);";
                cmd.ExecuteNonQuery();
            }

            Environment.SetEnvironmentVariable(
                "VINTAGEHORIZONS_TEST_LOCAL_OFFER_MISS_MARKER", marker);
            using LodLocalOfferSource? offers = LodLocalOfferSource.TryOpen(clientDb, new CaptureLogger());
            c.True(offers != null, "the forced-miss sibling fixture opens");
            if (offers == null) return;

            long key = LodWorld.SectionKey(0, 11, 12);
            c.True(offers.RequestBlob(key), "the forced-miss blob read is queued");
            c.True(WaitForBlobResult(offers).Blob == null,
                "the integration hook forces exactly one retryable sibling-cache miss");
            c.Eq(LodLocalOfferSource.DescribeKey(key), File.ReadAllText(marker).Trim(),
                "the miss marker identifies the exact section that must retry");

            Thread.Sleep(1100);
            c.True(offers.RequestBlob(key), "the same key can retry after its cooldown");
            c.SeqEq(new byte[] { 1, 2, 3 },
                WaitForBlobResult(offers).Blob ?? Array.Empty<byte>(),
                "the same sibling-cache key succeeds after the normal retry cooldown");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "VINTAGEHORIZONS_TEST_LOCAL_OFFER_MISS_MARKER", oldMarker);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    static LodSiblingRow[] WaitForRows(LodLocalOfferSource offers)
    {
        offers.RequestDiscovery();
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (timeout.ElapsedMilliseconds < 5000)
        {
            if (offers.TryTakeChangedRows(out LodSiblingRow[] rows)) return rows;
            Thread.Sleep(10);
        }
        return Array.Empty<LodSiblingRow>();
    }

    /// <summary>
    /// The premise of the incremental offer scan, proven against the real store on a real
    /// file: rewriting a key numbers its row above every row that existed, and an untouched
    /// row keeps its rowid. Also the reason the scan is not incremental alone: once the
    /// highest row is deleted - as the store deletes an unreadable one - SQLite hands that
    /// rowid out again, below what the scanner has already seen.
    /// </summary>
    static void AStoreRewriteGetsANewRowid(Check c)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-rowid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "server.db");
        long a = LodWorld.SectionKey(0, 1, 1);
        long parent = LodWorld.SectionKey(3, 2, 2);
        long b = LodWorld.SectionKey(0, 3, 3);
        long late = LodWorld.SectionKey(0, 9, 9);
        try
        {
            WithStore(path, store =>
            {
                store.SaveBlob(0, 1, 1, new byte[] { 1 }, applyToParent: false);
                store.SaveBlob(3, 2, 2, new byte[] { 2 }, applyToParent: false);
                store.SaveBlob(0, 3, 3, new byte[] { 3 }, applyToParent: false);
            });
            Dictionary<long, long> before = ReadRowids(path);

            WithStore(path, store => store.SaveBlob(3, 2, 2, new byte[] { 4 }, applyToParent: false));
            Dictionary<long, long> after = ReadRowids(path);
            c.True(after[parent] > before.Values.Max(),
                "a rewritten row is numbered above every row that existed before it");
            c.Eq(before[a], after[a], "an untouched row keeps its rowid");
            c.Eq(before[b], after[b], "and so does every other untouched row");

            ExecuteRaw(path, "DELETE FROM Section WHERE Detail=3 AND SX=2 AND SZ=2");
            WithStore(path, store => store.SaveBlob(0, 9, 9, new byte[] { 5 }, applyToParent: false));
            c.Eq(after[parent], ReadRowids(path)[late],
                "after the highest row is deleted its rowid is reused, which only a full scan sees");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
        }
    }

    /// <summary>
    /// Merge records are the client's memory of which server-cache rows its copies already
    /// contain. They must survive a restart, leave with the section row they describe when
    /// the store deletes an unreadable one, and leave with every section on a format purge.
    /// </summary>
    static void SiblingMergeRecordsFollowTheirSections(Check c)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-merge-records-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cache.db");
        long kept = LodWorld.SectionKey(0, 4, 4);
        long unreadable = LodWorld.SectionKey(1, 5, 5);
        byte[] good = LodStore.Serialize(Fixtures.Snapshot(Fixtures.SolidSection()));
        try
        {
            WithStore(path, store =>
            {
                store.SaveBlob(0, 4, 4, good, applyToParent: false);
                store.SaveBlob(1, 5, 5, new byte[] { 4, 0xFF, 0x00 }, applyToParent: false);
                store.SaveSiblingMerges(new[] { (unreadable, 12L) });

                // The path the pipeline uses: queued on the storage worker, written as it stops.
                using var worker = new LodStorageThread(store);
                worker.SaveSiblingMerges(new[] { (kept, 11L) });
            });

            var records = new Dictionary<long, long>();
            WithStore(path, store =>
            {
                c.Eq(2, store.LoadSiblingMerges((key, rowid) => records[key] = rowid),
                    "records written directly and through the storage worker both survive a reopen");
                c.Eq(11L, records.GetValueOrDefault(kept), "each keeps the rowid it named");
                c.True(store.LoadSection(1, 5, 5, null!, resolveBlockIds: false) == null,
                    "the unreadable row reads as missing");
                records.Clear();
                store.LoadSiblingMerges((key, rowid) => records[key] = rowid);
                c.False(records.ContainsKey(unreadable),
                    "deleting an unreadable row deletes its merge record with it");
                c.True(records.ContainsKey(kept), "and no other record");
            });

            ExecuteRaw(path, "UPDATE Meta SET Value='1' WHERE Key='FormatVersion'");
            WithStore(path, store =>
                c.Eq(0, store.LoadSiblingMerges((_, _) => { }),
                    "a format purge discards the merge records with the sections they describe"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
        }
    }

    static void WithStore(string path, Action<LodStore> use)
    {
        using var store = new LodStore(new CaptureLogger());
        if (!store.Open(path)) throw new InvalidOperationException("fixture cache did not open: " + path);
        use(store);
    }

    /// <summary>An edit behind the store's back, unpooled so no handle outlives it (G13).</summary>
    static void ExecuteRaw(string path, string sql)
    {
        var options = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        };
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(options.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static Dictionary<long, long> ReadRowids(string path)
    {
        var options = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        };
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(options.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT rowid, Detail, SX, SZ FROM Section";
        var rowids = new Dictionary<long, long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rowids[LodWorld.SectionKey(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3))] =
                reader.GetInt64(0);
        }
        return rowids;
    }

    static LodLocalOfferSource.BlobResult WaitForBlobResult(LodLocalOfferSource offers)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (timeout.ElapsedMilliseconds < 5000)
        {
            if (offers.TryTakeBlobResult(out LodLocalOfferSource.BlobResult result)) return result;
            Thread.Sleep(10);
        }
        return default;
    }

    static bool ProcessHoldsHandleTo(string path)
    {
        foreach (string fd in Directory.EnumerateFiles("/proc/self/fd"))
        {
            try
            {
                if (new FileInfo(fd).LinkTarget == path) return true;
            }
            catch
            {
                // A descriptor can close between listing and reading; not this check's business.
            }
        }
        return false;
    }

    /// <summary>
    /// Server assist must never borrow the writable LodStore connection on the owning
    /// thread. Its dedicated reader owns one unpooled read-only connection, preserves
    /// FIFO result order, and counts completed blobs against the same bounded allowance
    /// as queued and executing reads.
    /// </summary>
    static void AssistBlobReaderIsBoundedOrderedAndReadOnly(Check c)
    {
        string dir = Path.Combine(Path.GetTempPath(), "vh-assist-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "server.db");
        try
        {
            var writerOptions = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            };
            using (var writer = new Microsoft.Data.Sqlite.SqliteConnection(writerOptions.ToString()))
            {
                writer.Open();
                using var create = writer.CreateCommand();
                create.CommandText =
                    "CREATE TABLE Section (Detail INTEGER NOT NULL, SX INTEGER NOT NULL, "
                    + "SZ INTEGER NOT NULL, Data BLOB NOT NULL, PRIMARY KEY (Detail, SX, SZ));";
                create.ExecuteNonQuery();

                for (int i = 0; i < LodAssistBlobReader.MaxOutstanding; i++)
                {
                    using var insert = writer.CreateCommand();
                    insert.CommandText =
                        "INSERT INTO Section (Detail, SX, SZ, Data) VALUES (0, @x, 7, @data)";
                    insert.Parameters.AddWithValue("@x", i + 1);
                    insert.Parameters.AddWithValue("@data", new byte[] { (byte)(i + 1), 0x5a });
                    insert.ExecuteNonQuery();
                }
            }

            var logger = new CaptureLogger();
            using (var reader = new LodAssistBlobReader(path, logger, trackAllocations: true))
            {
                var expected = new List<long>();
                for (int i = 0; i < LodAssistBlobReader.MaxOutstanding; i++)
                {
                    long key = LodWorld.SectionKey(0, i + 1, 7);
                    expected.Add(key);
                    c.True(reader.TryEnqueue(new LodAssistBlobReadRequest(
                        i % 2 == 0 ? "alice" : "bob", 4, key, Environment.TickCount64)),
                        "a read within the total outstanding cap is accepted");
                }

                c.False(reader.TryEnqueue(new LodAssistBlobReadRequest(
                    "carol", 9, LodWorld.SectionKey(0, 99, 7), Environment.TickCount64)),
                    "queued and completed reads together enforce the outstanding cap");

                LodAssistBlobReadResult[] results = WaitForAssistReadResults(
                    reader, LodAssistBlobReader.MaxOutstanding);
                c.Eq(LodAssistBlobReader.MaxOutstanding, results.Length,
                    "every accepted read publishes one result");
                c.SeqEq(expected, results.Select(result => result.Key).ToArray(),
                    "the single reader publishes results in request order");
                c.True(results.All(result => !result.ReadFailed),
                    "ordinary reads do not report failures");
                c.True(results.Select((result, i) =>
                        result.Blob.SequenceEqual(new byte[] { (byte)(i + 1), 0x5a })).All(ok => ok),
                    "each result carries the exact stored blob");
                c.Eq(0, reader.Outstanding,
                    "taking every result releases the complete outstanding allowance");

                long missing = LodWorld.SectionKey(0, 999, 7);
                c.True(reader.TryEnqueue(new LodAssistBlobReadRequest(
                    "alice", 4, missing, Environment.TickCount64)),
                    "a later read is accepted after results release the cap");
                LodAssistBlobReadResult[] miss = WaitForAssistReadResults(reader, 1);
                c.Eq(1, miss.Length, "a missing row still publishes one terminal result");
                c.Eq(0, miss[0].Blob.Length, "a missing row is represented by an empty blob");
                c.False(miss[0].ReadFailed, "an ordinary row miss is not a database failure");
            }

            using (var failedReader = new LodAssistBlobReader(
                Path.Combine(dir, "absent.db"), logger, trackAllocations: false))
            {
                long key = LodWorld.SectionKey(0, 1, 7);
                c.True(failedReader.TryEnqueue(new LodAssistBlobReadRequest(
                    "alice", 4, key, Environment.TickCount64)),
                    "a read is accepted before a lazy connection failure is known");
                LodAssistBlobReadResult[] failed = WaitForAssistReadResults(failedReader, 1);
                c.Eq(1, failed.Length, "a database failure still publishes one result");
                c.True(failed[0].ReadFailed, "a database failure is explicit and retryable upstream");
                c.Eq(1, failedReader.ReadErrors, "the reader counts the failed query");
                c.True(failedReader.FirstReadError != null,
                    "the reader retains the first failure for diagnostics");
            }

            if (OperatingSystem.IsLinux())
            {
                c.False(ProcessHoldsHandleTo(path),
                    "disposing the assist reader closes rather than pools its file handle");
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp dir */ }
        }
    }

    static LodAssistBlobReadResult[] WaitForAssistReadResults(
        LodAssistBlobReader reader, int count)
    {
        var results = new List<LodAssistBlobReadResult>(count);
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (results.Count < count && timeout.ElapsedMilliseconds < 5000)
        {
            while (reader.TryTakeResult(out LodAssistBlobReadResult result))
                results.Add(result);
            if (results.Count < count) Thread.Sleep(10);
        }
        return results.ToArray();
    }

    /// <summary>
    /// Bad input must come back null, never throw. The storage thread deserializes rows off
    /// the main thread and the network path deserializes whatever a server sent; an
    /// exception on either takes down more than the one bad section.
    /// </summary>
    static void Rejection(Check c)
    {
        var store = new LodStore(null!);
        byte[] good = LodStore.Serialize(Fixtures.Snapshot(Fixtures.SolidSection()));

        c.NoThrow(() => store.DeserializeForeign(Array.Empty<byte>(), null), "an empty blob does not throw");
        c.Eq(null, store.DeserializeForeign(Array.Empty<byte>(), null), "an empty blob returns null");

        c.Eq(null, store.DeserializeForeign(new byte[] { 4 }, null), "a one-byte blob returns null");

        byte[] wrongVersion = (byte[])good.Clone();
        wrongVersion[0] = 99;
        c.Eq(null, store.DeserializeForeign(wrongVersion, null), "a blob from a future format returns null");

        // Truncation is the realistic corruption: a partial write, or a section cut short
        // in transit.
        byte[] truncated = good[..(good.Length / 2)];
        c.NoThrow(() => store.DeserializeForeign(truncated, null), "a truncated blob does not throw");
        c.Eq(null, store.DeserializeForeign(truncated, null), "a truncated blob returns null");

        byte[] garbage = (byte[])good.Clone();
        for (int i = 1; i < garbage.Length; i += 3) garbage[i] ^= 0xA5;
        c.NoThrow(() => store.DeserializeForeign(garbage, null), "a corrupted blob does not throw");

        c.True(store.DeserializeForeign(good, null) != null, "a good blob still deserializes");
    }

    static void BackgroundForeignDecodeIsBoundedAndIsolated(Check c)
    {
        using var store = new LodStore(null!);
        using var worker = new LodStorageThread(store);
        byte[] good = LodStore.Serialize(Fixtures.Snapshot(Fixtures.SolidSection()));

        // Local sibling-cache work has its own cap. Completed results still count until
        // the owning thread takes them, so a fast decoder cannot make this race flaky.
        for (int i = 0; i < 8; i++)
        {
            c.True(worker.TryEnqueueForeign(
                7, i, LodForeignSource.LocalOffer, good),
                $"local foreign decode {i} enters the bounded queue");
        }
        c.False(worker.TryEnqueueForeign(
            7, 99, LodForeignSource.LocalOffer, good),
            "a ninth local foreign decode is rejected before retaining its blob");

        for (int i = 0; i < 8; i++)
        {
            LodForeignDecodeResult result = WaitForForeign(worker, c);
            c.Eq(7L, result.Epoch, "the decoded result retains its world epoch");
            c.Eq((long)i, result.Key, "the foreign decoder preserves FIFO identity");
            c.True(result.Section != null, "a valid foreign blob decodes on the worker");
            c.True(result.Section?.PendingPaletteCodes != null,
                "worker decode retains codes for owning-thread registry resolution");
            c.True(result.Section == null || result.Section.Palette.All(e => e.BlockId == 0),
                "worker decode never resolves live block ids");
        }
        c.True(worker.CanEnqueueForeign(LodForeignSource.LocalOffer),
            "taking results releases local decoder capacity");

        byte[] future = (byte[])good.Clone();
        future[0] = 99;
        c.True(worker.TryEnqueueForeign(
            8, 100, LodForeignSource.ServerAssist, future),
            "a future blob is isolated to one server decode job");
        c.True(worker.TryEnqueueForeign(
            8, 101, LodForeignSource.ServerAssist, good,
            sourceRowid: 77, expectedContentRevision: 42),
            "valid work can queue behind a rejected blob");
        c.Eq(null, WaitForForeign(worker, c).Section,
            "a future blob returns one failed result rather than throwing");
        LodForeignDecodeResult merge = WaitForForeign(worker, c);
        c.True(merge.Section != null,
            "the decoder remains alive after rejecting a future blob");
        c.Eq(77L, merge.SourceRowid,
            "the decoder preserves the rowid a merge record will name");
        c.Eq(42L, merge.ExpectedContentRevision,
            "the decoder preserves the revision that guards a newer-wins merge");
    }

    static LodForeignDecodeResult WaitForForeign(LodStorageThread worker, Check c)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 5000)
        {
            if (worker.TryTakeForeignResult(out LodForeignDecodeResult result)) return result;
            Thread.Sleep(1);
        }

        c.True(false, "the foreign decoder publishes a result within five seconds");
        return default;
    }

    static LodSection Restore(Check c, LodSection section, string[]? codes = null)
    {
        byte[] blob = LodStore.Serialize(Fixtures.Snapshot(section, codes: codes));
        LodSection? back = new LodStore(null!).DeserializeForeign(blob, null);
        c.True(back != null, "the blob deserializes");
        return back!;
    }
}
