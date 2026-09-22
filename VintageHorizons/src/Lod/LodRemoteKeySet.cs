namespace VintageHorizons;

/// <summary>The owning-thread outcome of trying to adopt one local sibling-cache offer.</summary>
public enum LodLocalOfferOutcome
{
    /// <summary>The blob installed and the source no longer owns a pending request.</summary>
    Installed,

    /// <summary>The listed row was not readable yet; keep every request marker retryable.</summary>
    RetryableMiss,

    /// <summary>The blob was corrupt or local data won the race; stop asking this source.</summary>
    Unavailable,
}

/// <summary>What one batch of sibling-cache rows turned into.</summary>
/// <param name="Offered">Keys this client does not hold, now offered for adoption.</param>
/// <param name="FillsQueued">Keys it does hold, now waiting to be merged.</param>
public readonly record struct LodSiblingDelta(int Offered, int FillsQueued);

/// <summary>How a server-sync fill ended (see <see cref="LodRemoteKeySet.QueueNetworkRefresh"/>).</summary>
public enum LodNetworkRefreshEnd
{
    /// <summary>The server's copy, at <see cref="LodNetworkRefreshOutcome.Version"/>, is merged and durable.</summary>
    Durable,

    /// <summary>
    /// The server's copy is merged but not yet saved. The fill is over, so its fetch slot is
    /// free; the debt is not, until <see cref="Durable"/> follows for the same section.
    /// </summary>
    Merged,

    /// <summary>The server refused it or its copy was unusable; the client's own copy is untouched.</summary>
    Failed,

    /// <summary>The client's own copy could not be read back, so there is nothing left to correct.</summary>
    NotHeld,
}

public readonly record struct LodNetworkRefreshOutcome(long Key, LodNetworkRefreshEnd End, long Version);

/// <summary>
/// Bookkeeping for sections a remote source offers: which keys only it has, which of those
/// the view currently wants, and which have been asked for already.
///
/// Split out of LodPipeline so it can be reasoned about - and tested - on its own. It is
/// pure set logic over a LodWorld, but living inside a class whose constructor needs a game
/// API and starts five threads made it unreachable, and the one bug that mattered most here
/// took three wrong diagnoses read off counters before anyone looked at the branch itself.
///
/// Two kinds of offer. A key this client does not hold is remote-only: the view adopts the
/// whole row the first time it wants it. A key it does hold may still be incomplete - copied
/// while pregen had filled a sliver of it, or captured only partly by this client - so the
/// source's row is merged into it instead, and that pending merge is a fill (see
/// <see cref="LodSection.MergeFrom"/>). Before fills existed a held key was skipped for good,
/// which is what left the distant walls of the 18:23 screenshot.
/// </summary>
public class LodRemoteKeySet
{
    /// <summary>
    /// Eager fills that may be loading or pinned in RAM at once. An eager fill (a rewritten
    /// level-0 row) is fetched whether or not the view shows the section, so its target must
    /// be brought into RAM first and kept there until the merge lands. This bounds that RAM.
    /// </summary>
    public const int MaxEagerFillsActive = 16;

    readonly LodWorld world;

    public LodRemoteKeySet(LodWorld world) => this.world = world;

    /// <summary>
    /// Keys only a remote source has. Kept separate from HasDataSet so the loader can tell
    /// "evicted from RAM, still on disk" from "never been on this disk".
    /// </summary>
    public readonly HashSet<long> RemoteOnly = new();

    /// <summary>
    /// Keys this store actually holds a row for, as reported by LoadAllKeys. Distinct from
    /// LodWorld.HasDataSet, which also holds ancestors synthesised for quadtree descent and
    /// so cannot answer "can local disk supply this?".
    /// </summary>
    readonly HashSet<long> localKeys = new();

    readonly HashSet<long> remoteWanted = new();

    readonly record struct FillRequest(long SourceRowid, LodMergeMode Mode, bool Eager);

    /// <summary>Held keys whose source has a row this client's copy has not merged.</summary>
    readonly Dictionary<long, FillRequest> fills = new();

    /// <summary>Pending fills waiting for their residency check, round-robin (see PumpFills).</summary>
    readonly Queue<long> fillChecks = new();
    readonly HashSet<long> fillChecksQueued = new();

    /// <summary>Fills whose target is in RAM, waiting for a source to accept the request.</summary>
    readonly HashSet<long> fillWanted = new();

    /// <summary>Fills a source accepted: the blob is being read, decoded or installed.</summary>
    readonly HashSet<long> fillInFlight = new();

    /// <summary>Eager fills whose target is pinned in RAM (at most MaxEagerFillsActive).</summary>
    readonly HashSet<long> eagerActive = new();

    /// <summary>
    /// For each key, the sibling-cache rowid this client has already accounted for: loaded
    /// from the durable merge records at open, then the row it first saw, adopted or merged
    /// this session. A held key whose row id has moved on from this was rewritten by the
    /// server after the client saw it.
    /// </summary>
    readonly Dictionary<long, long> siblingAccounted = new();

    /// <summary>Merges whose record may be written once the merged copy is durable.</summary>
    readonly Dictionary<long, (long Rowid, long Revision)> recordsAwaitingDurable = new();
    readonly List<(long Key, long Rowid)> recordsReady = new();

    /// <summary>
    /// Server-sync fills whose ending the tracker must hear about. The network has no rowid, so
    /// these carry the server's section version instead, in the fill's SourceRowid, and never
    /// write a sibling merge record.
    /// </summary>
    readonly HashSet<long> networkTracked = new();
    readonly Dictionary<long, (long Version, long Revision)> networkAwaitingDurable = new();
    readonly List<LodNetworkRefreshOutcome> networkOutcomes = new();

    public int PendingFills => fills.Count;
    public int FillsWantedNow => fillWanted.Count;
    public int FillsInFlight => fillInFlight.Count;
    public int EagerFillsActive => eagerActive.Count;
    public int MergeRecordsAwaitingSave => recordsAwaitingDurable.Count;

    /// <summary>Fills finished without a read because the held copy had every column already.</summary>
    public long FillsAlreadyComplete { get; private set; }

    /// <summary>Fills whose source arrived after their target had left RAM; they wait again.</summary>
    public long FillsDeferred { get; private set; }

    /// <summary>Fills dropped because their source could not be read or decoded.</summary>
    public long FillsAbandoned { get; private set; }

    /// <summary>Record that local disk holds a row for this key. Called during the cache key scan.</summary>
    public void AddLocalKey(long key) => localKeys.Add(key);

    /// <summary>
    /// A durable merge record read at open: this client's stored copy of the key already
    /// contains the sibling cache's row with this rowid.
    /// </summary>
    public void AddMergeRecord(long key, long sourceRowid) => siblingAccounted[key] = sourceRowid;

    public bool IsHeldLocally(long key) => localKeys.Contains(key);

    bool Holds(long key) => localKeys.Contains(key) || world.Sections.ContainsKey(key);

    /// <summary>
    /// Route a reload request. True when only the network can supply this key, so the
    /// caller sends it there instead of to the local store - a key local disk has never
    /// held would come back empty and land in LoadFailed, which is permanent.
    ///
    /// A key with a pending fill loads from local disk as usual: the fill needs the local
    /// copy in RAM to merge into, not a replacement for it.
    /// </summary>
    public bool WantFromRemote(long key)
    {
        if (!RemoteOnly.Contains(key)) return false;
        remoteWanted.Add(key);
        return true;
    }

    /// <summary>
    /// Register rows the sibling cache wrote since the last batch, at every detail level.
    ///
    /// A key this client does not hold is offered for adoption. A key it holds is merged. The
    /// first time the client sees the key's row, only the columns its copy lacks are filled.
    /// After that a rewritten level-0 row replaces the columns it carries, which is how
    /// server-side snow refreshes reach ground the client already holds (the owner's choice,
    /// 2026-09-12); it is fetched eagerly, as the whole-section refresh it replaces was. A
    /// rewritten parent only fills, because the client rebuilds its parents from level 0.
    /// </summary>
    public LodSiblingDelta AddSiblingRows(IEnumerable<LodSiblingRow> rows)
    {
        int offered = 0, queued = 0;
        foreach (LodSiblingRow row in rows)
        {
            bool seen = siblingAccounted.TryGetValue(row.Key, out long previous);
            if (seen && previous == row.Rowid) continue;
            siblingAccounted[row.Key] = row.Rowid;

            if (!Holds(row.Key))
            {
                if (Offer(row.Key)) offered++;
                continue;
            }

            bool rewritten = seen && LodWorld.KeyLevel(row.Key) == 0;
            QueueFill(row.Key, row.Rowid,
                rewritten ? LodMergeMode.NewerWins : LodMergeMode.FillMissing, eager: rewritten);
            queued++;
        }
        return new LodSiblingDelta(offered, queued);
    }

    /// <summary>
    /// Register durable level-zero changes a server announced over the network. A key this
    /// client does not hold is offered like any other; one it holds is merged with the newer
    /// copy winning, as for a rewritten sibling row. The network carries no rowid, so nothing
    /// is recorded: the server announces a key again only when it changes again.
    /// </summary>
    public int AddRemoteUpdates(IEnumerable<long> keys)
    {
        int added = 0;
        foreach (long key in keys)
        {
            if (!Holds(key))
            {
                Offer(key);
                continue;
            }

            QueueFill(key, sourceRowid: 0,
                LodWorld.KeyLevel(key) == 0 ? LodMergeMode.NewerWins : LodMergeMode.FillMissing,
                eager: true);
            added++;
        }
        return added;
    }

    /// <summary>
    /// Merge the server's copy of a held section it announced as changed at
    /// <paramref name="version"/>, newest winning, and report how it ends (see
    /// <see cref="TakeNetworkRefreshOutcomes"/>). Returns false, having offered the key instead,
    /// when this client does not hold it: there is no stale copy to correct.
    ///
    /// Eager, as a level-0 refresh always was: the client rebuilds its parents from level 0, so
    /// a distant change only shows once the level-0 merge has happened, whether or not the view
    /// ever loads that section itself.
    /// </summary>
    public bool QueueNetworkRefresh(long key, long version)
    {
        if (!Holds(key))
        {
            Offer(key);
            return false;
        }

        networkTracked.Add(key);
        // A fill already waiting for a newer announcement keeps it.
        if (fills.TryGetValue(key, out FillRequest pending)) version = Math.Max(version, pending.SourceRowid);
        QueueFill(key, version,
            LodWorld.KeyLevel(key) == 0 ? LodMergeMode.NewerWins : LodMergeMode.FillMissing, eager: true);
        return true;
    }

    /// <summary>Whether this client holds a copy of the key, on disk or in RAM.</summary>
    public bool HoldsSection(long key) => Holds(key);

    /// <summary>
    /// Register keys a remote source offers. Only those with no local data become
    /// remote-only; anything already on disk stays a local read, because local wins.
    /// </summary>
    public int AddRemoteKeys(IEnumerable<long> keys)
    {
        int added = 0;
        foreach (long key in keys)
        {
            if (Offer(key)) added++;
        }
        return added;
    }

    bool Offer(long key)
    {
        // Against localKeys, NOT HasDataSet. HasDataSet also contains every ancestor
        // that RegisterInTree synthesised while registering a finer key, so testing it
        // skipped coarse keys the server really could serve - whichever of a node and
        // its descendants happened to be processed first decided the other's fate.
        // Those keys stayed out of RemoteOnly, routed to a local store with no such
        // row, came back null, and were recorded in LoadFailed, which is permanent.
        // Observed as nodes drawn at L5 with two children "load-failed" and the
        // pipeline idle: terrain that could never resolve, at any distance.
        if (localKeys.Contains(key)) return false;
        if (!RemoteOnly.Add(key)) return false;

        // A key poisoned by that bug, or by an earlier miss before the manifest
        // arrived, has to be given back its chance now that a source exists.
        world.LoadFailed.Remove(key);
        world.LoadsInFlight.Remove(key);

        // Into the quadtree skeleton too, or descent will not even consider the key
        // and nothing would ever ask for it. Same call the local key scan uses, minus
        // the mip flag: the server's pyramid is already built, so nothing is pending.
        world.InstallStoredKey(LodWorld.KeyLevel(key), LodWorld.KeySx(key), LodWorld.KeySz(key),
            applyToParent: false);
        return true;
    }

    /// <summary>
    /// The client wrote its own content into a key the source offered but it never adopted,
    /// for example a capture forced through while the adoption was pending. That partial copy
    /// now stands in front of the source's, so ask for the source's other columns.
    /// </summary>
    public void NoteLocalContent(long key)
    {
        if (!RemoteOnly.Contains(key) || fills.ContainsKey(key) || !world.Sections.ContainsKey(key))
            return;
        QueueFill(key, sourceRowid: 0, LodMergeMode.FillMissing, eager: false);
    }

    void QueueFill(long key, long sourceRowid, LodMergeMode mode, bool eager)
    {
        if (fills.TryGetValue(key, out FillRequest pending))
        {
            // A pending rewrite stays a rewrite when a later row arrives behind it.
            if (pending.Mode == LodMergeMode.NewerWins) mode = LodMergeMode.NewerWins;
            eager |= pending.Eager;
        }
        fills[key] = new FillRequest(sourceRowid, mode, eager);
        EnqueueFillCheck(key);
    }

    void EnqueueFillCheck(long key)
    {
        if (fillChecksQueued.Add(key)) fillChecks.Enqueue(key);
    }

    /// <summary>
    /// Advance pending fills. A target already in RAM becomes wanted; an eager one not in RAM
    /// is loaded and pinned first, within MaxEagerFillsActive; a lazy one waits for the view
    /// to load it. At most <paramref name="maxExamined"/> keys per call, round-robin, so a
    /// first session with thousands of pending fills never costs one tick a scan of all of
    /// them. Every dequeued key is validated, because it can finish, restart or change mode
    /// while it waits (G25).
    /// </summary>
    public void PumpFills(int maxExamined)
    {
        int budget = Math.Min(maxExamined, fillChecks.Count);
        for (int n = 0; n < budget; n++)
        {
            long key = fillChecks.Dequeue();
            fillChecksQueued.Remove(key);
            if (!fills.TryGetValue(key, out FillRequest fill)
                || fillWanted.Contains(key) || fillInFlight.Contains(key)) continue;

            if (world.Sections.TryGetValue(key, out LodSection? section))
            {
                if (fill.Mode == LodMergeMode.FillMissing
                    && section.CapturedColumns >= LodSection.GridSize * LodSection.GridSize)
                {
                    // Every column is already ours, so a fill can add nothing. Account the row
                    // as merged without reading it.
                    FillsAlreadyComplete++;
                    if (networkTracked.Contains(key)) NoteNetworkMerged(key, fill.SourceRowid);
                    else NoteMerged(key, fill.SourceRowid);
                    continue;
                }
                fillWanted.Add(key);
                continue;
            }

            if (world.LoadFailed.Contains(key))
            {
                // Our own copy could not be read back (the store deletes an unreadable row),
                // so there is nothing to merge into: adopt the source's copy whole instead.
                DropFill(key);
                localKeys.Remove(key);
                Offer(key);
                if (networkTracked.Remove(key))
                    networkOutcomes.Add(new LodNetworkRefreshOutcome(key, LodNetworkRefreshEnd.NotHeld, 0));
                continue;
            }

            if (fill.Eager && (eagerActive.Contains(key) || eagerActive.Count < MaxEagerFillsActive))
            {
                if (eagerActive.Add(key)) world.EvictionPins.Add(key);
                world.EnsureResident(key);
            }
            EnqueueFillCheck(key);
        }
    }

    /// <summary>Fills whose target is in RAM and that no source has accepted yet.</summary>
    public long[] FillWanted() =>
        fillWanted.Count == 0 ? Array.Empty<long>() : fillWanted.ToArray();

    /// <summary>True when a decoded copy of this key may be merged into the resident one.</summary>
    public bool CanMergeInto(long key) => fills.ContainsKey(key) || RemoteOnly.Contains(key);

    /// <summary>How the pending fill for this key merges; an unrequested merge only fills.</summary>
    public LodMergeMode MergeModeFor(long key) =>
        fills.TryGetValue(key, out FillRequest fill) ? fill.Mode : LodMergeMode.FillMissing;

    /// <summary>
    /// Keys the render path asked for that only a remote source has. Fetch order therefore
    /// follows what the player can actually see.
    /// </summary>
    public long[] Wanted() =>
        remoteWanted.Count == 0 ? Array.Empty<long>() : remoteWanted.ToArray();

    /// <summary>
    /// Drop the keys that were actually asked for. Only these, never the whole set: a key
    /// held back by the in-flight cap is already in LodWorld.LoadsInFlight, where the
    /// render scheduler skips it, and forgetting it here would strand it there for the rest
    /// of the session. A fill that was sent moves to in flight.
    /// </summary>
    public void MarkRequested(IEnumerable<long> sent)
    {
        foreach (long key in sent)
        {
            remoteWanted.Remove(key);
            if (fillWanted.Remove(key)) fillInFlight.Add(key);
        }
    }

    /// <summary>
    /// Complete one visibility-driven read from the local sibling cache. A retryable miss
    /// keeps the key wanted but releases LoadsInFlight because no reader retained
    /// responsibility. Removing wanted while leaving that marker set is the stranded-key
    /// failure this state transition prevents. A fill never left the wanted set, so a miss
    /// changes nothing for it; the reader's own cooldown paces the retry.
    /// </summary>
    public void CompleteLocalOffer(long key, LodLocalOfferOutcome outcome)
    {
        switch (outcome)
        {
            case LodLocalOfferOutcome.Installed:
                remoteWanted.Remove(key);
                break;
            case LodLocalOfferOutcome.RetryableMiss:
                if (fills.ContainsKey(key) && !RemoteOnly.Contains(key)) break;
                world.LoadsInFlight.Remove(key);
                remoteWanted.Add(key);
                break;
            case LodLocalOfferOutcome.Unavailable:
                MarkUnavailable(key);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
        }
    }

    /// <summary>
    /// A sibling-cache reader transferred responsibility to the structural decoder.
    /// Stop duplicate submissions while retaining LoadsInFlight until publication.
    /// </summary>
    public void MarkLocalOfferAccepted(long key)
    {
        remoteWanted.Remove(key);
        if (fillWanted.Remove(key)) fillInFlight.Add(key);
    }

    /// <summary>
    /// A foreign section is now resident and queued for local persistence. Keep the
    /// offered route until storage acknowledges the row, so a failed write retains its
    /// remote fallback.
    /// </summary>
    public void MarkInstalled(long key)
    {
        remoteWanted.Remove(key);
        world.LoadFailed.Remove(key);
        world.LoadsInFlight.Remove(key);
    }

    /// <summary>
    /// The source's row with this rowid is now part of the resident copy, merged or adopted
    /// whole. The fill it answered is finished unless a later row arrived meanwhile. The
    /// record that the stored copy contains this row is written only once that copy is
    /// durable, so a crash before the save repeats the merge instead of skipping it (G16).
    /// </summary>
    public void NoteMerged(long key, long sourceRowid)
    {
        FinishFill(key, sourceRowid);

        if (sourceRowid == 0) return;
        if (!siblingAccounted.ContainsKey(key)) siblingAccounted[key] = sourceRowid;
        if (!world.SaveDirty.Contains(key))
        {
            // Nothing unsaved: the stored copy already holds everything the merge produced.
            recordsAwaitingDurable.Remove(key);
            recordsReady.Add((key, sourceRowid));
            return;
        }
        recordsAwaitingDurable[key] = (sourceRowid, world.PersistenceRevision(key));
    }

    /// <summary>
    /// The server's copy at <paramref name="version"/> is now part of the resident copy, merged or
    /// adopted whole. The network twin of <see cref="NoteMerged"/>: the same fill bookkeeping, but
    /// a server version is not a sibling rowid, so no merge record is written. A tracked server-sync
    /// fill is reported once the merged copy is durable, the same rule merge records follow (G16).
    /// </summary>
    public void NoteNetworkMerged(long key, long version)
    {
        bool finished = FinishFill(key, version);
        if (!networkTracked.Contains(key)) return;
        if (finished) networkTracked.Remove(key);

        if (!world.SaveDirty.Contains(key))
        {
            networkAwaitingDurable.Remove(key);
            networkOutcomes.Add(new LodNetworkRefreshOutcome(key, LodNetworkRefreshEnd.Durable, version));
            return;
        }
        networkAwaitingDurable[key] = (version, world.PersistenceRevision(key));
        // Saves are checkpointed every 30 seconds. Holding the fetch slot until then capped a
        // re-check at one checkpoint's worth of sections per checkpoint - about one a second
        // where the server serves eight - which the first sandbox test measured.
        if (finished)
            networkOutcomes.Add(new LodNetworkRefreshOutcome(key, LodNetworkRefreshEnd.Merged, version));
    }

    /// <summary>
    /// End the fill a merge answered, unless a newer row or version arrived while this one was
    /// being read (G7). Returns whether the fill is finished.
    /// </summary>
    bool FinishFill(long key, long sourceRevision)
    {
        fillWanted.Remove(key);
        fillInFlight.Remove(key);
        if (fills.TryGetValue(key, out FillRequest pending) && pending.SourceRowid > sourceRevision)
        {
            // A later row arrived while this one was being read; it still needs merging.
            EnqueueFillCheck(key);
            return false;
        }

        fills.Remove(key);
        if (eagerActive.Remove(key)) world.EvictionPins.Remove(key);
        return true;
    }

    /// <summary>How tracked server-sync fills ended since the last call, oldest first.</summary>
    public List<LodNetworkRefreshOutcome>? TakeNetworkRefreshOutcomes()
    {
        if (networkOutcomes.Count == 0) return null;
        var taken = new List<LodNetworkRefreshOutcome>(networkOutcomes);
        networkOutcomes.Clear();
        return taken;
    }

    /// <summary>
    /// A fill's source arrived after its target had left RAM, so there was nothing to merge
    /// into. Keep the fill; it is asked for again once the view, or an eager load, brings the
    /// section back.
    /// </summary>
    public void DeferFill(long key)
    {
        if (!fills.ContainsKey(key)) return;
        fillWanted.Remove(key);
        fillInFlight.Remove(key);
        FillsDeferred++;
        EnqueueFillCheck(key);
    }

    /// <summary>Merge records whose merged copy is now durable, for the storage worker to write.</summary>
    public List<(long Key, long Rowid)>? TakeReadyMergeRecords()
    {
        if (recordsReady.Count == 0) return null;
        var ready = new List<(long Key, long Rowid)>(recordsReady);
        recordsReady.Clear();
        return ready;
    }

    /// <summary>
    /// Storage acknowledged a local row. Future reloads may use disk and no longer need
    /// the remote fallback; this transition deliberately happens after, never at, enqueue.
    /// A merge record waiting for this revision, or an earlier one, may now be written.
    /// </summary>
    public void MarkPersisted(long key, long persistenceRevision = long.MaxValue)
    {
        localKeys.Add(key);
        RemoteOnly.Remove(key);
        remoteWanted.Remove(key);
        if (recordsAwaitingDurable.TryGetValue(key, out (long Rowid, long Revision) awaiting)
            && persistenceRevision >= awaiting.Revision)
        {
            recordsAwaitingDurable.Remove(key);
            recordsReady.Add((key, awaiting.Rowid));
        }
        if (networkAwaitingDurable.TryGetValue(key, out (long Version, long Revision) network)
            && persistenceRevision >= network.Revision)
        {
            networkAwaitingDurable.Remove(key);
            networkOutcomes.Add(new LodNetworkRefreshOutcome(key, LodNetworkRefreshEnd.Durable, network.Version));
        }
    }

    /// <summary>
    /// A network source accepted the request but reported that its row is not written yet.
    /// Release the transport attempt while preserving both the offered key and the owning
    /// thread's desire to retry it.
    /// </summary>
    public void MarkRetryable(long key)
    {
        if (fillInFlight.Remove(key)) EnqueueFillCheck(key);
        if (!RemoteOnly.Contains(key)) return;
        world.LoadFailed.Remove(key);
        world.LoadsInFlight.Remove(key);
        remoteWanted.Add(key);
    }

    /// <summary>
    /// The remote source will not supply this key - declined, gone, or unparseable. Clears
    /// the render path's wait on it: LodWorld.LoadsInFlight is set by TryGetForRender and
    /// otherwise only cleared by InstallLoaded, so without this a declined key stays
    /// "in flight" for the session, the mesh scheduler skips it, and its parent is pinned
    /// coarse forever. A fill that cannot be supplied leaves the client's own copy exactly as
    /// it was, including its ability to reload from local disk.
    /// </summary>
    public void MarkUnavailable(long key)
    {
        bool wasFill = DropFill(key);
        if (wasFill) FillsAbandoned++;
        if (networkTracked.Remove(key))
            networkOutcomes.Add(new LodNetworkRefreshOutcome(key, LodNetworkRefreshEnd.Failed, 0));
        bool wasOffered = RemoteOnly.Remove(key);
        remoteWanted.Remove(key);
        if (wasFill && !wasOffered) return;

        // Already resident (a local capture won the race): just stop waiting. Recording a
        // load failure would block reloading it after a future RAM eviction.
        if (world.Sections.ContainsKey(key))
        {
            world.LoadsInFlight.Remove(key);
            return;
        }

        world.InstallLoaded(key, null);
    }

    bool DropFill(long key)
    {
        bool had = fills.Remove(key);
        fillWanted.Remove(key);
        fillInFlight.Remove(key);
        if (eagerActive.Remove(key)) world.EvictionPins.Remove(key);
        return had;
    }

    public void Clear()
    {
        localKeys.Clear();
        RemoteOnly.Clear();
        remoteWanted.Clear();
        fills.Clear();
        fillChecks.Clear();
        fillChecksQueued.Clear();
        fillWanted.Clear();
        fillInFlight.Clear();
        foreach (long key in eagerActive) world.EvictionPins.Remove(key);
        eagerActive.Clear();
        siblingAccounted.Clear();
        recordsAwaitingDurable.Clear();
        recordsReady.Clear();
        networkTracked.Clear();
        networkAwaitingDurable.Clear();
        networkOutcomes.Clear();
        FillsAlreadyComplete = 0;
        FillsDeferred = 0;
        FillsAbandoned = 0;
    }
}
