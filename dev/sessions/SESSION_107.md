# Session 107 — A natural seam fog bank, then one that follows the terrain

**Date:** `2026-09-21`
**Branch/commit:** `codex/water-depth-light`, 0.4.123 at `b46cf0a`, 0.4.124 at `1fb10f5`
**Mod version:** `0.4.123–0.4.124`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Source and checks remain authoritative; the owner's in-game
> observations are the acceptance evidence for appearance.

## Context and investigation

The owner asked for an investigation only, with no changes, of two screenshots.
`2026-09-21_00-32-42.png` was 0.4.121: the fog shape was good but did not hide the seam.
`2026-09-21_09-44-39.png` was 0.4.122 and, in his words, looked terrible: too bright, too dense and
very unnatural. He wanted a fog that is very dense at the seam, lightens as it moves away, and is
feathery and wispy at its edges.

Both sessions had loaded the same saved dials: density 125%, coverage 90%, spread 40, height 32,
floor 26, billow 32, edge softness 20% and quality 75%. They had been raised to push 0.4.121 and
were compensations rather than a taste.

### 1. Why each build failed

Both were source-traced, then reproduced by an offline C# port of both shaders over a synthetic
32-block staircase seam. The first mock-up matched both screenshots closely.

- **0.4.121 was too thin where it mattered.** All opacity came from billows times a crossing
  profile. At the saved dials the peak extinction was about 0.033 per block through a 32-block
  layer, so the vertical optical depth over the seam was about 0.3–1.4. Between a quarter and three
  quarters of the colour step showed through, and the mock-up averaged 55%. The LOD-side plateau
  also ended about 12 blocks out, while the real staircase strays about 20 blocks from the rounded
  line.
- **0.4.122 added a second body instead of fixing the first.**
  - The seal was noise-free, full height and flat-topped, and normalized to a sideways half-crossing
    of the LOD side. At the saved dials it was 0.376 per block, 10–30 times the bank's density.
  - Viewed from above, its column reached an optical depth of about 9–20. That fully opaque region
    shows no inner texture, and its edge fell from solid to clear in 6–8 blocks, a crisp outline at
    seam distance.
  - Its colour was the unlit horizon fog colour, up to 3.5% brighter again, and brighter than snow in
    the same scene.
  - At default dials it was still a wall, so the dials were not the cause.
  - In the mock-up, staircase corners beyond its 27-block reach still leaked up to 80–88%.

### 2. The proposal

The proposal was to design by how see-through the fog column is, not by raw density:

- just opaque over a measured core, then an exponential falloff outward;
- coverage erosion, so the rim breaks into strands instead of the whole pattern dimming;
- wind-stretched, swirled, thread-like detail;
- a dense ground layer under a lumpy top, and a faint haze skirt;
- real sunlight, capped below the horizon colour.

The mock-up was iterated through five versions in the scratchpad. A haze skirt at 0.5 optical depth
milked out the whole foreground and was halved. The sun probe was kept because without it the bank
went flat grey. The owner approved the redesign, a one-time dial reset, and keeping the mock-up.

He also corrected an assumption. The seam and fog are seen from every angle in ordinary play, not
mainly from above. Instead of a GPU timing harness, he wanted an on/off switch so he could judge the
cost himself.

## Work narrative

### 3. 0.4.123: the natural bank

- **Measured core.** When the field is built, it now measures how far the real staircase strays
  from the rounded centreline, at every edge vertex and midpoint outside the faded ends. The core
  half-width is that distance plus a margin, clamped to 12–48 blocks.
- **Seal.** The seal is exactly 3.5 optical depth through the vertical column over the core, whatever
  the bank's height. The bank's mean column is 3.2 and the haze 0.25; each is normalized by its own
  numerically integrated column fraction (0.5923 and 0.5548 of the bank top).
- **Shape.** Beyond the core the bank fades exponentially, 1.6 times further over Farseer, and
  is cut to nothing past one and a half shoulders. The top is lower toward the rim and undulates.
  Density is full through a ground layer of 0.3 of the top, then thins with a scale height of 0.42.
- **Noise.** It comes from a baked 64³ RGBA8 tiling texture: 16 lattice cells per repeat and four
  independent channels, with mipmaps. It is sampled with an explicit LOD from the pixel footprint,
  and sub-pixel coverage is replaced by its analytic mean, so the fringe fades to a veil at distance
  instead of vanishing.
- **Lighting.** One cheap probe 14 blocks toward the sun, a Beer term plus a softer lobe standing in
  for light scattered many times, sky occlusion by height, and a mild forward-scatter phase. Shade
  runs from 0.64 to 0.93 of the fog colour, cooler in shadow.
- **Switch.** A saved Seam fog switch on the Farseer tab and a session `.vhfog` toggle. Off draws
  no pass, builds no field, and does not hold Farseer back waiting for fog.
- **Dial reset.** `SilhouetteFogModel` marks what the saved dials were tuned for. An older file
  had its fog dials reset once, with a log line, and an unreadable file is never overwritten.

The first mock-up of the shipping shader showed worst-pixel leaks of 13–17%. An angled ray reaching
the core's edge crosses mostly the thinner shoulder outside it, so the "vertical column is the
shortest path" argument fails there (G247). Two changes brought every tested angle under 4%: a margin
of 10 blocks instead of 6, and a seal falloff of 0.3 of the spread instead of 0.12. The seal's
fade then takes on the bank's billows, so the seam-side edge is lobed rather than a clean offset.

### 4. The owner's verdict on 0.4.123

`2026-09-21_16-50-02.png` and `16-51-46.png` report the result:

- "The fog shape looks fantastic."
- At 35% density the staircase still showed from above; about 60% covered it well.
- The ribbon deformed as the camera turned.
- Performance in the same scene was 400 FPS with fog on and 550 with it off, about 0.68 ms per
  frame at 1440p.

### 5. 0.4.124: two densities, a camera-independent outline, half resolution

- **Two densities.** Fog seam density covers the seal and the bank over the core, then blends out
  across the shoulder to Fog outer density along the wandering rim, so the blend is lobed rather
  than a band. `FogModel` 3 resets only the densities of a model-2 file, and his other dials were
  kept. The defaults became his tuning: seam 60%, outer 35%, coverage 100%, spread 40, height 24,
  floor 16, billow 12, drift 1, edge softness 50% and quality 52%.
- **Camera cause.** The outline was traced only from Farseer tiles in the view frustum. Turning
  changed that set, so the seam was cut at different places, and the whole-path simplification
  re-fitted the centreline (G248). Tiles entering at the reach edge while travelling could bend the
  curve near the player for the same reason.
- **Camera fix.** Every in-reach Farseer tile now caches its fog coverage. Visible tiles refresh as
  they are drawn, and off-screen tiles refresh 8 per frame, each at most once per 30 frames. The
  centreline is a biweight moving average of the staircase over a fixed window (1.5 times the
  spread, 48–144 blocks), corner-cut once. The field's texel size follows the reach, not the
  momentary window. A new check extends a frontier more than 300 blocks away and confirms the field
  near the start does not move.
- **Performance.**
  - The march runs at half resolution. Each pixel takes the farthest texel of its 2×2 block on even
    checkerboard squares and the nearest on odd ones, and records its ray length in an R32F target.
  - A new upsample shader rebuilds full resolution. It weights the four neighbours by position and
    by how closely their ray lengths match the pixel's surface, and composites premultiplied fog.
  - Octaves smaller than a pixel are skipped.
  - Faint steps reuse the last lighting instead of computing their own; skipping their colour
    outright would have darkened thin haze.
  - The field is converted to half floats on the worker before upload.
- **Long rays along the seam.** The mock-up found a pixel 2,835 blocks along the seam showing 50%.
  A skimming ray used up 192 steps of about 4 blocks before reaching far fog (G249); 0.4.122's
  160 × 3.6-block budget was worse. Thin fog now takes steps of up to 2% of the distance. Each step
  is sampled at its midpoint, because a longer step sampled at its start undercounted fog thickening
  toward the ground: straight down rose from 9% to 12% until the midpoint sample brought it to 6.5%.

With the new defaults the mock-up reads 1–2% average show-through and at most 5% at the worst pixel
from above, at a distance, at ground level, from the Farseer side and along the seam. Straight down
reads 6.5% average and 12.5% worst, the cost of 60% seam density.

### 6. The owner's 0.4.124 session

The owner played 0.4.124 from 17:25. The client log shows:

- the one-time density notice;
- `lodsilhouettefogvolume` and `lodsilhouettefogupsample` loaded, and "the lodsilhouette shader
  compiled; horizon silhouettes can draw";
- no seam-fog stop and no fog GL error while the session was still running.

He said "getting better" and deferred more refinements to the next session. His saved dials at
17:37 were:

| Dial | Value |
|---|---|
| Fog seam density | 60% |
| Fog outer density | 60% |
| Fog coverage | 100% |
| Fog spread | 48 |
| Fog height | 32 |
| Fog floor depth | 20 |
| Fog billow size | 12 |
| Fog drift speed | 1 |
| Fog edge softness | 35% |
| Fog quality | 52% |

No specific verdict on the camera fix, performance or upsample edges was reported.

---

## Delivered

- **0.4.123:** the natural-bank model and the measured core described above; the baked noise
  texture; sunlight; the Seam fog switch and `.vhfog`; the model-versioned one-time dial reset.
  - Verified, copy-installed and owner-played.
  - Artifact: 697,966 bytes, SHA-256 `BF473EEA3BAADB91490CE7FF268CB4720FAD28CFE1A331690AC5DE9FD7B59696`.
  - Checkpoint `b46cf0a`.
- **0.4.124:** seam and outer densities with the owner's tuning as defaults; the fog outline built
  from every in-reach tile, with local smoothing and a reach-fixed texel size; the half-resolution
  march with a depth-aware upsample; octave and lighting skips; half-float upload; distance-grown
  midpoint steps.
  - Verified, copy-installed and owner-played; runtime shader compile production-observed.
  - Artifact: 701,843 bytes, 45 entries, SHA-256
    `8A15E534A49CDA2DCAD07C1A628B4EBA97C2A8823B206E2A165348A505862C7C`.
  - Checkpoint `1fb10f5`.
- `scripts/fog-mockup/`: an offline mock-up that renders six viewpoints through a line-for-line C#
  port of the shipping fog shader.
  - It reads the shader's constants and the dial defaults from source, and samples the mod's own
    baked noise texture.
  - It reports average and worst seam show-through, with the worst pixel's location.
  - It is left uncommitted pending the owner's go-ahead.
- Final checks: 25,418 assertions, zero failures; Release builds warning/error free. No game was
  launched by the agent.

## Decisions

- **Design by column optical depth, then shade it.** Rejected: a stronger seal (the 0.4.122
  form), more noise, or a global density increase. Each of them either paints a wall or leaves
  pockets.
- **The seal is sized like the bank.** It fills the bank's holes over the measured core and fades
  under the shoulder. It is never a denser second body.
- **The core width is measured, not guessed.** It is the staircase's stray from the rounded line plus
  a 10-block margin, so angled views of its edge are covered.
- **Reset dials when their meaning changes, and only those dials.** Model 1 files reset every fog
  dial; model 2 files reset only the densities.
- **The owner's tuning is the default.** It hid the seam better than the first defaults at the same
  densities, as with the anti-aliasing precedent.
- **The outline is a function of the terrain.** It must not depend on the frustum or on how far the
  frontier extends beyond the local window. This is the fog-side counterpart of settled decision 45.
- **Half resolution plus a depth-aware upsample** was chosen over fewer steps or a cheaper look. The
  fog is low-frequency, and a checkerboard of farthest and nearest rays gives both sides of a depth
  edge a matching sample.
- **No GPU timing harness.** The owner judges cost with `.vhfog`, per his instruction.

## Traps

**A seal normalized to one path length paints a wall (G246).**
- Trigger: a seam seal normalized for a sideways half-crossing, viewed down its column.
- Failure: optical depth 9–20, an opaque textureless ribbon with a crisp edge, in the raw horizon
  colour.
- Safer action: target "just opaque", at the bank's own scale, fade it exponentially and light it.

**The vertical column is not the shortest path at the core's edge (G247).**
- Trigger: proving coverage from the column above the core.
- Failure: an angled ray reaching the edge crosses mostly the thinner shoulder, and 13–17% leaked.
- Safer action: add a margin and a gentle seal falloff, then measure every angle in the mock-up.

**A fog outline derived from the frustum's tiles moves with the camera (G248).**
- Trigger: building the seam field from the visible draw list.
- Failure: turning changes the tile set, and the whole-path simplification re-fits the curve.
- Safer action: build from every in-reach tile with cached coverage, and smooth locally.

**A fixed step budget does not reach far fog along the seam (G249).**
- Trigger: rays skimming along the bank.
- Failure: 192 short steps run out, leaving far seam unfogged at the horizon.
- Safer action: let steps in thin fog grow with distance, and sample each at its midpoint.

**The engine's uniform collector ignores `sampler3D` (G250).**
- Trigger: `p.Uniform("fogNoise", ...)` on a `sampler3D`.
- Failure: the collector's regex has no `sampler3D`, so the name is missing and the call would
  throw.
- Safer action: resolve the location with `GL.GetUniformLocation` per `ProgramId` and set it
  directly.

**Skipping a faint step's lighting must not skip its colour.**
- Trigger: counting a faint step's opacity while skipping its lighting.
- Failure: the step adds opacity with no colour and darkens thin haze.
- Safer action: reuse the last lit colour. Caught in review before any build.

## Flagged and unverified

Judgement calls awaiting human review:

- whether seam 60% / outer 35% is the right default split, given that he saved 60/60 after playing;
- whether straight-down views at 60% seam density still show a trace of the steps;
- whether the ribbon is now steady while turning in place and while travelling, apart from its drift;
- whether the half-resolution upsample leaves halos or blocky edges around trees and ridges in front
  of the fog;
- whether field rebuilds, now less frequent but covering the whole reach, cause a hitch while
  travelling.

Claims that lack the evidence level required to treat them as established:

- 0.4.124's performance gain is unmeasured. The expectation of a quarter to a third of the cost is
  an estimate, and the owner's `.vhfog` A/B is the measurement.
- All coverage percentages are from the offline mock-up (flat ground, fixed sun, display colours,
  analytic field), not the game.
- The 0.4.123 seam leak from above at 35% is human-observed; that 60% covers the seam is his
  judgement from one viewpoint.
- G250 is source-traced from the decompiled 1.22.7 engine. The raw GL binding it prescribes is
  production-observed only in the sense that 0.4.124 compiled and ran without a logged fog failure.
