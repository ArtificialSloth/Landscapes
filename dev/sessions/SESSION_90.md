# Session 90 - P3 client copies complete, and the sweep paced to capture

**Date:** `2026-09-12`
**Branch/commit:** `codex/water-depth-light`; 0.4.68 `52d3aa2`, 0.4.69 `ae677b7`, documentation commit recorded by Git history
**Mod version:** `0.4.68-0.4.69`
**Assist protocol / blob / schema:** `1 / 4 / 6` (additive client table `SiblingMerge`; no meaning changed)

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The session picked up Session 89's continuation point: P3 / Fix C plus D4 of
`dev/plans/PLAN_WORLD_GENERATION_AND_LOD_COVERAGE.md`. The acceptance case was the finished 18:23
world (`d73a7a95`, "peaceful adventure valley"), whose far terrain drew as thin walls because the
client had copied each far section once, while pregen had filled only its western edge, and never
took a fuller server row for a key it already held.

Reading the install path before building showed a conflict the plan had not named. The plan and
Session 89 said client-captured columns always win. But the level-0 whole-section replacement being
removed was the only route by which server-side snow refreshes (dormant winter campaigns and
`/vhrefresh`) reached ground the client already held. The owner was asked before implementation and
chose: the server's newer copy wins at level 0.

## Work narrative

### 1. The merge rule as built

For a key the client holds, the first sibling row it sees only fills the columns its copy lacks.
A level-0 row the server rewrote after the client saw or merged it replaces the client's columns
one by one. It never erases a column only the client has, and never replaces an observed-snow
column with an unobserved (generated-peek) one. Parents (L1 and up) only ever fill, because the
client rebuilds them from level 0. If the client captured the section after the server copy was
handed to the decoder, the merge falls back to fill-only for that pass (G7). `LodSection.MergeFrom`
remaps palettes by block id, as mip publication does, and carries snow provenance and coverage
with each copied column.

### 2. Scanner (D4), fill bookkeeping and records

`LodLocalOfferSource` no longer reads every row's `ModifiedMs` every 2 s. That column is stored
after the blob, so the old query walked every row's overflow pages: 828 ms of CPU per scan on the
owner's 1.8 GB server cache. It now reads `rowid, Detail, SX, SZ` for rows above the highest rowid
seen (about 0.1 ms), plus a full key scan once a minute or whenever the maximum rowid falls, since
SQLite reuses a deleted top rowid (G189). A full key read is answered from the primary-key index in
15.6 ms. Rows are published at every level; blob reads report their rowid.

`LodRemoteKeySet` turns sibling rows into offers (keys the client lacks) or fills (keys it holds).
A first-sighting fill waits for the view to load its section; a complete copy is accounted without
a read. A rewritten level-0 row is eager: its target is loaded and pinned (`LodWorld.EvictionPins`,
at most 16 at once). Every request ends merged, deferred (target left RAM) or abandoned, and an
unreadable own copy falls back to adoption. Reloads of held keys always go to local disk.

A merge is recorded in the additive client table `SiblingMerge (Detail, SX, SZ, SourceRowid)` only
after the merged copy is durable (G16), so a crash repeats a merge rather than skipping it. Format
purges and unreadable-row deletes take the records with them. The dedicated-server refresh path
merges the same way; the network carries no rowid, so nothing is recorded there.

### 3. Checks, tooling and the first playable build

New `SectionMergeChecks` (41 assertions); `RemoteKeyChecks` rewritten for fills (107); the offer
reader tested against the real `Section` schema and `INSERT OR REPLACE`; a real-store rowid check
(rewrite numbered above every row; deleted top rowid reused); merge records across reopen, delete
and purge; a retryable network completion. The benchmark script's sibling-offer parser had not
matched the stats line since an earlier build inserted a `refreshed` field; it now matches.

A new offline command, `siblingfield CLIENT.db [SERVER.db] [--scan-seconds N]`, compares a client
cache with its sibling per level and times the scan queries. On a frozen copy of the 18:23 world,
before the fix: 645 of the client's 2,912 rows were behind the server, 1,471,451 columns lacking
(L4 95 of 100, L5 34 of 36, L6 9 of 9). The shipping scanner over 130 s on the same file was busy
0.056% of one thread.

0.4.68: Release and Debug builds clean, 24,584 assertions, 38-entry zip SHA-256
`B1A894BC8C41C26E0A3D383F8740A81DE7B63F2F153736EFE3B7E3E833A81D53`, installed, `52d3aa2`.

### 4. The owner's run

The owner loaded the world on 0.4.68 and reported, "The vast majority of terrain is filled in now."
The client log agrees: 760 sections merged in the first four minutes and, by 23:31, 2,207 merged,
1,468,006 columns filled, 10,231 replaced by newer server saves and 490 fills waiting for their
sections to be on screen. The scanner, with the server writing, stayed at 0.04-0.11% of one thread.

### 5. The remaining thin strips

A read-only per-chunk map of the live server cache, client cache and savegame covered the 301 x 301
chunk pregen square. At 23:35 the server lacked 2,541 chunk columns: 2,461 are at vanilla's pass 5
of 6 (unfinished), 80 are finished but were never captured, and none are absent from the savegame.
The unfinished ones form one-chunk-wide north-south lines, which matched the strips in the owner's
top-down screenshot once rotated. The owner had been flying over bare spots to make vanilla render
them, which finishes them; that explains the counts falling during the measurement (G191).

### 6. 0.4.69: the sweep outran capture

The same session's server log showed the startup savegame sweep dropping columns: capture backlog
up to 792 and 4,043 unloaded-before-capture drops. The sweep loads existing columns without keeping
them loaded, at its configured rate regardless of the capture queue, so queued columns were
unloaded before capture reached them (G190). 0.4.69 holds loads while 32 or more columns are queued
or being captured (`LodSavegameSweep.LoadsAllowed`). 24,588 assertions; Release and Debug clean;
38-entry zip SHA-256 `BD48A3BE96ED05B3A153C94BEA2F9E77EA2BA3CE775D31EE213010BE81B3850A`, installed,
`ae677b7`.

---

## Delivered

- 0.4.68 (`52d3aa2`): P3 / Fix C plus D4 - incremental rowid sibling scan at every level, column
  merge into held sections under the owner's rule, `SiblingMerge` records, network refresh merge,
  `.vhinfo` and a once-a-minute log line for held-section progress, `siblingfield`, tests.
- 0.4.69 (`ae677b7`): savegame sweep paced to the capture queue, with checks.
- Human-tested: the 18:23 world's far terrain draws nearly complete on 0.4.68.
- Documentation pass for P3 and 0.4.69.

## Decisions

- Owner, 2026-09-12: a level-0 row the server rewrote after the client saw it wins column by
  column; first sightings and parents fill only. Rejected: the plan's client-always-wins rule,
  which would have stopped server snow refreshes reaching held ground.
- A merge reads and writes the live section in one owning-thread step, so only the source decode
  is asynchronous; newer-wins additionally requires the content revision unchanged since hand-off.
- Merge records are written only after the merged copy is durable.
- The sweep waits for capture rather than keeping swept columns loaded; keeping columns loaded is
  the generation job's design (P5).

## Traps

- G188: a held key was skipped forever and the only refresh replaced whole sections.
- G189: `rowid` scans need a full-scan backstop, and a column after the blob costs an overflow walk.
- G190: a producer that loads columns without keeping them must pace to the capture queue.
- G191: vanilla pregen leaves one-chunk-wide lines at pass 5; the mod cannot draw them.

## Flagged and unverified

- 0.4.69's pacing is harness-tested only; its effect on drops and sweep duration is unmeasured.
- The offline after-comparison (`siblingfield`) was not run after the owner's revisit.
- Why vanilla left those lines at pass 5 is not established; that flying near them finishes them
  rests on the owner's report and falling counts, not on engine source.
- Dedicated servers still cannot fill a section a client held before joining.
- Snow refreshes still do not reach far parents; they never did.
- P4 (sweep false alarm during pregen) was not attempted. W1-W8 remain open; ask before P5-P7.
