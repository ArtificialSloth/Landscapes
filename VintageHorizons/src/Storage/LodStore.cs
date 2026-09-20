using System.IO.Compression;
using System.Buffers.Binary;
using System.Text;
using Microsoft.Data.Sqlite;
using Vintagestory.API.Common;

namespace VintageHorizons;

/// <summary>
/// Per-world SQLite cache for LOD sections, built on the game's own SQLiteDBConnection
/// (bundled Microsoft.Data.Sqlite - no external dependencies). One row per
/// (detail level, section). Palettes store block CODES, not ids - ids are savegame-
/// local and can shift across game/mod updates (DH's lesson). The ApplyToParent flag
/// persists the mip-propagation queue so pyramid consistency survives crashes.
/// </summary>
public class LodStore : SQLiteDBConnection
{
    const byte BlobFormatVersion = 4;

    /// <summary>Bump when stored data SEMANTICS change; old rows are purged.</summary>
    const string SchemaVersion = "6"; // v6: palette colors are now UNTINTED + tint-class flags (v5: 1-block leaves)

    /// <summary>
    /// Bump when the MEANING of a stored column fingerprint changes; old claims are dropped.
    ///
    /// Separate from <see cref="SchemaVersion"/> because it says nothing about the sections:
    /// a fingerprint rule change makes the sweep examine columns it would have skipped, while
    /// every captured section stays exactly as valid as it was. Bumping the schema for it
    /// would throw away the cache for no reason.
    /// </summary>
    const string FingerprintRule = "2"; // v2: landscape-only rows (v1: whole stored chunk rows)

    public override string DBTypeCode => "vintagehorizons lod cache";

    SqliteCommand? upsertCmd;

    public LodStore(ILogger logger) : base(logger)
    {
    }

    public bool Open(string filePath)
    {
        string error = "";
        bool ok = OpenOrCreate(filePath, ref error, true, true, false);
        if (!ok) logger.Error("[VintageHorizons] Could not open LOD cache {0}: {1}", filePath, error);
        return ok;
    }

    protected override void CreateTablesIfNotExists(SqliteConnection sqliteConn)
    {
        using (var cmd = sqliteConn.CreateCommand())
        {
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS Meta (Key TEXT PRIMARY KEY, Value TEXT);
                CREATE TABLE IF NOT EXISTS Section (
                    Detail INTEGER NOT NULL,
                    SX INTEGER NOT NULL,
                    SZ INTEGER NOT NULL,
                    Data BLOB NOT NULL,
                    ApplyToParent INTEGER NOT NULL DEFAULT 0,
                    ModifiedMs INTEGER NOT NULL,
                    PRIMARY KEY (Detail, SX, SZ)
                );
                CREATE INDEX IF NOT EXISTS SectionKeys ON Section (Detail, SX, SZ, ApplyToParent);
                CREATE TABLE IF NOT EXISTS SiblingMerge (
                    Detail INTEGER NOT NULL,
                    SX INTEGER NOT NULL,
                    SZ INTEGER NOT NULL,
                    SourceRowid INTEGER NOT NULL,
                    PRIMARY KEY (Detail, SX, SZ)
                );
                CREATE TABLE IF NOT EXISTS ColumnFingerprint (
                    CX INTEGER NOT NULL,
                    CZ INTEGER NOT NULL,
                    Fingerprint INTEGER NOT NULL,
                    Rows INTEGER NOT NULL,
                    ModifiedMs INTEGER NOT NULL,
                    PRIMARY KEY (CX, CZ)
                );
                CREATE TABLE IF NOT EXISTS Climate (
                    PX INTEGER NOT NULL,
                    PZ INTEGER NOT NULL,
                    Data BLOB NOT NULL,
                    ModifiedMs INTEGER NOT NULL,
                    PRIMARY KEY (PX, PZ)
                );
                DROP TABLE IF EXISTS Region;
                DROP TABLE IF EXISTS Region2;";
            cmd.ExecuteNonQuery();
        }

        PurgeOutdatedData(sqliteConn);
        PurgeOutdatedFingerprints(sqliteConn);
    }

    /// <summary>
    /// Drops every stored column fingerprint when the rule that produced them changed.
    ///
    /// A fingerprint is a claim that this cache holds what the savegame held at a particular
    /// moment, expressed as one number. Change what that number covers and the old ones no
    /// longer answer any question: they cannot match, so they would report every column as
    /// changed - the safe direction, but silently and forever, since nothing rewrites a
    /// fingerprint except a capture. Dropping them says it once and recovers in one pass.
    /// </summary>
    void PurgeOutdatedFingerprints(SqliteConnection sqliteConn)
    {
        string? existing;
        using (var check = sqliteConn.CreateCommand())
        {
            check.CommandText = "SELECT Value FROM Meta WHERE Key='FingerprintRule'";
            existing = check.ExecuteScalar() as string;
        }
        if (existing == FingerprintRule) return;

        if (existing != null)
        {
            long dropped = 0;
            using (var count = sqliteConn.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM ColumnFingerprint";
                dropped = (long)(count.ExecuteScalar() ?? 0L);
            }
            if (dropped > 0)
            {
                logger.Notification(
                    "[VintageHorizons] The savegame fingerprint now covers the landscape only, "
                    + "so {0} older column claims no longer mean anything and were dropped. The "
                    + "next world start examines those columns once and records them again; no "
                    + "captured terrain is lost.", dropped);
            }
        }

        using var cmd = sqliteConn.CreateCommand();
        cmd.CommandText =
            "DELETE FROM ColumnFingerprint; "
            + "INSERT OR REPLACE INTO Meta (Key, Value) VALUES ('FingerprintRule', '"
            + FingerprintRule + "');";
        cmd.ExecuteNonQuery();
    }

    void PurgeOutdatedData(SqliteConnection sqliteConn)
    {
        string? existing;
        using (var check = sqliteConn.CreateCommand())
        {
            check.CommandText = "SELECT Value FROM Meta WHERE Key='FormatVersion'";
            existing = check.ExecuteScalar() as string;
        }
        if (existing == SchemaVersion) return;

        // Only say this when a cache really did hold an older format. A brand new
        // database has no FormatVersion row at all, so the check below is unequal on a
        // first-ever run too - and announcing that we are discarding someone's data
        // before they have any is alarming and untrue. The write still happens either
        // way; it is the claim that is conditional.
        //
        // With a count, because this is the one place the mod deletes data a player
        // accumulated. Deliberate destruction is fine; silent destruction is not.
        if (existing != null)
        {
            long discarded = 0;
            using (var count = sqliteConn.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM Section";
                discarded = (long)(count.ExecuteScalar() ?? 0L);
            }
            logger.Notification(
                "[VintageHorizons] LOD cache format {0} is not ours ({1}); discarding {2} cached "
                + "sections from the old format. They rebuild from capture as you play.",
                existing, SchemaVersion, discarded);
        }
        // Sections only, with the merge records that describe them. Remembered climate is
        // worldgen, not our format: it does not go stale when the way we store TERRAIN
        // changes, and throwing it away would cost the player the exploration that filled it
        // for no reason. It carries its own format byte and a blob that does not match is
        // ignored rather than parsed.
        //
        // The column fingerprints go with them. Each one claims this cache holds a proven
        // capture of a column, and the sweep skips a column whose claim matches the savegame;
        // leaving them behind after deleting the sections they describe would make the sweep
        // skip terrain it no longer has, permanently, with nothing to notice it.
        using var cmd = sqliteConn.CreateCommand();
        cmd.CommandText = "DELETE FROM Section; DELETE FROM SiblingMerge; DELETE FROM ColumnFingerprint; INSERT OR REPLACE INTO Meta (Key, Value) VALUES ('FormatVersion', '" + SchemaVersion + "');";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// One remembered climate patch, or null when the player has never been there. Called on
    /// the storage thread; the render path never touches SQLite for this.
    /// </summary>
    public byte[]? LoadClimatePatch(int px, int pz)
    {
        try
        {
            using var cmd = sqliteConn.CreateCommand();
            cmd.CommandText = "SELECT Data FROM Climate WHERE PX=@px AND PZ=@pz";
            cmd.Parameters.AddWithValue("@px", px);
            cmd.Parameters.AddWithValue("@pz", pz);
            return cmd.ExecuteScalar() as byte[];
        }
        catch (Exception e)
        {
            logger.Warning("[VintageHorizons] could not read remembered climate at {0},{1}: {2}",
                px, pz, e.Message);
            return null;
        }
    }

    /// <summary>
    /// Write one remembered climate patch. Failure is logged and dropped: the cells are relearned
    /// the next time the player is there, so there is nothing to retry and nothing to acknowledge.
    /// </summary>
    public void SaveClimatePatch(int px, int pz, byte[] data)
    {
        try
        {
            using var cmd = sqliteConn.CreateCommand();
            cmd.CommandText =
                "INSERT OR REPLACE INTO Climate (PX, PZ, Data, ModifiedMs) "
                + "VALUES (@px, @pz, @data, @ms)";
            cmd.Parameters.AddWithValue("@px", px);
            cmd.Parameters.AddWithValue("@pz", pz);
            cmd.Parameters.AddWithValue("@data", data);
            cmd.Parameters.AddWithValue("@ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }
        catch (Exception e)
        {
            logger.Warning("[VintageHorizons] could not store remembered climate at {0},{1}: {2}",
                px, pz, e.Message);
        }
    }

    /// <summary>
    /// Every column fingerprint this cache remembers, keyed by <see cref="Net.LodColumnMap.Key"/>.
    ///
    /// An old cache gains the table empty on first open, and a missing entry reads as
    /// "assume changed", which is exactly today's behaviour. That is why this is a new table
    /// rather than a schema bump: a bump would wipe the cache, and the cache is the baseline
    /// any measurement of this feature has to be made against.
    /// </summary>
    public Dictionary<long, ulong> LoadColumnFingerprints()
    {
        var found = new Dictionary<long, ulong>();
        try
        {
            // Unlike the climate accessors above, these are reached from the server tick
            // thread and from startup while the storage thread may be writing, so they take
            // the same transaction lock the bulk key load does.
            lock (transactionLock)
            {
                using var cmd = sqliteConn.CreateCommand();
                cmd.CommandText = "SELECT CX, CZ, Fingerprint FROM ColumnFingerprint";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    found[Net.LodColumnMap.Key(reader.GetInt32(0), reader.GetInt32(1))] =
                        unchecked((ulong)reader.GetInt64(2));
                }
            }
        }
        catch (Exception e)
        {
            logger.Warning("[VintageHorizons] could not read column fingerprints, so the sweep "
                + "will examine every column as it did before: {0}", e.Message);
            found.Clear();
        }
        return found;
    }

    /// <summary>
    /// Remember that this column's savegame content is now captured.
    ///
    /// **Call this only once the column's capture has durably succeeded.** The fingerprint
    /// records "we hold a correct capture of this", not "we have looked at this", and only
    /// the first may cause a later sweep to skip the column. Writing it when the row was
    /// merely read creates a permanent silent hole: a column whose capture then failed keeps
    /// a fingerprint that still matches an unchanged file forever, so neither the next sweep
    /// nor a background audit ever has a mismatch to act on.
    /// </summary>
    public void SaveColumnFingerprint(int cx, int cz, ulong fingerprint, int rows)
    {
        try
        {
            lock (transactionLock)
            {
                using var cmd = sqliteConn.CreateCommand();
                cmd.CommandText =
                    "INSERT OR REPLACE INTO ColumnFingerprint "
                    + "(CX, CZ, Fingerprint, Rows, ModifiedMs) "
                    + "VALUES (@cx, @cz, @fp, @rows, @ms)";
                cmd.Parameters.AddWithValue("@cx", cx);
                cmd.Parameters.AddWithValue("@cz", cz);
                cmd.Parameters.AddWithValue("@fp", unchecked((long)fingerprint));
                cmd.Parameters.AddWithValue("@rows", rows);
                cmd.Parameters.AddWithValue("@ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception e)
        {
            // Dropping one is safe in the direction that costs work rather than correctness:
            // the column simply gets examined again next time.
            logger.Warning("[VintageHorizons] could not store the column fingerprint at {0},{1}: "
                + "{2}", cx, cz, e.Message);
        }
    }

    /// <summary>
    /// Forget a column's fingerprint, so the next sweep examines it again. Used when a
    /// capture is known to have failed after its fingerprint was already recorded.
    /// </summary>
    public void ForgetColumnFingerprint(int cx, int cz)
    {
        try
        {
            lock (transactionLock)
            {
                using var cmd = sqliteConn.CreateCommand();
                cmd.CommandText = "DELETE FROM ColumnFingerprint WHERE CX=@cx AND CZ=@cz";
                cmd.Parameters.AddWithValue("@cx", cx);
                cmd.Parameters.AddWithValue("@cz", cz);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception e)
        {
            logger.Warning("[VintageHorizons] could not clear the column fingerprint at {0},{1}: "
                + "{2}", cx, cz, e.Message);
        }
    }

    public override void OnOpened()
    {
        base.OnOpened();

        upsertCmd = sqliteConn.CreateCommand();
        upsertCmd.CommandText =
            "INSERT OR REPLACE INTO Section (Detail, SX, SZ, Data, ApplyToParent, ModifiedMs) " +
            "VALUES (@detail, @sx, @sz, @data, @atp, @ms)";
        upsertCmd.Parameters.Add("@detail", SqliteType.Integer);
        upsertCmd.Parameters.Add("@sx", SqliteType.Integer);
        upsertCmd.Parameters.Add("@sz", SqliteType.Integer);
        upsertCmd.Parameters.Add("@data", SqliteType.Blob);
        upsertCmd.Parameters.Add("@atp", SqliteType.Integer);
        upsertCmd.Parameters.Add("@ms", SqliteType.Integer);
        upsertCmd.Prepare();
    }

    /// <summary>
    /// Write an already-serialized section. Deflate happens in Serialize (outside any
    /// lock, on the storage thread); the lock is held only for the row write, so a
    /// main-thread demand load never waits behind compression.
    /// </summary>
    public void SaveBlob(int level, int sx, int sz, byte[] data, bool applyToParent)
    {
        if (upsertCmd == null) return;

        lock (transactionLock)
        {
            ExecuteUpsert(level, sx, sz, data, applyToParent);
        }
    }

    /// <summary>
    /// Serialize a checkpoint outside the database lock, then commit every row in one
    /// SQLite transaction. The cache is reconstructible and checkpoints are deliberately
    /// coarse; paying one durable transaction per section only multiplies filesystem
    /// synchronization without making the checkpoint meaningfully safer.
    /// </summary>
    public void SaveBatch(IReadOnlyList<LodSaveSnapshot> snapshots)
    {
        if (upsertCmd == null || snapshots.Count == 0) return;

        var blobs = new byte[snapshots.Count][];
        for (int i = 0; i < snapshots.Count; i++) blobs[i] = Serialize(snapshots[i]);

        lock (transactionLock)
        {
            using SqliteTransaction transaction = sqliteConn.BeginTransaction();
            upsertCmd.Transaction = transaction;
            try
            {
                for (int i = 0; i < snapshots.Count; i++)
                {
                    LodSaveSnapshot snapshot = snapshots[i];
                    ExecuteUpsert(snapshot.Level, snapshot.SX, snapshot.SZ, blobs[i],
                        snapshot.ApplyToParent);
                }
                transaction.Commit();
            }
            finally
            {
                upsertCmd.Transaction = null;
            }
        }
    }

    void ExecuteUpsert(int level, int sx, int sz, byte[] data, bool applyToParent)
    {
        upsertCmd!.Parameters["@detail"].Value = level;
        upsertCmd.Parameters["@sx"].Value = sx;
        upsertCmd.Parameters["@sz"].Value = sz;
        upsertCmd.Parameters["@data"].Value = data;
        upsertCmd.Parameters["@atp"].Value = applyToParent ? 1 : 0;
        upsertCmd.Parameters["@ms"].Value = Environment.TickCount64;
        upsertCmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Block code -> id. Per-store on purpose: block ids are savegame-local, so a
    /// cache shared between worlds would resolve the previous world's ids. Concurrent
    /// because sections deserialize on both the main and storage threads.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> blockIdByCode = new();

    SqliteCommand? loadOneCmd;

    /// <summary>Load a single section row, or null if absent/unreadable. Used for demand reload after RAM eviction.</summary>
    /// <param name="resolveBlockIds">
    /// False when called from the storage thread: palette codes are kept on the
    /// section and resolved later on the main thread, so the block registry is never
    /// read off-thread.
    /// </param>
    public LodSection? LoadSection(int level, int sx, int sz, IWorldAccessor world, bool resolveBlockIds = true)
    {
        lock (transactionLock)
        {
            if (loadOneCmd == null)
            {
                loadOneCmd = sqliteConn.CreateCommand();
                loadOneCmd.CommandText = "SELECT Data FROM Section WHERE Detail=@detail AND SX=@sx AND SZ=@sz";
                loadOneCmd.Parameters.Add("@detail", SqliteType.Integer);
                loadOneCmd.Parameters.Add("@sx", SqliteType.Integer);
                loadOneCmd.Parameters.Add("@sz", SqliteType.Integer);
                loadOneCmd.Prepare();
            }

            loadOneCmd.Parameters["@detail"].Value = level;
            loadOneCmd.Parameters["@sx"].Value = sx;
            loadOneCmd.Parameters["@sz"].Value = sz;

            object? blob = loadOneCmd.ExecuteScalar();
            if (blob is not byte[] bytes) return null;

            LodSection? section = Deserialize(bytes, resolveBlockIds ? world : null);
            if (section == null)
            {
                // Unreadable data must never linger to slow down or confuse future
                // sessions - delete on sight; the area recaptures on exploration.
                logger.Warning("[VintageHorizons] Deleting unreadable cached section L{0} {1},{2}", level, sx, sz);
                DeleteSection(level, sx, sz);
            }
            return section;
        }
    }

    /// <summary>
    /// Parse a blob that did not come from this database - i.e. one off the network.
    /// Same reader as the disk path, so a section that survives the wire is
    /// indistinguishable from one that was stored locally.
    /// </summary>
    public LodSection? DeserializeForeign(byte[] blob, IWorldAccessor? world) => Deserialize(blob, world);

    /// <summary>
    /// Finish a section that was deserialized off-thread by resolving its palette
    /// block ids. MUST run on the main thread - it reads the block registry.
    /// </summary>
    /// <summary>
    /// Recompute a palette entry's flags, tint slot and colour from the live block. Set
    /// by the coordinator; runs on the main thread only. A zero colour means "keep what
    /// is stored" - see LodStableColorResolver.
    /// </summary>
    public System.Func<int, (byte Flags, byte TintSlot, int Color, ushort TextureSlot)>? ClassifyBlock;

    /// <summary>
    /// A block code to a live block id, or 0 when the registry does not know it.
    ///
    /// Only a SUCCESS is cached. Caching the failure too was the obvious thing, and it is
    /// wrong: this runs on the storage thread against whatever the registry holds at that
    /// moment, and sections start loading from cache before a world has finished coming up.
    /// A lookup that lost that race used to answer 0 for that code for the rest of the
    /// session, however many times it was asked and however ready the registry became. A
    /// miss costs an AssetLocation parse and a registry lookup, which is the price of not
    /// being permanently wrong; the hit path, which is nearly all of them, is unchanged.
    /// </summary>
    int LookUpBlockId(string code, System.Func<string, int> lookUp)
    {
        int blockId = lookUp(code);
        if (blockId <= 0)
        {
            unresolvedCodes.TryAdd(code, 0);
            return 0;
        }

        unresolvedCodes.TryRemove(code, out _);
        blockIdByCode[code] = blockId;
        return blockId;
    }

    /// <summary>The registry lookup, as a seam: a test needs to fail one and then succeed.</summary>
    static System.Func<string, int> RegistryLookup(IWorldAccessor world) =>
        code => world.GetBlock(new Vintagestory.API.Common.AssetLocation(code))?.BlockId ?? 0;

    /// <summary>
    /// Codes no lookup has ever resolved. Held so the count and the names can be reported
    /// rather than guessed at: an unresolved code becomes terrain with no colour, and the
    /// only previous symptom was black ground in a screenshot.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> unresolvedCodes = new();

    /// <summary>Codes that never resolved, for the status commands. Empty is the norm.</summary>
    public string[] UnresolvedCodes() => unresolvedCodes.Keys.ToArray();

    void Reclassify(LodSection section, int index, int blockId)
    {
        if (ClassifyBlock == null || blockId <= 0) return;

        (byte flags, byte slot, int color, ushort textureSlot) = ClassifyBlock(blockId);
        LodPaletteEntry e = section.Palette[index];
        e.Flags = flags;
        e.TintSlot = slot;
        e.TextureSlot = textureSlot;

        // Colours captured before they were section-independent were a random pixel of
        // the block's texture, redrawn for every section, which put a hard green/brown
        // step on every section edge (G46). The live block is the authority here for the
        // same reason it already is for flags, and correcting on load repairs a cache
        // that already exists instead of discarding it.
        if (color != 0) e.Color = color;
        section.Palette[index] = e;
    }

    public void ResolvePendingPalette(LodSection section, IWorldAccessor world) =>
        ResolvePendingPalette(section, RegistryLookup(world));

    public void ResolvePendingPalette(LodSection section, System.Func<string, int> lookUp)
    {
        string[]? codes = section.PendingPaletteCodes;
        if (codes == null) return;

        for (int i = 0; i < codes.Length && i < section.Palette.Count; i++)
        {
            string code = codes[i];
            if (code.Length == 0) continue;

            if (!blockIdByCode.TryGetValue(code, out int blockId))
            {
                blockId = LookUpBlockId(code, lookUp);
            }

            LodPaletteEntry e = section.Palette[i];
            e.BlockId = blockId;
            section.Palette[i] = e;
            Reclassify(section, i, blockId);
        }

        section.PendingPaletteCodes = null;
    }

    void DeleteSection(int level, int sx, int sz)
    {
        // Its merge record goes with it: the record describes a copy that no longer exists.
        using var cmd = sqliteConn.CreateCommand();
        cmd.CommandText = "DELETE FROM Section WHERE Detail=@detail AND SX=@sx AND SZ=@sz;"
            + "DELETE FROM SiblingMerge WHERE Detail=@detail AND SX=@sx AND SZ=@sz";
        cmd.Parameters.AddWithValue("@detail", level);
        cmd.Parameters.AddWithValue("@sx", sx);
        cmd.Parameters.AddWithValue("@sz", sz);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Every sibling merge record: for a key, the rowid of the server-side cache row whose
    /// content this cache's stored copy already contains (see LodRemoteKeySet). Read once at
    /// open, so a restart does not merge the same rows again. An additive table: an older
    /// build never reads it, and the Section table's meaning is unchanged.
    /// </summary>
    public int LoadSiblingMerges(Action<long, long> onRecord)
    {
        int count = 0;
        lock (transactionLock)
        {
            using var cmd = sqliteConn.CreateCommand();
            cmd.CommandText = "SELECT Detail, SX, SZ, SourceRowid FROM SiblingMerge";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                onRecord(LodWorld.SectionKey(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)),
                    reader.GetInt64(3));
                count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Write merge records in one transaction, on the storage thread. A failure is logged and
    /// dropped: the only cost is that those merges run again next session.
    /// </summary>
    public void SaveSiblingMerges(IReadOnlyList<(long Key, long Rowid)> records)
    {
        if (records.Count == 0) return;
        try
        {
            lock (transactionLock)
            {
                using SqliteTransaction transaction = sqliteConn.BeginTransaction();
                using var cmd = sqliteConn.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "INSERT OR REPLACE INTO SiblingMerge (Detail, SX, SZ, SourceRowid) "
                    + "VALUES (@detail, @sx, @sz, @rowid)";
                SqliteParameter detail = cmd.Parameters.Add("@detail", SqliteType.Integer);
                SqliteParameter sx = cmd.Parameters.Add("@sx", SqliteType.Integer);
                SqliteParameter sz = cmd.Parameters.Add("@sz", SqliteType.Integer);
                SqliteParameter rowid = cmd.Parameters.Add("@rowid", SqliteType.Integer);
                foreach ((long key, long sourceRowid) in records)
                {
                    detail.Value = LodWorld.KeyLevel(key);
                    sx.Value = LodWorld.KeySx(key);
                    sz.Value = LodWorld.KeySz(key);
                    rowid.Value = sourceRowid;
                    cmd.ExecuteNonQuery();
                }
                transaction.Commit();
            }
        }
        catch (Exception e)
        {
            logger.Warning("[VintageHorizons] could not record {0} merged server-cache rows; "
                + "they merge again next session: {1}", records.Count, e.Message);
        }
    }

    /// <summary>
    /// Enumerate stored section KEYS only - no blob parsing. Join-time cost stays
    /// proportional to explored area count, not data size; section data itself is
    /// demand-loaded when the renderer or pipeline first needs it.
    ///
    /// The SectionKeys index is what makes that true in practice. Without it this is a
    /// table scan, and the table's leaf pages are spread through a file that is hundreds
    /// of megabytes of section blobs, so the reads are scattered over the whole cache
    /// even though not one blob is wanted. The index holds the four columns alone, a few
    /// hundred KB read in order.
    ///
    /// Measured on ext4 with the page cache dropped between runs, which is the state at
    /// world join:
    ///
    ///    5581 sections (257 MB)   173.4 ms -> 4.6 ms
    ///   15000 sections (691 MB)   931.1 ms -> 12.6 ms
    ///
    /// The same test against tmpfs shows only 2.1x, so a measurement taken in RAM will
    /// say this does not matter. It does; the cache lives on a disk.
    /// </summary>
    public int LoadAllKeys(Action<int, int, int, bool> onKey)
    {
        int count = 0;
        lock (transactionLock)
        {
            using var cmd = sqliteConn.CreateCommand();
            cmd.CommandText = "SELECT Detail, SX, SZ, ApplyToParent FROM Section";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                onKey(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3) != 0);
                count++;
            }
        }
        return count;
    }

    /// <summary>Thread-safe: reads only the snapshot's private arrays, never live world state.</summary>
    public static byte[] Serialize(LodSaveSnapshot snap)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(BlobFormatVersion);

        using (var deflate = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var w = new BinaryWriter(deflate, Encoding.UTF8))
        {
            w.Write((ushort)snap.PaletteCodes.Length);
            for (int i = 0; i < snap.PaletteCodes.Length; i++)
            {
                w.Write(snap.PaletteCodes[i]);
                w.Write(snap.PaletteColors[i]);
                w.Write(snap.PaletteFlags[i]);
            }

            int total = LodSection.GridSize * LodSection.GridSize;
            for (int col = 0; col < total; col++)
            {
                w.Write((ushort)snap.RunCount(col));
            }
            foreach (ulong run in snap.Runs) w.Write(run);

            var capturedBits = new byte[total / 8];
            for (int i = 0; i < total; i++)
            {
                if (snap.Captured[i]) capturedBits[i >> 3] |= (byte)(1 << (i & 7));
            }
            w.Write(capturedBits);

            // Append-compatible format-4 extension. Old readers stop after Captured and
            // safely ignore this tail; new readers treat a missing tail as legacy data
            // whose snow source is unknown, so the existing cache does not need a purge.
            var snowStateBits = new byte[LodSection.SnowStateObservedByteCount];
            int words = Math.Min(
                snap.SnowStateObservedBits.Length, LodSection.SnowStateObservedWordCount);
            for (int i = 0; i < words; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(
                    snowStateBits.AsSpan(i * sizeof(ulong), sizeof(ulong)),
                    snap.SnowStateObservedBits[i]);
            }
            w.Write(snowStateBits);

            // Second append-compatible format-4 tail: the answer paired with the
            // provenance above. A reader must have both before it may suppress climate
            // fallback; 0.4.43 rows carried only provenance and could otherwise claim a
            // downsampled-away snow layer was authoritative bare ground.
            var snowCoveredBits = new byte[LodSection.SnowStateObservedByteCount];
            words = Math.Min(
                snap.SnowCoveredBits.Length, LodSection.SnowStateObservedWordCount);
            for (int i = 0; i < words; i++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(
                    snowCoveredBits.AsSpan(i * sizeof(ulong), sizeof(ulong)),
                    snap.SnowCoveredBits[i]);
            }
            w.Write(snowCoveredBits);
        }

        return ms.ToArray();
    }

    /// <param name="world">Null to defer block-id resolution to the main thread.</param>
    LodSection? Deserialize(byte[] blob, IWorldAccessor? world)
    {
        if (blob.Length < 2 || blob[0] != BlobFormatVersion) return null;

        try
        {
            using var ms = new MemoryStream(blob, 1, blob.Length - 1);
            using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
            using var r = new BinaryReader(deflate, Encoding.UTF8);

            var section = new LodSection();

            int paletteCount = r.ReadUInt16();
            string[]? deferredCodes = world == null ? new string[paletteCount] : null;

            for (int i = 0; i < paletteCount; i++)
            {
                string code = r.ReadString();
                int color = r.ReadInt32();
                byte flags = r.ReadByte();

                int blockId = 0;
                if (deferredCodes != null)
                {
                    deferredCodes[i] = code;
                }
                else if (code.Length > 0 && !blockIdByCode.TryGetValue(code, out blockId))
                {
                    blockId = LookUpBlockId(code, RegistryLookup(world!));
                }
                section.Palette.Add(new LodPaletteEntry { BlockId = blockId, Color = color, Flags = flags });

                // Stored flags predate per-species tint slots (and can go stale if a game
                // update moves a block to a different colour map), so the live block is
                // the authority whenever we can resolve it here.
                if (deferredCodes == null) Reclassify(section, section.Palette.Count - 1, blockId);
            }

            section.PendingPaletteCodes = deferredCodes;

            int total = LodSection.GridSize * LodSection.GridSize;
            var counts = new ushort[total];
            int runTotal = 0;
            for (int col = 0; col < total; col++)
            {
                counts[col] = r.ReadUInt16();
                runTotal += counts[col];
            }

            section.Runs = new ulong[runTotal];
            for (int i = 0; i < runTotal; i++) section.Runs[i] = r.ReadUInt64();

            int offset = 0;
            for (int col = 0; col < total; col++)
            {
                section.ColumnStart[col] = offset;
                offset += counts[col];
            }
            section.ColumnStart[total] = offset;

            var capturedBits = r.ReadBytes(total / 8);
            for (int i = 0; i < total; i++)
            {
                if ((capturedBits[i >> 3] & (1 << (i & 7))) != 0)
                {
                    section.Captured[i] = true;
                    section.CapturedColumns++;
                }
            }

            byte[] snowStateBits = r.ReadBytes(LodSection.SnowStateObservedByteCount);
            if (snowStateBits.Length != 0
                && snowStateBits.Length != LodSection.SnowStateObservedByteCount)
                return null;

            for (int i = 0; i < snowStateBits.Length / sizeof(ulong); i++)
            {
                section.SnowStateObservedBits[i] = BinaryPrimitives.ReadUInt64LittleEndian(
                    snowStateBits.AsSpan(i * sizeof(ulong), sizeof(ulong)));
            }

            byte[] snowCoveredBits = r.ReadBytes(LodSection.SnowStateObservedByteCount);
            if (snowCoveredBits.Length != 0
                && snowCoveredBits.Length != LodSection.SnowStateObservedByteCount)
                return null;

            if (snowCoveredBits.Length == 0)
            {
                // The 0.4.43 extension knew only that vanilla had been sampled. At mip
                // levels that bit could survive after the thin snow material vanished,
                // so it is not enough to assert bare ground. Retain terrain and fall back
                // to climate until a rolling authoritative recapture upgrades it.
                Array.Clear(section.SnowStateObservedBits);
            }
            else
            {
                for (int i = 0; i < snowCoveredBits.Length / sizeof(ulong); i++)
                {
                    section.SnowCoveredBits[i] = BinaryPrimitives.ReadUInt64LittleEndian(
                        snowCoveredBits.AsSpan(i * sizeof(ulong), sizeof(ulong)));
                }
            }

            return section;
        }
        catch
        {
            return null;
        }
    }

    public override void Close()
    {
        upsertCmd?.Dispose();
        upsertCmd = null;
        loadOneCmd?.Dispose();
        loadOneCmd = null;
        base.Close();
    }
}
