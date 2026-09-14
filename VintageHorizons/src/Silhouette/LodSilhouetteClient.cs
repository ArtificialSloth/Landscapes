using System.Collections.Concurrent;
using Vintagestory.API.Common;
using VintageHorizons.Net;

namespace VintageHorizons.Silhouette;

/// <summary>
/// Client half of the silhouette first look (PLAN_HORIZON_SILHOUETTES_AND_FRONTIER P1).
///
/// It takes the tiles the server pushes, keeps the lattice nodes they carry, decides which tiles
/// need a new mesh, and builds meshes on worker threads. The renderer uploads and draws the
/// results. Everything here runs on the client main thread except the network intake queue and
/// the mesh jobs, which touch only their own copies.
///
/// Session-only and off by default until the Silhouettes tab exists (P4).
/// </summary>
internal sealed class LodSilhouetteClient
{
    const int MaxDecodesPerPump = 16;
    const int MaxJobsInFlight = 4;
    const int CoverageChecksPerPump = 48;

    readonly ILogger logger;
    readonly Func<bool> available;
    readonly Action<bool, int> sendRequest;
    readonly ConcurrentQueue<AssistSilhouetteTile> arrivals = new();
    readonly ConcurrentQueue<MeshResult> finished = new();
    readonly Dictionary<long, LodSilhouetteTile> tiles = new();
    readonly List<long> tileOrder = new();
    readonly Dictionary<long, double> nodes = new();
    readonly Dictionary<long, ulong> coverage = new();
    readonly Dictionary<long, int> meshVersion = new();
    readonly HashSet<long> dirty = new();
    int coverageCursor;
    int jobsInFlight;
    bool requestSent;
    bool enabled;
    int seaLevel;
    double lastX, lastZ;

    public readonly record struct MeshResult(long Key, int TileX, int TileZ, int Version, int Epoch,
        LodSilhouetteMeshArrays? Mesh);

    public LodSilhouetteClient(ILogger logger, Func<bool> available, Action<bool, int> sendRequest)
    {
        this.logger = logger;
        this.available = available;
        this.sendRequest = sendRequest;
    }

    public bool Enabled => enabled;
    public int ReachBlocks { get; private set; } = LodSilhouetteBuilder.DefaultReachBlocks;

    /// <summary>Changes whenever everything held is dropped, so the renderer drops its meshes too.</summary>
    public int Epoch { get; private set; }

    public bool RequestSent => requestSent;
    public int TileCount => tiles.Count;
    public int NodeCount => nodes.Count;
    public int PendingMeshes => dirty.Count + Volatile.Read(ref jobsInFlight);
    public int TilesReceived, TilesRejected, MeshesBuilt, EmptyMeshes, MeshFailures;

    public void SetEnabled(bool on)
    {
        if (enabled == on) return;
        enabled = on;
        if (on) return;

        // Off is the last silhouette message until the player switches it back on (plan 4.10).
        if (requestSent && available()) sendRequest(false, ReachBlocks);
        Clear();
    }

    /// <summary>Network thread.</summary>
    public void OnTileArrived(AssistSilhouetteTile tile)
    {
        if (Volatile.Read(ref enabled)) arrivals.Enqueue(tile);
    }

    /// <summary>World leave. The session switch survives; the next join asks again.</summary>
    public void Reset() => Clear();

    void Clear()
    {
        requestSent = false;
        Epoch++;
        tiles.Clear();
        tileOrder.Clear();
        nodes.Clear();
        coverage.Clear();
        meshVersion.Clear();
        dirty.Clear();
        coverageCursor = 0;
        while (arrivals.TryDequeue(out _)) { }
        while (finished.TryDequeue(out _)) { }
    }

    /// <summary>Main thread, once per client tick.</summary>
    public void Pump(LodWorld world, double playerX, double playerZ, int worldSeaLevel)
    {
        if (!enabled) return;
        lastX = playerX;
        lastZ = playerZ;
        seaLevel = worldSeaLevel;
        if (!requestSent)
        {
            if (!available()) return;
            sendRequest(true, ReachBlocks);
            requestSent = true;
            logger.Notification("Horizon silhouettes: asked the server for a {0:N0}-block reach.", ReachBlocks);
        }

        for (int n = 0; n < MaxDecodesPerPump && arrivals.TryDequeue(out AssistSilhouetteTile? message); n++)
            Accept(message);
        CheckCoverage(world);
        Schedule(world);
    }

    void Accept(AssistSilhouetteTile message)
    {
        long key = LodSilhouetteTile.KeyOf(message.TileX, message.TileZ);
        if (tiles.TryGetValue(key, out LodSilhouetteTile? held) && held.Revision >= message.Revision) return;
        LodSilhouetteTile? tile = LodSilhouetteTile.Decode(message.TileX, message.TileZ,
            message.Revision, message.Payload);
        if (tile == null)
        {
            if (++TilesRejected == 1)
                logger.Warning("Horizon silhouettes: tile {0},{1} did not decode and was dropped.",
                    message.TileX, message.TileZ);
            return;
        }

        TilesReceived++;
        if (held == null) tileOrder.Add(key);
        tiles[key] = tile;
        dirty.Add(key);

        // Every lattice chunk of the tile is a node. A changed node re-meshes each tile it reaches.
        for (int localZ = LodSilhouetteField.LatticeOffsetChunks; localZ < LodSilhouetteTile.ChunksPerEdge;
             localZ += LodSilhouetteField.LatticeChunks)
        {
            for (int localX = LodSilhouetteField.LatticeOffsetChunks; localX < LodSilhouetteTile.ChunksPerEdge;
                 localX += LodSilhouetteField.LatticeChunks)
            {
                int i = LodSilhouetteField.NodeOfLatticeChunk(tile.TileX * LodSilhouetteTile.ChunksPerEdge + localX);
                int j = LodSilhouetteField.NodeOfLatticeChunk(tile.TileZ * LodSilhouetteTile.ChunksPerEdge + localZ);
                long nodeKey = LodSilhouetteField.NodeKey(i, j);
                bool known = tile.TryChunkMean(localX, localZ, out double mean);
                bool had = nodes.TryGetValue(nodeKey, out double old);
                if (known == had && (!known || old == mean)) continue;
                if (known) nodes[nodeKey] = mean;
                else nodes.Remove(nodeKey);
                foreach ((int tx, int tz) in LodSilhouetteField.TilesTouchedByNode(i, j))
                    dirty.Add(LodSilhouetteTile.KeyOf(tx, tz));
            }
        }
    }

    /// <summary>
    /// Rolling: a few tiles per tick ask the cache which of their 64 level-0 cells hold terrain.
    /// A change re-meshes the tile, so silhouettes give way as the player explores.
    /// </summary>
    void CheckCoverage(LodWorld world)
    {
        if (tileOrder.Count == 0) return;
        for (int n = 0; n < CoverageChecksPerPump && n < tileOrder.Count; n++)
        {
            if (coverageCursor >= tileOrder.Count) coverageCursor = 0;
            long key = tileOrder[coverageCursor++];
            if (!tiles.TryGetValue(key, out LodSilhouetteTile? tile)) continue;
            ulong mask = CoverageOf(world, tile.TileX, tile.TileZ);
            if (coverage.TryGetValue(key, out ulong held) && held == mask) continue;
            coverage[key] = mask;
            dirty.Add(key);
        }
    }

    static ulong CoverageOf(LodWorld world, int tileX, int tileZ)
    {
        ulong mask = 0;
        int cellsPerEdge = LodSilhouetteTile.TileBlocks / LodSection.SectionBlocks;
        for (int localZ = 0; localZ < cellsPerEdge; localZ++)
        {
            for (int localX = 0; localX < cellsPerEdge; localX++)
            {
                long key = LodWorld.SectionKey(0, tileX * cellsPerEdge + localX, tileZ * cellsPerEdge + localZ);
                if (world.HasDataSet.Contains(key))
                    mask |= 1UL << LodSilhouetteField.CoverageBit(localX, localZ);
            }
        }
        return mask;
    }

    void Schedule(LodWorld world)
    {
        while (Volatile.Read(ref jobsInFlight) < MaxJobsInFlight && dirty.Count > 0)
        {
            long key = NearestDirty();
            dirty.Remove(key);
            if (!tiles.TryGetValue(key, out LodSilhouetteTile? tile)) continue;

            if (!coverage.TryGetValue(key, out ulong mask))
                coverage[key] = mask = CoverageOf(world, tile.TileX, tile.TileZ);
            int version = meshVersion.TryGetValue(key, out int v) ? v + 1 : 1;
            meshVersion[key] = version;

            int firstX = LodSilhouetteField.FirstNodeOfTile(tile.TileX);
            int firstZ = LodSilhouetteField.FirstNodeOfTile(tile.TileZ);
            var snapshot = new double[LodSilhouetteField.TileNodeSpan, LodSilhouetteField.TileNodeSpan];
            for (int b = 0; b < LodSilhouetteField.TileNodeSpan; b++)
                for (int a = 0; a < LodSilhouetteField.TileNodeSpan; a++)
                    snapshot[b, a] = nodes.TryGetValue(LodSilhouetteField.NodeKey(firstX + a, firstZ + b),
                        out double h) ? h : double.NaN;

            int tileX = tile.TileX, tileZ = tile.TileZ, epoch = Epoch, sea = seaLevel;
            Interlocked.Increment(ref jobsInFlight);
            Task.Run(() =>
            {
                LodSilhouetteMeshArrays? mesh = null;
                try
                {
                    mesh = LodSilhouetteField.BuildMesh(tileX, tileZ, snapshot, mask, sea);
                }
                catch (Exception e)
                {
                    if (Interlocked.Increment(ref MeshFailures) == 1)
                        logger.Error("Horizon silhouettes: a tile mesh failed to build: {0}", e);
                }
                finally
                {
                    finished.Enqueue(new MeshResult(key, tileX, tileZ, version, epoch, mesh));
                    Interlocked.Decrement(ref jobsInFlight);
                }
            });
        }
    }

    long NearestDirty()
    {
        long best = 0;
        double bestDistance = double.MaxValue;
        foreach (long key in dirty)
        {
            double x = (LodWorld.KeySx(key) + 0.5) * LodSilhouetteTile.TileBlocks - lastX;
            double z = (LodWorld.KeySz(key) + 0.5) * LodSilhouetteTile.TileBlocks - lastZ;
            double distance = x * x + z * z;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = key;
        }
        return best;
    }

    /// <summary>
    /// Render thread (the client main thread). Only results still current: a later version of
    /// the same tile, or a world change, makes an older result stale, and it is dropped here.
    /// </summary>
    public bool TryTakeFinished(out MeshResult result)
    {
        while (finished.TryDequeue(out result))
        {
            if (result.Epoch != Epoch) continue;
            if (!meshVersion.TryGetValue(result.Key, out int version) || version != result.Version) continue;
            if (result.Mesh == null) EmptyMeshes++;
            else MeshesBuilt++;
            return true;
        }
        return false;
    }
}
