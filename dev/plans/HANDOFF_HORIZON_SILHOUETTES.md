# Handoff - Farseer at 0.4.108, full corner sweep next

**Updated:** `2026-09-17`, Session 99
**Branch/source checkpoint:** `codex/water-depth-light`, 0.4.108 at `6708b19`; nothing pushed
**Installed:** verified 0.4.108 private test build, copy-installed beside rollback builds; not yet run
**Last human result:** 0.4.106 fog “very good”; 0.4.107 “excellent,” with one remaining corner taper
**Last accepted whole-mod baseline:** `0.4.64`
**Compatibility:** assist protocol / blob / schema `1 / 4 / 6`, unchanged. Server and client
silhouette stores remain schema `1`; transport generation remains `1`.

Read, in this order:
1. this page;
2. [Session 99](../sessions/SESSION_99.md);
3. [the Farseer/Terrain Ahead plan](PLAN_HORIZON_SILHOUETTES_AND_FRONTIER.md), especially P4-P6;
4. [the gotchas](../GOTCHAS.md) G209-G222 plus shader/render G5, G10, G130, G158, G160-G161 and
   G174;
5. [TODO](../TODO.md).

`CLAUDE.md` holds the working rules. Ask before any game launch.

## Where things stand

- P1-P4 and most P5 persistence/tuning work are built through 0.4.108.
- Farseer is intentionally coarse fallback scenery: 16/8/4 quads per tile, bounded regeneration,
  lazy client persistence and exact stable replacement coverage at every installed LOD level.
- Ready vanilla/LOD terrain owns exact 32-block cells. Farseer remains until replacement is
  drawable; cached data, frustum selection and camera yaw are not ownership proof.
- Only the unresolved Farseer edge slopes through the adjustable apron. The broad distant field
  retains its representative height.
- A separate atmospheric volume now conceals the residual visual join. It is derived from the
  exact ownership line, spills across both sides, and does not alter ownership.
- The owner rejected 0.4.102's flat/angular surface fog. The layered 0.4.106 volume looked very
  good. Moving it after OIT and adding corner caps in 0.4.107 produced an excellent result and
  preserved visible water, but one diagonal corner still read as a missing adjacent fog section.
- 0.4.108 expands only that corner cap to the full square spill footprint. It is the exact next
  visual gate.
- Terrain Ahead remains the independent P6 feature. Canopy remains P7.

## Current implementation

### Ownership, apron and exact fog boundary

- Stable vanilla readiness and exact installed LOD geometry form the suppression union. Unknown or
  partial cells fail open to Farseer.
- Every installed L0-L6 mesh carries a compact 32-block ground mask derived from its emitted opaque
  top surfaces and published with the same mesh identity.
- The selector uses camera position/distance only: resident target, coarser parent, then finer
  descendants. It never consults the frustum or current-frame draw list.
- The ownership-edge mask marks cardinal edges only where two drawable Farseer cells have opposite
  owners. Missing neighbour tiles create no fog seam. A content signature reuses unchanged CPU/GPU
  state.
- Fog is visual spill over the exact boundary. It never dilates ownership, suppresses more Farseer,
  or changes the 384-block-default edge apron.

### Volumetric seam fog

- One bounded mesh crosses every unique ownership edge. Vertical sheets provide ground-view depth;
  horizontal sheets provide aerial body. Density peaks at the seam and reaches zero at the lateral,
  upper and lower rim.
- World-anchored three-dimensional billows drift slowly through the layers. The mesh is rebuilt only
  when the boundary or shape settings change.
- Vertices where perpendicular edges meet receive one layered horizontal cap. Straight points
  receive none. At 0.4.108 the cap reaches the full `spread × spread` square footprint, and every
  perimeter vertex still has zero density.
- Defaults: 48-block spread to each side, 88-block height, 16-block floor depth, seven vertical and
  five horizontal layers at the default detail.

### Render-stage and water safety

- Farseer terrain remains in the ordinary opaque callback after cached opaque terrain.
- The fog wrapper draws at `AfterOIT` order 1.0, after the Vintage Horizons water capture at 0.99.
  The veil therefore covers already-composited water instead of water repainting the seam.
- A color-only framebuffer attaches primary color, samples primary depth read-only, writes RGB only
  and preserves primary alpha/depth. It uses explicit standard blending for attachment zero; the
  engine's SSAO-aware general blend helper is deliberately not used.
- Water shaders, liquid depth, revealage, water ownership and the shared terrain readiness mask are
  unchanged.
- Compile, draw or guarded GL failure disables Farseer for the session with one named error rather
  than continuing with a plausible no-fog fallback.

### Settings and status

- The visible tab is **Farseer**. It persists reach, Outline detail, Hill softness, Apron
  width/depth and the classic color/look controls.
- The fog has eight persisted live-preview dials: density, coverage, spread, height, floor depth,
  billow size, drift speed and layer detail. Density zero disables the fog pass.
- `.vhsilhouette stats` reports the shader/failure state and the active fog density/spread/height.
- Many internal/config/command names remain “Silhouette” for compatibility. Finish the deliberate
  player-facing rename and bundle Farseer's MIT notice before closing P5.

### Persistence, delivery and bounded cost

- The client store is `<savegame>-silhouette.db`; the server store is
  `<savegame>-silhouette-server.db`. Both remain schema 1 and are lazy/off-owned as documented in
  Sessions 96-98.
- Current peers use bounded revision manifests and nearest-first requests with world-session
  identity; old direct-push peers remain supported.
- Network decode and mesh construction are capped at two jobs each. Owner-thread publication,
  pruning, remesh and upload are bounded. Reach changes retain useful intersection state.
- Fog is one draw, but its fill/blending cost and the controlled total Farseer off/on cost remain
  unmeasured. Layer detail intentionally trades more crossing sheets for fewer visible planes.

## Exact continuation

1. Ask before launching. Load 0.4.108 at the viewpoint from screenshot
   `2026-09-17_20-02-06.png` in the owner's normal Vintage Story screenshot folder.
2. Inspect that ninety-degree turn and one other. The fog should read as one continuous sweep:
   - no rounded bite;
   - no missing square/adjacent section;
   - no hard square outer perimeter.
3. Include a water-edge turn. Water must remain visible, while fog stays on top of its chunk edge.
4. After the view, inspect `client-main.log`: Farseer must remain enabled and there must be no
   seam-fog compile/draw `InvalidOperation`.
5. Do not change density, spread or layer detail before giving the isolated 0.4.108 cap a verdict.
   If the cap is accepted, tune preferences through the existing dials rather than changing its
   topology.
6. Then complete the broader correctness matrix: turn in place, approach obliquely, move through
   replacement load/unload, inspect excavated vanilla ground, and look once at night, rain/natural
   fog and winter.
7. Take a controlled same-scene Farseer off/on timing and a separate low/default/high fog-detail
   comparison. Do not infer cost from the old informal 550→450 observation.
8. Finish the player-facing Farseer rename/license work before calling P5 complete.

## Do not reopen these designs casually

- Do not return to surface-painted fog; it was human-rejected as flat and angular (G217).
- Do not draw the veil before OIT or write primary alpha/depth; that broke or exposed water (G218).
- Do not use `GlToggleBlend(true)` on the one-attachment framebuffer (G219).
- Do not upload integer reach through a float-uniform call (G220).
- Do not solve a corner-only hole by thickening every straight strip. Cap only perpendicular turns,
  and keep the full square perimeter at zero density (G221-G222).
- Do not change water shaders, water ownership or shared terrain readiness to fix fog.
- Do not add terrain detail back merely because it is available. The owner saw no visual loss from
  the 0.4.93 reduction and wants the feature lightweight.

## Deferred integration debt

- Rapid replacement, excavation and full camera-yaw matrix beneath the veil.
- Cold client restart/full-disc store read-back and off-state store behavior.
- Complete singleplayer disc counts and `/vhworldgen` captured-column integration.
- Dedicated opt-in/reach clamp, multiplayer fairness, disconnect/off cleanup and mixed versions.
- Final weather look and controlled GPU/off/hitch evidence.
- Optional island/inlet refinement only if the accepted join still needs it.
- `bodanboys` generator stall remains set aside; do not reproduce it merely to test brakes.

## Verification and artifacts

- Release mod build: zero warnings/errors.
- Final fast tier: 25,168 assertions, zero failures.
- `dist/vintagehorizons_0.4.108.zip`: 666,934 bytes, 43 root-relative entries, manifest 0.4.108,
  one matching DLL, fog shaders present, no PDB or wrapper directory.
- SHA-256: `3ADAB55D48C80E173400A9F690C6146FF9EB282A26DA2E000155D6FA217A088E`.
- Copy-installed with 0.4.101-0.4.107 and all older rollback archives retained.
- Source checkpoint: `6708b19`.

Installed fog-series rollback hashes:

| version | bytes | SHA-256 |
|---|---:|---|
| 0.4.102 | 659,462 | `0D2AE4A767A0263FEFE1B06B90331533B370D36AB89390DAA8D7A7204C43C312` |
| 0.4.103 | 662,997 | `B25E85B24425400833C29E11794A9CEB643D1DF8A0297AD7E131C02384264860` |
| 0.4.104 | 665,887 | `99D4EEC991CB1D7A7FB4C4D2BBB603BC833B02A76AB79A0E60BA7E9691817D5F` |
| 0.4.105 | 665,903 | `7D31D3633DF45E2F73993AC14B0F6B838A5969B1F862713241795957DF6D66AA` |
| 0.4.106 | 665,898 | `41E6B6EF582168A8E50C4A59D93AB9A711C0E5B5B532946FBA954C943982AB05` |
| 0.4.107 | 666,966 | `26A6788B3AEDB2089DF1A6FE61FDCB753F49CEF2940F7B553013D0BBAB6F6F3A` |
| 0.4.108 | 666,934 | `3ADAB55D48C80E173400A9F690C6146FF9EB282A26DA2E000155D6FA217A088E` |

## Working cautions

- Docs are current through Session 99. Do not edit them again until asked.
- Never launch a game or test server without the authority required by `CLAUDE.md`.
- Every playable version receives its exact verified/install checkpoint. Never push, tag or publish
  without approval.
- Preserve height truth: captured > preview > accelerator.
- Stored data is not live draw readiness; frustum selection is not terrain ownership; L0 is not all
  visible LOD.
- Source/static shader checks are not a runtime GLSL claim. The owner's screenshots/logs are the
  runtime evidence recorded here.
