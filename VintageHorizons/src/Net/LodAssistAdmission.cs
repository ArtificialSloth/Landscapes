using System.Collections.Concurrent;

namespace VintageHorizons.Net;

/// <summary>
/// What the server accepts from one client, before any work is done for it (pre-release review
/// S1 and S2, 0.4.161).
///
/// The protocol promises an answer for every requested key, and the server used to keep that
/// promise for any client: past its 256-key queue it sent each extra key its own refusal, at
/// once, on the owning thread. The game accepts a 1 MB packet from a player, room for well over
/// 100,000 keys, so one message from a modified client made the server send that many replies in
/// one go. Likewise every hello re-sent the whole key manifest and queued a full scan of what
/// changed. The honest client never has more than <see cref="LodAssist.MaxSectionsInFlight"/>
/// keys outstanding and greets once per connection, so a request past these bounds can only come
/// from a client that is not playing by the protocol. It gets no reply at all: answering it is
/// the cost being prevented, and an honest client never meets these limits.
/// </summary>
internal static class LodAssistAdmission
{
    /// <summary>
    /// Keys one request may carry. Four times the honest client's whole in-flight allowance, so a
    /// future client that batches a little differently still fits.
    /// </summary>
    internal const int MaxKeysPerRequest = 4 * LodAssist.MaxSectionsInFlight;

    /// <summary>
    /// Keys one player may have queued or being read. Sixteen times the honest allowance; a
    /// player at this bound is already far outside the protocol.
    /// </summary>
    internal const int MaxQueuedPerPlayer = 256;

    /// <summary>
    /// Whether a section request is looked at at all: from a greeted client, carrying at least
    /// one key and no more than <see cref="MaxKeysPerRequest"/>.
    /// </summary>
    internal static bool AcceptsRequest(bool greeted, int keyCount) =>
        greeted && keyCount > 0 && keyCount <= MaxKeysPerRequest;

    /// <summary>
    /// How many of an accepted request's keys are queued, given how many this player already
    /// holds. The rest are dropped without a reply: only a client already past its own limit
    /// can send them.
    /// </summary>
    internal static int Admitted(int keyCount, int held) =>
        Math.Clamp(MaxQueuedPerPlayer - held, 0, keyCount);
}

/// <summary>
/// One answered hello per connection. Checked on the network thread, so a flood of hellos does
/// not even become queued work on the owning thread. A disconnect forgets the player, so the next
/// connection greets as normal.
/// </summary>
internal sealed class LodHelloGate
{
    readonly ConcurrentDictionary<string, byte> answered = new(StringComparer.Ordinal);

    /// <summary>True the first time a connected player says hello; false for every repeat.</summary>
    public bool TryOpen(string playerUid) => answered.TryAdd(playerUid, 0);

    public void Forget(string playerUid) => answered.TryRemove(playerUid, out _);
}
