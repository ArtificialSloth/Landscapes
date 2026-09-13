namespace VintageHorizons;

/// <summary>
/// The raw temperature, rainfall and current snow line of every 64-block cell around the
/// player, so distant terrain can be coloured by the climate it stands in rather than by the
/// climate under the player's feet.
///
/// This is the whole spatial half of distant climate rendering. Everything else a tint needs is
/// either arithmetic - the lapse rate that turns raw temperature into the temperature at a
/// vertex's own height, and the season blend weight - or a lookup in a colour map. Temperature
/// and rainfall remain two bytes per cell; snow adds one quantised threshold that is refreshed
/// as the season moves. This is still a field of climate rather than colours: colours would need
/// one plane per tint slot and would also have to be resampled every time the season moved.
///
/// **64 blocks** because that is one level-0 section edge, so a section origin is cell-aligned
/// at every LOD level and the shader can build a cell address without a large world coordinate
/// rounding onto its neighbour. It is also finer than the climate needs: measured on the
/// owner's world, raw temperature moves 2.3 counts and rainfall 10.8 counts per 64 blocks, and
/// both scale exactly linearly from 32 to 256 blocks - climate is a smooth ramp at this scale,
/// not a noisy field, so interpolating between cell centres costs curvature only.
///
/// Snow is deliberately part of the same field. The old renderer calculated one snow line at
/// the player's position and sent that single height to the whole view, so walking into a cold
/// chunk repainted every visible mountain white on the next refresh. Carrying the snow line with
/// the raw climate gives missing cells the same nearest-known behaviour and lets the shader
/// interpolate the threshold at the terrain's own X/Z.
///
/// **The client does not know the climate everywhere.** It holds map regions for ground it has
/// been sent - about 1,000 blocks on arrival, growing toward 3,000 as the player explores,
/// against 8,192 blocks of drawn terrain - and asking beyond that returns a fixed default
/// rather than an error. Cells with no real answer are therefore filled by spreading outward
/// from the nearest cell that has one, so there is no line in the terrain where knowledge ends
/// and the far field takes the nearest real climate instead of the player's own. That is
/// strictly better than one climate for the whole world and degrades to it exactly when
/// nothing is known but the ground underfoot.
///
/// Storage follows <see cref="VanillaSeamDistanceField"/>: a wrapped ring whose addressing the
/// shader reconstructs, held to it by a static check.
/// </summary>
internal sealed class LodClimateField
{
    /// <summary>Blocks per axis in one climate cell.</summary>
    public const int CellBlocks = 64;

    /// <summary>
    /// Chamfer weights for the spread, in thirds of a cell, so a diagonal step costs 4/3
    /// rather than 1. The same weights the seam field uses and for the same reason: a plain
    /// Manhattan sweep spreads in a diamond, and a diamond of climate around a loaded region
    /// would put a corner in the colour of the horizon.
    /// </summary>
    const int Orthogonal = 3;
    const int Diagonal = 4;

    const int Unreached = ushort.MaxValue;

    readonly int capacity;
    readonly int capacityMask;
    readonly int[] texels;
    readonly ushort[] spread;
    readonly byte[] temperature;
    readonly byte[] rainfall;
    readonly float[] snowLine;
    readonly ulong[] snowStateObserved;

    int minCellX;
    int minCellZ;
    int width;
    int depth;

    int pendingMinCellX;
    int pendingMinCellZ;
    float snowMinY;
    float snowStepY = 1f;
    float pendingSnowMinY;
    float pendingSnowStepY = 1f;

    /// <summary>
    /// Cells answered so far in the refresh being BUILT, and cells in the one on screen.
    ///
    /// Two counters, not one, because a refresh takes a few hundred frames and the published
    /// field has to keep describing itself the whole time. With one counter, starting a
    /// refresh reset it to zero, Active went false, and every fragment in the world fell back
    /// to the old whole-world tint for the length of the rebuild - a colour flip to the
    /// previous system and back, every cadence.
    /// </summary>
    int pendingKnownCells;
    int knownCells;

    public LodClimateField(int cellCapacity)
    {
        if (cellCapacity <= 0 || (cellCapacity & (cellCapacity - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(cellCapacity),
                "Climate field capacity must be a positive power of two.");

        capacity = cellCapacity;
        capacityMask = cellCapacity - 1;
        int cells = checked(cellCapacity * cellCapacity);
        texels = new int[cells];
        spread = new ushort[cells];
        temperature = new byte[cells];
        rainfall = new byte[cells];
        snowLine = new float[cells];
        snowStateObserved = new ulong[(cells + 63) / 64];
        Clear();
    }

    public int Capacity => capacity;
    public int Width => capacity;
    public int Height => capacity;
    public int[] Texels => texels;
    public int MinCellX => minCellX;
    public int MinCellZ => minCellZ;
    public int WidthCells => width;
    public int DepthCells => depth;
    public int KnownCells => knownCells;
    public float SnowMinY => snowMinY;
    public float SnowStepY => snowStepY;

    /// <summary>
    /// Usable only once at least one cell carries a real climate. With none, the spread has
    /// nothing to spread and the field must not be drawn from at all - the caller keeps its
    /// previous behaviour instead. Publishing a field of zeroes would be a confident claim
    /// that the whole world is freezing and bone dry.
    /// </summary>
    public bool Active => width > 0 && depth > 0 && knownCells > 0;
    public long Bytes => texels.LongLength * sizeof(int) + spread.LongLength * sizeof(ushort)
        + temperature.LongLength + rainfall.LongLength + snowLine.LongLength * sizeof(float)
        + snowStateObserved.LongLength * sizeof(ulong);

    /// <summary>True when the buffer differs from what the GPU last accepted.</summary>
    public bool Dirty { get; private set; }

    public long Builds { get; private set; }
    public long Uploads { get; private set; }

    /// <summary>
    /// Wrapped address of one cell, matching the addressing the vertex shader reconstructs.
    /// A static check holds the two together, because a silent disagreement here misplaces
    /// every climate at once and reads as a colour bug rather than an indexing one.
    /// </summary>
    public static int TexelIndex(int cellX, int cellZ, int capacity)
    {
        int mask = capacity - 1;
        return (cellZ & mask) * capacity + (cellX & mask);
    }

    /// <summary>
    /// The cell a world block falls in. Floor division, not truncation: block -1 belongs to
    /// cell -1, and truncation would put it in cell 0 alongside block 0, folding two cells of
    /// terrain onto one climate on whichever side of the origin the world is.
    /// </summary>
    public static int CellOf(int blockCoordinate) =>
        blockCoordinate >= 0
            ? blockCoordinate / CellBlocks
            : -(((-blockCoordinate) + CellBlocks - 1) / CellBlocks);

    /// <summary>Nothing known anywhere, which is what an unbuilt field must publish.</summary>
    public void Clear()
    {
        Array.Clear(texels);
        Array.Fill(spread, (ushort)Unreached);
        Array.Clear(temperature);
        Array.Clear(rainfall);
        Array.Clear(snowLine);
        Array.Clear(snowStateObserved);
        width = depth = 0;
        pendingKnownCells = 0;
        knownCells = 0;
        Dirty = true;
    }

    /// <summary>Answers the climate at one world position, or false where nothing is known.</summary>
    public delegate bool Sampler(int blockX, int blockZ, out byte temperature, out byte rainfall);

    /// <summary>
    /// The same real climate answer plus the current snow threshold at that cell. Kept as a
    /// separate delegate so climate-only checks and callers do not have to invent seasonal
    /// state; the renderer uses this overload.
    /// </summary>
    public delegate bool SeasonalSampler(int blockX, int blockZ,
        out byte temperature, out byte rainfall, out float snowLineY,
        out bool snowStateIsObserved);

    /// <summary>
    /// Start a window centred on the camera. The window is re-sampled wholesale rather than
    /// scrolled: the wrapped ring hands a reused slot to different world coordinates, and a
    /// full rewrite is what stops the shader reading another place's climate.
    /// </summary>
    public void BeginRefresh(int camBlockX, int camBlockZ)
    {
        BeginRefresh(camBlockX, camBlockZ, 0f, 65_000f);
    }

    /// <summary>
    /// Start a window and define how its snow line is encoded in the texture alpha byte.
    /// Snow is blended over 24 blocks, so one byte across the world's vertical span retains
    /// more precision than the visible transition needs without adding another sampler.
    /// </summary>
    public void BeginRefresh(int camBlockX, int camBlockZ, float minSnowY, float maxSnowY)
    {
        if (!float.IsFinite(minSnowY) || !float.IsFinite(maxSnowY) || maxSnowY <= minSnowY)
            throw new ArgumentOutOfRangeException(nameof(maxSnowY),
                "Snow encoding range must be finite and non-empty.");

        pendingMinCellX = CellOf(camBlockX) - capacity / 2;
        pendingMinCellZ = CellOf(camBlockZ) - capacity / 2;
        pendingSnowMinY = minSnowY;
        pendingSnowStepY = (maxSnowY - minSnowY) / 255f;
        Array.Clear(snowStateObserved);
        for (int z = 0; z < capacity; z++)
        {
            int row = ((pendingMinCellZ + z) & capacityMask) * capacity;
            for (int x = 0; x < capacity; x++)
                spread[row + ((pendingMinCellX + x) & capacityMask)] = Unreached;
        }
        pendingKnownCells = 0;
    }

    /// <summary>Rows of cells in one refresh. Spread over frames by the caller.</summary>
    public int RefreshRowCount => capacity;

    /// <summary>
    /// Sample one row of the pending window. Only cells the sampler can actually answer are
    /// marked known; the rest are left for the spread to fill, because a cell filled with the
    /// game's no-data default would be a confident wrong climate rather than a missing one.
    /// </summary>
    public void RefreshRow(int row, Sampler sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        bool WithoutSnow(int blockX, int blockZ,
            out byte temperature, out byte rainfall, out float snowLineY,
            out bool snowStateIsObserved)
        {
            snowLineY = pendingSnowMinY + pendingSnowStepY * 255f;
            snowStateIsObserved = false;
            return sample(blockX, blockZ, out temperature, out rainfall);
        }

        RefreshRow(row, WithoutSnow);
    }

    /// <summary>Sample one row including the current, position-specific snow threshold.</summary>
    public void RefreshRow(int row, SeasonalSampler sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (row < 0 || row >= capacity) return;

        int cellZ = pendingMinCellZ + row;
        int at = (cellZ & capacityMask) * capacity;
        int blockZ = cellZ * CellBlocks + CellBlocks / 2;
        for (int x = 0; x < capacity; x++)
        {
            int cellX = pendingMinCellX + x;
            int index = at + (cellX & capacityMask);
            int blockX = cellX * CellBlocks + CellBlocks / 2;
            if (!sample(blockX, blockZ, out byte t, out byte r, out float snow,
                out bool observed)) continue;
            temperature[index] = t;
            rainfall[index] = r;
            snowLine[index] = Math.Clamp(snow, pendingSnowMinY,
                pendingSnowMinY + pendingSnowStepY * 255f);
            SetSnowStateObserved(index, observed);
            spread[index] = 0;
            pendingKnownCells++;
        }
    }

    /// <summary>
    /// Fill every unknown cell from the nearest known one and publish. Two chamfer sweeps, the
    /// same shape as the seam field's, except that they carry the climate along with the
    /// distance rather than only the distance.
    /// </summary>
    public void CompleteRefresh()
    {
        knownCells = pendingKnownCells;
        minCellX = pendingMinCellX;
        minCellZ = pendingMinCellZ;
        snowMinY = pendingSnowMinY;
        snowStepY = pendingSnowStepY;
        width = depth = capacity;
        SweepForward();
        SweepBackward();
        WriteTexels();
        Builds++;
        Dirty = true;
    }

    void Relax(int fromIndex, int intoIndex, int cost)
    {
        int candidate = spread[fromIndex] + cost;
        if (candidate >= spread[intoIndex]) return;
        spread[intoIndex] = (ushort)Math.Min(candidate, Unreached - 1);
        temperature[intoIndex] = temperature[fromIndex];
        rainfall[intoIndex] = rainfall[fromIndex];
        snowLine[intoIndex] = snowLine[fromIndex];
        // Climate is intentionally spread into gaps; capture provenance is not. An
        // observed source cannot claim that its unknown neighbour was observed too.
        SetSnowStateObserved(intoIndex, observed: false);
    }

    int Index(int cellX, int cellZ) =>
        ((minCellZ + cellZ) & capacityMask) * capacity + ((minCellX + cellX) & capacityMask);

    void SweepForward()
    {
        for (int z = 0; z < depth; z++)
        {
            for (int x = 0; x < width; x++)
            {
                int into = Index(x, z);
                if (spread[into] == 0) continue;
                if (x > 0) Relax(Index(x - 1, z), into, Orthogonal);
                if (z > 0)
                {
                    Relax(Index(x, z - 1), into, Orthogonal);
                    if (x > 0) Relax(Index(x - 1, z - 1), into, Diagonal);
                    if (x < width - 1) Relax(Index(x + 1, z - 1), into, Diagonal);
                }
            }
        }
    }

    void SweepBackward()
    {
        for (int z = depth - 1; z >= 0; z--)
        {
            for (int x = width - 1; x >= 0; x--)
            {
                int into = Index(x, z);
                if (spread[into] == 0) continue;
                if (x < width - 1) Relax(Index(x + 1, z), into, Orthogonal);
                if (z < depth - 1)
                {
                    Relax(Index(x, z + 1), into, Orthogonal);
                    if (x < width - 1) Relax(Index(x + 1, z + 1), into, Diagonal);
                    if (x > 0) Relax(Index(x - 1, z + 1), into, Diagonal);
                }
            }
        }
    }

    /// <summary>
    /// Pack for upload. BGRA ints with red at bits 16-23, the way this renderer's other
    /// CPU-built textures are packed, because `LoadOrUpdateTextureFromBgra` is the upload path.
    /// Red carries temperature, green rainfall, blue carries how far the climate was spread,
    /// and alpha carries the position-specific snow line across the published vertical range.
    /// </summary>
    void WriteTexels()
    {
        for (int z = 0; z < depth; z++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = Index(x, z);
                // A cell the sweep never reached has no climate behind it, so its stored
                // temperature and rainfall are whatever the previous window left there.
                // Publishing that would paint one place with another place's climate, which
                // is the exact failure this field exists to remove - so it goes out as
                // nothing, and Active refuses the whole field when that is every cell.
                if (spread[i] == Unreached)
                {
                    texels[i] = unchecked((int)0xFF000000) | 255;
                    continue;
                }
                int far = Math.Min(255, (int)spread[i]);
                int snow = IsSnowStateObserved(i)
                    ? 255
                    : Math.Clamp((int)MathF.Round(
                        (snowLine[i] - snowMinY) / snowStepY), 0, 255);
                texels[i] = (snow << 24)
                    | (temperature[i] << 16) | (rainfall[i] << 8) | far;
            }
        }
    }

    /// <summary>
    /// The climate the SHADER would read at a world position, computed the same way from the
    /// same published texels: half-cell shift, clamp to the window, bilinear between four
    /// cells. Exists so a diagnostic reports what is actually being drawn rather than what the
    /// sampler was told, and so a check can hold this and the GLSL to the same answer.
    /// </summary>
    public bool TryClimateAt(double blockX, double blockZ,
        out float temperature, out float rainfall, out int spread)
    {
        temperature = rainfall = 0f;
        spread = 255;
        if (!Active) return false;

        double fx = blockX / CellBlocks - 0.5;
        double fz = blockZ / CellBlocks - 0.5;
        int cx = Math.Clamp((int)Math.Floor(fx), minCellX, minCellX + capacity - 2);
        int cz = Math.Clamp((int)Math.Floor(fz), minCellZ, minCellZ + capacity - 2);
        float tx = (float)Math.Clamp(fx - cx, 0.0, 1.0);
        float tz = (float)Math.Clamp(fz - cz, 0.0, 1.0);

        Read(cx, cz, out float t00, out float r00, out int s00);
        Read(cx + 1, cz, out float t10, out float r10, out int s10);
        Read(cx, cz + 1, out float t01, out float r01, out int s01);
        Read(cx + 1, cz + 1, out float t11, out float r11, out int s11);

        temperature = Lerp(Lerp(t00, t10, tx), Lerp(t01, t11, tx), tz);
        rainfall = Lerp(Lerp(r00, r10, tx), Lerp(r01, r11, tx), tz);
        spread = Math.Max(Math.Max(s00, s10), Math.Max(s01, s11));
        return true;
    }

    /// <summary>The snow threshold the shader reads at a world position.</summary>
    public bool TrySnowLineAt(double blockX, double blockZ, out float snowLineY)
    {
        snowLineY = 0f;
        if (!Active) return false;

        double fx = blockX / CellBlocks - 0.5;
        double fz = blockZ / CellBlocks - 0.5;
        int cx = Math.Clamp((int)Math.Floor(fx), minCellX, minCellX + capacity - 2);
        int cz = Math.Clamp((int)Math.Floor(fz), minCellZ, minCellZ + capacity - 2);
        float tx = (float)Math.Clamp(fx - cx, 0.0, 1.0);
        float tz = (float)Math.Clamp(fz - cz, 0.0, 1.0);

        float y00 = ReadSnowLine(cx, cz);
        float y10 = ReadSnowLine(cx + 1, cz);
        float y01 = ReadSnowLine(cx, cz + 1);
        float y11 = ReadSnowLine(cx + 1, cz + 1);
        snowLineY = Lerp(Lerp(y00, y10, tx), Lerp(y01, y11, tx), tz);
        return true;
    }

    void Read(int cellX, int cellZ, out float temperature, out float rainfall, out int spread)
    {
        int v = texels[TexelIndex(cellX, cellZ, capacity)];
        temperature = (v >> 16) & 0xFF;
        rainfall = (v >> 8) & 0xFF;
        spread = v & 0xFF;
    }

    float ReadSnowLine(int cellX, int cellZ)
    {
        int v = texels[TexelIndex(cellX, cellZ, capacity)];
        return snowMinY + ((v >> 24) & 0xFF) * snowStepY;
    }

    bool IsSnowStateObserved(int index) =>
        (snowStateObserved[index >> 6] & (1UL << (index & 63))) != 0;

    void SetSnowStateObserved(int index, bool observed)
    {
        int word = index >> 6;
        ulong mask = 1UL << (index & 63);
        if (observed) snowStateObserved[word] |= mask;
        else snowStateObserved[word] &= ~mask;
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public void MarkUploaded()
    {
        Dirty = false;
        Uploads++;
    }
}
