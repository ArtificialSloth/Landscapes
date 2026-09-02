# Session 74 — The seasonal blend weight, and the height ramp that had to exist to carry it

**Date:** `2026-09-01`
**Branch/commit:** `codex/seasonal-tint-blend-weight`, branched from `c98c164` on `codex/exact-vanilla-draw-range`
**Mod version:** `0.3.172` - `0.3.176`; 0.3.175 human-tested and its core correction accepted, 0.3.176 built and installed but not seen
**Assist protocol / blob / schema:** `1 / 4 / 6`

> IN PROGRESS at session close. Two owner-reported defects are open against 0.3.175 and one fix
> for them is in 0.3.176, unseen. Nothing is committed. Read "Open at close" before continuing.

## Context

Session 73 fixed three causes of the distant-terrain colour mismatch and left a fourth open: the
seasonal tint. `dev/plans/PLAN_DISTANT_TINT_SAMPLING.md` is the live plan and was amended twice
during this session; read it rather than this record for the design.

## 1. The engine computes the season blend weight two different ways

Established by reading `ClientWorldMap.ApplyColorMapOnRgba`'s IL with Mono.Cecil against
`colormap.vsh`. Both paths blend the same two colour-map pixels the same way - `ColorOverlay` on
the CPU is the shader's `mix`, verified - so the weight is the only difference. It differs twice:

- **Both of the CPU's correction terms are integer divisions that are always exactly zero.**
  `Math.Max(0, 128 - temp) / 512` cannot exceed 128/512, and `Math.Max(0, temp - 130) / 200`
  cannot exceed 125/200. Checked exhaustively over 0..255. The shader computes both as floats,
  where they reach 0.25 and 0.625.
- **The CPU has no altitude term.** Its `heightAboveSealevel` parameter is real but is spent
  entirely on the climate map's coordinate via `Climate.GetAdjustedTemperature`, never on the
  weight. The shader shifts the weight's temperature by `dist * (1.5 - 1/1.5)`, about +0.83 per
  block above sea level.

The two agree only near temperature 128 at sea level and diverge by up to 0.39 in weight there,
0.85 at sixty blocks up. On a real mountain - climate map reading 60 at 200 blocks up - the shader
applies **0.00** seasonal colour where the CPU applies **0.55**. The sign flips around 128, so cold
and hot ground are wrong in opposite directions, which is the shape that defeats any single global
colour correction.

`dev/TODO.md` had carried this as a hypothesis since Session 31, estimated at "roughly a third".
It is larger than that and it is two faults, not one.

**Why the 2026-08-31 test patch never showed it:** the owner's ground there is 8 blocks above sea
level at temperature ~125, and at that altitude the two formulas cross at 125. The patch sits
within a few points of the one place the bug is invisible.

## 2. The correction is cheap because it does not need the season pixel

`corrected = climate + (engineAnswer - climate) * (shaderWeight / engineWeight)`. The engine
returns `climate * (1 - w) + season * w`, so the whole correction lives in the part that moved away
from the climate colour - no season pixel, no map row, no reproduction of the engine's hashing. The
division is provably safe: the engine's weight is `0.5 - cos(t/42)/2.3`, bounded in
[0.065, 0.935], so it can reach neither zero nor its clamp.

`Climate.DescaleTemperature` is public and is the exact inverse of the scaling, round-tripping to
+/-1 in 255, and it saturates at the same 0..255 the tesselator packs - so it lands exactly on what
the shader receives.

## 3. 0.3.172 shipped that correction and it did nothing

**The most instructive failure of the session.** Arithmetically correct, passed every check, and
never reached the screen. The tint table was sampled at two fixed heights, sea level and +320, and
the correction was therefore evaluated at *those* heights: x1.058 at the low one and refused at the
high one, where the adjusted temperature saturates. The owner's ground needed x0.506.

The owner's A/B/A reading proved it: the diagnostic's own lattice line moved properly while the
published table moved 0.683 to 0.686.

The plan had already said in writing that the two-height table "is reproducing the wrong thing",
and it was built into anyway. The lesson is not about tint: **a correction that is a function of a
quantity the delivery mechanism does not carry cannot be delivered, however correct it is.** The
checks could not catch it because they test arithmetic, and the arithmetic was never in doubt.

## 4. The height ramp

The two-height table is replaced by a 96-step ramp, 8 blocks apart, starting 256 below sea level,
uploaded as a 64x96 texture and read with two `texelFetch` calls and a hand-written lerp.

**World height does not enter, and that is the point.** Everything height-dependent comes from the
lapse rate, which is per block, so the window in which anything moves is fixed around sea level:
above 382 blocks over it both the climate coordinate and the weight are flat for every reachable
temperature, and they saturate the other way below. A taller world simply puts more terrain in the
flat tail, which the clamp represents exactly. Two brackets around the visible terrain were
rejected for the opposite reason - a taller world gives two points more curve to span.

**Resolution was measured, not guessed:** linear interpolation of the weight is worth 0.034 at
8-block spacing, 0.072 at 16, 0.157 at 32. A ramp coarse enough to be cheap to sample is not
accurate enough to be worth having, which decided the structure rather than the sample count:

- the weight is arithmetic, so it costs nothing at all 96 steps;
- the season pixel's lattice average does not move with height at all;
- only the climate pixel needs a map read per height, and 17 heights carry it.

Refresh is spread one *(slot, sampled height)* per frame rather than one slot, so per-frame cost is
what it was before despite reading the maps at 17 heights instead of 2.

## 5. Three defects found reviewing my own work, before the owner saw them

- **The ramp was not rebound between passes.** `RebindOwnSamplers` exists because the engine
  rewrites texture-unit bindings on every `Use()` (G130). I added a fourth sampler and bound it in
  one place only.
- **A skipped tint slot would have published as untinted.** The old `BeginRefresh` seeded the
  working buffer from the published one; my rewrite dropped that while still publishing the whole
  buffer.
- **A fabricated temperature.** Where the climate could not be read, the code fell back to
  pretending the temperature was 128, which produces a confident colour nothing supports. It now
  holds the engine's own answer flat up the ramp instead.

The last two are the same mistake twice: filling a gap with something plausible instead of
something true.

## 6. What the owner reported against 0.3.175

**Accepted:** it draws, the correction moves the published table (A went 0.694/0.606/0.082 with it
off to 0.665/0.526/0.078 with it on, and A now agrees with the lattice line as a working ramp
should), and **distant high terrain matched vanilla when checked against vanilla's own rendering at
the same spots**. That is acceptance of the session's core correction.

**Two defects, both open:**

- **Distant seasonal colour disappears in some areas** and returns on moving elsewhere.
- **The blend band is dramatically worse** and "way off from the proper colours", while ordinary
  distant terrain still looks great.

## 7. The cause found for the disappearing colour, and the fix in 0.3.176

`SampleReference` recovered the season pixel by differencing three colour-map calls. Where the two
maps nearly agree, that difference is a few counts of an 8-bit channel wide, so the recovery failed
- and it failed for whole slots at once, because a slot's 64 lattice positions share a climate.
`BuildRamp` then blended against a season pixel that had never been written this pass, i.e. stale
or zero. Colour vanishing in one area and not another is exactly that.

0.3.176 recovers the season pixel using the engine's weight computed from the position's own
temperature instead - stable, one fewer colour call - and makes a negative stored temperature the
single "this slot cannot be rebuilt" signal covering a missing season pixel as well as a missing
temperature, so a slot that cannot be rebuilt publishes the engine's own answer flat rather than
painting from stale memory.

**Unverified.** It is a plausible cause for the band regression too, and it is not proven to be
the only one - see below.

---

## Delivered

- **0.3.172** - the season weight correction, behind `.vhtint weight on|off`, with
  `LodSeasonWeight` and `SeasonWeightChecks`. Correct and inert; superseded.
- **0.3.173** - `.vhtint` gains a `C'` line so the lattice can be measured without the weight
  correction contaminating the comparison.
- **0.3.174** - the height ramp: registry, texture upload, vertex shader, addressing check.
- **0.3.175** - the three self-review fixes in section 5.
- **0.3.176** - the season-pixel recovery fix in section 7.
- Checks: 6,007 assertions at 0.3.172, **12,214** at 0.3.176.
- `dev/plans/PLAN_DISTANT_TINT_SAMPLING.md` amended twice, including the world-height analysis.

## Decisions

- **The ramp is indexed by distance from sea level**, not absolute height and not a fraction of
  world height, because the lapse rate is per block. Rejected alternatives are recorded in the plan.
- **A texture, not uniforms.** 64 slots x 96 steps is 6,144 vec4, past any guaranteed limit.
- **Nearest filtering with a hand-written lerp**, because hardware filtering would blend along the
  slot axis and mix one block's tint into its neighbour's, silently.
- **Do not reproduce the engine's map indexing** to make the ramp cheap, even though reading
  `ColorMap.Pixels` directly would nearly eliminate the sampling cost. That would be a third
  transcription of engine internals in a system already bitten twice (G139, G140).
- **The tropical branch is not reproduced.** Only `tropicalKapok` carries `ExtraFlags: 1`, and the
  engine's CPU path has no tropical branch at all; those slots keep the engine's own weight.
- **Refuse rather than approximate.** Every case the correction cannot answer honestly - tropical
  map, saturated temperature, missing climate, switch off - keeps the engine's answer, and
  `.vhtint` prints which reason applied.

## Traps

**A correction cannot be delivered by a mechanism that does not carry its independent variable.**
Trigger: applying a per-position or per-height correction inside a table sampled at fixed points.
Failure: 0.3.172. Safer: check where the corrected value is *evaluated*, not just whether it is
correct; and prefer a check that reads what the renderer publishes over one that tests the formula.
Candidate for GOTCHAS.

**Differencing 8-bit channels to recover a blend factor fails where the inputs agree.** Trigger:
algebraically recovering one operand of a blend from two engine calls. Failure: section 7. Safer:
compute the factor from its own inputs where they are available, and make "could not compute" a
single explicit signal rather than several independent ones. Candidate for GOTCHAS.

## Open at close

1. **The blend band regression is unexplained and is the priority.** 0.3.176's fix may cover it and
   may not. If it does not, the next suspects in order: (a) the tint ramp is bound to texture unit
   10 and nothing has verified that unit is free of the engine's own samplers in the included
   shader stages - G130 is precisely this class of defect, and the band is the path that also
   samples `seamField` (7) and `blockAtlas` (9); (b) the band's terrain now sees a genuinely
   height-varying tint where before it saw a nearly constant one, so a steep ramp near the seam
   could read as a colour gradient the band never had.
2. **Nothing is committed.** Five versions of work on `codex/seasonal-tint-blend-weight`.
3. **`.vhtint`'s B line still uses the old per-position correction path**, so its "A vs B" and
   "B vs C'" labels no longer describe what the ramp does. It misleads.
4. **Slot sharing**, measured at 38% excess blue on `soil-medium-verysparse`, untouched. Slots are
   keyed on the untinted share rounded to eighths and only 18 of 64 are in use.
5. **`DocCheck` fails on version drift** - STATUS, CHANGELOG and the source-file count are all
   behind. Expected mid-work; the ritual has not been run for this session.
6. **No shader is compile-verified offline**; there is no GLSL validator in the toolchain.
7. Carried from Session 73: the placed-block invalidation bug (G138), the sparse-grass pattern, and
   the grey `soil-low-normal` sections.
