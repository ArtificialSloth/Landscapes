namespace VintageHorizons.Net;

/// <summary>What a client cache remembers about its server's cache, as read at open.</summary>
/// <param name="Generation">The server cache generation it last caught up with; empty for none.</param>
/// <param name="Version">The version it caught up to, or <see cref="LodAssistSync.NoRecord"/>.</param>
/// <param name="Owed">Held sections announced as changed and not yet merged, with the version announced.</param>
public sealed record LodAssistSyncState(string Generation, long Version, List<(long Key, long Version)> Owed);

/// <summary>
/// One change to that record, applied by the storage worker in one transaction, in order. Deletes
/// run before inserts, so a section settled and then owed again in the same write ends up owed.
/// </summary>
public sealed class LodAssistSyncWrite
{
    /// <summary>Forget every owed section first: a re-check from zero replaces the whole list.</summary>
    public bool ResetOwed;

    /// <summary>Record this generation. Null leaves it as it is.</summary>
    public string? Generation;

    /// <summary>Record this version. Null leaves it as it is.</summary>
    public long? Version;

    public readonly List<long> Settled = new();
    public readonly List<(long Key, long Version)> Owed = new();
}

/// <summary>What a client tells the server at join: the generation and version it caught up to.</summary>
public readonly record struct LodAssistSyncRequest(string Generation, long Version);

/// <summary>What one completed span did, for the log.</summary>
/// <param name="Joined">The first span of the session, answering the join.</param>
/// <param name="FromZero">The server had no usable record for this client, so everything it holds is re-checked.</param>
/// <param name="Recorded">False when the version could not be recorded this session (see <see cref="LodAssistSync"/>).</param>
public readonly record struct LodAssistSpanSummary(
    bool Joined, bool FromZero, long From, long To, int Announced, int Owed, bool Recorded);

/// <summary>
/// How far this client has caught up with its server's section cache (the terrain-change plan's
/// Phase B), and which held sections the server has changed since.
///
/// The server numbers every row it writes with a version that only grows. At join this client
/// says which version it caught up to; the server announces every level-0 section written since,
/// and keeps announcing new ones while the player is online. Each announcement is a span
/// (from, to]: every level-0 section whose version lies in it. A section this client does not
/// hold is simply offered. One it holds is <em>owed</em>: the server's newer copy has to be
/// fetched and merged, newest winning, before this client's copy is current again.
///
/// Two things make that survive a quit, a crash, or a server that refuses for now:
///
/// - A span's owed sections and the version it reaches are written in one transaction, so the
///   record can never claim a version while losing the sections still owed below it.
/// - An owed section leaves the record only once its merged copy is durable (G16), and only if
///   the copy merged is at least as new as the version that was announced (G7).
///
/// So an interrupted re-check resumes where it stopped, and nothing merged is fetched twice.
///
/// Owed sections are fetched nearest first, a bounded number at a time, and only inside the
/// server's serving radius: a server refuses anything further out, and asking would spend the
/// player's request budget on refusals.
///
/// A client with no record, or with a record for another generation of the server's cache, is
/// sent a span from <see cref="NoRecord"/>: every level-0 section the server holds. Every one of
/// those this client holds becomes owed. The owner chose this over keeping what the client has
/// (2026-09-22): always correct, at the price of one background re-download of the overlap.
///
/// Pure bookkeeping, owned by the client's game thread; tests drive it without a game.
/// </summary>
public sealed class LodAssistSync
{
    /// <summary>The version a client with no record asks from. Below every real version.</summary>
    public const long NoRecord = -1;

    /// <summary>
    /// Owed sections handed to the pipeline at once. Twice its eager-fill cap, so a fetch always
    /// has a successor ready, and small enough that choosing them nearest first stays cheap.
    /// </summary>
    public const int MaxFillsActive = 2 * LodRemoteKeySet.MaxEagerFillsActive;

    /// <summary>First wait after a refused fetch, doubled for each refusal after it.</summary>
    public const long FailureCooldownMs = 60_000;

    /// <summary>
    /// Refusals of one section in one session before it waits for the next join. A server can
    /// refuse a section it lists - usually the serving radius - and without a ceiling one it has
    /// lost since would be asked for every few minutes for the rest of the session.
    /// </summary>
    public const int MaxFailuresPerSession = 3;

    const long PumpIntervalMs = 500;

    // ---- The durable record, as last queued for writing ----

    string recordedGeneration = "";
    long recordedVersion = NoRecord;

    /// <summary>Every owed section, with the version announced for it.</summary>
    readonly Dictionary<long, long> owed = new();

    /// <summary>Owed sections that have a row in the record, so settling one must delete it.</summary>
    readonly HashSet<long> owedRecorded = new();

    // ---- This session ----

    bool participating;
    string serverGeneration = "";
    bool joinSpanDone;

    /// <summary>
    /// Set when a span does not continue the one before it. Its sections are still owed, but no
    /// version is recorded for the rest of the session: the next join starts from the last good
    /// one, which is always safe and at worst announces something twice.
    /// </summary>
    bool versionBroken;

    /// <summary>The version the spans this session have reached.</summary>
    long sessionVersion = NoRecord;

    bool spanOpen;
    long spanFrom, spanTo;
    bool spanFromZero;
    int spanAnnounced;
    readonly HashSet<long> spanOwed = new();

    /// <summary>Owed sections handed to the pipeline and not yet finished.</summary>
    readonly HashSet<long> active = new();
    readonly Dictionary<long, long> notBeforeMs = new();
    readonly Dictionary<long, int> failures = new();
    long nextPumpMs;
    bool wasOwing;

    readonly List<LodAssistSyncWrite> writes = new();
    LodAssistSyncWrite? openSettle;

    // Reused by Pump, so choosing the nearest sections allocates nothing.
    readonly long[] pickKeys = new long[MaxFillsActive];
    readonly double[] pickDistances = new double[MaxFillsActive];

    public int OwedCount => owed.Count;
    public int ActiveCount => active.Count;
    public long RecordedVersion => recordedVersion;
    public string RecordedGeneration => recordedGeneration;

    /// <summary>True once this session speaks versions with the server.</summary>
    public bool Active => participating && serverGeneration.Length > 0;

    /// <summary>True once the join span has completed, so owed sections may be fetched.</summary>
    public bool Established => Active && joinSpanDone;

    public long SettledThisSession { get; private set; }

    /// <summary>Install the record read at open. Everything it lists is owed and recorded.</summary>
    public void Load(LodAssistSyncState state)
    {
        recordedGeneration = state.Generation ?? "";
        recordedVersion = state.Version;
        owed.Clear();
        owedRecorded.Clear();
        foreach ((long key, long version) in state.Owed)
        {
            owed[key] = version;
            owedRecorded.Add(key);
        }
        wasOwing = owed.Count > 0;
    }

    /// <summary>
    /// Start a session. A client that takes its freshness from a sibling cache (integrated play)
    /// does not participate: the network would be a second opinion on the same fact, and two
    /// mechanisms describing one fact is how they come to disagree.
    /// </summary>
    /// <returns>What to send in the greeting, or null when not participating.</returns>
    public LodAssistSyncRequest? Begin(bool participate)
    {
        participating = participate;
        if (!participate) return null;
        return new LodAssistSyncRequest(recordedGeneration, recordedVersion);
    }

    /// <summary>The server's cache generation from its welcome; empty from a server without versions.</summary>
    public void OnWelcome(string? generation) => serverGeneration = generation ?? "";

    /// <summary>
    /// Apply one chunk of a versioned span. Held sections become owed; the caller offers the rest.
    /// Returns a summary when this chunk completed the span.
    /// </summary>
    /// <param name="versions">Per-key versions; a missing entry reads as the span's end, which is never too low.</param>
    public LodAssistSpanSummary? OnSpan(long from, long to, IReadOnlyList<long> keys,
        IReadOnlyList<long>? versions, bool last, Func<long, bool> isHeld)
    {
        if (!Active) return null;

        if (!spanOpen) OpenSpan(from, to);

        for (int i = 0; i < keys.Count; i++)
        {
            spanAnnounced++;
            long key = keys[i];
            if (!isHeld(key)) continue;
            long version = versions != null && i < versions.Count ? versions[i] : to;
            if (owed.TryGetValue(key, out long before) && before >= version) continue;
            owed[key] = version;
            spanOwed.Add(key);
            // Already being fetched for an older announcement: the copy on its way may predate
            // this one. Release it so the next pump asks again for this version; the merge of the
            // older copy then settles nothing (Absorbed compares versions).
            active.Remove(key);
        }

        return last ? CommitSpan() : null;
    }

    void OpenSpan(long from, long to)
    {
        spanOpen = true;
        spanFrom = from;
        spanTo = to;
        spanAnnounced = 0;
        spanFromZero = from < 0;

        if (spanFromZero)
        {
            // A new lineage: whatever was owed belonged to rows the server no longer vouches for.
            // The record keeps them until this span commits, so quitting first loses nothing.
            owed.Clear();
            owedRecorded.Clear();
            active.Clear();
            spanOwed.Clear();
            failures.Clear();
            notBeforeMs.Clear();
            return;
        }

        // A span must begin where the last one ended: the join from our record, every later one
        // from the join's end. Anything else means announcements went missing in between.
        long expected = joinSpanDone ? sessionVersion : recordedVersion;
        if (from != expected || serverGeneration != recordedGeneration) versionBroken = true;
    }

    LodAssistSpanSummary CommitSpan()
    {
        bool joined = !joinSpanDone;
        var write = new LodAssistSyncWrite { ResetOwed = spanFromZero };
        foreach (long key in spanOwed)
        {
            if (owed.TryGetValue(key, out long version)) write.Owed.Add((key, version));
        }

        bool recorded = !versionBroken;
        if (recorded)
        {
            write.Version = spanTo;
            if (serverGeneration != recordedGeneration || spanFromZero) write.Generation = serverGeneration;
            recordedVersion = spanTo;
            recordedGeneration = serverGeneration;
        }

        foreach ((long key, _) in write.Owed) owedRecorded.Add(key);
        if (write.ResetOwed || write.Owed.Count > 0 || write.Version != null) Queue(write);

        var summary = new LodAssistSpanSummary(joined, spanFromZero, spanFrom, spanTo,
            spanAnnounced, spanOwed.Count, recorded);
        sessionVersion = spanTo;
        spanOpen = false;
        spanOwed.Clear();
        joinSpanDone = true;
        if (owed.Count > 0) wasOwing = true;
        return summary;
    }

    /// <summary>
    /// Settle owed sections the server does not list at all. Called once the join span completes,
    /// by which time the full key manifest that precedes it on the channel has been applied.
    /// Without this a section the server has since lost would be asked for, and refused, every
    /// session for ever.
    /// </summary>
    public int ForgetUnlisted(Func<long, bool> serverListed)
    {
        List<long>? gone = null;
        foreach (long key in owed.Keys)
        {
            if (!serverListed(key)) (gone ??= new List<long>()).Add(key);
        }
        if (gone == null) return 0;
        foreach (long key in gone) Settle(key);
        return gone.Count;
    }

    /// <summary>
    /// A merge of the server's copy of this section is durable. Settles the debt only when the
    /// copy merged is at least as new as the version announced; an older one leaves it owed.
    /// </summary>
    public void Absorbed(long key, long mergedVersion)
    {
        if (!owed.TryGetValue(key, out long announced)) return;
        active.Remove(key);
        if (mergedVersion < announced) return;
        Settle(key);
        SettledThisSession++;
    }

    /// <summary>
    /// This client no longer holds the section, so there is no stale copy to correct: the next
    /// time the view wants it, the server's current copy is adopted whole.
    /// </summary>
    public void NotHeld(long key)
    {
        if (!owed.ContainsKey(key)) return;
        active.Remove(key);
        Settle(key);
    }

    /// <summary>
    /// The server refused this section or its copy was unusable. It stays owed, waits, and is asked
    /// for again; after <see cref="MaxFailuresPerSession"/> it waits for the next session.
    /// </summary>
    public void Failed(long key, long nowMs)
    {
        if (!owed.ContainsKey(key)) return;
        active.Remove(key);
        int count = failures.TryGetValue(key, out int n) ? n + 1 : 1;
        failures[key] = count;
        notBeforeMs[key] = count >= MaxFailuresPerSession
            ? long.MaxValue
            : nowMs + FailureCooldownMs * (1L << (count - 1));
    }

    void Settle(long key)
    {
        owed.Remove(key);
        spanOwed.Remove(key);
        failures.Remove(key);
        notBeforeMs.Remove(key);
        if (!owedRecorded.Remove(key)) return;
        if (openSettle == null)
        {
            openSettle = new LodAssistSyncWrite();
            writes.Add(openSettle);
        }
        openSettle.Settled.Add(key);
    }

    void Queue(LodAssistSyncWrite write)
    {
        // Settles already queued must reach the record before this write, which may owe the same
        // section again. The next settle starts a new write after it.
        writes.Add(write);
        openSettle = null;
    }

    /// <summary>
    /// Hand the nearest owed sections to the pipeline, up to <see cref="MaxFillsActive"/> at
    /// once, inside the serving radius and outside any refusal cooldown. <paramref name="queueFill"/>
    /// returns false when the client no longer holds the section, which settles it.
    /// </summary>
    /// <param name="serveRadiusBlocks">The server's radius; 0 or less means unlimited.</param>
    public void Pump(long nowMs, double playerX, double playerZ, int serveRadiusBlocks,
        Func<long, long, bool> queueFill)
    {
        if (!Established || owed.Count == 0 || active.Count >= MaxFillsActive || nowMs < nextPumpMs)
            return;
        nextPumpMs = nowMs + PumpIntervalMs;

        int capacity = MaxFillsActive - active.Count;
        int picked = 0;
        foreach ((long key, _) in owed)
        {
            if (active.Contains(key)) continue;
            if (notBeforeMs.TryGetValue(key, out long notBefore) && nowMs < notBefore) continue;
            if (!LodAssistServerSystem.WithinServeRadius(key, playerX, playerZ, serveRadiusBlocks)) continue;

            double distance = LodWorld.NearestDistanceSqTo(key, playerX, playerZ);
            if (picked == capacity && distance >= pickDistances[picked - 1]) continue;

            // Insertion into a short sorted array: the nearest `capacity` so far, nearest first.
            int at = picked < capacity ? picked++ : picked - 1;
            while (at > 0 && pickDistances[at - 1] > distance)
            {
                pickDistances[at] = pickDistances[at - 1];
                pickKeys[at] = pickKeys[at - 1];
                at--;
            }
            pickDistances[at] = distance;
            pickKeys[at] = key;
        }

        for (int i = 0; i < picked; i++)
        {
            long key = pickKeys[i];
            if (queueFill(key, owed[key])) active.Add(key);
            else NotHeld(key);
        }
    }

    /// <summary>Record changes for the storage worker, oldest first. The list is the caller's.</summary>
    public List<LodAssistSyncWrite>? TakeWrites()
    {
        if (writes.Count == 0) return null;
        var taken = new List<LodAssistSyncWrite>(writes);
        writes.Clear();
        openSettle = null;
        return taken;
    }

    /// <summary>True once each time the owed list empties after holding something.</summary>
    public bool TakeCaughtUp()
    {
        if (!wasOwing || owed.Count > 0 || !Established) return false;
        wasOwing = false;
        return true;
    }

    public string Describe() => !participating
        ? "not used (this client reads the server's cache directly)"
        : !Active
            ? "not in use with this server (it does not offer section versions)"
            : $"caught up to version {recordedVersion}{(versionBroken ? " (not recording: a span went missing)" : "")}, "
                + $"{owed.Count} changed sections owed ({active.Count} fetching), {SettledThisSession} merged this session";

    /// <summary>Forget the session; the record itself is reloaded with the next world.</summary>
    public void Reset()
    {
        recordedGeneration = "";
        recordedVersion = NoRecord;
        owed.Clear();
        owedRecorded.Clear();
        participating = false;
        serverGeneration = "";
        joinSpanDone = false;
        versionBroken = false;
        sessionVersion = NoRecord;
        spanOpen = false;
        spanOwed.Clear();
        active.Clear();
        notBeforeMs.Clear();
        failures.Clear();
        nextPumpMs = 0;
        wasOwing = false;
        writes.Clear();
        openSettle = null;
        SettledThisSession = 0;
    }
}
