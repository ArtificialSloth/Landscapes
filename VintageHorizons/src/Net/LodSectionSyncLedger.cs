namespace VintageHorizons.Net;

/// <summary>
/// Which versioned span each syncing player has been sent through, on the server (terrain-change
/// plan Phase B; the client half is <see cref="LodAssistSync"/>).
///
/// A span is every level-0 section whose version lies in (from, to]. A player's spans must follow
/// on from one another with no gap, or a change could fall between two of them and never be
/// announced. Two kinds of query produce them, both answered by the assist reader:
///
/// - a join span, once per greeting, from the version the client's record names;
/// - a broadcast every 30 seconds, from where the previous broadcast ended, shared by everyone.
///
/// The rule that keeps each player's spans contiguous is order. Joins and broadcasts go through one
/// FIFO on the reader and their results are applied here in that same order, so a broadcast
/// result applied after a player's join result was queried after it, and so began no later than
/// the join ended. The player is sent only the rows past its own end, and its end moves up to the
/// broadcast's. A broadcast applied before the join result is simply not the player's: the join,
/// queried later, already covers it.
///
/// A broadcast with nothing past a player's end is not sent to that player, and its end stays
/// where it was. That is still contiguous: a query sees every committed row up to its end, versions
/// only grow, so nothing can later appear in the range it found empty.
///
/// Held apart from the server system because the failure is an ordering, and an ordering can be
/// checked with no server, no session and no players - the same reason as <see cref="ManifestLedger"/>.
/// </summary>
public sealed class LodSectionSyncLedger
{
    sealed class Player
    {
        public long Session;
        public bool Ready;
        public long SentThrough;
    }

    readonly Dictionary<string, Player> players = new();

    /// <summary>The end of the newest broadcast applied, or null before the first.</summary>
    public long? BroadcastThrough { get; private set; }

    /// <summary>Players that sync by version, whether or not their join span has been sent.</summary>
    public int Syncing => players.Count;

    public bool IsSyncing(string playerUid) => players.ContainsKey(playerUid);

    /// <summary>
    /// Where broadcasts start, fixing it at the cache's committed version the first time it is
    /// asked. Called before any join is queried: every join span then ends at or above it, so the
    /// first broadcast begins no later than any player's end. Starting from a broadcast query of
    /// its own would not do - a player that joined before that query ran would lose the rows
    /// between its join and the query, which the query would report as the baseline, not as
    /// changes.
    /// </summary>
    public long EnsureBaseline(long committedVersion)
    {
        BroadcastThrough ??= committedVersion;
        return BroadcastThrough.Value;
    }

    /// <summary>
    /// A client that syncs by version was greeted. Returns the version its join span starts after:
    /// its own record when that names this cache's generation, otherwise
    /// <see cref="LodAssistSync.NoRecord"/>, which re-checks everything it holds.
    /// </summary>
    public long Join(string playerUid, long session, string clientGeneration, long clientVersion,
        string cacheGeneration)
    {
        players[playerUid] = new Player { Session = session };
        bool continues = clientGeneration.Length > 0 && clientGeneration == cacheGeneration
            && clientVersion >= 0;
        return continues ? clientVersion : LodAssistSync.NoRecord;
    }

    /// <summary>
    /// The join span, ending at <paramref name="to"/>, is being sent. False when the greeting it
    /// answered has since been replaced or the player left; the result is then dropped.
    /// </summary>
    public bool Joined(string playerUid, long session, long to)
    {
        if (!players.TryGetValue(playerUid, out Player? player) || player.Session != session) return false;
        player.Ready = true;
        player.SentThrough = to;
        return true;
    }

    /// <summary>
    /// Stop syncing this player by version for the session, because its join span could not be
    /// read. It is served as a client without versions; its record is untouched, so the next join
    /// starts where it left off.
    /// </summary>
    public void Abandon(string playerUid, long session)
    {
        if (players.TryGetValue(playerUid, out Player? player) && player.Session == session)
            players.Remove(playerUid);
    }

    /// <summary>
    /// Apply a broadcast ending at <paramref name="to"/> whose rows carry
    /// <paramref name="versions"/>, ascending. Returns each ready player that has anything past its
    /// own end: the span start to send it and the first row it has not been sent.
    /// </summary>
    public List<(string PlayerUid, long From, int FirstRow)> Broadcast(long to, IReadOnlyList<long> versions)
    {
        BroadcastThrough = to;
        var sends = new List<(string, long, int)>();
        foreach ((string uid, Player player) in players)
        {
            if (!player.Ready || to <= player.SentThrough) continue;
            int first = FirstAbove(versions, player.SentThrough);
            if (first >= versions.Count) continue;
            sends.Add((uid, player.SentThrough, first));
            player.SentThrough = to;
        }
        return sends;
    }

    /// <summary>A player left, or the cache it was syncing with closed.</summary>
    public void Forget(string playerUid) => players.Remove(playerUid);

    /// <summary>The cache was replaced: every player's spans and the broadcast baseline mean nothing now.</summary>
    public void Clear()
    {
        players.Clear();
        BroadcastThrough = null;
    }

    /// <summary>The first index whose version is above <paramref name="version"/>, in ascending versions.</summary>
    static int FirstAbove(IReadOnlyList<long> versions, long version)
    {
        int lo = 0, hi = versions.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (versions[mid] > version) hi = mid;
            else lo = mid + 1;
        }
        return lo;
    }
}
