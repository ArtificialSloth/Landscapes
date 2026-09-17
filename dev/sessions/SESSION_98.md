# Session 98 — Farseer handoff regressions and mesh-exact suppression through 0.4.101

**Date:** `2026-09-17`
**Branch/commit:** `codex/water-depth-light` at `a137181`
**Mod version:** `0.4.95–0.4.101`; 0.4.101 built, verified and copy-installed; not yet human-tested
**Assist protocol / blob / schema:** `1 / 4 / 6`; both silhouette stores remain schema `1`

> Session records are Tier 3 history. This record follows Session 97's first buried join and
> records the successive visual corrections that established the current Farseer ownership model.

## Context and investigation

The 0.4.94 buried join misunderstood the desired shape. It added visible bands around cached
terrain and tried to make the whole Farseer surface meet real terrain too exactly. The owner
clarified the intended result: Farseer is a distant backdrop, not another detailed terrain system.
Only its edge near loaded vanilla or LOD terrain should slope down far enough to avoid a sky slit;
the distant field must keep its representative height instead of becoming a sunken basin around a
flat LOD plateau.

The owner then made overlap suppression non-negotiable after digging into vanilla terrain exposed
Farseer below it. This exposed three different meanings of “terrain exists”:

1. terrain data is present somewhere in the cache;
2. a particular LOD mesh has been installed and can actually draw;
3. that mesh covers a particular piece of ground.

Using the first answer hides Farseer before its replacement appears. Using current-frame or
frustum-selected draw commands makes ownership change with camera yaw. Using only level-0 mesh
readiness misses the coarser resident parents the renderer actually draws. The final fix therefore
derives coverage from the opaque top surfaces emitted into every installed LOD mesh and carries
that coverage with the mesh publication itself.

The last two owner screenshots for 0.4.100 showed broad white Farseer mountains visibly surviving
over real LOD terrain. The camera-angle oscillation had stopped, but 0.4.100 recognized only
level-0 replacement meshes while the scene was using coarser parents. The owner also identified a
separate legibility problem: every Farseer hill had the same flat shade, so overlapping ridges
merged into one silhouette.

## Work narrative

### 1. Remove the invented bands and isolate the edge treatment (0.4.95–0.4.97)

- **0.4.95** removed the visible Farseer terrain bands added in 0.4.94.
- **0.4.96** replaced the exact-contact join with a simple buried underlay. Its width is the
  adjustable **Apron width** dial, whose default is 384 blocks.
- The owner reported that sinking the entire Farseer field made the far terrain look like a basin
  below a plateau. **0.4.97** therefore keeps the distant field at its representative height and
  slopes only the edge down as it approaches real terrain.

The apron remains a gap-hiding fallback at a data frontier. It is not the primary ownership rule,
and it must not globally lower the Farseer disc.

### 2. Make real terrain own its footprint (0.4.98)

0.4.98 suppresses Farseer wherever loaded vanilla or LOD terrain claims the ground. This fixes the
case where excavation reveals the fallback terrain underneath a real vanilla chunk. Suppression is
a fragment decision in the existing Farseer draw; it does not add another render pass or GPU
readback.

That result was visually more stable than the following experimental handoff and remains the
owner's stated comparison baseline for camera behavior.

### 3. Distinguish availability from replacement readiness (0.4.99)

Suppressing from broad cache availability created a new gap: Farseer disappeared before the
vanilla or LOD replacement was ready to draw. 0.4.99 changed the handoff to use live readiness,
analogous to the established vanilla-to-LOD handoff.

The first implementation used the current frame's selected/frustum-visible LOD commands. That made
the ownership answer depend on view direction. As the owner turned the camera, Farseer shifted
back and forth and a gap reopened beside the LOD terrain. This was a significant regression.

### 4. Remove view direction from ownership, then find the level-0 blind spot (0.4.100)

0.4.100 replaced frustum/current-frame ownership with stable resident mesh state. This fixed the
terrible camera reaction, but the test was limited to level 0. The renderer can and does draw a
resident parent at levels 1–6 when its level-0 descendants are absent or not selected. In the
owner's scene, those coarser meshes were visible while the Farseer suppression logic still treated
their ground as uncovered. The result was extensive Farseer geometry drawn over LOD terrain.

### 5. Carry exact all-level coverage with each installed mesh (0.4.101)

0.4.101 adds a compact 32-block ground-coverage mask to every installed LOD mesh:

- the mask is built on the existing mesh worker from the opaque top surfaces that were actually
  emitted;
- it travels with the same mesh result and publication identity as the GPU mesh;
- it exists for levels 0–6, from 2×2 cells at level 0 to at most 128×128 cells (2 KiB) at level 6;
- partial or unknown coverage fails open, leaving Farseer present until replacement is proven.

For a Farseer fragment, the stable selector chooses the desired LOD level from camera position and
distance only. It uses that resident target when available, otherwise a coarser resident parent,
otherwise finer resident descendants. Frustum membership and camera yaw do not participate. The
exact selected 32-block cell suppresses Farseer; vanilla readiness remains part of the same stable
ownership union.

The steady-state cost is bounded lookup and bitmask work—about 89 dictionary probes per Farseer
tile—not a geometry scan. Coverage is generated during meshing, so there is no extra render pass,
readback, or duplicated LOD geometry.

### 6. Give distant ridges readable contrast (0.4.101)

The Farseer fragment shader now reconstructs a geometric normal from screen derivatives and shades
the surface from a world/sun direction. A gentle distance-depth term further separates nearer and
farther ridges. Both are camera-independent, so turning the view cannot make ownership or lighting
oscillate. The cached outer fade keeps a neutral shade while the terrain surface receives the new
slope and depth contrast.

This is intended to distinguish overlapping hills without adding geometry. Runtime GLSL
compilation and the owner's visual judgement remain the acceptance evidence.

### 7. Verification and artifact

- Final Release mod build: zero warnings and zero errors.
- Benchmark project build: zero warnings and zero errors.
- Final checks: **25,137 assertions, zero failures**.
- Stable-level tests prove: target resident wins; a coarser parent wins when the target is absent;
  finer descendants are considered only after target and parents are absent; no resident level
  returns no replacement.
- `dist/vintagehorizons_0.4.101.zip`: 654,148 bytes, 41 root-relative file entries, one DLL, no
  PDB, no wrapper directory, manifest version 0.4.101.
- SHA-256: `14CAC44C0C41DC26F48155DC2352A22E90EF123CE0C5B72F21B4734A99C8B392`.
- The archive was copy-installed beside all older rollback builds with the same hash.
- Exact source/test/version checkpoint: `a137181`.
- Documentation validation: **10,664 checks, zero failures**.

No game was launched for 0.4.101. Its shader compilation, visual suppression, ridge contrast and
runtime cost have build/static/harness evidence only.

---

## Delivered

- **0.4.95 (`020f3e5`):** removed the visible Farseer terrain bands.
- **0.4.96 (`8cb90e7`):** replaced exact joins with a buried underlay and made its 384-block apron
  width adjustable.
- **0.4.97 (`62d5f29`):** stopped lowering the whole Farseer field and sloped only its edge near
  replacement terrain.
- **0.4.98 (`d6aae26`):** suppressed Farseer below loaded vanilla and LOD terrain.
- **0.4.99 (`85b7f20`):** delayed suppression until replacement readiness, but tied the answer to
  current-frame/frustum selection and regressed with camera yaw.
- **0.4.100 (`df4bf43`):** made ownership stable across camera direction, but considered only
  level-0 LOD meshes and leaked Farseer over coarser resident terrain.
- **0.4.101 (`a137181`):** added mesh-exact 32-block coverage for all LOD levels, stable
  distance-level selection with parent/descendant fallback, and camera-independent slope/depth
  contrast for Farseer terrain.

## Decisions

- **Suppression over real terrain is mandatory.** Farseer is fallback scenery and may never remain
  visible where ready vanilla or LOD geometry covers the same ground, including below excavated
  vanilla terrain.
- **Suppression requires live drawable proof.** Stored data or requested work is not enough;
  Farseer remains until the replacement geometry is ready.
- **Ownership is independent of viewing direction.** Frustum and current-frame draw selection may
  save rendering work but cannot decide which terrain system owns a ground cell.
- **The proof belongs to the installed mesh.** Exact coverage is derived from emitted opaque top
  surfaces at every LOD level and published with that mesh's identity.
- **Only the edge slopes.** The adjustable apron hides an unresolved frontier; it must not sink the
  entire distant field or become a visible band.
- **Contrast comes from shading, not more triangles.** World-directed slope light plus gentle
  depth shading keeps the deliberately cheap geometry readable.

## Traps

- **Trigger:** suppress from cached data availability. **Failure:** Farseer vanishes while the LOD
  job is still pending, exposing sky. **Safer:** require installed replacement geometry.
- **Trigger:** derive ownership from the current frustum or draw list. **Failure:** turning the
  camera changes the handoff and makes geometry shift or gaps pulse. **Safer:** use stable resident
  state and camera-position-only level selection.
- **Trigger:** inspect only level-0 readiness. **Failure:** coarser parent meshes draw while Farseer
  remains visible over them. **Safer:** cover every resident level and apply deterministic
  parent/descendant fallback.
- **Trigger:** claim a whole coarse mesh footprint. **Failure:** sparse or incomplete geometry hides
  Farseer in cells it cannot replace. **Safer:** derive a compact cell mask from emitted top
  surfaces and fail open where proof is absent.
- **Trigger:** light distant terrain from the camera direction. **Failure:** hill contrast changes
  as the player turns and looks like geometry movement. **Safer:** use a world-space direction and
  camera-independent depth contrast.

## Flagged and unverified

Judgement calls awaiting human review:

- Whether 0.4.101 keeps Farseer completely off both vanilla and LOD terrain from the same camera
  positions that exposed 0.4.100, without reopening a sky gap before replacement loads.
- Whether the new slope/depth contrast separates overlapping ridges naturally without making
  Farseer look striped, over-lit or too dark.
- Whether the default apron width and depth should change after the exact ownership handoff is
  accepted; both remain adjustable.

Claims lacking required evidence:

- The changed Farseer GLSL has not compiled in game.
- Camera-yaw stability, rapid movement/loading transitions, excavation, mixed LOD levels and
  replacement unload/reload have not been human-tested on 0.4.101.
- No controlled GPU/FPS comparison has measured the new coverage lookups and shading.
- Cold client restart/full-disc persistence, dedicated multiplayer, mixed versions, remaining
  player-facing rename/license work, Terrain Ahead (P6), and canopy (P7) remain open.
