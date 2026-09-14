# Session 91 — Generate World lands; engine threads measured and rejected

**Date:** `2026-09-13–14`
**Branch/commit:** `codex/water-depth-light`; source checkpoints `6a510ae`, `1cd1040`, `30858f4`, `439bc68`, `088923a`, `9ea75c5`; documentation/tooling changes uncommitted at close
**Mod version:** `0.4.71–0.4.76`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

Session 90 left the sweep false-positive diagnostic (P4) and the owner-approved Generate World
work (P5–P7) next. The owner wanted real terrain generated in a circular area, beginning nearest
the player and moving outward in a spiral, to complement vanilla map discovery. During this
session the command and settings page were built, then repaired from the owner's start,
pause/resume, stop and reopen tests. The owner ultimately reported “Success.”

The remaining P0 question was whether Vintage Story's built-in multi-thread generation could make
this substantially faster without changing terrain. Controlled copied-world runs measured one,
two and six total engine worldgen threads with the full active mod stack and with only Vintage
Horizons plus vanilla. The relevant engine setting and the actual named threads were checked so
the result could not be confused with a test that silently stayed single-threaded.

## Work narrative

### 1. P4: concurrent generation no longer causes a false sweep accusation

The savegame sweep now observes the engine generation queue before it issues any of its own loads.
If a position sampled absent becomes present while other generation was already active, the result
is `Inconclusive`, not a warning that the sweep generated it. Verified and unsafe results remain
distinct. This shipped in 0.4.71 (`6a510ae`).

### 2. A resumable, bounded real-world generation job

Version 0.4.72 (`1cd1040`) added `/vhworldgen`: a deliberate, permission-gated exception to the
mod's normal “generate nothing” rule. It drives the engine's real `LoadChunkColumn(...,
keepLoaded: true)` path, so completed terrain belongs to the savegame. Work is divided into 16×16
chunk tiles and bounded by engine-queue, capture, durability, server-load and disk-space gates.
Each kept column is released through the engine's force-loaded-list seam only after the job's own
capture/persistence obligation is safe. A durable plan bitmap supports pause, resume, stop and an
offered manual resume after restart; jobs never resume silently.

The first implementation exposed several engine/API traps. A level-0 LOD section is 64 blocks, or
2×2 vanilla chunks; treating its coordinates as chunk coordinates prevented durable progress from
being credited. A server-cache row does not prove real saved terrain, because `/vhgen` peeks can
populate the same cache. And the public `UnloadChunkColumn` path disposes immediately without the
save behavior needed here, so releasing the exact kept lease matters.

### 3. Circular outward traversal

Version 0.4.73 (`30858f4`) made the default area a true circle. The tile containing the player is
first; 16×16 tiles are visited in circular radial bands, clockwise within a band, and the columns
inside a tile are nearest-first. This preserves large persistence-friendly tiles while revealing
terrain in the same broad near-to-far shape as ordinary exploration. The durable state format moved
from 1 to 2 so a bitmap written for the earlier order cannot be resumed against the new plan.

### 4. Generate page, protocol additions and control recovery

Version 0.4.74 (`439bc68`) added a Generate tab to the shared Horizons editor and `.vhconfig`.
It exposes radius, centre, circle/square, prepare/confirm, progress, estimates, pause/resume and
stop. Only the measured Balanced preset is offered. The server advertises feature and permission
state through append-compatible protocol-1 additions: `AssistWelcome.Features` field 7,
`CanControlWorldGeneration` field 8, server settings fields 17–23, and the appended
`AssistWorldGenRequest` and `AssistWorldGenStatus` message types. Feature bits are reserved in
landing order: bit 0 Generate, bit 1 frontier, bit 2 silhouettes.

The normal in-game Settings window pauses integrated singleplayer and therefore suspends the
server that must answer a start request. Version 0.4.75 (`088923a`) makes the embedded page offer
**Open live controls**, closes Settings and opens the standalone `.vhconfig` window without
forcing the simulation to resume. It also keeps action errors visible across status-poll races and
binds release through the concrete 1.22 WorldAPI wrapper to `ServerMain`.

The owner's pause/resume then exposed a different stall: tile and final boundaries waited for
global capture/dirty counters that unrelated sweep, snow or player loads may keep nonzero forever.
Version 0.4.76 (`9ea75c5`) waits only for this job's kept leases and captured-but-not-durable L0
rows. It also prevents a finished status from hiding a newly prepared confirmation. After trying
start, pause/resume, stop, close, reopen and a fresh run, the owner reported success. A small job
reaching final durable completion and an actual restart/resume remain explicit verification debt.

### 5. The engine multi-thread flag was active, but the option failed its gate

Vintage Story does not use an ordinary `serverconfig.json` Boolean for this. The setting is
`MaxWorldgenThreads` in `servermagicnumbers.json`, read once per server process. Its default is 1;
the engine computes `min(5, MaxWorldgenThreads - 1)` additional pass threads. Thus a configured
value of 6 means five named `worldgen1`–`worldgen5` threads plus `chunkdbthread`, six pipeline
threads total. `--reducedthreads` overrides the setting.

The benchmark launcher wrote the requested value into every sandbox's
`servermagicnumbers.json`, launched `VintagestoryServer.dll` without `--reducedthreads`, and
recorded the runtime thread names. Six-thread results contain `worldgen1` through `worldgen5`; the
two-thread result contains `worldgen1`. CPU samples show work on several of those threads. The
configuration was therefore active during the tests; no missed switch can explain the result.

With the full mod stack over 3,721 requested columns, the two one-thread controls completed in
448.45 s and 444.81 s (8.30 and 8.37 columns/s), while six threads took 437.73 s (8.50/s): about
1.02×, inside ordinary run variation. With Vintage Horizons plus vanilla over 961 columns, the
two controls took 99.38 s and 99.25 s; six threads took 99.87 s and two threads took 111.46 s.
Queue depth increased rather than throughput.

More importantly, terrain was scheduling-dependent. Against two agreeing one-thread controls,
the full-stack six-thread run had 4,050 differing chunk rows, including 3,215 block payloads. The
minimal six-thread run had 1,055 such rows (892 block payloads), and the two-thread run had 408
(288 block payloads). Every requested coordinate completed and there were no queue-overflow,
stuck or exception warnings, so these are valid completed results rather than failed runs. P0
therefore failed: Vintage Horizons does not expose a multi-thread selector or the proposed Fast
and Maximum presets. The engine pipeline really ran, but it produced negligible speedup and
nondeterministic terrain.

### 6. Why manual flight still feels faster

The command must preserve a durable, restartable savegame and complete LOD capture, so it applies
backpressure at tile, capture and persistence boundaries. Flying creates a moving local demand and
does not wait for that entire durability contract before showing the next nearby terrain. The
owner chose to hold off on the remaining overlap/pipelining optimization—starting the next tile
while the preceding tile saves—and move next to the Farseer silhouette plan.

---

## Delivered

- 0.4.71 (`6a510ae`): concurrent-pregen absence results become inconclusive instead of false sweep warnings.
- 0.4.72 (`1cd1040`): bounded, resumable `/vhworldgen` real-savegame generation with disk and backlog gates.
- 0.4.73 (`30858f4`): circular near-to-far radial tile bands and nearest-first columns.
- 0.4.74 (`439bc68`): Generate page, permissions/status messages, L0 progress fix and provenance-safe skip behavior.
- 0.4.75 (`088923a`): live-control handoff from paused Settings, durable action errors and safe kept-column release binding.
- 0.4.76 (`9ea75c5`): job-owned boundary obligations and prepared-confirmation recovery; owner reported success.
- P0 benchmark tooling and evidence: the engine flag was proven active; multi-thread generation was rejected on both speed and terrain identity.

## Decisions

- Real world generation remains an explicit, confirmed, privileged action; automatic sweep,
  `/vhgen` and the future frontier stay non-persisting.
- A circle is the default, traversed outward from the player in radial clockwise tile bands;
  16×16 tiles remain the persistence unit.
- Only the Balanced one-thread-safe behavior is exposed. The proposed thread control, Fast,
  Maximum and Custom presets are rejected because P0 failed.
- A future frontier producer must yield to explicit `/vhworldgen` or `/vhgen`; it must never hold
  the exclusive authority while idle or merely following travel.
- Further worldgen speed work is deferred at the owner's request. The next feature plan is
  Farseer silhouettes.

## Traps

- `MaxWorldgenThreads` lives in `servermagicnumbers.json`; a value of 6 creates five additional
  named pass threads, not six additional threads, and `--reducedthreads` suppresses them.
- A cache row can come from a transient peek and cannot by itself justify skipping real terrain.
- Level-0 section coordinates cover 2×2 chunks; persistence callbacks must credit all planned
  chunks represented by the captured L0 row.
- Integrated Settings pauses the server that handles Generate actions; hand control to a live
  standalone dialog rather than unpausing the simulation.
- A bounded producer must wait on its own durability obligations, not global pipeline dirtiness
  that unrelated work can keep alive.

## Flagged and unverified

- **Judgement calls awaiting human review:** none for the settled multi-thread decision. The owner
  has deferred the next-tile/save overlap optimization.
- **Claims below established level:** a small job has not yet been watched all the way to final
  durable completion; automatic offered resume has not been exercised across a real server
  restart; dedicated-server read-only/control permissions and a long-run memory/disk profile remain
  untested. The fixed 15-second shutdown-save deadline has not yet become configurable. The
  0.4.69 sweep pacing/drop line and P3 after-comparison also remain open.

