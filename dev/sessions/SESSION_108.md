# Session 108 — A shallow seam strip, a see-through bank, and fog that dissolves

**Date:** `2026-09-21`
**Branch/commit:** `codex/water-depth-light`, 0.4.125 at `92eb9c3`
**Mod version:** `0.4.125`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Source and checks remain authoritative; the owner's in-game
> observations are the acceptance evidence for appearance.

## Context and investigation

The owner asked for two changes to the 0.4.124 seam fog.

1. **Too dense.** The whole fog wall had to stay dense to hide the seam at ground level. He asked for
   a strip dedicated to the seam, no more than 2-3 blocks above the ground, so the fog above it
   could be much thinner: the player should be able to see Farseer terrain vaguely through it.
2. **Abrupt.** When exploring loads new chunks, the fog that covered the old seam vanished in one
   frame. He asked for it to dissipate instead, over about half a second, with the timing left to
   the agent and put on a dial.

### 1. Where the density came from

The 0.4.124 medium was designed by vertical-column optical depth: the seal gave 3.5 over the
measured core and the bank's mean column was 3.2, each divided by its own column height. The bank
is wide and low, so a level view crosses about eight columns' worth of it. On his saved 60/60 dials
the mock-up showed only **1.7% of the terrain behind the fog from ground level**; from above the
same fog was fine. The seal also filled the bank's full height, so no density setting could thin
the upper fog without opening the seam.

### 2. Why the fog vanished

`LodTerrainRenderer.SilhouetteFog` rebuilds the field on a worker whenever the seam mask's
signature changes and publishes it by replacing the texture. Nothing of the old field survives the
swap, so fog over a stretch the seam has left disappears in the frame the new field uploads. While
exploring, the signature changes often, so a fade has to cope with a new field arriving before the
last fade has finished.

## Work narrative

### 3. The offline mock-up grew what this needed

`scripts/fog-mockup` could not show either request: its Farseer side was a flat colour, its ground
was flat, and it had only one field. It gained:

- **Farseer hills** rising up to 55 blocks from 90 blocks past the seam, shaded by the sun, and a
  **"terrain beyond the fog" count**: the share of pixels on the far side of the fog that still show
  through.
- **`--ramp=BLOCKS`**, which tilts the ground across the seam so the real ground at the staircase is
  up to that many blocks off the sampled seam height, as on a hillside. It adds a column with the
  strip resting on the sampled height alone.
- **A dissolve sheet** (`fog-dissolve.png`): the seam moves 64 blocks toward Farseer and the old fog
  is drawn at 0, 25, 50, 75 and 100% of the fade.
- **`--with-0.4.124`**: a frozen port of the 0.4.124 shader (`Fog0124.cs`) as the before picture.
- Terrain intersection now marches a heightfield instead of a flat plane.

### 4. The strip and the see-through bank

- **The strip.** A layer whose column over its band is 3.5 optical depth at 100% Seam strip density,
  whatever its depth. It is full to 0.55 of its local top and gone by 1.35 of it (column fraction
  0.95). Its top rises and falls with the broad noise and the billows, between 0.64 and 1.44 of
  the dial. Over its band, noise varies its density by a quarter either way but cannot open it.
- **Its band.** The measured staircase stray plus 6 blocks (8-44). A layer this shallow is crossed
  close to where a ray lands, so it needs less margin than the tall bank's core (stray plus 10). Its
  edge wanders by up to 6 blocks, broad and billow-sized, inside that margin. Past the band it frays
  out over 12 blocks, lobed by the billows and lower than the band, and ends at 3.5 fall-offs.
- **The bank.** 0.4.123's shape is kept, but its density no longer depends on its height. It is
  quoted along a level crossing through its ground layer (`BankCrossingOpticalDepth` 1.0, haze
  0.15) over twice the core plus the two exponential shoulders. The seam-to-outer density blend and
  the full-height seal are gone.
- **Dials.** Fog seam density became **Seam strip density**, Fog outer density became **Fog
  density**, and **Seam strip height** (1-16, default 3) was added. `FogModel` 4 resets only the two
  densities, to 100%.

Iterations, measured on his 0.4.124 shape dials (spread 48, height 32, floor 20, edge 35%):

| Step | Terrain beyond, ground level | Seam straight down, worst | Note |
|---|---:|---:|---|
| Strip added, bank still column-normalized (0.9) | 8.8% | 18.3% | the bank was still a wall, and a descending step could jump the 3-block strip (§5) |
| Bank on a level crossing (0.8), step cap over the strip | 52.2% | 6.1% | |
| Bank crossing 1.0, wider fringe | 47.7% | 6.0% | the shipped values |

From above, the first strip read as a smooth pale ribbon with clean edges, the look G246 warns
about. A wider, lobed fringe (5, then 8, then 12 blocks) and billow-dependent shading of its top
made it read as low mist in the mock-up. That the strip no longer reads as a painted stripe in play
is the owner's call.

### 5. Two things a shallow layer needs

- **Steps must not jump it.** The march sizes a step from the previous sample's extinction, so a ray
  dropping from the thin bank arrives with a long step. The step whose middle lands above the strip
  counts only bank, the next begins below the ground, and the strip is skipped. Straight down, the
  worst seam pixel showed 18%. Over the strip's reach, a step may now descend at most to just inside
  its highest possible top, then a third of its depth at a time. The worst pixel fell to 6%.
- **It must rest on the ground actually there.** The field's ground is the Farseer height at the
  nearest point of the smoothed centreline. On a slope that is several blocks off wherever the
  staircase strays from the centreline. So the strip rests on the surface the ray lands on while
  the sample is within 24 blocks of the landing point, blending back to the sampled height by 64
  blocks and never moving more than 16 blocks from it. Rays into the sky use the sampled height.
  The mock-up on a 10-block ramp:

| View | Resting on the sampled height, worst | Resting on the landing surface, worst |
|---|---:|---:|
| From above | 84.4% | 1.0% |
| At a distance | 69.8% | 1.0% |
| Ground level | 58.5% | 1.0% |
| Along the seam | 49.6% | 1.0% |
| Straight down | 91.5% | 5.9% |

### 6. Dissolving the old fog

- **What is kept.** When a build starts, the render thread snapshots the published field's half
  floats and lattice (`LodSilhouetteFogLayer`) and, if it still has weight left, the current
  residual.
- **The worker.** After building the new field, `BuildResidual` carries both onto the new field's
  lattice (fields at one reach share texel size and grid alignment). Each texel keeps whichever
  candidate has more fog there, judged by weight and distance past the core. Fog shown until now
  starts at its open-end fade plus a head start, the smoothed build time in fade durations. An
  earlier residual carries on with what it had left, so a new field arriving mid-fade does not
  restart or cut the fade. Where the new field has the same distance, ground and end fade, the
  weight is zero.
- **The shader.** The residual is a second `RGBA16F` texture on unit 3. The shader subtracts the fade
  durations elapsed since the build began and uses what is left as the old field's weight. That
  weight scales the old bank's envelope, which lowers, thins and erodes it into strands, and the
  old strip by its square, so it clears at an even pace. Old and new combine as the thicker of the
  two, so an unchanged stretch neither doubles nor dips.
- **Cost.** The noise is sampled once and shared, and the second shape evaluation runs only where
  the residual has weight. The texture is freed when its largest weight has elapsed.
- **New fog appears at once**, so the moved seam is never exposed. A fade-in was offered, not built.
- **Fog fade time** (0-3 s) defaults to 0.8 s. The owner suggested about half a second. The strip
  stays nearly opaque through the first quarter of the fade (86% at a quarter, 59% at half, 20% at
  three quarters), so its visible change lasts about half a second.

### 7. Checks and packaging

- The seal's column check became the strip's: column at tops 0.8-23, a quarter left at half weight,
  lateral profile, band inside the core, reach.
- A new residual check moves four rows of a staircase frontier two cells. It shows the moved stretch
  at 1 + head start, an unchanged stretch at zero, an earlier residual carried on from where it
  was, an expired residual, differing lattices and an unchanged republish all leaving nothing.
- The wiring checks cover the new uniforms, unit 3 and its restore, the landing anchor and the step
  cap.
- 25,443 assertions pass.
- The Release zip was verified (shader matches source, new strings in the DLL) and copy-installed
  beside 0.4.124.

### 8. The owner's 0.4.125 session

His client log, 18:37-19:07:

- the one-time density notice;
- `VintageHorizons 0.4.125 loaded`;
- "the lodsilhouette shader compiled", which requires all three fog programs;
- no seam-fog stop, and a clean exit.

He said: "Everything looked good." He also reported hitches while exploring new terrain, with the
minimum FPS dropping low periodically. He does not know whether that is the fog or Farseer, the
savegame sweep changes, or something else, and asked for it to be noted for future performance
work. No measurement of it was taken.

His saved dials at 19:02 differed from the shipped defaults in three places: spread 36, floor depth
20 and edge softness 35%. At his request they became the defaults (source only, after 0.4.125).
On them the mock-up reads the same seam coverage, and more terrain beyond the fog: 58% from ground
level, 84% along the seam, 88% from the Farseer side.

---

## Delivered

- **0.4.125:** the seam strip resting on the landing surface, with its step cap; the bank on a
  level crossing; the dissolving residual; Seam strip height and Fog fade time; the renamed
  densities; `FogModel` 4.
  - Verified, copy-installed and owner-played; runtime shader compile production-observed.
  - Artifact: 708,897 bytes, 45 entries, SHA-256
    `03BC8AE3A662549B9A7A0A7F207DCCBD6FFF0A17B040B4F5D75C889392E8B1C4`.
  - Checkpoint `92eb9c3`.
- **After 0.4.125, source only:** fog defaults set to the owner's tuning (spread 36, floor depth 20,
  edge softness 35%), with the mock-up now rendering the shipped defaults. The next build carries it.
- **`scripts/fog-mockup/`:** hills, `--ramp`, the dissolve sheet, the beyond-the-fog count and the
  frozen 0.4.124 comparison. Committed with this session's documentation, together with Sessions
  106-107's.
- Final checks: 25,443 assertions, zero failures; Release builds error/warning free. No game was
  launched by the agent.

## Decisions

- **The strip alone hides the seam; the bank is scenery.** Rejected: keeping column normalization
  and lowering the dial. The see-through range would have been the bottom tenth of a slider that
  moves in 5% steps.
- **The strip follows the smoothed centreline, not the exact staircase.** A narrow strip on the
  staircase itself would have drawn the zigzag in fog.
- **The strip rests on the landing surface, within 16 blocks of the sampled height.** Rejected: a
  pure screen-space decal on the landing point (it loses the side view of the layer). Not tried: a
  per-texel ground from Farseer's 32-block height grid, which is coarser than the landing surface
  and is Farseer's height, not the real terrain's.
- **Old fog dissolves through a residual carried forward on the worker.** Rejected:
  - a two-texture crossfade, which a third update arriving mid-fade would cut short;
  - delaying the new field until a fade ends, which would expose the new seam;
  - a per-frame GPU pass, which adds another shader program with no offline check.
- **New fog appears at once.** The seam must never show; a fade-in is the owner's call.
- **Reset only the densities** for model 2 and 3 files, whose meaning changed.
- **The owner's tuning is the default**, as with 0.4.123 and 0.4.124.

## Traps

**A bank normalized by its vertical column is a wall from the side (G251).**
- Trigger: sizing a wide, low bank so its column over the seam is just opaque.
- Failure: a level view crosses about eight columns' worth; on 0.4.124 at 60/60, 1.7% of the terrain
  behind showed from ground level.
- Safer action: quote scenery fog along a level crossing and hide the seam with a separate shallow
  layer.

**A thin dense layer is stepped over (G252).**
- Trigger: a march that sizes each step from the last sample's extinction, meeting a layer a few
  blocks deep from above.
- Failure: the step whose middle lies above the layer counts none of it and the next starts below
  the ground; straight down, 18% of the seam showed at the worst pixel.
- Safer action: over the layer's reach, cap each step's descent to just inside its highest top.

**A shallow layer on a coarse sampled height is buried or floats on slopes (G253).**
- Trigger: placing a 3-block layer on the seam height sampled along the smoothed centreline.
- Failure: on a 10-block ramp, up to 91% of the seam showed at the worst pixel.
- Safer action: rest it on the surface the ray lands on, near the landing point, clamped to the
  sampled height.

**Replacing a published field removes its fog in one frame (G254).**
- Trigger: publishing a rebuilt seam field by swapping the texture.
- Failure: the owner saw the fog over the old seam vanish as chunks loaded.
- Safer action: carry the old field forward as a residual whose weight wears down, chained across
  updates, zero where nothing changed.

## Flagged and unverified

Judgement calls awaiting human review:

- whether 0.8 s is the right fade and whether new fog should fade in too;
- whether the strip reads as mist or as a painted stripe from above, where it is a pale band about
  60 blocks wide;
- whether edges taller than the strip, such as tree lines, cliff faces or the sunk Farseer apron,
  show at the seam, and how the landing anchor looks on cliffs;
- where between the new defaults and 200% the owner wants Fog density.

Claims that lack the evidence level required to treat them as established:

- "Everything looked good" is a whole-result verdict. Straight-down coverage, upsample edges, camera
  steadiness and the fade's feel were not separately reported.
- 0.4.125's cost is unmeasured. Each field rebuild now also builds and uploads a residual, and
  while one fades the shader evaluates the old field's shape again wherever it still has weight.
- The exploring hitch is reported, not measured, and its cause is unknown (TODO).
- All coverage and see-through percentages are from the offline mock-up (simple ground, fixed sun,
  display colours, analytic field), not the game.
- The two-texture crossfade's failure under frequent updates is reasoned from the rebuild trigger,
  not observed.
- During the doc pass, the unrelated `VanillaReadinessChecks.SteadyStateAllocation` check failed
  in 3 of 7 fast-tier runs with no source change between them (TODO). The 25,443-assertion passes
  quoted above are the runs where it passed.
