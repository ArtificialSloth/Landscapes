# Session 96 — silhouette P3 and P4 built through 0.4.87; runtime gate deferred

**Date:** `2026-09-15`
**Branch/commit:** `codex/water-depth-light`; source checkpoints `65990f1` (0.4.85), `069f0b1` (0.4.86) and `2111ee9` (0.4.87). This documentation pass is uncommitted at close.
**Mod version:** `0.4.87`
**Assist protocol / blob / schema:** `1 / 4 / 6`. The silhouette tile store remains schema `1`; silhouette transport generation is `1`.

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

Session 95 ended with the P0.3 side-by-side awaiting the owner's verdict. The owner accepted the
accelerator accuracy, said lower accuracy could also be acceptable for a useful saving, and made the
compatibility rule explicit:

- accept Watersheds' sampler and Algernon's Terrain Sampler;
- prefer Watersheds when a player/server has both;
- accept Algernon alone, because some players will not use Watersheds.

The owner accepted the P3 accelerator design, directed work through P3 and P4, and said no other
players were available for testing. No game or server was launched in this session. Runtime gates
that require a player or a dedicated multiplayer pair remain open rather than blocking source work.

The work started from the Session 95 handoff, the silhouette plan, G199-G208, and the live source.

## Work narrative

### 1. Accepted accelerators (0.4.85)

The live builder now chooses one optional accelerator:

- Watersheds' native sampler when both supported samplers are installed;
- Algernon's Terrain Sampler when it is present without Watersheds;
- the standalone engine-preview path when neither is present or the admin disables accelerators.

The chosen sampler runs off the main thread. Calls are serialized until concurrent sampling beside
real generation has evidence; up to four tile jobs may wait. Each tile receives 64 coarse samples,
one every 64 blocks. Captured heights and engine previews remain better origins and replace an
accelerator answer. One engine preview refines a tile that looks low or coastal. If the sampler
throws or returns unusable data, it is disabled for the session and previews take over with a named
log line.

This deliberately does not subtract the measured +1.4-block lean. D8 accepts representative rather
than exact terrain, and the owner accepted the picture as it stood.

### 2. Final P3 transport and dedicated opt-in (0.4.86)

The original 0.4.77 direct-push prototype is retained only for an older peer. Current peers use the
registered manifest/request messages:

- the server offers tile keys with revisions in bounded manifest chunks;
- the client requests the nearest missing revisions, with at most 16 in flight;
- every accepted request ends in a tile, an explicit refusal, or a retryable answer;
- retryable answers wait two seconds and stop after a bounded number of attempts;
- a world-session id prevents a late packet from an old world freeing or filling a current slot;
- tiles and offers outside the moving reach are pruned, and GPU removal is requested;
- server replies are capped at 20 tiles/s per player and 80/s in all.

Transport generation `1` and the session identity are append-only fields on protocol 1. Older
0.4.77-0.4.85 servers ignore them and keep pushing tiles; the new client accepts that legacy form.

Dedicated servers now have the planned explicit opt-in. `EnableSilhouettes = true` offers the
feature; the default `null` remains off on dedicated and automatic on integrated/LAN servers. A
dedicated request is clamped by `SilhouetteReachBlocks` (default 8,192). The preview rate is
configurable and bounded. The builder also yields to every ordinary automatic-generation gate, not
only the exclusive generation authority.

### 3. P4 renderer, handoff and settings (0.4.87)

The client worker builds three meshes for every tile: 128, 32 and 8 quads per edge. The renderer
chooses among them by projected cell size against the existing LOD pixel target, not by a fixed
distance. Covered interiors retain their raw heights; uncovered terrain clamps to sea level. Live
cached edge heights anchor the join, mixed-resolution outer edges share the 64-block boundary, and
the silhouette sinks slightly beside cached coverage.

The new opaque silhouette pass:

- draws after cached opaque terrain and owns no second far-plane policy;
- uses the existing stepped projection with the silhouette reach as another extent;
- carries exact 64-block cached coverage as a per-tile shader bit mask;
- discards covered fragments inside the cached fade, while allowing the silhouette beyond the
  cached far edge;
- shares one classic-colour include with the cached terrain's fade target, so both sides approach
  the same colour at a join;
- compiles only after a server has supplied a drawable mesh;
- disables the client feature for the session if the shader fails.

The client config now persists the on/off choice, reach and classic-look dials. The General distance
scale carries cached draw-distance and silhouette-reach handles, dims levels beyond cached draw
distance, and hides the silhouette band when off. The new Silhouettes tab has live preview, status,
defaults and the look dials. Performance turns silhouettes off; Balanced and Quality turn them on.
The `.vhsilhouette` command remains a session-only override and does not rewrite the saved choice.

Turning silhouettes off sends one final request, clears network queues, tiles, coverage, worker
obligations and GPU meshes, removes their projection extent, and leaves the cached shader on its
previous sky-fade path.

### 4. Verification and artifact

- Release build: zero warnings and zero errors.
- Fast checks: **25,076 assertions, zero failures** against the local Vintage Story runtime.
- The 0.4.87 archive has 41 entries, matches the assembled mod folder byte-for-byte, reports
  0.4.87, and contains no PDB.
- `dist/vintagehorizons_0.4.87.zip`: 643,980 bytes; SHA-256
  `0BEEBE9B8B8CC8561CBDB90F08B86063B7C181F92F68035A99D456B8E0E330AA`.
- The archive was copy-installed without removing older builds.
- Exact source checkpoint: `2111ee9a5ae1d0bf277fc41a8b6ec51c162db666`.
- Documentation validation: **10,659 checks, zero failures**.

No visual, frame-time, shader-compile, restart-readback or multiplayer claim is made. The shader
contract and source wiring are harness-tested; only a real game can compile the GLSL and judge the
join.

---

## Delivered

- **0.4.85 (`65990f1`):** accepted accelerator selection, 64-block coarse sampling, low/coastal
  preview refinement, failure fallback and server switch. Zip SHA-256
  `6A4D3DD7B950DADC58E16E89F7A207B387A86DAF9A5FACFDF732EC665F997D76`.
- **0.4.86 (`069f0b1`):** bounded manifest/request transport, session/revision safety, reach pruning,
  all generation gates, dedicated opt-in/reach clamp and mirrored settings. Zip SHA-256
  `E2C1935080C6DCDAB4CA54BD5DB0801D0EBBD73295F492786F7D099319D0F66D`.
- **0.4.87 (`2111ee9`):** adaptive meshes, render pass, exact coverage, shared handoff colour,
  far-plane integration, settings tab, scale handles, presets and complete client off state. Zip
  SHA-256 `0BEEBE9B8B8CC8561CBDB90F08B86063B7C181F92F68035A99D456B8E0E330AA`.
- Documentation at the owner's request: Session 96, index, changelog, architecture, wire history,
  TODO/DONE, status, README, plan status and the clean-session handoff.

## Decisions

- Both supported accelerators are accepted. Prefer Watersheds when both exist; accept Algernon
  alone; require neither.
- Keep the measured height lean. Accuracy was accepted, and a correction would add policy without
  acceptance evidence.
- Finish P4 without multiplayer or visual testing. Missing people defer those gates; they do not
  turn source/harness evidence into human evidence.
- Use an exact per-tile coverage mask in the shader rather than a vertex covered flag. Coverage is
  cell-owned, so the mask avoids ambiguity at vertices shared by covered and uncovered cells.
- `.vhsilhouette` stays session-only. Saved settings belong to the Settings editor.

## Traps

- No new durable trap was proven. The implementation continued to rely on G5, G10, G130, G158,
  G160-G161, G174 and G199-G208.
- A check run without the `VINTAGE_STORY` environment used the repository fallback game path and
  produced irrelevant fixture failures. The correct final run set the local game path explicitly.
  This is already a documented build rule, not a new rendering gotcha.

## Flagged and unverified

Judgement calls awaiting human review:

- P4's sea and inland appearance, cached/silhouette join, night/rain/fog/winter colour, and the two
  new scale handles.
- Whether the default classic-look dials should change.
- If the join cannot be made clean, the plan's fallback—draw silhouettes only beyond cached draw
  distance—requires an owner decision.

Claims lacking the required evidence:

- The silhouette GLSL has not compiled in game in 0.4.87.
- GPU time and the off-state frame-time A/B are unmeasured.
- Stored-tile read-back, the error brake and the full requested disc have not been observed in a
  new runtime session.
- Dedicated opt-in, reach clamping, multi-player fairness and mixed-version transport have no
  live two-player/dedicated run.
- P2's `/vhworldgen`-to-tile integration gate remains open.
- P5 client tile persistence and owner tuning are not built; Terrain Ahead remains P6.
