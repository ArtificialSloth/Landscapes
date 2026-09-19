# Session 101 — Formal savegame-sweep scheduling analysis

**Date:** `2026-09-18`
**Branch/commit:** `codex/water-depth-light`; source checkpoint `2fbf2d1`, documentation work uncommitted
**Mod version:** `0.4.110`; no source, configuration, build or installed artifact changed
**Assist protocol / blob / schema:** `1 / 4 / 6`; both silhouette stores remain schema `1`

> Session records are Tier 3 history. This session converts Session 100's performance attribution
> into the formal analysis required before any savegame-sweep implementation work.

## Context and investigation

The owner requested a formal analysis file to serve as the basis for future savegame-sweep changes.
The preceding investigation had already established from current source and the owner's production
log that Farseer was nearly idle while the startup sweep loaded 7–14 columns/s without yielding to
the engine's pending chunk count. This session preserved that evidence, traced the installed
vanilla queue path more precisely, and documented the lifecycle, work size, current brakes, missing
gates, evidence limitations, candidate directions, invariants and acceptance criteria.

Scope was documentation only. No game process was launched. No source, tests, configuration,
savegame, cache, release archive, protocol, blob format or schema was changed.

## Work narrative

### 1. Separate submission pacing from downstream priority

The sweep already has meaningful pacing: a 50 ms callback, 16-probe callback cap, 256-probe
in-flight cap, fractional load allowance with no catch-up burst, a 1 ms cooperative loop budget and
a 32-column total capture-backlog brake. Those controls had to be recorded accurately; saying the
sweep has “no throttle” would be false.

The missing property is player priority. Read-only decompilation of the installed engine confirmed
that `WorldAPI.LoadChunkColumnPriority` aliases `ServerMain.LoadChunkColumnFast`, enters
`fastChunkQueue`, and is drained through `loadChunkAreaBlocking` before the ordinary bounded chunk
generation slice. The blocking loop can advance other requests in the shared set, so it is not a
total player freeze, but the outer scheduler does not return to ordinary service until the priority
target completes. The 1 ms Vintage Horizons budget ends after enqueueing and cannot bound that
work. Existence tests are also drained before the normal slice.

This distinction became G228 because it applies beyond this one sweep: a small producer callback is
not proof that the work it queues is background-priority or cheap downstream.

### 2. Quantify lifecycle and workload

At the default/current radius 128 and safety neighbourhood four, each enabled world start submits
70,225 existence probes over a radius-132 square, then scans 66,049 inner positions. Progress and
completion live only in the `LodSavegameSweep` object, so every restart begins again at the spiral
centre and a durable LOD row does not suppress recapture.

The owner log from the 2026-09-17 20:27 launch was retained as production evidence: the probe pass
took 5m35s at about 209.6 queries/s; the load pass then took 11m18s to reach half of its candidate
scan, with interval rates falling from roughly 14 to 7 loads/s. Capture backlog repeatedly reached
28–32, three columns unloaded before capture, and oldest dirty work reached 387.4 seconds. The
sweep files are unchanged from the logged 0.4.108 build through 0.4.110.

The older dedicated radius-24 benchmark remains positive evidence for bounded callback cadence and
safe completion, but does not answer integrated-singleplayer fairness during active exploration.

### 3. Establish the evidence boundary before selecting a fix

The queue path and sustained competing workload establish a credible contention mechanism. They do
not quantify how much player-visible terrain delay the sweep caused. The analysis therefore makes a
same-world, same-cache, same-route integrated sweep-on/sweep-off A/B the mandatory first action. A
2/s run is an optional third condition, not a substitute for the disabled control.

Possible future directions are recorded as alternatives rather than decisions: engine-queue or
shared automatic-generation gates, player-activity yield, lower/adaptive rate, a fairer load API,
durable progress, cache-aware skipping with a real freshness proof, paced verification and separate
snow-refresh gating. Every alternative must preserve zero generation, capture-before-unload,
bounded/no-catch-up work, edit/weather correctness, dedicated and integrated layouts, and useful
attribution telemetry.

### 4. Wire the analysis into the documentation route

The formal analysis is linked from TODO, STATUS, CHANGELOG and the `CLAUDE.md` task-routing table.
STATUS now distinguishes the established queue mechanism from the still-unmeasured player-latency
magnitude, and its next-work list places the controlled A/B before any sweep change. No architecture
decision or compatibility ledger entry was warranted because behavior and formats did not change.

---

## Delivered

- `dev/plans/ANALYSIS_SAVEGAME_SWEEP_SCHEDULING.md`: authoritative formal analysis of lifecycle,
  cadence, queue interaction, production and benchmark evidence, causal confidence, first
  experiment, alternatives, invariants, acceptance matrix and open questions.
- `dev/GOTCHAS.md` G228: the downstream priority-load trap hidden by a small issue budget.
- Documentation routing and current-state reconciliation in `CLAUDE.md`, `CHANGELOG.md`,
  `dev/TODO.md`, `STATUS.md`, `dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md`, this session and the
  session index.
- No implementation, build, package or runtime mutation.

## Decisions

- **A controlled A/B precedes implementation.** Current 16/s versus sweep disabled on the same
  integrated exploration route and world/cache snapshot is the minimum evidence. A 2/s condition is
  optional and cannot replace the control.
- **Current pacing is real but not fairness.** Preserve its no-burst and capture-correctness value;
  do not describe the issue as a complete absence of throttling.
- **No remedy is selected.** The formal file records alternatives and their risks so measured
  results, not API names or intuition, choose the design.
- **No settled architecture or compatibility change occurred.** `dev/ARCHITECTURE.md` and
  `dev/WIRE_HISTORY.md` remain untouched.

## Traps

- **Trigger:** judge the sweep by its 1 ms callback cost. **Failure:** the callback only enqueues a
  fast blocking engine request; the expensive load/callback/capture/save chain is downstream.
  **Safer:** measure player readiness and engine/capture queues end to end. Promoted as G228.
- **Trigger:** call the sweep unthrottled. **Failure:** that obscures the actual gap and invites
  removal of the capture brake that prevents permanent LOD holes. **Safer:** distinguish rate,
  burst, memory/capture and player-priority controls explicitly.
- **Trigger:** choose a lower rate from the production log alone. **Failure:** it cannot establish
  whether probe traffic, fast-queue priority, raw throughput, repeated restarts or another producer
  dominates the perceived slowdown. **Safer:** run the phase-matched A/B first.

## Flagged and unverified

Judgement calls awaiting human review:

- Whether the sweep A/B should be prioritized before or after the pending 0.4.110 fog visual gate.
- Whether a 2/s third condition is worth the extra run after the on/off result.
- Which future fairness policy best balances idle-server completion time against player demand.

Claims lacking required evidence:

- The size of the sweep's effect on player-visible vanilla chunk readiness is unmeasured.
- Probe-phase versus load-phase contribution is unmeasured.
- A non-fast public engine load path with suitable completion/capture semantics has not been proven.
- Durable progress, completion invalidation and cache-row freshness have designs to explore but no
  accepted model.
- Dormant snow canary/campaign contention has the same queue mechanism but no isolated runtime A/B.
