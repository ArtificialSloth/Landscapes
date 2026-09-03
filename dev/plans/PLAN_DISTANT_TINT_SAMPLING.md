# Plan - where the distant seasonal tint is sampled from

**Status:** Amended 2026-09-01 after 0.3.172/0.3.173 and two owner readings from high ground.
Read section 5A first: it records a shipped correction that is arithmetically right and never
reaches the screen, and it moves the two-height tint table from a footnote to the blocking item.
Section 4's measurement is still outstanding and still decides section 6, but it is no longer
first in line.
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
2. **The climate**, temperature and rainfall, which set the climate map's coordinate.
   **Averaging this is wrong** - it is a property of the ground being drawn, not of a
   neighbourhood.

> **Corrected in Session 75.** This said climate sets the season map's column as well. It does not.
> `colormap.vsh` reads the season column at `clamp(seasonRel + b/40, 0.01, 0.99)` - the calendar
> plus a position noise - and climate never enters it. The season colour therefore needs no spatial
> field at all, which removed half of what Phase 2 below was sized to carry. See also G144 for the
> separate and larger fault this uncovered in the same expression.

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

## 4A. What the mountain readings added

Owner readings on 2026-09-01, `soil-medium-normal` at 511161,203,513885 - 93 blocks above sea
level, -10.4C, raw temperature 103 - with the weight correction switched off and on at the same
block:

| | weight OFF | weight ON |
|---|---|---|
| A, what the shader multiplies by | R0.683 G0.627 B0.080 | R0.686 G0.627 B0.081 |
| B, the lattice at that actual height | R0.693 G0.604 B0.081 | R0.667 G0.518 B0.075 |

Three things fall out of it.

**The lattice error changes sign with position.** At the sea-level test patch the lattice read 22%
HIGH in blue against the single point; here it reads 29% LOW, and the reading also puts this exact
block at the very top of its own neighbourhood's blue range. Row averaging cannot do that - the row
mean is the row mean, so its bias has one sign. Climate smearing goes whichever way the local
gradient runs. Two readings are not proof and section 4 still stands, but the evidence now leans
toward climate smear, which is what section 6 assumes.

**Slot sharing is real and was wrongly eliminated.** `soil-medium-verysparse` drew with
`forestfloor-5`'s untinted share - its own is R0.176 G0.125 B0.079, the slot carries
R0.203 G0.164 B0.140 - which puts it at 0.207 blue where its own share gives 0.150, **38% too
much**. Session 73 checked this at the test patch, found the slot claimed by the block itself, and
eliminated it. It was not eliminated; it was absent at that one position. Slots are keyed on the
untinted share rounded to 1/8, so blocks of genuinely different coverage share one and the first
to register wins. Independent of everything else here, and cheap to narrow while slot use is 18
of 64.

**The two-height table is wrong at altitude by more than the weight is.** A interpolates green to
0.627 where the lattice at the real height gives 0.518 - 21% - against the roughly 6% estimated
from the test patch, where the blend factor was 0.025 rather than 0.291.

## 5A. Why 0.3.172's weight correction does not reach the screen

**This is a build that shipped, passes every check, and does nothing.** Recorded in full because
the failure is instructive and the plan predicted it in writing.

A barely moves between off and on - 0.683 to 0.686 - while B moves properly. A is the published
tint table, which is what the renderer draws; B is the diagnostic sampling at the block's own
height. The correction reaches the diagnostic and not the terrain.

The table is sampled at two fixed heights, sea level and sea level + 320, and the shader
interpolates between them per vertex. So the correction is evaluated at *those* heights, not where
the ground is:

| sample height | correction computed there |
|---|---|
| 110, sea level | x1.058, negligible |
| 203, the ground being looked at | **x0.506** |
| 430, the upper sample | refused - the adjusted temperature saturates at 0, so the engine's own weight cannot be recovered |

The table therefore carries a 6% correction from below and nothing from above, and 0.686 against
0.683 is that 6% leaking through the blend.

An earlier section of this plan already said the two-height table "is reproducing the wrong thing"
and it was built into anyway. The lesson is not about tint: **a correction that is a function of a
quantity the delivery mechanism does not carry cannot be delivered, however correct it is.** The
harness checks could not catch it because they test the arithmetic, and the arithmetic was never
in doubt.

## 5B. Phase 1, revised - the height ramp

Replaces the two-height table. This is now the blocking item, ahead of section 4's measurement,
because nothing else can land until the delivery mechanism can carry a height-dependent value.

### Why world height does not enter

Everything height-dependent here comes from one quantity: temperature falls at a fixed 1 unit per
1.5 blocks. That is per block, not per fraction of the world, so the range over which anything
moves is a fixed window around sea level and the same in every world:

| raw temperature | blocks above sea level where both the climate coordinate and the weight go flat |
|---|---|
| 60 | 139 |
| 103 | 154 |
| 160 | 240 |
| 255 | 382 |

Below sea level it moves too and saturates going down - at 110 below, the weight is 0.482 against
0.884 at sea level.

So **index the ramp by distance from sea level over a fixed window, and clamp outside it.** A
taller world simply puts more terrain in the flat tail, which a clamp represents exactly. Nothing
reads `MapSizeY`.

Rejected: **two sample heights bracketing the visible terrain.** It adapts to world height in the
wrong way - a taller world means a longer span for two points to cover linearly, across a curve
that is flat, then drops 0.63 over sixty blocks, then flat again. The present fixed +320 is already
an instance of that failure, and in a short world with a low sea level it would sit outside the
world entirely.

Rejected: **a fraction of world height.** Same objection, and it keys on a quantity that has
nothing to do with the curve.

### Resolution, measured rather than guessed

Linear interpolation error of the weight against sample spacing, worst case over raw temperatures
20-240 and distances -256 to +512:

| spacing | samples | worst error |
|---|---|---|
| 8 | 97 | 0.034 |
| 16 | 49 | 0.072 |
| 32 | 25 | 0.157 |
| 64 | 13 | 0.304 |

A ramp coarse enough to be cheap to SAMPLE is not accurate enough to be worth having - which
decides the structure below rather than the sample count.

### The structure that makes it affordable

Sampling the colour maps at ~100 heights is out of the question: 64 lattice positions x 100 heights
x 20 slots is 64x today's refresh cost. But the three things that vary do not vary the same way,
and separating them makes the expensive one small:

- **The season pixel's average is height-independent.** Its column is the year and its row is a
  position hash. The hash does include Y, so an individual sample re-rolls with height, but the
  distribution it is drawn from does not - so the lattice AVERAGE is the same at every height and
  needs sampling once.
- **The weight is arithmetic.** `LodSeasonWeight` computes it from the raw temperature and the
  distance from sea level. It needs no sampling at any height, which is what makes the 8-block
  spacing above affordable.
- **Only the climate pixel genuinely moves with height and needs the map read.** It is a smooth
  gradient in temperature, so a handful of sampled heights carries it.

So the ramp is built as `climate(h) * (1 - w(h)) + season * w(h)`, with `climate(h)` interpolated
from a few sampled heights, `season` sampled once, and `w(h)` computed at every ramp step. The
season average is recovered algebraically from the two calls already being made -
`season = climate + (engineAnswer - climate) / wEngine`, safe because `wEngine` is bounded below
by 0.065 and a check pins it.

### Delivery

A small 2D texture, slot on one axis and ramp step on the other, sampled in the vertex shader with
two `texelFetch` calls and a manual lerp along the height axis - manual because hardware filtering
would blend across SLOTS on the other axis, which would be silent and wrong. 64 slots x 128 steps
x RGBA8 is 32 KB.

This is the third instance of a pattern the renderer already runs twice, the vanilla readiness mask
and the seam distance field, both CPU-built 2D textures whose addressing must agree between C# and
GLSL and both held to it by a static check. Follow that precedent including the check.

The existing `tintsLow`/`tintsHigh` uniform arrays and `tintYLow`/`tintYHigh` are replaced, not
extended: 64 slots x 96 steps as uniforms would be 6,144 vec4 and well past any guaranteed limit.

### Concrete parameters

- **Ramp:** 96 steps, 8 blocks apart, starting 256 blocks below sea level - covering -256 to +504,
  which contains the whole window section 5B measures. Outside it, clamp.
- **Sampled heights:** 17, evenly spread across the same window, 47 blocks apart. The climate map
  is a smooth gradient in temperature so linear interpolation between them carries it; the weight,
  which is not smooth, is computed at all 96 steps and needs no sampling.
- **Refresh spreading:** one (slot, sampled height) pair per frame rather than one slot per frame.
  17 x 20 slots is 340 units of about 128 colour-map calls each, so the per-frame cost stays what
  it is today and the whole table still republishes well inside the 30-second cadence. Publishing
  stays atomic.

### Two things this must not quietly become

**Do not reproduce the engine's map indexing to make it cheaper.** Reading `ColorMap.Pixels`
directly and reproducing the season row hash and the climate coordinate would make the whole ramp
arithmetic and cost almost nothing - and it would be a third transcription of engine internals in
a system that has already been bitten twice by exactly that (G139, G140). Sample through the
engine's own call at the 17 heights and interpolate.

**Do not let the sampled-height count drift down for cost.** It is the climate map's resolution,
not the weight's, and the weight is the part that needed 8-block spacing. If the cost is a problem,
spread the refresh over more frames rather than sampling fewer heights.

> **Superseded in Session 75.** The whole height axis is gone. Climate is stored per cell and the
> shader applies the lapse rate at the vertex's own height, so height is exact arithmetic and needs
> no sampling. The ramp survives only as a fallback and is scheduled for removal. The claim above
> that the climate map is "a smooth gradient in temperature" is also worth qualifying: measured off
> `planttint.png` it is a broad gradient with about +/-5 counts of per-texel noise, and adjacent
> texels differ by 5.7 counts on average. Interpolating across that averages the noise, which is
> correct for a summary of distant ground for the same reason G47 gives - but the reasoning should
> say so rather than assert smoothness.

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

> **Corrected in Session 75.** The criterion below cannot pass and is not a target. C is the
> engine's answer for ONE block, and the engine picks a season map row per position from a hash:
> across the 64 lattice positions the same report prints, blue spans 0.004..0.090. Holding a
> deliberate average to within a few percent of one draw from that is not possible. Compare the
> field path's own numbers - the D lines added in 0.3.182 - against the lattice mean instead, and
> read the spread line beside any single-sample reading. See G145.

Offline, `.vhtint` should report A and C within a few percent on every channel at all three Phase 0
positions, not only at the test patch.


## 10. What Session 75 actually built

Phase 2 shipped, in a different shape than section 5 describes, and section 5 should be read as the
design that was considered rather than the one that exists.

**Built:** a field of CLIMATE - raw temperature and rainfall, two bytes per 64-block cell, no slot
axis - and the shader finishes the colour from it. One climate read per cell per refresh against
roughly 370,000 colour calls for section 5's shape, and the height ramp disappears because the
lapse rate is arithmetic. Cell size 64 blocks: one level-0 section edge, so a section origin is
cell-aligned at every LOD level, and the measured gradient is linear well past it.

**The constraint neither design accounted for.** The client holds map regions for roughly 1,000
blocks on arrival, growing toward 3,000 as the player explores, against 8,192 of drawn terrain -
and `ClientWorldMap.GetClimate` returns the constant `0xB4B4B4` outside them rather than failing.
This bounds section 5's design exactly as much as the one that shipped. Cells with no answer are
filled by spreading outward from the nearest cell that has one, so nothing draws an edge where the
data stops. `.vhclimate` measures it.

**Phase 0 is answered, differently than it was posed.** Section 4 asked which half of the lattice
error was climate smear and which was row averaging, to decide whether a per-position tint was
worth building. That question is now moot for the climate half - it is per position and correct -
and the row half is settled: the mod averages the rows on purpose and must, because distant terrain
is a summary. What section 4 correctly identified as the thing that "matters more and should run
first" - whether `ApplyColorMapOnRgba` agrees with `colormap.vsh` at all - turned out to be the
whole remaining error, twice: the blend weight in Session 74 and the season column in Session 75.
It is now G144, and it should have been the first thing done.

**Still open:** retire the ramp; persist the field so coverage accumulates across sessions; slot
sharing. See `dev/TODO.md`.
