# Session 103 — reviewed the change-detection plan, measured the frontier, and built Phase A

**Date:** `2026-09-19` to `2026-09-20`
**Branch/commit:** `codex/water-depth-light` / `839e864a` (0.4.116)
**Mod version:** `0.4.112` through `0.4.116`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The owner asked what was next, then directed the work in order: review the Codex recommendations
appended to the terrain-change plan, run the sustained virgin-frontier measurement Session 102 left
open, fix the gate threshold he had already spotted himself, and then build Phase A.

Two of his own verdicts arrived during the session and closed standing gates. He accepted the seam
fog after playing 0.4.111, which closed the 0.4.110 shape gate. He also reported that the savegame
sweep was hard to tell apart in play and that play feedback on it would not be reliable — which
turned out to be exactly right, for a reason neither of us had assumed.

Outside scope: the Farseer P4/P5 observation matrix, the multiplayer gate (still no second player),
and the player-facing Farseer rename.

## Work narrative

### 1. The §11 review, and three corrections to the plan proper

The Codex recommendations were directionally right and none was dangerous, but two rested on
premises that were not true and the section left open a silent-failure hole it inherited from
§§4.1–4.3 without noticing. Recorded as plan §12; §11's text was left intact.

Three findings were errors or gaps in §§4 and 7 rather than in §11, and were corrected in place:

1. **`GameWorldSave` fires before the engine writes, not after.** §4.1.4 asserted the opposite and
   built the fast path on it. `VintagestoryAPI.xml` documents the event as "Triggered **before** the
   game world data is being saved to disk", and `LodServerCaptureSystem.OnGameWorldSave` already
   relies on that ordering to flush the mod's own data into the same save. Later confirmed from the
   engine's own code (§6 below).
2. **Nobody said when a fingerprint becomes "stored".** Both readings were available and they differ
   by a permanent silent hole: a fingerprint written when a savegame row was *read* leaves a column
   whose capture then failed matching an unchanged file forever, invisible to both the next sweep
   and the §4.3 audit, which compares the file against that same wrong record. Resolved as §4.1.6
   item 3, with acceptance test 14.
3. **File size and mtime are weaker than SQLite's own proof.** The database header carries a
   four-byte change counter at offset 24 that increments on every commit and persists. Read off the
   owner's world: counter 651, version-valid-for 651, read/write format 1/1 — a rollback journal,
   matching §4.1.1's measurement. WAL only updates it at checkpoint, and the design already branches
   on journal mode.

Five further findings concern §11's own text and were left for the owner, the substantive one being
that §11.3 mis-states the current build: capture-backlog pressure has gated loads since 0.4.68, and
the storage signals it proposes adding already exist.

### 2. The owner's play session never reached the code it was testing

Read offline from his `server-main.log`. The 0.4.111 session ran 16:13:21 to 16:18:36 and had
examined 90% of 70,225 probe positions when he quit. The load phase never started, and 0.4.111's
gate guards `StepLoad` only, so it could not have held anything.

His "hard to tell a difference" was therefore correct for a better reason than assumed: there was
nothing to tell apart. The finding generalises (G233) and carries a product fact — on his world a
short session is spent entirely in the probe pass, capturing nothing, every start.

The client log for the same session also closed the log half of the fog gate: both silhouette
shaders compiled, zero `InvalidOperation`, zero GL errors, Farseer never disabled, clean exit.

### 3. The sustained virgin-frontier A/B

The route was grounded in the frozen seed rather than assumed: reading `mapchunk` read-only showed
the eastern frontier along the corridor at chunk X 8053, ragged between 8046 and 8061 across
Z 7980–8020. Three conditions, frozen seed restored per run, 360 s settle so the probe pass
finished first. The pre-gate arm was built from checkpoint `2fbf2d1`, whose only source difference
from 0.4.111 is the sweep itself.

| Virgin frontier, 5.33 blocks/s | Avg FPS | Median | 1% low | 1% tail | RSS | Oldest dirty |
|---|---:|---:|---:|---:|---:|---:|
| Sweep off | 523.6 | 578.9 | 172.9 | 5.78 ms | 7,076 MB | — |
| 0.4.111 gate | 517.4 | 576.6 | 165.9 | 6.03 ms | 7,685 MB | 57.3 s |
| Pre-gate (`2fbf2d1`) | 503.2 | 579.5 | 134.4 | 7.44 ms | 8,906 MB | 170.6 s |

The frontier costs what saved terrain cost: 22.3% of the 1% low and 28.7% of the tail against the
off control, close to Session 102's 19.9% and 24.8%. 0.4.111 recovers most of it. The storage queue
agrees — oldest dirty section 170.6 s ungated against 57.3 s gated.

### 4. The gate threshold is a speed, and 0.4.112 gives it a floor

The owner had already spotted this. The quiet window is renewed by every chunk-boundary crossing, so
above 32 blocks per 5 s — 6.4 blocks/s — each crossing lands inside the previous window and the hold
never lapses. Nothing chose that number; it fell out of two constants picked for other reasons.

Measured sweep throughput while moving, sections saved per minute mid-window: about 880 with the
pre-gate scheduler, about 110 held at 5.33 blocks/s, and one whole minute at 7 at 6.67 blocks/s.

0.4.112 clamps a held sweep to `FloorPerSecond` — one eighth of the configured rate, 2/s at the
default 16/s — drawn from its own allowance that only held ticks spend, so a long hold cannot burst
on release. The engine-generation gate stays absolute.

The first verification run showed the floor doing nothing, which looked like a broken fix and was
not: on a virgin frontier real generation is permanently in flight, so the absolute engine gate
returns a zero allowance on nearly every tick and the floor has nothing to clamp (2,478 engine holds
against 2,481 player holds). The floor only binds on saved terrain, where the engine counter is
blind (G229, G235). Re-measured there, same session, frozen seed per run:

| Saved terrain, 13.3 blocks/s | Avg FPS | 1% low | 1% tail | RSS | Loads under hold | Sections saved |
|---|---:|---:|---:|---:|---:|---:|
| 0.4.111, no floor | 313.6 | 104.6 | 9.56 ms | 8,094 MB | 0 | 1,732 |
| 0.4.112, 2/s floor | 317.3 | 103.2 | 9.69 ms | 8,478 MB | 193 | 2,015 |

Both differences are inside run variation, and hold counts were unchanged at 1,618 against 1,616, so
the gate still fires identically and only the trickle is new.

### 5. Phase A1 and A2 — the private read path and fingerprints

`LodSavegameReader` opens the world's own savegame a second time, read-only, on its own thread. It
detects the journal mode and never sets it, paces reads in short batches with yields (relaxed under
WAL), waits out `SQLITE_BUSY`, validates the schema rather than guessing at an unexpected one, and
disables itself for the session with one named line on any failure.

`LodSavegameScan` reads the existence map from `mapchunk` and one content fingerprint per column
from `chunk`, dimension zero only. Each row's hash is mixed with its own y and combined by XOR with
a row count, so the result is sensitive to a changed, added or removed layer without needing an
ordered scan — ordering by position sorts y-major and would scatter each column across the table.

`LodStore` gained a `ColumnFingerprint` table through `CREATE TABLE IF NOT EXISTS` with no schema
bump, so an existing cache gains it empty, a missing entry reads as "assume changed", and there is
no migration.

The checks prove the properties whose failure is silent, against real SQLite files rather than
mocks: that the connection cannot write and leaves rows intact after trying, that an unfamiliar
schema is refused, that the change counter advances on commit and not otherwise, that pacing does
not drop or repeat rows, and that a changed, shortened or neighbouring column fingerprints as it
should. Feeding identical bytes in five piece sizes found a real bug: the hasher dropped its carry
when a piece ended mid-word, so the same unchanged terrain could fingerprint differently between
runs and every column would have looked changed at random.

Against the owner's real 3.22 GB world the reader independently reproduced the measurements taken
by hand: delete journal, change counter 651, 64,179 map columns.

### 6. Phase A3 — the sweep consumes them, and the probe pass is gone

The sweep now waits for a whole-world index instead of probing. On failure it falls back to the
probe pass with a named line.

A column is skipped only when the savegame's fingerprint and the cache's proven one are both known
and equal. The commit point is the part that had to be right: a submitted column earns its
fingerprint only once `IsAuthoritativeChunkDurable` agrees the capture reached the durable row.

Two corrections during implementation:

- The rate allowance was bounding *decisions* as well as loads, which would have left a world where
  nothing changed still crawling through 66,049 positions a few per tick. It now bounds loads only;
  a column that needs a load and cannot afford one is put back and retried, preserving spiral order.
- Snow registration had to happen before the skip. 0.4.115 later moved that from an ordering inside
  a loop to an explicit rule over every outcome, with checks over the whole enum, because the plan
  names it the single easiest regression to introduce here and its failure is silent.

Sandbox-verified over two integrated runs on the frozen world. First adoption read 62,098 columns in
**6.4 s**, emitted no probe progress lines at all, and recorded 1,905 fingerprints as captures
reached the durable row. The restart, with that cache kept, read them back and skipped exactly those
1,905 columns, then fingerprinted 1,911 more. No read-path failures, no fingerprint warnings, no
capture drops, no exceptions. That sandbox world is WAL rather than a rollback journal, so the
journal detection and the WAL pacing branch both ran for the first time without being arranged.

### 7. Why A4 was not built as specified

Two findings, and together they change the step's shape.

**There is no post-save hook.** Decompiling the installed engine settles what the API docs only
implied. `ServerSystemLoadAndSaveGame` subscribes to `OnGameWorldBeingSaved` at `OnBeginRunGame`,
while mods subscribe during world load, so mod handlers run first and the write follows. At
shutdown, `OnBeginShutdown` triggers the same event and saves synchronously inside it. The only
phase after everything is `EnumServerRunPhase.Exit`, and `EnterRunPhase` **returns immediately for
`Exit` without notifying any system or mod**. So a clean-shutdown marker cannot record the file's
state after the final save, which is what §4.1.5 assumed.

**And the payoff shrank.** §4.1.5 was designed when a world start cost 5m35s of paced engine probes.
A3 removed that. Measured on the owner's real world with the mod's own code: the whole index is
**73.3 s**, of which fingerprints are 73.0 s. So the fast path would now trade an unverifiable
assumption for 73 seconds of low-priority background file reading that does not compete with the
player.

Recommendation recorded: do not build §4.1.5's fast path as specified, and do A5 first, because the
audit is what makes any such trust recoverable. The owner accepted and directed A5.

### 8. Phase A5 — the audit, and a seven-week-old dormant bug

The audit re-reads a rotating slice of the world through the same read path and compares it against
what the cache proved it captured. It only disputes claims this cache makes: an uncaptured column is
not an audit failure, or the audit would re-report the whole uncaptured world forever. A mismatch
drops the claim, forgets the stored fingerprint so nothing skips it meanwhile, and asks the engine
for the column through the ordinary load path — not `QueueColumn`, which serves only a column the
world already holds loaded, and an audited column is almost never near a player.

`AuditColumnsPerSecond` defaults to 16, walks a 57,600-column world about once an hour, and 0
disables it with no residual cost.

The first audit run re-read nothing at all. The cause was not in the audit:

**`verifying` was set when the absence verifier started and never cleared.** `Step()` begins with
`if (!active || verifying) return;`, so every tick after the startup sweep did nothing, in every
released build since `0596d30` (2026-08-02) — including the accepted 0.4.64 baseline. Automatic
dormant-terrain snow refresh, its canary and its regional campaigns have therefore never run once.
Nothing reported it because the status line reads configured state rather than whether work
happened, so it always looked healthy. This is the likely reason winter convergence has sat in the
debt list unresolved.

Fixing it turns on a load path that has never executed, and that path issued engine priority loads
with none of the brakes 0.4.111 and 0.4.112 added. Enabling it bare would have undone both in a
place nobody was watching, so the same change gives the refresh arm the capture-backlog brake, the
engine-generation gate and the player-activity hold with its rate floor.

Re-verified in the sandbox: the audit re-read 4,661 columns over 16 full passes with no read-path
failure, and the refresh arm recorded real phase timings for the first time. It reported 33
mismatches, which is the false-positive question §10 asks about — inflated here because the sandbox
lacks mods the world's block entities need, so the engine discards them and rewrites those chunks.

---

## Delivered

**Playable builds, each verified and copy-installed:**

- `0.4.112` (`6f4aa42`) — the held sweep keeps a 2/s floor instead of stopping.
- `0.4.113` (`61ca662`) — the sweep reads the savegame instead of probing; skips captured columns.
- `0.4.114` (`b513808`) — sweep telemetry for the read path; corrected a wrong count in a log line.
- `0.4.115` (`2d9b25f`) — the snow-candidate rule enforced in one place. No behaviour change.
- `0.4.116` (`d0a5dca`) — the background audit, and the dormant snow-refresh fix with its gates.
  Installed artifact SHA-256 `159902C4B0A047823E0C9DBA3DFF153DE3014A32A85E9262851A8500B2AC1F60`.

**Source, not separately released:** `611ce7a` carries A1 and A2 with no consumer.

**New source:** `LodSavegameReader`, `LodSavegameScan`, `LodSavegameIndex`, plus `LodCaptureLedger`
and `LodSweepDecision` extracted from the sweep. `LodStore` gained the `ColumnFingerprint` table and
its three accessors; `LodServerConfig` gained `AuditColumnsPerSecond`.

**Checks:** 25,319 assertions, 0 failures, from 25,190 at session start. New suites for the savegame
read path and change detection, plus ledger, sweep-decision and audit rules.

**Documentation:** plan §12 and its corrections to §§4.1.4, 4.1.5, 4.1.6, 4.3 and 7; G231–G237.

## Decisions

- **The floor is one eighth of the configured rate**, not a fixed constant and not a new setting, so
  it scales with whatever the owner sets. Rejected: a separate configurable floor, which is a knob
  nobody can set meaningfully without the measurement that produced 2/s.
- **The engine-generation gate stays absolute** while the player gate became a floor. They mean
  different things: one says real generation is in flight, the other says a player is streaming
  saved terrain that the engine counter cannot see.
- **A fingerprint is committed only on durable capture**, never on read. The alternative is a
  permanent silent hole that the audit cannot see either.
- **The audit only disputes claims the cache makes.** Rejected: auditing every column the savegame
  holds, which would re-report the entire uncaptured world forever.
- **The audit waits for the startup sweep to finish.** During first adoption almost nothing is
  captured, so there is little to audit; every later start skips most columns and reaches that point
  in minutes.
- **A4's fast path was not built.** Recorded with its two reasons in §7 above; the owner directed A5
  instead. The plan's §4.1.5 text now carries the corrected proof but its ordering premise is dead.
- **Re-measured rather than inherited.** The 0.4.112 comparison uses a same-session control rather
  than Session 102's numbers; the cross-session comparison had suggested a 5% cost that a same-session
  control showed to be 1%, inside run variation.

## Traps

- **`GameWorldSave` fires before the write, and no hook exists after it.** Promoted as G231; the
  `Exit` half is G236.
- **A fingerprint records that you looked, not that you captured.** Promoted as G232.
- **A short session never leaves the sweep's probe pass**, so a play verdict can be about code that
  never executed. Promoted as G233.
- **A quiet window renewed by ordinary movement is a stop, not a throttle.** Promoted as G234.
- **An absolute gate hides the gate you are trying to measure.** Promoted as G235.
- **A flag set and never cleared can silence a whole periodic arm, and a status line that reads
  configured state will keep reporting it as healthy.** Promoted as G237.

## Flagged and unverified

**Judgement calls awaiting human review:**

- A4's fast path is recommended against, not cancelled. §11's findings 4–8 remain the owner's to
  fold in or discard.
- `AuditColumnsPerSecond` defaults to 16, which is on by default. The off state costs nothing, but
  the default is a product choice.
- The snow-refresh arm starts working on the next launch after seven weeks dormant. It is gated, but
  it has never run in anger.

**Claims lacking the required evidence level:**

- 0.4.113 through 0.4.116 have **no in-game play verdict**. All runtime evidence is sandbox.
- The audit is proven to run; it is **not proven to catch a real corruption**, which was not stageable
  in a live world. Its decision rules are covered offline.
- The 33 sandbox mismatches are **not a false-positive rate** for the owner's setup, which has the
  mods the sandbox lacks.
- The index cost on the owner's world (73.3 s) was measured offline through the checks harness, not
  observed during a real world start.
- Multi-player fairness remains unmeasured for the sweep, the floor and the audit alike.
- Snow refresh is observed to *execute*; convergence is not demonstrated.
