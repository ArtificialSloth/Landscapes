# Session 104 — the first play verdict on phase A, and a misread diagnostic

**Date:** `2026-09-20`
**Branch/commit:** `codex/water-depth-light` / `5043e96` (0.4.117)
**Mod version:** `0.4.117`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The owner played 0.4.116 on his `test2` world and reported that it seemed good, with consistent
minor FPS dips while exploring new terrain, and asked whether those were outside vanilla's normal
cost for generating terrain. He then directed two changes discussed at the end of Session 103: log
the sweep's own numbers unconditionally, and add a storage-pressure brake.

Only the first was built. The second is not supported by the evidence, and the investigation that
established this is the substance of the session.

## Work narrative

### 1. What phase A actually did on `test2`

Read from his own `server-main.log`, session 09:54:22 to 10:04:13.

- The read path opened on a **WAL** world this time — change counter 100 — and read all **15,112
  columns in 9.9 s**. There is not one probe progress line in the log. A3 works on his machine.
- The sweep **did not finish**. 15,112 columns at 16/s needs about 16 minutes and the session ran
  about 10, so the whole session was first-adoption work at full rate.
- It captured and saved **3,986 sections**, with the capture backlog sitting at 31–40 against its
  limit of 32, so the sweep was throttling itself on capture for much of the time.
- **The audit and the snow refresh never ran.** Both wait for the sweep to finish, so the arm
  revived in 0.4.116 still has not executed on the owner's machine.
- One unloaded-before-capture drop, no capture errors, and no Vintage Horizons errors client-side.

### 2. Attributing the dips

Partly the mod, mostly vanilla, and the mod's share is already measured: the Session 103 frontier
A/B compared exactly this situation — exploring new terrain with the sweep on and off — and put the
difference at about **4% of the 1% low**. His session was the worst case for that share, because
first adoption is the most work the sweep will ever do on a world, and it is one-time: later starts
skip nearly everything.

The useful follow-up is free. Playing `test2` again for long enough to finish the sweep, then
exploring, separates the two without any instrumentation: if the dips largely go, they were the
sweep; if they are unchanged, they were vanilla generation all along.

### 3. The number that looked like an alarm and was not

His log read `52 dirty, oldest dirty 307.6s, 7.38 saves/s`. Those three cannot describe one queue:
52 items at 7.38 a second drain in seconds, not five minutes. Checking rather than acting on it:

- `LodSaveDirtySet.Add` returns false for a key already present, so a re-dirtied section **keeps its
  original first-dirty timestamp**.
- `LodWorld.CompleteSave` clears dirty state only when the completed revision is still the newest,
  so a section modified again while its save is in flight **stays dirty**.
- During a sweep, the coarse parent sections are re-dirtied continuously by mip propagation as
  columns underneath them are captured.

So the age measures how long the longest-standing section has been *continuously changing*, not how
long a write has waited. It climbs for as long as the sweep runs and says nothing about storage
being behind. The write queue itself is separately bounded: `SaveSomeDirtySections` stops feeding
the storage thread at `MaxStorageBacklog` = 256.

### 4. Why no brake was added

A brake on that age would have fired permanently during every sweep, for a benign reason — the
precise trap of gating on a signal whose floor nobody established. The dirty *count* never
approached its limit either (52 against 512), so a count-based brake would never have fired at all.
Storage pressure is already bounded, and this was a diagnostic defect rather than a scheduling one.

This reverses the plan's §11.3 direction and a recommendation made to the owner at the end of
Session 103. It was proposed on the strength of a number that had not been traced to its definition.

### 5. What 0.4.117 changes

- The sweep's status prints once a minute beside the cache-work line while work is running, with no
  environment variable needed. The owner cannot copy text out of game chat, so anything required to
  answer "what was the mod doing while that happened?" has to reach the log by default. The p95/p99
  phase dump stays development-only behind `VINTAGEHORIZONS_STATS`.
- The cache-work line now says the oldest section has been *changing* for that long, and prints the
  storage queue depth beside it, which is the real backlog. A check fails if it is ever relabelled.

### 6. The owner's question about retiring the logging

Recorded as a release-checklist item rather than acted on, because the wording will keep changing
while the features do. The split:

- **Development-only** — the per-15-second timing and allocation dumps. Already behind
  `VINTAGEHORIZONS_STATS`, off unless set, so nothing needs retiring.
- **Keep** — the handful of plain-language lines a dedicated-server admin with no game window has
  nothing else to go on for: started, what it is doing, finished, anything unusual. At most one line
  a minute, only while work is happening, and they stop by themselves when it ends.

---

## Delivered

- `0.4.117` (`5043e96`) — unconditional sweep status logging; the cache-work line relabelled with
  the storage queue depth added. Verified, copy-installed and checkpointed.
- `LodPipeline.StorageBacklog` exposes the real save-queue depth; `OldestDirtyAgeMs` carries the
  documentation that stops it being read as a backlog again.
- 25,320 assertions, 0 failures.

## Decisions

- **No storage-pressure brake.** The save queue is already bounded at 256, the dirty count never
  approached its limit, and the age signal does not mean what it appeared to. Reverses plan §11.3
  and a recommendation made in Session 103.
- **The sweep's status logs unconditionally; the phase dump does not.** The division is between what
  a server owner needs to understand what happened and what a developer needs to profile it.
- **Logging retirement is a release-checklist item**, not work to do now.

## Traps

- **A continuously-modified item's age is not a queue latency, and printing the two side by side
  reads as an alarm.** Promoted as G238.
- A number quoted in a status line had never been traced to its definition before being proposed as
  a scheduling input. Trace the signal before gating on it; check whether the figures in one line
  can describe the same thing.

## Flagged and unverified

**Judgement calls awaiting human review:**

- The dips were attributed to vanilla generation plus a measured ~4% sweep share, on the strength of
  the Session 103 frontier A/B rather than a measurement on `test2` itself.

**Claims lacking the required evidence level:**

- **The audit and the revived snow refresh have still never run on the owner's machine.** Both wait
  for the sweep to finish and his session ended before it did.
- 0.4.117 is **not play-tested**; its only runtime evidence is that it builds and its checks pass.
- The one unloaded-before-capture drop in his session is unexplained. It is a single column and the
  capture brake exists to prevent exactly this, so it is noted rather than investigated.
- `test2`'s sweep has not completed, so no start on that world has yet exercised the skip path.
