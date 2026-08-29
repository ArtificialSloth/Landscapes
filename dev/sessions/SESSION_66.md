# Session 66 - Terrain slabs, and the near field's water

**Date:** `2026-08-28`
**Branch/commit:** `codex/terrain-slabs-and-near-water`, branched from `a55fc5b` on `codex/seam-blending-and-block-textures`
**Mod version:** `0.3.140` and `0.3.141`; both built, verified, and copy-installed. Neither human-tested at close.
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The owner opened the session on the two items he had queued: make the mod compatible with
**Terrain Slabs**, the half-block terrain mod his world was generated with, and stop cached
water being drawn everywhere.

Neither had a symptom description. Both were settled offline before any source changed.

### 1. What Terrain Slabs actually breaks

The owner's live client cache was decoded directly rather than reasoned about. The mod's
blocks are captured, coloured and classified **correctly**: `terrainslabs:soil-game-low-normal`
carries byte-identical colour and flags to `game:soil-low-normal`, and the same held for every
pair checked across rock, gravel, sand, forest floor and snow. Nothing about capture, palettes,
tints or textures is wrong.

The defect is height, and only height. Runs are stored as whole blocks
(`paletteId | yTop | yBottom`), so a block filling the lower half of its cell is rebuilt as a
full cube: the LOD puts back exactly the stair-steps the mod exists to remove, and stands the
ground half a block high wherever a slab sits. On the owner's world that is **13.9% of all
captured surface columns** and about **31% of soil-family surface columns** - not a corner case.

### 2. Why cached water was drawn over the game's own

Source-traced through the decompiled client. Cached terrain registers at
`EnumRenderStage.Opaque` at the default order 0.5; vanilla's terrain is `ret-op` at 0.37, so the
mod correctly draws after vanilla's ground. But vanilla's **liquid pass is not in that stage**:
`SystemRenderTerrain` renders `poolsByRenderPass[4]` from `RenderOIT`, in the later OIT stage.
The engine leaves `GlDepthMask(true)` on through the opaque stage and the mod never clears it,
so cached water writes depth *before* vanilla's water is drawn and depth-rejects it.

Cached water was exempt from every suppression rule (G124), and the 48-block near-field radius
had defaulted to zero in 0.3.139. Nothing was left to keep it off the camera, so the cache's
flat sheet stood in front of the real animated water on every lake the player could see.

## Work narrative

### 3. Half-block terrain (0.3.140)

`LodPaletteEntry.FlagHalf` takes the free flag bit 2. `LodBlockPolicy` sets it from the block's
own collision shape - a single box with the full horizontal footprint, standing on the cell
floor, no taller than half a block - so no mod is named anywhere and a shallow snow layer is
caught for free. Flags are recomputed from the live block on load (`LodStore.Reclassify`), so an
existing cache gains half blocks with no re-exploration and nothing is re-captured.

The mesher was converted to **half-block Y units** throughout. The alternative - shaving the
surface alone - was rejected on inspection: coverage is computed from run spans, so a slab that
reports full height while drawing half of one culls its taller neighbour's wall and opens a
see-through notch into solid ground. Doubling once at two accessors keeps the rest of the file
integer interval arithmetic; only the emission points halve back. A `short` holds every height a
14-bit run can carry for any world up to 8,191 blocks.

The behaviour is gated per detail level and carried on the job, not read from a static, for the
same reason the cave reach is: a switch flipped mid-flight must not build one world two ways.

### 4. Measuring it: `slabfield`

A new offline harness, modelled on `CaveField`, meshes real cache sections **twice with the
shipping mesher** - flag off, then flag on, with the four neighbours flagged to match - and
reports the difference. It matches slabs by stored palette-code prefix, which is a stand-in for
the shape test and is stated as one.

Over 120 real level-0 sections at cave reach 64:

| | baseline | half blocks | change |
|---|---|---|---|
| opaque vertices | 3,830,096 | 5,046,612 | **+31.76%** |
| of which flat surfaces | 1,365,708 | 1,642,280 | +20.25% |
| of which walls | 2,464,388 | 3,404,332 | **+38.14%** |
| water vertices | 102,832 | 105,812 | +2.90% |

Coarse levels measured +40.60% (L1), +33.48% (L2) and +22.98% (L3), so the gate defaults to the
near band only, on the same argument that already restricts block textures to level 0.

The same harness with the flag off produced numbers **bit-identical to the baseline** across 120
sections including the cave-boundary path, which is the regression proof for the unit rewrite.

### 5. The owner's frame rate, and what he actually wanted

He reported **300 to 270 FPS** in one scene and asked whether there was a cheaper way.

The breakdown above answers it: **77% of the extra geometry is walls**. Wherever a slab sits
beside a full block, that block must now draw the top half of its side face - previously culled
outright, because the slab claimed the whole cell and looked like complete cover. That wall *is*
the half-step. There is no cheaper way to draw one. The one genuinely wasted case - slabs buried
under another block, where the gap is sealed inside solid ground - was measured at 12,134 against
146,620 exposed, so harvesting it saves nothing worth having.

He then supplied the symptom the TODO had never carried: *"on the mesh overlap between vanilla
and LOD, the blocks that are slabs in vanilla are overlapped by full blocks from the LOD, and it
looks bad"*, and said that fixing that alone would be acceptable.

That is a different problem with a free answer. Cached solid geometry is already lowered by a
constant so it loses depth fights with vanilla where the two overlap - `bandSinkBlocks`, added in
0.3.139 for lake floors, currently one sixteenth of a block. The pokethrough is half a block.
`.vhband sink 8` is exactly that, costs no geometry, needs no rebuild, and moves every cached
surface together so the only place it can be seen is the overlap it is for. Handed to the owner
as the first thing to try; **not yet tested**.

Half-block terrain therefore ships **off by default**, retained as the honest version behind
`.vhslabs`.

### 6. The water fix was wrong, and the log said why

0.3.140 hung water suppression on the per-cell ownership mask, on the reasoning that 0.3.127 had
pushed `chunkliquid`'s fade out with the other four terrain programs and so removed the ring
G124 documents. **That reasoning was wrong**: G124's dry riverbeds were observed and fixed in
session 65, after 0.3.127 shipped. The ring is real and current.

The owner reported no change. His client log gave the mechanism immediately:

```
vanilla readiness: ... 515 ready/0 observed/2953 pending/0 unknown ...
33506 ownership denied no-geometry/59798 denied beyond view distance ...
mask 104 owned/48 KiB/97 uploads
```

Ownership is proven per 32-block cell only where the engine reports a chunk both drawn *and*
holding geometry. **104 owned cells against 2,953 pending** is far too sparse to stand between
the player and the lake in front of him, and it never will be dense enough for that job.

0.3.141 replaces it with a plain radius. Cached water is discarded within
`WaterNearFraction` (0.70) of the engine's view distance, proving nothing and asking nothing.
The bound is read from the engine's own shader: `chunkliquid.vsh` clamps the liquid alpha on
`length(worldPos.xz) / viewDistance`, holding its ceiling to 0.725 and reaching zero at 0.8,
while the ground keeps drawing past 1.0. Everything past 0.8 is the ring where the engine draws
a bed and no river, so the cutoff has to stop short of it. Water is restored to **full exemption
from the ownership mask**, so G124 stands exactly as written.

## Verification

- Warning-free Release builds of both versions.
- **5,622 assertions, zero failures.** New coverage: half-block surfaces, the see-through-notch
  case, the buried-slab gap, the level gate, half-block classification including the false
  positives that matter (posts, narrow boxes, top slabs, water), and the water near radius
  against the engine's own fade ratio at seven view distances.
- Static asset checks updated to pin the new shader conditions and their renderer uploads.
- Mesher bench unchanged: fixtures carry no half-block flags, so the added multiply costs nothing
  measurable.

---

## Delivered

**0.3.140** - built, verified, copy-installed.

- `LodPaletteEntry.FlagHalf` on free bit 2; `LodBlockPolicy` sets it from collision shape.
- `LodMesher` converted to half-block Y units: surfaces, walls, both coverage paths.
- `MeshJob.HalfBlockLevels` with a per-level gate; `LodTerrainRenderer.HalfBlockLevels`.
- `.vhslabs off | near | all | <n>`, remeshing on change and reporting live vertex totals.
- `tests/VintageHorizons.Checks/SlabField.cs`: offline half-block cost harness over a real cache.
- Water tied to per-cell ownership inside half the view distance. **Superseded; did not work.**

**0.3.141** - built, verified, copy-installed.

- Cached water suppressed by a near radius, `LodNearHandoff.WaterNearRadius`, defaulting to 0.70
  of the engine's view distance. Water fully exempt from the ownership mask again.
- `.vhwater off | on | <fraction>`, effective immediately with no rebuild.
- Half-block terrain defaults **off**; `.vhslabs near` is the level-0 setting.

**Documentation** - this record, index row, changelog, G125-G127, the blob-flag ledger note,
TODO/DONE, and regenerated status.

## Decisions

- **Half-block Y units through the whole mesher, not a surface-only shave.** Coverage is computed
  from run spans; a run that reports one height and draws another culls its neighbour's wall and
  opens a see-through notch. Source-traced before implementation, not discovered in play.
- **The flag is derived from collision shape, never from a block code or mod id.** It costs
  nothing, works for any slab mod, and picks up shallow snow layers. Rejected: matching the
  `terrainslabs:` domain, which would have been exact for one mod and blind to every other.
- **Level-gated and off by default.** +32% opaque geometry at level 0 is the owner's call to
  make, and the symptom he actually reported has a zero-cost answer in `.vhband sink`.
- **Water yields by radius, not by ownership.** Measured, not assumed: 104 owned cells against
  2,953 pending. Ownership is the right tool for ground, where the mask has time to commit and a
  wrong answer costs a correction; it is the wrong tool for anything the player is standing next
  to.
- **0.70 of the view distance**, from the engine's own alpha clamp, with 0.10 of margin below the
  point its water starts disappearing. A knob, because only a person looking at a distant river
  can confirm the margin is enough.
- **G124 not reversed.** Water stays exempt from ownership in both the shader and the
  whole-section skip. The near radius is a separate mechanism at a distance the gotcha does not
  cover.

## Traps

- **Ownership is far too sparse to be a per-pixel switch for the near field.** Trigger: reaching
  for the readiness mask to hide anything the player is standing beside. Failure: the change does
  nothing and looks like it did not ship - 104 owned cells against 2,953 pending in a real log.
  Safer: read the readiness line of the periodic log before designing against ownership, and use a
  distance where density matters more than correctness. Recorded as G125.
- **The engine draws its water in a later render stage than the mod draws anything.** Trigger:
  any cached surface that writes depth and has a vanilla counterpart. Failure: cached water
  depth-rejects vanilla's own, silently, with no error and no missing geometry to notice. Safer:
  check which stage the vanilla counterpart renders in before assuming "we draw after vanilla".
  Recorded as G126.
- **Half-block terrain costs walls, not surfaces.** Trigger: costing any change that alters
  coverage rather than adding faces. Failure: quoting the surface delta and being surprised by the
  total - the flat delta was +20% and the walls +38%. Safer: split the measurement by face
  orientation before quoting a number. Recorded as G127.
- **A gotcha is only expired if the evidence post-dates the thing that supposedly expired it.**
  0.3.127 was argued to have removed G124's ring; the dry riverbeds were observed in session 65,
  after it. Cost one wrong build. Check the date of the observation, not the date of the fix that
  should have covered it.

## Flagged and unverified

Judgement calls awaiting human review:

- Half-block terrain defaults off. If `.vhband sink` does not close the overlap complaint, the
  default should probably become `near`.
- `WaterNearFraction` at 0.70. Raise if flat cache water still shows on a near lake; lower if a
  distant river goes dry.
- `.vhband sink 8` as the answer to the slab overlap. Proposed from the mechanism, untested.

Claims that lack the evidence level to be treated as established:

- **Neither 0.3.141 change is human-tested.** The water radius is source-traced and
  harness-tested; the slab default is a measurement, not a playtest.
- The depth-stage explanation for cached water hiding vanilla's is **source-traced only**. It has
  not been confirmed by a run in which only the depth behaviour changed.
- The half-block cave-boundary coverage branch has **no dedicated check**. Its non-slab behaviour
  is regression-covered by 120 bit-identical real sections; its slab branch is not.
- The +31.76% figure is one seeded sample of 120 level-0 sections of one world. No frame-time
  benchmark was run; the owner's 300-to-270 observation is a single uncontrolled scene.
