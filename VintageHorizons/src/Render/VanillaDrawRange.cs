namespace VintageHorizons;

/// <summary>
/// The engine's own per-frame terrain range cull, transcribed rather than bounded.
///
/// <para>
/// G43 established that <c>ModelDataPoolLocation.IsVisible</c> ends in
/// <c>FrustumCulling.InFrustumAndRange</c>, whose last clause is a horizontal distance test
/// from the player's integer block position to the mesh's bounding-sphere centre. Every
/// per-chunk signal the tracker can read stays latched at "drawing" when that clause fails,
/// which is why ownership needs it at all.
/// </para>
/// <para>
/// Until now the mod could not read the sphere, so it bounded it: the centre is somewhere in
/// the 32-block column, therefore deny anything whose nearest face passes
/// <c>viewDistance - 46</c> (<see cref="VanillaRenderReadiness.SphereCentreSlackBlocks"/>).
/// That is correct and costs a permanent overlap ring the width of the slack. The sphere is
/// on the pool location as a public field, so the slack is not necessary - it is the price of
/// not looking. <see cref="VanillaChunkGeometry.TryReadDrawBounds"/> looks.
/// </para>
/// <para>
/// **This is a transcription, not a model.** Every constant and every comparison below is
/// copied from the installed 1.22.7 IL, including the widths the engine happens to use: the
/// distance is computed in double and rounded to float by <c>BlockPos.HorDistanceSqTo</c>
/// before the comparison, <c>ViewDistanceSq</c> is an int, <c>lod0BiasSq</c> a float and
/// <c>lod2BiasSq</c> a double. Reproducing those widths is not pedantry - a knife-edge cell
/// that disagrees with the engine by one ulp is a fragment nobody draws. Do not "clean up"
/// the types. <c>VanillaDrawRangeChecks</c> holds this against an independent copy of the
/// same switch.
/// </para>
/// <para>
/// The frustum clause is deliberately absent; see <see cref="Draws"/>.
/// </para>
/// </summary>
internal readonly struct VanillaDrawRangeState
{
    /// <summary>Terrain LOD pools, from `EnumLodPool`: 0 nearby detail, 1 everywhere, 2 everywhere except far, 3 far only.</summary>
    public const int LodLevelCount = 4;

    /// <summary>Every level set: what a chunk with all four pools would report.</summary>
    public const int AllLodLevels = 0b1111;

    /// <summary>
    /// Ordinary terrain - `CubeTesselator`, `TopsoilTesselator`, `LiquidTesselator` - all
    /// tesselate into pool level 1, whose bound is the widest of the four. A chunk holding
    /// this is drawn out to the full view distance and nothing nearer.
    /// </summary>
    public const int EverywhereLod = 1 << 1;

    /// <summary>False when the engine's culler could not be read; every caller must then keep its old rule.</summary>
    public readonly bool Available;

    /// <summary>
    /// `player.Entity.Pos.AsBlockPos`, which is what `ClientMain` hands the culler once per
    /// frame - the PLAYER, truncated to whole blocks, not the camera. In third person the two
    /// are several blocks apart and only this one decides what the engine draws.
    /// </summary>
    public readonly int PlayerBlockX;
    public readonly int PlayerBlockZ;

    /// <summary>`viewDistance * viewDistance + 400`, from the CLIENT setting rather than the server-approved value.</summary>
    public readonly int ViewDistanceSq;
    public readonly float Lod0BiasSq;
    public readonly double Lod2BiasSq;

    public VanillaDrawRangeState(int playerBlockX, int playerBlockZ, int viewDistanceSq,
        float lod0BiasSq, double lod2BiasSq)
    {
        Available = true;
        PlayerBlockX = playerBlockX;
        PlayerBlockZ = playerBlockZ;
        ViewDistanceSq = viewDistanceSq;
        Lod0BiasSq = lod0BiasSq;
        Lod2BiasSq = lod2BiasSq;
    }

    /// <summary>
    /// `BlockPos.HorDistanceSqTo`, including the cast to float that the engine's own return
    /// type performs before the comparison widens it back to double.
    /// </summary>
    public static float HorizontalDistanceSq(int blockX, int blockZ, float x, float z)
    {
        double dx = x - (double)blockX;
        double dz = z - (double)blockZ;
        return (float)(dx * dx + dz * dz);
    }

    /// <summary>
    /// One level's clause from `InFrustumAndRange`. The `default` arm returning false is the
    /// engine's, and it matters: an unrecognised level is not drawn, so a future pool level
    /// this mod does not know about costs an overlap rather than a hole.
    /// </summary>
    public bool DrawsAtLod(double distanceSq, int lodLevel)
    {
        switch (lodLevel)
        {
            case 0:
                if (Lod0BiasSq > 0f) return distanceSq < (double)(Lod0BiasSq + 1024f);
                return false;
            case 1:
                return distanceSq < (double)ViewDistanceSq;
            case 2:
                return distanceSq <= Lod2BiasSq;
            case 3:
                if (distanceSq > Lod2BiasSq) return distanceSq < (double)ViewDistanceSq;
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Whether the engine's range clause admits any of the pools this chunk actually holds.
    ///
    /// <para>
    /// A chunk submits if ANY of its pool locations survives, and they share one bounding
    /// sphere (`ChunkRenderer.AddTesselatedChunk` hands `tesschunk.boundingSphere` to both the
    /// centre and edge parts), so the whole chunk reduces to one centre plus the set of levels
    /// present. An empty mask draws nothing, which is the correct answer for a chunk holding
    /// no drawable mesh.
    /// </para>
    /// <para>
    /// **The frustum planes are deliberately not here.** Including them would make ownership a
    /// function of where the player is looking, so every turn would flap the mask, churn the
    /// section aggregates and invalidate the temporal occlusion answers (G55, G56) - to hide
    /// cached terrain that is off screen anyway. Leaving them out is also the safe direction
    /// by construction: the engine's real predicate is this one ANDed with the planes, so what
    /// is admitted here is a superset of what is admitted there, and a superset can only
    /// REFUSE ownership that the engine would have granted. Refused ownership is cached
    /// terrain drawn where vanilla also draws - an overlap, never a hole.
    /// </para>
    /// </summary>
    public bool Draws(float centreX, float centreZ, int lodMask)
    {
        if (!Available || lodMask == 0) return false;
        double distanceSq = HorizontalDistanceSq(PlayerBlockX, PlayerBlockZ, centreX, centreZ);
        for (int level = 0; level < LodLevelCount; level++)
        {
            if ((lodMask & (1 << level)) == 0) continue;
            if (DrawsAtLod(distanceSq, level)) return true;
        }
        return false;
    }

    /// <summary>
    /// <see cref="Draws"/> with the chunk treated as <paramref name="marginBlocks"/> further
    /// away than it is, so ownership is given up early rather than exactly.
    ///
    /// <para>
    /// The margin covers the one term of the engine's predicate that neither the sphere nor
    /// the player position can answer: whether the chunk is still LOADED. Vanilla unloads on
    /// its own schedule, ownership of an unloaded chunk is only withdrawn when a probe next
    /// reaches it, and a probe reaches it within about a second - so for that second, ground
    /// vanilla has dropped has nothing drawing it. Handing it back early is what covers that.
    /// </para>
    /// <para>
    /// It is NOT the sphere-placement slack. That one was 46 blocks of not knowing where the
    /// engine measures from, and reading the sphere removes it completely and provably. This
    /// is a smaller, separate guard against a latency, it is measured in the same units, and
    /// it is a knob because the right value is a question about how fast vanilla drops chunks
    /// in play, which no offline check can answer.
    /// </para>
    /// <para>
    /// Applied to the distance rather than to any one bound, so it pulls in whichever of the
    /// four level clauses is actually deciding - and then ANDed with the unmargined answer,
    /// which is not belt and braces. Level 3 is far-ONLY: its clause has a lower bound as well
    /// as an upper one, so moving a chunk outward can switch it on rather than off. A margin
    /// that only inflated the distance therefore GRANTED ownership, inside the level-2 bias,
    /// for a chunk holding nothing but far geometry - a hole guard generating holes. The AND
    /// makes the direction structural instead of arguable: this can only ever subtract from
    /// what <see cref="Draws"/> admits, whatever the engine's clauses do next.
    /// </para>
    /// <para>
    /// Zero is exactly <see cref="Draws"/>.
    /// </para>
    /// </summary>
    public bool DrawsWithMargin(float centreX, float centreZ, int lodMask, float marginBlocks)
    {
        if (!Available || lodMask == 0) return false;
        if (!Draws(centreX, centreZ, lodMask)) return false;
        if (marginBlocks <= 0f) return true;

        double distance = Math.Sqrt(HorizontalDistanceSq(PlayerBlockX, PlayerBlockZ, centreX, centreZ))
            + marginBlocks;
        double distanceSq = distance * distance;
        for (int level = 0; level < LodLevelCount; level++)
        {
            if ((lodMask & (1 << level)) == 0) continue;
            if (DrawsAtLod(distanceSq, level)) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether anything this state decides has moved since <paramref name="previous"/>.
    ///
    /// <para>
    /// This is what makes a per-frame exact rule affordable, and it is exact rather than a
    /// heuristic: every input is either an integer the engine itself quantises - the player's
    /// block position, which changes at most once per block walked and never while standing
    /// still - or a setting. Nothing here varies continuously with the camera, so a sweep
    /// gated on this runs zero times in a settled view and once per block in motion.
    /// </para>
    /// </summary>
    public bool DiffersFrom(in VanillaDrawRangeState previous) =>
        Available != previous.Available
        || PlayerBlockX != previous.PlayerBlockX
        || PlayerBlockZ != previous.PlayerBlockZ
        || ViewDistanceSq != previous.ViewDistanceSq
        || Lod0BiasSq != previous.Lod0BiasSq
        || Lod2BiasSq != previous.Lod2BiasSq;

    /// <summary>
    /// The furthest any terrain pool level is drawn, in blocks.
    ///
    /// <para>
    /// Not simply the view distance: level 0's clause is `distanceSq &lt; lod0BiasSq + 1024`, and
    /// with `LodBias` at 1 that reaches FURTHER than level 1's `viewDistanceSq = vd^2 + 400` -
    /// 71.6 blocks against 67.1 at a 64-block view distance. Taking the view distance alone as
    /// "the widest bound" would therefore be wrong in exactly the direction that costs a hole.
    /// </para>
    /// </summary>
    public double WidestDrawBoundBlocks()
    {
        if (!Available) return 0;
        double widest = Math.Sqrt(ViewDistanceSq);
        if (Lod0BiasSq > 0f) widest = Math.Max(widest, Math.Sqrt(Lod0BiasSq + 1024f));
        return widest;
    }

    /// <summary>
    /// A sound, chunk-free early denial: true when even the NEAREST point of this column is past
    /// every bound, so no placement of the sphere centre inside it can be drawn.
    ///
    /// <para>
    /// The centre lies inside the column, so its distance is at least the column's nearest-face
    /// distance. If the nearest face is already outside, the centre certainly is, and every LOD
    /// clause refuses. That makes this a strict subset of what <see cref="DrawsWithMargin"/>
    /// refuses - which is the property that matters, because it is applied to cells that have
    /// not yet reached an ownership decision and therefore cannot consult a real sphere.
    /// </para>
    /// <para>
    /// Nothing is subtracted for sphere placement. Slack is only needed when denying on a
    /// distance that might UNDERSTATE how far the centre is; this one overstates by
    /// construction, so subtracting would be denying ground the engine still draws. The
    /// difference between that and the old conservative bound is the whole bug: `viewDistance -
    /// 46` at a 64-block view distance is 18 blocks, against 67 here.
    /// </para>
    /// </summary>
    public bool ColumnCertainlyNotDrawn(int chunkX, int chunkZ, float marginBlocks = 0f)
    {
        if (!Available) return false;
        double nearest = VanillaRenderReadiness.ColumnDistanceBlocksFrom(
            chunkX, chunkZ, PlayerBlockX, PlayerBlockZ);
        return nearest > WidestDrawBoundBlocks() - Math.Max(0f, marginBlocks);
    }

    // There is deliberately no column-level pre-filter for the loss sweep here.
    //
    // The obvious one - skip a column whose worst-case sphere placement is still inside the
    // level-1 bound - was written, and it is UNSOUND. It reasons about `ViewDistanceSq`, which
    // is level 1's bound and the widest of the four; a chunk holding only level-2 geometry is
    // cut off at `lod2BiasSq` instead, which at the default far bias is a good deal nearer. Such
    // a column passes the filter while holding a cell the engine has already stopped drawing,
    // and skipping it keeps ownership over ground nothing draws.
    //
    // Making it sound needs the cell's own pool mask, which is only readable inside the loop the
    // filter exists to avoid entering - so the filter would have to do the work it was saving.
    // The work in question is a state-byte compare per cell over the committed set, once per
    // block the player walks. That is not worth an unsound skip, and `SweepRangeLosses` reports
    // its measured peak so the trade stays visible rather than assumed.
}
