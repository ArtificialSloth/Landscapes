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

/// <summary>Chunk/L0-section mapping shared by capture persistence and the resumable plan.</summary>
public static class LodWorldGenSections
{
    public const int ChunkBlocks = 32;
    public const int ChunksPerLevelZeroSection = LodSection.SectionBlocks / ChunkBlocks;

    public static long KeyForChunk(int cx, int cz) => LodWorld.SectionKey(0,
        cx / ChunksPerLevelZeroSection, cz / ChunksPerLevelZeroSection);

    public static bool ContainsChunk(long levelZeroKey, int cx, int cz) =>
        LodWorld.KeyLevel(levelZeroKey) == 0 && KeyForChunk(cx, cz) == levelZeroKey;

    /// <summary>Whether all block columns in one chunk came from loaded/savegame terrain.</summary>
    public static bool HasAuthoritativeColumns(LodSection section, int cx, int cz)
    {
        int chunkColumns = LodSection.GridSize / ChunksPerLevelZeroSection;
        int startX = cx % ChunksPerLevelZeroSection * chunkColumns;
        int startZ = cz % ChunksPerLevelZeroSection * chunkColumns;
        for (int z = startZ; z < startZ + chunkColumns; z++)
        {
            for (int x = startX; x < startX + chunkColumns; x++)
            {
                int col = LodSection.ColumnIndex(x, z);
                if (!section.Captured[col] || !section.IsSnowStateObserved(col)) return false;
            }
        }
        return true;
    }
}

/// <summary>
/// Deterministic, clipped area enumeration for the real-world generation job. Tiles are
/// globally aligned and visit circular radial bands around the player, clockwise inside
/// each band. A tile's own columns also run nearest-first. The result keeps the large
/// persistence boundary while making generated map coverage grow like ordinary exploration
/// instead of as a square raster. Keeping this free of game types makes the ordering and
/// restart identity directly checkable.
/// </summary>
public sealed class LodWorldGenPlan
{
    public const int TileChunks = 16;
    public static readonly EnumWorldGenShape DefaultShape = EnumWorldGenShape.Circle;

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

        int minTileX = minCx / TileChunks, maxTileX = maxCx / TileChunks;
        int minTileZ = minCz / TileChunks, maxTileZ = maxCz / TileChunks;
        var candidates = new List<TileCandidate>();

        for (int tileZ = minTileZ; tileZ <= maxTileZ; tileZ++)
        for (int tileX = minTileX; tileX <= maxTileX; tileX++)
        {
            int tileMinX = Math.Max(minCx, tileX * TileChunks);
            int tileMaxX = Math.Min(maxCx, tileX * TileChunks + TileChunks - 1);
            int tileMinZ = Math.Max(minCz, tileZ * TileChunks);
            int tileMaxZ = Math.Min(maxCz, tileZ * TileChunks + TileChunks - 1);
            var tileColumns = new List<(int Cx, int Cz, long DistanceSq, double Angle)>();

            for (int cz = tileMinZ; cz <= tileMaxZ; cz++)
            for (int cx = tileMinX; cx <= tileMaxX; cx++)
            {
                long rx = (long)cx - centreCx;
                long rz = (long)cz - centreCz;
                if (shape == EnumWorldGenShape.Circle
                    && rx * rx + rz * rz > (long)radiusChunks * radiusChunks) continue;
                tileColumns.Add((cx, cz, rx * rx + rz * rz, Angle(rx, rz)));
            }

            if (tileColumns.Count == 0) continue; // circular corner outside the footprint
            tileColumns.Sort(static (a, b) =>
            {
                int byDistance = a.DistanceSq.CompareTo(b.DistanceSq);
                if (byDistance != 0) return byDistance;
                int byAngle = a.Angle.CompareTo(b.Angle);
                if (byAngle != 0) return byAngle;
                int byZ = a.Cz.CompareTo(b.Cz);
                return byZ != 0 ? byZ : a.Cx.CompareTo(b.Cx);
            });

            candidates.Add(new TileCandidate(tileX, tileZ,
                TileRadialBand(centreCx, centreCz, tileX, tileZ),
                Angle(tileX * TileChunks + (TileChunks - 1) / 2.0 - centreCx,
                    tileZ * TileChunks + (TileChunks - 1) / 2.0 - centreCz),
                tileColumns));
        }

        candidates.Sort(static (a, b) =>
        {
            int byBand = a.Band.CompareTo(b.Band);
            if (byBand != 0) return byBand;
            int byAngle = a.Angle.CompareTo(b.Angle);
            if (byAngle != 0) return byAngle;
            int byZ = a.Z.CompareTo(b.Z);
            return byZ != 0 ? byZ : a.X.CompareTo(b.X);
        });

        foreach (TileCandidate candidate in candidates)
        {
            int first = columns.Count;
            int tileOrdinal = tiles.Count;
            foreach (var column in candidate.Columns)
            {
                int index = columns.Count;
                columns.Add(new LodWorldGenColumn(column.Cx, column.Cz, tileOrdinal));
                indexByColumn.Add(LodColumnMap.Key(column.Cx, column.Cz), index);
            }
            tiles.Add(new LodWorldGenTile(candidate.X, candidate.Z,
                first, columns.Count - first));
        }
    }

    sealed record TileCandidate(int X, int Z, int Band, double Angle,
        List<(int Cx, int Cz, long DistanceSq, double Angle)> Columns);

    /// <summary>
    /// Tile-width radial band measured from the nearest point of the tile to the player.
    /// Band -1 is the unique player-containing tile. Band 0 is its adjacent reveal area,
    /// then each full 16-chunk step moves the clockwise sweep outward once.
    /// </summary>
    internal static int TileRadialBand(int centreCx, int centreCz, int tileX, int tileZ)
    {
        int tileMinX = tileX * TileChunks, tileMaxX = tileMinX + TileChunks - 1;
        int tileMinZ = tileZ * TileChunks, tileMaxZ = tileMinZ + TileChunks - 1;
        long dx = centreCx < tileMinX ? (long)tileMinX - centreCx
            : centreCx > tileMaxX ? (long)centreCx - tileMaxX : 0;
        long dz = centreCz < tileMinZ ? (long)tileMinZ - centreCz
            : centreCz > tileMaxZ ? (long)centreCz - tileMaxZ : 0;
        if (dx == 0 && dz == 0) return -1;
        return (int)(Math.Sqrt(dx * dx + dz * dz) / TileChunks);
    }

    /// <summary>Clockwise map angle starting east, normalized so sorting is stable.</summary>
    internal static double Angle(double dx, double dz)
    {
        double angle = Math.Atan2(dz, dx);
        return angle < 0 ? angle + Math.PI * 2 : angle;
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
    // Format 2 changed plan order from globally aligned square rings/rows to a circular
    // outward tile spiral. Format 3 repairs the durable acknowledgement mapping: one L0
    // section covers 2x2 chunks, while format 2 accidentally treated its section coordinate
    // as a chunk coordinate and could credit the wrong DoneBits. Those cursors are unsafe.
    public const int CurrentFormat = 3;

    public int Format { get; set; } = CurrentFormat;
    public int CentreCx { get; set; }
    public int CentreCz { get; set; }
    public int RadiusChunks { get; set; }
    public EnumWorldGenShape Shape { get; set; } = LodWorldGenPlan.DefaultShape;
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
}

/// <summary>
/// Exclusive authority belongs only to explicit/bounded jobs. A future automatic frontier
/// planner observes this state and yields; it must never acquire and block player work.
/// </summary>
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
