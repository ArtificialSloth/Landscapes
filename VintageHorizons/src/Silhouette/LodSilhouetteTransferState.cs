using VintageHorizons.Net;

namespace VintageHorizons.Silhouette;

/// <summary>
/// Pure owning-thread state for the manifest/request half of silhouette delivery. Keeping it
/// separate from the game channel makes the three required request endings and stale-session
/// rejection directly testable.
/// </summary>
internal sealed class LodSilhouetteTransferState
{
    internal const int RetryDelayMs = 2000;
    internal const int MaxRetriesPerKey = 8;

    readonly Dictionary<long, int> offered = new();
    readonly HashSet<long> inFlight = new();
    readonly HashSet<long> refused = new();
    readonly Dictionary<long, int> retries = new();
    readonly Dictionary<long, long> retryNotBefore = new();

    public long SessionId { get; private set; }
    public int Offered => offered.Count;
    public int InFlight => inFlight.Count;
    public int Refused => refused.Count;
    public int LastSequence { get; private set; } = -1;
    public bool ManifestComplete { get; private set; }

    public void Reset(long sessionId)
    {
        SessionId = sessionId;
        offered.Clear();
        inFlight.Clear();
        refused.Clear();
        retries.Clear();
        retryNotBefore.Clear();
        LastSequence = -1;
        ManifestComplete = false;
    }

    /// <summary>Apply one current-session, internally consistent manifest chunk.</summary>
    public bool Apply(AssistSilhouetteManifest manifest)
    {
        long[] keys = manifest.Keys ?? Array.Empty<long>();
        int[] revisions = manifest.Revisions ?? Array.Empty<int>();
        if (manifest.SessionId != SessionId || keys.Length != revisions.Length
            || manifest.Sequence <= LastSequence) return false;

        for (int i = 0; i < keys.Length; i++)
        {
            long key = keys[i];
            int revision = revisions[i];
            if (LodWorld.KeyLevel(key) != LodSilhouetteTile.Level || revision <= 0) continue;
            if (offered.TryGetValue(key, out int held) && held >= revision) continue;
            offered[key] = revision;
            refused.Remove(key);
            retries.Remove(key);
            retryNotBefore.Remove(key);
        }
        LastSequence = manifest.Sequence;
        ManifestComplete |= manifest.Last;
        return true;
    }

    public int OfferedRevision(long key) => offered.GetValueOrDefault(key);

    /// <summary>Select nearest missing revisions and retain their slots until a reply.</summary>
    public long[] Select(double playerX, double playerZ, Func<long, int> heldRevision, long now)
    {
        int available = LodAssist.MaxSilhouetteTilesInFlight - inFlight.Count;
        if (available <= 0) return Array.Empty<long>();

        var candidates = new List<(long Key, double Distance)>(Math.Min(offered.Count, available * 4));
        foreach (KeyValuePair<long, int> entry in offered)
        {
            if (refused.Contains(entry.Key) || inFlight.Contains(entry.Key)
                // Equality means this exact server revision is already held. A greater local
                // revision can be a persisted copy from before the server rebuilt its store;
                // request the offered revision so the server can replace that stale authority.
                || heldRevision(entry.Key) == entry.Value
                || retryNotBefore.TryGetValue(entry.Key, out long notBefore) && now < notBefore)
                continue;
            double x = (LodWorld.KeySx(entry.Key) + 0.5) * LodSilhouetteTile.TileBlocks - playerX;
            double z = (LodWorld.KeySz(entry.Key) + 0.5) * LodSilhouetteTile.TileBlocks - playerZ;
            candidates.Add((entry.Key, x * x + z * z));
        }
        if (candidates.Count == 0) return Array.Empty<long>();
        candidates.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        int count = Math.Min(available, candidates.Count);
        var selected = new long[count];
        for (int i = 0; i < count; i++)
        {
            selected[i] = candidates[i].Key;
            inFlight.Add(selected[i]);
        }
        return selected;
    }

    public void SendFailed(IEnumerable<long> keys)
    {
        foreach (long key in keys) inFlight.Remove(key);
    }

    /// <summary>Forget offers outside the moving reach. In-flight slots finish normally.</summary>
    public void Prune(Func<long, bool> keep)
    {
        foreach (long key in offered.Keys.Where(key => !keep(key)).ToArray())
        {
            offered.Remove(key);
            refused.Remove(key);
            retries.Remove(key);
            retryNotBefore.Remove(key);
        }
    }

    /// <summary>
    /// Begin a reply only for a slot in the current world. A stale world's packet cannot release
    /// a current request that happens to use the same tile key.
    /// </summary>
    public bool BeginReply(long sessionId, long key) =>
        sessionId == SessionId && inFlight.Remove(key);

    public void Succeeded(long key)
    {
        retries.Remove(key);
        retryNotBefore.Remove(key);
    }

    public bool Failed(long key, bool retryable, long now)
    {
        if (retryable)
        {
            int count = retries.GetValueOrDefault(key) + 1;
            if (count < MaxRetriesPerKey)
            {
                retries[key] = count;
                retryNotBefore[key] = now + RetryDelayMs;
                return true;
            }
        }
        retries.Remove(key);
        retryNotBefore.Remove(key);
        refused.Add(key);
        offered.Remove(key);
        return false;
    }
}
