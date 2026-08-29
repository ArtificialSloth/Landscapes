# Session 68 — The water seam, and a sampler the engine took back

**Date:** `2026-08-29`
**Branch/commit:** `codex/water-seam-transition`, branched from `94cde0e` on
`codex/terrain-slabs-and-near-water`. Checkpoint of the pre-session tree preserved as
`codex/checkpoint-pre-water-transition-20260829`.
**Mod version:** `0.3.144` through `0.3.152`
**Assist protocol / blob / schema:** `1 / 4 / 6` (unchanged)

## Context and investigation

The owner reported that 0.3.141's near-field water suppression drew a hard circle across every
lake — "a GPU rendering effect and not proper game chunk boundaries" — and asked for the earlier
behaviour back, where cached water stopped at vanilla chunk boundaries. The session widened
once into making water *transition* well rather than merely stop, and then narrowed hard onto a
single rendering defect that had been misattributed three times.

Two things were established before any code changed.

**The engine draws its water in a later stage than this mod draws anything.** Confirmed in the
decompiled client rather than assumed: `SystemRenderTerrain` registers `OnRenderOIT` at
`EnumRenderStage.OIT`, and `ClientPlatformWindows.LoadFrameBuffer(Transparent)` enters that
stage with `GlDepthMask(false)` and the depth test still on, reading whatever the opaque stage
left behind. So cached water writing depth in the opaque stage depth-rejects vanilla's own
surface everywhere the two overlap. G126, now corroborated in the engine's own source.

**The bound 0.3.141's radius was derived from does not exist while this mod is running.**
`chunkliquid.vsh` clamps liquid alpha on `length(worldPos.xz) / viewDistance`, holding to 0.725
and reaching zero at 0.8 — but `VanillaHorizonEffects` rewrites that same `viewDistance` uniform
on every `Use()` to `ceil(lastDrawableVertex / 0.725)`, which places the fade beyond the
furthest vertex the engine's culler will submit. At a 512 view distance the liquid program is
told 754. Vanilla's water therefore never fades; it draws at full alpha to its chunk footprint
and stops. The 0.70 fraction was stopping short of a ring that is not there.

## Work narrative

### 1. The root fix, and what it makes possible

Cached water now draws with depth writes disabled, restored in a `finally`. The depth *test*
still runs, so cached water is still occluded by anything nearer and still draws after the
opaque pass; what changes is that vanilla's water is allowed to draw on top of it. That inverts
every downstream failure mode. Before, a cell ownership had not yet proven meant a flat cached
sheet standing in front of the real animated surface. After, it means a faint tint underneath
one — which is what made it safe to put water back under the ownership mask at all, where
0.3.140 had found the mask too sparse to matter.

### 2. Matching vanilla's water instead of hiding from it

Three terms were transcribed from the engine's own shaders rather than approximated.

- **Opacity.** `chunkliquid.vsh`'s grazing-angle term, `max(0.2, 0.01 + 4.5 * (1 + dot)^3)`
  scaled by 0.8. About 16% opaque looking straight down at a pond underfoot, clamped fully
  opaque by roughly 25 blocks out. Cached water was a flat 0.66 at every distance and angle,
  which is why the cached lakebed showed through at the seam where vanilla draws solid. Applied
  to every cached water fragment and deliberately not on the band: it describes the surface
  rather than correcting it, and fading it in would be a fresh seam.
- **Specular glint** and **open-water foam**, both from `chunkliquid.fsh`'s cold-liquid branch
  for the case a distant lake is in — still water, so the flow vector is zero, and open water,
  so its depth-difference term is zero and drops out exactly. All inputs are public API:
  `ShaderUniforms.SunPosition3D / SunSpecularIntensity / WaterWaveCounter / WindWaveCounter /
  WaterFlowCounter / WindSpeed / PlayerPosForFoam` and `Calendar.ReflectColor`. `gnoise` is
  already included by the mod's fragment shader, so the noise is vanilla's own function, and
  the foam coordinate uses the engine's own re-based foam anchor, putting our noise in exactly
  vanilla's phase.

These two were band-weighted at first, to bound the per-pixel cost. **That was wrong and the
owner saw it immediately:** a term that fades out and then stops draws a line where it stops.
Both are now carried everywhere cached water draws, which is also the more faithful answer —
vanilla's water is shiny and grainy to its own horizon.

Vanilla's *shoreline* rim is not reproduced. It is derived by sampling the depth texture during
OIT; this shader runs in the opaque stage where that buffer is the live render target, and
sampling an attachment while it is bound is undefined. It has to come from mesh data instead.

### 3. Three wrong explanations for one square

The owner reported an "enormous square" under `.vhband shape on`, and this consumed most of the
session. Recorded in full because each wrong answer was reached by plausible reasoning.

**First wrong answer: the band saturates.** Reasoning: ownership commits as a scatter, so
distance-to-nearest-owned-column collapses to zero and the band paints its whole window at full
strength. Disproved by reproducing the owner's exact configuration offline through the shipping
distance-field code — the result is a clean ring with a full gradient that reaches zero inside
its window.

**Second wrong answer: a circle seen in perspective.** Disproved by sampling the screenshot's
pixels: the boundary traces two perfectly straight lines, 31 pixels of rise per 80 across, all
the way to the apex. A circle cannot project to that.

**A real defect found on the way, which was not the cause.** `SeamFieldMarginCells` sized the
field's margin from `BandWidthBlocks` alone, while the shader ramps on
`seamCells * 8 - bandInnerBlocks`, so the ramp needs `width + inner` — 174 blocks against a
margin sized for 136. The ramp was being cut while still carrying a weight of 0.31, which is a
straight-edged step on the window's own boundary. Fixed and pinned across every width and inner
setting the commands accept. It does not bite at the owner's view distances, where the ramp
finishes with room to spare.

**The measurement that settled it.** Counting hues in the screenshot: 234,832 red pixels
against 37 yellow and 6 blue. The band's shape view is a ramp, so half its area should be
yellow-to-blue. The fill was flat, at one value, and not the band's ramp at all.

**The instrumentation that answered it.** Two changes made the next screenshot decisive: the
shape view took two palettes, ground ramping blue-yellow-red and water and thin cover ramping
dark-to-bright green, and the mask's own debug paint became magenta so it could never again be
mistaken for the band's red. The owner reported the square turned **green on water but stayed
red on flowers** — and flowers are thin cover, which is drawn in the same pass as water. The
square was not "water"; it was *everything drawn by the water pass*.

### 4. The actual cause

`ShaderProgramBase.Use()` re-binds every sampler the shared includes declare — `shadowMapFar`,
`shadowMapNear`, `sky`, `glow`, `liquidDepth` — to whichever texture unit the linker assigned
that name **in that program**. Those numbers are per program, and this mod's three variants
declare different sampler sets. Texture unit bindings are global GL state.

The opaque pass never saw the consequence: `DrawIndirectBucket` re-applies the frame uniforms
immediately after activating the packed program, which rebinds everything. The water pass did:
`prog.Use()` in that method's `finally` re-ran the engine's binds, and nothing re-applied
anything between there and the water draws. The seam field sampler was left pointing at another
texture, read approximately zero, and the shader reads zero as *distance zero from vanilla* —
full band strength, across the whole field window and nowhere else. A flat square, on water and
flowers, with correct ground beside it.

`RebindOwnSamplers` now runs after the program switch and again at the head of the water pass.

### 5. Removing the last radius, then giving water its own footprint

With the square gone the owner identified what remained: cached water was still drawing inside
vanilla's chunks. The cause was mine — `waterMaskRadius` barred ownership from hiding water past
0.725 of the view distance, so beyond that ring nothing suppressed it. That allowance, its
`waterMaskBounded` switch and the `.vhwater bound` command are deleted rather than retuned. A
radius cannot express a chunk boundary, so there is now no distance-from-camera term anywhere in
the water path.

The owner then asked whether water could give back the *overlap* ground keeps. Ownership is
refused for `SphereCentreSlackBlocks` (46) plus `OverlapMarginBlocks` (16) inside vanilla's real
edge so cached ground can cover any chunk the engine has quietly stopped submitting. That is a
hole guard and it earns its cost; water has no such stake, because missing water is local and
obvious while cached water inside live terrain is not.

Water therefore got **its own ownership footprint in the mask's green channel** — the texel
always had three unused channels, so this costs no memory, no second texture and no second
lookup. Water's footprint is the ground footprint dilated by whole chunk columns with a
**Chebyshev** structuring element, so corners stay square and the boundary still lands on chunk
edges; a Euclidean or chamfer dilation would round it onto a smooth curve. Dilating also closes
the uncommitted pockets inside the footprint, which a range test would leave open.

The dilation was two chunks and **overshot by exactly one**, reported as a chunk-wide strip with
no water in it. The refusal compares a column's *nearest* face against the threshold, so the
outermost owned column already reaches a full chunk further out; only `62 - 32 = 30` blocks
remain, which is one chunk.

### 6. Diagnostics

Added because three builds of screenshots could not answer questions the log could.

- `.vhband map` writes two pictures to the client log: the committed ownership footprint per
  chunk column, and the band weight the shader computes from it, plus the weight the field's
  outer ring ends on — the number that separates a fade from a cliff and which nothing on screen
  shows.
- The periodic report carries every water setting and both debug paint states.
- `.vhwater` with no arguments writes its settings to the log, because chat cannot be copied out
  of the game.

---

## Delivered

**0.3.144** — cached water stops writing depth; water back under the ownership mask bounded by
the liquid fade radius; vanilla's opacity term; glint and foam band-weighted; near cutoff an
absolute 48 blocks.

**0.3.145** — band margin covers `width + inner`; glint and foam carried everywhere; the block
near cutoff replaced by an off-by-default Chebyshev chunk count; `.vhband map`.

**0.3.146** — `.vhwater bound` switch over the outward allowance.

**0.3.147** — `.vhband map` also dumps the ownership footprint.

**0.3.148** — two-palette shape view, magenta mask paint, water settings and paint states in the
log.

**0.3.149** — `RebindOwnSamplers` after a program switch and before the water pass.

**0.3.150** — the outward allowance, its switch and its command removed; one ownership test for
every band at every distance.

**0.3.151** — water's own dilated footprint in the mask's green channel.

**0.3.152** — the dilation derivation corrected for the nearest-face measurement; `.vhwater
edge` overrides it live.

Tests: `BandShapeChecks`, `WaterFootprintChecks`, `WaterEdgeChecks`, plus static checks pinning
the sampler rebind sites and their ordering, the two ownership channels, the single ownership
lookup, and the absence of any camera-distance term in the water path. 5,730 assertions, zero
failures. Warning-free Release build.

## Decisions

- **No radius anywhere in the water path, at any size.** Rejected twice in play as a GPU effect.
  The unit for the remaining optional near rule is whole chunks so its boundary cannot leave the
  grid, and it is off by default.
- **Glint and foam everywhere rather than band-weighted.** The cost argument lost to the fact
  that a term which ends draws a line where it ends.
- **Opacity everywhere, never band-weighted**, for the same reason in the opposite direction.
- **Two ownership footprints in one texel** rather than a second mask texture or a laxer shared
  rule. Ground's overlap is a hole guard and must not move; water's must.
- **Chebyshev dilation**, not Euclidean, so the footprint stays chunk-shaped.
- **G124's allowance deleted rather than retuned.** The ring it guards is pushed out of range by
  this mod's own horizon hook; if dry riverbeds return, the answer is a better ownership signal.

## Traps

- **`Use()` rebinds the shared includes' samplers, per program, into global state.** Any path
  that activates a program without re-applying its textures draws with the engine's. Promoted as
  G130.
- **A distance test is a circle wherever it is measured from.** Promoted as G131.
- **A chunk-level refusal measured to the nearest face already covers one chunk.** Promoted as
  G132.
- **A screenshot cannot distinguish a gradient from a cliff at a shallow angle**, and cannot
  attribute a colour to a feature when two features paint the same colour. Both were fixed by
  instrumentation, not by looking harder.

## Flagged and unverified

Judgement calls awaiting human review:

- `.vhwater edge` derives 1. The owner tested 0.3.151 (which derived 2) and reported the
  one-chunk gap; **0.3.152 is not human-tested.**
- Glint and foam strengths default to 1.00 and have not been tuned in play.

Claims lacking the evidence level to treat as established:

- **No shader in this session was compile-verified offline.** There is no GLSL validator in the
  toolchain; the only proof any of these shader edits compile is the game loading them. 0.3.144
  through 0.3.151 did load.
- **G124 may return.** Water is now under ownership at every distance. The reasoning that its
  ring cannot exist rests on the horizon hook working, and that hook fails open per pass.
- **The blend band is circular by construction, and this session did not fix it.** Ownership is
  refused by a distance rule, so its footprint is a disc of chunks; and a chamfer distance field's
  level sets are smooth curves regardless of how blocky the seed is — the code chooses chamfer
  weights specifically to stay near-Euclidean. Confirmed from the owner's live `.vhband map`:
  concentric rings, full strength to ~190 blocks, fading to nothing at ~310. The owner accepts
  the band as it stands; recorded so it is not rediscovered.
- No benchmark was run. Cached water now shades glint and foam on every water fragment, roughly
  six `gnoise` calls each, where it previously shaded none.
