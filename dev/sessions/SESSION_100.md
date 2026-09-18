# Session 100 — Fog ends, fog corners, drift, and who actually slows terrain loading

**Date:** `2026-09-18`
**Branch/commit:** `codex/water-depth-light`; source checkpoints `3b0bed0` (0.4.109) and `2fbf2d1` (0.4.110)
**Mod version:** `0.4.109–0.4.110`; 0.4.110 built, verified, copy-installed and awaiting its visual check
**Assist protocol / blob / schema:** `1 / 4 / 6`; both silhouette stores remain schema `1`

> Session records are Tier 3 history. This record continues Session 99's seam-fog work, corrects a
> regression that work introduced, and clears Farseer of an unrelated performance complaint.

## Context and investigation

Three separate owner requests, in order:

1. Screenshot `2026-09-17_20-02-06.png`, annotated with a grid over the empty ground beside the
   fog's end: "I want the fog to cover the adjacent terrain blocks at the ends of the existing fog."
2. "How is Farseer prioritized for work loads? When exploring, it feels like terrain is slower to
   load. I know Farseer is set to keep a 4km radius, I'm wondering if every time I move, it's trying
   to generate an entire band of geometry to maintain that 4km radius."
3. Screenshot `2026-09-18_09-25-51.png`: "Can we now smooth over the sharp corners everywhere? The
   fog is pretty sharp at all corners. I'd like the fog to be more nebulous over all." Plus: the Fog
   Drift Speed dial appears to do nothing except while the slider is being dragged.

Screenshots were measured rather than read. The 20-02-06 pixel profile across the two fog edges
showed an abrupt one-sample step, from about 210 to 168 on the minimum colour channel, along a
straight horizontal line and a straight vertical line meeting at a right angle, with one empty
quadrant between them. That is a hard geometric termination, not a fade.

## Work narrative

### 1. Sweep the fog past its ends (0.4.109)

Every strip ran from its own grid vertex to the next and stopped there at full density. Sideways
and vertically the mesh already faded to zero, but along its own edge it did not fade at all, so
wherever the frontier stopped going straight — a ninety-degree turn, or the last known edge of a
chain — the strip ended in a wall and a turn left one spill-by-spill quadrant empty beside it.
0.4.107/0.4.108's flat horizontal cap covered that quadrant at 65% of the bands' density with a
cone falloff, so a step remained across the join and the quadrant carried no vertical sheets.

A strip is now described as a short run of cross-sections along its own edge. A vertex the frontier
continues collinearly keeps full density, because the neighbouring strip carries the fog on; an
unsupported end grows one more cross-section a spill further along at zero density. The corner cap
was removed: two swept ends fill the quadrant between them with real volume.

Linear interpolation over a quad whose only dense corner is one vertex leaves the far triangle flat
at zero, which bit a diagonal out of the swept corner. Quads are now split along the diagonal that
joins their two heavier corners, which puts the dense corner on both triangles. Ordinary strips have
equal weights on both diagonals and keep their original split, so nothing else moved.

Measured on an L-turn fixture, looking straight down, the quadrant beyond the turn fell
monotonically from the crossing centre to zero at the rim with no hole and no step.

### 2. Clear Farseer of the exploring complaint

Source-traced first: the client pumps bounded work per tick (two mesh jobs, two decodes, one
manifest chunk); the server builder holds a 1 ms tick budget, caps previews at 16/s and 4 in flight,
and yields whenever an explicit job holds the generation authority, a shared gate is set, or eight
or more real chunks are pending. `Recentre` fires every 256 blocks of travel and does restart the
spiral scan from the centre, but `attempted` and `TileHasHeights` skip everything already done, so
the genuinely new work is only the leading crescent: about nine 512-block tiles per 256 blocks
travelled at a 4,096-block reach, well under one preview per second even at a sprint.

Then measured, from the owner's own `server-main.log` for 2026-09-17 20:37–20:45:

- 2 lattice probes and 2 previews in eight minutes.
- 5,100 yield ticks in about 470 s: Farseer stood aside on roughly 73% of its ticks.
- 138 tile messages, about 0.3 tiles/s and 2 KB/s.
- "every lattice position within 4,096 blocks of Doden has been requested" — the disc was exhausted,
  with 1,086 tiles already in the store.
- Its "heights from N loaded or previewed columns" counter grew at about 13 columns/s, matching the
  savegame sweep's load rate exactly: those heights are a free by-product, not Farseer's own work.

The sweep is what was loading: 7–14 columns/s continuously, pinned at its capture-backlog ceiling
(28–32 of 32), only 50% through its load pass after seven and a half minutes, with the cache save
backlog 80–390 s old. `ObserveOtherGenerationQueue` records `CurrentGeneratingChunkCount` for
absence classification only; `StepLoad`'s allowance is the rate and the capture backlog and nothing
else, so it keeps issuing `LoadChunkColumnPriority` while the player waits, where Farseer holds at
eight pending. It is also centred on spawn, not the player. No code was changed for this; the owner
asked for it to be recorded for later.

### 3. Round the corners (0.4.110)

0.4.109 was the cause of the "sharp corners everywhere" report. It gave every unsupported end a full
spill-by-spill **square** footprint. On the one isolated turn of the earlier screenshot that filled a
real hole, but the frontier is normally a chunk-grid staircase where every vertex is a turn, so every
strip tapered at both ends and each square corner stood out past its neighbours' own reach. Their
union grew a regular sawtooth.

This was established offline before anything was changed. The real mesh for a staircase seam was
dumped and its plan-view density rasterized: the sawtooth reproduced exactly, and the pure geometry
render showed sharp triangular spikes on both rims.

Two rejected attempts are worth recording, because both look obviously right on paper:

- **A smooth shoulder on the crossing profile alone.** `smoothstep(0) = 0`, so the rim's position
  does not move; only the gradient's shape changes. The rendered outline was indistinguishable.
- **A single broad domain warp.** At 1.6× the billow size its wavelength is far larger than the
  32-block teeth, so it translated the whole sawtooth instead of dissolving it. Four amplitudes were
  rendered; all kept the teeth.

The fault was geometric. A strip's footprint should be the segment dilated by the spill — a stadium,
with rounded ends — and the square corners are what protrude past that. The end is now closed as a
two-cross-section arc, `(0.556, 0.831)` and `(1.0, 0.12)` in along/half-width fractions of the
spill, so the footprint reaches full spill along its own axis and no further on the diagonal.

Making the noise disguise what remains needed the second UV channel, which the mesh had been filling
with zeros. The layer's density and the position across the layer now travel separately, so the
fragment shader can bend the profile into a smooth shoulder and push the rim in and out with a
two-octave world-anchored wobble under a new Fog edge softness dial. The fine octave near the
32-block cell size is the one that dissolves the teeth.

Removing the spikes also removed density: plan-view peak falls 2.71 to 1.50. The core still
saturates, so only the rim is thinner; Fog spread is the dial for that.

### 4. Make the drift dial a rate (0.4.110)

The shader offset its noise by `fogTime * 0.006 * fogDriftSpeed` in **noise** units. At the owner's
100-block billow size that is 0.6 blocks per second — real motion, but invisible at seam distance.
Worse, the offset was elapsed time multiplied by the live speed, so changing the dial displaced the
whole pattern by the elapsed time at once. That jump was the only motion the fog ever showed, which
is exactly what the owner described: drift while dragging the slider, nothing afterwards.

Drift is now integrated on the CPU from the render stage's frame delta, which the fog stage had been
discarding, and uploaded as a world distance in blocks: 6 blocks/s at speed one, each frame's step
clamped so a pause cannot teleport the billows, and the accumulated distance wrapped so a long
session keeps the noise interpolation's precision. Because it is a world distance added before the
billow scaling, the billow-size dial now changes their size and not their pace.

### 5. Verification and artifacts

- Final Release build: zero warnings and zero errors.
- Final fast tier: **25,183 assertions, zero failures**.
- Focused checks cover the swept-end count, the exact vertex/index budget, the rounded arc's reach,
  the measured plan-view fill of the diagonal quadrant, continuity across the old strip ends, the
  integrated drift, and that every uniform the draw uploads is declared by the shader.
- `dist/vintagehorizons_0.4.109.zip`: 666,759 bytes, SHA-256
  `09D19B3E7443870EAC3E10519DFF83DF63B118A6D248014A6CD3A08C76B63FFE`.
- `dist/vintagehorizons_0.4.110.zip`: 668,091 bytes, 43 root-relative entries, one DLL, no PDB or
  wrapper directory, manifest version 0.4.110, SHA-256
  `E3DD28E214B303CF85110065B1E1AB995D58D9B8F75E63AAFAB7ABD434E66584`.
- Both were copy-installed beside 0.4.101–0.4.108; all rollback files were retained.

The agent launched no game. Every runtime screenshot, log and visual verdict came from the owner.

---

## Delivered

- **0.4.109 (`3b0bed0`):** per-edge swept ends in place of the corner cap, and the heavier-diagonal
  quad split that lets a fading end decay over its whole quad.
- **0.4.110 (`2fbf2d1`):** arc-closed swept ends, the separated crossing profile with its smooth
  shoulder and two-octave rim wobble, the new Fog edge softness control, and integrated world-space
  billow drift.
- **`dev/TODO.md`:** the savegame sweep's missing yield, the Farseer measurement that clears it, the
  sweep A/B to run first, and the yield-reason split, all recorded as open work.

## Decisions

- **A strip's footprint is a stadium, not a rectangle.** It reaches one spill from the frontier in
  every direction, including past an unsupported end. This **reverses Session 99's "full
  spill-by-spill square footprint"** decision and G222's remedy: that square corner *is* the sharp
  corner, and on a staircase frontier it is a tooth at every turn. G221's underlying requirement —
  no missing adjacent quadrant at a turn — is still met, now by two overlapping swept ends.
- **Fade along the edge, do not cap the corner.** A cap is a separate body at a different density,
  so its join is visible however large it is. Continuing the strip's own geometry and fading it
  matches by construction.
- **The frontier's own continuity decides.** Only an end no boundary edge continues collinearly is
  swept, so straight runs are untouched and their cost is unchanged.
- **Geometry first, then noise.** Noise disguises an outline; it does not fix one. Two attempts to
  dissolve the sawtooth with shading alone were rendered and rejected before the footprint changed.
- **The drift dial is a rate.** Speed is integrated, never multiplied by elapsed time, and expressed
  as a ground speed so the billow-size dial does not change the pace.
- **Fog edge softness is a player control**, following Session 99's rule that every material fog
  dimension is one. Zero restores the bare geometric edge.
- **Farseer is cleared and the sweep is not changed.** The measurement clears Farseer; the sweep's
  asymmetry is recorded for the owner to prioritise, and the A/B comes before any change to it.

## Traps

- **Trigger:** end a fog strip at its grid vertex with the same density it carries in the middle.
  **Failure:** a hard wall at every turn and every chain end, and one empty quadrant beside a turn.
  **Safer:** fade the strip's own geometry past any end the frontier does not continue.
- **Trigger:** fill that quadrant with a square footprint. **Failure:** on a staircase frontier every
  strip is swept at both ends, and the square corners protrude into a regular sawtooth. **Safer:**
  close the end as an arc so the footprint is the segment dilated by the spill.
- **Trigger:** split a quad along its fixed diagonal when only one corner carries weight.
  **Failure:** the far triangle interpolates flat zero and bites a diagonal out of the fade.
  **Safer:** split along the diagonal joining the two heavier corners.
- **Trigger:** smooth a hard rim by curving the density profile. **Failure:** a curve through zero
  leaves the zero-crossing where it was, so the outline does not move at all. **Safer:** displace the
  profile coordinate before shaping it, or move the geometry.
- **Trigger:** break up a chunk-grid outline with one broad noise octave. **Failure:** a wavelength
  larger than the features slides them instead of dissolving them. **Safer:** include an octave at or
  below the feature size.
- **Trigger:** offset an animated pattern by elapsed time multiplied by a live speed dial.
  **Failure:** every dial change teleports the pattern by the elapsed time, which reads as motion
  while dragging and stillness afterwards. **Safer:** integrate the speed into an accumulated phase.
- **Trigger:** express a drift rate in noise units. **Failure:** the real speed is multiplied by the
  noise scale, so a large billow-size setting can make visible motion invisible. **Safer:** drift in
  world units before the scaling.
- **Trigger:** blame the feature under development for a loading complaint. **Failure:** Farseer was
  doing two previews in eight minutes while another producer loaded 7–14 columns/s. **Safer:** read
  the periodic status lines of every producer in the same log window before attributing.

## Flagged and unverified

Judgement calls awaiting human review:

- Whether 0.4.110's staircase outline now reads as nebulous rather than toothed, and whether the
  thinner rim that came with removing the spikes is acceptable or wants more Fog spread.
- Whether the Fog edge softness default of 50% is right.
- Whether the owner's Fog coverage of 100%, which leaves the billow noise unable to open any gap,
  is the intended look now that the outline is irregular.

Claims lacking required evidence:

- Neither 0.4.109 nor 0.4.110 has run in game. Both are source-traced and harness-tested, and the
  before/after outline is computed offline from the real mesh, not observed.
- The drift fix is unobserved. Its rate was chosen from arithmetic, not from a visual judgement.
- The fog's GPU/blending cost is still unmeasured, and the arc's extra cross-section per swept end
  has not been counted against a real frontier.
- The savegame sweep's effect on player-visible chunk latency is source-traced only; the A/B has not
  been run.
- The 73% yield figure is not broken down by reason, so whether Farseer held for the engine's pending
  chunks or for Vintage Horizons' own save backlog is unknown.
- All Farseer load measurements are of already-covered terrain near the owner's base. Riding into
  genuinely unexplored land is unmeasured.
- The earlier rapid replacement, excavation, weather/night/winter, cold-restart/full-disc,
  dedicated-player and mixed-version Farseer matrices remain incomplete.
