using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using VintageHorizons.Net;

namespace VintageHorizons.Silhouette;

/// <summary>
/// Server half of the silhouette first look (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER P1).
///
/// For each player who asked, it walks the sampling lattice outward from them, nearest first,
/// out to their reach plus one tile. It takes one chunk in every 8x8, probes it for existence
/// and, where no terrain exists yet, previews it to the Terrain pass. Only the 32x32 height map
/// of a preview is kept: nothing reaches the capture pipeline, and a preview writes nothing to
/// the savegame. Tiles stay in RAM and are pushed to every player whose reach covers them.
///
/// It is an automatic producer, so it never takes the world-generation authority. It holds new
/// work while an explicit job holds that authority, or while the engine is generating real
/// chunks for someone (settled decision 39). With no requesting player it registers no tick work.
///
/// Every probe and preview is served by the engine's one world-generation thread. If that thread
/// stops answering, the builder adds no more work for the rest of the session and says so in the
/// log and in chat (<see cref="LodGenerationStallWatch"/>).
/// </summary>
internal sealed class LodSilhouetteBuilder
{
    public const int DefaultReachBlocks = 8192;
    public const int MinReachBlocks = 1024;

    /// <summary>The distance scale's end; no server ceiling in singleplayer (plan A7).</summary>
    public const int MaxReachBlocks = 32768;

    public const int PreviewsPerSecond = 16;
    public const int MaxPeeksInFlight = 16;
    public const int MaxProbesInFlight = 32;
    public const int MaxProbeIssuesPerTick = 4;

    /// <summary>
    /// Real chunk generation pending for someone: hold new previews. The engine's counter never
    /// sees peeks, so this measures only real loads. P0 sets the final value (plan 4.5).
    /// </summary>
    public const int YieldGeneratingChunks = 8;

    public const int TilesPerSecondPerPlayer = 20;

    const double TickWorkBudgetMs = 1.0;
    const long StuckPeekTimeoutMs = 300_000;
    const long StatusLogIntervalMs = 30_000;
    const int MaxSkipsPerProbe = 4096;

    readonly ICoreServerAPI sapi;
    readonly ILogger logger;
    readonly Func<bool> authorityBusy;
    readonly Action<IServerPlayer, AssistSilhouetteTile> send;

    sealed class Demand
    {
        public required IServerPlayer Player;
        public int ReachBlocks;
        public double CentreX, CentreZ;
        public int CentreI, CentreJ;
        public int SpiralIndex, SpiralTotal;
        public bool Exhausted;
        public readonly Dictionary<long, int> Sent = new();
        public readonly HashSet<long> Queued = new();
        public readonly LodTickAllowance SendAllowance = new();
    }

    enum EnumProbeStep { Probed, Exhausted, Paused }

    readonly record struct PeekInFlight(int Cx, int Cz, long IssuedMs);

    readonly Dictionary<string, Demand> demands = new();
    readonly List<string> demandOrder = new();
    int nextDemand;
    readonly Dictionary<long, LodSilhouetteTile> tiles = new();
    readonly HashSet<long> attempted = new();
    readonly Queue<(int Cx, int Cz)> pendingPeeks = new();
    readonly Dictionary<long, PeekInFlight> peeksInFlight = new();
    readonly List<long> stale = new();
    readonly LodTickAllowance previewAllowance = new();
    readonly LodGenerationStallWatch generatorWatch = new();
    int probesInFlight;
    long listenerId;
    bool listening;
    bool firstPeekReported;
    long nextStatusLogMs;
    long lastLandedMs = -1;
    int lastLandedCx, lastLandedCz;

    public int Probed, Existing, Peeked, Landed, Empty, TimedOut, YieldTicks, TilesSent;
    public long BytesSent;

    public LodSilhouetteBuilder(ICoreServerAPI sapi, ILogger logger, Func<bool> authorityBusy,
        Action<IServerPlayer, AssistSilhouetteTile> send)
    {
        this.sapi = sapi;
        this.logger = logger;
        this.authorityBusy = authorityBusy;
        this.send = send;
    }

    public int TileCount => tiles.Count;
    public int PlayerCount => demands.Count;

    /// <summary>Main thread.</summary>
    public void Request(IServerPlayer player, AssistSilhouetteRequest request)
    {
        if (!request.Enabled)
        {
            if (Forget(player.PlayerUID))
                logger.Notification("Horizon silhouettes: {0} switched them off; their demand is dropped.",
                    player.PlayerName);
            return;
        }

        int reach = Math.Clamp(request.ReachBlocks <= 0 ? DefaultReachBlocks : request.ReachBlocks,
            MinReachBlocks, MaxReachBlocks);
        if (!demands.TryGetValue(player.PlayerUID, out Demand? demand))
        {
            demand = new Demand { Player = player };
            demands[player.PlayerUID] = demand;
            demandOrder.Add(player.PlayerUID);
        }
        demand.Player = player;
        demand.ReachBlocks = reach;
        Recentre(demand, force: true);
        logger.Notification(
            "Horizon silhouettes: {0} asked for a {1:N0}-block reach. Previewing one chunk in every "
            + "8x8 around them, nearest first, heights only; nothing is written to the savegame.",
            player.PlayerName, reach);
        EnsureListening();
    }

    /// <summary>Main thread. True when the player had a demand.</summary>
    public bool Forget(string playerUid)
    {
        if (!demands.Remove(playerUid)) return false;
        demandOrder.Remove(playerUid);
        if (demands.Count == 0) StopListening();
        return true;
    }

    public void Dispose()
    {
        if (!listening) return;
        sapi.Event.UnregisterGameTickListener(listenerId);
        listening = false;
    }

    void EnsureListening()
    {
        if (listening) return;
        listenerId = sapi.Event.RegisterGameTickListener(_ => Tick(), LodTickAllowance.TickMilliseconds);
        listening = true;
        nextStatusLogMs = sapi.World.ElapsedMilliseconds + StatusLogIntervalMs;
    }

    void StopListening()
    {
        if (!listening) return;
        sapi.Event.UnregisterGameTickListener(listenerId);
        listening = false;
        pendingPeeks.Clear();
        LogStatus("idle, no player is asking");
    }

    void Tick()
    {
        long now = sapi.World.ElapsedMilliseconds;
        WatchGenerator(now);
        ExpireStuckPeeks(now);
        foreach (Demand demand in demands.Values) Recentre(demand, force: false);
        IssueWork(now);
        SendTiles(now);
        if (now >= nextStatusLogMs)
        {
            nextStatusLogMs = now + StatusLogIntervalMs;
            LogStatus("working");
        }
    }

    /// <summary>
    /// When the world-generation thread stops answering, nothing the builder does can restart
    /// it, so it stops adding work and says what it was waiting for. In 0.4.77 the only symptom
    /// was a hang when the player left the world.
    /// </summary>
    void WatchGenerator(long now)
    {
        if (!generatorWatch.Tick(now, peeksInFlight.Count > 0 || probesInFlight > 0)) return;
        pendingPeeks.Clear();

        var waiting = new List<string>();
        foreach (PeekInFlight peek in peeksInFlight.Values.OrderBy(p => p.IssuedMs))
        {
            waiting.Add($"{peek.Cx},{peek.Cz} (blocks {peek.Cx * LodSilhouetteTile.ChunkBlocks},"
                + $"{peek.Cz * LodSilhouetteTile.ChunkBlocks}, waiting {(now - peek.IssuedMs) / 1000} s)");
        }
        string last = lastLandedMs < 0
            ? "none this session"
            : $"chunk {lastLandedCx},{lastLandedCz}, {(now - lastLandedMs) / 1000} s ago";
        logger.Warning(
            "Horizon silhouettes: the world generator has not answered for {0} s, with {1} previews "
            + "and {2} existence checks outstanding. Previews stop for the rest of this session; "
            + "tiles already built stay. A world-generation thread that stops answering also stops "
            + "new chunks arriving, and leaving the world then hangs while the game waits for it. "
            + "Previews still waiting, oldest first (chunk x,z): {3}. Last preview that came back: {4}.",
            (now - generatorWatch.SilentSinceMs) / 1000, peeksInFlight.Count, probesInFlight,
            waiting.Count == 0 ? "none" : string.Join("; ", waiting), last);
        foreach (Demand demand in demands.Values)
        {
            demand.Player.SendMessage(GlobalConstants.GeneralChatGroup,
                "[VintageHorizons] The world generator stopped answering horizon silhouette previews, "
                + "so they are stopped for this session. If leaving the world now hangs, this is why; "
                + "the server log has the details.", EnumChatType.Notification);
        }
    }

    /// <summary>Main thread: a probe or a preview came back from the world-generation thread.</summary>
    void GeneratorAnswered()
    {
        long now = sapi.World.ElapsedMilliseconds;
        generatorWatch.Answered(now);
        if (generatorWatch.LateAnswers != 1) return;
        logger.Notification(
            "Horizon silhouettes: the world generator answered again {0} s after previews were "
            + "stopped, so it was slow rather than stuck. Previews stay off for this session.",
            (now - generatorWatch.StalledAtMs) / 1000);
    }

    void Recentre(Demand demand, bool force)
    {
        EntityPos? pos = demand.Player.Entity?.Pos;
        if (pos == null) return;
        double dx = pos.X - demand.CentreX, dz = pos.Z - demand.CentreZ;
        if (!force && dx * dx + dz * dz < LodSilhouetteField.LatticeBlocks * LodSilhouetteField.LatticeBlocks)
            return;

        demand.CentreX = pos.X;
        demand.CentreZ = pos.Z;
        demand.CentreI = NearestNode(pos.X);
        demand.CentreJ = NearestNode(pos.Z);
        int radius = (demand.ReachBlocks + LodSilhouetteTile.TileBlocks) / LodSilhouetteField.LatticeBlocks + 2;
        demand.SpiralTotal = (2 * radius + 1) * (2 * radius + 1);
        demand.SpiralIndex = 0;
        demand.Exhausted = false;

        // Anything already built that the player now reaches and does not hold goes out again.
        foreach (KeyValuePair<long, LodSilhouetteTile> entry in tiles)
        {
            if (!Covers(demand, entry.Value)) continue;
            if (demand.Sent.TryGetValue(entry.Key, out int revision) && revision == entry.Value.Revision)
                continue;
            demand.Queued.Add(entry.Key);
        }
    }

    static int NearestNode(double blockCoordinate) => (int)Math.Round(
        (blockCoordinate - LodSilhouetteField.NodeOriginBlocks) / LodSilhouetteField.LatticeBlocks);

    /// <summary>The tile's nearest point lies within the reach plus the one-tile margin.</summary>
    static bool Covers(Demand demand, LodSilhouetteTile tile)
    {
        double nearestX = Math.Clamp(demand.CentreX, tile.BlockX, tile.BlockX + LodSilhouetteTile.TileBlocks);
        double nearestZ = Math.Clamp(demand.CentreZ, tile.BlockZ, tile.BlockZ + LodSilhouetteTile.TileBlocks);
        double dx = nearestX - demand.CentreX, dz = nearestZ - demand.CentreZ;
        double limit = demand.ReachBlocks + LodSilhouetteTile.TileBlocks;
        return dx * dx + dz * dz <= limit * limit;
    }

    void IssueWork(long now)
    {
        // A generator that stopped answering gets nothing more this session (WatchGenerator).
        if (generatorWatch.Stalled) return;
        if (authorityBusy()
            || sapi.WorldManager.CurrentGeneratingChunkCount >= YieldGeneratingChunks)
        {
            YieldTicks++;
            return;
        }

        // Only previews spend the rate: they are the worldgen cost. Probes are existence reads,
        // bounded by their own in-flight cap and by the short queue of empty positions.
        int allowance = previewAllowance.Available(now, PreviewsPerSecond);
        var budget = new LodWorkBudget(TickWorkBudgetMs);
        int peeks = 0;
        while (peeks < allowance && pendingPeeks.Count > 0
            && peeksInFlight.Count < MaxPeeksInFlight && !budget.Expired)
        {
            (int cx, int cz) = pendingPeeks.Dequeue();
            IssuePeek(cx, cz, now);
            peeks++;
        }
        previewAllowance.Spend(peeks);

        // Then probe further out, players in turn.
        int probes = 0, idle = 0;
        while (probes < MaxProbeIssuesPerTick && idle < demandOrder.Count
            && probesInFlight < MaxProbesInFlight && pendingPeeks.Count < MaxPeeksInFlight
            && !budget.Expired)
        {
            Demand demand = demands[demandOrder[nextDemand++ % demandOrder.Count]];
            if (ProbeNext(demand) == EnumProbeStep.Probed)
            {
                probes++;
                idle = 0;
            }
            else
            {
                idle++;
            }
        }
    }

    EnumProbeStep ProbeNext(Demand demand)
    {
        double limit = demand.ReachBlocks + LodSilhouetteTile.TileBlocks;
        int skipped = 0;
        while (demand.SpiralIndex < demand.SpiralTotal)
        {
            if (++skipped > MaxSkipsPerProbe) return EnumProbeStep.Paused;
            (int di, int dj) = LodColumnMap.SpiralAt(demand.SpiralIndex++);
            int i = demand.CentreI + di, j = demand.CentreJ + dj;
            double nx = LodSilhouetteField.NodeBlockX(i) - demand.CentreX;
            double nz = LodSilhouetteField.NodeBlockX(j) - demand.CentreZ;
            if (nx * nx + nz * nz > limit * limit) continue;
            int cx = LodSilhouetteField.LatticeChunkOfNode(i);
            int cz = LodSilhouetteField.LatticeChunkOfNode(j);
            if (!attempted.Add(LodColumnMap.Key(cx, cz))) continue;
            Probe(cx, cz);
            return EnumProbeStep.Probed;
        }

        if (!demand.Exhausted)
        {
            demand.Exhausted = true;
            LogStatus($"every lattice position within {demand.ReachBlocks:N0} blocks of "
                + $"{demand.Player.PlayerName} has been requested");
        }
        return EnumProbeStep.Exhausted;
    }

    void Probe(int cx, int cz)
    {
        probesInFlight++;
        Probed++;
        sapi.WorldManager.TestMapChunkExists(cx, cz, exists =>
        {
            // The callback need not be on the main thread; every field here is main-thread state.
            sapi.Event.EnqueueMainThreadTask(() =>
            {
                probesInFlight--;
                GeneratorAnswered();
                // Existing terrain is never previewed: its heights come from capture (P2), and
                // the client draws its cached terrain there anyway.
                if (exists) Existing++;
                else if (!generatorWatch.Stalled) pendingPeeks.Enqueue((cx, cz));
            }, "vh-silhouette-probe");
        });
    }

    void IssuePeek(int cx, int cz, long now)
    {
        peeksInFlight[LodColumnMap.Key(cx, cz)] = new PeekInFlight(cx, cz, now);
        Peeked++;
        sapi.WorldManager.PeekChunkColumn(cx, cz, new ChunkPeekOptions
        {
            UntilPass = EnumWorldGenPass.Terrain,
            OnGenerated = columns => OnPeeked(cx, cz, columns),
        });
    }

    void OnPeeked(int cx, int cz, Dictionary<Vec2i, IServerChunk[]>? columns)
    {
        // Copy on whatever thread this is, then publish on the main thread.
        ushort[]? heights = null;
        int zeros = 0;
        bool forestMap = false;
        if (columns != null
            && columns.TryGetValue(new Vec2i(cx, cz), out IServerChunk[]? column)
            && column is { Length: > 0 })
        {
            IMapChunk? map = column[0].MapChunk;
            ushort[]? source = map?.WorldGenTerrainHeightMap;
            if (source != null && source.Length >= LodSilhouetteTile.HeightMapLength)
            {
                heights = new ushort[LodSilhouetteTile.HeightMapLength];
                Array.Copy(source, heights, heights.Length);
                foreach (ushort h in heights)
                    if (h == 0) zeros++;
            }
            forestMap = map?.MapRegion?.ForestMap != null;
        }
        sapi.Event.EnqueueMainThreadTask(() => Land(cx, cz, heights, zeros, forestMap),
            "vh-silhouette-peeked");
    }

    void Land(int cx, int cz, ushort[]? heights, int zeros, bool forestMap)
    {
        // Even a preview already given up on shows the generator is still answering.
        GeneratorAnswered();
        // A callback after its timeout has already been counted.
        if (!peeksInFlight.Remove(LodColumnMap.Key(cx, cz))) return;
        lastLandedMs = sapi.World.ElapsedMilliseconds;
        lastLandedCx = cx;
        lastLandedCz = cz;
        if (heights == null)
        {
            if (++Empty == 1)
            {
                logger.Warning(
                    "Horizon silhouettes: the preview of chunk {0},{1} carried no worldgen terrain "
                    + "height map, so it gave no heights. If every preview reports this, silhouettes "
                    + "stay empty and the worldgen pass is the thing to investigate.", cx, cz);
            }
            return;
        }

        if (!firstPeekReported)
        {
            // P0.1: the heights are source-traced as populated with the owner's worldgen mods;
            // this line is the runtime confirmation, and ForestMap is the later canopy's input.
            firstPeekReported = true;
            logger.Notification(
                "Horizon silhouettes: first preview landed (chunk {0},{1}): {2} of 1,024 worldgen "
                + "terrain heights are zero (expect 0); map region forest map {3}.",
                cx, cz, zeros, forestMap ? "present" : "absent");
        }

        (int tileX, int tileZ) = LodSilhouetteTile.TileOfChunk(cx, cz);
        long key = LodSilhouetteTile.KeyOf(tileX, tileZ);
        if (!tiles.TryGetValue(key, out LodSilhouetteTile? tile))
            tiles[key] = tile = new LodSilhouetteTile(tileX, tileZ);
        if (!tile.SetChunk(cx, cz, heights, EnumSilhouetteOrigin.Preview)) return;
        Landed++;
        foreach (Demand demand in demands.Values)
            if (Covers(demand, tile)) demand.Queued.Add(key);
    }

    void ExpireStuckPeeks(long now)
    {
        if (peeksInFlight.Count == 0) return;
        stale.Clear();
        foreach (KeyValuePair<long, PeekInFlight> entry in peeksInFlight)
            if (now - entry.Value.IssuedMs > StuckPeekTimeoutMs) stale.Add(entry.Key);
        if (stale.Count == 0) return;
        foreach (long key in stale) peeksInFlight.Remove(key);
        TimedOut += stale.Count;
        logger.Warning("Horizon silhouettes: {0} previews never called back and were given up on "
            + "({1} total). Those positions stay without heights this session.", stale.Count, TimedOut);
    }

    void SendTiles(long now)
    {
        foreach (Demand demand in demands.Values)
        {
            if (demand.Queued.Count == 0) continue;
            int allowance = demand.SendAllowance.Available(now, TilesPerSecondPerPlayer);
            int sent = 0;
            while (sent < allowance && demand.Queued.Count > 0)
            {
                long key = NearestQueued(demand);
                demand.Queued.Remove(key);
                if (!tiles.TryGetValue(key, out LodSilhouetteTile? tile)) continue;
                if (demand.Sent.TryGetValue(key, out int revision) && revision == tile.Revision) continue;
                byte[] payload = tile.Encode();
                send(demand.Player, new AssistSilhouetteTile
                {
                    TileX = tile.TileX,
                    TileZ = tile.TileZ,
                    Revision = tile.Revision,
                    Payload = payload,
                });
                demand.Sent[key] = tile.Revision;
                sent++;
                TilesSent++;
                BytesSent += payload.Length;
            }
            demand.SendAllowance.Spend(sent);
        }
    }

    long NearestQueued(Demand demand)
    {
        long best = 0;
        double bestDistance = double.MaxValue;
        foreach (long key in demand.Queued)
        {
            double x = (LodWorld.KeySx(key) + 0.5) * LodSilhouetteTile.TileBlocks - demand.CentreX;
            double z = (LodWorld.KeySz(key) + 0.5) * LodSilhouetteTile.TileBlocks - demand.CentreZ;
            double distance = x * x + z * z;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = key;
        }
        return best;
    }

    public string Describe()
    {
        string text = string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0} player(s), {1} tiles; {2} lattice probes ({3} already hold terrain); previews {4} "
            + "issued, {5} landed, {6} without heights, {7} timed out, {8} in flight; held {9} ticks "
            + "for other generation; {10} tile messages sent ({11:0.0} KB)",
            demands.Count, tiles.Count, Probed, Existing, Peeked, Landed, Empty, TimedOut,
            peeksInFlight.Count, YieldTicks, TilesSent, BytesSent / 1024.0);
        if (!generatorWatch.Stalled) return text;
        return text + string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "; STOPPED for this session: the world generator went silent for {0} s ({1} answers came "
            + "back later)", (generatorWatch.StalledAtMs - generatorWatch.SilentSinceMs) / 1000,
            generatorWatch.LateAnswers);
    }

    void LogStatus(string phase) =>
        logger.Notification("Horizon silhouettes ({0}): {1}.", phase, Describe());
}
