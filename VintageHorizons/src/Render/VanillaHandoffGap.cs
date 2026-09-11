namespace VintageHorizons;

/// <summary>
/// Why the engine is not drawing a chunk whose mask texel is suppressing cached terrain.
/// Every value except <see cref="Drawing"/> and <see cref="Unknown"/> is a frame in which
/// nothing draws that ground: the sky-coloured flash that `.vhpaint` shows in magenta.
/// </summary>
internal enum VanillaGapReason : byte
{
    /// <summary>Every engine signal says the chunk is submitted this frame.</summary>
    Drawing,

    /// <summary>The client no longer holds the chunk at all.</summary>
    Unloaded,

    /// <summary>
    /// A chunk object the tessellator has never visited: its drawn counter is still zero. A
    /// chunk the client has just installed, or re-installed over one it already had, looks
    /// exactly like this until its first mesh is uploaded.
    /// </summary>
    Fresh,

    /// <summary>Air holding a suppressing texel, which the publication path never allows (G40).</summary>
    Empty,

    /// <summary>Tessellated at some point, but holding no pool geometry now.</summary>
    NoMesh,

    /// <summary>Holds geometry, but no pool location the engine would submit: all hidden, or at a level it does not draw.</summary>
    Hidden,

    /// <summary>The ray culler's occlusion verdict (G43). Normally behind other terrain, so normally invisible.</summary>
    Culled,

    /// <summary>The engine's range clause, evaluated with the chunk's current sphere and this frame's player block.</summary>
    OutOfRange,

    /// <summary>A binding was unavailable. Never reported, so a game update cannot invent gaps.</summary>
    Unknown,
}

/// <summary>What the engine exposes about one chunk, already read. Pure input to <see cref="VanillaHandoffGap.Classify"/>.</summary>
internal readonly record struct VanillaGapSignals(
    bool Present,
    bool DrawnKnown,
    int DrawnCount,
    bool Empty,
    bool GeometryKnown,
    bool HoldsGeometry,
    bool Drawable,
    bool CullKnown,
    bool CullVisible,
    bool RangeKnown,
    bool RangeDraws);

/// <summary>What ended a gap episode.</summary>
internal enum VanillaGapEnd : byte
{
    /// <summary>The mask still gives the cell to vanilla, and vanilla is drawing it again.</summary>
    VanillaResumed,

    /// <summary>An ordinary ownership path - probe, range sweep or resync - handed the cell to the cache.</summary>
    HandedToCache,

    /// <summary>The cell's column scrolled out of the tracked window.</summary>
    LeftWindow,

    /// <summary>`.vhgap release` handed it to the cache in the frame the scan found it.</summary>
    ReleasedByGapScan,
}

/// <summary>
/// The pure half of `.vhgap`: which engine signal failed, and which failures the release
/// comparison may act on. Kept free of engine types so the rules have deterministic checks.
/// </summary>
internal static class VanillaHandoffGap
{
    public const int ReasonCount = (int)VanillaGapReason.Unknown + 1;

    /// <summary>
    /// The first signal that says "not drawn", in the order the engine reaches them: a chunk
    /// must exist, must have been through the tessellator, must hold geometry, must have a
    /// submittable pool location, and only then do the culler and the range clause - whose
    /// inputs live on that geometry - get a say. An unreadable signal never reports a gap.
    /// </summary>
    public static VanillaGapReason Classify(in VanillaGapSignals signals)
    {
        if (!signals.Present) return VanillaGapReason.Unloaded;
        if (signals.DrawnKnown && signals.DrawnCount == 0) return VanillaGapReason.Fresh;
        if (signals.Empty) return VanillaGapReason.Empty;
        if (!signals.GeometryKnown) return VanillaGapReason.Unknown;
        if (!signals.HoldsGeometry) return VanillaGapReason.NoMesh;
        if (!signals.Drawable) return VanillaGapReason.Hidden;
        if (signals.CullKnown && !signals.CullVisible) return VanillaGapReason.Culled;
        if (signals.RangeKnown && !signals.RangeDraws) return VanillaGapReason.OutOfRange;
        return VanillaGapReason.Drawing;
    }

    /// <summary>
    /// Whether `.vhgap release` may hand this cell to the cache. Only reasons the ordinary gain
    /// path refuses as well: an unloaded or fresh chunk fails IsChunkRendered, a meshless one
    /// fails the pool-geometry gain gate, an out-of-range one fails the exact range rule, and
    /// air never takes a texel. Hidden and culled chunks would pass every one of those gates
    /// and be taken straight back, so releasing them would make the mask alternate each probe.
    /// </summary>
    public static bool Releasable(VanillaGapReason reason) => reason is
        VanillaGapReason.Unloaded or VanillaGapReason.Fresh or VanillaGapReason.Empty
        or VanillaGapReason.NoMesh or VanillaGapReason.OutOfRange;

    /// <summary>
    /// Whether an episode gets its own log line. Occlusion culling is counted but not listed:
    /// with the culler on, many committed cells sit behind terrain for long stretches, and a
    /// line for each would bury the handful of gaps the log exists to find.
    /// </summary>
    public static bool LoggedIndividually(VanillaGapReason reason) => reason is not
        (VanillaGapReason.Drawing or VanillaGapReason.Unknown or VanillaGapReason.Culled);

    public static string Name(VanillaGapReason reason) => reason switch
    {
        VanillaGapReason.Drawing => "drawing",
        VanillaGapReason.Unloaded => "unloaded",
        VanillaGapReason.Fresh => "fresh chunk, not meshed yet",
        VanillaGapReason.Empty => "air",
        VanillaGapReason.NoMesh => "no mesh",
        VanillaGapReason.Hidden => "hidden",
        VanillaGapReason.Culled => "occlusion-culled",
        VanillaGapReason.OutOfRange => "out of range",
        _ => "unknown",
    };

    public static string Name(VanillaGapEnd end) => end switch
    {
        VanillaGapEnd.VanillaResumed => "vanilla drew it again",
        VanillaGapEnd.HandedToCache => "handed to the cache",
        VanillaGapEnd.LeftWindow => "left the tracked window",
        _ => "released by .vhgap",
    };
}

/// <summary>
/// Pairs per-frame gap sightings into episodes, so the log says "this chunk, this long, this
/// reason" instead of one line per frame. Pure: the renderer supplies frames, sightings and
/// the end cause.
/// </summary>
internal sealed class VanillaHandoffGapTracker
{
    public readonly record struct Episode(
        VanillaChunkCell Cell,
        VanillaGapReason StartReason,
        VanillaGapReason LastReason,
        long StartFrame,
        long EndFrame,
        long StartMilliseconds,
        long EndMilliseconds,
        VanillaGapEnd End,
        bool SphereStale,
        double StartDistanceBlocks,
        bool StartedOnCrossing,
        string? StartDetail)
    {
        /// <summary>
        /// Frames in which the gap was drawn. A sighting is taken before the cache draws, so an
        /// episode released in the frame it was found was never drawn and counts zero.
        /// </summary>
        public long VisibleFrames => EndFrame - StartFrame;
    }

    struct Open
    {
        public VanillaGapReason StartReason;
        public VanillaGapReason LastReason;
        public long StartFrame;
        public long StartMilliseconds;
        public long SeenFrame;
        public bool SphereStale;
        public double StartDistanceBlocks;
        public bool StartedOnCrossing;
        public string? StartDetail;
    }

    readonly Dictionary<VanillaChunkCell, Open> open = new();
    readonly List<VanillaChunkCell> closing = new();
    long frame;
    long milliseconds;

    public int OpenCount => open.Count;

    public bool IsOpen(VanillaChunkCell cell) => open.ContainsKey(cell);

    public void BeginFrame(long renderFrame, long nowMilliseconds)
    {
        frame = renderFrame;
        milliseconds = nowMilliseconds;
    }

    /// <summary>
    /// Records that the mask gives this cell to vanilla while vanilla draws nothing there this
    /// frame. Returns true when the sighting opens a new episode.
    /// </summary>
    public bool Observe(VanillaChunkCell cell, VanillaGapReason reason, bool sphereStale,
        double distanceBlocks, bool onCrossing, string? detail = null)
    {
        if (open.TryGetValue(cell, out Open episode))
        {
            episode.LastReason = reason;
            episode.SeenFrame = frame;
            episode.SphereStale |= sphereStale;
            open[cell] = episode;
            return false;
        }

        open[cell] = new Open
        {
            StartReason = reason,
            LastReason = reason,
            StartFrame = frame,
            StartMilliseconds = milliseconds,
            SeenFrame = frame,
            SphereStale = sphereStale,
            StartDistanceBlocks = distanceBlocks,
            StartedOnCrossing = onCrossing,
            StartDetail = detail,
        };
        return true;
    }

    /// <summary>Closes an episode because the scan itself handed the cell to the cache this frame.</summary>
    public bool Release(VanillaChunkCell cell, List<Episode> ended)
    {
        if (!open.Remove(cell, out Open episode)) return false;
        ended.Add(Close(cell, episode, VanillaGapEnd.ReleasedByGapScan));
        return true;
    }

    /// <summary>
    /// Closes every episode that had no sighting this frame. The caller says why it ended,
    /// because only the caller can see the mask and the window.
    /// </summary>
    public int EndFrame(Func<VanillaChunkCell, VanillaGapEnd> endOf, List<Episode> ended)
    {
        closing.Clear();
        foreach (KeyValuePair<VanillaChunkCell, Open> entry in open)
        {
            if (entry.Value.SeenFrame != frame) closing.Add(entry.Key);
        }

        foreach (VanillaChunkCell cell in closing)
        {
            open.Remove(cell, out Open episode);
            ended.Add(Close(cell, episode, endOf(cell)));
        }
        return closing.Count;
    }

    /// <summary>Forgets open episodes without reporting them; used when the log or the world changes.</summary>
    public void Clear() => open.Clear();

    Episode Close(VanillaChunkCell cell, in Open episode, VanillaGapEnd end) =>
        new(cell, episode.StartReason, episode.LastReason, episode.StartFrame, frame,
            episode.StartMilliseconds, milliseconds, end, episode.SphereStale,
            episode.StartDistanceBlocks, episode.StartedOnCrossing, episode.StartDetail);
}
