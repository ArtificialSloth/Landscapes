using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace VintageHorizons.Checks;

/// <summary>
/// Holds the mod's copy of the engine's terrain range cull against the engine itself.
///
/// <para>
/// The rule this suite guards decides whether cached terrain draws at the seam, and it decides
/// it from a transcription. A transcription that drifts does not throw, log, or look wrong from
/// anywhere except standing at the boundary - it just owns slightly the wrong ground. So there
/// are three independent holds here, and they fail for different reasons:
/// </para>
/// <para>
/// 1. <see cref="AgreesWithTheEnginesOwnSwitch"/> runs the REAL `FrustumCulling` from the
///    installed game against the mod's copy over a dense grid. Not a re-implementation - the
///    engine's own object, with its own fields set - so G97 does not apply: there is no shared
///    assumption to agree wrongly about.
/// 2. <see cref="NeverGrantsWhereTheBoundDenies"/> asserts the property that makes shipping
///    this safe at all: everywhere 0.3.152's conservative bound granted ownership, the exact
///    rule grants it too, so enabling it cannot uncover ground that used to be covered.
/// 3. <see cref="BoundaryArithmetic"/> pins both sides of every comparison the switch makes,
///    including the ones that are `&lt;` against the ones that are `&lt;=`.
/// </para>
/// </summary>
public static class VanillaDrawRangeChecks
{
    const int ChunkBlocks = 32;

    public static void Run(Check c)
    {
        DistanceMatchesBlockPos(c);
        BoundaryArithmetic(c);
        LodMaskCombination(c);
        AgreesWithTheEnginesOwnSwitch(c);
        TheMarginOnlyEverGivesGroundBack(c);
        NeverGrantsWhereTheBoundDenies(c);
        QuantisedInputsGateTheSweep(c);
        TheEarlyGateCanOnlyRefuseWhatTheExactRuleRefuses(c);
        ANearBoundCanBeTighterThanTheViewDistance(c);
        UnavailableStateOwnsNothing(c);
        PoolLocationBinding(c);
    }

    /// <summary>
    /// The engine measures with `BlockPos.HorDistanceSqTo`, which computes in double and
    /// returns a FLOAT. That rounding is part of the comparison, so the mod has to do it too:
    /// a cell whose true distance sits inside the bound by less than one float ulp is drawn or
    /// not drawn according to which side the rounding lands on, and disagreeing there is a
    /// fragment nobody draws.
    /// </summary>
    static void DistanceMatchesBlockPos(Check c)
    {
        var rnd = new Random(20260829);
        int mismatches = 0;
        for (int i = 0; i < 4000; i++)
        {
            int px = rnd.Next(0, 1_000_000);
            int pz = rnd.Next(0, 1_000_000);
            float x = px + (float)(rnd.NextDouble() * 2000 - 1000);
            float z = pz + (float)(rnd.NextDouble() * 2000 - 1000);

            float engine = new BlockPos(px, 64, pz, 0).HorDistanceSqTo(x, z);
            float ours = VanillaDrawRangeState.HorizontalDistanceSq(px, pz, x, z);
            if (!engine.Equals(ours)) mismatches++;
        }
        c.Eq(0, mismatches, "the distance is bit-identical to the engine's own, float rounding included");
    }

    static void BoundaryArithmetic(Check c)
    {
        // 256-block view distance: ViewDistanceSq is 65,936, so the level-1 bound is a hair
        // over 256.8 blocks - the +400 is worth nearly a block and is not decoration.
        var range = new VanillaDrawRangeState(0, 0, 256 * 256 + 400, lod0BiasSq: 64f * 64f, lod2BiasSq: 200.0 * 200.0);

        c.True(range.DrawsAtLod(65_935.0, 1), "level 1 draws just inside its bound");
        c.False(range.DrawsAtLod(65_936.0, 1), "level 1 is strictly less than, so the bound itself is out");
        c.False(range.DrawsAtLod(65_937.0, 1), "and past it is out");

        // Level 2 is the only `<=` in the switch. Copying it as `<` would refuse ownership on
        // exactly one distance, which is unobservable and wrong.
        c.True(range.DrawsAtLod(40_000.0, 2), "level 2 draws inside its bias");
        c.True(range.DrawsAtLod(40_000.0 + 0.0, 2), "level 2 includes its own bound: the engine uses <=");
        c.False(range.DrawsAtLod(40_000.5, 2), "and excludes anything past it");

        // Level 0's bound carries a bare +1024 the engine adds in float before widening.
        c.True(range.DrawsAtLod(64f * 64f + 1023.0, 0), "level 0 draws inside its bias plus 1024");
        c.False(range.DrawsAtLod(64f * 64f + 1024.0, 0), "and stops at it");

        var noLod0 = new VanillaDrawRangeState(0, 0, 256 * 256 + 400, lod0BiasSq: 0f, lod2BiasSq: 200.0 * 200.0);
        c.False(noLod0.DrawsAtLod(0.0, 0),
            "a zero level-0 bias draws nothing at all, even underfoot - the engine returns false outright");
        c.True(noLod0.DrawsAtLod(0.0, 1), "which does not affect the other levels");

        // Level 3 is far-only: a hole in the middle, not a disc.
        c.False(range.DrawsAtLod(39_999.0, 3), "level 3 does not draw inside the level-2 bias");
        c.True(range.DrawsAtLod(40_000.5, 3), "level 3 draws between the bias and the view distance");
        c.False(range.DrawsAtLod(65_936.0, 3), "and stops at the view distance like level 1");

        // Anything the engine does not recognise is not drawn. A future pool level must cost
        // an overlap, never ownership granted on a guess.
        c.False(range.DrawsAtLod(0.0, 4), "an unknown pool level draws nothing");
        c.False(range.DrawsAtLod(0.0, -1), "including a negative one");
    }

    static void LodMaskCombination(Check c)
    {
        var range = new VanillaDrawRangeState(0, 0, 256 * 256 + 400, 64f * 64f, 200.0 * 200.0);

        // 240 blocks out: past the level-2 bias, inside the view distance. Ordinary terrain
        // (level 1) still draws; a chunk holding only level-2 detail does not.
        const float far = 240f;
        c.True(range.Draws(far, 0f, 1 << 1), "ordinary terrain draws to the full view distance");
        c.False(range.Draws(far, 0f, 1 << 2), "a chunk holding only near-detail geometry does not");
        c.True(range.Draws(far, 0f, (1 << 2) | (1 << 3)), "its far-only companion covers that ground instead");
        c.True(range.Draws(far, 0f, (1 << 1) | (1 << 2)), "any admitted level is enough for the chunk to submit");

        c.False(range.Draws(far, 0f, 0), "a chunk with no drawable pool draws nothing");
        c.False(range.Draws(1000f, 0f, VanillaDrawRangeState.AllLodLevels),
            "past the view distance no level admits it");
    }

    /// <summary>
    /// The mod's copy against the game's own `FrustumCulling`, over every level and a dense
    /// sweep of distances, at several view distances and bias settings.
    /// </summary>
    static void AgreesWithTheEnginesOwnSwitch(Check c)
    {
        MethodInfo? inFrustumAndRange = typeof(FrustumCulling).GetMethod("InFrustumAndRange",
            BindingFlags.Instance | BindingFlags.Public);
        c.True(inFrustumAndRange != null, "the installed game still has FrustumCulling.InFrustumAndRange");

        FieldInfo? playerPosField = typeof(FrustumCulling).GetField("playerPos",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        c.True(playerPosField != null, "and still measures range from a player BlockPos");
        if (inFrustumAndRange == null || playerPosField == null) return;

        // The frustum planes are private and start as six default `Plane` structs whose
        // `distanceOfPoint` answers zero, so `AABBisOutside` never rejects and every call
        // reaches the range switch. That is exactly the isolation this comparison wants:
        // ownership deliberately drops the plane test, so the only thing being compared is
        // the clause it keeps.
        int compared = 0;
        int mismatches = 0;
        string firstMismatch = string.Empty;

        foreach (int viewDistance in new[] { 32, 64, 128, 256, 512, 640 })
        foreach (float lodBias in new[] { 0f, 0.5f, 1f })
        foreach (float lodBiasFar in new[] { 0.25f, 0.6f, 1f })
        {
            var culler = new FrustumCulling();
            culler.UpdateViewDistance(viewDistance);
            float near = Math.Min(640, viewDistance) * lodBias;
            float far = Math.Min(640, viewDistance) * lodBiasFar;
            culler.lod0BiasSq = near * near;
            culler.lod2BiasSq = far * far;
            playerPosField.SetValue(culler, new BlockPos(0, 64, 0, 0));

            var ours = new VanillaDrawRangeState(0, 0, culler.ViewDistanceSq,
                culler.lod0BiasSq, culler.lod2BiasSq);

            for (int level = 0; level < VanillaDrawRangeState.LodLevelCount; level++)
            for (float distance = 0f; distance <= viewDistance + 40f; distance += 0.5f)
            {
                var sphere = new Sphere(distance, 64f, 0f, 1f, 1f, 1f);
                bool engine = (bool)inFrustumAndRange.Invoke(culler, new object[] { sphere, false, level })!;
                bool mine = ours.Draws(sphere.x, sphere.z, 1 << level);
                compared++;
                if (engine == mine) continue;
                mismatches++;
                if (firstMismatch.Length == 0)
                {
                    firstMismatch = $"vd {viewDistance} bias {lodBias}/{lodBiasFar} level {level} "
                        + $"at {distance}: engine {engine}, ours {mine}";
                }
            }
        }

        c.True(compared > 100_000, $"the comparison actually swept a dense grid ({compared} points)");
        c.Eq(0, mismatches, "the mod's range clause agrees with the engine's everywhere: " + firstMismatch);
    }

    /// <summary>
    /// The early handover covers vanilla dropping a chunk before a probe notices, and it can
    /// only ever hand ground BACK to cached terrain. A margin that granted ownership anywhere
    /// the exact rule refused it would be a hole generator rather than a hole guard, and the
    /// direction is easy to get backwards - the margin makes the chunk look FURTHER away, not
    /// nearer, and only the level-3 clause has a lower bound to be pushed past.
    /// </summary>
    static void TheMarginOnlyEverGivesGroundBack(Check c)
    {
        int examined = 0;
        int grantedByMargin = 0;
        int demotedByMargin = 0;
        string firstGrant = string.Empty;

        foreach (int viewDistance in new[] { 64, 128, 256, 512 })
        foreach (float margin in new[] { 0f, 4f, 16f, 48f, 200f })
        {
            var range = new VanillaDrawRangeState(0, 0, viewDistance * viewDistance + 400,
                lod0BiasSq: viewDistance * 0.5f * (viewDistance * 0.5f),
                lod2BiasSq: viewDistance * 0.6 * (viewDistance * 0.6));

            for (int mask = 1; mask < 16; mask++)
            for (float distance = 0f; distance <= viewDistance + 60f; distance += 0.5f)
            {
                bool exact = range.Draws(distance, 0f, mask);
                bool withMargin = range.DrawsWithMargin(distance, 0f, mask, margin);
                examined++;
                if (withMargin && !exact)
                {
                    grantedByMargin++;
                    if (firstGrant.Length == 0)
                        firstGrant = $"vd {viewDistance} margin {margin} pools {mask} at {distance}";
                }
                else if (exact && !withMargin) demotedByMargin++;
            }
        }

        c.True(examined > 100_000, $"the margin sweep covered a real population ({examined} points)");
        c.Eq(0, grantedByMargin,
            "the early handover never grants ownership the exact rule refused: " + firstGrant);
        c.True(demotedByMargin > 0, "and it does actually hand ground back somewhere");

        // Zero is exactly the transcription, so the default path and the knob's floor are the
        // same code rather than two roundings of it.
        var plain = new VanillaDrawRangeState(0, 0, 256 * 256 + 400, 1024f, 40_000.0);
        int disagreements = 0;
        for (float distance = 0f; distance <= 300f; distance += 0.25f)
        {
            for (int mask = 1; mask < 16; mask++)
            {
                if (plain.Draws(distance, 0f, mask) != plain.DrawsWithMargin(distance, 0f, mask, 0f))
                    disagreements++;
            }
        }
        c.Eq(0, disagreements, "a zero margin is bit-identical to the plain transcription");

        c.False(default(VanillaDrawRangeState).DrawsWithMargin(0f, 0f, 1 << 1, 16f),
            "an unavailable state grants nothing however small the margin");
    }

    /// <summary>
    /// The property that makes this safe to turn on: it can only ever GRANT ownership that
    /// 0.3.152 refused, never withdraw ownership 0.3.152 held - for chunks holding ordinary
    /// level-1 terrain, which is every chunk `CubeTesselator`, `TopsoilTesselator` and
    /// `LiquidTesselator` touch.
    ///
    /// <para>
    /// If that ever stops holding, the exact rule can uncover ground the bounded rule covered,
    /// and the failure is a hole rather than the overlap this whole change exists to remove.
    /// The reverse direction is deliberately NOT asserted: the exact rule refusing a chunk the
    /// bound granted is the level-2-only case, where the engine genuinely draws nothing and
    /// cached terrain filling it is a fix, not a regression.
    /// </para>
    /// </summary>
    static void NeverGrantsWhereTheBoundDenies(Check c)
    {
        int checkedCells = 0;
        int regressions = 0;
        string first = string.Empty;

        foreach (int viewDistance in new[] { 64, 128, 256, 512 })
        foreach (double camera in new[] { 0d, 16d, 31.9d, 1_000_016d })
        {
            int cameraChunk = (int)Math.Floor(camera / ChunkBlocks);
            var range = new VanillaDrawRangeState((int)camera, (int)camera,
                viewDistance * viewDistance + 400,
                lod0BiasSq: viewDistance * 0.5f * (viewDistance * 0.5f),
                lod2BiasSq: viewDistance * 0.6 * (viewDistance * 0.6));

            for (int offsetZ = -24; offsetZ <= 24; offsetZ++)
            for (int offsetX = -24; offsetX <= 24; offsetX++)
            {
                int chunkX = cameraChunk + offsetX;
                int chunkZ = cameraChunk + offsetZ;
                if (chunkX < 0 || chunkZ < 0) continue;

                bool boundDenies = VanillaRenderReadiness.BeyondVanillaDrawRange(
                    chunkX, chunkZ, camera, camera, viewDistance, extraMarginBlocks: 16f);
                if (boundDenies) continue;

                // The bound granted this column. Every sphere centre the engine could possibly
                // have placed inside it must therefore also be granted by the exact rule, or
                // enabling the exact rule opens ground that used to be covered.
                for (int localX = 0; localX <= ChunkBlocks; localX += 4)
                for (int localZ = 0; localZ <= ChunkBlocks; localZ += 4)
                {
                    float centreX = chunkX * ChunkBlocks + localX;
                    float centreZ = chunkZ * ChunkBlocks + localZ;
                    checkedCells++;
                    // The SAME margin ownership actually applies, not the bare transcription:
                    // testing the property against a function the renderer does not call would
                    // prove something true about code nothing runs.
                    if (range.DrawsWithMargin(centreX, centreZ,
                        VanillaDrawRangeState.EverywhereLod, 16f)) continue;
                    regressions++;
                    if (first.Length == 0)
                    {
                        first = $"vd {viewDistance} camera {camera} chunk {chunkX},{chunkZ} "
                            + $"centre {centreX},{centreZ}";
                    }
                }
            }
        }

        c.True(checkedCells > 50_000, $"the safety sweep covered a real population ({checkedCells} placements)");
        c.Eq(0, regressions,
            "the exact rule grants ownership everywhere the conservative bound already did: " + first);
    }

    /// <summary>
    /// The sweep runs only when the engine's own inputs move, and every one of those inputs is
    /// quantised by the engine itself. If a continuously-varying value ever leaked in here the
    /// sweep would fire every frame and the "affordable" half of this design would be gone.
    /// </summary>
    static void QuantisedInputsGateTheSweep(Check c)
    {
        var a = new VanillaDrawRangeState(100, 200, 65_936, 1024f, 40_000.0);
        c.False(a.DiffersFrom(a), "an unchanged state does not trigger a sweep");
        c.True(a.DiffersFrom(new VanillaDrawRangeState(101, 200, 65_936, 1024f, 40_000.0)),
            "crossing a block boundary in X does");
        c.True(a.DiffersFrom(new VanillaDrawRangeState(100, 201, 65_936, 1024f, 40_000.0)),
            "and in Z");
        c.True(a.DiffersFrom(new VanillaDrawRangeState(100, 200, 65_000, 1024f, 40_000.0)),
            "so does a view distance change");
        c.True(a.DiffersFrom(new VanillaDrawRangeState(100, 200, 65_936, 2048f, 40_000.0)),
            "so does the near LOD bias");
        c.True(a.DiffersFrom(new VanillaDrawRangeState(100, 200, 65_936, 1024f, 50_000.0)),
            "so does the far one");
        c.True(a.DiffersFrom(default), "and an unavailable state is always a change");
        c.True(default(VanillaDrawRangeState).DiffersFrom(a), "in both directions");
    }

    /// <summary>
    /// The property the whole first-sighting gate rests on, and the one whose absence caused
    /// the deadlock shipped in 0.3.154.
    ///
    /// <para>
    /// A cell that has not reached an ownership decision has no chunk fetched and therefore no
    /// sphere, so any denial applied to it must be derivable from the column alone. The old
    /// rule used `viewDistance - 46`, which at a 64-block view distance is 18 blocks - it
    /// refused chunks the engine was plainly drawing, and a refused cell never becomes an
    /// ownership decision, so it never got the accurate rule that would have admitted it.
    /// </para>
    /// <para>
    /// The replacement is sound by construction: the sphere centre lies inside the column, so
    /// its distance is at least the column's nearest-face distance. What is asserted here is
    /// that implication over a dense sweep - everything the early gate refuses, the exact rule
    /// refuses too. If that ever stops holding, the deadlock is back.
    /// </para>
    /// </summary>
    static void TheEarlyGateCanOnlyRefuseWhatTheExactRuleRefuses(Check c)
    {
        int examined = 0;
        int deadlocked = 0;
        int prunedAtAll = 0;
        string first = string.Empty;

        foreach (int viewDistance in new[] { 32, 64, 128, 256 })
        foreach (float lodBias in new[] { 0f, 0.33f, 1f })
        foreach (float margin in new[] { 0f, 16f })
        foreach (int playerBlock in new[] { 0, 17, 5_000_003 })
        {
            float near = Math.Min(640, viewDistance) * lodBias;
            var range = new VanillaDrawRangeState(playerBlock, playerBlock,
                viewDistance * viewDistance + 400, near * near,
                viewDistance * 0.66 * (viewDistance * 0.66));
            int playerChunk = playerBlock / ChunkBlocks;

            for (int offsetZ = -20; offsetZ <= 20; offsetZ++)
            for (int offsetX = -20; offsetX <= 20; offsetX++)
            {
                int chunkX = playerChunk + offsetX;
                int chunkZ = playerChunk + offsetZ;
                if (chunkX < 0 || chunkZ < 0) continue;
                if (!range.ColumnCertainlyNotDrawn(chunkX, chunkZ, margin)) continue;
                prunedAtAll++;

                // The gate refused this column. Every sphere placement the engine could have
                // chosen inside it must also be refused by the exact rule, at every level.
                for (int localX = 0; localX <= ChunkBlocks; localX += 2)
                for (int localZ = 0; localZ <= ChunkBlocks; localZ += 2)
                {
                    examined++;
                    if (!range.DrawsWithMargin(chunkX * ChunkBlocks + localX,
                        chunkZ * ChunkBlocks + localZ,
                        VanillaDrawRangeState.AllLodLevels, margin)) continue;
                    deadlocked++;
                    if (first.Length == 0)
                    {
                        first = $"vd {viewDistance} bias {lodBias} margin {margin} "
                            + $"chunk {chunkX},{chunkZ} local {localX},{localZ}";
                    }
                }
            }
        }

        c.True(prunedAtAll > 1000, $"the early gate actually prunes something ({prunedAtAll} columns)");
        c.True(examined > 100_000, $"the soundness sweep covered a real population ({examined})");
        c.Eq(0, deadlocked,
            "the early gate never refuses a column the exact rule would have admitted: " + first);

        // And the bound it measures against has to be the widest of the four levels, not the
        // view distance: with LodBias at 1, level 0 reaches further than level 1 does.
        var biased = new VanillaDrawRangeState(0, 0, 64 * 64 + 400,
            lod0BiasSq: 64f * 64f, lod2BiasSq: 0.45);
        c.True(biased.WidestDrawBoundBlocks() > Math.Sqrt(64 * 64 + 400),
            "the widest bound follows level 0 past the view distance when the near bias is 1");
        c.True(biased.Draws(70f, 0f, 1 << 0),
            "level 0 really does draw past the level-1 bound there");
        c.False(biased.ColumnCertainlyNotDrawn(2, 0),
            "so a column at that distance is not pruned");

        c.False(default(VanillaDrawRangeState).ColumnCertainlyNotDrawn(1000, 1000),
            "an unavailable state prunes nothing, so a failed culler read cannot refuse the world");
    }

    /// <summary>
    /// Why the loss sweep examines every committed cell instead of filtering down to a
    /// plausible ring first: the four LOD bounds are not the same distance, and the widest one
    /// is the only one a column-level filter can see.
    ///
    /// <para>
    /// A filter reasoning about `ViewDistanceSq` passes a column holding a chunk whose only
    /// geometry is near-detail, which the engine cut off at `lod2BiasSq` a long way nearer -
    /// and passing it means keeping ownership over ground nothing draws. Such a filter was
    /// written and deleted; this records that the gap it ignored is real at ordinary settings
    /// rather than a theoretical one, so it stays deleted.
    /// </para>
    /// </summary>
    static void ANearBoundCanBeTighterThanTheViewDistance(Check c)
    {
        // The engine's own arithmetic: lod2BiasSq is (min(640, viewDistance) * LodBiasFar)^2.
        const int viewDistance = 256;
        const float lodBiasFar = 0.6f;
        float far = viewDistance * lodBiasFar;
        var range = new VanillaDrawRangeState(0, 0, viewDistance * viewDistance + 400,
            lod0BiasSq: 0f, lod2BiasSq: far * far);

        // 200 blocks out: inside the view distance, well past the far bias.
        c.True(range.Draws(200f, 0f, VanillaDrawRangeState.EverywhereLod),
            "ordinary terrain is still drawn at 200 blocks");
        c.False(range.Draws(200f, 0f, 1 << 2),
            "a chunk holding only near-detail geometry is not, at the same distance");

        // Which is the whole point: a column filter sized on the view distance would have kept
        // that second cell owned. Quantified so the size of the gap is on record.
        double nearBound = far;
        double wideBound = Math.Sqrt(viewDistance * viewDistance + 400);
        c.True(wideBound - nearBound > 100,
            $"the two bounds differ by more than 100 blocks at the defaults "
            + $"({nearBound:0} against {wideBound:0})");
    }

    static void UnavailableStateOwnsNothing(Check c)
    {
        var unavailable = default(VanillaDrawRangeState);
        c.False(unavailable.Available, "the default state reports itself unusable");
        c.False(unavailable.Draws(0f, 0f, VanillaDrawRangeState.AllLodLevels),
            "an unusable state grants nothing, so a failed read cannot suppress cached terrain");
        c.False(unavailable.DrawsWithMargin(0f, 0f, VanillaDrawRangeState.AllLodLevels, 16f),
            "and grants nothing with a margin either");

        // The sweep is the other consumer, and it must collect nothing rather than everything:
        // an unreadable culler withdrawing ownership from every committed cell at once would
        // be 0.3.3 with a different cause.
        var model = new VanillaRenderReadiness(4, 2);
        model.SetWindow(0, 0, 4, 4);
        var cell = new VanillaChunkCell(1, 1, 1);
        model.EnqueueCandidate(cell);
        model.TryDequeueCandidate(out _);
        model.Observe(cell, rendered: true, 1, out _);
        model.Observe(cell, rendered: true, 2, out VanillaReadinessPublication publication);
        model.ResolvePublication(publication, accepted: true);
        model.SetDrawSphere(cell, 48f, 48f, VanillaDrawRangeState.EverywhereLod);

        var losses = new List<VanillaChunkCell>();
        c.Eq(0, model.CollectRangeLosses(unavailable, losses),
            "an unusable state drives no sweep, so it cannot withdraw ownership either");
        c.Eq(VanillaReadinessState.VanillaReady, model.State(cell),
            "and the committed cell keeps its ownership");
    }

    /// <summary>
    /// The two public fields the exact rule reads off a pool location, asserted against the
    /// installed assembly. They need no reflection at runtime, which is exactly why nothing
    /// would report their disappearance: the mod would fail to compile against a new game, or
    /// worse, compile against a renamed field with a different meaning.
    /// </summary>
    static void PoolLocationBinding(Check c)
    {
        FieldInfo? sphere = typeof(ModelDataPoolLocation).GetField("FrustumCullSphere",
            BindingFlags.Instance | BindingFlags.Public);
        c.True(sphere != null && sphere.FieldType == typeof(Sphere),
            "a pool location still carries the bounding sphere the engine range-culls it by");

        FieldInfo? lodLevel = typeof(ModelDataPoolLocation).GetField("LodLevel",
            BindingFlags.Instance | BindingFlags.Public);
        c.True(lodLevel != null && lodLevel.FieldType == typeof(int),
            "and still carries which terrain LOD pool it belongs to");

        FieldInfo? hide = typeof(ModelDataPoolLocation).GetField("Hide",
            BindingFlags.Instance | BindingFlags.Public);
        c.True(hide != null && hide.FieldType == typeof(bool),
            "and the flag that keeps a held mesh out of the draw");

        FieldInfo? viewDistanceSq = typeof(FrustumCulling).GetField("ViewDistanceSq",
            BindingFlags.Instance | BindingFlags.Public);
        c.True(viewDistanceSq != null && viewDistanceSq.FieldType == typeof(int),
            "the culler still exposes the squared view distance it compares against");
        FieldInfo? lod0 = typeof(FrustumCulling).GetField("lod0BiasSq",
            BindingFlags.Instance | BindingFlags.Public);
        c.True(lod0 != null && lod0.FieldType == typeof(float), "and the near LOD bias");
        FieldInfo? lod2 = typeof(FrustumCulling).GetField("lod2BiasSq",
            BindingFlags.Instance | BindingFlags.Public);
        c.True(lod2 != null && lod2.FieldType == typeof(double), "and the far one");

        // `UpdateViewDistance` is where the +400 lives. If the engine ever changes that
        // constant the mod inherits it for free by reading ViewDistanceSq - but only as long
        // as it keeps reading it rather than recomputing the square itself.
        var culler = new FrustumCulling();
        culler.UpdateViewDistance(256);
        c.Eq(256 * 256 + 400, culler.ViewDistanceSq,
            "the engine still pads the squared view distance by 400");
    }
}
