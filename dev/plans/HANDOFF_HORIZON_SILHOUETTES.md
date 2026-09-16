# Handoff - horizon silhouettes at 0.4.87, P4 runtime gate next

**Updated:** `2026-09-15`, Session 96
**Branch/source checkpoint:** `codex/water-depth-light`, 0.4.87 at `2111ee9`; nothing pushed
**Installed:** verified 0.4.87 private test build, copy-installed; 0.4.85-0.4.87 have not run in game
**Last accepted baseline:** `0.4.64`
**Compatibility:** assist protocol / blob / schema `1 / 4 / 6`, unchanged. The silhouette tile
store has schema `1`; current silhouette transport generation is `1`.

Read, in this order:
1. this page;
2. [Session 96](../sessions/SESSION_96.md);
3. [the silhouette plan](PLAN_HORIZON_SILHOUETTES_AND_FRONTIER.md), especially phases P4-P5;
4. [the gotchas](../GOTCHAS.md) G199-G208 plus shader/render G5, G10, G130, G158, G160-G161 and
   G174;
5. [TODO](../TODO.md).

`CLAUDE.md` holds the working rules; follow them. Ask before any game launch.

## Where things stand

- **P1-P4 are built in source.** The current artifact is 0.4.87.
- **No 0.4.85-0.4.87 runtime evidence exists.** The owner was away and no other players were
  available. Do not describe source/harness evidence as a passed visual, shader, performance,
  restart or multiplayer gate.
- **Next gate:** P4's small in-game visual/UI/performance checklist below.
- **After acceptance:** P5 tuning and client tile persistence. Terrain Ahead remains the separate
  P6 feature.

## Owner decisions now implemented

- The P0.3 accelerator accuracy is accepted. Representative shape is enough; no +1.4-block lean
  correction is applied.
- Support both optional accelerators. Prefer Watersheds' native sampler when both are installed;
  accept Algernon's Terrain Sampler when Watersheds is absent. Horizons requires neither.
- Continue without multiplayer evidence, but retain that evidence as an open gate.
- The P3 accelerator and P4 design were accepted for implementation.

## What 0.4.85-0.4.87 contain

### 0.4.85 - accepted accelerator path

- One selected sampler fills each tile at 64-block spacing (64 cells per tile) on a background
  caller. Up to four tile jobs may wait; calls themselves are serialized until concurrency beside
  real generation is measured.
- Captured terrain and engine previews remain better origins and replace accelerator cells.
- One engine preview refines a tile that looks low or coastal.
- A named sampler failure disables it for the session and previews take over.
- `SilhouetteUseAccelerators = false` retains the standalone preview path.

### 0.4.86 - final P3 transport and dedicated opt-in

- Current peers exchange chunked key/revision manifests and nearest-first requests; the older
  direct-push path remains for 0.4.77-0.4.85 peers.
- Sixteen client requests may be in flight. Server replies are capped at 20/s per player and 80/s
  total. Every accepted key gets success, refusal or bounded retryable state.
- A world-session id rejects late packets from an old world. Moving reach prunes offers, tiles and
  meshes outside its margin.
- Dedicated servers offer silhouettes only with explicit `EnableSilhouettes = true`; `null` remains
  integrated/LAN automatic and dedicated off. `SilhouetteReachBlocks` clamps dedicated reach;
  `SilhouettePreviewsPerSecond` is bounded.
- The builder yields to the complete shared generation gate, `/vhworldgen`, `/vhgen` and real loads.

### 0.4.87 - P4 rendering and settings

- Each tile has 128/32/8-quad meshes. Projected cell size chooses one; no fixed LOD distances.
- Covered interiors keep raw heights, uncovered terrain clamps to sea, live cached edge heights
  anchor the join, outer edges stitch at a common 64-block boundary, and the near join sinks.
- Exact 64-block cached coverage is a per-tile shader mask. It suppresses silhouette fragments
  inside cached terrain's fade but permits them beyond cached terrain's far edge.
- Cached terrain and the silhouette share one classic-colour include at the fade target.
- The silhouette pass draws after cached opaque terrain and contributes through the existing
  stepped projection policy.
- Shader compilation waits for a drawable tile; failure disables the feature for the session.
- `.vhconfig` has a persistent Silhouettes tab: enable, reach, status and classic-look dials. The
  General scale has cached-draw and silhouette-reach handles. Performance disables silhouettes;
  Balanced/Quality enable them. `.vhsilhouette` remains session-only.
- Off sends the final demand and clears manifests, requests, tiles, coverage, mesh work, GPU meshes
  and projection extent.

## Exact continuation

### First: P4 human gate

Use 0.4.87 when the owner is present. Ask before launching. Keep the observations small:

1. **Shader and sea:** at sea with no cached land inside draw distance, confirm silhouettes appear
   toward known land and no compile failure is logged.
2. **Inland gap:** find one unexplored gap between cached areas and confirm it fills.
3. **Join:** look for a line, hole or double surface at an explored edge and at cached draw distance.
4. **Weather:** one check each at night, rain/fog and winter.
5. **Controls:** move cached draw distance and silhouette reach on the General scale; check the
   haze band, dimming and hidden state when off. Check the Silhouettes tab fits and previews dials.
6. **Cost:** capture silhouette GPU time if available and a same-scene on/off frame-time A/B.

If the join cannot be made clean, stop for the owner. The plan's fallback—silhouettes only beyond
cached draw distance—is a product decision, not an implementation detail.

### Then: P3 integration debt

- Let one singleplayer reach complete and read the source/fill/transport counts.
- Restart and observe server tile read-back.
- When another player is available, test a dedicated server with explicit opt-in: reach clamp,
  fairness, off/disconnect cleanup and current/older-peer behavior.
- P2 still needs one `/vhworldgen` area's captured columns observed in silhouette tiles.
- Do not deliberately reproduce the `bodanboys` stall merely to test its brakes. If generator
  silence or errors occur naturally, confirm the named stop line.

### After P4 acceptance: P5

- Compare islands and inlets against dense real terrain and adjust low/coastal refinement only if
  the picture needs it.
- Tune reach and classic-look dials with the owner.
- Add client tile persistence for instant rejoin; it must not open or cost anything while off.
- Colour-per-vertex, shared grid/height textures and finer coverage are measurement-triggered only.

## Verification and artifact

- Release build: zero warnings/errors.
- Fast tier: 25,076 assertions, zero failures.
- `dist/vintagehorizons_0.4.87.zip`: 643,980 bytes, 41 entries, exact assembled content, no PDB.
- SHA-256: `0BEEBE9B8B8CC8561CBDB90F08B86063B7C181F92F68035A99D456B8E0E330AA`.
- Copy-installed with older rollback archives retained.
- Source checkpoint: `2111ee9a5ae1d0bf277fc41a8b6ec51c162db666`.

## Open, not established

- P4's actual GLSL compile and picture.
- GPU cost and zero-cost off A/B.
- Stored-tile restart read-back, complete-disc convergence and error-brake behavior.
- Dedicated multiplayer and mixed-version behavior.
- Accelerator sampling beside real generation; mountains were thin in P0.3.
- Why the test worlds' sea level is 82 and whether the owner's worlds share it.
- The set-aside `bodanboys` preview stall (G204).

## Working cautions

- Docs are current through Session 96. Do not edit them again until asked.
- Never launch a game or test server without the authority required by `CLAUDE.md`.
- Versioned playable source receives its exact checkpoint; documentation-only commits still require
  owner approval. Never push without approval.
- Preserve the source hierarchy: captured > preview > accelerator. Do not let a fast answer replace
  terrain the engine actually generated.
