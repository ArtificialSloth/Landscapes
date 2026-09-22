# Handoff — Farseer at 0.4.125, seam fog in a good place

**Updated:** 2026-09-21, Session 108
**Branch/source checkpoint:** `codex/water-depth-light`, 0.4.125 at `92eb9c3`; fog defaults changed
after it in source; nothing pushed
**Installed:** verified 0.4.125 private test build, copy-installed beside rollback builds; owner-played
**Compatibility:** assist protocol 1, section blob 4, main database schema 6, silhouette stores 1

## Read in this order

1. `CLAUDE.md`;
2. [Session 108](../sessions/SESSION_108.md), then [Session 107](../sessions/SESSION_107.md);
3. this handoff;
4. [the Farseer/Terrain Ahead plan](PLAN_HORIZON_SILHOUETTES_AND_FRONTIER.md), especially P4-P6;
5. `dev/GOTCHAS.md` G199-G222 and G244-G254.

## Current state

- P1-P4 and most P5 persistence/tuning work are built through 0.4.125.
- Farseer is opaque fallback terrain derived from server height tiles. Exact stable 32-block
  installed-terrain coverage decides where it yields; unknown coverage keeps the fallback.
- The near Farseer edge alone slopes into an adjustable buried apron. The distant field keeps its
  representative height. Slope/depth contrast is world-directed and camera-yaw independent.
- The seam fog is a shallow strip that hides the seam under a see-through bank, with an outline
  built from the terrain, and fog left behind by a moved seam dissolves. Of 0.4.125 the owner said
  "Everything looked good."
- He has since reported hitches with low minimum FPS while exploring new terrain, cause unknown,
  noted in TODO for future performance work. Ask him what comes next before choosing work.

## The current fog design

### Outline and CPU field

- `LodSilhouetteSeamMask` derives one unique directed boundary from the stable current replacement
  answer. Farseer is on the right-hand/positive-distance side. Missing neighbour tiles are not
  invented seams.
- The mask is built from **every Farseer tile within reach**, not only those in view (G248).
  - Each `SilhouetteGpuMesh` caches its fog replacement coverage and the frame it was worked out.
  - Visible tiles refresh as they draw; off-screen tiles refresh 8 per frame, each at most once per
    30 frames.
  - A tile joins the outline once its coverage is known.
  - With the fog switched off, the mask is not built.
- Maximal paths become a centreline by a biweight moving average of the staircase over a fixed arc
  window (1.5 times Fog spread, 48-144 blocks), corner-cut once and resampled. Every point depends
  only on the staircase near it. Only true open ends receive a swept longitudinal fade.
- An owning-thread prepare phase copies all live seam heights. One worker then:
  - rasterizes the path samples and runs a separable squared Euclidean distance transform;
  - measures how far the real staircase strays from the centreline (`StaircaseDeviationBlocks`);
  - converts the field to half floats;
  - builds the residual (below).
- The `RGBA16F` field carries effective signed distance, nearest seam-ground height, unsigned
  distance for conservative skips, and open-end fade.
  - Its texel size comes from the reach (`TexelBlocksForReach`): eight blocks at a 4,096-block
    reach, about 1,200 texels square, and never a flip with the momentary window.
  - It is bounded at 2,048x2,048, and padded to hold the bank's fade or the strip's fringe.
- Jobs are revisioned; stale results cannot publish; all GL work stays on the render thread.
  Farseer waits for the first field unless the fog is switched off.

### The medium (`lodsilhouettefogvolume.fsh`)

- **The seam strip hides the seam.**
  - **Column.** 3.5 optical depth at 100% Seam strip density, whatever its depth. Its profile is
    full to 0.55 of its local top and gone by 1.35 (column fraction 0.95).
  - **Band.** The measured stray plus 6 blocks (`StripHalfWidth`, 8-44). Noise varies its density
    by a quarter either way but cannot open it there.
  - **Edge.** It wanders by up to 6 blocks inside the margin. Past the band it frays out over
    12 blocks, lobed and lower, and ends at 3.5 fall-offs.
  - **Footing.** It rests on the surface the ray lands on while the sample is within 24 blocks of
    the landing point, blending back to the sampled seam height by 64, and never more than 16 blocks
    from it (G253). Sky rays use the sampled height.
  - **Steps.** Over its reach, a descending step may reach at most just inside its highest top, then
    a third of its depth at a time (G252).
- **The bank is scenery.**
  - **Density.** Quoted along a level crossing through its ground layer: 1.0, with the haze at 0.15,
    over twice the core plus both shoulders (G251).
  - **Core.** The measured stray plus 10 blocks, clamped to 12-48, inside which the bank does not
    thin.
  - **Shape.** 0.4.123's: it fades exponentially beyond the core, 1.6 times further over Farseer, and
    is cut at one and a half shoulders. It is tallest over the seam with a lumpy top, full through a
    ground layer of 0.3 of the top, then thins with a scale height of 0.42.
  - **Texture.** Coverage erosion turns the rim into swirled, wind-stretched strands frayed by
    ridged detail.
- **Noise.** A baked 64³ RGBA8 tiling texture (`LodSilhouetteFogNoise`) with mipmaps, sampled at an
  explicit LOD from the pixel footprint. Sub-pixel coverage uses its analytic mean. All lookups at a
  point are made once (`vhFogSampleNoise`) and shared by the current field and the residual.
- **Light.** One probe 14 blocks toward the sun, a Beer term plus a softer multiple-scatter lobe,
  and a mild forward-scatter phase.
  - The strip is shaded only by the strip above the point (`stripAbove`).
  - Its sky view counts as about halfway up the bank, higher on billow crests.
  - Shade runs from 0.64 to 0.93 of the fog colour, with a cool shadow tint. Faint steps reuse the
    last lit colour.
- **March.** At most 192 steps.
  - Steps stay optically thin in dense fog and grow to 2% of the distance in thin fog.
  - Each step is sampled at its middle (G249).
  - Empty space is skipped with the unsigned distance of the field, or of the residual where it
    still has weight.

### The dissolve (settled decision 51)

- **Snapshot.** When a build starts, the render thread snapshots the shown field's half floats and
  lattice (`LodSilhouetteFogLayer`) and the current residual if it still has weight.
- **Worker.** `LodSilhouetteFogVolume.BuildResidual` carries both onto the new field's lattice.
  - Each texel keeps whichever candidate holds more fog there.
  - Fog shown until now starts at its open-end fade plus a head start, the smoothed build time over
    the fade time.
  - An earlier residual carries on with what it had left.
  - Texels where the new field is unchanged get zero weight.
- **Shader.** The residual is an `RGBA16F` texture on unit 3 (`fogResidual`, restored after the
  draw).
  - The shader subtracts the fade durations elapsed since the build began (`fogResidualElapsed`).
  - The weight scales the old bank's envelope and, squared, its strip.
  - The thicker of old and new shows.
  - Once its largest weight has elapsed the texture is freed.
- **New fog appears at once.** A fade-in was offered and not built. Fields on different lattices,
  which only a reach change produces, do not blend.

### Two passes and composition

- Pass 1 marches at half resolution into its own framebuffer: `RGBA16F` premultiplied fog plus
  `R32F` ray length. Each pixel uses the farthest texel of its 2x2 block on even checkerboard
  squares and the nearest on odd ones. Blending is off.
- Pass 2, `lodsilhouettefogupsample.fsh`, rebuilds full resolution. It weights the four neighbours
  bilinearly and by how closely their ray lengths match the pixel's surface (tolerance: 3% of
  distance plus one block), falling back to the closest match.
- The composite target owns only the primary `ColorAttachment0`. It writes RGB only with explicit
  premultiplied blending (never the SSAO-aware engine helper), preserving primary alpha/depth.
  Both passes run at `AfterOIT` order 1.0, after the water capture at 0.99.
- The noise `sampler3D` is bound with raw GL, because the engine's uniform collector does not know
  that type (G250).
- A shader/draw/GL failure disables Farseer for the session rather than exposing a plausible
  no-fog handoff.

## Player controls

The Farseer tab has a saved **Seam fog** switch; `.vhfog` toggles it for the session. It also has
twelve fog dials. The source defaults are the owner's 0.4.125 tuning:

| Dial | Default |
|---|---|
| Seam strip density | 100% |
| Seam strip height | 3 blocks |
| Fog density | 100% |
| Fog coverage | 100% |
| Fog spread | 36 blocks |
| Fog height | 24 blocks |
| Fog floor depth | 20 blocks |
| Fog billow size | 12 blocks |
| Fog drift speed | 100% |
| Fog fade time | 0.8 s |
| Fog quality | 52% |
| Fog edge softness | 35% |

0.4.125 itself shipped spread 40, floor depth 16 and edge softness 50%; the change reaches players
with the next build. `SilhouetteFogModel` records what saved dials were tuned for:

- model 1 files had every fog dial reset once;
- model 2 and 3 files have only their two densities reset;
- the current model is 4.

## Tools

`scripts/fog-mockup/` renders a staircase seam from six viewpoints through a line-for-line C# port
of the shipping shader (`ShippedFog.cs`):

- **Viewpoints:** above, at a distance, ground level, from the Farseer side, along the seam and
  straight down.
- **Scene:** Farseer hills rise from 90 blocks past the seam; `--ramp=BLOCKS` tilts the ground
  across it and adds a column with the strip on the sampled height alone.
- **Output:** average and worst seam show-through with the worst pixel's location, and how much of
  the terrain beyond the fog shows. `fog-dissolve.png` shows a 64-block seam move dissolving.
- **Comparisons:** `--with-0.4.124` and `--with-0.4.122` add frozen ports of those builds.
- **Source links:** it reads the shader's constants and the dial defaults from source and samples
  the mod's own noise texels.

Port any change to the shader's functions into `ShippedFog.cs`, render before and after, and read
the worst pixel. It marches at full resolution; the game marches at half. It is not the game: no
tone mapping, a fixed sun, and an analytic field.

## Next session

Ask before launching the game; the owner tests without an agent launch. Ask him what comes next.
If he returns to the fog, the open questions are in TODO: how the strip reads from above,
straight-down coverage, edges taller than the strip, camera steadiness, upsample edges, the fade
time and a possible fade-in. If he takes up the exploring hitches, start from the TODO note and
separate fog, Farseer, the sweep and vanilla by switching each off on one route.

## Remaining evidence

- Fog cost: the `.vhfog` on/off frame rate has not been taken since 0.4.123.
- Rapid replacement/unload, complete camera yaw, excavation, night, rain/natural fog and winter.
- Cold client restart/full-disc tile-store read-back and dedicated multiplayer matrix.
- Optional P5 island/inlet refinement only if the accepted picture still needs it.
- Finish the deliberate player-facing Farseer rename and bundle Farseer's MIT notice.
- P6 Terrain Ahead and then P7 canopy remain separate later features.

## Artifacts and evidence

| Version | Bytes | SHA-256 | Checkpoint | Human evidence |
|---|---:|---|---|---|
| 0.4.120 | 689,194 | `845C414B2CEA081BF1140A88A7B0DBE90307ACF48BBDC2095C44027C8F8DF344` | `e6b4a21` | Outer ribbon shape better; internal hard lines rejected |
| 0.4.121 | 693,012 | `4E67B6B2631C675F8BFE5DFC8BB3666A4C02CC8CA6F3E9A552309997926845F7` | `a5f528d` | Fog itself much better; seam under-covered and dials insufficient |
| 0.4.122 | 693,608 | `DFA51C84B89127D7E7AF3D7DE99EE3150736C1533E7BC2B3DF3C482C5D62DD59` | `d46b776` | Too bright, too dense, unnatural |
| 0.4.123 | 697,966 | `BF473EEA3BAADB91490CE7FF268CB4720FAD28CFE1A331690AC5DE9FD7B59696` | `b46cf0a` | Shape fantastic; seam visible at 35% density, covered at 60%; ribbon moved with the camera; 400 vs 550 FPS |
| 0.4.124 | 701,843 | `8A15E534A49CDA2DCAD07C1A628B4EBA97C2A8823B206E2A165348A505862C7C` | `1fb10f5` | Compiled and ran; "getting better"; wall too dense to see Farseer through; fog vanished when the seam moved |
| 0.4.125 | 708,897 | `03BC8AE3A662549B9A7A0A7F207DCCBD6FFF0A17B040B4F5D75C889392E8B1C4` | `92eb9c3` | Compiled and ran; "Everything looked good"; hitches while exploring, cause unknown |

All were verified and copy-installed without removing rollback artifacts. The final suite is
25,443 assertions with zero failures; Release builds are warning/error free. No agent game launch
occurred in Sessions 106-108.
