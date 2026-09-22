# Session 109 — Section versions: a returning player gets what changed while away

**Date:** `2026-09-22`
**Branch/commit:** `codex/water-depth-light`, 0.4.126 at `e668f81`, 0.4.127 at `89509e9`
**Mod version:** `0.4.127`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Source and checks remain authoritative; the owner's in-game
> observations are the acceptance evidence for appearance.

## Context and investigation

The owner asked whether the savegame-sweep work was finished, and whether both of its plans - the
[scheduling analysis](../plans/ANALYSIS_SAVEGAME_SWEEP_SCHEDULING.md) and the
[terrain-change detection plan](../plans/PLAN_TERRAIN_CHANGE_DETECTION.md) - had been carried out.
The review found:
- The analysis is essentially finished. Multiplayer fairness, per-waypoint readiness, the snow
  refresh's cost and winter convergence were never measured.
- Phase A of the plan is built and played, except A4, which cannot be built as specified.
- **Phase B had not been started.** Source confirmed it: the refresh flag was still there, and
  nothing versioned sections.

Phase B fixes a defect rather than an inefficiency, traced in source in §4.2.1:
- A player's game asks a server only for sections it does not hold.
- The live refresh reached only players online at the time.
- So a section that changed while a player was away was never offered to them again, and their
  cached copy stayed stale for good.

The owner said "Let's do Half B".

Outside scope: A4, the claim/audit ordering, the snow refresh's cost and the exploring-hitch
investigation.

## Work narrative

### 1. One question to the owner before building

The plan says a client whose record does not match "refetches from scratch". The owner's P3 rule
(2026-09-12) says the first time a client sees a server row, it only fills gaps. On a
bodanboys-sized world, refetching is about 15,000 sections: roughly 30 minutes and 700 MB of
background downloading per player, repeated after every server-cache rebuild. That was put to him,
with "keep what it has, track from now" recommended. **He chose "re-check everything once".** It was
built resumable, so the re-check really is once: the owed list is written to the client cache, and a
quit loses nothing.

### 2. Server: versions, a generation, and spans

- `Section` gains a `Version` column in place (`ALTER TABLE`), with no schema bump, for the reason
  the fingerprint table avoided one: a bump wipes the cache.
- Every checkpoint transaction takes one new version and writes the counter into `Meta` in the
  same transaction.
- A generation names the lineage. It is re-minted after a format purge, and whenever the insert
  trigger `SectionLegacyWrite` has seen a row written with version 0 - which is what any build from
  before this one writes.
- Rowids were rejected as versions: `StoreChecks.AStoreRewriteGetsANewRowid` already proves a rowid
  is reused after the highest row is deleted.
- Joins and 30-second broadcasts are both answered by the assist reader thread from one FIFO.
  `LodSectionSyncLedger` applies the results in that order. That order is what keeps each player's
  spans contiguous. A broadcast applied after a player's join was queried after it, so it began no
  later than the join ended. The first broadcast's baseline is taken from the committed counter
  before any join is queried, not from a query of its own; with a query, a player who joined first
  would lose the rows in between.
- The in-memory set of persisted keys the old refresh drained is gone. Players who do not sync by
  version receive the broadcast's keys as the plain refresh they always read.
- `AssistSection` carries the version of the row it was read from.

### 3. Client: a record, an owed list, and nearest-first fetching

- `LodAssistSync` holds the recorded generation and version and the owed sections, each with the
  version it was announced at.
- A span's debts and the version it reaches are one storage write. After any failed write the
  storage worker stops recording versions for the session.
- A span that does not continue the one before it is still fetched, but not recorded.
- Owed sections are handed to the pipeline nearest first, at most 32 at a time, inside the server's
  serving radius. A refused section cools down, doubling, and after three refusals waits for the
  next session.
- The pipeline reports how each such fill ends. Network merges carry the server version in the
  slot a sibling merge uses for its rowid, and never write a sibling merge record.
- A debt settles only when a copy at least as new as announced is durable. A newer announcement
  arriving mid-fetch keeps the fill open (G7).
- Integrated play does not take part.

### 4. Measured on a copy of the owner's cache: the index the planner ignored

On a copy of his largest server cache (1.85 GB, 30,584 sections, 22,801 at level 0):
- The migration costs 3.6 ms for the `ALTER` and 62 ms for the index.
- `EXPLAIN QUERY PLAN` showed the first index shape, `(Version, Detail, SX, SZ)`, going unused. The
  planner took `SectionKeys` for the equality on `Detail` and would have read each row's version
  from the row itself, past the blob (G255).
- The shipped index leads with `Detail`, and every query names it. A full re-check list then took
  9.5 ms and a live broadcast 0.02 ms, from the index alone.

### 5. The sandbox integration run, and the fix it forced

The owner asked for the tests to be run for him. The run used a dedicated server and a client in an
isolated sandbox profile (`phaseb`), four joins through `scripts/bench-windows.ps1`. It was seeded
with the default benchmark world, stood at the benchmark spawn, used the default serving limits and
had the sweep off. Changes "while away" were written into the sandbox server cache between joins,
the way a server save would make them. The route, config and injection script are now
[`bench/routes/section-sync.txt`](../../bench/routes/section-sync.txt),
[`bench/configs/section-sync.json`](../../bench/configs/section-sync.json) and
[`scripts/section-sync-inject.py`](../../scripts/section-sync-inject.py). Each result was predicted
from the two cache files before the run.

| Join | Setup | Result |
|---|---|---|
| 1 (0.4.126) | Fresh server cache, cold client | From zero with nothing held; the server's live saves reached the client as spans, 71 sections merged; quit with 8 owed |
| 2 (0.4.127) | 10 held sections rewritten at a new version | Server: caught up to 5, **16 changed since, up to 7**. Client: 16 held, 18 owed in all, all merged and saved about 30 s after joining; the scrambled rows replaced 41,031 columns and the server's recapture corrected them live |
| 3 (0.4.127) | Nothing changed | Exactly the **19 rows of the server's final save** announced, all held, caught up in 31 s |
| 4 (0.4.127) | A version-less insert, as an older build writes | Server logged the older build's write and re-minted the generation; the client **re-checked all 59 held sections** in 31 s under the new generation |

Join 1 measured catch-up at about one section a second, where the server serves eight. A merged
section held its fetch slot until its save, and saves are 30-second checkpoints (G256). 0.4.127
reports "merged" separately from "durable": the slot frees at once, and the section is not fetched
again while its save is pending. Joins 2-4 ran on it.

Test 9 cannot be staged literally in a live world. With a player standing still, the server
re-saved nearby sections every checkpoint as blocks ticked, and it always saves once more at
shutdown. Join 3 is the stricter form: exactly those rows and nothing else.

---

## Delivered

- **0.4.126** (`e668f81`): section versions and generations on server caches; the client's record
  and owed list; versioned join and live spans; nearest-first, radius-aware owed fetching; the
  appended protocol fields.
  - Artifact: 725,445 bytes, 45 entries, SHA-256
    `3484247FC240B01C4675CA01CB5C1893B82309A347A2448F631A6BD6B922831D`.
  - Verified and copy-installed; superseded by 0.4.127 before any play.
- **0.4.127** (`89509e9`): a merged section frees its fetch slot before its save.
  - Artifact: 725,515 bytes, 45 entries, SHA-256
    `B3B4A01AE824A563B6393E597A373E91A4944D9D7A7203D3465253DC21AE8CAF`.
  - Verified, copy-installed, sandbox-integration-tested. Not owner-played.
  - Also the first build carrying the owner's fog defaults from Session 108.
- **Checks:** `SectionSyncChecks` (148 assertions). The whole-session check walks the plan's
  acceptance tests 8-10 against real SQLite files. `WireFormatChecks` pins the appended fields and
  reads each pairing against frozen 0.4.125 shapes. `BenchRouteChecks` pins the section-sync route
  and config. The fast tier passes 25,610 assertions, zero failures; Release builds are error- and
  warning-free.
- **Tools:** the section-sync route, config and injection script above. The sandbox profile stays
  in `.testdata/profiles/phaseb` for re-runs.
- **Documentation:** this record; the plan's status and "how it was built" note; architecture
  decision 52 and a pointer from 32; the wire ledger; G255-G256; TODO/DONE, STATUS, CHANGELOG and a
  routing line in `CLAUDE.md`.

## Decisions

- **No record re-checks everything, newest winning** (owner, 2026-09-22). This was chosen over
  "keep it, track from now" at the cost of one background re-download of the overlap. The re-check
  is resumable.
- **The client keeps an owed list, not one number.** One number cannot advance past a section that
  is refused or unfinished without either losing it or re-fetching the whole span every join.
- **The refresh flag stays on the wire.** The server has one account of what changed, but §4.2.4
  requires older clients to keep today's behaviour.
- **A dedicated version column, one version per checkpoint.** Versions only have to grow, and
  rowids can be reused.
- **Older-build writes are caught by a trigger**, which costs such a build one Meta write per row.
  Counting version-0 rows at open was rejected: an older build rewriting a row that was already at 0
  leaves the count unchanged.
- **Integrated play does not take part.** The sibling cache's rowids already say what changed, and
  two accounts of one fact is how they disagree.
- **A newer-wins merge that falls back to fill-only**, because the client captured the section
  meanwhile, still settles the debt. The client's own capture is the newer copy of whatever it
  covered.

## Traps

- **An added column is read from the row, past the blob**, unless an index leads with the equality
  and the query names it. Check the plan on a copy of a real cache (G255).
- **A fetch slot held until durability** limits catch-up to one cap's worth per 30-second checkpoint,
  about one section a second. Offline checks call the durability report directly and cannot see it
  (G256).
- **"Nothing changed" cannot be staged with a player present.** The server keeps re-saving nearby
  sections as blocks tick, and saves once more at shutdown. Predict the exact rows from the two cache
  files instead.
- **A log watcher that tails two concatenated files** re-emits the second file's lines whenever the
  first grows. Watch each file on its own.

## Flagged and unverified

Judgement calls awaiting human review:
- The owner has not played 0.4.127. In singleplayer the client path is idle. The server cache should
  log "Adding section versions to this server cache" once, and nothing should look different.
- A stationary player re-fetches every section the server re-saves near them, about 66 merges in two
  minutes in the sandbox. This is behaviour from before this session, and a candidate for the
  exploring-hitch investigation.

Claims lacking the required evidence:
- **No second player has been involved.** Changes "while away" were injected into the sandbox server
  cache between joins; everything after that ran for real.
- **The 30-minute, 700 MB re-check figure for a bodanboys-sized world is arithmetic**: about 15,000
  held sections at the server's default 8 per second. It has not been measured.
- **A real older build writing to a versioned cache** was simulated with its exact upsert statement,
  not run.
- **A server cache restored from an older backup** is detected only when the client's record is
  newer than the restored copy.
