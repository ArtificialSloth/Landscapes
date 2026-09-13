using System.Text.Json;

namespace VintageHorizons.Net;

/// <summary>The footprint an explicit savegame generation job fills.</summary>
public enum EnumWorldGenShape
{
    Square,
    Circle,
}

/// <summary>One globally aligned 16x16-chunk tile in nearest-first order.</summary>
public readonly record struct LodWorldGenTile(int X, int Z, int FirstColumn, int ColumnCount);

/// <summary>One column in a deterministic world-generation plan.</summary>
public readonly record struct LodWorldGenColumn(int Cx, int Cz, int Tile);

public enum EnumWorldGenSkipReason
{
    None,
    DurableState,
    ServerCache,
}

/// <summary>The only two reasons a planned real column is omitted before issuing.</summary>
public static class LodWorldGenSkip
{
    public static EnumWorldGenSkipReason Classify(bool serverCacheHasL0, bool stateDone) =>
        serverCacheHasL0 ? EnumWorldGenSkipReason.ServerCache
        : stateDone     ? EnumWorldGenSkipReason.DurableState
        :                 EnumWorldGenSkipReason.None;
}

/// <summary>
/// Deterministic, clipped area enumeration for the real-world generation job. Tiles are
/// globally aligned, visit a square spiral around the centre tile, and use Z-major rows
/// internally. Keeping this free of game types makes the ordering and restart identity
/// directly checkable.
/// </summary>
public sealed class LodWorldGenPlan
{
    public const int TileChunks = 16;

    readonly List<LodWorldGenColumn> columns = new();
    readonly List<LodWorldGenTile> tiles = new();
    readonly Dictionary<long, int> indexByColumn = new();

    public IReadOnlyList<LodWorldGenColumn> Columns => columns;
    public IReadOnlyList<LodWorldGenTile> Tiles => tiles;
    public int Count => columns.Count;

    public LodWorldGenPlan(int centreCx, int centreCz, int radiusChunks,
        EnumWorldGenShape shape, int mapChunksX, int mapChunksZ)
    {
        if (radiusChunks < 0) throw new ArgumentOutOfRangeException(nameof(radiusChunks));
        if (mapChunksX <= 0) throw new ArgumentOutOfRangeException(nameof(mapChunksX));
        if (mapChunksZ <= 0) throw new ArgumentOutOfRangeException(nameof(mapChunksZ));

        int minCx = Math.Max(0, centreCx - radiusChunks);
        int maxCx = Math.Min(mapChunksX - 1, centreCx + radiusChunks);
        int minCz = Math.Max(0, centreCz - radiusChunks);
        int maxCz = Math.Min(mapChunksZ - 1, centreCz + radiusChunks);
        if (minCx > maxCx || minCz > maxCz) return;

        int minTileX = minCx / TileChunks;
        int maxTileX = maxCx / TileChunks;
        int minTileZ = minCz / TileChunks;
        int maxTileZ = maxCz / TileChunks;
        int centreTileX = Math.Clamp(centreCx, 0, mapChunksX - 1) / TileChunks;
        int centreTileZ = Math.Clamp(centreCz, 0, mapChunksZ - 1) / TileChunks;
        int tilesInBounds = (maxTileX - minTileX + 1) * (maxTileZ - minTileZ + 1);
        int acceptedTiles = 0;

        // The largest possible ring is bounded by the clipped tile rectangle. Iterating
        // the full enclosing spiral gives a deterministic nearest-first filtered order.
        int maxRing = Math.Max(
            Math.Max(Math.Abs(minTileX - centreTileX), Math.Abs(maxTileX - centreTileX)),
            Math.Max(Math.Abs(minTileZ - centreTileZ), Math.Abs(maxTileZ - centreTileZ)));
        int spiralPositions = (2 * maxRing + 1) * (2 * maxRing + 1);
        for (int i = 0; i < spiralPositions && acceptedTiles < tilesInBounds; i++)
        {
            (int dx, int dz) = LodColumnMap.SpiralAt(i);
            int tileX = centreTileX + dx;
            int tileZ = centreTileZ + dz;
            if (tileX < minTileX || tileX > maxTileX
                || tileZ < minTileZ || tileZ > maxTileZ) continue;
            acceptedTiles++;

            int first = columns.Count;
            int tileMinX = Math.Max(minCx, tileX * TileChunks);
            int tileMaxX = Math.Min(maxCx, tileX * TileChunks + TileChunks - 1);
            int tileMinZ = Math.Max(minCz, tileZ * TileChunks);
            int tileMaxZ = Math.Min(maxCz, tileZ * TileChunks + TileChunks - 1);
            int tileOrdinal = tiles.Count;

            for (int cz = tileMinZ; cz <= tileMaxZ; cz++)
            for (int cx = tileMinX; cx <= tileMaxX; cx++)
            {
                long rx = (long)cx - centreCx;
                long rz = (long)cz - centreCz;
                if (shape == EnumWorldGenShape.Circle
                    && rx * rx + rz * rz > (long)radiusChunks * radiusChunks) continue;

                int index = columns.Count;
                columns.Add(new LodWorldGenColumn(cx, cz, tileOrdinal));
                indexByColumn.Add(LodColumnMap.Key(cx, cz), index);
            }

            int count = columns.Count - first;
            if (count > 0)
            {
                tiles.Add(new LodWorldGenTile(tileX, tileZ, first, count));
            }
            else
            {
                // A circular corner can leave an otherwise in-bounds tile empty. No
                // columns reference its provisional ordinal, so omit it entirely.
            }
        }
    }

    public bool TryGetIndex(int cx, int cz, out int index) =>
        indexByColumn.TryGetValue(LodColumnMap.Key(cx, cz), out index);
}

/// <summary>
/// Durable cursor for one deterministic plan. The compact bitmap records only columns
/// whose L0 cache row became durable; an interrupted run can safely repeat everything
/// else from the now-generated savegame.
/// </summary>
public sealed class LodWorldGenState
{
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    public int CentreCx { get; set; }
    public int CentreCz { get; set; }
    public int RadiusChunks { get; set; }
    public EnumWorldGenShape Shape { get; set; }
    public int MapChunksX { get; set; }
    public int MapChunksZ { get; set; }
    public string Preset { get; set; } = "Balanced";
    public string StartedBy { get; set; } = "unknown";
    public long StartedUtcTicks { get; set; }
    public long SavegameBytesAtStart { get; set; }
    public long CacheBytesAtStart { get; set; }
    public int TotalColumns { get; set; }
    public byte[] DoneBits { get; set; } = Array.Empty<byte>();

    public static LodWorldGenState Create(LodWorldGenPlan plan, int centreCx, int centreCz,
        int radiusChunks, EnumWorldGenShape shape, int mapChunksX, int mapChunksZ,
        string startedBy, long savegameBytesAtStart, long cacheBytesAtStart) => new()
    {
        CentreCx = centreCx,
        CentreCz = centreCz,
        RadiusChunks = radiusChunks,
        Shape = shape,
        MapChunksX = mapChunksX,
        MapChunksZ = mapChunksZ,
        StartedBy = startedBy,
        StartedUtcTicks = DateTime.UtcNow.Ticks,
        SavegameBytesAtStart = savegameBytesAtStart,
        CacheBytesAtStart = cacheBytesAtStart,
        TotalColumns = plan.Count,
        DoneBits = new byte[(plan.Count + 7) / 8],
    };

    public bool IsDone(int index)
    {
        if ((uint)index >= (uint)TotalColumns || DoneBits == null
            || DoneBits.Length <= index >> 3) return false;
        return (DoneBits[index >> 3] & (1 << (index & 7))) != 0;
    }

    public bool MarkDone(int index)
    {
        if ((uint)index >= (uint)TotalColumns)
            throw new ArgumentOutOfRangeException(nameof(index));
        int b = index >> 3;
        byte mask = (byte)(1 << (index & 7));
        if ((DoneBits[b] & mask) != 0) return false;
        DoneBits[b] |= mask;
        return true;
    }

    public int DoneCount()
    {
        int count = 0;
        for (int i = 0; i < TotalColumns; i++) if (IsDone(i)) count++;
        return count;
    }

    public bool Matches(LodWorldGenPlan plan, int mapChunksX, int mapChunksZ) =>
        Format == CurrentFormat
        && TotalColumns >= 0
        && TotalColumns == plan.Count
        && MapChunksX == mapChunksX
        && MapChunksZ == mapChunksZ
        && DoneBits != null
        && DoneBits.Length == (TotalColumns + 7) / 8;

    internal static string Serialize(LodWorldGenState state) =>
        JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });

    internal static LodWorldGenState? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<LodWorldGenState>(json);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>The named reason a running job is not issuing another column.</summary>
public enum EnumWorldGenGate
{
    None,
    Paused,
    Faulted,
    ServerOverload,
    CaptureBacklog,
    SaveBacklogCount,
    SaveBacklogAge,
    EngineQueue,
    InFlight,
    TilePyramid,
    FinalPersistence,
}

/// <summary>Pure gate precedence shared by status and the issuing loop.</summary>
public static class LodWorldGenGates
{
    public static EnumWorldGenGate Select(bool paused, bool faulted, bool serverOverloaded,
        bool captureBacklogFull, int dirtySections, int dirtySectionLimit,
        long oldestDirtyAgeMs, long oldestDirtyLimitMs, int engineQueue, int queueTarget,
        int inFlight, int maxInFlight)
    {
        if (faulted) return EnumWorldGenGate.Faulted;
        if (paused) return EnumWorldGenGate.Paused;
        if (serverOverloaded) return EnumWorldGenGate.ServerOverload;
        if (captureBacklogFull) return EnumWorldGenGate.CaptureBacklog;
        if (dirtySections >= dirtySectionLimit) return EnumWorldGenGate.SaveBacklogCount;
        if (oldestDirtyAgeMs >= oldestDirtyLimitMs) return EnumWorldGenGate.SaveBacklogAge;
        if (engineQueue >= queueTarget) return EnumWorldGenGate.EngineQueue;
        if (inFlight >= maxInFlight) return EnumWorldGenGate.InFlight;
        return EnumWorldGenGate.None;
    }

    public static string Describe(EnumWorldGenGate gate) => gate switch
    {
        EnumWorldGenGate.None => "generating",
        EnumWorldGenGate.Paused => "paused by request",
        EnumWorldGenGate.Faulted => "stopped by a safety failure",
        EnumWorldGenGate.ServerOverload => "waiting for server tick recovery",
        EnumWorldGenGate.CaptureBacklog => "waiting for distant-terrain capture",
        EnumWorldGenGate.SaveBacklogCount => "waiting for distant-terrain saves",
        EnumWorldGenGate.SaveBacklogAge => "waiting for an old distant-terrain save",
        EnumWorldGenGate.EngineQueue => "engine generation queue at target",
        EnumWorldGenGate.InFlight => "kept-column limit reached",
        EnumWorldGenGate.TilePyramid => "finishing this tile's distant terrain",
        EnumWorldGenGate.FinalPersistence => "finishing distant-terrain persistence",
        _ => "waiting",
    };
}

/// <summary>The one internal component allowed to generate terrain at a time.</summary>
public enum EnumWorldGenAuthority
{
    None,
    StartupPregen,
    TransientGeneration,
    WorldGeneration,
    Frontier,
}

public sealed class LodWorldGenAuthority
{
    public EnumWorldGenAuthority Current { get; private set; }
    public string Label { get; private set; } = "";

    public bool TryAcquire(EnumWorldGenAuthority kind, string label, out string refusal)
    {
        if (kind == EnumWorldGenAuthority.None)
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (Current != EnumWorldGenAuthority.None)
        {
            refusal = $"{Label} already has world-generation authority";
            return false;
        }
        Current = kind;
        Label = label;
        refusal = "";
        return true;
    }

    public bool Release(EnumWorldGenAuthority kind)
    {
        if (Current != kind) return false;
        Current = EnumWorldGenAuthority.None;
        Label = "";
        return true;
    }
}

/// <summary>Bookkeeping for columns held in the engine's force-loaded set.</summary>
public sealed class LodWorldGenLeases
{
    public sealed class Lease
    {
        public required int Index { get; init; }
        public required int Cx { get; init; }
        public required int Cz { get; init; }
        public long IssuedAtMs { get; set; }
        public int Attempts { get; set; } = 1;
        public bool Loaded { get; set; }
        public bool Captured { get; set; }
    }

    readonly Dictionary<long, Lease> active = new();

    public int Count => active.Count;
    public IEnumerable<Lease> Active => active.Values;

    public bool TryBegin(int index, int cx, int cz, long nowMs) =>
        active.TryAdd(LodColumnMap.Key(cx, cz), new Lease
        {
            Index = index,
            Cx = cx,
            Cz = cz,
            IssuedAtMs = nowMs,
        });

    public bool TryGet(int cx, int cz, out Lease? lease) =>
        active.TryGetValue(LodColumnMap.Key(cx, cz), out lease);

    public bool Remove(int cx, int cz) => active.Remove(LodColumnMap.Key(cx, cz));

    public List<Lease> Expired(long nowMs, long timeoutMs) =>
        active.Values.Where(lease => !lease.Captured && nowMs - lease.IssuedAtMs >= timeoutMs).ToList();

    /// <summary>
    /// Release every live lease exactly once on success. Failed releases stay tracked so
    /// shutdown can retry them instead of silently abandoning force-loaded columns.
    /// </summary>
    public int ReleaseAll(Func<int, int, bool> release)
    {
        int released = 0;
        foreach (Lease lease in active.Values.ToArray())
        {
            if (!release(lease.Cx, lease.Cz)) continue;
            active.Remove(LodColumnMap.Key(lease.Cx, lease.Cz));
            released++;
        }
        return released;
    }
}
