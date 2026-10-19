namespace VintageHorizons.Checks;

/// <summary>
/// Which sections the client fetches from a server rather than from its own disk.
///
/// This is where the most expensive bug in the project's history lived. It survived three
/// diagnoses read off counters - fetch ordering, mesh throughput, uncoverable children -
/// before anyone printed the actual branch state and saw it in one shot. The lesson written
/// into DESIGN.md was "instrument the decision, do not infer it"; these checks are the
/// standing version of that.
/// </summary>
public static class RemoteKeyChecks
{
    public static void Run(Check c)
    {
        SynthesisedAncestors(c);
        LocalWins(c);
        WantedFollowsTheView(c);
        OnlyForgetWhatWasSent(c);
        LocalOfferOutcomes(c);
        SiblingRowsAtEveryLevel(c);
        FillModesFollowWhatTheClientHasSeen(c);
        LazyFillsWaitForTheView(c);
        EagerFillsLoadAndPinTheirTarget(c);
        FillRequestLifecycle(c);
        UnreadableOwnCopyIsAdoptedInstead(c);
        MergeRecordsWaitForTheDurableSave(c);
        NetworkRefreshMergesInsteadOfReplacing(c);
        Unavailable(c);
        NearestFirstMatchesTheOldOrder(c);
    }

    /// <summary>
    /// The sibling scan used to publish rewritten rows for level 0 only, and a held key was
    /// skipped outright - so a far L3 copy taken while pregen had filled one edge of it never
    /// changed again. That is the 18:23 screenshot's thin walls. Every level now reaches the
    /// key set, and a held key becomes a fill instead of being ignored.
    /// </summary>
    static void SiblingRowsAtEveryLevel(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);
        long heldL0 = LodWorld.SectionKey(0, 40, 40);
        long heldL3 = LodWorld.SectionKey(3, 5, 5);
        long missingL2 = LodWorld.SectionKey(2, 9, 9);
        remote.AddLocalKey(heldL0);
        remote.AddLocalKey(heldL3);

        LodSiblingDelta delta = remote.AddSiblingRows(new[]
        {
            new LodSiblingRow(heldL0, 10),
            new LodSiblingRow(heldL3, 11),
            new LodSiblingRow(missingL2, 12),
        });

        c.Eq(1, delta.Offered, "a key the client lacks is offered");
        c.Eq(2, delta.FillsQueued, "held keys at level 0 and level 3 both become fills");
        c.True(remote.RemoteOnly.Contains(missingL2), "the missing key is remote-only");
        c.False(remote.RemoteOnly.Contains(heldL3), "a held key never becomes remote-only");
        c.False(remote.WantFromRemote(heldL3),
            "a held key with a fill still reloads from local disk: the fill needs that copy");
        c.Eq(2, remote.PendingFills, "both fills are pending");

        LodSiblingDelta repeat = remote.AddSiblingRows(new[] { new LodSiblingRow(heldL3, 11) });
        c.Eq(0, repeat.Offered + repeat.FillsQueued, "the same row published again queues nothing");
    }

    /// <summary>
    /// The owner's rule (2026-09-12): the first time the client sees a held key's row it only
    /// fills what its copy lacks; a level-0 row the server rewrote after that wins column by
    /// column, which is how server-side snow refreshes reach held ground. Parents only fill.
    /// </summary>
    static void FillModesFollowWhatTheClientHasSeen(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);
        long l0 = LodWorld.SectionKey(0, 1, 1);
        long l1 = LodWorld.SectionKey(1, 2, 2);
        long recorded = LodWorld.SectionKey(0, 3, 3);
        foreach (long key in new[] { l0, l1, recorded }) remote.AddLocalKey(key);
        remote.AddMergeRecord(recorded, 50);

        remote.AddSiblingRows(new[]
        {
            new LodSiblingRow(l0, 10), new LodSiblingRow(l1, 11), new LodSiblingRow(recorded, 50),
        });
        c.Eq(LodMergeMode.FillMissing, remote.MergeModeFor(l0),
            "a level-0 row seen for the first time only fills");
        c.Eq(LodMergeMode.FillMissing, remote.MergeModeFor(l1), "so does a parent's");
        c.Eq(2, remote.PendingFills, "a row the cache records as already merged queues nothing");

        remote.AddSiblingRows(new[]
        {
            new LodSiblingRow(l0, 20), new LodSiblingRow(l1, 21), new LodSiblingRow(recorded, 51),
        });
        c.Eq(LodMergeMode.NewerWins, remote.MergeModeFor(l0),
            "a level-0 row rewritten after the client saw it wins");
        c.Eq(LodMergeMode.FillMissing, remote.MergeModeFor(l1), "a rewritten parent still only fills");
        c.Eq(LodMergeMode.NewerWins, remote.MergeModeFor(recorded),
            "a level-0 row rewritten after a recorded merge wins too, across a restart");
    }

    /// <summary>
    /// A first sighting only fills, so it waits for the view: the plan's "visible and nearest
    /// first". Loading every held section in the first session after the upgrade just to
    /// merge it would be a background storm for sections nobody is looking at.
    /// </summary>
    static void LazyFillsWaitForTheView(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);
        long key = LodWorld.SectionKey(3, 4, 4);
        remote.AddLocalKey(key);
        remote.AddSiblingRows(new[] { new LodSiblingRow(key, 7) });

        remote.PumpFills(16);
        c.Eq(0, remote.FillWanted().Length,
            "a fill does not read its source while its target is only on disk");
        c.False(world.EvictionPins.Contains(key), "and pins nothing");

        var partial = new LodSection();
        partial.SetColumn(0, new[] { LodSection.PackRun(0, 5, 0) });
        world.InstallLoaded(key, partial);
        remote.PumpFills(16);
        c.SeqEq(new[] { key }, remote.FillWanted(), "once the view has loaded it, the fill is wanted");

        // A complete copy has nothing to gain from a fill, so it is accounted without a read.
        long complete = LodWorld.SectionKey(3, 6, 6);
        remote.AddLocalKey(complete);
        world.InstallLoaded(complete, Fixtures.SolidSection());
        remote.AddSiblingRows(new[] { new LodSiblingRow(complete, 8) });
        remote.PumpFills(16);
        c.False(remote.FillWanted().Contains(complete), "a complete held copy is never read for a fill");
        c.Eq(1L, remote.FillsAlreadyComplete, "it is counted as already complete");
        c.Eq(1, remote.PendingFills, "and its fill is finished");
        c.SeqEq(new[] { (complete, 8L) }, remote.TakeReadyMergeRecords() ?? new(),
            "with nothing unsaved, its merge record is ready at once");
    }

    /// <summary>
    /// A rewritten level-0 row is fetched whether or not the view shows it, as the refresh it
    /// replaces was, so the target must be loaded - and kept loaded, or eviction could drop it
    /// between the load and the merge. The pins are bounded and every one is released.
    /// </summary>
    static void EagerFillsLoadAndPinTheirTarget(Check c)
    {
        var world = new LodWorld();
        var loads = new List<long>();
        world.RequestAsyncLoad = loads.Add;
        var remote = new LodRemoteKeySet(world);
        long key = LodWorld.SectionKey(0, 30, 30);
        remote.AddLocalKey(key);
        world.InstallStoredKey(0, 30, 30, applyToParent: false);
        remote.AddSiblingRows(new[] { new LodSiblingRow(key, 1) });
        remote.AddSiblingRows(new[] { new LodSiblingRow(key, 2) });
        c.Eq(LodMergeMode.NewerWins, remote.MergeModeFor(key), "the rewritten row wins");

        remote.PumpFills(16);
        c.SeqEq(new[] { key }, loads, "an eager fill starts loading its target from local disk");
        c.True(world.EvictionPins.Contains(key), "and pins it, so eviction cannot undo the load");
        c.Eq(0, remote.FillWanted().Length, "it is not read until the target is in RAM");

        world.InstallLoaded(key, Fixtures.SolidSection());
        remote.PumpFills(16);
        c.SeqEq(new[] { key }, remote.FillWanted(),
            "a loaded target makes the fill wanted, complete or not: a newer save may replace columns");

        remote.MarkLocalOfferAccepted(key);
        remote.NoteMerged(key, 2);
        c.False(world.EvictionPins.Contains(key), "the pin is released when the merge lands");
        c.Eq(0, remote.PendingFills, "and the fill is finished");

        var bounded = new LodWorld();
        bounded.RequestAsyncLoad = _ => { };
        var boundedRemote = new LodRemoteKeySet(bounded);
        var rows = new List<LodSiblingRow>();
        for (int i = 0; i < LodRemoteKeySet.MaxEagerFillsActive + 5; i++)
        {
            long k = LodWorld.SectionKey(0, 100 + i, 0);
            boundedRemote.AddLocalKey(k);
            bounded.InstallStoredKey(0, 100 + i, 0, applyToParent: false);
            rows.Add(new LodSiblingRow(k, 1));
        }
        boundedRemote.AddSiblingRows(rows);
        boundedRemote.AddSiblingRows(rows.Select(row => row with { Rowid = 2 }));
        boundedRemote.PumpFills(1000);
        c.Eq(LodRemoteKeySet.MaxEagerFillsActive, bounded.EvictionPins.Count,
            "eager loads, and so the RAM they pin, are capped");
        boundedRemote.Clear();
        c.Eq(0, bounded.EvictionPins.Count, "leaving the world releases every pin");
    }

    /// <summary>
    /// G6 for fills: every request ends merged, retried or abandoned, and no ending may touch
    /// the client's own copy or its ability to reload from local disk.
    /// </summary>
    static void FillRequestLifecycle(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);
        long key = LodWorld.SectionKey(2, 3, 3);
        remote.AddLocalKey(key);
        world.InstallLoaded(key, new LodSection());
        remote.AddSiblingRows(new[] { new LodSiblingRow(key, 5) });
        remote.PumpFills(16);
        c.SeqEq(new[] { key }, remote.FillWanted(), "a resident fill is wanted");
        c.True(remote.CanMergeInto(key), "a copy may be merged into while its fill is pending");

        remote.CompleteLocalOffer(key, LodLocalOfferOutcome.RetryableMiss);
        c.SeqEq(new[] { key }, remote.FillWanted(), "a read miss leaves the fill wanted");
        c.Eq(0, remote.Wanted().Length, "and never turns it into a remote-only request");

        remote.MarkLocalOfferAccepted(key);
        c.Eq(0, remote.FillWanted().Length, "decoder acceptance stops a duplicate read");

        // The target left RAM before the decode finished: nothing to merge into yet.
        world.Sections.Remove(key);
        remote.DeferFill(key);
        c.Eq(1, remote.PendingFills, "a deferred fill stays pending");
        remote.PumpFills(16);
        c.Eq(0, remote.FillWanted().Length, "and waits for the view to load its target again");
        world.InstallLoaded(key, new LodSection());
        remote.PumpFills(16);
        c.SeqEq(new[] { key }, remote.FillWanted(), "then is wanted again");

        // A newer row arrives while this one is being read: finishing the older one keeps it.
        remote.MarkLocalOfferAccepted(key);
        remote.AddSiblingRows(new[] { new LodSiblingRow(key, 9) });
        remote.NoteMerged(key, 5);
        c.Eq(1, remote.PendingFills, "merging an older row leaves the newer one pending");
        remote.PumpFills(16);
        c.SeqEq(new[] { key }, remote.FillWanted(), "and the newer row is read next");

        // An unreadable source abandons the fill and nothing else.
        remote.MarkUnavailable(key);
        c.Eq(0, remote.PendingFills, "an unreadable source abandons the fill");
        c.Eq(1L, remote.FillsAbandoned, "and says so");
        c.False(world.LoadFailed.Contains(key), "the client's own copy can still reload from disk");
        c.True(world.Sections.ContainsKey(key), "and is untouched");
    }

    /// <summary>
    /// The store deletes a row it cannot read. A fill waiting for that copy to load would then
    /// wait forever, so the key goes back to being an ordinary offer and is adopted whole.
    /// </summary>
    static void UnreadableOwnCopyIsAdoptedInstead(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);
        long key = LodWorld.SectionKey(1, 6, 6);
        remote.AddLocalKey(key);
        world.InstallStoredKey(1, 6, 6, applyToParent: false);
        remote.AddSiblingRows(new[] { new LodSiblingRow(key, 3) });
        world.LoadFailed.Add(key);

        remote.PumpFills(16);
        c.Eq(0, remote.PendingFills, "a fill whose own copy failed to load is dropped");
        c.True(remote.RemoteOnly.Contains(key), "the source's copy is offered for adoption instead");
        c.False(world.LoadFailed.Contains(key), "and the key is no longer marked as failed");
        c.True(remote.WantFromRemote(key), "so the next reload asks the source");
    }

    /// <summary>
    /// A merge record says the stored copy already contains a source row, so it may only be
    /// written once that copy is durable (G16). Written earlier, a crash between the merge and
    /// the save would skip the merge for good on the next start.
    /// </summary>
    static void MergeRecordsWaitForTheDurableSave(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);
        long key = LodWorld.SectionKey(0, 8, 8);
        remote.AddLocalKey(key);
        world.InstallLoaded(key, new LodSection());
        world.MarkChanged(key);
        remote.NoteMerged(key, 42);
        c.True(remote.TakeReadyMergeRecords() == null, "a record waits while the merged copy is unsaved");
        c.Eq(1, remote.MergeRecordsAwaitingSave, "and is counted as waiting");

        remote.MarkPersisted(key, persistenceRevision: world.PersistenceRevision(key) - 1);
        c.True(remote.TakeReadyMergeRecords() == null, "an older save cannot release it");
        remote.MarkPersisted(key, world.PersistenceRevision(key));
        c.SeqEq(new[] { (key, 42L) }, remote.TakeReadyMergeRecords() ?? new(),
            "the save that contains the merge releases its record");
        c.True(remote.TakeReadyMergeRecords() == null, "a record is handed over once");

        long clean = LodWorld.SectionKey(0, 9, 9);
        remote.AddLocalKey(clean);
        world.InstallLoaded(clean, new LodSection());
        remote.NoteMerged(clean, 7);
        c.SeqEq(new[] { (clean, 7L) }, remote.TakeReadyMergeRecords() ?? new(),
            "a merge into a copy with nothing unsaved is recorded at once");

        remote.NoteMerged(LodWorld.SectionKey(0, 10, 10), 0);
        c.True(remote.TakeReadyMergeRecords() == null, "a merge with no rowid writes no record");
    }

    /// <summary>
    /// A dedicated server's refresh used to replace the client's whole level-0 copy and route
    /// its reloads to the network until the replacement was durable. It is a newer-wins merge
    /// now: reloads stay local, and columns only the client has survive it.
    /// </summary>
    static void NetworkRefreshMergesInsteadOfReplacing(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);
        long held = LodWorld.SectionKey(0, 12, 13);
        long missing = LodWorld.SectionKey(0, 14, 15);
        remote.AddLocalKey(held);
        world.InstallLoaded(held, new LodSection());

        c.Eq(1, remote.AddRemoteUpdates(new[] { held, missing }), "a held key is merged");
        c.Eq(LodMergeMode.NewerWins, remote.MergeModeFor(held),
            "a level-0 refresh wins where both copies have a column");
        c.True(remote.RemoteOnly.Contains(missing), "a key the client lacks is simply offered");
        c.False(remote.WantFromRemote(held), "a held key keeps reloading from local disk");

        remote.PumpFills(16);
        c.SeqEq(new[] { held }, remote.FillWanted(), "its merge is wanted");
        remote.MarkRequested(new[] { held });
        c.Eq(0, remote.FillWanted().Length, "a sent request moves the fill to in flight");
        remote.MarkRetryable(held);
        remote.PumpFills(16);
        c.SeqEq(new[] { held }, remote.FillWanted(), "a not-yet reply makes it wanted again");
        remote.MarkRequested(new[] { held });
        remote.NoteMerged(held, 0);
        c.Eq(0, remote.PendingFills, "the merge finishes the fill");
        c.True(remote.TakeReadyMergeRecords() == null, "and records nothing: the network has no rowid");
    }

    /// <summary>
    /// THE regression test.
    ///
    /// Registering a fine key walks UPWARD, adding every ancestor to HasDataSet so the
    /// quadtree can descend to it. Those ancestors hold no data of their own - they are
    /// scaffolding. Testing HasDataSet to answer "can local disk supply this?" therefore
    /// says yes for keys local disk has never held.
    ///
    /// The consequence was not a missing section, it was a permanent one: the coarse key
    /// stayed out of RemoteOnly, was routed to a local store with no such row, came back
    /// null, and landed in LoadFailed - which nothing clears. Terrain that could never
    /// resolve, at any distance, showing as L5 nodes with two children stuck "load-failed"
    /// and an idle pipeline.
    /// </summary>
    static void SynthesisedAncestors(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);

        // A fine key from local disk. Registering it synthesises its whole ancestor chain.
        long fine = LodWorld.SectionKey(0, 8, 8);
        remote.AddLocalKey(fine);
        world.InstallStoredKey(0, 8, 8, applyToParent: false);

        long coarse = LodWorld.ParentKey(LodWorld.ParentKey(fine));
        c.True(world.HasDataSet.Contains(coarse),
            "registering a fine key synthesises its ancestors into HasDataSet");
        c.True(LodWorld.KeyLevel(coarse) > 0, "the synthesised ancestor is genuinely coarser");

        // The server offers that same coarse key, which it really does hold.
        int added = remote.AddRemoteKeys(new[] { coarse });

        c.Eq(1, added, "a server-held coarse key is accepted even though HasDataSet contains it");
        c.True(remote.RemoteOnly.Contains(coarse), "the coarse key is routed to the network, not local disk");
        c.True(remote.WantFromRemote(coarse), "asking to reload it goes to the network");

        // And a key already poisoned by the old bug must get its chance back.
        long poisoned = LodWorld.SectionKey(2, 30, 30);
        world.LoadFailed.Add(poisoned);
        world.LoadsInFlight.Add(poisoned);
        remote.AddRemoteKeys(new[] { poisoned });

        c.False(world.LoadFailed.Contains(poisoned), "a previously failed key is un-failed once a source exists");
        c.False(world.LoadsInFlight.Contains(poisoned), "a stranded in-flight key is released");
        c.True(remote.RemoteOnly.Contains(poisoned), "the recovered key is fetchable");
    }

    /// <summary>
    /// A key local disk really holds must stay a local read. The client's own capture is
    /// what it actually observed, including edits it witnessed, so it beats the server's.
    /// </summary>
    static void LocalWins(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);

        long key = LodWorld.SectionKey(0, 4, 4);
        remote.AddLocalKey(key);

        c.Eq(0, remote.AddRemoteKeys(new[] { key }), "a key local disk holds is not taken from the server");
        c.False(remote.RemoteOnly.Contains(key), "a locally-held key never becomes remote-only");
        c.False(remote.WantFromRemote(key), "reloading a locally-held key goes to disk");

        // Offering the same key twice must not count it twice.
        long fresh = LodWorld.SectionKey(0, 9, 9);
        c.Eq(1, remote.AddRemoteKeys(new[] { fresh }), "a new key counts once");
        c.Eq(0, remote.AddRemoteKeys(new[] { fresh }), "the same key offered again counts zero");
        c.Eq(1, remote.RemoteOnly.Count, "the remote set does not grow on a repeat offer");
    }

    static void WantedFollowsTheView(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);

        long a = LodWorld.SectionKey(0, 1, 1);
        long b = LodWorld.SectionKey(0, 2, 2);
        remote.AddRemoteKeys(new[] { a, b });

        c.Eq(0, remote.Wanted().Length, "nothing is wanted until the render path asks");

        remote.WantFromRemote(a);
        c.SeqEq(new[] { a }, remote.Wanted(), "only what the view asked for is wanted");

        // Wanted() must not clear: a key still in flight has to stay wanted, or it is
        // dropped between the request and the reply.
        c.SeqEq(new[] { a }, remote.Wanted(), "reading the wanted set does not consume it");

        remote.WantFromRemote(b);
        c.Eq(2, remote.Wanted().Length, "a second request joins the set");
    }

    /// <summary>
    /// The in-flight cap holds keys back, and only the ones actually sent may be forgotten.
    /// Forgetting the rest strands them: the render path has already put them in
    /// LoadsInFlight, where the mesh scheduler skips them, and nothing would ever re-ask.
    /// </summary>
    static void OnlyForgetWhatWasSent(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);

        long[] keys = Enumerable.Range(1, 5).Select(i => LodWorld.SectionKey(0, i, i)).ToArray();
        remote.AddRemoteKeys(keys);
        foreach (long key in keys) remote.WantFromRemote(key);

        // The transport sent only the first two.
        remote.MarkRequested(keys.Take(2));

        long[] stillWanted = remote.Wanted();
        c.Eq(3, stillWanted.Length, "keys held back by the cap stay wanted");
        c.False(stillWanted.Contains(keys[0]), "a sent key is forgotten");
        c.False(stillWanted.Contains(keys[1]), "the other sent key is forgotten");
        c.True(stillWanted.Contains(keys[2]), "an unsent key is still wanted");

        remote.MarkRequested(Array.Empty<long>());
        c.Eq(3, remote.Wanted().Length, "sending nothing forgets nothing");
    }

    /// <summary>
    /// A sibling-cache key can be listed before its blob becomes visible to the reader.
    /// The old caller put it in the taken array before reading, so a miss removed the
    /// wanted marker while LoadsInFlight remained set and the key could never be retried.
    /// </summary>
    static void LocalOfferOutcomes(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);
        long key = LodWorld.SectionKey(0, 21, 22);
        remote.AddRemoteKeys(new[] { key });
        remote.WantFromRemote(key);
        world.LoadsInFlight.Add(key);

        remote.CompleteLocalOffer(key, LodLocalOfferOutcome.RetryableMiss);
        c.SeqEq(new[] { key }, remote.Wanted(),
            "a transient local miss remains wanted for a later read");
        c.False(world.LoadsInFlight.Contains(key),
            "a transient local miss releases the attempt because no reader retained responsibility");

        world.LoadsInFlight.Add(key);
        remote.MarkLocalOfferAccepted(key);
        c.Eq(0, remote.Wanted().Length,
            "decoder acceptance prevents a duplicate sibling-cache submission");
        c.True(world.LoadsInFlight.Contains(key),
            "decoder acceptance retains responsibility until publication");

        world.InstallLoaded(key, new LodSection());
        remote.MarkInstalled(key);
        c.True(remote.RemoteOnly.Contains(key),
            "the offered route stays until persistence can acknowledge the adopted row");
        c.False(world.LoadsInFlight.Contains(key),
            "the successful install releases the in-flight marker");
        remote.MarkPersisted(key);
        c.False(remote.RemoteOnly.Contains(key),
            "the offered route retires only after a durable local acknowledgement");
        c.False(remote.WantFromRemote(key),
            "a durably persisted adopted row reloads from local disk");

        var networkWorld = new LodWorld();
        var networkRemote = new LodRemoteKeySet(networkWorld);
        long network = LodWorld.SectionKey(0, 27, 28);
        networkRemote.AddRemoteKeys(new[] { network });
        networkRemote.WantFromRemote(network);
        networkWorld.LoadsInFlight.Add(network);
        networkRemote.MarkRequested(new[] { network });
        c.Eq(0, networkRemote.Wanted().Length,
            "a network request transfers responsibility while its reply is pending");
        networkRemote.MarkRetryable(network);
        c.SeqEq(new[] { network }, networkRemote.Wanted(),
            "a retryable server reply restores the request to wanted");
        c.False(networkWorld.LoadsInFlight.Contains(network),
            "a retryable server reply releases the completed transport attempt");

        var corruptWorld = new LodWorld();
        var corruptRemote = new LodRemoteKeySet(corruptWorld);
        long corrupt = LodWorld.SectionKey(0, 23, 24);
        corruptRemote.AddRemoteKeys(new[] { corrupt });
        corruptRemote.WantFromRemote(corrupt);
        corruptWorld.LoadsInFlight.Add(corrupt);
        corruptRemote.CompleteLocalOffer(corrupt, LodLocalOfferOutcome.Unavailable);
        c.False(corruptWorld.LoadsInFlight.Contains(corrupt),
            "a permanently unreadable local blob releases the in-flight marker");
        c.True(corruptWorld.LoadFailed.Contains(corrupt),
            "a permanently unreadable local blob records explicit failure");

        var racedWorld = new LodWorld();
        var racedRemote = new LodRemoteKeySet(racedWorld);
        long raced = LodWorld.SectionKey(0, 25, 26);
        racedRemote.AddRemoteKeys(new[] { raced });
        racedRemote.WantFromRemote(raced);
        racedWorld.LoadsInFlight.Add(raced);
        racedWorld.Sections[raced] = new LodSection();
        racedRemote.CompleteLocalOffer(raced, LodLocalOfferOutcome.Unavailable);
        c.False(racedWorld.LoadsInFlight.Contains(raced),
            "a local-win race releases the obsolete remote request");
        c.False(racedWorld.LoadFailed.Contains(raced),
            "a local-win race does not poison the resident section");
    }

    static void Unavailable(Check c)
    {
        var world = new LodWorld();
        var remote = new LodRemoteKeySet(world);

        long key = LodWorld.SectionKey(0, 7, 7);
        remote.AddRemoteKeys(new[] { key });
        remote.WantFromRemote(key);
        world.LoadsInFlight.Add(key);

        remote.MarkUnavailable(key);

        c.False(remote.RemoteOnly.Contains(key), "a declined key stops being remote-only");
        c.Eq(0, remote.Wanted().Length, "a declined key stops being wanted");
        c.False(world.LoadsInFlight.Contains(key), "a declined key stops being in flight");
        c.True(world.LoadFailed.Contains(key), "a declined key is recorded as failed so it is not re-asked forever");

        // But if a local capture won the race and the section is already resident, recording
        // a failure would block reloading it after a future RAM eviction.
        var raced = new LodWorld();
        var racedRemote = new LodRemoteKeySet(raced);
        long resident = LodWorld.SectionKey(0, 3, 3);
        racedRemote.AddRemoteKeys(new[] { resident });
        raced.LoadsInFlight.Add(resident);
        raced.Sections[resident] = new LodSection();

        racedRemote.MarkUnavailable(resident);

        c.False(raced.LoadsInFlight.Contains(resident), "a resident section stops waiting");
        c.False(raced.LoadFailed.Contains(resident),
            "a resident section is not marked failed, so it can reload after eviction");
    }

    /// <summary>
    /// Pre-release review P2: the request pumps order wanted sections with reused storage and
    /// one distance per key. The order must be the one the old per-compare sort produced.
    /// </summary>
    static void NearestFirstMatchesTheOldOrder(Check c)
    {
        var random = new Random(160);
        var keys = new List<long>();
        for (int i = 0; i < 500; i++)
        {
            int level = random.Next(0, 7);
            keys.Add(LodWorld.SectionKey(level, random.Next(7900 >> level, 8100 >> level),
                random.Next(7900 >> level, 8100 >> level)));
        }
        const double x = 512_100, z = 511_950;
        double[] expected = keys.Select(k => LodWorld.NearestDistanceSqTo(k, x, z)).OrderBy(d => d).ToArray();

        var order = new LodNearestFirst();
        order.Keys.AddRange(keys);
        order.Sort(x, z);
        c.SeqEq(expected, order.Keys.Select(k => LodWorld.NearestDistanceSqTo(k, x, z)).ToArray(),
            "the reused sort gives the same nearest-first distances as the old comparison");
        c.True(order.Keys.OrderBy(k => k).SequenceEqual(keys.OrderBy(k => k)),
            "and holds exactly the keys it was given");

        order.Keys.Clear();
        order.Keys.AddRange(keys.Take(3));
        order.Sort(x, z);
        c.Eq(3, order.Keys.Count, "a shorter list after a longer one reuses the buffer without leftovers");
    }
}
