namespace VintageHorizons;

/// <summary>
/// World-space horizontal bounds of every section that currently owns an opaque or
/// water mesh. Adding a mesh expands the bounds in constant time. Removing an extreme
/// marks them for a rare rebuild from the remaining mesh keys; ordinary frames never
/// enumerate the mesh dictionaries.
/// </summary>
internal sealed class LodMeshBounds
{
    double minX, maxX, minZ, maxZ;

    public bool HasBounds { get; private set; }
    public bool Dirty { get; private set; }

    public void Include(long key)
    {
        GetFootprint(key, out double keyMinX, out double keyMaxX, out double keyMinZ, out double keyMaxZ);

        if (!HasBounds)
        {
            minX = keyMinX;
            maxX = keyMaxX;
            minZ = keyMinZ;
            maxZ = keyMaxZ;
            HasBounds = true;
            return;
        }

        minX = Math.Min(minX, keyMinX);
        maxX = Math.Max(maxX, keyMaxX);
        minZ = Math.Min(minZ, keyMinZ);
        maxZ = Math.Max(maxZ, keyMaxZ);
    }

    /// <summary>
    /// An interior removal cannot change the outer bounds. An extreme removal may or
    /// may not (another section can share that edge), so defer one exact rebuild until
    /// the distance is next requested.
    /// </summary>
    public void Remove(long key)
    {
        if (!HasBounds || Dirty) return;

        GetFootprint(key, out double keyMinX, out double keyMaxX, out double keyMinZ, out double keyMaxZ);
        if (keyMinX == minX || keyMaxX == maxX || keyMinZ == minZ || keyMaxZ == maxZ)
        {
            Dirty = true;
        }
    }

    public void Rebuild(IEnumerable<long> opaqueKeys, IEnumerable<long> waterKeys)
    {
        Clear();
        foreach (long key in opaqueKeys) Include(key);
        foreach (long key in waterKeys) Include(key);
        Dirty = false;
    }

    public double FarthestDistanceTo(double x, double z)
    {
        if (!HasBounds) return 0;

        double dx = Math.Max(Math.Abs(x - minX), Math.Abs(x - maxX));
        double dz = Math.Max(Math.Abs(z - minZ), Math.Abs(z - maxZ));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    public void Clear()
    {
        minX = maxX = minZ = maxZ = 0;
        HasBounds = false;
        Dirty = false;
    }

    static void GetFootprint(long key, out double minX, out double maxX, out double minZ, out double maxZ)
    {
        int footprint = LodWorld.KeyFootprintBlocks(key);
        minX = LodWorld.KeySx(key) * (double)footprint;
        minZ = LodWorld.KeySz(key) * (double)footprint;
        maxX = minX + footprint;
        maxZ = minZ + footprint;
    }
}

/// <summary>Pure far-edge arithmetic shared by the renderer and isolated checks.</summary>
internal static class LodFarDistance
{
    public const float VanillaMargin = 1536;
    public const float ProjectionMargin = 512;
    public const float MinimumProjectionDistance = 3000;

    /// <summary>
    /// lodvertexsurface.vsh's fade: it starts at this share of the view distance, and ends this
    /// far short of the distance it is handed. lodownershiptest.fsh discards past that end, so
    /// the end is where cached terrain stops, on a circle, whatever the sections around it hold.
    /// </summary>
    public const float ShaderFadeStartFraction = 0.785f;
    public const float ShaderFadeMargin = 512;

    /// <summary>The shader divides by the fade's length, which must stay above zero.</summary>
    public const float MinimumFadeLength = 64;

    /// <summary>
    /// How far inside the terrain's end a cell must lie for Farseer to give it up: the 24 a
    /// camera snapped to its chunk can be off, and 8 because the shader's fade is interpolated
    /// across a face and so reaches its end a few blocks early on a long flat one. Too little
    /// opens sky between the two; too much only lets them overlap where the terrain is nearly
    /// Farseer's colour already, and overlap outranks holes.
    /// </summary>
    public const double OwnedEdgeSlackBlocks = 32;

    const int CameraSnapBlocks = 32;

    public static float Effective(double farthestMeshDistance, float vanillaViewDistance, int farViewDistanceCap)
    {
        float far = (float)farthestMeshDistance;
        if (farViewDistanceCap > 0) far = Math.Min(far, farViewDistanceCap);
        return Math.Max(far, vanillaViewDistance + VanillaMargin);
    }

    public static float RequiredProjection(float effectiveFarDistance) =>
        Math.Max(MinimumProjectionDistance, effectiveFarDistance + ProjectionMargin);

    /// <summary>
    /// The distance handed to the terrain's fade. <see cref="Effective"/> holds its floor for the
    /// camera projection and the globe curve, and nothing here may move those, but left to reach
    /// the fade that floor let a short cap end terrain without one: a hard edge, in whole sections.
    /// Taking the smaller of the two keeps every cap the floor never touched exactly as it was.
    /// </summary>
    public static float TerrainFade(float effectiveFarDistance, float vanillaViewDistance,
        int farViewDistanceCap)
    {
        float fade = farViewDistanceCap > 0
            ? Math.Min(effectiveFarDistance, farViewDistanceCap + ShaderFadeMargin)
            : effectiveFarDistance;
        return Math.Max(fade, vanillaViewDistance * ShaderFadeStartFraction
            + ShaderFadeMargin + MinimumFadeLength);
    }

    public static float TerrainEnd(float terrainFade) => terrainFade - ShaderFadeMargin;

    /// <summary>The squared section-centre distance past which the cap stops a section drawing.</summary>
    public static float CullDistanceSquared(int farViewDistanceCap)
    {
        if (farViewDistanceCap <= 0) return float.MaxValue;
        float cull = farViewDistanceCap + LodSection.SectionBlocks;
        return cull * cull;
    }

    /// <summary>
    /// The cap's one test, shared by the draw and by what Farseer and the fog are told is drawn.
    /// The two disagreeing is a hole: a mesh in memory that nothing draws, with Farseer cut away
    /// over it.
    /// </summary>
    public static bool SectionPastCull(double originX, double originZ, int footprint,
        double cameraX, double cameraZ, float cullDistanceSquared)
    {
        double dx = originX + footprint / 2.0 - cameraX;
        double dz = originZ + footprint / 2.0 - cameraZ;
        return dx * dx + dz * dz > cullDistanceSquared;
    }

    /// <summary>
    /// A camera coordinate moved to its chunk's middle. Ownership against the terrain's end asks
    /// from here, so the whole circle changes hands as the player crosses a chunk line, as the
    /// vanilla edge does. Asked from the camera itself some cell flips on almost every moving
    /// frame, and each flip rebuilds the seam mask and prepares a fog field on the render thread.
    /// </summary>
    public static double SnapCamera(double coordinate) =>
        (Math.Floor(coordinate / CameraSnapBlocks) + 0.5) * CameraSnapBlocks;

    /// <summary>
    /// Whether every part of a cell lies inside the terrain's end, from a snapped camera. The
    /// farthest corner decides: a cell cut by the circle stays Farseer's, so the terrain's
    /// discarded part has something behind it.
    /// </summary>
    public static bool CellInsideTerrain(double cellMinX, double cellMinZ, int cellBlocks,
        double snappedCameraX, double snappedCameraZ, float terrainEnd)
    {
        double reach = terrainEnd - OwnedEdgeSlackBlocks;
        if (reach <= 0) return false;
        double dx = Math.Max(Math.Abs(cellMinX - snappedCameraX),
            Math.Abs(cellMinX + cellBlocks - snappedCameraX));
        double dz = Math.Max(Math.Abs(cellMinZ - snappedCameraZ),
            Math.Abs(cellMinZ + cellBlocks - snappedCameraZ));
        return dx * dx + dz * dz <= reach * reach;
    }

    /// <summary>
    /// Whether no cell of an area can pass <see cref="CellInsideTerrain"/>: its nearest point
    /// already lies past the terrain's end, and a cell is judged by its farthest corner, which is
    /// never nearer. Lets Farseer skip working out coverage for tiles nothing cached can reach.
    /// </summary>
    public static bool AreaOutsideTerrain(double minX, double minZ, int blocks,
        double snappedCameraX, double snappedCameraZ, float terrainEnd)
    {
        double reach = terrainEnd - OwnedEdgeSlackBlocks;
        if (reach <= 0) return true;
        double dx = Math.Max(0, Math.Max(minX - snappedCameraX, snappedCameraX - (minX + blocks)));
        double dz = Math.Max(0, Math.Max(minZ - snappedCameraZ, snappedCameraZ - (minZ + blocks)));
        return dx * dx + dz * dz > reach * reach;
    }
}

/// <summary>
/// Conservative inner handoff from cached terrain to vanilla chunks. The old boundary
/// sat at 78.5% of configured view distance and could outrun streaming; removing it
/// entirely made approximate cached surfaces compete with ready vanilla geometry. Keep
/// at least 192 blocks of overlap and never hand off outside half the configured radius.
/// </summary>
internal static class LodNearHandoff
{
    public const float MinimumFallbackOverlap = 192;
    public const float MaximumInnerFraction = 0.5f;

    public static float InnerDiscardRadius(float vanillaViewDistance)
    {
        float distance = Math.Max(0, vanillaViewDistance);
        return Math.Max(0, Math.Min(
            distance * MaximumInnerFraction,
            distance - MinimumFallbackOverlap));
    }

    /// <summary>
    /// How many vanilla chunk columns around the camera's own hide cached water outright.
    /// ZERO: there is no near rule by default, and the ownership mask is the only boundary.
    ///
    /// The unit is chunks so that any non-zero value puts its boundary on a chunk edge. Two
    /// earlier builds measured it in blocks and both drew a visible ring, because a per-pixel
    /// distance test is a circle however small you make it.
    /// </summary>
    public const int WaterNearChunks = 0;

    /// <summary>
    /// Where the engine's own water is still at full opacity, as a fraction of the distance
    /// uploaded to <c>chunkliquid</c>: it holds the alpha ceiling to 0.725 and reaches zero at
    /// 0.8, while the ground keeps drawing past 1.0.
    ///
    /// Kept as documentation of the engine's behaviour and used by nothing. It was once the
    /// bound on how far ownership could hide cached water, and that bound was a radius: past
    /// it, cached water drew into chunks vanilla was already drawing. Cached water now obeys
    /// the ownership mask at every distance, so its boundary is a chunk edge and no fraction
    /// of the view distance appears in the water path at all.
    /// </summary>
    public const float VanillaWaterFadeStart = 0.725f;
}

/// <summary>
/// Stabilizes the readiness-derived near handoff. Shrinkage applies immediately, because a
/// late shrink leaves cached terrain suppressed where vanilla no longer draws - a hole.
/// Growth waits for the larger radius to hold, because an early or oscillating grow
/// suppresses cached terrain that vanilla has not actually replaced yet, and pops.
/// While waiting, the smallest radius seen during the hold is what eventually applies, so
/// continuous movement cannot stall growth and cannot smuggle a too-large value through.
/// </summary>
internal sealed class LodNearHandoffState
{
    public const float Step = 32;
    public const long GrowHoldMilliseconds = 500;

    float applied;
    float growCandidate;
    long growCandidateSince;
    bool hasGrowCandidate;

    public float Applied => applied;

    public float Update(float ownedRadius, long nowMilliseconds)
    {
        float target = QuantizeDown(Math.Max(0, ownedRadius));
        if (target <= applied)
        {
            applied = target;
            hasGrowCandidate = false;
            return applied;
        }

        if (!hasGrowCandidate)
        {
            hasGrowCandidate = true;
            growCandidate = target;
            growCandidateSince = nowMilliseconds;
            return applied;
        }

        if (target < growCandidate) growCandidate = target;
        if (nowMilliseconds - growCandidateSince < GrowHoldMilliseconds) return applied;

        applied = growCandidate;
        hasGrowCandidate = false;
        return applied;
    }

    static float QuantizeDown(float distance) => MathF.Floor(distance / Step) * Step;
}

internal readonly record struct LodFarPlaneUpdate(float Distance, bool Changed);

/// <summary>
/// Stabilizes the expensive game-camera projection reset. Required growth is applied
/// immediately. Shrinkage must remain in the same lower 512-block band for five seconds,
/// preventing ordinary camera movement from bouncing the projection between two values.
/// </summary>
internal sealed class LodFarPlaneState
{
    public const float Step = 512;
    public const long ShrinkCooldownMilliseconds = 5000;

    float appliedDistance;
    float shrinkCandidate;
    long shrinkCandidateSince;
    bool hasShrinkCandidate;

    public LodFarPlaneUpdate Update(float requiredDistance, long nowMilliseconds)
    {
        float target = QuantizeUp(requiredDistance);

        if (appliedDistance == 0 || target > appliedDistance)
        {
            appliedDistance = target;
            hasShrinkCandidate = false;
            return new LodFarPlaneUpdate(appliedDistance, true);
        }

        if (target == appliedDistance)
        {
            hasShrinkCandidate = false;
            return new LodFarPlaneUpdate(appliedDistance, false);
        }

        if (!hasShrinkCandidate || shrinkCandidate != target || nowMilliseconds < shrinkCandidateSince)
        {
            shrinkCandidate = target;
            shrinkCandidateSince = nowMilliseconds;
            hasShrinkCandidate = true;
            return new LodFarPlaneUpdate(appliedDistance, false);
        }

        if (nowMilliseconds - shrinkCandidateSince < ShrinkCooldownMilliseconds)
        {
            return new LodFarPlaneUpdate(appliedDistance, false);
        }

        appliedDistance = target;
        hasShrinkCandidate = false;
        return new LodFarPlaneUpdate(appliedDistance, true);
    }

    public void Reset()
    {
        appliedDistance = 0;
        shrinkCandidate = 0;
        shrinkCandidateSince = 0;
        hasShrinkCandidate = false;
    }

    static float QuantizeUp(float distance) => MathF.Ceiling(distance / Step) * Step;
}
