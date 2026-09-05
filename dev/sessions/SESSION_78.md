# Session 78 — Pricing the season row, and giving distant water its depth

**Date:** `2026-09-04`
**Branch/commit:** `codex/water-depth-light`, from `checkpoint/pre-water-depth` (`bc3a1c0`)
**Mod version:** `0.4.2` through `0.4.10`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

Opened on a TODO review. The owner took four decisions off it, then the session became one
long piece of work on distant water, driven throughout by his screenshots rather than by
reasoning about the shader.

Out of scope: everything measured here about geometry is offline, against his real cache with
no game process. Everything about appearance is his screen. Nothing in this session was
verified by a bench run.

The record below is honest about a bad build and about three attributions that were argued
before they were evidenced. The seam band that prompted half the work is **still not
attributed** at session close.

## Work narrative

### 1. Four decisions off the TODO

- **Leaf cutout** — asked what the current default is. There is no leaf switch at all:
  `IsSeeThroughPlant` guards on `EnumBlockMaterial.Plant`, leaves are `Leaves`, so they never
  carry `FlagCutout` and always act as cover. `.vhcutout` governs the plant family only.
- **Far Biome Colors** — stays off. Already the default; recorded as decided, not flagged.
- **Cached draw distance** — 4,096 on a fresh install. A new config said unlimited while the
  Defaults button said 32,768, so pressing Defaults changed the setting rather than resetting
  it. Both say 4,096 now and a check holds them together. Existing configs keep their value.
- **Settings window** — preset drop-down moved off the right edge so the cost words could
  carry a "Cost" column heading.

### 2. The season row is priced and closed

The owner's standing question was whether baking the seasonal map row into the mesh was worth
building. It needed the feature's cost split into its two halves, and no shipped switch could
do that: `.vhtint leaves off` removes the noise and the fetch together.

`.vhtint leafcheap` is the instrument. It swaps the engine's two value noises for a cheap
position hash of similar spread, leaving the scattered `seasonMaps` fetch exactly as
scattered — verified offline over the measured viewpoint's neighbourhood: all sixteen rows,
evenly spread, adjacent cells never sharing, so the fetch stays as incoherent as the real one
and the measurement does not flatter itself.

One bench run, one viewpoint, four states:

| state | fps (median) | frame |
|---|---|---|
| shipped | 563.9 | 1.7734 ms |
| cheap row (fetch only) | 570.2 | 1.7538 ms |
| feature off | 574.5 | 1.7406 ms |
| restore (drift control) | 563.0 | 1.7762 ms |

Drift 2.8 us against a 35 us effect. **Noise 20 us, fetch 14 us.** Scaled by the 2.94x this
viewpoint runs lighter than the owner's scene, the whole feature is 9.6 fps against his
measured ~10, so the split transfers. Baking reaches only the noise half — about 5.5 fps of
his 300 — for +33% packed geometry memory and a C# transcription of noise whose own authors
warn about float drift. **Not worth building.** Closed.

### 3. The seasonal map shrank to one column, and measured nothing

Only the ROW varies per vertex; the COLUMN is the point in the year and is one value for the
whole frame. So everything any vertex can reach is 16 rows over at most 32 pages — two
kilobytes, not 136. The column moved to the CPU with it.

It measured nothing. The fetch half went 14 us to 6 us while the noise half moved the same
amount the other way and the total stayed at 33 us; the 136 KiB map was already resident in a
multi-megabyte L2 and there was no miss to remove. **Kept at the owner's direction** for the
memory and the simpler shader, not for frame time. Equivalence is pinned by exact texel
equality against the full store at five columns over every page and row.

Writing that check found that the ends of the year clamp to columns 1 and 126, not 0 and 127:
`clamp(rel, 0.01, 0.99)` binds first and the 0..127 clamp never fires. Pinned at what four
accepted builds actually drew.

### 4. Water: the depth gradient

The owner's report was that vanilla darkens the ground under water and we do not. Measured off
his screenshots, vanilla water covered a 2.2x brightness range against one flat value for ours.

The mechanism is exact and the engine hands it over: water carries `lightAbsorption: 1`, and
`ChunkIlluminator.IsDirectlyIlluminated` settles a lit column at `defaultSunLight - (summed
absorption above)` with **no per-block travel penalty** — so a bed under d blocks of water is
exactly d light levels darker. Cached terrain stores no per-position light at all: one
sky-exposed level multiplies every surface in the world.

It needed neither capture nor probing. The cache already holds the water runs standing on the
bed, so the depth is arithmetic on data an existing cache has. Baking the ratio into the
stored colour is exact rather than a shortcut: `SunBrightness` is the world's MAXIMUM sun
level, not the clock, and with no block light vanilla's own light maths collapses to
`lightColor.a * ambientColor`, linear in the level.

Measured with the shipping mesher: **+0.33% vertices** on the current cache.

### 5. Three bugs that each produced "no effect"

The owner reported the switch doing nothing. It was doing nothing, for three separate reasons,
and each would have been enough alone.

**A table of ones.** Built from the world's ramp at the first mesh; if the world cannot answer
yet, a `SunBrightness` of 0 makes every lookup clamp to the same floor and every ratio come
out 1.00. The feature then splits the geometry, pays its whole bill, and multiplies by one.
Cached for the session. Now not cached while degenerate, with sections meshed before it
resolved rebuilt once when it does.

**Level 0 discarded the colour outright.** A textured quad spends word 2 of its packed record
on a texture slot INSTEAD of a colour, and the vertex shader recovers the colour from the
block table by that slot. Texture slots are emitted at level 0 only. So the darkened colour
was written into the field level 0 reads as an index. The owner found this from the outside —
"it darkens at distance and un-darkens as I walk toward it, and it follows my L0/L1
threshold" — which named the cause exactly. Submerged quads now give the slot up.

**The opacity ceiling hid it.** Set to 0.85, which leaves the bed 15% of the pixel; a perfect
result would have moved the colour about 5%. Built the gradient, then set a dial that
suppressed it.

### 6. The wave, and the owner's chunk-grid insight

Vanilla does not draw water flat. Transcribing the engine's own `gnoise` and intensity terms
puts the dip at 0.10 blocks on an inland lake and up to 0.96 on open ocean in wind, and ours
was flat — so where they meet vanilla's surface dips below ours and nothing fills the wedge.

Carrying the wave needs a surface fine enough to hold it; the wavelength is about three
blocks. The first answer given was that the band could not gate it, because the blend band is
view-dependent and the mesh is cached. **That was wrong, and the owner pushed back.**
Ownership is decided per 32-BLOCK VANILLA CHUNK, so a seam can land nowhere else — a static
grid, section-local, known when the mesh is built. Measured with the shipping mesher:

| | water vertices | whole mesh |
|---|---|---|
| merged (before) | 106,820 | — |
| 2x2 patches everywhere | 287,348 | +4.24% |
| per block everywhere | 801,404 | +16.32% |
| **strip +/-1 on the chunk grid** | **149,152** | **+0.99%** |

Full fidelity in the only place a seam can be, for a sixteenth of doing it everywhere.

His second question — do we not already re-mesh for the band, with vanilla textures? — is
right about the observation and cuts the other way: texture slots are baked into every L0 quad
regardless of the band and the FRAGMENT shader gates the sampling. Data uniform, use gated.
Geometry cannot work that way. Re-meshing measured 74 ms a section, so a band ring would be
about 3 seconds of worker time per 64 blocks walked.

### 7. Water is not shaded by its normal

The wave made a latent difference visible. Vanilla's `chunkliquid.fsh` has no normal term at
all — a fact already written in this shader's own comment, about ground — and ours put water
through the ground's shade. Flat water has one normal and nothing showed; a tilted surface
picks up a different shade from a flat one, and every boundary between them drew a bright
crease. Measured off a screenshot: a 20-pixel band, luma 29 -> 49 -> 31, same water at the
same depth either side.

### 8. A bad build, and its revert

0.4.8 shipped a derivation: vanilla's fresnel saturates at range, so an opaque sheet is what
vanilla would draw there, so the ceiling should not sit below it, and the invented extinction
compensating for that ceiling should go. The argument holds as far as it goes and ignores that
at that range **vanilla draws no water at all** — there is nothing for a flat opaque sheet to
agree with, and it sits beside vanilla's near water, which visibly grades. Measured off the
owner's screenshot: cached water flat at luma 80 against vanilla running 82 down to 55.

Reverted in 0.4.9 to the pair he had tuned and accepted.

### 9. The seam band: four attributions, none confirmed

A chunk-aligned band along the seam, measured brighter than the water on both sides — luma 93
against 80 beyond and 82 approaching, and it overshoots away from vanilla in both a dark and a
bright scene rather than sitting between them.

Ruled out, in order:

1. **The seam blend band** — `!translucent` gated in the shader. Land only.
2. **Fog interpolation** — real and measured. Fog is evaluated per vertex and interpolated;
   our quads span tens of blocks where vanilla's water has a vertex per block. Worst case
   0.022 fog units across a 60-block quad, roughly 1 luma, peaking at the 250-block clamp.
   About a tenth of the band. Not it.
3. **Vanilla's own foam firing on our depth** — argued from vanilla's shallow-water term
   reading the depth buffer. Never tested; superseded.
4. **Water walls at uncaptured edges** — a real gap found in the mesher's guard, which covers
   an absent SECTION but not an uncaptured COLUMN. `.vhwater walls off` built as a diagnostic
   at the owner's request. **Tested: it did not fix the seam.**

The owner's own reading at close: it is our seabed seen THROUGH vanilla's water, mismatched
against vanilla's own seabed beside it. That is consistent with the invented clarity term,
which darkens our bed where vanilla's carries only the light absorption — 0.32 of shore
brightness at eight blocks against vanilla's 0.70. **Untested**, because the bisection had
left `.vhwater depth off` saved in his config, which silently disables clarity.

## Delivered

- **0.4.2** — depth shading; `waterdepthfield` harness; 4,096 draw distance; settings window
  Cost column and preset placement; `.vhtint leafcheap`; season column table.
- **0.4.3** — degenerate absorption table no longer cached; deferred rebuild; the report
  resolves rather than reading a cache; water-only surface scan; ceiling 0.85 -> 0.7.
- **0.4.4** — submerged quads give up their texture slot so level 0 keeps the shading.
- **0.4.5** — return-path extinction (`.vhwater clarity`, 16 blocks).
- **0.4.6** — vanilla's wave on a +/-1 strip at the chunk grid (`.vhwater wave`).
- **0.4.7** — water no longer shaded by its normal; clarity 10.
- **0.4.8** — ceiling 1.0, clarity 0. **Regression; reverted.**
- **0.4.9** — revert to the owner's accepted settings.
- **0.4.10** — `.vhwater walls` diagnostic.
- 21,865 assertions, zero failures.

## Decisions

- **Baking the season row: rejected on measurement.** 5.5 fps of 300 for +33% packed memory
  and a noise transcription. Do not re-propose without new evidence.
- **The season column table kept despite measuring nothing**, at the owner's direction, for
  memory and shader simplicity.
- **Per-block water confined to the chunk grid**, not everywhere and not by patch size:
  +0.99% against +16.32%, with better fidelity where it matters.
- **Submerged ground gives up its block texture** rather than packing depth into the spare
  high bits of the slot field. The spare bits exist — real slot values are twelve of
  twenty-four — and using them is a shader change on every render path.
- **The owner's accepted settings outrank the derivation.** 0.4.8 was defensible and worse.

## Traps

- **A switch whose cost lands in both halves of an A/B is not measurable.** The water-surface
  split was computed regardless of whether the feature was enabled, so it happened in the
  baseline too and the delta was exactly `+0, 0.00%`. Promoted as G152.
- **A diagnostic that answers from a cache its own setter just cleared says one thing
  forever.** `.vhwater clarity` invalidated the absorption table and then described it, so it
  reported "no table resolved yet" on every call, in every case, working or not — through a
  whole testing session. Promoted as G153.
- **A textured quad has no colour word.** Anything written there at level 0 is read as an
  index and discarded. Promoted as G154.
- **The blend band moves with the player; the vanilla chunk grid does not.** Confusing them
  cost a wrong "it cannot be done" answer to a correct suggestion. Promoted as G155.
- **A screenshot is evidence of a moment, not of a state.** A frame taken before terrain
  finished loading was read as a rendering defect and produced a whole wrong finding, an
  unfinished change, and a retraction. Promoted as G156.
- **A measurement that counts one buffer cannot see a change in the other.** `VertexCount` is
  opaque only; a water-only experiment produced byte-identical totals until the water buffer
  was counted too.
- **A cache that the owner is playing on moves under a harness.** Two geometry figures a
  commit apart were not a controlled pair: the same 120 section keys held different contents.

## Flagged and unverified

Judgement calls awaiting human review:

- `WaterClarity` at 10 and `WaterMaxAlpha` at 0.7. The clarity term has **no vanilla
  counterpart**; it was added to compensate for capping our opacity below vanilla's, and being
  depth-only it cannot follow the view angle — which is why a value tuned at one render
  distance stops matching at another. The owner found that and it is not fixed.
- The wave's 64-block fade past vanilla's view distance, and the +/-1 strip width.

Claims that lack the evidence level to be treated as established:

- **The seam band is unattributed.** Four hypotheses, three ruled out and one tested negative.
- **No build after 0.4.1 has been bench-measured.** Every frame-time figure here is either the
  season-row runs or offline geometry.
- **No shader in this session was compile-verified offline.** There is still no GLSL validator
  in the toolchain.
- The depth gradient, the wave, and the crease fix are all owner-observed as better; none has
  a measured before/after beyond screenshot pixel samples.
