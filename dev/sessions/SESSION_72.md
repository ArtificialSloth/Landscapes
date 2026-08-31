# Session 72 — Blend-toggle lifecycle corrections and placed-block invalidation review

**Date:** `2026-08-31`
**Branch/commit:** `codex/exact-vanilla-draw-range`, worktree based on checkpoint `4879b48`
**Mod version:** `0.3.166`
**Assist protocol / blob / schema:** `1 / 4 / 6`

## Context and investigation

The owner resumed normal work after Session 71's documentation-only close and directed a checkpoint
commit before the five deferred blend-toggle findings were corrected. Commit `4879b48` preserves the
Forest Floor fix, the first master-toggle implementation, and Session 71's review record.

After the corrections were built and checked, the owner asked how a newly placed block gains an L0
texture once vanilla terrain no longer draws that area. Source-tracing that path established that
texture acquisition itself is wired, but exposed a separate pre-existing content-invalidation bug.
No source fix for that newly found bug was authorized in this session; it is documented and moved to
the TODO.

No game process or performance benchmark was run. Runtime and visual claims remain unproven.

## Work narrative

### 1. Texture-slot production now follows the master switch

Live capture now resolves a texture slot when it registers a new palette entry. Cache and foreign
section reclassification use the same gated resolver. When the switch is off, palette work returns
slot zero and mesh snapshots omit the texture-slot array entirely, so the disabled path no longer
performs that CPU lookup/allocation/copy work.

Turning the switch back on begins a bounded owning-thread repair over resident L0 palettes. It
examines at most 256 entries and about one millisecond per tick, preserves slots already resolved,
and delays textured mesh publication until the repair is complete. Sections captured or loaded
after enabling resolve slots directly. Rare live transitions continue to use the established
bounded L0 remesh path rather than retaining two mesh representations.

### 2. Mesh work and texture tables are isolated across worlds and failures

Mesh jobs/results now carry the pipeline world epoch. The renderer rejects a cross-world result
before touching the in-flight set or dirty state, which protects a new world's same-numbered
section key from an old worker that completed after teardown. World leave clears queued mesh work,
resident texture-slot registration, cached texture means, and GPU table/failure state.

Block-table refresh is now replace-then-swap. A new GL texture receives the complete table and
becomes live only after upload succeeds. If refresh fails, the previous valid table remains bound
for already-published slot-bearing meshes while a texture-mode revision prevents newer incompatible
results from publishing; the table retires when the fallback remesh drains the last dependent mesh.

### 3. Placed-block source trace found a distinct open defect

Every client `ChunkDirty` event queues that chunk column for recapture. Capture reads the new block
id, owning-thread palette registration resolves its colour/tint and, while enabled, its L0 texture
slot, and `ReplaceColumns` installs the changed runs.

`ReplaceColumns` returns only an edge mask. Zero correctly means an interior mutation touched no
neighbour-facing edge, but it is also the value returned when no content changed. `ApplyOneCaptureResult`
currently calls `World.MarkChanged` only when that mask is nonzero. Therefore a typical placement in
an established section can mutate the resident runs and palette without advancing the content or
persistence revisions, dirtying the L0 mesh, or scheduling mip propagation. The texture may already
be retrieved, yet stale geometry can remain when vanilla relinquishes the area. A later edge-touching
change can incidentally publish the accumulated RAM state; leaving first can lose the unsaved change.

The correction must carry a separate changed/not-changed result from column replacement, always call
`MarkChanged` for changed content, and use the edge mask only to select neighbour rebuilds. G138 and
`dev/TODO.md` own this open work.

### 4. Verification and private test artifact

The Release project built with zero warnings and zero errors. Four focused suites covered bounded
resident backfill, off-mode snapshot accounting, texture/world identity propagation, and cross-file
lifecycle wiring. The complete fast tier passed **5,957 assertions with zero failures**.

Version 0.3.166 was packaged as a 14-entry archive with preserved asset paths, matching manifest and
assembly versions, no PDB or game dependency, and SHA-256
`7B9CAD84F76229A4252109B188CC32AE787E5FAF9ED624F1C59C570F08A17C94`. It was copy-installed beside
the owner's rollback set without replacing or removing another artifact.

---

## Delivered

- Corrected all five lifecycle/performance findings from Session 71.
- Added focused executable coverage for resident texture-slot repair, off-mode snapshot omission,
  and mesh world identity, plus static integration assertions for teardown and atomic table refresh.
- Built, verified, packaged, and copy-installed private test version 0.3.166.
- Source-traced and documented the still-open interior placed-block invalidation defect; no source
  change for that defect is included.
- Updated architecture, changelog, gotchas, TODO/completion history, current status, and this index.

## Decisions

- Keep texture-slot backfill on the owning thread and bound it by both entries and elapsed time;
  block and atlas registries remain unsafe worker inputs.
- Gate textured mesh publication on the whole resident L0 source being prepared, avoiding a mixed
  transition where some old palettes still carry zero.
- Preserve a previously valid GPU lookup table across refresh failure only while published geometry
  still depends on it.
- Record the placed-block invalidation bug separately rather than folding an unreviewed sixth source
  change into the already verified lifecycle pass.

## Traps

- A bitmask can validly be zero after real work. If zero also represents “no change,” the caller
  needs a separate changed flag; treating the mask as a boolean silently loses invalidation.
- Clearing a concurrent queue cannot cancel a worker that already owns a job. Publication identity
  must be checked before any bookkeeping mutation.
- Turning a feature off at the shader is not the same as removing its CPU preparation. Resolver
  calls and snapshot payloads need the same master gate.
- Rebuilding a live GPU lookup table in place destroys the fallback old geometry still needs if
  upload fails halfway through.

## Flagged and unverified

Judgement calls awaiting human review:

- Forest Floor's corrected top and the off/on/startup-off toggle states have not been viewed in game.
- Rapid toggles, a busy world change, and the GL refresh-failure fallback have no runtime exercise.
- The placed-block invalidation correction remains to be implemented and accepted.

Claims lacking their evidence level:

- No controlled benchmark quantifies the disabled pipeline's performance benefit.
- The 0.3.166 lifecycle behavior is built and harness-tested, not human-tested.
- The placed-block defect is source-proven but has not been reproduced in a running client.
