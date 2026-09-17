# Handoff - Farseer at 0.4.101, mesh-exact ownership gate next

**Updated:** `2026-09-17`, Session 98
**Branch/source checkpoint:** `codex/water-depth-light`, 0.4.101 at `a137181`; nothing pushed
**Installed:** verified 0.4.101 private test build, copy-installed; not yet run
**Last accepted baseline:** `0.4.64`
**Comparison note:** the owner called 0.4.98 substantially more stable than 0.4.99; 0.4.101 is the
current correction, not yet accepted
**Compatibility:** assist protocol / blob / schema `1 / 4 / 6`, unchanged. Server and client
silhouette stores remain schema `1`; transport generation remains `1`.

Read, in this order:
1. this page;
2. [Session 98](../sessions/SESSION_98.md);
3. [the Farseer/Terrain Ahead plan](PLAN_HORIZON_SILHOUETTES_AND_FRONTIER.md), especially P4-P6;
4. [the gotchas](../GOTCHAS.md) G209-G216 plus shader/render G5, G10, G130, G158, G160-G161 and
   G174;
5. [TODO](../TODO.md).

`CLAUDE.md` holds the working rules. Ask before any game launch.

## Where things stand

- P1-P4 and most P5 persistence/tuning work are built through 0.4.101.
- The owner saw instant prior terrain after leave/rejoin and saw no visual loss from 0.4.93's large
  geometry reduction.
- 0.4.94's bands/global sinking were rejected. 0.4.95 removed the bands; 0.4.96-0.4.97 changed the
  join to an adjustable underlay whose near edge slopes down without sinking the whole field.
- Suppression over ready vanilla and LOD terrain is non-negotiable, including below excavated
  vanilla ground. It must not occur before replacement geometry can draw.
- 0.4.99 used current-frame/frustum LOD selection and visibly shifted with camera yaw, reopening a
  gap. 0.4.100 stabilized camera behavior but recognized only L0, so Farseer remained broadly over
  visible coarser LOD parents.
- 0.4.101 carries exact 32-block coverage with every installed L0-L6 mesh and adds
  camera-independent slope/depth contrast. It is the exact next visual gate and has not run.
- Terrain Ahead remains the independent P6 feature. Canopy remains P7.

## Current implementation

### Persistence and delivery

- The client store is `<savegame>-silhouette.db`. It opens only when Farseer is enabled and offered,
  reads nearest stored tiles through bounded store work, and closes with live state on off/world
  teardown.
- Current-session network replies remain authoritative; stored revisions cannot block a server
  whose revision sequence restarted. Replies that precede store opening are backfilled afterward.
- Current peers use bounded revision manifests/nearest requests with session identity; 0.4.77-0.4.85
  direct push remains the compatibility path.

### Lightweight renderer and scheduling

- Each 512-block tile stores 16/8/4-quad meshes (32/64/128-block quads), 672 base triangles total,
  and draws one. Projected size plus Outline detail chooses the broad-horizon tier.
- Frustum and back-face culling are draw optimizations only; neither owns the handoff. Network decode
  and mesh construction are capped at two worker jobs each. Owner-thread decode publication, one
  mesh snapshot per pump, pruning, dirty work and GPU upload all have bounds.
- Reach changes retain tiles/meshes, update demand in place and prune gradually. Hill softness
  debounces 250 ms, feathers up to 128 blocks on workers and enqueues four remeshes per pump.

### Edge apron and exact ownership

- The fallback field keeps its distant representative height. Only its edge near real terrain
  slopes down through an adjustable buried apron; the default width is 384 blocks. The width and
  depth are dials.
- Stable vanilla readiness and exact installed LOD geometry form the suppression union. Farseer
  remains in unknown/partial cells, then disappears where replacement is proven.
- During ordinary LOD meshing, every installed L0-L6 mesh gets a compact 32-block ground mask from
  the opaque top surfaces actually emitted. The mask travels in the same mesh result/publication as
  the GPU mesh, so it cannot describe different data.
- For a visible Farseer tile, the selector chooses a target level from camera position/distance,
  uses that resident level when present, then a coarser resident parent, then finer resident
  descendants. View direction and current-frame draw lists do not participate.
- The largest mask is 128×128 bits / 2 KiB at L6. Per visible tile, suppression is about 89 bounded
  dictionary lookups plus bit tests; there is no geometry scan, extra render pass or GPU readback.

### Ridge contrast

- The Farseer fragment shader reconstructs a geometric normal with `dFdx`/`dFdy` and shades from a
  world/sun direction.
- Gentle distance-depth shading helps distinguish nearer and farther overlapping hills.
- Neither term uses camera yaw. The cached outer fade passes neutral surface shade so the shared
  color handoff keeps its existing behavior.

### Settings and naming

- The visible tab is **Farseer**. The General scale has vanilla-tab typography, two-line `L1`-`L6`
  distances, and live cached/reach handles.
- Farseer persists reach, Outline detail, Hill softness, Apron width/depth and classic color/look
  controls.
- Many internal/config/command names remain “Silhouette” for now. Finish the player-facing rename
  deliberately without casually renaming registered protocol fields. Farseer's MIT license/notice
  still needs to be bundled; source/shader attribution already exists.

## Exact continuation

1. Ask before launching. Load 0.4.101 at the same locations that exposed the 0.4.100 overlap.
2. Hold position and turn through the whole scene. Farseer must not shift, appear or disappear merely
   because camera yaw changed.
3. Approach vanilla and LOD boundaries from oblique angles, then move quickly enough to see terrain
   load and unload. Report separately:
   - any Farseer color over ready vanilla terrain;
   - any Farseer color over ready LOD terrain, especially coarser parents;
   - any sky pixel, slit or flash before replacement becomes drawable;
   - any Farseer visible below excavated vanilla ground.
4. Compare overlapping Farseer ridges while turning. They should have readable slope/depth contrast
   without stripes, exaggerated darkness or camera-relative shade changes.
5. This run also proves or disproves the changed GLSL compilation.
6. Only after ownership is clean, tune Apron width/depth, Outline detail and Hill softness. Then take
   a controlled same-scene off/on FPS/GPU-time comparison and change reach/shape dials while watching
   frame hitches.
7. Only then decide whether islands/inlets need further low/coastal refinement.
8. Finish the player-facing Farseer rename/license work before calling P5 complete.

Do not add terrain detail back merely because it is available. The owner explicitly saw no visual
loss from the 0.4.93 reduction and wants this component as lightweight as feasibly possible.

## Deferred integration debt

- Cold client restart and full-disc store read-back; off-state store unopened/closed behavior.
- Complete singleplayer disc counts and `/vhworldgen` captured-column integration.
- Dedicated opt-in/reach clamp, multi-player fairness, disconnect/off cleanup and mixed versions.
- Night, rain/fog and winter look.
- `bodanboys` generator stall remains set aside; do not reproduce it just to test brakes.

## Verification and artifact

- Release mod build: zero warnings/errors.
- Benchmark project build: zero warnings/errors.
- Fast tier: 25,137 assertions, zero failures.
- Stable-level unit coverage proves target, coarser-parent, finer-descendant and no-resident cases.
- `dist/vintagehorizons_0.4.101.zip`: 654,148 bytes, 41 root-relative file entries, manifest
  0.4.101, one matching DLL, no PDB or wrapper directory.
- SHA-256: `14CAC44C0C41DC26F48155DC2352A22E90EF123CE0C5B72F21B4734A99C8B392`.
- Copy-installed with older rollback archives retained.
- Source checkpoint: `a137181`.

## Working cautions

- Docs are current through Session 98. Do not edit them again until asked.
- Never launch a game or test server without the authority required by `CLAUDE.md`.
- Every playable version receives its exact verified/install checkpoint. Never push, tag or publish
  without approval.
- Preserve height truth: captured > preview > accelerator.
- Stored data is not live draw readiness; frustum selection is not terrain ownership; L0 is not all
  visible LOD.
- Source/static shader checks are not a runtime GLSL claim.
