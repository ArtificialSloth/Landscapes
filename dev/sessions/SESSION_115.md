# Session 115 — The cliff curtain: fog that pours down the cached terrain's wall without Farseer

**Date:** `2026-10-18`
**Branch/commit:** `main`, checkpoints `dde1c9d` (0.4.149) to `295c22c` (0.4.156); nothing pushed
**Mod version:** `0.4.156`
**Assist protocol / blob / schema:** `1 / 4 / 6` (unchanged)

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The owner looked at the fog without Farseer (0.4.146, decision 57) in his own world for the first
time. His screenshot showed the cached terrain ending in a sheer grey wall, from the lip down to the
foot of its columns, with the fog covering only its top. He asked for the fog to cover the whole wall
and to spill over the edge. Eight builds followed, each judged by him in game; the offline fog
mock-up (`scripts/fog-mockup`) was extended to judge each one first. Out of scope: Farseer's own fog,
which is unchanged in every build (the mock-up's Farseer render is byte-identical to 0.4.148's).

## Work narrative

### 1. Why the wall showed, and the curtain (0.4.149)

Every captured column runs down to y = 1 (`LodWorker`'s capture loop) and the mesher walls off a
missing neighbour from the column's top to its foot, so beyond the cached edge stands a wall about 100
blocks tall in empty sky. The fog was built for Farseer's seam, a ground-level edge: its floor reached
20 blocks under the ground (`fogFloorDepthBlocks`), so it covered only the wall's top.

0.4.149 adds a curtain to the fog shader (`lodsilhouettefogvolume.fsh`, section 7 of `vhFogShape`),
active only for a field built from the cached terrain's edge (`QueueSilhouetteFogVolume(..., curtain:
true)` from `TerrainFog.cs`, uniform `fogCurtainActive`): over the band the staircase strays across and
out past it, from the lip (`fogCurtainFloorY` = `TerrainFogWallFootY` = 1 is the foot), with a lip
bulge, outward tongues and tall tendrils whose pattern slid down at `SilhouetteFogFallBlocksPerSecond`
times the drift dial. The march reaches down to the foot beside the wall (`vhFogFloorBelow`) and a
skip may not carry a ray sideways into the curtain (`vhFogCurtainGap`). A dark band under the lip was
the dense strip's shading below ground; the strip there is lit like the curtain (`sunk`).

The mock-up gained `--cliff`: the same staircase with empty sky past it, a cell-walking intersector
for the wall, and a "wall shows" count. 0.4.149's curtain cut the wall showing from 42-71% (0.4.148)
to 1% average in every view.

### 2. The animation, through four builds (0.4.150-0.4.153, 0.4.156)

- **0.4.149, sideways:** the owner saw the curtain move sideways. A new Lucas-Kanade motion measure in
  the mock-up confirmed 8 px a second right and 0 down: the tendrils rode the wind (6 blocks a second)
  and were stretched four times along their fall, so the fall slid each streak along its own length
  and never showed (G281).
- **0.4.150, flow map:** two cross-faded copies of the tendrils slid along a flow out over the lip and
  down. The owner: falling now, but the top too fast, a line, pulsing, and a lip like a fog cliff edge.
  The copies restarted every 3 s everywhere at once, and tore where the flow turned within 10 blocks
  (G282). (The line he saw later proved to be the vanilla/LOD handover seen through the fog, which he
  accepts.)
- **0.4.151, a path in the edge's frame:** the tendrils scroll without restarting along a path
  coordinate that runs outward over the band at a third of the fall speed (`CurtainTopSlowdown`),
  turns through a soft minimum (`CurtainTurnBlocks`), then falls; the outward direction is a central
  difference a texel either side of the field. A first version measured place along the edge from the
  nearest edge point everywhere; deep in the curtain that averaged a run of tendrils along each ray into
  a smear. Pulsing measured 1% against 6%. The bank's floor (and the curtain's top) curve down past the
  band as a parabola (`vhFogDroop`, radius 12), so the fog rolls over the lip.
- **0.4.153, striations and corner smears:** the owner saw fine dashes and smeared fans. The dashes were
  march strides of 8+ blocks crossing empty air and landing inside the dense curtain at a jittered
  depth (G283); strides near the curtain are capped to about two half-resolution pixels. The fans were
  the nearest-edge-point coordinate: below a convex corner a whole arc shares one point; it now applies
  only over the band (`along = xz - outward * acrossTop`) (G282). The dense strip had been bent down
  with the bank into a thin opaque "plastic" sheet; beside empty sky it now ends past the band.
- **0.4.156, wallpaper:** the owner said the holes held their shape as the curtain fell. An unseen eddy
  field (1.5 times the streams' size, falling at a fifth of their speed) bends both stream octaves by up
  to 6 blocks either way and 9 vertically. A first try, bending by the visible fine octave, barely
  changed anything. The mock-up's direct measure on the tendril pattern down the wall's face: a second
  later the best slide correlates 0.86, against 1.00 for 0.4.155 (0.99 for the first try). An earlier
  measure on rendered frames was dominated by the static wall and sky and could not tell them apart.

### 3. The bottom: tendrils that fade (0.4.153)

The curtain reaches 48 blocks below the foot (`CurtainBottomFadeBlocks`) and thins to the sheet off the
wall's face. Thresholding the flowing pattern opened holes rising into the curtain; instead each
6-block column hangs on to its own length, a quarter to the whole of the dissolve, tapering, with the
lengths drifting as the fall goes on (`n.drip`). A mock-up view of the foot was added.

### 4. Fog colour under storms and in caves (0.4.152)

The owner noticed all the fog darkening with his position. Decompiled `AmbientManager.UpdateAmbient`
(1.22.7) blends the fog colour from ordered modifiers and darkens it by scene and fog brightness; the
weather system adds `weather`, `desertstorm` and `lightningambient`, and `blackfogincaves` pulls the fog
to black when little sunlight reaches the player or they are below sea level (G284). The owner chose to
follow the time of day whole and take only part of the local darkening. `LodSteadyFogColour` repeats
the game's blend with those four at 30% of their weight; time of day, the world's saved ambient and
water or lava over the eyes count whole. Only the seam fog uses it. Owner: "much better".

### 5. Cost (0.4.154-0.4.156)

The owner measured, on one scene: with 0.4.153 fog without Farseer 370 FPS against 390 with Farseer;
with 0.4.155 Farseer on 410, fog only 390, fog off 490. The fog costs about 0.52 ms a frame. The
mock-up cannot time a GPU, so it counts march steps, lit samples and the curtain's extra lookups per
ray over the cliff views:

| | steps | samples | extra texture reads (approx.) |
|---|---|---|---|
| 0.4.148, no curtain | 30.7 | 23.6 | 0 |
| 0.4.153 | 46.9 | 36.0 | about 256 |
| 0.4.154 | 37.4 | 26.4 | about 97 |
| 0.4.155-0.4.156 | 37.6 | 24.7 | about 80, and 6.1 samples skip the bank's noise |

- **0.4.154:** tendrils looked up only beside the wall (they had been looked up within 20 blocks of the
  ground everywhere); pixel-deep strides only near the box the dense curtain can fill, a stride
  elsewhere covering most of the way to it; the outward direction's four reads skipped more than 18
  blocks past the band; steps through half-hidden curtain may cross optical depth 1.
- **0.4.155, the veil (owner: less coverage is fine and more realistic):** density 0.12 a block times
  0.35 + 1.3 x streak squared, dense in the streams and nearly clear between; a sheet from 6 blocks
  behind the outermost stretch of wall instead of the whole band; shorter tongues and fringe; mist
  gathering near the foot (up to 3x within 16 blocks); beside the wall the strip ends 4 blocks under
  the lip, since hanging down the face it drew a pale band through the veil. Where only the curtain can
  be (below the bank's floor, `vhFogBankless`) samples skip the bank's and strip's noise, and steps
  cross optical depth 0.6. Wall shows 3.6-5.5% on average, up to 27-42% between streams. Owner:
  coverage right.
- The owner's numbers did not move with the curtain cuts (the gap stayed about 20 FPS), so most of the
  0.52 ms is the fog's general machinery, not the curtain.
- **0.4.156, main thread:** without Farseer the outline worked out 16 tiles' coverage every frame, over
  6,000 tile walks a second at 400 FPS, gathered the disc and re-signed every tile each frame. Now 960
  tiles a second (`TerrainFogCoverageRefreshesPerSecond`; never-seen tiles still 16 a frame at once),
  the disc is gathered after 8 blocks of camera movement, and the outline is signed only when a tile's
  coverage or the tile set changed. `.dlfog stats` reports averaged main-thread microseconds for
  Farseer's and the fog's preparation and for issuing the fog pass.
- **Not obtained:** the owner's three `.dlfog stats` lines in his 0.4.156 session were each printed the
  instant he typed `.dlfog on`; the averages still held the fog-off period (no field, 0 microseconds)
  (G285). The CPU share of the 0.52 ms is unknown.
- **Seen in the same log:** while he travelled (19:43-19:45), 9-10% of the main thread's 40-46 MB/s was
  `ValueTuple<PlanPoint, ...>[]` - the fog outline's path tracing, which `QueueSilhouetteFogVolume`
  runs on the main thread (`LodSilhouetteFogVolume.Prepare`) for every rebuild.

### 6. Checks and tools

`TerrainFogChecks` gains the curtain (veil depth in front of the outermost wall at its thinnest and in
a stream, tendrils, rock and foot ends, wiring), the steady colour, and the outline pacing; the
shader/mirror parity list carries every curtain constant. The mock-up gains `--cliff`, views of the lip
side-on and of the foot, the cost counters, the motion and pulsing measures, and the tendril morphing
measure. 26,320 to 26,355 assertions, 0 failures.

---

## Delivered

- **0.4.149** (`dde1c9d`): the curtain; mock-up `--cliff`. Owner: coverage good, motion sideways.
- **0.4.150** (`6ac97ef`): flow-map curtain, no wind. Owner: falling, but fast top, pulsing, cliff lip.
- **0.4.151** (`1b91990`): scrolling edge-frame path, rolling lip.
- **0.4.152** (`bc5016b`): `LodSteadyFogColour`; with 0.4.151 played: colour "much better", top better.
- **0.4.153** (`857d24d`): stride cap, place-along-edge only over the band, strip ends past the band,
  48-block tendril dissolve. Owner: costs more than Farseer.
- **0.4.154** (`39ea1a3`): curtain cost cuts.
- **0.4.155** (`49d7ce5`): the veil and the bankless fast path. Owner: coverage right; holes slide
  unchanged.
- **0.4.156** (`295c22c`): eddy bend; outline paced by time; main-thread timings in `.dlfog stats`.
  Played by the owner; his verdict on the session: "Good enough."
- Tests: 26,355 assertions, 0 failures. Mock-up extended (above).

## Decisions

- With Farseer off the fog covers the cached terrain's wall from lip to foot, as a veil: the wall shows
  dimly between streams, never hidden whole (owner: less coverage looks more real). Settled decision 58.
- The curtain never rides the wind; it crosses the top slowly, falls, changes shape as it falls, and
  ends in fading tendrils 48 blocks below the foot. Farseer's fog is unchanged.
- The seam fog follows the time of day whole and takes player-local weather and cave darkening at 30%
  (owner's choice among the offered options). Settled decision 59; the terrain haze keeps the game's
  colour.
- Rejected: two cross-faded flow-map copies (pulse and tear); the nearest edge point as place along the
  edge everywhere (fans at corners, smears through the thickness); bending the dense strip down (plastic
  sheet); thresholding the flowing pattern for tendrils (rising holes); lighting mist under the foot as
  unshadowed (the floating terrain shades it).

## Traps

- G281: a noise pattern stretched along its own motion hides that motion, and one riding the wind reads
  as sideways.
- G282: a cross-faded flow map pulses and tears where the flow turns; the nearest-edge-point coordinate
  fans out below convex corners and smears through a thick medium.
- G283: a long march stride into a dense medium lands at a jittered depth and draws dashes; bound the
  stride by the distance to the box the medium can fill.
- G284: the game's fog colour carries player-local modifiers; a horizon bank shows them.
- G285: `.dlfog on` prints its stats at once, with averages from the fog-off period.
- GLSL: `out` is reserved (a `float out` will not compile), and `smoothstep` with edge0 > edge1 is
  undefined; there is still no GLSL compiler on this machine, so every shader change was read through.
- A Python heredoc in this shell turns `\n` in a string into a real newline; edit with the Edit tool or
  build strings with `chr(10)`.
- Comparing renders: a rigid-slide correlation on rendered frames is dominated by the static wall and
  sky behind a veil; measure the pattern itself.

## Flagged and unverified

- Judgement calls awaiting the owner: the release number (0.4.156 is now the newest played build);
  the game-launching check tiers; pushing the fourteen local commits since `1bea2c1`; the ModDB
  description; whether the curtain wants a player dial (its density and look are fixed constants).
- Unverified: where the fog's 0.52 ms goes - the main-thread share was not obtained (G285), and the
  0.4.156 pacing's effect on frame rate is unmeasured; the mock-up's cost counts are proxies, not
  timings. In the mock-up the wall's stepped outline and flat foot show faintly through the veil, and
  the strands below the foot are darker than the sky when the sun is behind the cliff; in game the wall
  is light grey, and the owner has not commented on either. The curtain at the largest far distances
  and on a slower machine is untested.
