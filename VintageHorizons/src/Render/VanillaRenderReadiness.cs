namespace VintageHorizons;

/// <summary>
/// Pure, renderer-owned model for the vanilla-chunk handoff. It deliberately knows
/// nothing about OpenGL or ICoreClientAPI: engine observations become publication
/// proposals, and committed CPU ownership changes only after the renderer accepts the
/// matching GPU update.
/// </summary>
internal sealed class VanillaRenderReadiness
{
    const int ChunkBlocks = 32;
    const byte QueuedFlag = 1;
    const byte PublicationPendingFlag = 2;
    const byte ObservationQueuedFlag = 4;

    /// <summary>
    /// This cell owns its ground for every CPU aggregate but must never own a mask texel.
    /// Air is the only producer today: a column counts as owned only when all of its chunks
    /// do and every column has sky, so refusing air ownership outright collapsed 0.3.3 - but
    /// cached terrain overshoots the real surface into those air chunks, and vanilla draws
    /// nothing there, so a suppressing texel removes the only draw (G40).
    ///
    /// It lives here rather than in the renderer's publication call because the mask is also
    /// rebuilt wholesale - at creation and on every window change - and a rebuild has no
    /// chunk lookup to consult. Storing the bit is what lets <see cref="WriteMask"/> honour
    /// the same exclusion the incremental path applies.
    /// </summary>
    const byte MaskExcludedFlag = 8;

    /// <summary>
    /// A cell the engine has just announced, rechecked at the front of every frame's probes
    /// until vanilla has its mesh up. See <see cref="Watch"/>.
    /// </summary>
    const byte WatchedFlag = 16;

    readonly int capacity;
    readonly int capacityMask;
    readonly int verticalChunks;
    readonly long[] columnTags;
    readonly int[] columnReady;
    readonly byte[] states;
    readonly byte[] flags;
    readonly int[] observedFrames;
    readonly long[] pendingPublicationTokens;

    /// <summary>
    /// The engine's own range-cull inputs for each cell, cached at probe time so the per-frame
    /// loss sweep is pure arithmetic.
    ///
    /// <para>
    /// This is what lets the sweep run every time the player crosses a block boundary without
    /// reintroducing G45: reading the sphere needs the chunk, taking the chunk needs the
    /// client's chunk lock, and doing that for every committed cell every frame would be the
    /// join-time stall all over again. The probe already holds the chunk when it decides
    /// ownership, so the values are copied out once there and re-tested for free afterwards.
    /// </para>
    /// <para>
    /// NaN means "no sphere recorded", and the sweep skips those cells entirely rather than
    /// guessing - a cell with no recorded sphere keeps whatever the conservative bound decided
    /// for it. The values go stale if a chunk re-tesselates into a different shape; the
    /// once-a-second full revalidation re-probes every committed cell, which bounds that.
    /// </para>
    /// </summary>
    readonly float[] sphereCentreX;
    readonly float[] sphereCentreZ;
    readonly byte[] lodMasks;
    readonly VanillaChunkCell[] candidates;
    readonly long[] candidateFrames;
    readonly VanillaChunkCell[] observations;
    readonly Dictionary<long, int>[] readyCounts;

    /// <summary>
    /// When the engine announced each cell, in the caller's milliseconds; 0 for none. It is
    /// what a watch expires against, and what the confirmation delay is measured from.
    /// </summary>
    readonly long[] announcedMs;
    readonly VanillaChunkCell[] watches;

    int candidateHead;
    int candidateCount;
    int observationHead;
    int observationCount;
    int watchHead;
    int watchCount;
    int minChunkX;
    int minChunkZ;
    int width;
    int depth;
    long nextPublicationToken;

    public int PendingCandidates => candidateCount;
    public int PendingObservations => observationCount;
    public int PendingWatches => watchCount;
    public long WatchesStarted { get; private set; }
    public long WatchesPromoted { get; private set; }
    public long WatchesExpired { get; private set; }
    public long WatchesRefused { get; private set; }
    public int HorizontalCapacity => capacity;
    public int VerticalChunks => verticalChunks;

    /// <summary>
    /// Raised with a column's world chunk coordinates immediately before its ring slot is
    /// wiped, so a mirror of this ring can drop the same column. Every wipe funnels through
    /// <see cref="ClearSlot"/>, which is what makes one hook sufficient.
    /// </summary>
    public Action<int, int>? ColumnEvicted { get; set; }
    public int ActiveMinChunkX => minChunkX;
    public int ActiveMinChunkZ => minChunkZ;
    public int ActiveWidth => width;
    public int ActiveDepth => depth;
    public int ActiveCells => checked(width * depth * verticalChunks);
    public long TrackedArrayBytes =>
        columnTags.LongLength * sizeof(long)
        + states.LongLength + flags.LongLength + lodMasks.LongLength
        + observedFrames.LongLength * sizeof(int)
        + (sphereCentreX.LongLength + sphereCentreZ.LongLength) * sizeof(float)
        + pendingPublicationTokens.LongLength * sizeof(long)
        + candidateFrames.LongLength * sizeof(long)
        + announcedMs.LongLength * sizeof(long)
        + (candidates.LongLength + observations.LongLength + watches.LongLength) * (sizeof(int) * 3L);
    public int CommittedReadyCells { get; private set; }
    public long CandidateEventsAccepted { get; private set; }
    public long CandidateEventsCoalesced { get; private set; }
    public long CandidateEventsDropped { get; private set; }
    public long ScheduledCandidatesAccepted { get; private set; }
    public long ScheduledCandidatesCoalesced { get; private set; }

    public VanillaRenderReadiness(int horizontalCapacity, int verticalChunkCount)
    {
        if (horizontalCapacity <= 0 || (horizontalCapacity & (horizontalCapacity - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(horizontalCapacity),
                "Horizontal capacity must be a positive power of two.");
        if (verticalChunkCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(verticalChunkCount));

        capacity = horizontalCapacity;
        capacityMask = horizontalCapacity - 1;
        verticalChunks = verticalChunkCount;
        int columns = checked(horizontalCapacity * horizontalCapacity);
        int cells = checked(columns * verticalChunkCount);
        columnTags = new long[columns];
        Array.Fill(columnTags, -1L);
        columnReady = new int[columns];
        states = new byte[cells];
        flags = new byte[cells];
        observedFrames = new int[cells];
        sphereCentreX = new float[cells];
        sphereCentreZ = new float[cells];
        Array.Fill(sphereCentreX, float.NaN);
        Array.Fill(sphereCentreZ, float.NaN);
        lodMasks = new byte[cells];
        pendingPublicationTokens = new long[cells];
        candidates = new VanillaChunkCell[cells];
        candidateFrames = new long[cells];
        observations = new VanillaChunkCell[cells];
        announcedMs = new long[cells];
        watches = new VanillaChunkCell[cells];
        readyCounts = new Dictionary<long, int>[LodWorld.MaxLevel + 1];
        for (int level = 0; level < readyCounts.Length; level++)
            readyCounts[level] = new Dictionary<long, int>();
    }

    /// <summary>
    /// Changes the valid camera-centred window. Cells leaving it are synchronously
    /// invalidated so CPU whole-section counts cannot retain stale vanilla ownership.
    /// Cells entering it remain Unknown until events or incremental seeding enqueue them.
    /// </summary>
    public bool SetWindow(int newMinChunkX, int newMinChunkZ, int newWidth, int newDepth,
        long renderFrame = 0)
    {
        if (newMinChunkX < 0 || newMinChunkZ < 0)
            throw new ArgumentOutOfRangeException(nameof(newMinChunkX));
        if (newWidth <= 0 || newWidth > capacity || newDepth <= 0 || newDepth > capacity)
            throw new ArgumentOutOfRangeException(nameof(newWidth));

        bool changed = newMinChunkX != minChunkX || newMinChunkZ != minChunkZ
            || newWidth != width || newDepth != depth;
        if (!changed) return false;

        int oldMaxX = minChunkX + width;
        int oldMaxZ = minChunkZ + depth;
        int newMaxX = newMinChunkX + newWidth;
        int newMaxZ = newMinChunkZ + newDepth;

        for (int z = minChunkZ; z < oldMaxZ; z++)
        {
            for (int x = minChunkX; x < oldMaxX; x++)
            {
                if (x < newMinChunkX || x >= newMaxX || z < newMinChunkZ || z >= newMaxZ)
                    ClearColumn(x, z);
            }
        }

        int oldMinX = minChunkX;
        int oldMinZ = minChunkZ;
        int oldWidth = width;
        int oldDepth = depth;

        minChunkX = newMinChunkX;
        minChunkZ = newMinChunkZ;
        width = newWidth;
        depth = newDepth;

        // Columns the window just gained are the streaming frontier, and while moving they
        // are exactly the ground the player is heading into. Queue them here rather than
        // waiting for a discovery cursor to sweep the whole window and reach them: at
        // flight speed the window moves again long before a sweep gets that far, so the
        // ground ahead would be the last thing ever probed.
        for (int z = newMinChunkZ; z < newMinChunkZ + newDepth; z++)
        {
            for (int x = newMinChunkX; x < newMinChunkX + newWidth; x++)
            {
                bool wasInside = x >= oldMinX && x < oldMinX + oldWidth
                    && z >= oldMinZ && z < oldMinZ + oldDepth;
                if (wasInside) continue;
                for (int y = 0; y < verticalChunks; y++)
                    EnqueueCandidate(new VanillaChunkCell(x, y, z), renderFrame);
            }
        }

        return true;
    }

    /// <summary>Queues at most <paramref name="maxCells"/> cells for initial discovery.</summary>
    public int SeedUnknownCells(ref int cursor, int maxCells, long renderFrame = 0)
    {
        if (maxCells <= 0 || width == 0 || depth == 0) return 0;
        int total = checked(width * depth * verticalChunks);
        int accepted = 0;
        int examined = 0;
        while (cursor < total && examined++ < maxCells)
        {
            int value = cursor++;
            int y = value % verticalChunks;
            value /= verticalChunks;
            int x = minChunkX + value % width;
            int z = minChunkZ + value / width;
            if (EnqueueCandidate(new VanillaChunkCell(x, y, z), renderFrame)) accepted++;
        }
        return accepted;
    }

    /// <summary>
    /// Incrementally requeues committed cells around the expected vanilla streaming
    /// frontier. The cursor enumerates ring perimeters directly, so an interior square
    /// cannot consume the boundary-first scan budget.
    /// </summary>
    public int QueueReadyShell(int centerChunkX, int centerChunkZ, int innerRadius,
        int outerRadius, ref int cursor, int maxCells, long renderFrame = 0)
    {
        if (maxCells <= 0 || outerRadius < 0) return 0;
        innerRadius = Math.Clamp(innerRadius, 0, outerRadius);
        int columns = ShellColumnCount(innerRadius, outerRadius);
        int total = checked(columns * verticalChunks);
        if (cursor >= total) cursor = 0;

        int accepted = 0;
        int examined = 0;
        while (cursor < total && examined++ < maxCells)
        {
            int value = cursor++;
            int y = value % verticalChunks;
            int columnOrdinal = value / verticalChunks;
            ShellColumn(centerChunkX, centerChunkZ, innerRadius, outerRadius,
                columnOrdinal, out int x, out int z);
            var cell = new VanillaChunkCell(x, y, z);
            if (State(cell) == VanillaReadinessState.VanillaReady
                && EnqueueCandidate(cell, renderFrame))
                accepted++;
        }
        return accepted;
    }

    /// <summary>
    /// Queues every committed cell for revalidation. Cursor-based sweeps kept failing to
    /// reach ground the camera had moved away from, and each failure left the mask hiding
    /// cached terrain nothing else drew - a hole that outlived the movement that caused it.
    /// Re-confirming the whole committed set on a fixed interval removes the class of bug
    /// rather than the instance: ownership can then be wrong for at most that interval, no
    /// matter how the camera moved to make it wrong.
    ///
    /// The committed set is small - roughly 1,700 cells at a 256-block view distance - and
    /// a probe costs well under a microsecond, so a full pass is a fraction of one frame's
    /// probe budget and is spread across frames by that budget in any case.
    /// </summary>
    public int QueueAllReadyCells(long renderFrame = 0)
    {
        int queued = 0;
        for (int slot = 0; slot < columnTags.Length; slot++)
        {
            if (columnTags[slot] == -1L || columnReady[slot] == 0) continue;
            UnpackColumn(columnTags[slot], out int chunkX, out int chunkZ);
            if (chunkX < minChunkX || chunkX >= minChunkX + width
                || chunkZ < minChunkZ || chunkZ >= minChunkZ + depth) continue;

            int baseIndex = slot * verticalChunks;
            for (int y = 0; y < verticalChunks; y++)
            {
                if ((VanillaReadinessState)states[baseIndex + y] != VanillaReadinessState.VanillaReady)
                    continue;
                if (EnqueueCandidate(new VanillaChunkCell(chunkX, y, chunkZ), renderFrame)) queued++;
            }
        }
        return queued;
    }

    /// <summary>
    /// Every committed cell inside the active window, appended to <paramref name="into"/>. The
    /// walk <see cref="QueueAllReadyCells"/> makes, without queueing anything: the handoff gap
    /// scan asks the engine about the committed set in the frame it is used.
    /// </summary>
    public int CollectCommittedCells(List<VanillaChunkCell> into)
    {
        int found = 0;
        for (int slot = 0; slot < columnTags.Length; slot++)
        {
            if (columnTags[slot] == -1L || columnReady[slot] == 0) continue;
            UnpackColumn(columnTags[slot], out int chunkX, out int chunkZ);
            if (chunkX < minChunkX || chunkX >= minChunkX + width
                || chunkZ < minChunkZ || chunkZ >= minChunkZ + depth) continue;

            int baseIndex = slot * verticalChunks;
            for (int y = 0; y < verticalChunks; y++)
            {
                if ((VanillaReadinessState)states[baseIndex + y] != VanillaReadinessState.VanillaReady)
                    continue;
                into.Add(new VanillaChunkCell(chunkX, y, chunkZ));
                found++;
            }
        }
        return found;
    }

    /// <summary>
    /// Low-cadence interior maintenance under an examined-cell budget. This deliberately
    /// ignores cell state. A ready-only sweep can lose ownership but never gain it: a
    /// chunk that finishes tessellating after its dirty event was probed leaves its cell
    /// Pending, and nothing revisits it. A 2026-08-18 route measured exactly that - one
    /// 15-second interval spent 432,744 probes re-confirming 1,727 already-ready cells
    /// and none on the 1,801 pending ones, so ownership could only advance when camera
    /// movement reset the discovery cursor.
    /// </summary>
    public int QueueMaintenanceCells(ref int cursor, int maxCells, long renderFrame = 0)
    {
        if (maxCells <= 0 || width == 0 || depth == 0) return 0;
        int total = checked(width * depth * verticalChunks);
        if (cursor >= total) cursor = 0;
        int accepted = 0;
        int examined = 0;
        while (cursor < total && examined++ < maxCells)
        {
            int value = cursor++;
            int y = value % verticalChunks;
            value /= verticalChunks;
            int x = minChunkX + value % width;
            int z = minChunkZ + value / width;
            if (EnqueueCandidate(new VanillaChunkCell(x, y, z), renderFrame)) accepted++;
        }
        return accepted;
    }

    /// <summary>
    /// Admits one cell for probing. <paramref name="engineEvent"/> separates candidates the
    /// client actually announced from the tracker's own scheduled sweeps, because a single
    /// combined counter is dominated by maintenance and hides whether engine events are
    /// arriving, coalescing, or being refused at all.
    /// </summary>
    public bool EnqueueCandidate(VanillaChunkCell cell, long renderFrame = 0, bool engineEvent = false)
    {
        if (!TryGetCellIndex(cell, prepareColumn: true, out int index))
        {
            if (engineEvent) CandidateEventsDropped++;
            return false;
        }

        if ((flags[index] & QueuedFlag) != 0)
        {
            if (engineEvent) CandidateEventsCoalesced++;
            else ScheduledCandidatesCoalesced++;
            return false;
        }
        if (candidateCount == candidates.Length)
        {
            if (engineEvent) CandidateEventsDropped++;
            return false;
        }

        if ((VanillaReadinessState)states[index] == VanillaReadinessState.Unknown)
            states[index] = (byte)VanillaReadinessState.Pending;
        flags[index] |= QueuedFlag;
        int queueIndex = (candidateHead + candidateCount) % candidates.Length;
        candidates[queueIndex] = cell;
        candidateFrames[queueIndex] = renderFrame;
        candidateCount++;
        if (engineEvent) CandidateEventsAccepted++;
        else ScheduledCandidatesAccepted++;
        return true;
    }

    /// <summary>
    /// Returns one still-current candidate. Stale entries left by ring movement are
    /// discarded without making the new aliased cell look queued.
    /// </summary>
    public bool TryDequeueCandidate(out VanillaChunkCell cell)
    {
        while (candidateCount > 0)
        {
            cell = candidates[candidateHead];
            candidateHead = (candidateHead + 1) % candidates.Length;
            candidateCount--;
            if (!TryGetCellIndex(cell, prepareColumn: false, out int index)) continue;
            if ((flags[index] & QueuedFlag) == 0) continue;
            flags[index] &= unchecked((byte)~QueuedFlag);
            return true;
        }
        cell = default;
        return false;
    }

    /// <summary>
    /// Moves first-true observations into the probe queue only after a later render
    /// frame. The separate fixed queue prevents an observation from immediately
    /// re-probing itself repeatedly in the frame that first saw it.
    /// </summary>
    public int PromoteObserved(long renderFrame, int maxCells)
    {
        int promoted = 0;
        int examined = 0;
        while (observationCount > 0 && examined++ < maxCells)
        {
            VanillaChunkCell cell = observations[observationHead];
            bool resolved = TryGetCellIndex(cell, prepareColumn: false, out int index);
            if (!resolved
                || (flags[index] & ObservationQueuedFlag) == 0
                || (VanillaReadinessState)states[index] != VanillaReadinessState.ObservedRendered)
            {
                // Dropping the entry has to drop the flag with it, or the cell is left marked
                // as queued while sitting in no queue - and `QueueObservation` then refuses to
                // queue it ever again, silently retiring the deferred-observation path for that
                // cell for the rest of the session. It reaches ObservedRendered and waits for a
                // sweep to notice instead. Safety is unaffected (the two-observation rule is
                // enforced by the frame stamp, not by this flag), which is exactly why it went
                // unseen: the symptom is slower ownership, not wrong ownership.
                //
                // Guarded on the lookup rather than on the index, because an unresolved cell
                // reports index -1 and clearing a flag at a negative or aliased index would be
                // a far worse bug than the one being fixed.
                if (resolved) flags[index] &= unchecked((byte)~ObservationQueuedFlag);
                PopObservation();
                continue;
            }
            if (renderFrame <= observedFrames[index]) break;
            if (candidateCount == candidates.Length) break;

            PopObservation();
            flags[index] &= unchecked((byte)~ObservationQueuedFlag);
            if (EnqueueCandidate(cell, renderFrame)) promoted++;
        }
        return promoted;
    }

    /// <summary>
    /// Records that the engine has just announced this cell and puts it on the short list of
    /// cells rechecked at the front of every frame's probes (<see cref="PromoteWatched"/>).
    ///
    /// <para>
    /// The engine announces a chunk before it has tesselated it, so the probe that answers the
    /// announcement nearly always finds nothing to hand over yet and leaves the cell Pending in
    /// no queue. Before this list it then waited for the interior sweep to come round - about
    /// 190 frames across a 256-block window - while vanilla was already drawing the chunk and
    /// the cache was drawing it too. That is the "brief fight where vanilla has just loaded",
    /// and when the cached geometry there was a coarse stand-in it showed as floating slabs.
    /// </para>
    /// <para>
    /// Only the timing of the recheck changes. Ownership is still granted by the same two
    /// observations, range rule and geometry gate, so nothing can be handed over sooner than
    /// the engine is actually drawing it. A committed cell is not watched: it has nothing to
    /// wait for. The first announcement's time is kept, so a chunk dirtied again while it is
    /// still waiting is measured from when it first arrived.
    /// </para>
    /// </summary>
    public bool Watch(VanillaChunkCell cell, long nowMs)
    {
        if (!TryGetCellIndex(cell, prepareColumn: false, out int index)) return false;
        if ((VanillaReadinessState)states[index] == VanillaReadinessState.VanillaReady) return false;
        if (announcedMs[index] == 0) announcedMs[index] = Math.Max(1, nowMs);
        if ((flags[index] & WatchedFlag) != 0) return true;
        if (watchCount == watches.Length)
        {
            WatchesRefused++;
            return false;
        }

        flags[index] |= WatchedFlag;
        watches[(watchHead + watchCount) % watches.Length] = cell;
        watchCount++;
        WatchesStarted++;
        return true;
    }

    /// <summary>
    /// Takes a cell off the watch list, leaving its announcement time for the caller to take.
    /// The ring entry is dropped lazily the next time <see cref="PromoteWatched"/> reaches it.
    /// </summary>
    public void Unwatch(VanillaChunkCell cell)
    {
        if (!TryGetCellIndex(cell, prepareColumn: false, out int index)) return;
        flags[index] &= unchecked((byte)~WatchedFlag);
    }

    public bool IsWatched(VanillaChunkCell cell) =>
        TryGetCellIndex(cell, prepareColumn: false, out int index)
        && (flags[index] & WatchedFlag) != 0;

    /// <summary>
    /// When the engine announced this cell, in the caller's milliseconds, and forgets it; 0 when
    /// it was never announced. Taken once - at the commit it was waiting for, or when a watch
    /// ends without one - so a much later commit cannot report a wait it did not have.
    /// </summary>
    public long TakeAnnouncement(VanillaChunkCell cell)
    {
        if (!TryGetCellIndex(cell, prepareColumn: false, out int index)) return 0;
        long announced = announcedMs[index];
        announcedMs[index] = 0;
        return announced;
    }

    /// <summary>
    /// Puts watched cells at the FRONT of the probe queue, examining at most
    /// <paramref name="maxCells"/> of them, and drops the ones that no longer need it:
    /// committed, unwatched, out of the window, or older than <paramref name="limitMs"/> since
    /// their announcement. A dropped cell falls back to the ordinary sweeps, which is exactly
    /// the behaviour before this list existed.
    ///
    /// <para>
    /// With <paramref name="promote"/> false it only drops, so the list cannot fill while fast
    /// confirmation is switched off, and announcement times stay measurable for comparison.
    /// </para>
    /// </summary>
    public int PromoteWatched(long renderFrame, long nowMs, long limitMs, int maxCells, bool promote)
    {
        int promoted = 0;
        int budget = Math.Min(maxCells, watchCount);
        for (int n = 0; n < budget; n++)
        {
            VanillaChunkCell cell = watches[watchHead];
            watchHead = (watchHead + 1) % watches.Length;
            watchCount--;

            if (!TryGetCellIndex(cell, prepareColumn: false, out int index)
                || (flags[index] & WatchedFlag) == 0) continue;

            // No announcement left means someone already took it - the watch is over whatever the
            // flag says, and it must not be measured again.
            bool ready = (VanillaReadinessState)states[index] == VanillaReadinessState.VanillaReady;
            long announced = announcedMs[index];
            if (ready || announced == 0 || nowMs - announced > limitMs)
            {
                if (!ready && announced != 0)
                {
                    WatchesExpired++;
                    announcedMs[index] = 0;
                }
                flags[index] &= unchecked((byte)~WatchedFlag);
                continue;
            }

            if (promote && EnqueueCandidateFront(cell, renderFrame))
            {
                promoted++;
                WatchesPromoted++;
            }
            watches[(watchHead + watchCount) % watches.Length] = cell;
            watchCount++;
        }
        return promoted;
    }

    /// <summary>
    /// Admits a cell at the head of the probe queue so it is probed this frame, ahead of sweep
    /// and revalidation work. A cell already queued keeps its place rather than appearing twice.
    /// </summary>
    bool EnqueueCandidateFront(VanillaChunkCell cell, long renderFrame)
    {
        if (!TryGetCellIndex(cell, prepareColumn: false, out int index)) return false;
        if ((flags[index] & QueuedFlag) != 0) return false;
        if (candidateCount == candidates.Length) return false;

        if ((VanillaReadinessState)states[index] == VanillaReadinessState.Unknown)
            states[index] = (byte)VanillaReadinessState.Pending;
        flags[index] |= QueuedFlag;
        candidateHead = (candidateHead - 1 + candidates.Length) % candidates.Length;
        candidates[candidateHead] = cell;
        candidateFrames[candidateHead] = renderFrame;
        candidateCount++;
        ScheduledCandidatesAccepted++;
        return true;
    }

    /// <summary>
    /// Applies one public IsChunkRendered observation. A true value needs two
    /// observations separated by a render frame. A false value proposes immediate loss
    /// when the cell was committed. Neither path changes CPU aggregates yet.
    /// </summary>
    public bool Observe(VanillaChunkCell cell, bool rendered, long renderFrame,
        out VanillaReadinessPublication publication)
    {
        publication = default;
        if (!TryGetCellIndex(cell, prepareColumn: false, out int index)) return false;

        VanillaReadinessState state = (VanillaReadinessState)states[index];
        if (!rendered)
        {
            observedFrames[index] = 0;
            if (state == VanillaReadinessState.VanillaReady
                && (flags[index] & PublicationPendingFlag) == 0)
            {
                flags[index] |= PublicationPendingFlag;
                long token = NextPublicationToken();
                pendingPublicationTokens[index] = token;
                publication = new VanillaReadinessPublication(cell, Ready: false, Token: token);
                return true;
            }
            // Leaving ObservedRendered means leaving the observation queue. Clearing the flag
            // here is the direct statement of that; `PromoteObserved` then discards the stale
            // ring entry on sight rather than treating the cell as still queued.
            flags[index] &= unchecked((byte)~ObservationQueuedFlag);
            states[index] = (byte)VanillaReadinessState.Pending;
            return false;
        }

        if (state == VanillaReadinessState.VanillaReady) return false;
        if (state == VanillaReadinessState.ObservedRendered
            && renderFrame > observedFrames[index]
            && (flags[index] & PublicationPendingFlag) == 0)
        {
            flags[index] &= unchecked((byte)~ObservationQueuedFlag);
            flags[index] |= PublicationPendingFlag;
            long token = NextPublicationToken();
            pendingPublicationTokens[index] = token;
            publication = new VanillaReadinessPublication(cell, Ready: true, Token: token);
            return true;
        }

        bool firstObservation = state != VanillaReadinessState.ObservedRendered;
        states[index] = (byte)VanillaReadinessState.ObservedRendered;
        observedFrames[index] = RenderFrameStamp(renderFrame);
        if (firstObservation) QueueObservation(cell, index);
        return false;
    }

    /// <summary>
    /// Completes the atomic publication handshake. Rejection never advances CPU
    /// ownership; the caller may retry or disable the hybrid path on a failed clear.
    /// </summary>
    public bool ResolvePublication(VanillaReadinessPublication publication, bool accepted)
    {
        if (!TryGetCellIndex(publication.Cell, prepareColumn: false, out int index)) return false;
        if ((flags[index] & PublicationPendingFlag) == 0) return false;
        if (pendingPublicationTokens[index] != publication.Token) return false;
        flags[index] &= unchecked((byte)~PublicationPendingFlag);
        pendingPublicationTokens[index] = 0;

        bool wasReady = (VanillaReadinessState)states[index] == VanillaReadinessState.VanillaReady;
        if (!accepted)
        {
            if (publication.Ready && !wasReady)
            {
                states[index] = (byte)VanillaReadinessState.ObservedRendered;
                QueueObservation(publication.Cell, index);
            }
            else if (!publication.Ready && wasReady)
                states[index] = (byte)VanillaReadinessState.VanillaReady;
            return false;
        }

        if (publication.Ready == wasReady) return true;
        if (publication.Ready)
        {
            states[index] = (byte)VanillaReadinessState.VanillaReady;
            ChangeReadyCounts(publication.Cell, 1);
            columnReady[index / verticalChunks]++;
            CommittedReadyCells++;
        }
        else
        {
            states[index] = (byte)VanillaReadinessState.Pending;
            ChangeReadyCounts(publication.Cell, -1);
            columnReady[index / verticalChunks]--;
            CommittedReadyCells--;
        }
        return true;
    }

    public VanillaReadinessState State(VanillaChunkCell cell)
    {
        return TryGetCellIndex(cell, prepareColumn: false, out int index)
            ? (VanillaReadinessState)states[index]
            : VanillaReadinessState.Unknown;
    }

    /// <summary>
    /// Records whether this cell is excluded from the GPU mask while keeping every CPU
    /// aggregate it already contributes to. Returns whether the stored bit changed, so a
    /// resync can count how often the incremental path and the authoritative chunk lookup
    /// disagreed. A cell outside the active window stores nothing.
    /// </summary>
    public bool SetMaskExcluded(VanillaChunkCell cell, bool excluded)
    {
        if (!TryGetCellIndex(cell, prepareColumn: false, out int index)) return false;
        bool was = (flags[index] & MaskExcludedFlag) != 0;
        if (was == excluded) return false;
        if (excluded) flags[index] |= MaskExcludedFlag;
        else flags[index] &= unchecked((byte)~MaskExcludedFlag);
        return true;
    }

    public bool IsMaskExcluded(VanillaChunkCell cell) =>
        TryGetCellIndex(cell, prepareColumn: false, out int index)
        && (flags[index] & MaskExcludedFlag) != 0;

    /// <summary>
    /// Records the engine's range-cull inputs for a cell, read from the chunk the probe is
    /// already holding. Storing them is not itself an ownership claim - the publication
    /// handshake still decides that - so it is safe to record before a proposal is accepted.
    /// </summary>
    public bool SetDrawSphere(VanillaChunkCell cell, float centreX, float centreZ, int lodMask)
    {
        if (!TryGetCellIndex(cell, prepareColumn: false, out int index)) return false;
        sphereCentreX[index] = centreX;
        sphereCentreZ[index] = centreZ;
        lodMasks[index] = (byte)(lodMask & 0xFF);
        return true;
    }

    /// <summary>
    /// Forgets a cell's recorded sphere, so the loss sweep stops re-testing a value nothing
    /// re-confirms. Called whenever a decision falls back to the conservative bound: a cell
    /// that lost its mesh must not keep answering from the mesh it used to have.
    /// </summary>
    public void ClearDrawSphere(VanillaChunkCell cell)
    {
        if (!TryGetCellIndex(cell, prepareColumn: false, out int index)) return;
        sphereCentreX[index] = float.NaN;
        sphereCentreZ[index] = float.NaN;
        lodMasks[index] = 0;
    }

    public bool TryGetDrawSphere(VanillaChunkCell cell,
        out float centreX, out float centreZ, out int lodMask)
    {
        centreX = centreZ = float.NaN;
        lodMask = 0;
        if (!TryGetCellIndex(cell, prepareColumn: false, out int index)) return false;
        if (float.IsNaN(sphereCentreX[index])) return false;
        centreX = sphereCentreX[index];
        centreZ = sphereCentreZ[index];
        lodMask = lodMasks[index];
        return lodMask != 0;
    }

    /// <summary>
    /// Committed cells the engine's range clause no longer admits, collected in one pass.
    ///
    /// <para>
    /// This is the half of the exact rule that removes <c>OverlapMarginBlocks</c>. That margin
    /// exists because ownership is withdrawn only when a probe gets round to the cell, and the
    /// probe cadence is a second - so cached terrain had to take ground back early enough to
    /// cover a chunk vanilla might drop in the meantime. Range loss does not need a probe: its
    /// inputs are the stored sphere and the player's block position, so it can be re-decided
    /// for the whole committed set in the same frame the player moves, and the margin it was
    /// paying for goes to zero.
    /// </para>
    /// <para>
    /// Whole-set, not cursored, and that is the point of G39: a cursored sweep is exactly how
    /// ownership survived in ground the camera had moved away from. Nor is it filtered down to
    /// a plausible ring first - a column-level pre-filter can only reason about the widest of
    /// the four LOD bounds, and a chunk holding only near-detail geometry is cut off at a much
    /// nearer one. Every committed cell is examined; the work is a state-byte compare each, and
    /// the caller only runs it when the engine's own quantised inputs have actually changed.
    /// </para>
    /// <para>
    /// Cells with no recorded sphere are skipped, not demoted. They were decided by the
    /// conservative bound and remain its business.
    /// </para>
    /// </summary>
    public int CollectRangeLosses(in VanillaDrawRangeState range, List<VanillaChunkCell> into,
        float marginBlocks = 0f)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        if (!range.Available || width == 0 || depth == 0) return 0;

        for (int slot = 0; slot < columnTags.Length; slot++)
        {
            if (columnTags[slot] == -1L || columnReady[slot] == 0) continue;
            UnpackColumn(columnTags[slot], out int chunkX, out int chunkZ);
            if (chunkX < minChunkX || chunkX >= minChunkX + width
                || chunkZ < minChunkZ || chunkZ >= minChunkZ + depth) continue;

            int baseIndex = slot * verticalChunks;
            for (int y = 0; y < verticalChunks; y++)
            {
                int index = baseIndex + y;
                if ((VanillaReadinessState)states[index] != VanillaReadinessState.VanillaReady) continue;
                if ((flags[index] & PublicationPendingFlag) != 0) continue;
                if (float.IsNaN(sphereCentreX[index]) || lodMasks[index] == 0) continue;
                if (range.DrawsWithMargin(sphereCentreX[index], sphereCentreZ[index],
                    lodMasks[index], marginBlocks)) continue;
                into.Add(new VanillaChunkCell(chunkX, y, chunkZ));
            }
        }
        return into.Count;
    }

    /// <summary>
    /// Whether every cell in a section's column footprint is owned, counting only the vertical
    /// chunk band the caller names.
    ///
    /// <para>
    /// This exists to answer a question <see cref="Classify"/> structurally cannot: it demands
    /// every vertical chunk from bedrock to sky, and the bottom bands are owned by nothing. A
    /// chunk of solid stone with no exposed face holds no mesh, and one the player has never
    /// been near was never tesselated at all, so neither can ever report drawn - and a single
    /// such cell anywhere in the column stops the whole section qualifying, forever. That is
    /// why the whole-mesh skip has never fired. The cached mesh does not occupy those bands, so
    /// asking about them was always the wrong question.
    /// </para>
    /// <para>
    /// A direct scan, not an incremental aggregate. It is a measurement of what a
    /// geometry-derived rule WOULD classify, run on demand from a diagnostic, so it costs
    /// whatever it costs; nothing on the render path may call it until that is settled and it
    /// has an O(1) counterpart. An out-of-window column answers false, which keeps the
    /// measurement honest rather than optimistic.
    /// </para>
    /// </summary>
    public bool AllOwnedInBand(long sectionKey, int minChunkY, int maxChunkY)
    {
        int level = LodWorld.KeyLevel(sectionKey);
        if (level < 0 || level > LodWorld.MaxLevel) return false;
        if (minChunkY < 0) minChunkY = 0;
        if (maxChunkY >= verticalChunks) maxChunkY = verticalChunks - 1;
        if (minChunkY > maxChunkY) return false;

        int edgeCells = 2 << level;
        int baseX = LodWorld.KeySx(sectionKey) * edgeCells;
        int baseZ = LodWorld.KeySz(sectionKey) * edgeCells;

        for (int dz = 0; dz < edgeCells; dz++)
        for (int dx = 0; dx < edgeCells; dx++)
        {
            int chunkX = baseX + dx;
            int chunkZ = baseZ + dz;
            if (chunkX < minChunkX || chunkX >= minChunkX + width
                || chunkZ < minChunkZ || chunkZ >= minChunkZ + depth) return false;

            int slot = ColumnSlot(chunkX, chunkZ);
            if (columnTags[slot] != PackColumn(chunkX, chunkZ)) return false;
            int baseIndex = slot * verticalChunks;
            for (int y = minChunkY; y <= maxChunkY; y++)
            {
                if ((VanillaReadinessState)states[baseIndex + y] != VanillaReadinessState.VanillaReady)
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// The highest cell in a column that both owns its ground AND is allowed to suppress a
    /// fragment, or -1 when the column has none.
    ///
    /// <para>
    /// The second condition is the whole point. Air cells are owned - they have to be, or the
    /// column aggregate collapses (G40) - but they are excluded from the mask, so cached
    /// geometry standing inside one is never discarded. This returns the top of the band that
    /// can actually hide something, which is what cached geometry has to stay under to be
    /// hidden at all.
    /// </para>
    /// </summary>
    public int HighestSuppressingY(int chunkX, int chunkZ)
    {
        if (chunkX < minChunkX || chunkX >= minChunkX + width
            || chunkZ < minChunkZ || chunkZ >= minChunkZ + depth) return -1;
        int slot = ColumnSlot(chunkX, chunkZ);
        if (columnTags[slot] != PackColumn(chunkX, chunkZ)) return -1;

        int baseIndex = slot * verticalChunks;
        for (int y = verticalChunks - 1; y >= 0; y--)
        {
            int index = baseIndex + y;
            if ((VanillaReadinessState)states[index] != VanillaReadinessState.VanillaReady) continue;
            if ((flags[index] & MaskExcludedFlag) != 0) continue;
            return y;
        }
        return -1;
    }

    /// <summary>How many committed cells currently carry a recorded sphere, for telemetry.</summary>
    public int CellsWithDrawSphere()
    {
        int found = 0;
        for (int slot = 0; slot < columnTags.Length; slot++)
        {
            if (columnTags[slot] == -1L || columnReady[slot] == 0) continue;
            int baseIndex = slot * verticalChunks;
            for (int y = 0; y < verticalChunks; y++)
            {
                int index = baseIndex + y;
                if ((VanillaReadinessState)states[index] != VanillaReadinessState.VanillaReady) continue;
                if (!float.IsNaN(sphereCentreX[index]) && lodMasks[index] != 0) found++;
            }
        }
        return found;
    }

    public int ReadyCount(long sectionKey)
    {
        int level = LodWorld.KeyLevel(sectionKey);
        return level >= 0 && level < readyCounts.Length
            ? readyCounts[level].GetValueOrDefault(sectionKey)
            : 0;
    }

    /// <summary>
    /// Checks the per-section ready counts against the cell states they summarise, repairs
    /// any disagreement, and returns how many section keys were wrong.
    ///
    /// These counts are what the whole-mesh skip trusts. Cell states are re-probed every
    /// second, so a stale cell corrects itself, but nothing re-derives the counts: if one
    /// drifts high, its section reports fully owned forever, its draw is skipped forever,
    /// and no amount of re-probing fixes it. That is the shape of a hole that survives
    /// standing still, which is what play reported, so the counts are audited rather than
    /// trusted.
    /// </summary>
    public int AuditReadyCounts()
    {
        var recomputed = new Dictionary<long, int>[LodWorld.MaxLevel + 1];
        for (int level = 0; level < recomputed.Length; level++)
            recomputed[level] = new Dictionary<long, int>();

        for (int slot = 0; slot < columnTags.Length; slot++)
        {
            long tag = columnTags[slot];
            if (tag == -1L) continue;
            UnpackColumn(tag, out int chunkX, out int chunkZ);
            int baseIndex = slot * verticalChunks;
            for (int y = 0; y < verticalChunks; y++)
            {
                if ((VanillaReadinessState)states[baseIndex + y] != VanillaReadinessState.VanillaReady)
                    continue;
                for (int level = 0; level <= LodWorld.MaxLevel; level++)
                {
                    int edgeCells = 2 << level;
                    long key = LodWorld.SectionKey(level, chunkX / edgeCells, chunkZ / edgeCells);
                    recomputed[level][key] = recomputed[level].GetValueOrDefault(key) + 1;
                }
            }
        }

        int repaired = 0;
        for (int level = 0; level <= LodWorld.MaxLevel; level++)
        {
            foreach (KeyValuePair<long, int> held in readyCounts[level])
            {
                if (recomputed[level].GetValueOrDefault(held.Key) != held.Value) repaired++;
            }
            foreach (KeyValuePair<long, int> truth in recomputed[level])
            {
                if (!readyCounts[level].ContainsKey(truth.Key)) repaired++;
            }
            readyCounts[level] = recomputed[level];
        }
        return repaired;
    }

    public VanillaSectionOwnership Classify(long sectionKey)
    {
        int level = LodWorld.KeyLevel(sectionKey);
        if (level < 0 || level > LodWorld.MaxLevel) return VanillaSectionOwnership.CacheOnly;
        int count = readyCounts[level].GetValueOrDefault(sectionKey);
        if (count == 0) return VanillaSectionOwnership.CacheOnly;
        int edgeCells = 2 << level;
        int total = checked(edgeCells * edgeCells * verticalChunks);
        return count == total ? VanillaSectionOwnership.VanillaOnly : VanillaSectionOwnership.Mixed;
    }

    public void Clear()
    {
        for (int slot = 0; slot < columnTags.Length; slot++)
        {
            if (columnTags[slot] == -1L) continue;
            ClearSlot(slot);
            columnTags[slot] = -1L;
        }
        Array.Clear(columnReady);
        candidateHead = 0;
        candidateCount = 0;
        observationHead = 0;
        observationCount = 0;
        watchHead = 0;
        watchCount = 0;
        minChunkX = minChunkZ = width = depth = 0;
        CommittedReadyCells = 0;
        for (int level = 0; level < readyCounts.Length; level++) readyCounts[level].Clear();
    }

    public void ResetTelemetry()
    {
        CandidateEventsAccepted = 0;
        CandidateEventsCoalesced = 0;
        CandidateEventsDropped = 0;
        ScheduledCandidatesAccepted = 0;
        ScheduledCandidatesCoalesced = 0;
        WatchesStarted = 0;
        WatchesPromoted = 0;
        WatchesExpired = 0;
        WatchesRefused = 0;
    }

    public void GetStateCounts(out int unknown, out int pending, out int observed, out int ready)
    {
        pending = observed = ready = 0;
        for (int slot = 0; slot < columnTags.Length; slot++)
        {
            if (columnTags[slot] == -1L) continue;
            int baseIndex = slot * verticalChunks;
            for (int y = 0; y < verticalChunks; y++)
            {
                switch ((VanillaReadinessState)states[baseIndex + y])
                {
                    case VanillaReadinessState.Pending: pending++; break;
                    case VanillaReadinessState.ObservedRendered: observed++; break;
                    case VanillaReadinessState.VanillaReady: ready++; break;
                }
            }
        }
        unknown = Math.Max(0, ActiveCells - pending - observed - ready);
    }

    /// <summary>
    /// Reports how committed readiness is distributed vertically. Whole-section vanilla
    /// ownership needs every vertical chunk of every covered column, so a column that
    /// never completes proves the CPU whole-mesh skip is unreachable at this granularity
    /// and that any such aggregate must come from cached geometry coverage instead.
    /// The caller owns <paramref name="readyByY"/> so periodic diagnostics allocate nothing.
    /// </summary>
    public void GetColumnReadiness(int[] readyByY, out int columnsTracked,
        out int fullyReadyColumns, out int partialColumns, out int maxReadyPerColumn)
    {
        ArgumentNullException.ThrowIfNull(readyByY);
        if (readyByY.Length < verticalChunks)
            throw new ArgumentException("Readiness histogram is shorter than the world.", nameof(readyByY));

        Array.Clear(readyByY, 0, verticalChunks);
        columnsTracked = fullyReadyColumns = partialColumns = maxReadyPerColumn = 0;
        for (int slot = 0; slot < columnTags.Length; slot++)
        {
            if (columnTags[slot] == -1L) continue;
            int baseIndex = slot * verticalChunks;
            int ready = 0;
            bool tracked = false;
            for (int y = 0; y < verticalChunks; y++)
            {
                var state = (VanillaReadinessState)states[baseIndex + y];
                if (state != VanillaReadinessState.Unknown) tracked = true;
                if (state != VanillaReadinessState.VanillaReady) continue;
                readyByY[y]++;
                ready++;
            }

            if (!tracked) continue;
            columnsTracked++;
            if (ready == verticalChunks) fullyReadyColumns++;
            else if (ready > 0) partialColumns++;
            if (ready > maxReadyPerColumn) maxReadyPerColumn = ready;
        }
    }

    /// <summary>
    /// Distance in blocks from a camera position to the nearest tracked column that is not
    /// completely vanilla-ready, and to the nearest column with no ready cell at all. A
    /// single radial handoff can only suppress cached terrain inside the first of these,
    /// so this measures whether one global radius is worth anything before any pixel
    /// depends on it. Columns outside the active window are unknown and bound the result,
    /// so an untracked frontier cannot be mistaken for owned ground.
    /// </summary>
    /// <summary>
    /// Rewrites a mask from committed state. Window movement clears departing columns
    /// inside this class, and a ring slot reused by different world coordinates would
    /// otherwise leave another place's ownership in the texture, so the mask is rebuilt
    /// wholesale whenever the window moves rather than patched from outside.
    ///
    /// <para>
    /// A rebuild has to apply the same exclusion the incremental path applies, or it
    /// re-poisons the atlas with the air ownership that path deliberately withholds - and it
    /// runs on every 32-block crossing, so the poison would return for up to a full resync
    /// interval every chunk of travel. It cannot call back into the world to ask, so it
    /// reads the exclusion bit recorded at publication instead.
    /// </para>
    /// </summary>
    public void WriteMask(VanillaReadinessMask mask)
    {
        ArgumentNullException.ThrowIfNull(mask);
        mask.Clear();
        if (width == 0 || depth == 0) return;

        for (int slot = 0; slot < columnTags.Length; slot++)
        {
            long tag = columnTags[slot];
            if (tag == -1L || columnReady[slot] == 0) continue;
            UnpackColumn(tag, out int chunkX, out int chunkZ);
            if (chunkX < minChunkX || chunkX >= minChunkX + width
                || chunkZ < minChunkZ || chunkZ >= minChunkZ + depth) continue;

            int baseIndex = slot * verticalChunks;
            for (int y = 0; y < verticalChunks; y++)
            {
                if ((VanillaReadinessState)states[baseIndex + y] != VanillaReadinessState.VanillaReady)
                    continue;
                if ((flags[baseIndex + y] & MaskExcludedFlag) != 0) continue;
                mask.Set(new VanillaChunkCell(chunkX, y, chunkZ), ready: true);
            }
        }
    }

    public double NearestIncompleteColumnBlocks(double cameraX, double cameraZ,
        out double nearestUnreadyBlocks)
    {
        double nearestIncomplete = double.MaxValue;
        nearestUnreadyBlocks = double.MaxValue;
        if (width == 0 || depth == 0) return 0;

        for (int z = minChunkZ; z < minChunkZ + depth; z++)
        {
            for (int x = minChunkX; x < minChunkX + width; x++)
            {
                int slot = ColumnSlot(x, z);
                int ready = columnTags[slot] == PackColumn(x, z) ? columnReady[slot] : 0;
                if (ready == verticalChunks) continue;

                double distance = ColumnDistanceBlocks(x, z, cameraX, cameraZ);
                if (distance < nearestIncomplete) nearestIncomplete = distance;
                if (ready == 0 && distance < nearestUnreadyBlocks) nearestUnreadyBlocks = distance;
            }
        }

        // The window edge is itself an unknown boundary: nothing beyond it is owned.
        double edge = WindowEdgeDistanceBlocks(cameraX, cameraZ);
        if (edge < nearestIncomplete) nearestIncomplete = edge;
        if (edge < nearestUnreadyBlocks) nearestUnreadyBlocks = edge;
        if (nearestUnreadyBlocks == double.MaxValue) nearestUnreadyBlocks = edge;
        return nearestIncomplete == double.MaxValue ? edge : nearestIncomplete;
    }


    /// <summary>Shortest horizontal distance from a point to a chunk column's own volume.</summary>
    public static double ColumnDistanceBlocksFrom(int chunkX, int chunkZ, double x, double z)
        => ColumnDistanceBlocks(chunkX, chunkZ, x, z);

    /// <summary>
    /// Whether the engine might already have stopped drawing this column, because it is far
    /// enough from the camera that its range test can reject it.
    ///
    /// <para>
    /// Loaded, meshed, unhidden and cull-visible is still not drawn. Every terrain pool
    /// location is range-tested per frame in `FrustumCulling.InFrustumAndRange`, and each
    /// terrain LOD level's bound is at most `ViewDistanceSq` - so ground the player walked
    /// away from keeps every "vanilla is drawing here" signal latched while the engine has
    /// quietly stopped submitting it. Ownership that ignores this holds the trailing annulus
    /// between the view-distance circle and the tracked window edge committed forever, and
    /// the mask discards cached terrain there against nothing.
    /// </para>
    /// <para>
    /// The threshold has to clear the whole chunk, not just reach it. The engine measures
    /// range from the mesh's bounding-sphere centre, and that centre is the geometry extents
    /// midpoint - `TesselatedChunk` builds it as `positionX + (xMax + xMin) / 2f` - so it
    /// sits wherever the actual blocks in that chunk happen to be, anywhere across the
    /// 32-block span. Comparing the column's nearest face against the plain view distance
    /// therefore leaves a residual stale ring up to <see cref="SphereCentreSlackBlocks"/>
    /// wide: a chunk whose near face is just inside the view distance can have its sphere
    /// centre well outside it, so the engine range-culls it while every latched chunk signal
    /// still says "drawing" - a thinner copy of the exact band this clause exists to remove.
    /// </para>
    /// <para>
    /// So the comparison is made against `viewDistance - 46`. Both the sphere centre and the
    /// nearest-face point lie inside one chunk column, so they are at most the in-chunk
    /// horizontal diagonal `32 * sqrt(2)` = 45.26 blocks apart, and 46 rounds that up. The
    /// engine culls no earlier than `sqrt(viewDistance^2 + 400) >= viewDistance`, so
    /// engine-culled implies face distance `>= viewDistance - 45.26 > viewDistance - 46`,
    /// which this denies. No placement of the geometry midpoint inside the chunk can produce
    /// a hole from this clause.
    /// </para>
    /// <para>
    /// The price is paid in the safe direction: cached terrain may be drawn over the
    /// outermost chunk and a half of live vanilla terrain, which reads as a seam rather than
    /// a gap. Overlap outranks holes. It is pure arithmetic on purpose - the probe loop is
    /// hot, and a chunk lookup here would take the client's chunk lock once per probe, which
    /// is exactly what made an earlier diagnostic slow the world join down.
    /// </para>
    /// </summary>
    /// <param name="extraMarginBlocks">
    /// Additional overlap beyond the geometric slack. The slack alone answers "can the
    /// engine still submit this column", which is not the same question as "will the engine
    /// still be holding this column a moment from now". Vanilla unloads chunks on its own
    /// schedule, and a column that loses ownership only after it stops drawing leaves ground
    /// that nothing draws until loss is detected. Widening the denial hands those columns
    /// back to cached terrain before vanilla drops them, so the handover happens early
    /// rather than late.
    /// </param>
    public static bool BeyondVanillaDrawRange(int chunkX, int chunkZ,
        double cameraX, double cameraZ, float viewDistance, float extraMarginBlocks = 0f) =>
        ColumnDistanceBlocks(chunkX, chunkZ, cameraX, cameraZ)
        > Math.Max(0f, viewDistance - SphereCentreSlackBlocks - Math.Max(0f, extraMarginBlocks));

    /// <summary>
    /// How far the engine's range measurement can sit beyond this column's nearest face:
    /// the in-chunk horizontal diagonal `32 * sqrt(2)` = 45.26, rounded up. Both the mesh
    /// bounding-sphere centre the engine measures from and the nearest-face point this class
    /// measures to lie inside the same 32-block column, so their separation cannot exceed
    /// it. The rounding leaves sub-block slack for geometry that overhangs the chunk edge.
    /// </summary>
    internal const float SphereCentreSlackBlocks = 46;

    static double ColumnDistanceBlocks(int chunkX, int chunkZ, double x, double z)
    {
        double dx = Math.Max(0, Math.Max(chunkX * (double)ChunkBlocks - x,
            x - (chunkX * (double)ChunkBlocks + ChunkBlocks)));
        double dz = Math.Max(0, Math.Max(chunkZ * (double)ChunkBlocks - z,
            z - (chunkZ * (double)ChunkBlocks + ChunkBlocks)));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    double WindowEdgeDistanceBlocks(double cameraX, double cameraZ)
    {
        double minX = minChunkX * (double)ChunkBlocks;
        double minZ = minChunkZ * (double)ChunkBlocks;
        double maxX = (minChunkX + width) * (double)ChunkBlocks;
        double maxZ = (minChunkZ + depth) * (double)ChunkBlocks;
        return Math.Max(0, Math.Min(
            Math.Min(cameraX - minX, maxX - cameraX),
            Math.Min(cameraZ - minZ, maxZ - cameraZ)));
    }

    public long OldestCandidateFrame()
    {
        long oldest = long.MaxValue;
        for (int offset = 0; offset < candidateCount; offset++)
        {
            int queueIndex = (candidateHead + offset) % candidates.Length;
            VanillaChunkCell cell = candidates[queueIndex];
            if (!TryGetCellIndex(cell, prepareColumn: false, out int index)
                || (flags[index] & QueuedFlag) == 0) continue;
            long frame = candidateFrames[queueIndex];
            if (frame > 0 && frame < oldest) oldest = frame;
        }
        return oldest == long.MaxValue ? -1 : oldest;
    }

    public long OldestObservedFrame()
    {
        long oldest = long.MaxValue;
        for (int slot = 0; slot < columnTags.Length; slot++)
        {
            if (columnTags[slot] == -1L) continue;
            int baseIndex = slot * verticalChunks;
            for (int y = 0; y < verticalChunks; y++)
            {
                int index = baseIndex + y;
                if ((VanillaReadinessState)states[index] != VanillaReadinessState.ObservedRendered) continue;
                long frame = observedFrames[index];
                if (frame > 0 && frame < oldest) oldest = frame;
            }
        }
        return oldest == long.MaxValue ? -1 : oldest;
    }

    public static int ChunkCoordinate(double worldCoordinate) =>
        checked((int)Math.Floor(worldCoordinate / ChunkBlocks));

    public static int ChunkCoordinate(int sectionChunkOrigin, float sectionLocalBlocks) =>
        checked(sectionChunkOrigin + (int)MathF.Floor(sectionLocalBlocks / ChunkBlocks));

    public static VanillaReadinessWindow CalculateWindow(double cameraX, double cameraZ,
        float approvedViewDistance, int mapSizeX, int mapSizeZ, int guardChunks = 2)
    {
        int mapChunksX = Math.Max(1, (mapSizeX + ChunkBlocks - 1) / ChunkBlocks);
        int mapChunksZ = Math.Max(1, (mapSizeZ + ChunkBlocks - 1) / ChunkBlocks);
        int centerX = Math.Clamp(ChunkCoordinate(cameraX), 0, mapChunksX - 1);
        int centerZ = Math.Clamp(ChunkCoordinate(cameraZ), 0, mapChunksZ - 1);
        int vanillaRadius = Math.Max(0, (int)Math.Ceiling(Math.Max(0f, approvedViewDistance) / ChunkBlocks));
        int outerRadius = checked(vanillaRadius + Math.Max(0, guardChunks));
        int minX = Math.Max(0, centerX - outerRadius);
        int minZ = Math.Max(0, centerZ - outerRadius);
        int maxX = Math.Min(mapChunksX - 1, centerX + outerRadius);
        int maxZ = Math.Min(mapChunksZ - 1, centerZ + outerRadius);
        int required = Math.Max(maxX - minX + 1, maxZ - minZ + 1);
        return new VanillaReadinessWindow(centerX, centerZ, minX, minZ,
            maxX - minX + 1, maxZ - minZ + 1, vanillaRadius, outerRadius,
            NextPowerOfTwo(required));
    }

    static int RenderFrameStamp(long frame) =>
        frame >= int.MaxValue ? int.MaxValue : frame <= int.MinValue ? int.MinValue : (int)frame;

    void QueueObservation(VanillaChunkCell cell, int index)
    {
        if ((flags[index] & ObservationQueuedFlag) != 0 || observationCount == observations.Length) return;
        flags[index] |= ObservationQueuedFlag;
        observations[(observationHead + observationCount) % observations.Length] = cell;
        observationCount++;
    }

    void PopObservation()
    {
        observationHead = (observationHead + 1) % observations.Length;
        observationCount--;
    }

    static int NextPowerOfTwo(int value)
    {
        int result = 1;
        while (result < value)
        {
            if (result > 1 << 29) throw new OverflowException("Readiness window is too large.");
            result <<= 1;
        }
        return result;
    }

    static int ShellColumnCount(int innerRadius, int outerRadius)
    {
        int outerWidth = checked(outerRadius * 2 + 1);
        int innerWidth = innerRadius == 0 ? 0 : innerRadius * 2 - 1;
        return checked(outerWidth * outerWidth - innerWidth * innerWidth);
    }

    static void ShellColumn(int centerX, int centerZ, int innerRadius, int outerRadius,
        int ordinal, out int x, out int z)
    {
        for (int radius = innerRadius; radius <= outerRadius; radius++)
        {
            int count = radius == 0 ? 1 : radius * 8;
            if (ordinal >= count)
            {
                ordinal -= count;
                continue;
            }
            if (radius == 0)
            {
                x = centerX;
                z = centerZ;
                return;
            }

            int top = radius * 2 + 1;
            if (ordinal < top)
            {
                x = centerX - radius + ordinal;
                z = centerZ - radius;
                return;
            }
            ordinal -= top;
            int right = radius * 2;
            if (ordinal < right)
            {
                x = centerX + radius;
                z = centerZ - radius + 1 + ordinal;
                return;
            }
            ordinal -= right;
            int bottom = radius * 2;
            if (ordinal < bottom)
            {
                x = centerX + radius - 1 - ordinal;
                z = centerZ + radius;
                return;
            }
            ordinal -= bottom;
            x = centerX - radius;
            z = centerZ + radius - 1 - ordinal;
            return;
        }
        throw new ArgumentOutOfRangeException(nameof(ordinal));
    }

    bool TryGetCellIndex(VanillaChunkCell cell, bool prepareColumn, out int index)
    {
        index = -1;
        if (cell.X < minChunkX || cell.X >= minChunkX + width
            || cell.Z < minChunkZ || cell.Z >= minChunkZ + depth
            || cell.Y < 0 || cell.Y >= verticalChunks) return false;

        int slot = ColumnSlot(cell.X, cell.Z);
        long tag = PackColumn(cell.X, cell.Z);
        if (columnTags[slot] != tag)
        {
            if (!prepareColumn) return false;
            ClearSlot(slot);
            columnTags[slot] = tag;
        }
        index = slot * verticalChunks + cell.Y;
        return true;
    }

    void ClearColumn(int chunkX, int chunkZ)
    {
        int slot = ColumnSlot(chunkX, chunkZ);
        if (columnTags[slot] != PackColumn(chunkX, chunkZ)) return;
        ClearSlot(slot);
        columnTags[slot] = -1L;
    }

    void ClearSlot(int slot)
    {
        long tag = columnTags[slot];
        int baseIndex = slot * verticalChunks;
        if (tag != -1L)
        {
            UnpackColumn(tag, out int chunkX, out int chunkZ);

            // Anything mirroring this ring has to forget the column too. The slot is about
            // to be reused by different world coordinates, and a mirror that keeps the old
            // value answers for the new column with the old column's ownership. On the GPU
            // side that means suppressing cached terrain in cells nothing owns, which no
            // amount of re-probing repairs because the tracker itself is already correct.
            ColumnEvicted?.Invoke(chunkX, chunkZ);
            for (int y = 0; y < verticalChunks; y++)
            {
                int index = baseIndex + y;
                if ((VanillaReadinessState)states[index] == VanillaReadinessState.VanillaReady)
                {
                    ChangeReadyCounts(new VanillaChunkCell(chunkX, y, chunkZ), -1);
                    CommittedReadyCells--;
                }
            }
        }
        columnReady[slot] = 0;
        Array.Clear(states, baseIndex, verticalChunks);
        Array.Clear(flags, baseIndex, verticalChunks);
        Array.Clear(observedFrames, baseIndex, verticalChunks);
        Array.Clear(pendingPublicationTokens, baseIndex, verticalChunks);
        // The watch flag went with the flags above; the time it was measured from goes too,
        // or the arriving column would expire against the departed one's announcement.
        Array.Clear(announcedMs, baseIndex, verticalChunks);
        // The recorded spheres are ring-addressed like everything else, so a slot changing
        // hands must forget them too - G44's failure with a different payload. An arriving
        // column inheriting the departed column's sphere would be tested against the wrong
        // geometry, and near the boundary that decides whether its ground is drawn at all.
        Array.Clear(lodMasks, baseIndex, verticalChunks);
        Array.Fill(sphereCentreX, float.NaN, baseIndex, verticalChunks);
        Array.Fill(sphereCentreZ, float.NaN, baseIndex, verticalChunks);
    }

    void ChangeReadyCounts(VanillaChunkCell cell, int delta)
    {
        for (int level = 0; level <= LodWorld.MaxLevel; level++)
        {
            int edgeCells = 2 << level;
            long key = LodWorld.SectionKey(level, cell.X / edgeCells, cell.Z / edgeCells);
            Dictionary<long, int> counts = readyCounts[level];
            int next = counts.GetValueOrDefault(key) + delta;
            if (next < 0) throw new InvalidOperationException("Vanilla readiness count underflow.");
            if (next == 0) counts.Remove(key);
            else counts[key] = next;
        }
    }

    int ColumnSlot(int chunkX, int chunkZ) =>
        ((chunkZ & capacityMask) * capacity) + (chunkX & capacityMask);

    static long PackColumn(int chunkX, int chunkZ) => ((long)(uint)chunkZ << 32) | (uint)chunkX;

    static void UnpackColumn(long packed, out int chunkX, out int chunkZ)
    {
        chunkX = (int)packed;
        chunkZ = (int)(packed >> 32);
    }

    long NextPublicationToken()
    {
        nextPublicationToken++;
        if (nextPublicationToken == 0) nextPublicationToken++;
        return nextPublicationToken;
    }
}

internal enum VanillaReadinessState : byte
{
    Unknown,
    Pending,
    ObservedRendered,
    VanillaReady,
}

internal enum VanillaSectionOwnership : byte
{
    CacheOnly,
    Mixed,
    VanillaOnly,
}

internal readonly record struct VanillaChunkCell(int X, int Y, int Z);

internal readonly record struct VanillaReadinessPublication(VanillaChunkCell Cell, bool Ready, long Token);

internal readonly record struct VanillaReadinessWindow(
    int CenterX,
    int CenterZ,
    int MinX,
    int MinZ,
    int Width,
    int Depth,
    int VanillaRadius,
    int OuterRadius,
    int RequiredCapacity);
