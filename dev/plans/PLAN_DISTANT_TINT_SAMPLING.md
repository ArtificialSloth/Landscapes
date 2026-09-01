# Plan - where the distant seasonal tint is sampled from

**Status:** Not started. **One measurement is outstanding before the design can be chosen**, and it
is cheap. Do not build section 5 until section 4 has run - the current evidence is consistent with
two different causes that call for opposite fixes.
**Date:** 2026-08-31, after Session 73.
**Scope:** Client rendering only. How the live tint table is sampled and how a cached fragment
selects from it. No cache, database, network or wire-format change is expected; section 7 records
the one option that would need them.
**Baseline:** Since 0.3.19 the tint is one colour per slot, sampled by averaging
`IWorldAccessor.ApplyColorMapOnRgba` over an 8x8 lattice of positions 8 blocks apart - a 56-block
span - centred on the **camera**, at two heights (sea level and sea level + 320), refreshed every
30 seconds one slot per frame and published atomically. Every cached surface in the world is
multiplied by the result, interpolated between the two heights by vertex Y. Since 0.3.20 the value
is diluted by the untinted-dirt share (G49). `.vhtint`, added in 0.3.167, takes one block's reading
apart.

## 1. The problem, in one sentence

There is one tint for the entire visible world and it is sampled where the player is standing.

## 2. What is measured

From the owner's `.vhtint` reading on the 2026-08-31 test patch, `soil-medium-normal` at
`512443,118,513426`, sea level 110:

| line | meaning | value |
|---|---|---|
| A | what the shader multiplies by here, undiluted | R0.710 G0.602 B0.162 |
| B | the same 64-position lattice re-run at this height | R0.701 G0.571 B0.152 |
| C | the engine's CPU colour-map answer for this exact block | R0.659 G0.541 B0.125 |
| - | lattice spread at this height | B **0.078 .. 0.220** |

Independently, inverting the screenshot's post-processing put the mod's effective tint at
`R0.694 G0.595 B0.156` against vanilla's rendered `R0.688 G0.581 B0.119`. Instrument and photograph
agree on the size of the error - +30% and +31% blue - which is what makes the decomposition
trustworthy even though each measurement alone would not be.

Established from those numbers:

- **Bucket sharing is not involved.** The slot was claimed by this block and carries its own share.
- **The two-height interpolation is not involved.** The ground is 8 blocks above sea level, so the
  blend is 0.025. A vs B is ~6% of the blue, and that 6% is the low sample being taken *at* sea
  level rather than at the terrain's height - not the interpolation between them.
- **The remaining ~22% is in the lattice**, B against C.
- **Red and green are close throughout.** Blue takes almost all of the error because blue in these
  colour maps sits near the bottom of a range spanning nearly 3x, so anything that averages lifts
  it disproportionately while red and green sit mid-range and barely move.

## 3. What is NOT established, and why it decides the design

Session 73 attributed the 22% to the lattice smearing across a **climate gradient**. That is one of
two readings of the same number, and the other one inverts the fix.

The lattice averages over two things at once, because position moves both:

1. **The season map's row**, which `colormap.vsh` picks per block from
   `valuenoise(worldPos/24) + valuenoise(worldPos/12)`. Sixteen rows, varying at a 12-24 block
   scale. In midsummer they run #628100 to #97B825, so blue alone spans 0 to 0.145 across the rows
   at one climate. **Averaging these is correct** - a vanilla field really is all sixteen mixed,
   and G47 exists because sampling one row painted every distant field with it and re-rolled as the
   player walked.
2. **The climate**, temperature and rainfall, which set both the climate map's coordinate and the
   season map's column. **Averaging this is wrong** - it is a property of the ground being drawn,
   not of a neighbourhood.

The measured spread, blue 0.078..0.220, is wider than the row range alone, so both are present. It
does not say in what proportion. And the two readings disagree about what to do:

- If the 22% is mostly **climate smear**, the lattice is reaching into terrain that is not the
  terrain being drawn, and the fix is to make the tint a function of position (section 5).
- If it is mostly **row averaging**, the lattice is doing its job and the error is that vanilla's
  own visible average over this small patch happens to sit near one row - in which case the fix is
  elsewhere entirely, and a per-position tint would change nothing.

The screenshot leans toward the first reading: vanilla's visible average over the patch came out
B0.119, close to the single-point C0.125 and far from the lattice average B0.152. But the patch is
roughly 30 blocks across and the row noise varies at 12-24 blocks, so it may hold only one or two
distinct row values - which would produce exactly that result under either reading. **This is not
resolvable from the data in hand.**

## 4. Phase 0 - the measurement that decides it

Add one line to `.vhtint`: the same lattice average taken over a **short** span, wide enough to
decorrelate the row hash and narrow enough that climate is effectively constant. Roughly 24 blocks;
the exact span should be stated as a constant with the noise scales it is derived from.

That gives a three-way split instead of a two-way one:

- **C to B-short** - row averaging alone.
- **B-short to B** - climate smear alone.

Take it at three places: the test patch, somewhere in the middle of a large uniform biome, and
across a visible biome boundary. The middle-of-biome reading is the control - climate smear should
collapse to nothing there while row averaging does not.

**Also outstanding, and independent:** whether `ApplyColorMapOnRgba` agrees with `colormap.vsh` at
all. `dev/TODO.md` has carried this since Session 31 - the C# path's `Math.Max(0, 128 - temp) / 512`
and `Math.Max(0, temp - 130) / 200` are **integer** divisions that evaluate to zero for every
reachable temperature, where the shader computes both as floats and adds an altitude term; the
estimate there is roughly a third more seasonal colour in the mod's tint than vanilla draws.
`.vhtint` **cannot** settle this, because both its A and C lines go through
`ApplyColorMapOnRgba` and the error cancels. Settling it needs the shader's weight computed
independently in C# from `tempRel`, `rainfallRel`, `seasonRel` and altitude, and compared against
the CPU call at a known position.

This matters more than the sampling question and should run first, because **everything the mod
samples is that call**. If it is wrong, resampling it better converges on the wrong answer.

## 5. Phase 2 - the design, if section 4 says climate smear

A tint field over world cells, replacing one colour per slot with one colour per (slot, cell).

**Shape.** A 2D texture indexed by slot on one axis and a flattened world cell on the other,
sampled in the **vertex** shader by the vertex's own world position, bilinear between cells so the
result is continuous. This is the third instance of a pattern the renderer already runs twice - the
vanilla readiness mask and the seam distance field are both CPU-built 2D textures with an
addressing rule that must agree between C# and GLSL, and both are held to it by a static check.
Follow that precedent including the check; a silent addressing disagreement misaddresses every
cell at once and looks like a colour bug, which is the most expensive failure mode this codebase
has.

**Why not per section.** Sections double in edge length per LOD level, so one L6 section spans 64x
the blocks of an L0 one. A single colour per section would tile visibly at distance and shift as
sections load and change level - a new artifact traded for an old one. It also does not fit: the
GPU-driven path has no per-draw uniforms, its per-section facts ride in a fixed 64-byte record, and
a section may use up to 64 slots. A fixed cell size in blocks keeps granularity independent of LOD
level, which is the same invariant the blend band holds (nothing may vary below the world grid, and
nothing should vary at a scale that reads as a screen-space effect rather than as terrain).

**Cell size** is the one number that needs judgement, and it trades against exactly one thing: how
sharply a real biome boundary can render. Start from how fast climate actually moves - measure it
with the Phase 0 instrument rather than guessing - and prefer the largest cell that keeps the
error under the ~6% that the sea-level sampling contributes today, so this stops being the dominant
term.

**Sampling per cell** should average the row hash and *not* average climate: sample a short span
around the cell centre, the same span Phase 0 establishes. That is strictly less work per sample
than today's 56-block lattice.

**Cost.** Not the concern. Today's refresh is 20 slots x 2 heights x 64 positions = 2,560
`ApplyColorMapOnRgba` calls once every 30 seconds, one slot per frame. Per-cell over a 256-block
grid covering the drawn radius is order 82,000 calls per refresh - about 32x, but spread over
frames it is ~2,700 per frame for 30 frames once every 30 seconds, and it is sparse: only cells
with drawn terrain and only slots present in them. One extra texture fetch per **vertex**, not per
fragment. A 64-slot by 4,096-cell RGBA8 table is about 1 MB. The real cost of this work is the new
addressing invariant, not frames.

**Publishing** must stay atomic as it is today, and the refresh must stay spread across frames. A
spatially varying table refreshed in place would make bands shift visibly rather than one flat
colour changing smoothly.

**Height** folds into this: sample each cell at the terrain's own height there rather than at sea
level and sea level + 320, which removes the ~6% and the two-height interpolation together. The
cached height field already exists per column.

## 6. Phase 1 - what is worth doing regardless

Sampling the low table at the terrain's height rather than at sea level is worth ~6% of the blue,
costs nothing, and is correct under either reading of section 3. It is still worth holding until
Phase 0 has run, because if Phase 2 follows then the two-height table disappears and this changes
shape with it.

## 7. Deferred - the faithful option

Do what vanilla does: carry `tempRel`/`rainfallRel` per column, bind the colour-map atlas, and do
the lookup per fragment with the engine's own weight formula. This is the only approach that can be
exactly right, including at biome boundaries and across the whole season weight curve.

It is deferred because it is the only option here that reaches the blob format - climate would
either be stored per column, needing a format bump and re-capture, or re-derived on load, which is
the cheaper half of the same idea and worth pricing first. Revisit if Phase 2 lands and the result
is still judged short.

## 8. Non-goals

- Do not change what the tint is *applied to*. The untinted-share dilution (G49) and the top-tile
  coverage (G140) are settled and human-accepted; this plan changes only where the colour is
  sampled from.
- Do not reintroduce single-position sampling anywhere. G47 is the record of what that costs.
- Do not chase the ~4% hue-neutral residual on stone. It is ambient occlusion, shadow filtering and
  the atlas mip chain, and cached terrain is a summary of the world rather than a copy of it.
- Do not tune `.vhband speckle` to compensate for a tint error. That is what 0.15 was partly doing
  before Session 73, and it hid the problem rather than showing it.

## 9. Acceptance

The seam is the only place a person can compare, so acceptance is two observations, both the
owner's:

- At a seam running through grass, cached ground differs from vanilla in **nothing the eye finds**,
  rather than in paleness.
- Walking from one biome into another, distant terrain **keeps its own colour** rather than
  changing with the player's position. This one has never been judged in play and is the larger
  half of the defect; the test patch cannot show it.

Offline, `.vhtint` should report A and C within a few percent on every channel at all three Phase 0
positions, not only at the test patch.
