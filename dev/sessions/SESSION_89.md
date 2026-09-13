# Session 89 — GPU validation, durable cache draining, and client-copy handoff

**Date:** `2026-09-12`
**Branch/commit:** `codex/water-depth-light`; source through `28746a7`, documentation handoff commit recorded by Git history
**Mod version:** `0.4.67`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The owner asked to begin P1 of `dev/plans/PLAN_WORLD_GENERATION_AND_LOD_COVERAGE.md`, after first checkpointing two uncommitted planning documents. The visible failure was a generated world whose distant terrain appeared as thin strips with large sky holes. The owner's 2026-09-12 18:23 Vintage Story screenshot supplied the visual reference. Owner decisions W1-W8 remain open and must be asked before P5-P7.

The first P1 build exposed a concrete engine mismatch: the client API reported a 2560x1440 frame while the primary framebuffer, depth texture and viewport were 1920x1080. P2 then addressed a separate server-side persistence bottleneck. The owner's 0.4.67 run proved that saving now drains, but also proved that P2 alone cannot repair already-held incomplete client sections. No P3 source edits were made before this handoff.

## Work narrative

### 1. Planning checkpoint

The two pre-existing world-generation planning documents were committed as `fd56c34` (`docs: plan horizon coverage and world generation`) before implementation began, as the owner requested.

### 2. P1 / Fix A — GPU depth-copy validation

Version 0.4.65 made the required depth-copy probe validate against frame dimensions and added retry/rearm diagnostics. It built and passed tests, but the owner's `.vhgpu on` run failed because the game API's nominal frame dimensions were not the dimensions of the framebuffer actually being copied.

Version 0.4.66 corrected the authority: validation now prefers the concrete primary `FrameBufferRef` dimensions, with API dimensions retained only as fallback and diagnostic evidence. The owner reran it and reported, "It worked now. Good." Release and Debug builds completed with zero warnings/errors and 23,248 fast assertions passed. The verified package SHA-256 was `18B1B05C36BE2E6DDF03BF3DF2B1CAD3934EB8EE6C3DD330721C6595BF04E8FD`; source checkpoint `0ed190c`.

### 3. P2 / Fix B — bounded server cache draining

Version 0.4.67 replaced unordered dirty-section selection with first-dirtied order. Redirtying preserves the key's position and age; only an exact durable acknowledgement removes the obligation. A checkpoint may snapshot up to eight 256-row transactions per tick while retaining the existing 2 ms / 512 KiB drain budget and prohibiting overlapping checkpoint transactions. If eligible dirty work remains after the storage backlog drains, the next checkpoint begins immediately instead of waiting another 30 seconds. Retry-delayed keys do not block newer eligible work.

The client and server pipeline now expose capture backlog, unload drops and capture errors. `/vhserver` reports dirty count, oldest dirty age, cumulative saves per second, total saved, total capture backlog, unload drops and capture errors. The same cache-work line is logged once per minute while sweep, pregen, generate or refresh work is active. W8 was deliberately not assumed: shutdown remains at 15 seconds.

Deterministic coverage includes ordered age semantics; 600 old keys under continuous 300-key inflow and a 256-row cap; immediate versus 30-second cadence; back-to-back fake-storage batches; and server-status fields. Release and Debug builds completed with zero warnings/errors and 24,475 fast assertions passed. The verified 39-entry package had root manifest/DLL, no PDB or private DLL, SHA-256 `402A45B0E3753B0988A46DC1C6F4D793C648F58AD6B0AC507003C11B677C4FC8`, and an identical installed copy. Source checkpoint `28746a7`.

### 4. Owner runtime evidence and remaining visual defect

The owner's integrated-world log proved 0.4.67 loaded and `/vhrefresh 32` ran near block 255936,255680 without generating terrain. The startup sweep remained active, status appeared both on demand and once per minute, and capture backlog stayed between one and five with zero unload drops and zero capture errors. Dirty work fell from 32 at 102.7 seconds oldest and 189 saved, to 8 at 12.0 seconds and 255 saved; later readings were 17 at 9.5 seconds and 308 saved, then 21 at 23.6 seconds. Refresh reached at least 90% (3803/4225), with zero absent/skipped rows and no unresolved-save warning.

This establishes the P2 runtime gate for normal integrated load: the cache drains and stays under two minutes. The greater-than-256 saturation case remains harness evidence because this refresh mostly rediscovered identical content.

The owner saw no visual change and no holes fill. That is expected evidence for P3, not a P2 regression: the current client refuses a fuller server/local offer once it already owns the same section key. Returning to the world without generating more vanilla terrain is the correct P3 acceptance setup; the planned merge should fill only columns absent from the client copy, while client-captured columns always win.

### 5. Exact P3 continuation point

Implement P3 / Fix C and D4 next. Start by reading the complete files below; the previous investigation was read-only and some combined terminal output was truncated:

- `VintageHorizons/src/Lod/LodLocalOfferSource.cs`
- `VintageHorizons/src/Lod/LodRemoteKeySet.cs`
- the foreign decode/install path in `VintageHorizons/src/Lod/LodPipeline.cs`
- `VintageHorizons/src/Lod/LodSection.cs`
- `VintageHorizons/src/Storage/LodStore.cs`
- `VintageHorizons/src/Storage/LodStorageThread.cs`
- `PumpLocalOffers` in `VintageHorizons/src/VintageHorizonsModSystem.cs`
- `VintageHorizons/src/Net/LodAssistClient.cs`
- `RemoteKeyChecks.cs`, `StoreChecks.cs`, and `ServerAssistChecks.cs`

The intended design is:

1. Replace the two-second full sibling-cache polling loop with an incremental rowid scan: `SELECT Detail, SX, SZ, ModifiedMs FROM Section WHERE rowid > @last`. SQLite `INSERT OR REPLACE` gives a rewritten row a new rowid. Do one full scan at start and rare safety scans.
2. Publish changed keys at every detail level. Current update tracking is explicitly level-zero only.
3. Fill, never overwrite. For an existing client-held section, palette-remap and copy only columns it has not captured; merge captured/snow bits consistently. Client-captured columns always win.
4. Record the last merged server rowid per key in a small additive client table so restarts do not repeat work. Existing schema meaning remains 6 unless implementation proves otherwise, but record the additive table in `dev/WIRE_HISTORY.md` when delivered.
5. Keep decode/merge revision-checked so an asynchronous result cannot replace newer content.
6. Audit the dedicated-server level-zero partial-copy path under the same fill-only rule.
7. Add deterministic palette/captured/snow merge tests, a real-SQLite `INSERT OR REPLACE` rowid test, held-key delivery at every level, and request-lifecycle coverage. Measure D4 scanner CPU.

Current source behavior to replace: `LodLocalOfferSource` uses discovery and blob-reader workers, full scans every 2 seconds, and level-zero-only update deltas. `QueueForeignBlob` sets `replaceExisting = Remote.IsUpdate(key)`, and `InstallForeignSections` chooses whole-section `World.InstallReplacement` or `InstallLoaded`; that whole replacement is incompatible with the required client-wins fill rule. `LodStore` schema 6 has `Section(Detail,SX,SZ,Data,ApplyToParent,ModifiedMs)` and uses `INSERT OR REPLACE`.

Do not proceed to P5-P7 until W1-W8 have been put to the owner. P4 may ride with P3. P0 can be performed when a restart is convenient, but P6 is gated on its identical-terrain result.

---

## Delivered

- Planning baseline commit `fd56c34`.
- P1 0.4.66 accepted runtime correction at `0ed190c`; 23,248 assertions; verified/copy-installed package.
- P2 0.4.67 at `28746a7`; 24,475 assertions; verified/copy-installed package; normal integrated runtime drain established.
- Durable P3 continuation record with no partial P3 source changes.

## Decisions

- Concrete primary framebuffer dimensions, not nominal API frame dimensions, own depth-copy compatibility validation.
- Dirty persistence is an ordered obligation; redirtying does not surrender age or queue position.
- Existing checkpoint budgets and 256-row transaction cap remain; throughput comes from bounded repeated snapshots and immediate follow-up after backlog drain.
- W8 remains open, so shutdown stays at 15 seconds.
- P3 must fill missing columns and never overwrite columns already captured by the client.
- W1-W8 are still open. Ask the owner before P5-P7; do not infer them from plan proposals.

## Traps

- Render scaling can make the API-reported frame size disagree with the actual primary framebuffer and depth attachment. Validate concrete resources first.
- Fixing server capture/save throughput does not repair a stale client section when key ownership suppresses the offer or whole-section replacement is unsafe.
- A capped unordered dirty set can keep choosing newly dirtied keys and starve old obligations despite apparently healthy aggregate save activity.

## Flagged and unverified

- P3 is not implemented. The 18:23 world's holes are the intended acceptance target after its fill-only merge is built; no additional vanilla terrain generation should be required.
- The P2 greater-than-256 continuous-inflow gate is deterministic harness evidence, not a comparable runtime stress run.
- D4 incremental scanner CPU is unmeasured.
- P4's sweep-during-pregen false alarm is not implemented.
- P0 and every W1-W8 owner decision remain open. P5-P7 must pause for those decisions.
