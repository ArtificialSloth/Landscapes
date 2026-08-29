# Plan - vanilla-to-LOD blend band

**Status:** Phases 1 and 2 built and human-tested through 0.3.139 (Session 65). The band mechanism
works as designed. **Phase 2's premise did not survive play**: three average-matching corrections
were each tuned to look excellent on one terrain and were worse than nothing on the next, and the
owner closed the question on domain knowledge - vanilla terrain is never a single flat colour, so
nothing that only adjusts an average can meet it. The work then moved to sampling the game's own
block textures, which is outside this plan's scope and is recorded in Session 65 and `STATUS.md`.

Read this plan for the band mechanism - the distance field, the block-grid invariant, the step
quantisation, the sink and the overlap margin all stand. Do **not** read section 5.3 as current:
the per-channel multiply it specifies was built, rejected, replaced by a darken/saturate pair, and
that was rejected too. Section 6's water diagnosis was also incomplete; a second water defect -
ownership suppressing cached water where vanilla draws the bed but not the river - was found and
fixed in 0.3.132 and is recorded as G124.
**Date:** 2026-08-28.
**Scope:** Client rendering only - one new renderer-owned CPU field, one new shading term
over LOD geometry the mod already builds, and a sub-block vertical offset inside the
existing vanilla overlap. No cache, database, network, wire-format, or vanilla-engine
change.
**Baseline:** Since 0.3.17 the handoff is per-cell ownership at 32x32x32 vanilla chunk
granularity; a cached fragment inside an owned cell is discarded. Ownership is denied
beyond `viewDistance - 46`, which is what keeps LOD drawing across the outermost chunk and
a half of live vanilla terrain and is the current hole guard. The older radial
`cacheHandoffDistance` cutoff survives as the `.vhmask off` fallback. Vanilla's own radial
terrain fade was moved beyond anything the engine can draw in 0.3.127, so vanilla terrain
currently ends at a hard, unfaded edge.

## 1. Outcome

A band of LOD terrain immediately outside the vanilla seam whose appearance starts as an
imitation of vanilla and relaxes outward into ordinary LOD appearance, such that a player
at normal play distance cannot locate the boundary between the two terrains.

- The band lies entirely outside vanilla's footprint. It never draws over ground vanilla
  owns and never alters vanilla's own rendering.
- Its shape is inherited from the real ownership footprint, not from a radius. It is a ring
  only because the loaded area is a ring.
- Its contours are quantised to the world block grid and read as blocky terrain, not as a
  screen-space effect.
- Existing hole protection is unchanged by this work.
- Z-fighting where LOD overlaps live vanilla is removed by a sub-block sink rather than by
  reducing the overlap.

## 2. Non-goals

- Do not correct the global LOD/vanilla colour difference. That is a separate owner
  project. This plan spreads the remaining step; it does not close it.
- Do not modify vanilla's shaders, uniforms, atmosphere, view distance, or draw order.
- Do not reduce the vanilla overlap in this plan. Revisit only after the band is accepted.
- Do not introduce a new mesh type, a new section level, or new geometry for the band.
- Do not vary any band value at a scale finer than one world block.
- Do not use screen-space dither, camera-relative noise, or any effect that moves when the
  camera turns rather than when the world does.
- Do not use alpha fade, transparency, or anything that could show sky through the ground.
- Do not promise the transition becomes invisible under inspection. The target is
  imperceptible at play distance while moving, judged by the owner in game.

## 3. Governing invariants

1. **Block-grid quantisation.** No value the band computes may change at a scale finer than
   one world block. This is the rule the previously rejected smooth radial ring violated: a
   gradient finer than the world's own grid reads as a GPU effect painted over the terrain
   rather than as terrain itself.
2. **Vanilla is untouched.** No vanilla program, uniform, mesh, or pixel is modified. All
   imitation of vanilla happens inside LOD geometry the mod owns.
3. **Inner edge is exact.** The band's inner edge coincides with the committed ownership
   boundary in the same frame. Any lag there leaves uncorrected LOD sitting at the seam,
   which is the original defect.
4. **No radius.** The band is derived from the ownership footprint. No band decision may be
   a function of distance from the camera.
5. **Fail toward the current look.** A missing field, failed upload, unavailable tracker,
   non-default dimension, or world change disables the band and restores present behaviour.
   It never opens a hole and never leaves a partial correction applied.
6. **Hole protection is untouched.** The `viewDistance - 46` ownership denial and the
   overlap it produces are not modified by this plan.
7. **Bounded work.** Field rebuild is item- and time-bounded like every other renderer
   maintenance pass, and does no per-frame full-window scan.
8. **Render-thread GPU ownership.** Field texture creation, update, binding, and disposal
   stay with the renderer. No worker calls OpenGL.
9. **ASCII shaders.** All edited shader source and comments remain pure ASCII.

## 4. Evidence

### 4.1 Measured - offline, from an owner screenshot

`Pictures/Vintagestory/2026-08-28_13-49-39.png`, sampled as a 100px-wide column crossing
the land seam away from tree cover, in 16px bands:

| Side | R | G | B |
|---|---|---|---|
| LOD, just beyond the seam | 161 | 164 | 105 |
| Vanilla, just inside the seam | 140 | 152 | 83 |
| Step | +21 | +12 | +22 |

Flat on both sides, sharp across roughly 40 screen pixels. **LOD is about 8% brighter than
vanilla and loses the green cast.** R and B move nearly twice as far as G.

**Not established:** the cause. The channel pattern is consistent with LOD lacking vanilla's
dark pixels - grass texture detail, ambient occlusion in block crevices, and drop shadows -
but that is an untested hypothesis. One screenshot, one sample line, possible tree and
shadow contamination. The number is used here only to size the band.

**No water surface step was found.** Both 2026-08-28 screenshots were sampled - the river in
`13-49-39` and the lake in `13-49-31`, the latter as a vertical profile of 200px-wide bands
across open water. Water varied by only about 5 levels across the whole sampled span and no
boundary could be located in it. That negative result is consistent with the owner's report
that the water surfaces themselves look correct, and with the later diagnosis in section 6
that the real water defect is on the lake floor rather than the surface.

### 4.2 Source-traced

- Level 0 LOD is 1 block per column and runs to 512 blocks by default
  (`LodWorld.DefaultLevelThresholds`). At the owner's 192-384 vanilla view distances the
  seam and its band therefore sit entirely on 1-block LOD geometry, matching vanilla's own
  shape resolution. The visible defect is shading, not blockiness.
- The readiness mask is a 2D atlas of Y slices at 32-block cell granularity, addressed from
  an integer section origin plus a section-local offset, because float32 rounds a fragment
  near a chunk edge onto its neighbour at large world coordinates.
- LOD water is a flat plate drawn at a fixed 66% alpha with one tint slot, two-sided, in
  traversal order. It has no wave geometry, no specular or reflection, and no depth-based
  tint. Vanilla water has all four.
- A previous downward sink of LOD geometry near the transition was 8 blocks, was visibly
  wrong as the player approached, and was removed.

### 4.3 Owner statements - 2026-08-28

- The colour step is the primary defect and is what lets the eye find the boundary line.
- It is equally visible at all times of day; dusk and dawn are no worse.
- Vanilla visibly wins in the overlap, with LOD stabbing through; the effect is tolerable
  but unwanted.
- Band width is not constrained. Narrower is preferred, but fidelity outranks it.
- Overlap reduction is wanted mainly because it looks poor, and only secondarily for cost.
- Play distances are 192-384; the mod's value is that it lets vanilla's distance drop.
- The water transition is also jarring and is to be given its own attention.
- **LOD water surrounded only by LOD water looks fine.** The water defect is the overlap and
  transition itself, not LOD water's own appearance. Whatever is needed to fix it is
  authorised.
- The two water meshes do not appear to actually overlap. What looks terrible is the
  z-fighting on the river and lake floor.

## 5. Mechanism - land

### 5.1 Seam distance field

When the ownership mask updates, the CPU computes, for every 8-block cell in the window, the
horizontal distance to the nearest chunk vanilla owns. Two-pass sweep over a few thousand
cells. Uploaded beside the existing mask.

8 blocks is chosen deliberately, not as a resolution ceiling:

- It is a quarter of a vanilla chunk - small enough to read as a finer continuation of the
  game's blocky idiom, large enough to still read as steps at 200+ blocks.
- 16 and 32 are reachable from it as knobs. Finer than 8 is not needed: the step being
  hidden is about 21 levels, and 8-block steps across a 128-block band already put each step
  near the eye's threshold.
- Cost scales with the square of resolution. 1-block cells would be roughly 64x the cells
  and memory, rebuilt on every window move, which stops being free.
- Cell addressing has 8x the float precision margin of 1-block cells against the rounding
  trap the mask already works around.

The field's resolution governs the ramp only. The band's inner edge is the ownership
boundary and is therefore at vanilla's own 32-block granularity; nothing makes it finer.

### 5.2 Band weight

One value per fragment: 1 at the seam, falling to 0 at the outer edge, quantised into
8-block steps. Everything else is a function of it. Outside the band the shader does no
additional work.

- Width defaults to about 128 blocks, expressed in absolute blocks and **not** scaled by
  view distance. The step being hidden is a fixed number of colour levels regardless of how
  far away the seam is, so the number of steps required does not change when the seam moves.
- The inner edge follows the committed ownership boundary exactly and immediately.
- The outer edge is placed from a temporally smoothed width so the band does not visibly
  breathe as vanilla chunks stream in and out. Smoothing applies to width only, never to the
  inner edge.

### 5.3 The correction

At weight 1 the band wears vanilla's appearance; at 0 it wears LOD's own. The correction is
**multiplicative and contrast-shaped, not an additive colour offset**, because the measured
channel pattern says LOD's deficit is missing dark pixels rather than a wrong average. A
flat darkening would fix the mean and still look wrong, and would not survive a biome
change; a contrast term has a better chance of doing both.

Phase 2 extends this with a cheap block-step ambient-occlusion darkening inside the band,
restoring the actual missing signal rather than approximating its average. That is the most
likely route to a genuinely invisible seam and also the most speculative, which is why it is
not phase 1.

### 5.4 Geometry sink

Where LOD overlaps live vanilla, LOD geometry is lowered by one voxel (1/16 block), ramped
to zero by the time it reaches the band, to remove the z-fighting and stabbing-through the
owner reports. One voxel is chosen against the removed 8-block sink specifically because it
is below the threshold at which an approaching player can see the surface move.

The sink cures depth fighting between opaque surfaces, which is a precision problem, and
only that. Its most valuable application is underwater rather than on open land; see
section 6. Sink depth is a live knob, because one voxel is a starting guess and depth
precision at overlap distance may demand more.

## 6. Water - submerged terrain z-fighting, cured by the sink

**Diagnosed 2026-08-28 from `Pictures/Vintagestory/2026-08-28_14-54-55.png`.** The water
surfaces are not the problem. The owner reports LOD water surrounded by LOD water looks
fine, and that the two water meshes do not appear to overlap. What looks terrible is the
lake and river **floor**.

The screenshot shows unambiguous z-fighting across a broad band at the overlap distance:
ragged, horizontally elongated slivers of pale seabed punching through the dark submerged
surface, following no terrain feature, with the characteristic per-pixel alternation of two
near-coplanar surfaces.

**Why underwater looks so much worse than land, despite being the same defect.** The two
fighting surfaces differ enormously in colour here - pale bed against dark water-tinted bed
- so every pixel that flips is a maximum-contrast flip. The same amount of depth fighting
between two similarly-coloured land surfaces is nearly invisible. The severity is a contrast
effect, not a different mechanism.

**This is exactly the class the sink cures**, because it is a depth-precision problem
between opaque surfaces. No water-specific ownership rule, alpha ramp, or faked shine is
required. Those options are dropped.

**Open risk - 1/16 may not be enough at this distance.** Depth precision coarsens with
distance and this band sits 200+ blocks out. One voxel is a starting guess, not a derived
separation. Sink depth is therefore a `.vhband` knob from the first build, and if 1/16 does
not clear the fighting the depth is raised until it does.

**Why a larger sink is safe here in a way the removed 8-block sink was not.** The old sink
was visibly wrong because it moved as the player approached it. The overlap ring is always
at roughly the player's vanilla view distance, so it holds a near-constant distance from the
camera as the player moves. A depth chosen to suit that distance does not shift underfoot.

**Retained caution.** If a sink depth large enough to clear the seabed also lifts or lowers
a water surface relative to vanilla's, it would read as a lip across an otherwise perfectly
flat plane. Sink submerged terrain, not water surfaces, and verify at a shoreline.

## 7. Live controls

The first playable build is a tuning rig. Every number above is a live `.vhband` subcommand
so the owner can stand at the seam and dial until it disappears; only then are the defaults
frozen. At minimum: width, step size (8/16/32, no continuous option), correction strength,
sink depth, dither on/off once it exists, and a debug view that paints the band weight as a
colour ramp so the ring's shape can be judged from above independently of any colour work.

## 8. Phases

1. **Band, with debug view included.** Seam distance field, band weight, contrast
   correction, sink, and the full `.vhband` control set including the shape debug view.
   Delivered as one playable artifact for owner tuning. The sink applies to submerged
   terrain from the start - per section 6 that is where it is needed most - but not to water
   surfaces.
2. **Dither and the AO term**, each independently toggleable for A/B comparison. Dither must
   be anchored in world space per block; a screen-space pattern would crawl as the camera
   turns and is the same mistake the rejected smooth ring made. Precedent exists:
   cached-terrain colour noise was already moved off camera-relative coordinates onto a
   fixed world origin. Narrow the band if dither permits.
3. **Water follow-up, only if phase 1 does not clear it.** On the section 6 diagnosis the
   sink is the whole fix and there is no separate water phase. This slot exists for the case
   where no safe sink depth clears the seabed fighting at overlap distance.
4. **Revisit the overlap**, only after the band is accepted, and only if the sink has not
   already made the overlap a non-issue.

## 9. Known limits

- **The band requires level 0 geometry.** It works because LOD at the seam is 1 block per
  column, matching vanilla. At vanilla view distances beyond roughly 380 the seam starts
  landing on 2- and 4-block LOD columns and the imitation degrades, because the shapes stop
  matching. A rule that level 0 always extends to at least seam plus band width would fix it
  at a memory cost. Not required at the owner's 192-384 play range; stated here so it is not
  rediscovered later.
- **The correction is a guess at vanilla's appearance.** Vanilla does not exist outside the
  seam, so its pixels cannot be sampled there. The multiplicative form is the hedge; owner
  testing across at least two biomes is what would prove or break it.
- **The band spreads the step, it does not delete it.** If the global colour difference is
  later closed, the band gets easier and possibly narrower.

## 10. Risks

- Appearance varies more by biome than one correction can follow: right in grassland, wrong
  on a mountain. Mitigated by the multiplicative form and the live knobs; detected only by
  owner testing.
- The band slides visibly as the player walks, because its inner edge is welded to a
  boundary that moves. Mitigated by width smoothing; if it still crawls, that is a knob, not
  a redesign.
- The chosen 8-block step reads as banding rather than terrain. Detected by the debug view
  and the step-size knob before any conclusion is drawn about the correction itself.
- No sink depth both clears the seabed fighting at overlap distance and stays invisible.
  Would reopen the water-ownership options dropped in section 6.

## 11. Acceptance

Owner in-game judgement is the gate; no source-level or offline result substitutes for it.
The owner tests from an established position close to the automated benchmark route's spot.

- **Phase 1 accepted** when the owner cannot locate the land boundary at play distance while
  moving, in at least two biomes, with the frozen knob values recorded here.
- Before/after screenshots from the same position and clock, with the same seam sampling
  applied offline, so the residual step size is reported as a number rather than an
  impression.
- Any performance claim needs a paired benchmark. The band is expected to be nearly free -
  one texture fetch on fragments inside it, plus a bounded field rebuild - and that
  expectation is unproven until measured.
