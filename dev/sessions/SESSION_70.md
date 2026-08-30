# Session 70 — The seam holes, and what the overlap margin was actually hiding

**Date:** `2026-08-30`
**Branch/commit:** `codex/exact-vanilla-draw-range`, on top of `cc1ac3b`
**Mod version:** `0.3.161`-`0.3.163`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

The owner reported, after running `.vhband overlap 0`, that the seam between cached and vanilla
terrain carried small holes: usually part of a block, almost always a vertical face, and always
open to the sky. The brief was investigation only, no code.

Two things made this tractable that had not been available in earlier hole-hunts. The owner's own
configuration is on disk and says the vanilla view distance is **64 blocks**, so the seam sits
around 50-67 blocks out — confirmed by the client log's `range exact to 51 blocks` and 22-25 owned
mask cells. That places the whole seam inside level 0, where the cached blocks and vanilla's are
the *same blocks*, which eliminates every "the LOD is a coarser shape" explanation before it is
proposed. And the owner had already run the four discriminating tests in play, including the
directional one, so the diagnosis below was checked against observation rather than only against
source.

Out of scope and left alone: the blend band's own addressing, which carries the same off-by-one at
8-block granularity but affects colour only; and leaves, which share G137's defect and are far
denser than plants.

## Work narrative

### 1. The addressing defect (G136)

Ownership is a per-fragment test: `cellX = maskSectionOriginX + int(floor(sectionLocal.x / 32.0))`.
That is exact for a fragment inside a block and undefined for one lying on the boundary between
two. `LodMesher.EmitVerticalMerged` places a west wall at `Fixed * step` and an east wall at
`(Fixed + 1) * step`, so an east-facing wall standing on a chunk plane is emitted at the next
chunk's origin and floors into it.

Consequence, worked through per side: on the **-X and -Z** seams the mis-charged wall is the one
facing the player, its own chunk is outside vanilla's draw range, and it was discarded against
nothing — a hole. On **+X and +Z** the same mis-charge draws a wall belonging to vanilla's cell,
which points away and is back-face culled, so it is invisible. That asymmetry is the fingerprint:
no other rule in the renderer distinguishes +X from -X.

This also explains the 16-block overlap margin's whole career. The margin stops ownership half a
chunk short of vanilla's real edge, so a wall mis-charged one chunk inward still lands on ground
vanilla is drawing and is covered. `dev/TODO.md` had carried it since Session 65 as *"a
mitigation… whether loss detection itself is too slow was never established"*. It was never about
loss detection.

The fix originally sketched — bias each face's lookup half a block inward along its normal — was
rejected during implementation. Only the packed path carries a face id (`vhFace`); the expanded
path would need a vertex-format change. Worse, a *uniform* inward bias is exactly correct for
east/south/top faces and exactly wrong for west/north/bottom, so it moves the hole to the opposite
seam. What shipped instead is positional and path-independent: snap a coordinate that lies within
1/32 block of a chunk plane onto it, and ask **every** cell that could hold the block behind the
fragment, hiding the fragment only when they all agree. One `texelFetch` for an ordinary surface,
two for a wall in a plane, four on a chunk corner. It is conservative in the direction the project
already prefers, it fixes all three render paths from one shared file, and it removes a latent
hazard: a constant vertex attribute is not promised to interpolate to the last bit, so which side
of the plane a wall floored onto was hardware's choice.

A latent compile break was fixed in the same block: the `.vhpaint` branch wrote `outGNormal` and
`outGPosition` without the `SSAOLEVEL > 0` guard the branch below it has, so the diagnostic needed
to verify this work would have failed to compile the shader for any player with SSAO off.

### 2. The coverage defect (G137)

0.3.162 closed most of it. The owner then reported two residual cases at the seam, and identified
the second block himself as **flax** after an initial guess of tall grass.

A palette scan of the live cache settled what crops are: **4,855 crop palette entries, 100% stored
as solid cubes, at every growth stage, in 42% of level-0 sections**. Flowers and ferns are the only
plants drawn as mats, which is why they could not have been what was seen — a mat emits no vertical
face at all.

Mechanism, fully source-traced: `topCovered` drops the soil's top face because the crop run above
it is opaque and non-thin, and `CollectSide` drops the wall between two adjacent crops for the same
reason. Both drops are correct inside cached terrain and invisible. At the seam vanilla draws its
own crop as a transparent tuft, and the two dropped faces become a window through the hollow
cached shell — the cached mesh is a surface, not a volume, so there is nothing behind them.

The classification rule deliberately is **not** a name list. The game's own `crop.json` carries
`sideopaque: {all: false}` and `renderpass: "OpaqueNoCull"`, and `Block.AllSidesOpaque` is the
property vanilla's own tesselator consults, so `LodBlockPolicy` reads that under the existing Plant
material guard. The guard is load-bearing: leaves, glass, fences and stairs are non-opaque too, and
freeing all of them would wall every pair of leaves in every canopy.

`FlagCutout` takes free flag bit 4. No blob-format bump, on the two grounds `FlagHalf` established
in 0.3.140 and both re-checked here — no shipped reader tests bit 4, and `LodStore.Reclassify`
re-derives flags from the live block on every load, so the owner's existing 133 MB cache gains the
fix with no re-exploration.

### 3. Pricing it

Per G127, a coverage change spends its bill on walls and the vertex total will not say so. A new
`cutoutfield` harness meshes real cache sections twice with the shipping mesher — one flagged
input, the job knob as the only difference — and splits the result by face orientation.

Four samples of 120 level-0 sections: **+2.19%, +2.29%, +2.57%, +4.37%** opaque geometry, landing
on walls (+2.85% / +2.68%) with tops nearly free (+0.62%), and **draw clusters unchanged at +0.00%
in every sample**. For scale, the accepted slab work was +7.84% and the rejected exact-slab mode
+31.52%.

### 4. The margin's default

With both defects fixed the owner directed `.vhband overlap` to default to 0, which is what he had
been setting by hand. The doc comments that quoted "62 blocks at the defaults" and "16 blocks at
the defaults" were corrected rather than left to rot; the derived water dilation already resolves
to zero at a zero ring and needed no change.

### 5. What was investigated and deliberately not fixed

The owner also reported vanilla tree textures on treetops inside an otherwise-cached chunk, with
`.vhpaint` confirming the mask suppressing there. This is not a defect. `TryReadDrawBounds` reads
each 32³ chunk's own bounding sphere, whose centre is the geometry extents midpoint, so two
vertically stacked cells in one column can have centres up to 32*sqrt(2) apart — decisive at a
64-block view distance. Vanilla really is drawing the canopy and really is not drawing the trunk;
the mask reads both correctly. The only safe correction is to deny a column whose cells disagree,
which reinstates most of the 46-block slack the exact rule exists to remove, and only for the
sparse-geometry columns that produce trees. `.vhexact off` already demonstrates the trade in one
command. Recommended for acceptance and accepted.

---

## Delivered

**0.3.161** — source only, superseded before it was played. Carried the shader fix with a
parameter named `local`; renamed for clarity, which required a new identity per the artifact rule.

**0.3.162** — the addressing fix. `lodterrainbody.fsh` gains `MASK_PLANE_SLACK`,
`maskPlaneOffset` and `vanillaOwnsCell`; ownership is decided from every candidate cell rather
than one. `.vhpaint`'s SSAO guard corrected. `StaticAssetChecks.OwnershipMaskWiring` extended to
pin the plane rule and the slack constant. Built, verified, copy-installed, human-tested and
reported as excellent.

**0.3.163** — the coverage fix. `LodPaletteEntry.FlagCutout` (bit 4);
`LodBlockPolicy.IsSeeThroughPlant` from `Block.AllSidesOpaque` under the Plant guard;
`LodMesher.IsCutoutRun` applied to top cover, bottom cover and both side-coverage paths;
`MeshJob.CutoutBlocksCover` and `LodTerrainRenderer.CutoutBlocksCover` as the comparison switch;
`.vhcutout` reporting the live vertex total. New `cutoutfield` harness. Six new mesher assertions
covering both directions plus a solid-block control. Built, verified, copy-installed.

**Post-0.3.163 source** — `OverlapMarginBlocks` defaults to 0; stale default figures in three doc
comments corrected; `.vhband overlap` help text rewritten. Deliberately **not** packaged: the
owner directed no new artifact for a default change, so the installed 0.3.163 zip still starts at
16 and the runtime command remains the way to reach 0 in that build.

5,896 assertions, zero failures.

## Decisions

- **Positional ambiguity rule over a face-normal bias.** The bias is exact but needs a face id the
  expanded vertex format does not carry, and a uniform bias moves the hole rather than closing it.
  The positional rule fixes all three paths from one file and is provably hole-free at boundaries.
- **`Block.AllSidesOpaque` over a code-prefix list.** The engine's own occlusion answer, matching
  the precedent `IsBottomHalfBlock` set for shapes. The prefix list stays only for thin cover,
  where it decides how a block is *drawn* rather than what it covers.
- **Plant material guard retained**, excluding leaves, on geometry cost. Leaves have the same
  defect and are deferred to a measurement of their own.
- **Cutout blocks keep their cubes.** The owner was offered mats — which would have fixed the hole
  while *reducing* geometry — and chose no look change, accepting the geometry cost instead.
- **Half-vanilla trees accepted as correct behaviour**, not worked around.
- **No blob-format bump for `FlagCutout`**, against 0.3.140's two stated conditions.

## Traps

Promoted to `dev/GOTCHAS.md` as **G136** (a face lying in a cell boundary belongs to neither cell
until you say which) and **G137** (drawing a block as a cube does not entitle it to occlude like
one). Two more worth keeping here:

- **A mitigation with no established mechanism is a bug with a bandage on it.** The 16-block
  margin survived six releases and its own TODO entry saying the mechanism was never established.
  When a knob's value is justified only by "it closed the symptom in play", the symptom is still
  unexplained and the knob is hiding it.
- **Guessing the block is expensive; scanning the cache is cheap.** Tall grass was the confident
  guess and flax was the answer. A palette scan of the live cache took one command and would have
  said so first — flowers and ferns are already mats, so a plant with a *vertical face* was never
  going to be one of them.

## Flagged and unverified

Judgement calls awaiting human review:

- **The 2.2-4.4% geometry cost is unbenchmarked in game.** It is measured offline against the real
  cache with the shipping mesher, and draw clusters do not move, but no frame-time comparison
  exists. `.vhcutout on` is the A/B.
- **`OverlapMarginBlocks = 0` as a shipped default** rests on two fixes, one human-tested
  (0.3.162) and one not (0.3.163).

Claims lacking their evidence level:

- **0.3.163 is not human-tested.** Built, verified, copy-installed.
- **No shader in this session was compile-verified offline**; there is no GLSL validator in the
  toolchain. 0.3.162 loaded in game, which is the only proof available and is proof.
- **The +4.37% sample is 2x the median** and no cause was established for the spread; four samples
  is a small n (G98).
- **Leaves are believed to carry G137's defect** by source inspection only. No seam hole on a tree
  has been observed or measured.
- **The blend band's 8-block address carries G136's off-by-one** and was left alone as
  colour-only. Not verified as harmless in play.
