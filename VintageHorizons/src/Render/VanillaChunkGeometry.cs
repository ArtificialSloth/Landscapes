using System.Linq.Expressions;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace VintageHorizons;

/// <summary>
/// Answers the one question the engine's own "is this chunk rendered" query cannot:
/// did the engine actually produce any geometry for it.
///
/// <para>
/// <c>ICoreClientAPI.IsChunkRendered</c> compiles to <c>ClientChunk.quantityDrawn > 0</c>,
/// and <c>ChunkTesselatorManager.TesselateChunk</c> advances that counter and returns
/// before meshing whenever the chunk reports empty. So a chunk of air answers exactly like
/// a chunk of mountain, which is G40.
/// </para>
/// <para>
/// The mesh the tesselator does produce is recorded on the chunk as pool locations: index
/// and vertex ranges inside a shared model data pool. A chunk holding none of them has no
/// terrain in the world's buffers, whatever the drawn counter says. Those two arrays are
/// internal, so they are bound once by reflection and read through a compiled delegate;
/// a game update that renames them turns the whole query off rather than guessing, and
/// <c>tests/VintageHorizons.Checks</c> fails against the installed game if it does.
/// </para>
/// <para>
/// This is a measurement. It must never decide ownership on its own: G40 records what
/// happened the last time a chunk-emptiness rule reached the draw path from reasoning
/// rather than from evidence.
/// </para>
/// </summary>
static class VanillaChunkGeometry
{
    /// <summary>The two internal fields, or null when this game build does not have them.</summary>
    static readonly System.Func<ClientChunk, ModelDataPoolLocation[]?>? Center = Bind("centerModelPoolLocations");
    static readonly System.Func<ClientChunk, ModelDataPoolLocation[]?>? Edge = Bind("edgeModelPoolLocations");

    /// <summary>The field names this build binds, for the check tier to verify against the game.</summary>
    public static readonly string[] FieldNames = { "centerModelPoolLocations", "edgeModelPoolLocations" };

    /// <summary>False when the game changed shape underneath us and nothing can be measured.</summary>
    public static bool Available => Center != null && Edge != null;

    static System.Func<ClientChunk, ModelDataPoolLocation[]?>? Bind(string field)
    {
        try
        {
            FieldInfo? info = typeof(ClientChunk).GetField(field,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (info == null || info.FieldType != typeof(ModelDataPoolLocation[])) return null;

            // A compiled getter rather than FieldInfo.GetValue: this runs once per probed
            // cell inside the render frame's own budget, where reflection's per-call cost
            // is the difference between a diagnostic and a regression.
            ParameterExpression chunk = Expression.Parameter(typeof(ClientChunk), "chunk");
            return Expression.Lambda<System.Func<ClientChunk, ModelDataPoolLocation[]?>>(
                Expression.Field(chunk, info), chunk).Compile();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the engine holds meshed terrain for this chunk. Returns false when the
    /// answer is unavailable, which is the safe direction: an unmeasurable chunk is never
    /// reported as a discrepancy.
    /// </summary>
    public static bool TryHoldsGeometry(IWorldChunk? chunk, out bool holdsGeometry)
    {
        holdsGeometry = false;
        if (chunk is not ClientChunk client || Center == null || Edge == null) return false;

        try
        {
            holdsGeometry = HasMesh(Center(client)) || HasMesh(Edge(client));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the engine is currently drawing this chunk, which is the question ownership
    /// actually needs and the one nothing else here answers.
    ///
    /// <para>
    /// <c>IsChunkRendered</c> reads a counter that only ever goes up, so it means "this was
    /// tesselated at some point". Holding mesh pool locations is closer but still not it: the
    /// engine keeps a chunk's mesh while deciding not to submit it. <c>ChunkCuller</c> walks
    /// rays out from the camera each frame and writes its verdict to
    /// <c>ClientChunk.CullVisible</c>; a chunk it does not reach is not drawn, whatever the
    /// counter and the mesh say. The culler also has an <c>isAboveHeightLimit</c> path, which
    /// is why flying high enough makes vanilla stop drawing the ground directly below while
    /// every other signal still claims it.
    /// </para>
    /// <para>
    /// All three members are public API, so this needs no reflection. Empty counts as drawn:
    /// there is nothing to submit and nothing for the cache to cover, and refusing sky its
    /// cell is what collapsed 0.3.3. Unknown counts as drawn, so a wrong answer can only cost
    /// the correction, never open the cache over live terrain.
    /// </para>
    /// </summary>
    public static bool TryIsVanillaDrawing(IWorldChunk? chunk, out bool drawing)
    {
        drawing = true;
        if (chunk is not ClientChunk client) return false;

        try
        {
            if (chunk.Empty) return true;
            if (!TryHoldsGeometry(chunk, out bool holdsGeometry)) return false;
            if (!holdsGeometry)
            {
                drawing = false;
                return true;
            }

            // A chunk can hold its mesh and still be flagged not to submit it, which is a
            // separate switch from the culler's verdict. Both have to say yes.
            drawing = !AllHidden(Center?.Invoke(client), Edge?.Invoke(client))
                && client.CullVisible[ClientChunk.bufIndex];
            return true;
        }
        catch
        {
            drawing = true;
            return false;
        }
    }

    /// <summary>
    /// The inputs the engine's own range cull uses for this chunk: the bounding-sphere centre
    /// it measures from, and which terrain LOD pools it actually holds drawable, unhidden
    /// geometry in.
    ///
    /// <para>
    /// One sphere covers the whole chunk. `ChunkRenderer.AddTesselatedChunk` reads
    /// `tesschunk.boundingSphere` once and hands that same value to every centre and edge part,
    /// so all of a chunk's pool locations carry an identical `FrustumCullSphere` and only their
    /// `LodLevel` differs. That is what collapses "will the engine submit any of this chunk's
    /// meshes" to one point and one small bitmask.
    /// </para>
    /// <para>
    /// The centre is NOT the chunk centre. `TesselatedChunk.SetBounds` builds it as the geometry
    /// extents midpoint, `positionX + (xMax + xMin) / 2f`, so it sits wherever the blocks in that
    /// chunk happen to be. Bounding that unknown placement is exactly the 46 blocks of overlap
    /// this method exists to delete.
    /// </para>
    /// <para>
    /// `Hide` is applied here rather than by the caller because it is part of the same engine
    /// predicate: a hidden pool location is not submitted, so it must not contribute its level.
    /// A chunk whose every mesh is hidden reports no levels and is answered as "not drawing",
    /// which is the same conclusion <see cref="TryIsVanillaDrawing"/> reaches by its own route.
    /// </para>
    /// <para>
    /// Returns false when the answer is unavailable OR when the chunk holds nothing drawable.
    /// Both are the same instruction to the caller - do not decide ownership from this - and
    /// keeping them one return value is deliberate: a caller that treated "no mesh" as a
    /// confident "the engine draws nothing here" would be reinventing 0.3.3 (G40).
    /// </para>
    /// </summary>
    public static bool TryReadDrawBounds(IWorldChunk? chunk,
        out float centreX, out float centreZ, out int lodMask)
    {
        centreX = centreZ = 0f;
        lodMask = 0;
        if (chunk is not ClientChunk client || Center == null || Edge == null) return false;

        try
        {
            bool found = false;
            AccumulateDrawBounds(Center(client), ref centreX, ref centreZ, ref lodMask, ref found);
            AccumulateDrawBounds(Edge(client), ref centreX, ref centreZ, ref lodMask, ref found);
            return found && lodMask != 0;
        }
        catch
        {
            lodMask = 0;
            return false;
        }
    }

    static void AccumulateDrawBounds(ModelDataPoolLocation[]? locations,
        ref float centreX, ref float centreZ, ref int lodMask, ref bool found)
    {
        if (locations == null) return;
        foreach (ModelDataPoolLocation? location in locations)
        {
            if (location == null || location.IndicesEnd <= location.IndicesStart) continue;
            if (location.Hide) continue;

            // Out-of-range levels are dropped rather than clamped. The engine's own switch
            // answers false for anything it does not recognise, so a pool level added by a
            // future game version contributes nothing here and the chunk falls back to the
            // conservative bound instead of being granted ownership on a guess.
            int level = location.LodLevel;
            if (level < 0 || level >= VanillaDrawRangeState.LodLevelCount) continue;

            if (!found)
            {
                centreX = location.FrustumCullSphere.x;
                centreZ = location.FrustumCullSphere.z;
                found = true;
            }
            lodMask |= 1 << level;
        }
    }

    /// <summary>
    /// Every signal the engine offers about one chunk, printed side by side. Written for
    /// standing at a hole: each of these has at some point been mistaken for "the engine is
    /// drawing here", and the only way to stop guessing which one is lying is to read them
    /// together at a cell that is actually wrong.
    ///
    /// <para>
    /// counter - the ever-increasing tesselation marker behind IsChunkRendered (G40).
    /// empty - the server's own emptiness flag, delivered with the chunk.
    /// mesh - whether the engine holds index ranges for it in the shared pool.
    /// cull - the ray culler's per-frame verdict, CullVisible[bufIndex] (G43).
    /// hide - whether the pool locations are flagged not to submit.
    /// frustum - whether the engine last found it inside the view frustum.
    /// </para>
    /// </summary>
    public static string DescribeSignals(IWorldChunk? chunk)
    {
        if (chunk == null) return "signals: chunk not loaded";
        if (chunk is not ClientChunk client) return "signals: not a client chunk";

        try
        {
            bool anyMesh = false;
            bool anyHidden = false;
            bool anyFrustum = false;
            foreach (ModelDataPoolLocation[]? set in new[] { Center?.Invoke(client), Edge?.Invoke(client) })
            {
                if (set == null) continue;
                foreach (ModelDataPoolLocation? location in set)
                {
                    if (location == null) continue;
                    if (location.IndicesEnd > location.IndicesStart) anyMesh = true;
                    if (location.Hide) anyHidden = true;
                    if (location.FrustumVisible) anyFrustum = true;
                }
            }

            // The sphere and the level set are the two inputs to the engine's range clause,
            // and until 0.3.153 neither appeared in any diagnostic - so a cell that was
            // range-culled looked identical to one that was not, in the one report written
            // for standing at a hole. Printed even when the range rule is off, because the
            // question "where does the engine measure this chunk from" is the same either way.
            string bounds = TryReadDrawBounds(chunk, out float centreX, out float centreZ, out int lodMask)
                ? $" sphere={centreX:0.0},{centreZ:0.0} lods={LodMaskText(lodMask)}"
                : " sphere=none";

            // The drawn counter is the FIRST gate every probe passes through - it is what
            // `IsChunkRendered` reads - and this report was written to print every signal side
            // by side and then did not print that one. Two sessions were spent reasoning about
            // which gate had refused a chunk while the only one named in the summary above was
            // missing from the output.
            int drawnCount = DrawnCount(client, out bool drawnKnown);
            string drawn = drawnKnown ? drawnCount.ToString() : "?";

            return $"signals: drawn={drawn} empty={(chunk.Empty ? 1 : 0)} mesh={(anyMesh ? 1 : 0)} "
                + $"cull={(client.CullVisible[ClientChunk.bufIndex] ? 1 : 0)} "
                + $"hide={(anyHidden ? 1 : 0)} frustum={(anyFrustum ? 1 : 0)}{bounds}";
        }
        catch (Exception e)
        {
            return "signals unavailable: " + e.GetType().Name;
        }
    }

    /// <summary>
    /// `ClientChunk.quantityDrawn`, the counter behind `IsChunkRendered`. Internal like the
    /// pool arrays, so it is bound the same way and reports unknown rather than guessing.
    /// It only ever rises (nothing in the engine resets it), which is exactly why it means
    /// "was tesselated at some point" and not "is being drawn" - G40.
    /// </summary>
    static readonly System.Func<ClientChunk, int>? DrawnCounter = BindDrawn();

    static System.Func<ClientChunk, int>? BindDrawn()
    {
        try
        {
            FieldInfo? info = typeof(ClientChunk).GetField("quantityDrawn",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (info == null || info.FieldType != typeof(int)) return null;
            ParameterExpression chunk = Expression.Parameter(typeof(ClientChunk), "chunk");
            return Expression.Lambda<System.Func<ClientChunk, int>>(
                Expression.Field(chunk, info), chunk).Compile();
        }
        catch
        {
            return null;
        }
    }

    static int DrawnCount(ClientChunk chunk, out bool known)
    {
        known = false;
        if (DrawnCounter == null) return 0;
        try
        {
            int value = DrawnCounter(chunk);
            known = true;
            return value;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// The LOD pool set as digits, so a report says `lods=1` or `lods=12` rather than a number
    /// the reader has to decode into bits.
    /// </summary>
    public static string LodMaskText(int lodMask)
    {
        if (lodMask == 0) return "none";
        var text = new System.Text.StringBuilder(VanillaDrawRangeState.LodLevelCount);
        for (int level = 0; level < VanillaDrawRangeState.LodLevelCount; level++)
            if ((lodMask & (1 << level)) != 0) text.Append((char)('0' + level));
        return text.ToString();
    }

    /// <summary>
    /// True when every mesh this chunk holds is flagged not to draw. An absent array is not
    /// evidence either way here; the mesh-presence test above has already answered that.
    /// </summary>
    /// <remarks>
    /// Two explicit passes rather than a `foreach` over `new[] { center, edge }`. That array is
    /// a heap allocation, and this runs once per ownership-decision probe - hundreds of
    /// thousands of them per reporting interval, on the render thread. It is the kind of cost
    /// that never appears as a frame spike and shows up only as collections.
    /// </remarks>
    static bool AllHidden(ModelDataPoolLocation[]? center, ModelDataPoolLocation[]? edge)
    {
        bool sawAny = false;
        if (AnyDrawableUnhidden(center, ref sawAny)) return false;
        if (AnyDrawableUnhidden(edge, ref sawAny)) return false;
        return sawAny;
    }

    /// <summary>True as soon as one location in this set holds geometry and is not hidden.</summary>
    static bool AnyDrawableUnhidden(ModelDataPoolLocation[]? locations, ref bool sawAny)
    {
        if (locations == null) return false;
        foreach (ModelDataPoolLocation? location in locations)
        {
            if (location == null || location.IndicesEnd <= location.IndicesStart) continue;
            sawAny = true;
            if (!location.Hide) return true;
        }
        return false;
    }

    /// <summary>
    /// A pool location with no index range occupies a slot without drawing anything, so
    /// presence of the array alone is not presence of terrain.
    /// </summary>
    static bool HasMesh(ModelDataPoolLocation[]? locations)
    {
        if (locations == null) return false;
        foreach (ModelDataPoolLocation? location in locations)
            if (location != null && location.IndicesEnd > location.IndicesStart) return true;
        return false;
    }
}
