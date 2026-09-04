namespace VintageHorizons;

/// <summary>
/// Turns a section snapshot into raw vertex data (worker thread). Every run is a box;
/// faces are collected first, then greedy-merged before emission:
///
/// - Horizontal faces (tops/bottoms) group into Y-planes per (y, palette, side) and
///   merge into maximal rectangles with a standard 2D greedy sweep - ocean surfaces
///   and plains collapse from thousands of quads to a handful.
/// - Vertical faces (walls) merge along their strip axis where adjacent columns
///   expose identical (span, palette) segments - cliffs and frontier walls collapse
///   into long ribbons.
///
/// Coverage rules (unchanged from the non-greedy mesher): solid faces are only
/// culled by solid neighbors so terrain shows through translucent water; water
/// faces are culled by any coverage. A missing neighbor section is the frontier
/// of explored space and renders as a wall - unless the job says that side only
/// LOOKS missing because the section has not been loaded yet, in which case water
/// leaves the edge open; see CollectSide.
///
/// HEIGHTS INSIDE THIS FILE ARE IN HALF BLOCKS. Stored runs are whole blocks, but a
/// block flagged <see cref="LodPaletteEntry.FlagHalf"/> - a terrain slab - fills only the
/// lower half of its cell, and the surface, the walls and every coverage test have to
/// agree about that or a slab opens a half-block hole into the solid ground beside it.
/// Doubling once, at the two accessors below, is what keeps the rest of the file integer
/// interval arithmetic; only the emission points divide by two. The packed quad format
/// stores Y in quarter blocks, so a half-block plane is exactly representable there.
/// </summary>
public static class LodMesher
{
    const int W = 0, E = 1, N = 2, S = 3;

    // The tint slot rides in the alpha byte, so the vertex format stays position+colour:
    //   0..63    opaque,     tint slot = alpha
    //   64..127  water,      tint slot = alpha - 64
    //   128..191 thin plant, tint slot = alpha - 128
    // Slot 0 is the identity tint. The band picks the blend factor in the shader, so
    // water and flowers can be see-through by different amounts.
    const byte WaterBase = LodTintRegistry.MaxSlots;
    const byte ThinBase = LodTintRegistry.MaxSlots * 2;

    static byte AlphaFor(byte paletteFlags, byte tintSlot)
    {
        byte slot = tintSlot < LodTintRegistry.MaxSlots ? tintSlot : (byte)LodTintRegistry.SlotNone;
        if ((paletteFlags & LodPaletteEntry.FlagWater) != 0) return (byte)(WaterBase + slot);
        if ((paletteFlags & LodPaletteEntry.FlagThin) != 0) return (byte)(ThinBase + slot);
        return slot;
    }

    /// <summary>
    /// How much darker a surface is for the water standing above it, in WHOLE blocks, capped
    /// where the light ramp has bottomed out and further depth cannot be told apart.
    ///
    /// Water carries <c>lightAbsorption: 1</c>, and the engine's illuminator settles a directly
    /// lit column at <c>defaultSunLight - (summed absorption above)</c> with no per-block travel
    /// penalty - so a bed under d blocks of water is exactly d light levels darker than the
    /// shore beside it. Cached terrain has no per-position light of any kind, so it draws a
    /// lakebed as brightly as an open field; owner screenshots measure vanilla water covering a
    /// 2.2x brightness range for this reason against one flat value for ours.
    ///
    /// Baking the ratio into the stored colour is exact rather than a shortcut. `SunBrightness`
    /// is the world's MAXIMUM sun level, not the time of day - the clock arrives separately as
    /// the ambient colour, which multiplies afterwards - and with no block light vanilla's own
    /// light maths collapses to `lightColor.a * ambientColor`, linear in the level. So a
    /// constant factor here is arithmetically identical to drawing the surface at a lower light
    /// level, at every hour, with nothing to recompute when the sun moves.
    /// </summary>
    public const int MaxSubmergedDepth = 24;

    static int SubmergedColor(int color, int depth, float[]? absorption)
    {
        if (depth <= 0 || absorption == null || absorption.Length == 0) return color;
        float factor = absorption[Math.Min(depth, absorption.Length - 1)];
        if (factor >= 0.999f) return color;
        int r = (int)((color & 0xFF) * factor + 0.5f);
        int g = (int)(((color >> 8) & 0xFF) * factor + 0.5f);
        int b = (int)(((color >> 16) & 0xFF) * factor + 0.5f);
        return Math.Clamp(r, 0, 255)
            | (Math.Clamp(g, 0, 255) << 8)
            | (Math.Clamp(b, 0, 255) << 16);
    }

    /// <summary>Half-block extent to whole blocks of water, clamped to the useful range.</summary>
    static short SubmergedBlocks(int halfBlocks) =>
        (short)Math.Clamp(halfBlocks / 2, 0, MaxSubmergedDepth);

    /// <summary>
    /// A wall's depth, taken at its TOP - the shallowest, brightest end of it. One value per
    /// merged segment is a simplification of a gradient, and erring toward the light end keeps
    /// a submerged cliff from reading as a black band against the bed beside it.
    ///
    /// Water's own walls take none. A lake's edge outlined in shadow is not something vanilla
    /// draws, and the surface above it already carries the depth.
    /// </summary>
    static short WallSubmerged(bool isTranslucent, int waterSurface, int wallTop) =>
        isTranslucent || waterSurface == short.MinValue
            ? (short)0
            : SubmergedBlocks(waterSurface - wallTop);

    class Buffers
    {
        public readonly List<float> Xyz = new(16384);
        public readonly List<byte> Rgba = new(8192);
        public readonly List<int> Indices = new(12288);
        public readonly List<uint> Packed = new(3072);
        public readonly LodPackedClusterBuilder? ClusteredPacked;
        public readonly bool Pack;
        public bool HasTextureSlots;

        public Buffers(bool pack)
        {
            Pack = pack;
            if (pack) ClusteredPacked = new LodPackedClusterBuilder();
        }

        // Running vertical extent of everything emitted into this pass. Free: every Y
        // here was computed anyway, and AddVert is the single funnel every vertex passes
        // through, so no emission path can forget to contribute.
        public float MinY = float.PositiveInfinity;
        public float MaxY = float.NegativeInfinity;

        public LodHeightSpan Span => LodHeightSpan.Of(MinY, MaxY);

        public void Clear()
        {
            Xyz.Clear();
            Rgba.Clear();
            Indices.Clear();
            Packed.Clear();
            ClusteredPacked?.Clear();
            HasTextureSlots = false;
            MinY = float.PositiveInfinity;
            MaxY = float.NegativeInfinity;
        }
    }

    /// <summary>
    /// One exposed horizontal face: column cell + plane y + palette. Y and YBottom are in
    /// HALF blocks. A short holds every height a 14-bit run can carry for any world height
    /// up to 8,191 blocks, which is far past what the game builds.
    /// </summary>
    readonly record struct HFace(short Cx, short Cz, short Y, short Pid, bool Bottom, bool Water, bool Thin, short YBottom, short Submerged);

    /// <summary>One exposed wall segment on a column edge. YTop and YBottom are half blocks.</summary>
    readonly record struct VFace(byte Dir, short Fixed, short Along, short YTop, short YBottom, short Pid, bool Water, short Submerged);

    [ThreadStatic] static List<HFace>? hFaces;
    [ThreadStatic] static List<VFace>? vFaces;

    // Pooled per thread for the same reason the face lists are, and missed when they were
    // done: a fresh pair cost 240 KB per mesh job even for a section that emits five
    // quads, and their lists grew past the 85 KB large-object threshold and back on every
    // job for a dense one. Reused, they grow once per thread and stay grown.
    [ThreadStatic] static Buffers? opaqueBuffers;
    [ThreadStatic] static Buffers? waterBuffers;

    /// <summary>
    /// Vertices this thread would have emitted with and without cave culling, when the audit
    /// is switched on. The offline harness says the rule removes a third of the geometry and
    /// the game says it ADDS a fiftieth, so one of them is not measuring the real thing -
    /// and the only way to settle that is to build both meshes from inside the game, on the
    /// real sections, with the real neighbours.
    /// </summary>
    public static long AuditVerticesWithCulling;
    public static long AuditVerticesWithout;
    public static long AuditSections;

    // Worker cost of the cave classification itself, separated from ordinary face
    // collection/merging. The refined passage graph does more reasoning to emit less
    // geometry; this makes that CPU side of the trade visible in the same command that
    // reports the geometry saving.
    static long caveCullCalls;
    static long caveCullElapsedTicks;
    static long caveCullMaxTicks;

    public static string DescribeCaveCullTiming()
    {
        long calls = Interlocked.Read(ref caveCullCalls);
        long total = Interlocked.Read(ref caveCullElapsedTicks);
        long maximum = Interlocked.Read(ref caveCullMaxTicks);
        double toMilliseconds = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return calls == 0
            ? "cull worker timing: no completed sections"
            : $"cull worker timing: {calls:n0} sections, "
                + $"{total * toMilliseconds / calls:0.000} ms mean, "
                + $"{maximum * toMilliseconds:0.000} ms max";
    }

    public static void ResetCaveCullTiming()
    {
        Interlocked.Exchange(ref caveCullCalls, 0);
        Interlocked.Exchange(ref caveCullElapsedTicks, 0);
        Interlocked.Exchange(ref caveCullMaxTicks, 0);
    }

    static readonly bool AuditEnabled =
        Environment.GetEnvironmentVariable("VINTAGEHORIZONS_CAVE_AUDIT") == "1";

    public static MeshResult BuildMesh(MeshJob job)
    {
        // Before any buffer is touched, so the probe can borrow the same pooled buffers and
        // be completely finished with them before the real build starts clearing them.
        if (AuditEnabled && job.CaveCullReach > 0)
        {
            MeshResult plain = BuildMesh(new MeshJob
            {
                WorldEpoch = job.WorldEpoch,
                Key = job.Key,
                Self = job.Self,
                Neighbors = job.Neighbors,
                AssumedCoveredSides = job.AssumedCoveredSides,
                CaveCullReach = 0,
                HalfBlockLevels = job.HalfBlockLevels,
                SurfaceHalfBlocksOnly = job.SurfaceHalfBlocksOnly,
                CutoutBlocksCover = job.CutoutBlocksCover,
                EmitTextureSlots = job.EmitTextureSlots,
                TextureModeRevision = job.TextureModeRevision,
            });
            Interlocked.Add(ref AuditVerticesWithout, plain.VertexCount);
            Interlocked.Increment(ref AuditSections);
        }

        int level = LodWorld.KeyLevel(job.Key);
        int step = LodSection.ColumnStepBlocks << level;
        int gs = LodSection.GridSize;
        // Before anything is measured or emitted: cavities daylight cannot reach become
        // rock, so their floors, ceilings and walls are never built at all. Returns the same
        // instance when there is nothing to fill, which is the common case for a column of
        // open hillside.
        long caveStarted = job.CaveCullReach > 0
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0;
        LodCaveCull.Prepared cave = LodCaveCull.Prepare(
            job.Self, job.Neighbors, level, job.CaveCullReach);
        if (caveStarted != 0)
        {
            long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - caveStarted;
            Interlocked.Increment(ref caveCullCalls);
            Interlocked.Add(ref caveCullElapsedTicks, elapsed);
            long previous = Interlocked.Read(ref caveCullMaxTicks);
            while (elapsed > previous)
            {
                long observed = Interlocked.CompareExchange(
                    ref caveCullMaxTicks, elapsed, previous);
                if (observed == previous) break;
                previous = observed;
            }
        }
        SectionSnapshot self = cave.Self;

        // Whether slabs are drawn at their real half height here. Off at coarse levels by
        // default: a column already merges a whole neighbourhood, so half a block of
        // vertical detail buys nothing and the extra walls it needs are not free.
        bool half = level < job.HalfBlockLevels;
        bool surfaceHalfOnly = job.SurfaceHalfBlocksOnly;
        // Whether a block the game draws see-through may still act as cover here. Off is the
        // fix; on is the established behaviour, kept switchable because the cost is geometry.
        bool cutoutsCover = job.CutoutBlocksCover;

        var opaque = opaqueBuffers ??= new Buffers(pack: true);
        var water = waterBuffers ??= new Buffers(pack: false);
        var hf = hFaces ??= new List<HFace>(8192);
        var vf = vFaces ??= new List<VFace>(8192);
        opaque.Clear();
        water.Clear();
        hf.Clear();
        vf.Clear();

        // ---- Phase 1: collect exposed faces ----

        for (int cz = 0; cz < gs; cz++)
        {
            for (int cx = 0; cx < gs; cx++)
            {
                int col = LodSection.ColumnIndex(cx, cz);
                if (!self.Captured[col]) continue;

                Span<ulong> runs = self.ColumnRuns(col);

                // The water surface standing over this column, in half blocks, or a sentinel
                // where there is none. Runs are stored top-down, so the first translucent one
                // is the surface and everything below it is under that much water.
                int waterSurface = short.MinValue;
                if (job.WaterAbsorption != null)
                {
                    for (int r = 0; r < runs.Length; r++)
                    {
                        if (!IsTranslucentRun(self, runs[r])) continue;
                        waterSurface = TopHalf(self, runs, r, half, surfaceHalfOnly);
                        break;
                    }
                }

                for (int r = 0; r < runs.Length; r++)
                {
                    ulong run = runs[r];
                    // Half blocks from here down. A slab's run still ends on a whole-block
                    // boundary in storage; its SURFACE is half a block lower.
                    int yTop = TopHalf(self, runs, r, half, surfaceHalfOnly);
                    int yBottom = BottomHalf(run);
                    int pid = LodSection.RunPaletteId(run);
                    bool isTranslucent = IsTranslucent(self.PaletteFlags[pid]);

                    bool isThin = (self.PaletteFlags[pid] & LodPaletteEntry.FlagThin) != 0;

                    // Ground cover is a few centimetres of plant in a one-block cell, so
                    // it is drawn as a low mat: top face only, dropped to just above the
                    // soil. Rendering it as a full cube turned meadows into fields of
                    // solid blocks, which was the real problem -- most flowers store
                    // perfectly good colour.
                    if (isThin)
                    {
                        hf.Add(new HFace((short)cx, (short)cz, (short)yTop, (short)pid, false, true, true, (short)yBottom, 0));
                        continue;
                    }

                    // A slab's own top is half a block below the run above it, so this
                    // comparison also answers "is there a half-block gap here" without a
                    // second test: 2y-1 never equals 2y.
                    bool topCovered = r > 0
                        && BottomHalf(runs[r - 1]) == yTop
                        && !IsThinRun(self, runs[r - 1])
                        && !IsCutoutRun(self, runs[r - 1], cutoutsCover)
                        && IsTranslucentRun(self, runs[r - 1]) == isTranslucent;
                    // YBottom is carried for thin cover only, which is the one face that
                    // reads it (it sits a quarter block above its own base). A surface
                    // face is drawn at its own y whatever depth the run beneath it
                    // reaches, so recording that depth here only splits the plane: a flat
                    // sea would break up along the contour of the seabed under it.
                    //
                    // Submerged depth is the exception to that, and it is deliberate. A flat
                    // sea DOES break up along the contour of the seabed under it now, because
                    // that contour is what the eye reads as depth - and it was measured before
                    // it was written: over 481,287 columns of the owner's cache the exact
                    // per-block gradient costs +0.24% vertices, because a bed that changes
                    // depth is a bed that changes height and those quads were not merging
                    // anyway. A water SURFACE takes its own thickness instead, standing in for
                    // the darkened bed it is too opaque at this range to show.
                    //
                    // The WATER SURFACE is deliberately not one of them, and it was measured
                    // before that was decided: shading the surface by its own thickness costs
                    // +75% of the water buffer, because it breaks a lake into one quad per
                    // depth step exactly as the paragraph above warns. The bed underneath now
                    // carries the depth for free, so the cheaper and truer way to show it is to
                    // let the water be seen through rather than to paint the gradient twice.
                    short submerged =
                        job.WaterAbsorption == null || isTranslucent || waterSurface == short.MinValue
                            ? (short)0
                            : SubmergedBlocks(waterSurface - yTop);
                    if (!topCovered) hf.Add(new HFace((short)cx, (short)cz, (short)yTop, (short)pid, false, isTranslucent, false, 0, submerged));

                    bool bottomCovered = r < runs.Length - 1
                        && TopHalf(self, runs, r + 1, half, surfaceHalfOnly) == yBottom
                        && !IsThinRun(self, runs[r + 1])
                        && !IsCutoutRun(self, runs[r + 1], cutoutsCover)
                        && IsTranslucentRun(self, runs[r + 1]) == isTranslucent;
                    if (!bottomCovered && yBottom > 2 && !isTranslucent)
                    {
                        // A floor's own plane IS the run's bottom, so Y already carries it.
                        hf.Add(new HFace((short)cx, (short)cz, (short)yBottom, (short)pid, true, false, false, 0, 0));
                    }

                    bool solidCoverOnly = !isTranslucent;
                    CollectSide(vf, job, self, cave, W, cx, cz, cx - 1, cz,
                        yTop, yBottom, pid, isTranslucent, solidCoverOnly, half, surfaceHalfOnly,
                        cutoutsCover, waterSurface);
                    CollectSide(vf, job, self, cave, E, cx, cz, cx + 1, cz,
                        yTop, yBottom, pid, isTranslucent, solidCoverOnly, half, surfaceHalfOnly,
                        cutoutsCover, waterSurface);
                    CollectSide(vf, job, self, cave, N, cx, cz, cx, cz - 1,
                        yTop, yBottom, pid, isTranslucent, solidCoverOnly, half, surfaceHalfOnly,
                        cutoutsCover, waterSurface);
                    CollectSide(vf, job, self, cave, S, cx, cz, cx, cz + 1,
                        yTop, yBottom, pid, isTranslucent, solidCoverOnly, half, surfaceHalfOnly,
                        cutoutsCover, waterSurface);
                }
            }
        }

        // ---- Phase 2: greedy-merge and emit ----

        EmitHorizontalGreedy(hf, self, opaque, water, step, job.EmitTextureSlots,
            job.WaterAbsorption);
        EmitVerticalMerged(vf, self, opaque, water, step, job.EmitTextureSlots,
            job.WaterAbsorption);
        opaque.ClusteredPacked!.Finish(
            out uint[] clusteredPackedQuads,
            out LodPackedCluster[] packedClusters);

        if (AuditEnabled && job.CaveCullReach > 0)
        {
            Interlocked.Add(ref AuditVerticesWithCulling, opaque.Xyz.Count / 3);
        }

        return new MeshResult
        {
            WorldEpoch = job.WorldEpoch,
            Key = job.Key,
            Xyz = opaque.Xyz.ToArray(),
            Rgba = opaque.Rgba.ToArray(),
            Indices = opaque.Indices.ToArray(),
            VertexCount = opaque.Xyz.Count / 3,
            IndexCount = opaque.Indices.Count,
            PackedOpaqueQuads = opaque.Packed.ToArray(),
            PackedOpaqueQuadCount = opaque.Packed.Count / LodPackedQuadFormat.WordsPerQuad,
            HasTextureSlots = opaque.HasTextureSlots,
            TextureModeRevision = job.TextureModeRevision,
            ClusteredPackedOpaqueQuads = clusteredPackedQuads,
            PackedOpaqueClusters = packedClusters,
            WaterXyz = water.Xyz.Count > 0 ? water.Xyz.ToArray() : null,
            WaterRgba = water.Xyz.Count > 0 ? water.Rgba.ToArray() : null,
            WaterIndices = water.Xyz.Count > 0 ? water.Indices.ToArray() : null,
            WaterVertexCount = water.Xyz.Count / 3,
            WaterIndexCount = water.Indices.Count,
            Heights = new LodSectionHeights(opaque.Span, water.Span),
            ReadyAtMilliseconds = Environment.TickCount64,
            AssumedCoveredSides = job.AssumedCoveredSides,
        };
    }

    // ---- Horizontal faces: per-plane 2D greedy rectangles ----

    [ThreadStatic] static int[]? planeGrid;
    [ThreadStatic] static int planeGridStamp;

    static void EmitHorizontalGreedy(List<HFace> faces, SectionSnapshot self, Buffers opaque,
        Buffers water, int step, bool emitTextureSlots, float[]? absorption)
    {
        if (faces.Count == 0) return;
        int gs = LodSection.GridSize;

        // Group faces into planes by (y, pid, side, thin, run bottom); grid cells hold a
        // stamp so we never clear the array between planes.
        //
        // Every field the group scan below tests must appear here, ahead of Cz/Cx. The
        // scan walks a contiguous span of this order, so a field it tests but the sort
        // ignores ends the plane at the first cell that differs, wherever that lands.
        // Thin and YBottom were both missing, and a flat surface over an uneven base
        // split into one strip per row as a result.
        //
        // Sorting on them is only half of it. The other half is not recording a value a
        // face never reads: see the top and floor faces above, which now carry YBottom 0.
        // Otherwise the plane still breaks once per distinct depth underneath it.
        faces.Sort((a, b) =>
        {
            int c = a.Y.CompareTo(b.Y);
            if (c != 0) return c;
            c = a.Pid.CompareTo(b.Pid);
            if (c != 0) return c;
            c = a.Bottom.CompareTo(b.Bottom);
            if (c != 0) return c;
            c = a.Thin.CompareTo(b.Thin);
            if (c != 0) return c;
            c = a.YBottom.CompareTo(b.YBottom);
            if (c != 0) return c;
            c = a.Submerged.CompareTo(b.Submerged);
            if (c != 0) return c;
            c = a.Cz.CompareTo(b.Cz);
            return c != 0 ? c : a.Cx.CompareTo(b.Cx);
        });

        var grid = planeGrid ??= new int[gs * gs];

        int start = 0;
        while (start < faces.Count)
        {
            HFace first = faces[start];
            int end = start;
            while (end < faces.Count
                && faces[end].Y == first.Y
                && faces[end].Pid == first.Pid
                && faces[end].Bottom == first.Bottom
                && faces[end].Thin == first.Thin
                && faces[end].YBottom == first.YBottom
                && faces[end].Submerged == first.Submerged)
            {
                end++;
            }

            int stamp = ++planeGridStamp;
            for (int i = start; i < end; i++)
            {
                grid[faces[i].Cz * gs + faces[i].Cx] = stamp;
            }

            Buffers buf = first.Water ? water : opaque;
            int color = SubmergedColor(
                self.PaletteColors[first.Pid], first.Submerged, absorption);
            byte alpha = AlphaFor(self.PaletteFlags[first.Pid], self.PaletteTintSlots[first.Pid]);
            int textureSlot = TextureSlotFor(self, first.Pid, step, emitTextureSlots);

            for (int i = start; i < end; i++)
            {
                int cx = faces[i].Cx;
                int cz = faces[i].Cz;
                if (grid[cz * gs + cx] != stamp) continue; // already consumed

                // Extend width along +X, then height along +Z while the full row matches.
                int wRect = 1;
                while (cx + wRect < gs && grid[cz * gs + cx + wRect] == stamp) wRect++;

                int hRect = 1;
                while (cz + hRect < gs)
                {
                    bool rowOk = true;
                    for (int dx = 0; dx < wRect; dx++)
                    {
                        if (grid[(cz + hRect) * gs + cx + dx] != stamp) { rowOk = false; break; }
                    }
                    if (!rowOk) break;
                    hRect++;
                }

                for (int dz = 0; dz < hRect; dz++)
                {
                    for (int dx = 0; dx < wRect; dx++)
                    {
                        grid[(cz + dz) * gs + cx + dx] = 0;
                    }
                }

                float x0 = cx * step;
                float x1 = (cx + wRect) * step;
                float z0 = cz * step;
                float z1 = (cz + hRect) * step;
                // Ground cover sits a quarter block above the soil it stands on: clear
                // of z-fighting, invisible as a step. Measured UP from the run's bottom,
                // never down from its top -- mip merging fuses adjacent thin runs, so at
                // coarse levels one run spans several blocks and a fixed drop from the
                // top left the mat floating in mid-air. Clamped so it can never rise
                // above the run it represents.
                // Y is absolute (half blocks) and is NOT scaled by step, so neither is
                // the offset; the halving back to world blocks happens here.
                float top = first.Y * 0.5f;
                float y = first.Thin ? Math.Min(first.YBottom * 0.5f + 0.25f, top) : top;

                // Counter-clockwise from outside the run, matching OpenGL's front-face
                // default. The renderer may then reject opaque back faces without losing
                // a surface. This was not true for the old two-sided meshes: top/bottom
                // happened to oppose one another, but both X walls and both Z walls used
                // the same winding.
                if (first.Bottom)
                {
                    AddQuad(buf, LodPackedFace.Bottom, step, color, alpha,
                        x0, y, z0, x1, y, z0, x1, y, z1, x0, y, z1, textureSlot);
                }
                else
                {
                    AddQuad(buf, LodPackedFace.Top, step, color, alpha,
                        x0, y, z0, x0, y, z1, x1, y, z1, x1, y, z0, textureSlot);
                }
            }

            start = end;
        }
    }

    // ---- Vertical faces: merge along the strip axis ----

    static void EmitVerticalMerged(List<VFace> faces, SectionSnapshot self, Buffers opaque,
        Buffers water, int step, bool emitTextureSlots, float[]? absorption)
    {
        if (faces.Count == 0) return;

        faces.Sort((a, b) =>
        {
            int c = a.Dir.CompareTo(b.Dir);
            if (c != 0) return c;
            c = a.Fixed.CompareTo(b.Fixed);
            if (c != 0) return c;
            c = a.YTop.CompareTo(b.YTop);
            if (c != 0) return c;
            c = a.YBottom.CompareTo(b.YBottom);
            if (c != 0) return c;
            c = a.Pid.CompareTo(b.Pid);
            if (c != 0) return c;
            c = a.Submerged.CompareTo(b.Submerged);
            return c != 0 ? c : a.Along.CompareTo(b.Along);
        });

        int i = 0;
        while (i < faces.Count)
        {
            VFace seg = faces[i];
            int alongEnd = seg.Along + 1;
            int j = i + 1;
            while (j < faces.Count)
            {
                VFace next = faces[j];
                if (next.Dir != seg.Dir || next.Fixed != seg.Fixed || next.YTop != seg.YTop
                    || next.YBottom != seg.YBottom || next.Pid != seg.Pid
                    || next.Submerged != seg.Submerged || next.Along != alongEnd)
                {
                    break;
                }
                alongEnd++;
                j++;
            }

            Buffers buf = seg.Water ? water : opaque;
            int color = SubmergedColor(self.PaletteColors[seg.Pid], seg.Submerged, absorption);
            byte alpha = AlphaFor(self.PaletteFlags[seg.Pid], self.PaletteTintSlots[seg.Pid]);
            int textureSlot = TextureSlotFor(self, seg.Pid, step, emitTextureSlots);

            // W/E walls run along Z at fixed X; N/S walls run along X at fixed Z.
            bool xWall = seg.Dir is W or E;
            float fixedCoord = seg.Dir switch
            {
                W => seg.Fixed * step,
                E => (seg.Fixed + 1) * step,
                N => seg.Fixed * step,
                _ => (seg.Fixed + 1) * step,
            };
            float a0 = seg.Along * step;
            float a1 = alongEnd * step;
            // Half blocks back to world blocks. Y is absolute and is NOT scaled by step.
            float yTop = seg.YTop * 0.5f;
            float yBottom = seg.YBottom * 0.5f;

            if (seg.Dir == W)
            {
                AddQuad(buf, LodPackedFace.West, step, color, alpha,
                    fixedCoord, yBottom, a0, fixedCoord, yBottom, a1,
                    fixedCoord, yTop, a1, fixedCoord, yTop, a0, textureSlot);
            }
            else if (seg.Dir == E)
            {
                AddQuad(buf, LodPackedFace.East, step, color, alpha,
                    fixedCoord, yBottom, a0, fixedCoord, yTop, a0,
                    fixedCoord, yTop, a1, fixedCoord, yBottom, a1, textureSlot);
            }
            else if (seg.Dir == N)
            {
                AddQuad(buf, LodPackedFace.North, step, color, alpha,
                    a0, yBottom, fixedCoord, a0, yTop, fixedCoord,
                    a1, yTop, fixedCoord, a1, yBottom, fixedCoord, textureSlot);
            }
            else
            {
                AddQuad(buf, LodPackedFace.South, step, color, alpha,
                    a0, yBottom, fixedCoord, a1, yBottom, fixedCoord,
                    a1, yTop, fixedCoord, a0, yTop, fixedCoord, textureSlot);
            }

            i = j;
        }
    }

    // ---- Face collection helpers ----

    static bool IsTranslucent(byte flags) =>
        (flags & (LodPaletteEntry.FlagWater | LodPaletteEntry.FlagThin)) != 0;

    static bool IsTranslucentRun(SectionSnapshot s, ulong run) =>
        IsTranslucent(s.PaletteFlags[LodSection.RunPaletteId(run)]);

    /// <summary>
    /// Thin ground cover is drawn as a mat a quarter block tall, so it fills almost none
    /// of its cell and must never occlude anything. Without this a fern on a shoreline
    /// counted as cover for the water beside it and deleted the water's wall, letting you
    /// see through the edge of the pond.
    /// </summary>
    static bool IsThinRun(SectionSnapshot s, ulong run) =>
        (s.PaletteFlags[LodSection.RunPaletteId(run)] & LodPaletteEntry.FlagThin) != 0;

    /// <summary>
    /// A run this mesher draws as a solid cube that the GAME draws as a see-through cutout -
    /// a crop, tall grass, a vine (<see cref="LodPaletteEntry.FlagCutout"/>). It covers
    /// nothing, for the same reason a mat covers nothing.
    ///
    /// The difference only shows at the vanilla seam, and it shows as a hole. Inside our own
    /// terrain a crop cube really does hide the soil's top face and its neighbour's wall, so
    /// dropping them was correct and free. Where the game draws instead of us, the game's
    /// crop is a transparent tuft, and those two dropped faces become a window through the
    /// hollow shell of the cached terrain and out to the sky. Reported in play at a seam
    /// running through a flax field.
    ///
    /// The cost is walls between neighbouring crops and a top face under each one, all of it
    /// sealed inside solid geometry and invisible everywhere except the seam that needs it.
    /// That is why it is a switch: see MeshJob.CutoutBlocksCover and `.vhcutout`.
    /// </summary>
    static bool IsCutoutRun(SectionSnapshot s, ulong run, bool cutoutsCover) =>
        !cutoutsCover
        && (s.PaletteFlags[LodSection.RunPaletteId(run)] & LodPaletteEntry.FlagCutout) != 0;

    /// <summary>
    /// The block only fills the lower half of its cell (a terrain slab, a shallow snow
    /// layer). The flag is a property of the palette entry, so a run of them shortens once,
    /// at its top: two slabs cannot actually stack without air between them, and a run that
    /// merged them anyway is already an approximation of that column.
    /// </summary>
    static bool IsHalfRun(SectionSnapshot s, ulong run) =>
        (s.PaletteFlags[LodSection.RunPaletteId(run)] & LodPaletteEntry.FlagHalf) != 0;

    /// <summary>
    /// This run's top surface, in half blocks. The single place slabs shorten, and the one
    /// switch that turns the whole behaviour off for a level.
    /// </summary>
    static int TopHalf(SectionSnapshot s, Span<ulong> runs, int at, bool half,
        bool surfaceOnly)
    {
        ulong run = runs[at];
        return LodSection.RunYTop(run) * 2
            - (half && IsHalfRun(s, runs[at])
                && (!surfaceOnly || IsSurfaceHalfRun(s, runs, at)) ? 1 : 0);
    }

    /// <summary>
    /// Half-block detail is landscape detail. Preserve it on the highest solid terrain in
    /// a column (including under water or thin cover), while distant cave/overhang slabs
    /// keep the established full-block approximation.
    /// </summary>
    static bool IsSurfaceHalfRun(SectionSnapshot s, Span<ulong> runs, int at)
    {
        if (!IsHalfRun(s, runs[at])) return false;
        for (int i = 0; i < at; i++)
        {
            if (!IsTranslucentRun(s, runs[i])) return false;
        }
        return true;
    }

    /// <summary>This run's floor, in half blocks. A run always starts on a block boundary.</summary>
    static int BottomHalf(ulong run) => LodSection.RunYBottom(run) * 2;

    static (SectionSnapshot? snap, int col) NeighborColumn(
        MeshJob job, SectionSnapshot self, int cx, int cz)
    {
        int gs = LodSection.GridSize;

        if (cx >= 0 && cx < gs && cz >= 0 && cz < gs)
        {
            // The current column came from the post-cull snapshot, so its in-section
            // neighbour must come from that same snapshot. Reading job.Self here compares
            // invented rock with the old cave air and emits two walls across every internal
            // cave-cell boundary.
            return (self, LodSection.ColumnIndex(cx, cz));
        }

        SectionSnapshot? nb;
        int ncx = cx, ncz = cz;
        if (cx < 0) { nb = job.Neighbors[W]; ncx = gs - 1; }
        else if (cx >= gs) { nb = job.Neighbors[E]; ncx = 0; }
        else if (cz < 0) { nb = job.Neighbors[N]; ncz = gs - 1; }
        else { nb = job.Neighbors[S]; ncz = 0; }

        return (nb, nb == null ? 0 : LodSection.ColumnIndex(ncx, ncz));
    }

    /// <summary>
    /// Collect exposed wall segments for [yBottom, yTop) minus the neighbor's covered
    /// intervals. Every height here is in HALF blocks, including the neighbour's, so a
    /// slab beside a full block leaves the top half of that block's wall exposed instead
    /// of culling it and opening a see-through notch into the ground.
    /// </summary>
    static void CollectSide(List<VFace> vf, MeshJob job, SectionSnapshot self,
        LodCaveCull.Prepared cave, int dir, int cx, int cz, int ncx, int ncz,
        int yTop, int yBottom, int pid, bool isTranslucent, bool solidCoverOnly, bool half,
        bool surfaceHalfOnly, bool cutoutsCover, int waterSurface)
    {
        var (nb, ncol) = NeighborColumn(job, self, ncx, ncz);

        // The neighbour section is absent from RAM but the cache holds data for it, so
        // this is not the edge of explored space and there is nothing here to wall off.
        //
        // Water only. A spurious water wall is the worst artefact this mesher can
        // produce: an ocean edge is a 64-column, seabed-deep sheet at 66% opacity that
        // shows straight through the flat surface as a dark line down every section
        // boundary, and sections are meshed nearest-first so the outward side of nearly
        // every one of them was built before its neighbour had loaded. The same wall on
        // solid ground is hidden by the neighbouring terrain and is left alone: dropping
        // it would open a see-through gap at a cliff for as long as the repair takes.
        if (isTranslucent && nb == null && (job.AssumedCoveredSides & (1 << dir)) != 0) return;

        Span<ulong> neighborRuns = nb != null && nb.Captured[ncol] ? nb.ColumnRuns(ncol) : Span<ulong>.Empty;

        // For W/E walls the strip axis is Z (fixed = cx); for N/S it's X (fixed = cz).
        short fix = (short)(dir is W or E ? cx : cz);
        short along = (short)(dir is W or E ? cz : cx);

        // Cave filling is local to the mesh job: the neighbour snapshot still contains
        // its original cave. The shared 3x3 classification already knows which air in the
        // immediately adjacent boundary column is effectively rock, so fold that coverage
        // into this side without rebuilding four complete neighbour sections. This also
        // makes the two sides of a cross-section cave agree without another flood pass.
        bool external = ncx < 0 || ncx >= LodSection.GridSize
            || ncz < 0 || ncz >= LodSection.GridSize;
        if (external && cave.HasBoundaryCoverage && neighborRuns.Length > 0)
        {
            CollectSideWithCaveCoverage(vf, cave, neighborRuns, nb!, dir, fix, along,
                ncx, ncz, yTop, yBottom, pid, isTranslucent, solidCoverOnly, half,
                surfaceHalfOnly, cutoutsCover, waterSurface);
            return;
        }

        int cur = yTop;

        for (int i = 0; i < neighborRuns.Length && cur > yBottom; i++)
        {
            // A mat never covers anything; beyond that, solid faces are only culled by
            // solid neighbours so terrain stays visible through water.
            if (IsThinRun(nb!, neighborRuns[i])) continue;
            if (IsCutoutRun(nb!, neighborRuns[i], cutoutsCover)) continue;
            if (solidCoverOnly && IsTranslucentRun(nb!, neighborRuns[i])) continue;

            int nTop = TopHalf(nb!, neighborRuns, i, half, surfaceHalfOnly);
            int nBottom = BottomHalf(neighborRuns[i]);
            if (nTop <= yBottom) break;
            if (nBottom >= cur) continue;

            int coverTop = Math.Min(nTop, cur);
            if (coverTop < cur)
            {
                vf.Add(new VFace((byte)dir, fix, along, (short)cur, (short)coverTop, (short)pid, isTranslucent,
                    WallSubmerged(isTranslucent, waterSurface, cur)));
            }
            cur = Math.Max(nBottom, yBottom);
        }

        if (cur > yBottom)
        {
            vf.Add(new VFace((byte)dir, fix, along, (short)cur, (short)yBottom, (short)pid, isTranslucent,
                WallSubmerged(isTranslucent, waterSurface, cur)));
        }
    }

    /// <summary>
    /// Subtract the effective post-cull coverage of one external neighbour column. The
    /// original runs retain water/thin semantics; dark air selected by the cave pass adds
    /// opaque coverage only within the vertical extent that neighbour could rebuild.
    /// </summary>
    static void CollectSideWithCaveCoverage(List<VFace> vf, LodCaveCull.Prepared cave,
        Span<ulong> neighborRuns, SectionSnapshot neighbor, int dir, short fix, short along,
        int ncx, int ncz, int yTop, int yBottom, int pid, bool isTranslucent,
        bool solidCoverOnly, bool half, bool surfaceHalfOnly, bool cutoutsCover,
        int waterSurface)
    {
        // yTop/yBottom arrive in half blocks; the cave classification is per whole block,
        // so the walk stays on whole cells and each cell reports how much of itself it
        // covers. Only a neighbouring slab's own top cell covers a half.
        int columnTop = LodSection.RunYTop(neighborRuns[0]);
        int columnBottom = LodSection.RunYBottom(neighborRuns[^1]);
        int runAt = 0;
        int openTop = -1;

        for (int y = (yTop + 1) / 2 - 1; y >= yBottom / 2; y--)
        {
            // This cell clipped to the run being walled, again in half blocks. The top
            // cell is short by half when the run itself is a slab.
            int cellTop = Math.Min((y + 1) * 2, yTop);
            int cellBottom = Math.Max(y * 2, yBottom);
            if (cellTop <= cellBottom) continue;

            while (runAt < neighborRuns.Length
                && LodSection.RunYBottom(neighborRuns[runAt]) > y)
            {
                runAt++;
            }

            // Where the neighbour's cover reaches inside this cell, or -1 for none. Cover
            // always starts at the cell floor, so one number describes it.
            int coverTop = -1;
            if (runAt < neighborRuns.Length)
            {
                ulong run = neighborRuns[runAt];
                if (LodSection.RunYTop(run) > y && LodSection.RunYBottom(run) <= y
                    && !IsThinRun(neighbor, run)
                    && !IsCutoutRun(neighbor, run, cutoutsCover)
                    && (!solidCoverOnly || !IsTranslucentRun(neighbor, run)))
                {
                    coverTop = half && IsHalfRun(neighbor, run)
                        && (!surfaceHalfOnly
                            || IsSurfaceHalfRun(neighbor, neighborRuns, runAt))
                        && LodSection.RunYTop(run) == y + 1
                        ? y * 2 + 1
                        : y * 2 + 2;
                }
            }

            if (coverTop < 0 && y >= columnBottom && y < columnTop && cave.Fills(ncx, ncz, y))
            {
                coverTop = y * 2 + 2;
            }

            if (coverTop < 0)
            {
                if (openTop < 0) openTop = cellTop;
                continue;
            }

            int openBottom = Math.Max(coverTop, cellBottom);
            if (openBottom >= cellTop)
            {
                // Fully covered: close whatever was open above it.
                if (openTop >= 0)
                {
                    vf.Add(new VFace((byte)dir, fix, along,
                        (short)openTop, (short)cellTop, (short)pid, isTranslucent,
                        WallSubmerged(isTranslucent, waterSurface, openTop)));
                    openTop = -1;
                }
                continue;
            }

            // Half covered: the wall runs down to the slab's top and stops there.
            if (openTop < 0) openTop = cellTop;
            vf.Add(new VFace((byte)dir, fix, along,
                (short)openTop, (short)openBottom, (short)pid, isTranslucent,
                WallSubmerged(isTranslucent, waterSurface, openTop)));
            openTop = -1;
        }

        if (openTop >= 0)
        {
            vf.Add(new VFace((byte)dir, fix, along,
                (short)openTop, (short)yBottom, (short)pid, isTranslucent,
                WallSubmerged(isTranslucent, waterSurface, openTop)));
        }
    }

    // ---- Emission primitives ----

    static void AddQuad(Buffers buf, LodPackedFace face, int columnBlocks, int color, byte alpha,
        float x0, float y0, float z0, float x1, float y1, float z1,
        float x2, float y2, float z2, float x3, float y3, float z3,
        int textureSlot = 0)
    {
        int baseVert = buf.Xyz.Count / 3;
        AddVert(buf, color, alpha, x0, y0, z0);
        AddVert(buf, color, alpha, x1, y1, z1);
        AddVert(buf, color, alpha, x2, y2, z2);
        AddVert(buf, color, alpha, x3, y3, z3);
        buf.Indices.Add(baseVert);
        buf.Indices.Add(baseVert + 1);
        buf.Indices.Add(baseVert + 2);
        buf.Indices.Add(baseVert);
        buf.Indices.Add(baseVert + 2);
        buf.Indices.Add(baseVert + 3);

        if (buf.Pack)
        {
            if (textureSlot > 0) buf.HasTextureSlots = true;
            float minX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
            float maxX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
            float minY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
            float maxY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
            float minZ = Math.Min(Math.Min(z0, z1), Math.Min(z2, z3));
            float maxZ = Math.Max(Math.Max(z0, z1), Math.Max(z2, z3));
            LodPackedQuadFormat.Append(
                buf.Packed, face,
                (int)MathF.Round(minX / columnBlocks),
                (int)MathF.Round(maxX / columnBlocks),
                minY, maxY,
                (int)MathF.Round(minZ / columnBlocks),
                (int)MathF.Round(maxZ / columnBlocks),
                color, alpha, textureSlot);
            buf.ClusteredPacked!.Append(
                face,
                (int)MathF.Round(minX / columnBlocks),
                (int)MathF.Round(maxX / columnBlocks),
                minY, maxY,
                (int)MathF.Round(minZ / columnBlocks),
                (int)MathF.Round(maxZ / columnBlocks),
                columnBlocks,
                color, alpha, textureSlot);
        }
    }

    /// <summary>
    /// The block texture slot this quad may carry, or 0 for none.
    ///
    /// Level 0 only - `step` is one block there. A coarse section merges a whole
    /// neighbourhood into a single colour, so no one block stands behind the quad for a
    /// texture to belong to, and the colour form stays exactly as it is today.
    ///
    /// Solid land only. Water and thin cover draw translucent and were never the complaint.
    /// </summary>
    static int TextureSlotFor(SectionSnapshot self, int pid, int step, bool emitTextureSlots) =>
        emitTextureSlots && step == 1 && !IsTranslucent(self.PaletteFlags[pid])
            && (uint)pid < (uint)self.PaletteTextureSlots.Length
            ? self.PaletteTextureSlots[pid]
            : 0;

    static void AddVert(Buffers buf, int color, byte alpha, float x, float y, float z)
    {
        if (y < buf.MinY) buf.MinY = y;
        if (y > buf.MaxY) buf.MaxY = y;
        buf.Xyz.Add(x);
        buf.Xyz.Add(y);
        buf.Xyz.Add(z);
        buf.Rgba.Add((byte)(color & 0xFF));
        buf.Rgba.Add((byte)((color >> 8) & 0xFF));
        buf.Rgba.Add((byte)((color >> 16) & 0xFF));
        buf.Rgba.Add(alpha);
    }
}
