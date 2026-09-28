# Session 110 — Claim re-reads fixed, A4 dropped, and the hitches traced to garbage collection

**Date:** `2026-09-26..28`
**Branch/commit:** `codex/water-depth-light`; `6e1d718` (0.4.128), `427b96c` (A4 docs), `5e4d72c` (0.4.129), `5fe56a8` (0.4.130), `bceed3e` (0.4.131)
**Mod version:** `0.4.128` through `0.4.131`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The owner returned from a week away, asked what was next and whether the savegame-sweep plans were
finished, and said he did not want to leave a plan almost finished. Everything was built except A4;
the open items were evidence. His last play before the break was 0.4.125 on `test2`
(2026-09-21 18:37-19:07), which showed an undocumented line at 18:45:58: "The savegame read path
stopped answering requests to re-read captured columns". He also reported hitches - periodically low
minimum FPS - while exploring, and later asked for a diagnostic that records them, because he cannot
keep track of them by eye.

## Work narrative

### 1. A4 dropped

A4's clean-shutdown fast path cannot be built as specified (G236: no hook after the final save) and
its payoff fell to 73.3 s of off-thread reading once A3 removed the probe pass. He had accepted the
recommendation in Session 103; this session recorded it as dropped (`427b96c`).

### 2. The claim re-read give-up (0.4.128)

The claim re-reads after a world save were answered by the read path but collected only inside the
audit step, which runs after the sweep's load pass (`Done`) and only while the audit is enabled. His
first autosave came at 18:42:51 during the load pass; three one-minute timeouts later, at 18:45:58,
claim refresh turned itself off for the session - to the second. Claims then all came from the
startup reading. 0.4.128 collects read-path answers every tick in every phase and gives claim
re-reads their own queue ahead of the audit's capped one (G257). A read path whose thread stopped is
caught at once (`CanReread`).

Headless sandbox evidence (`scripts/claim-reread-sandbox.ps1`, a copy of the frozen `bodanboys`
seed, no player, autosave 120 s): 0.4.127 gave up at 295 s exactly as predicted with 0 of 5,530
claims read back after a save; 0.4.128 read back 5,459 of 5,459 with no give-up, and a restart
skipped 5,599. In his real 0.4.129+ play all 73 claims were read back after a save, but that sweep
finished in 25 s, before the first autosave.

### 3. Terrain-change acceptance tests in the sandbox

- **Tests 2, 3 and 16** (terrain changed while the mod was absent, WAL world): a 6-minute server run
  with no mods changed nothing, so the bottom two chunks of five claimed columns were swapped in the
  copied save. `scripts/fingerprint-dump` plus `scripts/claim-check.py` predicted 6,445 of 6,470
  claims still matching; the next start reported exactly 6,445, re-claimed all five edited columns
  with their new fingerprints, and rewrote no other claim except 18 already stale.
- **Test 5** (crash): the server was force-killed at 170 s with both databases mid-write (WAL 24 MB
  and 27 MB). Both passed `integrity_check`, 8,362 claims survived, and the restart reported exactly
  the predicted 8,353.

### 4. The hitch recorder (0.4.128-0.4.130)

`.vhhitch`, on by default: it switches the engine's own frame profiler on silently (threshold out of
reach), marks this mod's render callbacks inside it, adds .NET GC pause time per frame, and writes a
report a minute: 1%/0.1% lows, what was unusually slow in the slowest 1% of frames (each step judged
against its own moving average, including frames where it did not run), the worst frames, and the
singleplayer server's sweep, snow-refresh, audit and re-read work.

0.4.128 crashed his game twice on opening the world-edit menu: `current[Id(code)] += ms` read the
array before `Id` grew it at the 129th step name (G258). 0.4.129 fixed it and added a safety net:
any exception in the recorder logs once, restores the profiler and switches it off (G259).

0.4.130 added who allocates: an in-process `EventListener` on the runtime's GC keyword samples an
allocation tick per ~100 KB (thread via OS thread description, object type) and collection starts
with their reason; this mod's own jobs are measured exactly with `GC.GetAllocatedBytesForCurrentThread`.
Offline, sampling at 25,000/s cost nothing measurable (2.05 s vs 2.00 s; GC pause 153 vs 151 ms).

### 5. What the reports showed

- The slowest 1% of frames are mostly .NET collections: 160-330 gen1 collections a minute while
  exploring, 10-20 ms each, 2-4 s frozen per minute. No collection was induced.
- While exploring new terrain (0.4.130): 840-930 MB/s allocated. The game's `chunkdbthread`
  365-470 MB/s, almost all one closure type (`<>c__DisplayClass17_0`); vanilla client tesselation
  75-235 MB/s, mostly large `Int32[]` (the main source of full collections); this mod about 25-30%,
  led by seam-fog rebuilds at 94-112 MB/s of large arrays (about two ~50 MB rebuilds a second).
- The runtime's allocation-tick type name omits a nested type's declaring type (G260), so the closure
  cannot be named from the log. `ServerSystemSupplyChunks.tryLoadOrGenerateChunkColumnsInQueue`
  rescans its request queue calling `loadOrGenerateChunkColumn_OnChunkThread`, whose captured
  parameter makes the compiler allocate a closure on every call; that is the leading suspect, and a
  worldgen mod's closure on the same thread is not excluded.
- When he ran over old terrain (0.4.131, 17:03-17:05) the chunk thread fell to 3-145 MB/s and total
  allocation to 200-520 MB/s, so the churn belongs to generating new terrain.

### 6. 0.4.131: reuse instead of allocate

Seam-fog builds use a scratch handed to one build at a time and a small pool of half-float arrays
returned when a newer field is published; the mesh threads rent their output arrays and the
renderer returns them after upload; the cave cull's filled section borrows the thread's workspace in
the mesher's path; the capture batch is rented. `PoolingChecks` compares every pooled path bit for
bit with the allocating one after dirtying the storage; breaking the fog distance reset or the batch
clearing on purpose makes it fail.

His 0.4.131 session: seam-fog builds fell from 94-112 MB/s to 0.3-2.2 MB/s; the mesh threads left the
per-thread top eight after the first minute; this mod's exact jobs came to about 25-60 MB/s. 1% lows
while moving were 63-103 FPS (0.4.130: 51-62). The chunk thread now carries 60-84% of all garbage
while exploring new terrain. He said the fog looked fine.

---

## Delivered

- **0.4.128** (`6e1d718`): claim re-reads collected every tick with their own queue; `CanReread`; the
  hitch recorder. **Documentation** (`427b96c`): A4 recorded as dropped.
- **0.4.129** (`5e4d72c`): recorder array-growth crash fixed, exception safety net,
  `scripts/claim-reread-sandbox.ps1` (`-Fresh`, `-WithoutMod`, `-CrashAfterSeconds`).
- **0.4.130** (`5fe56a8`): `LodAllocationSampler`, per-job exact allocation, report lines for
  allocation by thread and type, large objects, and collections by cause.
- **0.4.131** (`bceed3e`): fog scratch and half pool, pooled mesh output, borrowed cave-cull rebuild,
  rented capture batch, `PoolingChecks`.
- **Checks:** 25,913 assertions pass. All four builds verified, copy-installed and owner-played; no
  push.

## Decisions

- **A4 dropped** (owner, 2026-09-26). Reviving it needs "record what the next save will touch, treat
  those as unverified", and it still trusts that the mod saw every change.
- **A build commit leaves nothing behind** (owner, 2026-09-26): pending documentation and tools are
  committed with it, overriding CLAUDE.md's narrower rule (now updated).
- **The whole-section `Runs`/`ColumnStart` rebuild on every changed capture stays.** Mesh and mip
  snapshots share those arrays by reference; reusing them would need reference tracking.
- **Mesh output is pooled only on request** (`pooledOutput`): tests and tools read results by array
  length and may hold several at once.
- **The recorder and sampler stay on by default** while the stutter is being chased; the offline
  benchmark showed no measurable cost.

## Traps

- **An answer collected only by one consumer's step is lost when that step is not running.** G257.
- **`array[Lookup()] += v`, where `Lookup` can replace the array, writes into the old array.** G258.
- **A diagnostic inside the render loop can take the game down.** G259.
- **The GC allocation-tick type name omits a nested type's declaring type.** G260.
- **An idle dedicated server with no player changes no terrain**, so "terrain changed while the mod
  was absent" cannot be staged by running without the mod; edit the copied save instead.
- **A read-only open of a WAL database still touches its `-shm`.** Read copies of frozen seeds.

## Flagged and unverified

- **The recorder's own minute report costs 14-58 ms on the render thread** at +0 s of each window
  ("vh:hitchrecorder" in the worst frames of 0.4.129-0.4.131); it should format off-thread.
- **A ~415 ms freeze inside the game's end-of-frame step (`finfr`)** appeared once in each of the
  0.4.129 and 0.4.131 sessions; not garbage collection; cause unknown.
- **The closure churn's owner** (vanilla supply-chunks loop or a worldgen mod) is inferred, not proven.
- **Tests 2/3/16 and 5 are sandbox evidence**; the plan asks for the owner's own runs.
- **Section sync with a second player** remains untested; his singleplayer logs show only the one-time
  migration line (6,247 sections, 2026-09-28 06:43:41).
