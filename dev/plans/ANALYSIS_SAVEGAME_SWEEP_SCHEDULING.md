# Analysis — savegame sweep scheduling and terrain-generation contention

**Date:** `2026-09-18`
**Source checkpoint:** `2fbf2d1` (`0.4.110`)
**Status:** original analysis complete; the controlled measurement and 0.4.111 follow-up are
recorded in §18
**Primary question:** can the default savegame sweep slow vanilla terrain delivery while a player
is exploring, how often does it run, and what does it currently do to yield?

> This is the required starting point for future savegame-sweep scheduling work. It records the
> current mechanism, the evidence behind the concern, the invariants a change must preserve, and
> the experiment that must precede implementation. It is not an approved implementation plan.
>
> **2026-09-19 follow-up:** preserve §§1–17 as the pre-measurement analysis. The required A/B was
> subsequently run, a player-aware load gate was implemented in 0.4.111, and the evidence boundary
> was refined after a direct save diff. See §18 and [Session 102](../sessions/SESSION_102.md).

## 1. Executive conclusion

The startup savegame sweep is a credible source of slower terrain delivery during exploration.
The queue interaction is source-proven and the sustained competing workload is production-observed;
the size of the player-visible penalty is not yet measured by a controlled sweep-on/sweep-off run.

The important findings are:

- The sweep starts once every time an enabled server or integrated-singleplayer world reaches
  `RunGame`. Its progress and completion are not persisted, so every restart begins again at the
  centre of the same spawn-centred square.
- At the default and currently observed configuration, it first submits **70,225 save-database
  existence probes**, then scans **66,049 candidate positions** and priority-loads every existing
  column whose complete 9×9 neighbourhood was present at probe time.
- Its 50 ms callback is deliberately smoothed. Probe submission is capped at 16 per callback and
  256 in flight; load submission has a long-run 16-column/s allowance, no delayed-tick catch-up
  burst, a 1 ms cooperative issue budget, and a 32-column LOD-capture-backlog brake.
- Those controls protect Vintage Horizons from one-second submission bursts and from outrunning its
  own capture worker. They do **not** yield to vanilla player demand, the engine's pending chunk
  count, player movement or location, server overload, dirty/save backlog, or the shared generation
  authority.
- `LoadChunkColumnPriority` is not merely a friendly asynchronous hint in the installed engine. It
  aliases the engine's fast load, enters `fastChunkQueue`, and is drained through a blocking load
  path before the ordinary bounded chunk-generation slice. The blocking loop can advance player
  requests in the shared request set, but it does not return to the normal outer scheduling slice
  until the priority target finishes. All queued existence tests are also drained before that
  normal slice.
- The sweep's 1 ms budget measures only the act of deciding and submitting work on the server
  owner thread. It does not bound database reads, chunk deserialization/activation, shared chunk
  thread work, main-thread load callbacks, LOD capture, mip propagation, or storage writes caused
  by the submitted request.
- One real 0.4.108 session took 5m35s to finish the probe pass and another 11m18s to reach 50% of
  the candidate scan. During the observed load window it sustained roughly 7–14 loaded columns/s,
  held the LOD capture backlog at 28–32 of 32, and coincided with an oldest dirty-cache age reaching
  387.4 seconds. The sweep source did not change between 0.4.108 and this 0.4.110 checkpoint.
- Farseer was effectively idle in the same production window: two probes and two previews in eight
  minutes while yielding on about 73% of its ticks. It is not the producer responsible for that
  sustained load.

The first future action must therefore be a controlled integrated-singleplayer A/B on the same
exploration route and world snapshot: current sweep versus sweep disabled, with a low-rate condition
such as 2/s useful only as a third run. A scheduling change should not be chosen until that run
measures the effect on player chunk readiness and establishes which sweep phase matters.

## 2. Scope and evidence boundary

This analysis covers:

- the one-time startup probe and load passes in `LodSavegameSweep`;
- the final absence verifier;
- the persistent dormant-terrain snow watcher and its finite refresh campaigns;
- the downstream LOD capture/storage work caused by sweep loads;
- the installed vanilla engine queues used by the relevant public APIs;
- existing benchmark evidence and the owner's production log.

It does not:

- change source, configuration, save data, cache data, protocol, blob format, or schema;
- decide a future gate, rate, API, or persistence model;
- claim that every terrain-loading complaint is caused by the sweep;
- quantify player-visible latency without the missing controlled A/B;
- assume that worker thread priority changes the vanilla chunk thread's priority;
- weaken the promise that the sweep generates no missing terrain.

Evidence levels used below:

| Level | Meaning in this analysis |
|---|---|
| Source-proven | Directly established from current Vintage Horizons source. |
| Engine-proven | Directly established from a read-only decompilation of the installed Vintage Story assemblies. |
| Production-observed | Measured from the owner's archived `server-main.log`. |
| Benchmark-observed | Measured in an isolated scripted run whose limitations are stated. |
| Hypothesis | Mechanistically credible but not yet isolated by a controlled comparison. |

## 3. What “the sweep” means

There are two related workloads with different lifetimes.

### 3.1 Startup sweep

The startup sweep indexes terrain that already exists in the savegame so Vintage Horizons can build
its LOD cache without requiring a player to revisit every old area. It runs in two passes:

1. Probe a square around world spawn to learn which map chunks exist in the save database.
2. Scan the configured inner square and load only existing columns whose complete safety
   neighbourhood also existed during the probe.

The two-pass safety rule is essential. Earlier measurement showed that loading frontier columns
without the wider neighbourhood check generated 1,460 new columns. A four-chunk neighbourhood was
the first measured width that generated zero in that vanilla test. The cost is leaving a border of
real frontier terrain uncaptured.

### 3.2 Post-sweep snow maintenance

If automatic snow refresh is enabled, the same object and 50 ms listener remain alive after the
startup sweep completes. Every 30 seconds it may priority-load one safe canary column to wake one
dormant weather region. A detected weather change creates a finite regional recapture campaign,
normally paced at one column per second. This is not a continuous one-column-per-second scan: when
there is no campaign, there are no campaign loads.

This distinction matters when measuring. Startup contention and long-lived snow maintenance need
separate evidence and may need separate gates even though both currently call the priority-load API.

## 4. Frequency, restart behavior, and lifetime

| Event | Current behavior | Evidence |
|---|---|---|
| World reaches `RunGame` with `SweepSavegame = true` and radius greater than zero | Constructs a new `LodSavegameSweep` and immediately starts it. | Source-proven: `LodServerCaptureSystem.OnRunGame`. |
| Same server process remains up | Startup probe/load pass runs once. After completion the listener stops only when automatic snow refresh is disabled. | Source-proven: `LodSavegameSweep.Finish` and `Stop`. |
| Server or integrated world restarts | A fresh object starts from `probeIndex = 0`, `loadIndex = 0`, and `Done = false`. | Source-proven; those fields exist only in memory. |
| Previously completed sweep restarts | The full probe and candidate scan repeat. No durable completion marker or candidate map is consulted. | Source-proven. |
| Interrupted sweep restarts | The partial prefix is not resumed; it repeats from the spiral centre. | Source-proven. |
| New terrain is explored during the run | Load events can add positions to the in-memory existence/candidate maps, but no durable progress is written for a later session. | Source-proven: `ObserveLoadedColumn`. |
| Archived launches checked on 2026-09-18 | Five September 17 starts announced a new sweep; none of those archived launches contained its finish line. | Production-observed. |

Repeated restarts therefore pay the complete 70,225-position probe pass again and repeatedly revisit
the early, spawn-centred portion of the load spiral. A warm LOD cache does not suppress the load:
the sweep's eligibility question is whether terrain exists safely in the savegame, not whether a
corresponding LOD row is already durable.

## 5. Exact default and observed workload

The default configuration and the owner's inspected active configuration were the same for the
relevant settings:

| Setting | Value | Effect |
|---|---:|---|
| `SweepSavegame` | `true` | Run at each world start. |
| `SweepRadiusChunks` | 128 | Inner load square extends 4,096 blocks from spawn. |
| `SweepColumnsPerSecond` | 16 | Long-run load-submission allowance. |
| `RefreshColumnsPerSecond` | 1 | Finite post-sweep snow campaign rate. |
| `PregenRadiusChunks` | 0 | Transient startup pre-generation is off; it is not this workload. |

With Vintage Story's 32-block chunks and `SafeNeighbourhood = 4`:

- probe radius: `128 + 4 = 132` chunks;
- probe square: `265 × 265 = 70,225` positions;
- candidate/load square: `257 × 257 = 66,049` positions;
- safety test for an existing candidate: every position in a 9×9 neighbourhood must exist;
- maximum final absence sample: 256 positions.

The 66,049 candidate positions are not all loaded. Absent positions are skipped; existing frontier
positions are recorded for possible later snow maintenance but not loaded; only safe interior
positions enter the priority-load path. The percentage in the load-pass progress line is percentage
of candidate positions scanned, not percentage of the eventual loaded-column count.

## 6. Current pacing and what each control actually protects

### 6.1 Controls that exist

| Control | Current bound | What it protects | What it does not protect |
|---|---:|---|---|
| Sweep listener cadence | 50 ms | Avoids a once-per-second owner-thread batch. | Does not assign low priority to engine work. |
| Probe issue cap | 16 per callback | Limits one callback's enqueue loop. | No long-run per-second probe rate; theoretical callback ceiling is 320/s. |
| Probe in-flight cap | 256 | Prevents all 70,225 tests from being queued at once. | The engine drains every queued existence test before its normal bounded generation slice. |
| Cooperative issue budget | 1 ms | Stops the mod's probe/load/refresh loop between resumable items. | Does not pre-empt one item or bound any downstream vanilla/capture/storage work. |
| Load allowance | 16/s by current config | Smooths submissions over time. | Is not a player-priority or engine-pressure gate. |
| Per-callback load cap | 1 at 16/s | Prevents a delayed callback from dumping many requests. | A single priority request can still occupy the engine's blocking load path for much longer than 1 ms. |
| Delayed-tick handling | No catch-up burst | A late server tick earns at most one callback's rounded share. | Does not cancel already queued engine work. |
| Capture-backlog brake | Stop issuing at total backlog 32 | Prevents swept columns from unloading before Vintage Horizons captures them. | Protects mod correctness, not player chunk latency or cache-save age. |
| Capture/storage worker priority | `BelowNormal` | Gives ordinary OS scheduling preference over those two mod workers. | Does not lower the vanilla chunk thread, main-thread callbacks, or database I/O initiated by priority loads. |
| Two-pass 9×9 safety rule | Load only proven-safe existing interiors | Preserves the generates-nothing promise in the measured worldgen setup. | Does not make loading, decoding, capture, or saving free. |

The capture-backlog brake was added after a real session reached backlog 792 and dropped 4,043
columns that unloaded before capture. It is necessary for correctness. A future player-pressure gate
must compose with it, not replace it.

### 6.2 Pressure signals the startup sweep does not use

The startup load pass does not pause or reduce its rate because of:

- `CurrentGeneratingChunkCount` or any other vanilla pending-player-work signal;
- player movement, distance from the sweep request, or whether the player is entering unexplored
  terrain;
- the server overload detector used by explicit world-generation work;
- `AutomaticGenerationGate`, including its save-backlog-age and dirty-section checks;
- the named generation authority held by `/vhworldgen`, `/vhgen`, or startup pre-generation;
- the LOD storage queue's oldest dirty age;
- whether the corresponding LOD data already exists durably.

`ObserveOtherGenerationQueue` sounds like a throttle but is not one. It records the engine queue
only before the first sweep load so the final safety verifier can avoid falsely accusing the sweep
when some other producer may have generated an absent sample. It never blocks or slows submission.

The snow canary and finite snow campaigns likewise do not consult player demand or the shared
automatic-generation gate. Campaign refresh also lacks the startup pass's 32-column capture-backlog
check, although its default rate is much lower at 1/s.

## 7. Why the 1 ms budget does not bound the expensive work

The mod callback performs cheap scheduling work:

1. choose the next spiral position;
2. classify it using the in-memory existence map;
3. call `TestMapChunkExists` or `LoadChunkColumnPriority`;
4. return after the API has accepted the request.

The costly chain occurs afterward:

1. the vanilla chunk thread reads the database and deserializes/activates the column;
2. vanilla completes the priority request through its chunk-request machinery;
3. `ChunkColumnLoaded` reaches the server owner thread;
4. Vintage Horizons snapshots/queues the column for LOD capture;
5. a below-normal capture worker builds LOD data;
6. owner-thread publication marks sections dirty and creates parent obligations;
7. a below-normal storage thread compresses and writes durable rows.

Only the initial submission loop is inside `LodWorkBudget(1.0)`. Checking the budget between items
cannot interrupt a database query, a blocking engine load, a capture, or a save already caused by
one submitted item. This is why telemetry showing a small `sweep load issue` duration cannot, by
itself, establish that the sweep is cheap for vanilla terrain delivery.

## 8. Installed vanilla queue path

The relevant installed-engine path was verified by read-only decompilation, not inferred from the
API method names.

### 8.1 Priority load path

1. `WorldAPI.LoadChunkColumnPriority` calls `ServerMain.LoadChunkColumnFast`.
2. `LoadChunkColumnFast` enqueues the request in `fastChunkQueue` when the column is not already
   fully loaded.
3. `ServerSystemSupplyChunks.OnSeparateThreadTick` first performs deletion and moves ordinary
   requests into the shared generating queue.
4. It then drains **all** current `fastChunkQueue` entries. Each is handled by
   `loadChunkAreaBlocking`.
5. `loadChunkAreaBlocking` adds the priority target as a blocking request and repeatedly walks the
   shared requested-column set until that blocking target finishes.
6. Only after the fast queue is empty does the outer tick service simple loads, at most one peek,
   every queued existence test, and finally the normal bounded
   `tryLoadOrGenerateChunkColumnsInQueue` slice.

The blocking loop walks all current requests, so ordinary/player requests in the shared set can make
progress. The precise claim is therefore not “the sweep freezes every player request.” The proven
problem is that a background indexing request enters the engine's highest-priority blocking lane,
and the chunk thread does not return to its ordinary bounded scheduling slice until that request is
complete. Repeating that at up to 16 columns/s can keep the engine in the fast path during active
exploration.

### 8.2 Existence-probe path

`TestMapChunkExists` enters `testChunkExistsQueue`. On each separate-thread tick the engine drains
that whole queue before its normal bounded generation slice. The sweep limits itself to 256 tests in
flight and 16 new submissions per 50 ms callback, but the default pass still performs 70,225
database existence checks. These do not load or generate terrain, yet they consume chunk-thread and
database time ahead of the ordinary slice.

### 8.3 Integrated singleplayer amplification

On a dedicated server the contention is primarily server CPU, chunk-thread scheduling, database I/O,
capture, and storage. In integrated singleplayer those server workloads share the same process,
machine, storage device, and CPU budget as the client renderer and simulation. Even work correctly
moved off the client game thread can therefore reduce the resources available while the player is
moving into new terrain.

## 9. Production observation: 2026-09-17 20:27 launch

Source: the owner's archived
`VintagestoryData/Logs/Archive/2026-09-17_20_27_20/server-main.log`, running Vintage Horizons
0.4.108. The sweep files are unchanged from checkpoint `6708b19` (0.4.108) through `2fbf2d1`
(0.4.110), so the scheduling result applies to current source.

### 9.1 Timeline

| Time | Sweep event | Cumulative loaded | Frontier skipped | Interval effective load rate |
|---|---|---:|---:|---:|
| 20:27:35 | Sweep starts probing. | — | — | — |
| 20:33:10 | Probe completes: 70,225 examined, 12,468 existed. | — | — | ~209.6 probes/s average |
| 20:37:22 | Candidate scan reaches 10%. | 3,330 | 699 | ~13.2 loads/s from load start |
| 20:39:21 | 20%. | 5,019 | 934 | ~14.2 loads/s since prior report |
| 20:41:11 | 30%. | 6,451 | 1,146 | ~13.0 loads/s |
| 20:42:36 | 40%. | 7,359 | 1,426 | ~10.7 loads/s |
| 20:44:28 | 50%. | 8,176 | 1,532 | ~7.3 loads/s |

The probe pass alone took 5m35s. The load pass took another 11m18s to scan half of its candidate
square, averaging about 12.1 actual loads/s over that interval. Falling below the configured 16/s is
consistent with time spent held by the capture-backlog brake and with the distribution of
absent/frontier candidates; the configured value is a ceiling, not a completion guarantee.

### 9.2 Concurrent pressure

During the measured load window:

- total LOD capture backlog repeatedly sat at 28–32 against the sweep ceiling of 32;
- three columns were recorded as unloaded before capture;
- the oldest dirty LOD work reached 387.4 seconds;
- the sweep continued at approximately 7–14 loaded columns/s;
- Farseer issued two lattice probes and two previews in eight minutes and accumulated roughly 5,100
  yield ticks over about 470 seconds;
- Farseer's captured-height count rose with the sweep's loaded columns because that intake is a free
  by-product of server column loads, not proof that Farseer requested them.

These observations prove sustained concurrent sweep work and clear Farseer as the high-rate producer
in that window. They do not by themselves measure how many seconds of player chunk readiness the
sweep cost.

## 10. Earlier dedicated benchmark and why it does not settle this report

The completed-sweep benchmark in
`bench/results/2026-08-18-join-sweep/README.md` is still useful evidence:

- dedicated server/client layout;
- radius 24 and rate 32/s;
- 3,249 existence probes;
- 1,018 existing columns loaded and 377 frontier columns skipped;
- about 68 seconds to completion;
- server pipeline tick maximum 17.874 ms, with no Vintage Horizons server tick at or above 25 ms;
- measured sweep probe/load issue maxima 3.945/6.004 ms;
- zero sampled absent positions generated.

It does not clear the current concern because it was a much smaller, warm-cache, older-code,
dedicated-process run with no same-route on/off comparison, no integrated-singleplayer resource
sharing, and no player actively riding into unexplored terrain. It predates the 0.4.69 capture
backlog brake. It proves bounded mod callback cadence and safe completion in that scenario; it does
not prove fair scheduling against live player demand at the default radius.

## 11. Causal assessment

| Claim | Assessment | Why |
|---|---|---|
| The sweep repeats every enabled world start. | Established | Current lifecycle and five archived starts agree. |
| It has pacing controls. | Established | 50 ms cadence, fractional allowance, issue caps, 1 ms cooperative budget, and capture brake are explicit. |
| It yields to vanilla player chunk pressure. | False in current source | No player/engine-pressure signal participates in `StepProbe`, `StepLoad`, canary loads, or campaign refresh. |
| Its priority loads run ahead of the normal engine slice. | Established | Installed-engine API alias and queue order were decompiled. |
| Its existence probes also precede the normal slice. | Established | Installed-engine queue order drains the test queue first. |
| It creates a sustained competing workload in the reported session. | Established | Production log shows 5m35s of probing and at least 11m18s of load scanning at 7–14 loads/s. |
| It caused some nonzero contention with exploration. | Strong inference | The same chunk thread, database, CPU and downstream capture/storage resources were in use without a player-pressure yield. |
| It is the full explanation for the player's perceived slowdown. | Not established | No controlled route compared sweep on and off, and vanilla/world/mod load was not otherwise held identical. |
| Lowering the configured rate alone is the correct fix. | Not established | It reduces request frequency but does not change queue priority, probes, restart repetition, or post-sweep refresh behavior. |

The careful conclusion is: the reported slowdown has a concrete mechanism and matching sustained
workload, not merely correlation. The magnitude and best remedy remain experimental questions.

## 12. Required first experiment

No sweep scheduling implementation should begin before this comparison.

### 12.1 Conditions

Use cloned copies of the same world and equivalent LOD-cache state so every run begins from the same
savegame, spawn, weather/time, mod list, configuration, and cache warmth.

1. **A — current behavior:** sweep enabled, radius 128, rate 16/s.
2. **B — control:** `SweepSavegame = false`.
3. **C — optional rate probe:** sweep enabled at 2/s. This distinguishes “too much throughput” from
   “wrong priority,” but it must not replace the on/off control.

Run the same deterministic exploration route in integrated singleplayer. Ideally measure two
separate windows: one during the existence-probe pass and one during the priority-load pass. Do not
compare a probe-phase route in one run with a load-phase route in another.

### 12.2 Measurements

At minimum capture:

- elapsed time from crossing each route marker to the required vanilla columns becoming ready;
- visible missing-terrain duration or a reproducible readiness proxy;
- average, 1% low, p95/p99/max frame time and hitch counts;
- `CurrentGeneratingChunkCount` over time;
- sweep phase, probes/loads issued, actual loaded-column rate, and phase-cost telemetry;
- total capture backlog, unloaded-before-capture drops, dirty-section count and oldest dirty age;
- server pipeline phase maxima;
- process CPU and storage activity if the harness already exposes them;
- start/end cache row counts and the absence-verifier result.

An observer statement such as “terrain kept up” is valuable but should accompany, not replace, the
route timing and queue evidence.

### 12.3 Interpretation

- If B materially beats A during the load phase but not the probe phase, prioritize load scheduling.
- If B beats A during probing, probe queue pacing also needs a player-pressure gate or a different
  discovery strategy.
- If C approaches B, rate adaptation may be sufficient; if C remains close to A while requests are
  present, queue priority and blocking semantics are the stronger suspect.
- If A and B are equivalent, preserve the current implementation and investigate another producer;
  do not “fix” a source-level asymmetry that has no measured user cost.

## 13. Candidate change directions — alternatives, not decisions

| Direction | Potential benefit | Main risk or unknown |
|---|---|---|
| Gate startup loads on `CurrentGeneratingChunkCount` | Directly yields when real chunk requests are pending; matches Farseer's intent. | Threshold and hysteresis need measurement; the public counter may include non-player producers. |
| Reuse or extend `AutomaticGenerationGate` | Adds named server-overload, capture, dirty-age, save-backlog and engine-queue reasons with consistent telemetry. | The sweep loads saved terrain rather than generating previews; some gates or thresholds may be unnecessarily strict. |
| Yield while players are moving into unready terrain | Targets the exact user harm and allows idle-server progress. | Requires a truthful readiness/activity signal and multiplayer fairness; distance alone is not sufficient. |
| Lower or adapt `SweepColumnsPerSecond` | Simple reduction in sustained pressure. | Does not change fast-queue priority, probe contention, restart repetition, or snow loads; can make an already long sweep last for hours. |
| Replace `LoadChunkColumnPriority` with a fairer engine path | Removes a background indexer from the fast blocking lane. | Must preserve the load-completion/capture path, avoid unload-before-capture losses, and be verified against the installed engine rather than inferred from API names. |
| Persist progress or a completion/candidate map | Avoids repeating the same full work after every restart. | Must detect newly explored/changed terrain, retain snow candidates, handle configuration/spawn changes, and define invalidation without trusting stale cache rows as savegame truth. |
| Skip columns whose current terrain revision is already durable | Could avoid redundant recapture while still scanning for new terrain. | The cache does not currently prove that its row matches the present savegame; edits and seasonal state make a naïve existence check unsafe. |
| Pace existence probes and final verification on player pressure | Protects the normal chunk-thread slice during the 70,225-query phase and removes the 256-query final batch. | Extends startup discovery; needs evidence that probes contribute materially. |
| Gate snow canaries and refresh campaigns separately | Prevents long-lived background priority loads from colliding with exploration after startup. | Must preserve eventual winter convergence and not strand signalled campaigns. |

A likely robust design may combine a fair engine-pressure gate, the existing capture-correctness
brake, and restart-aware progress. That is a hypothesis, not a selection. The A/B determines which
parts earn their complexity.

## 14. Invariants for any future implementation

Any accepted change must preserve all of these:

1. **Generate nothing.** Startup sweep and snow refresh may load only terrain proven safe by the
   complete-neighbourhood rule. Missing/frontier terrain remains untouched.
2. **Capture before unload.** A loaded column must not be allowed to disappear before its LOD
   snapshot is accepted; the existing 32-column correctness brake cannot simply be removed.
3. **Player work wins.** Background indexing must make bounded progress without repeatedly occupying
   a higher-priority lane than terrain required by connected players.
4. **No catch-up burst.** A pause, lag spike, or long yield must not release accumulated work as one
   synchronous batch.
5. **Bound every queue and retained object.** Probe callbacks, chunk loads, capture snapshots, dirty
   sections, storage work, and any new durable journal need explicit limits.
6. **Keep partial progress useful.** The spiral's centre-out property is valuable; interruption must
   not corrupt state or require an all-or-nothing transaction.
7. **Preserve edits and weather.** A cache row is not proof that saved terrain is unchanged. Snow
   maintenance must still converge eventually.
8. **Support dedicated and integrated layouts.** A fix may not assume a client is present or that
   server and renderer use separate processes.
9. **Keep attribution observable.** Telemetry must distinguish probe yield, load yield, capture
   pressure, save pressure, engine pressure, and player/activity pressure.
10. **Do not spend compatibility casually.** Protocol, blob format and database schema should remain
    unchanged unless a durable design genuinely requires a versioned addition.

## 15. Acceptance matrix for a future change

| Area | Minimum acceptance evidence |
|---|---|
| Player fairness | Same-route integrated A/B shows materially improved vanilla chunk readiness or establishes no regression. |
| Safety | Zero sampled absent positions become present, including with supported worldgen mods. |
| Capture correctness | No meaningful unloaded-before-capture increase; backlog remains bounded and converges. |
| Main/owner thread | No one-second burst; phase p95/p99/max stay within the established performance-plan gates. |
| Engine queue | Background work visibly yields under player pressure and resumes with hysteresis when pressure clears. |
| Persistence, if added | Clean completion, interruption, restart/resume, configuration change and newly explored terrain are all exercised. |
| Snow | Dormant canary and finite accumulating/melting campaigns still converge after yields and restart. |
| Dedicated server | No-player idle progress remains reasonable; multiplayer demand cannot starve or be starved by the sweep. |
| Diagnostics | Status/logs explain current phase, progress, gate reason, effective rate and backlog without per-tick spam. |

## 16. Open questions

- How much of the observed player delay belongs to the probe phase versus the priority-load phase?
- What vanilla counter or readiness signal best represents player-required terrain in both integrated
  and dedicated servers?
- Does any non-fast public load path provide a reliable completion callback without keeping the
  column resident, or would a small engine-adapter layer be needed?
- Can a sweep completion/progress record be invalidated cheaply when the savegame gains newly
  explored terrain?
- Can the mod prove a durable LOD row is current without introducing a terrain revision journal?
- Should startup sweep pause whenever any player is connected and moving, or only when their chunk
  demand crosses a measured threshold?
- Should existence probes share the same gate as loads, or only a smaller independent queue cap?
- Should snow refresh use the same fairness policy as startup loading but retain a separate low-rate
  allowance?
- How should multiple automatic producers divide idle capacity without each independently deciding
  that the server is idle?

## 17. Source and evidence map

Primary current source:

- `VintageHorizons/src/Net/LodSavegameSweep.cs` — lifecycle, passes, issue budgets, capture brake,
  priority loads, snow canaries and refresh campaigns.
- `VintageHorizons/src/Net/LodTickAllowance.cs` — 50 ms fractional allowance and no-catch-up rule.
- `VintageHorizons/src/Net/LodColumnMap.cs` — spiral, four-chunk safety neighbourhood and
  classification.
- `VintageHorizons/src/Net/LodAbsenceVerifier.cs` — final 256-position safety sample.
- `VintageHorizons/src/Net/LodServerCaptureSystem.cs` — `RunGame` construction, load-event capture,
  overload and shared generation gates.
- `VintageHorizons/src/Lod/LodPipeline.cs` — capture/backlog and dirty-publication path.
- `VintageHorizons/src/Lod/LodWorker.cs` and `VintageHorizons/src/Storage/LodStorageThread.cs` —
  below-normal mod worker threads.
- `VintageHorizons/src/Net/LodServerConfig.cs` — defaults, clamps and enable semantics.

Installed-engine types inspected:

- `Vintagestory.Server.WorldAPI.LoadChunkColumnPriority`;
- `Vintagestory.Server.ServerMain.LoadChunkColumnFast` and `fastChunkQueue`;
- `Vintagestory.Server.ServerSystemSupplyChunks.OnSeparateThreadTick`;
- `loadChunkAreaBlocking` and `tryLoadOrGenerateChunkColumnsInQueue`.

Measured artifacts and related records:

- `bench/results/2026-08-18-join-sweep/README.md`;
- archived production log `2026-09-17_20_27_20/server-main.log`;
- `dev/sessions/SESSION_100.md`;
- `dev/GOTCHAS.md` G190, G192, G199, G227 and G228;
- `dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md`.

## 18. 2026-09-19 measured follow-up

This section records what happened after the original analysis. It does not rewrite the original
alternatives or turn them into retroactive decisions.

### 18.1 Controlled result

The required comparison used the same frozen `BodanBoys` save, client/server LOD caches, mod list
and deterministic integrated-singleplayer route for every condition. The harness restores the seed
profile for each run through `bench/configs/sweep-ab-{on,off}.json`,
`bench/routes/sweep-active-exploration.txt` and the seeded-profile path in
`scripts/bench-windows.ps1`.

| Phase and condition | Average FPS | Median FPS | 1% low | 1% frame-time tail | Managed | RSS |
|---|---:|---:|---:|---:|---:|---:|
| Probe, sweep on | 487.5 | 525.9 | 136.0 | 7.35 ms | — | — |
| Probe, sweep off | 476.2 | 507.1 | 159.3 | 6.28 ms | — | — |
| Load, original sweep on | 315.5 | 351.3 | 88.3 | 11.32 ms | 4,848 MB | 8,820 MB |
| Load, sweep off | 316.4 | 340.4 | 110.3 | 9.07 ms | 4,510 MB | 7,578 MB |
| Load, 0.4.111 player-aware gate | 324.6 | 353.9 | 109.0 | 9.17 ms | 5,696 MB | 8,362 MB |

The probe pair's average moved against its tail result, so one pair is insufficient to change probe
scheduling. The load pair is materially clearer: average FPS was effectively equal, while the
original sweep worsened the 1% low by 19.9%, the frame-time tail by 24.8%, and RSS by 16.4%. This
establishes load-phase contention in the measured route without claiming that the sweep explains
every exploration slowdown.

### 18.2 Rejected primary signal

`CurrentGeneratingChunkCount` alone was tested first. A threshold of 8 recorded no useful holds and
measured 89.3 FPS / 11.20 ms / 8,853 MB RSS. A threshold of 1 was still effectively blind to
ordinary saved-column priority loads at the 50 ms scheduler cadence and measured 93.6 FPS /
10.68 ms / 8,922 MB RSS. The counter did become visible during actual new generation late in the
route. It is retained as a secondary guard at threshold 1, not as the primary saved-terrain signal.

### 18.3 Selected 0.4.111 scheduler

The startup load pass now observes online players without per-tick collection allocation. A new or
rejoining player, or any player's chunk-boundary crossing, opens a five-second quiet window in
which no new sweep priority load is submitted. Activity from any player extends the window. A
no-player server and a stationary player permit configured-rate progress.

The fractional allowance remains capped, so the quiet window cannot accumulate a catch-up burst.
The capture-backlog brake and complete-neighbourhood rule remain unchanged. Telemetry distinguishes
player holds, engine holds and the engine-queue peak. The measured follow-up recorded 1,621 player
holds, 209 engine holds and no capture drops/errors; useful sweep work still ran during roughly one
minute of quiet load time before movement began. Its 109.0 FPS / 9.17 ms tail result matches the
sweep-off control within run variation.

### 18.4 Route boundary corrected

The route was repeated exactly, and its centerline through endpoint chunk X 8050 already existed.
That did not make it a generated-only route. The final post-run save contained 62,276 `mapchunk`
rows versus the frozen seed's 62,098, and 750,820 `chunk` rows versus 748,684. The 178 new map
columns, with 12 vertical chunks each, occupied X 8053–8062 and Z 7990–8012 in the view-distance
halo beyond and around the endpoint.

The measured route is therefore mixed: mostly saved-terrain streaming, followed by genuine new
generation. It supports the player-activity gate and proves that the secondary engine guard engages
under real generation. It does not isolate sustained virgin-terrain travel. The remaining
frontier-specific experiment should restore the frozen seed, start near X 8050, continue through
untouched terrain, and compare sweep off, the original ungated scheduler and 0.4.111. Record
per-waypoint readiness, generation-queue history, save growth, frame/tick tails, sweep holds and
effective sweep rate.

### 18.5 Consequence for the terrain-change plan

Scheduling and invalidation remain separate. Version 0.4.111 protects current player demand when a
load is necessary. `PLAN_TERRAIN_CHANGE_DETECTION.md` reduces how often such a load is necessary by
using a clean marker and save metadata for the no-change fast path, then fingerprints for exact
changed-column discovery. First adoption and real mismatches still need the scheduler.

Elapsed game time and player population cannot prove that saved terrain is unchanged. They may pace
background work, but correctness must come from live change hooks while the mod is active and
savegame fingerprints after an unwatched, disabled or crashed interval. The Codex recommendations
appended to that plan are explicitly proposals awaiting independent review.
