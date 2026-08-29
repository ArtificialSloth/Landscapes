# Session 67 — Surface slabs without the geometry cliff

**Date:** `2026-08-29`
**Branch/commit:** `codex/slab-meshing-performance`; folded into `codex/terrain-slabs-and-near-water` at close
**Mod version:** `0.3.142` human-tested; `0.3.143` makes the accepted mode the default
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

Session 66 implemented exact half-block terrain and priced it at +31.76% ordinary opaque
geometry over 120 real level-0 cache sections. The owner then observed one scene fall from about
300 to 270 FPS and asked for a thorough investigation rather than accepting that a slab should
cost roughly the same as a shorter cube.

The work began from `00276f9`. A named restore point,
`codex/checkpoint-pre-slab-performance-20260829`, was created there. The implementation branch ran
in a linked worktree under `notes/worktrees/slab-meshing-performance`, leaving the original
`codex/terrain-slabs-and-near-water` folder available for another AI. No game process was launched
by the investigator.

## Work narrative

### 1. The shipping GPU stream, not draw calls, carries the regression

`slabfield` was extended to report clustered packed quads, populated draw clusters, face
orientations, and the packed wall faces eligible for an exact early shader exit. On the same
seeded 120-section, level-0, cave-reach-64 sample:

- ordinary opaque quads: 957,524 -> 1,261,653, +304,129 / +31.76%;
- clustered packed quads: 981,109 -> 1,290,355, +309,246 / +31.52%;
- populated draw clusters: 1,851 -> 1,851, no change;
- top quads: +32,424;
- bottom quads: +36,719;
- X/Z wall quads: +117,155 / +117,831.

The FPS loss is therefore credible without any draw-call regression. Greedy meshing makes broad,
same-height full-block terrain unusually cheap. Half-height contours split those planes and expose
234,986 ordinary wall quads that full-cell coverage previously culled.

### 2. Exact geometry alternatives were measured and rejected

Two plausible exact reductions were prototyped before changing the fidelity policy.

An alternate vertical rectangle cover swept distinct Y bands and chose the smaller of that cover
and the established full-span strip merge per material/plane group. It reduced only a few thousand
quads on both sides and changed clustered relative cost from +31.52% to +31.59%, while adding work
to every mesh build. It was removed.

Merging different palette ids when their final packed render material was byte-identical reduced
the slab mesh by only 8,464 ordinary quads, about 0.67%, and helped the no-slab baseline much more.
Relative slab cost rose to +35.34% clustered. It was also removed.

Both results matter because they rule out the tempting claim that the regression was merely a
weak greedy implementation. The expensive faces are real contour topology when every cave and
overhang slab is required to remain exact.

### 3. Surface terrain is the useful fidelity boundary

The cache held 158,754 flagged slab runs in the sample but only 65,624 captured columns ended in a
slab at the landscape surface. A new surface policy shortens only the highest solid run in each
column. Water and thin ground cover above it remain translucent for this decision, so submerged
terrain and flower-covered terrain stay half-height. Lower slabs beneath solid overhangs use the
established full-block LOD approximation.

That policy changed the same sample to:

- ordinary opaque quads: 957,524 -> 1,033,621, +76,097 / +7.95%;
- clustered packed quads: 981,109 -> 1,058,018, +76,909 / +7.84%;
- populated draw clusters: still 1,851;
- flat geometry: +0.30%;
- wall geometry: +12.18%;
- bottom quads: no increase.

The command surface keeps the previous exact behavior available: `.vhslabs surface` is the
low-cost landscape mode throughout all of level 0, `.vhslabs near` is exact for every run at
level 0, and `.vhslabs all` is exact at every level. Level 0 ends at the configured L1 transition,
512 blocks by default. This selection is independent of the much narrower vanilla/cache blend
band.

### 4. Packed walls now avoid work before fixed-function rejection

The packed vertex shader knows a wall's exact axis-aligned orientation and camera-relative plane
before material-table fetches, tinting, warping, seam work, fog, and projection. Fixed-function
back-face culling rejects the same wall only after all of that vertex work has run.

The packed decoder now sends vertical walls outside the clip volume and returns when the camera is
provably behind their X/Z plane. The test is exact even with global Y warping because the warp does
not change which side of a vertical plane the camera occupies. It follows `.vhbackface`, preserving
the existing escape switch. With a section-centre camera model, 123,184 of the exact mode's added
clustered wall quads and 39,154 of surface mode's added wall quads take the early exit. This is a
shader-work estimate, not a measured GPU time.

### 5. Human result and default

Version 0.3.142 was built, verified, copy-installed, and tested by the owner. The owner reported
that surface mode works very well with very little performance cost and directed that it become
the default. Version 0.3.143 therefore starts with surface-only half blocks at level 0. The exact
near/all modes and `.vhslabs off` remain available.

The linked-worktree run also exposed a test-infrastructure defect: static asset checks recognized
only a `.git` directory, so a nested linked worktree's `.git` file made them walk into the primary
checkout and validate the other branch's files. Repository-root discovery now accepts both forms.

---

## Delivered

- A surface-only half-block mesh policy carried on each immutable `MeshJob`.
- `.vhslabs surface`, with exact `.vhslabs near` and `.vhslabs all` preserved.
- Surface-only level 0 as the 0.3.143 shipping default.
- Exact early rejection of packed vertical back faces before the expensive shader tail.
- `slabfield` reporting for clustered quads, clusters, orientations, early exits, and paired
  surface/exact runs.
- Regression coverage for exact buried slabs, surface-only lower slabs, submerged surface slabs,
  shader orientation signs, early-return ordering, and the shipping default.
- Linked-worktree-aware static asset checks.
- Warning-free Release build, 5,628 fast assertions, and 453 documentation checks.
- Verified and copy-installed 14-entry 0.3.143 archive, SHA-256
  `7A8AA4ABE1C789F3CC99C44F74E781C1752B3FB862EE8CDFBE1488BAB991A8DE`.

## Decisions

- The product default is landscape fidelity, not exact distant cave/overhang slab fidelity.
- Exact geometry remains an explicit option rather than being deleted or silently approximated.
- The alternate rectangle cover and render-material merge are rejected on measured payoff.
- The shader optimization partitions no streams and adds no draw commands; it exploits information
  already present in each packed record.
- No protocol, blob, or schema change was needed.

## Traps

- A shorter block can cost much more than a full block under greedy meshing because it destroys
  large same-plane rectangles and changes neighbour coverage.
- Counting only directly buried slabs does not identify the useful fidelity boundary. Most of the
  avoidable bill was lower open cave/overhang surfaces; highest-solid-run classification found it.
- Fixed-function back-face culling does not save vertex-shader work.
- Static checks run from a linked worktree must recognize a `.git` file, not only a directory, or
  they may silently validate the primary checkout.

## Flagged and unverified

- The owner qualitatively reported very little surface-mode performance cost; no controlled FPS or
  GPU-timer number was recorded.
- The exact packed-shader saving is source-traced and harness-estimated. The owner necessarily ran
  the compiled packed shader in 0.3.142, but its contribution was not isolated from the geometry
  reduction.
- The 0.3.143 default wiring is the already accepted 0.3.142 mode selected automatically; that
  specific default startup has not been separately human-run at session close.
