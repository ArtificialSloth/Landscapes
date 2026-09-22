# Vintage Horizons — TODO and verification debt

> Tier 2 companion: open work only. Completed narrative moves to `dev/history/DONE.md`; current conclusions belong in `STATUS.md`.

## Start here

Read [Session 109](sessions/SESSION_109.md), then [Session 108](sessions/SESSION_108.md).
Section sync - the terrain-change plan's Phase B - is built (0.4.126-0.4.127) and passed a four-join
dedicated sandbox test. Of 0.4.125's seam fog the owner said "Everything looked good". Neither is the
next task by default: ask him what comes next before choosing work. Three things are recorded for
when he wants them.

### Section sync: what 0.4.127 left open

- **The owner has not played 0.4.127.** In singleplayer the client side is idle. The server cache
  should log "Adding section versions to this server cache" once, on its first open, and nothing
  should look different.
- **No second player has been involved.** The sandbox's "changed while away" was written into the
  server cache between joins, the way a server save would make it. A dedicated server with a second
  player changing terrain while the first is offline is the remaining real test. After it, the first
  player's rejoin should log "N held sections changed on the server since this client last caught
  up", then "caught up".
- **The large-world re-check is arithmetic, not measured.** About 15,000 held sections at the
  server's default 8 per second is roughly 30 minutes and 700 MB per player on a bodanboys-sized
  world. Measure it once when a dedicated copy of a large world is at hand. It has to finish across
  quits; the owed list makes that possible, but a real multi-session re-check has not been observed.
- **A server cache restored from an older backup** is detected only when a client's record is newer
  than the restored copy. That is a limit of the design, recorded rather than open work, unless
  backups become a normal workflow.
- Re-run the sandbox test with [`bench/routes/section-sync.txt`](../bench/routes/section-sync.txt)
  and [`scripts/section-sync-inject.py`](../scripts/section-sync-inject.py). Session 109's profile
  is `.testdata/profiles/phaseb`.

### For the future: hitches while exploring new terrain (owner, 2026-09-21)

On 0.4.125, while exploring new terrain, the owner saw hitches, with the minimum FPS dropping low
periodically. He does not know whether it is the fog or Farseer, the savegame sweep changes, or
something else, and asked for performance optimization to be looked into in the future. Nothing
has been measured yet; do not start without his go-ahead.

What is already known that bears on it, none of it a conclusion:

- Session 104 recorded "consistent minor FPS dips while exploring new terrain" on 0.4.116. That
  session's sweep was in its first adoption, and its dips were attributed to vanilla generation
  plus a one-time sweep share.
- The dormant-terrain snow refresh has had about 12,000 columns queued at 1/s on `test2` since
  0.4.119, and no frame-time run has been taken with it running (see below).
- Each seam-fog field rebuild uploads about 11 MB of half floats at a 4,096-block reach. Since
  0.4.125 it also builds and uploads a residual, and while a residual fades the shader evaluates the
  old field's shape again wherever the seam moved. Exploring rebuilds the field often. Rebuild
  hitches were already an open question for 0.4.124.
- Farseer previews, decode and meshing are worker-bound, and their uploads are capped at four tiles,
  2 MiB and 2 ms per frame (Session 100 found Farseer idle on 73% of its ticks while exploring).
- On a dedicated server the client re-fetches every held section the server re-saves near it.
  Session 109's sandbox saw about 66 merges in two minutes with the player standing still, as ticking
  blocks changed the ground. This is behaviour from before section sync, but it is steady background
  network, decode and disk work, and not yet measured for frame cost.

A first pass would separate the candidates on one route, turning each off in turn: `.vhfog off`,
`.vhsilhouette off`, the savegame sweep off, and then vanilla. Ask him how often the hitches come
and how deep they go, from his frame-time graph, and whether they change with movement speed.
Any diagnostic has to reach the client or server log, because he cannot copy game chat. The active
route for this work is `dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md`.

### Seam fog: what 0.4.125 left open

- **Not separately reported.** "Everything looked good" is a whole-result verdict. If the owner
  mentions the fog again, the specific questions are:
  - whether the strip reads as mist or as a painted stripe from above;
  - whether any trace of the steps shows straight down (the mock-up's worst pixel is 6%);
  - whether edges taller than the strip show at the seam: tree lines, cliff faces, the sunk
    Farseer apron. Seam strip height is the dial for that;
  - whether the ribbon holds still while turning and travelling (0.4.124's fix);
  - halos or blocky edges around near objects from the half-resolution upsample;
  - whether 0.8 s is the right fade, and whether new fog should fade in as well (offered, not built).
- **Cost.** The `.vhfog` on/off frame rate in one scene has not been taken since 0.4.123's 400 against
  550 FPS. It belongs with the exploring-hitch work above.
- **Defaults.** His tuning (spread 36, floor depth 20, edge softness 35%) is the source default after
  0.4.125 and reaches players with the next build.
- Render `scripts/fog-mockup` before and after any change to the medium, and read its worst pixel
  as well as its average.

### A fast check that fails intermittently

During the Session 108 doc pass, `VanillaReadinessChecks.SteadyStateAllocation` ("converged lookup,
revalidation scheduling, probing, and classification allocate nothing") failed in 3 of 7 runs of
the fast tier with no source change between them; it had passed in every earlier run that session.
It counts allocations on its own thread only, but its warm-up never calls `QueueMaintenanceCells`
or `PromoteObserved` before the measured loop. The cause is not established.

## Also open: 0.4.119 needs a session with a real sweep behind it

Read [Session 105](sessions/SESSION_105.md) first. Two builds landed and both were played on
`test2`, so phase A is no longer waiting on a play verdict: the startup sweep demonstrably skips
what it has already captured (10,172 of 17,296 columns, finishing in 27 seconds against 25 minutes
for a first adoption), the background audit and the revived snow refresh have reported real numbers
for the first time, and a clean quit left 0.34% of claims stale against 3.8% a session earlier.

What is not settled is how the new claim path behaves under load. The 0.4.119 session captured 273
columns; the interesting case is a session with a large sweep behind it, where hundreds of columns
settle at once and the re-read queue matters.

Open, in the order they block further work:

- **Play a world with a real sweep behind it, on 0.4.119.** A fresh world, or one with a lot of
  newly generated terrain, so several thousand columns pass through the settle path in one session.
  Afterwards the status line should report claims "read back after a world save" far outnumbering
  ones "from the startup reading", and the next start should load only a few hundred columns.
  This is the test that 0.4.119's claim change actually scales.
- **Give claim re-reads priority over audit re-reads.** Measured on 2026-09-20: claims for the
  20:54 save were not written until 20:56:39, because settle re-reads queue behind the audit's
  steady 16/s stream. Not a correctness fault - a session ending inside that window simply falls
  back to the index-time claim - but it makes the tail of every session fall back unnecessarily.
- **Measure the snow refresh's backlog cost.** The arm that was dead for six weeks now has about
  12,000 columns queued at 1/s on `test2`, roughly three hours of background priority loads. No FPS
  or frame-time run has yet been taken with that trickle running, and the owner deliberately stood
  still for both 0.4.118 and 0.4.119. This is the most likely source of any new in-play cost.
- **Confirm or overturn the A4 recommendation.** See Session 103 §7 and Session 105. The
  clean-shutdown marker still cannot be built (G236), but this session's measurement is the
  argument for keeping A4's save-hook half - which 0.4.119 has now built in its own shape. What
  remains is the owner's decision to record the plan's §4.1.5 fast path as dropped.
- **Prove the audit catches a real fault.** Unchanged and still open. It is now proven to run and
  to dispute claims - 81 in one pass on the owner's world - but every dispute so far is ordinary
  change, not a cache that was wrong. Staging real corruption was not possible in a live world;
  its decision rules are covered offline.

- **Complete the underlying ownership/motion matrix.** Fog intentionally conceals the transition,
  but must not become an excuse to regress correctness:
  - hold position and turn through the whole scene; Farseer ownership must not shift with yaw;
  - approach vanilla and LOD edges from oblique angles; no Farseer may appear over ready terrain;
  - move quickly enough to watch replacement load and unload; Farseer must remain until the
    replacement draws, with no sky flash or gap outside the veil;
  - look into excavated vanilla ground; no Farseer may show beneath it;
  - compare overlapping hills; slope/depth contrast should remain stable as the camera turns.
- **Complete the remaining P4/P5 observations:**
  - look once at night, in rain/natural fog and in winter;
  - verify the final scale typography/spacing and that the reach handle changes actual reach;
  - tune Outline detail, Hill softness and Apron width/depth once the fog is accepted. The Seam fog
    switch and ten fog dials (seam and outer density, coverage, spread, height, floor depth, billow
    size, drift speed, Fog quality and edge softness) already persist and preview live;
  - record Farseer off/on and seam fog off/on FPS in one scene, and any reach/shape-change hitch.
    The owner's earlier 550→450 Farseer observation was informal and predates the geometry
    simplification, exact all-level coverage and the fog pass;
  - compare islands/inlets only after the join is accepted; refine low/coastal sampling only if the
    picture still needs it.
- **Finish Farseer credit deliberately.** Rename remaining player-facing “Silhouette” labels,
  settings and commands where compatibility permits, while leaving registered protocol names
  stable. Bundle Farseer's MIT license/notice in addition to the existing source attribution.
- **P3 integration debt.** Source and harness work is complete; live evidence is not.
  - In singleplayer, let the requested disc complete and confirm counts, sources and fill ratio.
  - Persistence is human-seen across leave/rejoin. Still check a cold client restart, full-disc
    read-back, and store unopened/closed behavior while Farseer is off.
  - With other players available, exercise a dedicated server explicitly opted in, its reach clamp,
    per-player fairness, disconnect/off cleanup and a current/older peer combination.
  - No `/vhworldgen` run has yet been checked for its captured columns appearing in silhouette
    tiles (the P2 integration gate).
- **Then P6:** Terrain Ahead remains a separate feature with its own switch; P7 canopy follows only
  after P6 acceptance.
- **Useful but non-blocking silhouette measurements:** mountains (the P0.3 areas were coastal),
  accelerator sampling beside real generation, and why the test worlds' sea level is 82 (G208).
- **`bodanboys` preview stall** remains set aside by the owner. Previews there stalled the
  world-generation thread after Watersheds/TerraPrety coast-map errors (G204). Its cause requires a
  dump of a stalled process; whether the world stalls without silhouettes is unknown.
- **Later, not now: the sampler's other values** (owner, 2026-09-14). Algernon's Terrain Sampler
  also returns rainfall, temperature, geologic activity, forest and shrub density. These may help
  later work such as P7 canopy; nothing is planned yet.

The world-generation/LOD-coverage plan's core is complete (Session 91). Its verification debt,
not a gate for silhouette work:

- **Final durability.** Let one small circular job reach its final completed state and verify the
  server/cache rows are durable after a normal quit.
- **Restart resume.** Interrupt an active job with a real server shutdown, reload the world, accept
  the offered resume, and verify the bitmap continues rather than restarting or resuming silently.
- **Dedicated server.** Verify `controlserver` can prepare/confirm/pause/resume/stop, an unprivileged
  player sees progress read-only, and an older/vanilla server makes the Generate page unavailable.
- **Long run.** Measure memory, disk growth and the job's named gates over a larger circle.
- **Shutdown setting (W8).** `LodPipeline.StorageShutdownTimeoutMs` is still a fixed 15 seconds;
  expose it as a bounded server setting if this plan is revisited.
- **Deferred speed experiment.** Starting the next tile while the previous tile saves may remove a
  boundary bubble, using existing backlog gates as the brake. The owner said to hold off on
  2026-09-14; do not begin it without renewed direction.
- **P3 after-comparison.** `siblingfield` was not re-run after the owner's revisit; the 80 finished
  but uncaptured columns of the 18:23 world are still missing from its server cache.
- **Existing limits retained.** Dedicated servers cannot fill an already-held section from an
  unchanged pre-join row, and snow refreshes do not directly transfer far parents; clients rebuild
  parents from newer L0 where a durable refresh exists.

## Savegame sweep after 0.4.111: frontier evidence and workload avoidance

Authoritative analysis, engine queue trace, invariants, alternatives and acceptance matrix:
[`plans/ANALYSIS_SAVEGAME_SWEEP_SCHEDULING.md`](plans/ANALYSIS_SAVEGAME_SWEEP_SCHEDULING.md).

The controlled frozen-profile A/B and 0.4.111 follow-up are complete; see
[Session 102](sessions/SESSION_102.md) and §18 of the analysis. During the priority-load phase,
sweep on and off had effectively identical average FPS, but the original sweep worsened the 1% low
from 110.3 to 88.3 FPS, the 1% frame-time tail from 9.07 to 11.32 ms and RSS from 7,578 to 8,820 MB.
Engine-counter-only gates at thresholds 8 and 1 failed to see ordinary saved-column demand.

Version 0.4.111 instead pauses new startup-sweep priority loads for five seconds after any player
joins or crosses a chunk boundary. Any player's movement extends the window; stationary players and
no-player servers permit configured-rate progress. `CurrentGeneratingChunkCount >= 1` remains a
secondary real-generation guard, and the capped allowance prevents a catch-up burst. The follow-up
recovered 109.0 FPS / 9.17 ms tails, essentially the sweep-off control's 110.3 / 9.07, with no
capture drops/errors. The original A/B, signal selection and 0.4.69 drop-line runtime debt are
complete and live in `dev/history/DONE.md`.

Open, in the order they block further work:

- ~~**Run a sustained virgin-frontier A/B.**~~ **Done 2026-09-19**, and it produced 0.4.112's rate
  floor. Results in `STATUS.md`. Three conditions on `bench/routes/sweep-virgin-frontier.txt` with
  the frozen seed restored per run; the pre-gate arm was built from checkpoint `2fbf2d1`, whose
  only source difference from 0.4.111 is the sweep. What remains from it:
  - **0.4.112 has no play verdict**, and its floor size was chosen from one A/B rather than tuned.
  - **Multi-player fairness is still unmeasured.** One player's hold still stops the sweep for
    every other player; the floor bounds how badly, but nothing has been measured with two.
  - **The snow-refresh arm is still ungated.** `StepRefresh` issues `LoadChunkColumnPriority` with
    no player gate, no engine gate and no capture-backlog brake. 1/s by default against the sweep's
    16/s, so small, but the startup pass terminates and snow campaigns do not.
  - Per-waypoint readiness was not captured; the comparison rests on frame tails, storage age and
    sweep throughput.
- **Decide what to do about the probe pass, which is the cost a short session actually pays.** On
  the owner's world the probe phase alone outlasts a five-minute session, every start, and captures
  nothing while it runs. Phase A of the terrain-change plan removes that pass outright (§4.1.6 item
  1). That is a stronger case for Phase A than the repeated-load waste the plan leads with, and it
  is worth the owner knowing before he sequences the work.
- **Fold in or discard the §12 review's recommendations about §11 itself.** Findings 1-3 were
  factual errors or genuine gaps and are already corrected in §§4.1.4, 4.1.5, 4.1.6, 4.3 and 7.
  Findings 4-8 are about §11's own text and are the owner's call: §11.3 should say "reuse the
  existing `AutomaticGenerationGate`" rather than "add storage pressure" (capture backlog has
  gated loads since 0.4.68); §11.4 needs a starvation floor; the snow-refresh arm should be named
  as the known-open lane rather than an eventual nicety; §11.5's probe paragraph contradicts Phase
  A2/A3; §11.2 duplicates analysis §18.5.
- ~~**Give `StepRefresh` the same gates as `StepLoad`.**~~ **Done in 0.4.116**, and it became urgent
  rather than optional: the arm had never run at all (G237), so reviving it would have put an
  unmeasured ungated priority-load path into the world. It now takes the capture-backlog brake, the
  engine-generation gate and the player-activity hold with its rate floor. **Its cost has never been
  measured**, because until 0.4.116 there was nothing to measure.
- **Implement terrain change detection.** ~~Phase A~~ **A1–A3 and A5 are built (0.4.113–0.4.116)**;
  see Session 103 and `STATUS.md`. ~~Phase B~~ **Built (0.4.126–0.4.127), Session 109**; its open
  items are under "Section sync" at the top. **A4 was not built
  and is recommended against as specified** (Session 103 §7): there is no hook after the engine's
  final save, so its marker cannot record the file's post-save state, and the payoff fell from
  5m35s of contention to 73.3 s of off-thread reading once A3 removed the probe pass.
  The three corrections from the §12 review are implemented: a fingerprint is committed only once a
  column's capture reached the durable row; the change counter replaces file size and mtime, with a
  WAL branch; and nothing re-fingerprints inside `GameWorldSave`, which fires before the write.
- **Do not substitute time or population for change detection.** In-game time may pace an audit,
  but it cannot prove a change. Singleplayer can change terrain immediately; many multiplayer users
  may change nothing. Live hooks cover loaded changes while the mod is active, and fingerprints
  cover disabled, crashed or externally changed intervals. Player movement/count is a scheduling
  signal only.
- ~~**Choose any additional pressure gate from frontier evidence.**~~ **Closed 2026-09-20: none is
  warranted, and the candidate signal was misread.** Dirty *age* does not measure a write backlog —
  a section keeps its first-dirty timestamp while it continues to change, so the coarse parents age
  without bound during any sweep while the writes keep up (G238). Gating on it would have throttled
  the sweep permanently for a benign reason. The dirty *count* never approached its limit (52
  against 512) in the one production observation available, and the save queue is already bounded at
  `MaxStorageBacklog` = 256. Reopen only with evidence that downstream persistence is actually
  player-facing, not with a number that merely looks large.
- **Probe scheduling is moot where the read path works.** The probe pass only runs now when the
  savegame read path is unavailable (A3), so its pacing is a fallback concern rather than a live
  one. The item below predates that.
- **Leave probe scheduling alone for now.** The probe on/off pair had a worse 1% tail with probes,
  but average and median FPS moved the other way. Repeat or add waypoint evidence before changing
  probe pacing.
- ~~**Apply fairness to later background work.**~~ **Done in 0.4.116:** the audit and the snow
  refresh arm both take the load pass's gates and the same capped no-catch-up allowance. What
  remains is the assessment, not the policy: **winter convergence has never been demonstrated and
  until 0.4.116 could not have been**, because the arm that performs it never ran (G237). Judge it
  fresh rather than reading the old debt as a tuning problem.
- **Count the swept arc's cost.** Each swept end now emits two cross-sections instead of one, and on
  a staircase frontier every strip is swept at both ends. The vertex/index budget is exact and
  harness-checked, but it has not been counted against a real frontier or measured on the GPU.
- **Split the Farseer yield counter by reason.** `LodSilhouetteBuilder.YieldTicks` is one number, so
  the log cannot say whether Farseer held for the engine's pending chunks, for a held generation
  authority, or for Vintage Horizons' own capture/save backlog. In the measured window the cache
  save backlog was 80-390 s old against a `WorldGenOldestDirtySeconds` limit of 120, so
  `EnumWorldGenGate.SaveBacklogAge` is a live suspect for much of that 73% and cannot be confirmed.
## Distant structures and block-entity colours: what 0.4.62 leaves open

The white ruins are fixed and human-tested ([Session 87](sessions/SESSION_87.md), G180-G182).
Remaining:

- **Clutter, bookshelves, ground storage and lamps have no single material.** They take a colour
  from one of their own textures, or neutral grey; how that reads at distance has not been looked
  at specifically.
- **Structures cached before 0.4.62 turn grey, not their real colours.** 1,105 stored placeholder
  colours were repaired on load in the owner's first 30 seconds. They gain their materials only
  when the ground is captured again; whether `/vhrefresh` on the server delivers the refreshed rows
  to the client has not been checked.
- **The server half is not counted in any log the owner sends.** In 30 seconds the client's own
  captures recorded 4,496 chisels as their material, 28 without one and no failed reads. Whether the
  server reads block entities on every capture path (savegame sweep, generation peeks) is
  unmeasured.
- **Neither half of the cost is measured:** copying chisel contents where capture jobs are created,
  and the arithmetic on the capture worker.
- **Snow on a chiselled roof** comes from the chisel's own flags; source-reviewed only.

## Distant anti-aliasing and automatic thresholds: what is open

The shimmer is fixed and tuned by the owner ([Session 86](sessions/SESSION_86.md)); he kept the
dial ranges on 2026-09-12. Remaining:

- **Cost is measured on one machine.** The owner's RX 9070 XT at 1920x1080 rendered reported the
  resolve and composite at p95 100 us and the depth snapshot at p95 25 us, over about 464,000
  frames. Weaker GPUs and 4K are unmeasured; the Performance preset turns the feature off.
- **Not specifically looked at:** a large reach across the vanilla edge, distant water's edges, and
  smear at low frame rates, where each frame of memory lasts longer on screen.
- **The shaders are compiled only in game.** No GLSL validator is installed here; a compile failure
  switches the feature off for the session with one log line, and `.vhtaa` reports it.
- **Automatic thresholds have been in use since 0.4.57 without complaint**, but not evaluated at
  other FOVs, render scales or larger vanilla view distances, where re-selection and the level-0
  floor move.

## Handoff flash follow-ups

The vanilla-to-LOD handoff flash is fixed in 0.4.53 and confirmed by the owner. Its cause, the
change that introduced it (0.4.13) and the fix are in [Session 84](sessions/SESSION_84.md) and
G170. Remaining:

- **`.vhgap` scans before the physics step** (Before/0.994), so its range verdicts can be a frame
  old. Move the scan after the late range check or retire it.
- **Comparison switches for disproved hypotheses.** `.vhsnowline` was retired in 0.4.70. Retire
  `.vhgap release` or keep it deliberately. `.vhsky` is worth keeping as a diagnostic.
- **Stale recorded spheres and eviction gaps.** When vanilla re-tessellates a buried chunk beside a
  newly unloaded neighbour, the recorded draw sphere disagrees for 1–7 frames (invisible in
  practice); the sandbox also found two eviction gaps of 4 and 7 frames. Refreshing the recorded
  sphere from `RegisterOnChunkRetesselated` would close the first.
- **The 0.4.39 bisection result.** It was judged clean although its timing matches 0.4.40's. This
  is not explained; measure it with an objective count only if it matters.

## Winter update verification debt

The mechanism is built and the sampled appearance is satisfactory, but temporal convergence has
not yet received a clean acceptance run:

- Observe one real accumulation and one real melt without travelling through the target area.
  Confirm the 30-second regional signal, finite campaign, durable L0 update, live offer and client
  mip rebuild all occur, and record elapsed time for the configured 1-column/s default.
- Verify permanently warm and fully snow-covered regions become idle between weather-band
  crossings, while a dormant seasonal region eventually wakes through the one-region-per-poll
  canary.
- Exercise `/vhrefresh` at radius 0, the default radius and a frontier. Confirm no absent
  vanilla column is generated and a connected client receives the durable replacement.
- Compare authoritative snow at L0 and across mip boundaries, including mixed/tied coverage,
  frosted grass and frosted foliage. The owner has accepted the general look, not this matrix.
- Measure campaign tick cost and network/storage volume in a large explored world. The design is
  bounded (1 ms issue budget, default 1 column/s, level-zero transfer only), but no paired runtime
  performance capture isolates it yet.

## Distant water: protect the accepted water and complete continuity evidence

The accepted baseline is `0.4.64` on `codex/water-depth-light` (owner, 2026-09-12); the water
behaviour accepted in `0.4.39` is what this section protects. Read
[the current handoff](plans/HANDOFF_WATER_SEAM.md) and
[Session 82](sessions/SESSION_82.md). The settled seam and the 0.4.37 streaming improvement are
human-accepted in the tested scenes. Version 0.4.38's shared readiness-mask extension caused severe
terrain/seabed holes and was completely removed in 0.4.39.

- **Treat the remaining streaming flicker as an accepted limitation.** When a new vanilla water
  chunk first draws, a barely perceptible light flicker may remain. Do not pursue it through the
  shared readiness tracker, terrain ownership mask, chunk-presence state or publication timing.
  Any further experiment must stay inside the dedicated water stage and leave opaque terrain and
  seabed continuity untouched. A terrain-ownership change is separate scope requiring explicit
  owner authorization and a terrain-first runtime test.
- **Do not regress the settled seam or safe 0.4.37 veto.** Full-resolution OIT revealage replaces
  quarter-resolution liquid depth for junction SURFACE coverage. Before durable ownership catches
  up, cached OIT water is suppressed only when revealage and liquid depth both prove vanilla
  liquid at that pixel. Liquid depth remains authoritative for underwater murk. Preserve alpha
  1.3 / foam 1.6 / clarity 40.
- **Watch, do not tune, the movement-only open-water blips.** They were briefly reported away
  from the seam and then could not be reproduced. If they return, capture while moving and locate
  the mark in vanilla revealage, combined revealage, raw/repaired depth and final color before
  changing repair, foam or glint.
- **Complete the continuity matrix:** repeated movement and chunk streaming, both sides of the
  junction, corners and LOD joins, same seabed through each surface, shallow/deep shores, below
  water, stale cache, resize/fullscreen, shader reload and world changes. Include unrelated OIT
  transparencies after the same-frame veto is built.
- **Finish required-path lifecycle coverage:** transparency disabled and deliberate resource
  failures must produce a named failure and missing cached output, never plausible output from an
  older path.
- **Keep opaque fallback retirement separate from water development.** The per-section opaque
  `RenderMesh` call and runtime selection are gone, but remaining `MeshRef` allocation,
  publication and disposal are terrain-renderer work. Do not fold that cleanup into a water fix;
  it needs its own authorization and regression scope.
- **Rework old Phase 9 failure-injection expectations.** Tests and benchmark notes that expect a
  same-frame or next-frame legacy fallback are superseded for product renderer failures. Retain
  explicit diagnostic A/B modes only where they cannot be mistaken for automatic recovery.
- **Retain earlier geometry debt:** unknown-column water walls, wave strip/reach tradeoffs and
  shoreline detail need their own evidence. The owner's current wave-strip value of zero is a
  separate saved choice, not part of the accepted material defaults.

## Frame-time regression and Settings: what remains

Branch `codex/vertex-tint-cost`, `0.4.0`. The regression is measured and its unreachable half
is closed; what follows is what a sandbox run cannot answer. Read `dev/sessions/SESSION_77.md`
before touching this.

- **The fixes are measured in the sandbox, not in the owner's scene.** 100 us of 270 recovered
  at one seam viewpoint that runs about 2.9x lighter than his. The proportion should carry and
  the mechanism does not depend on the scene, but the number in his world is inference. **The
  tell: the same spot that read 285 fps on 0.3.194.**
- **Far Biome Colors defaults off, and the colour step that implies is unexamined.** With Close
  on and Far off, ground past the L2 boundary - about 2,048 blocks on default thresholds -
  wears the biome colour from where the player stands rather than its own. Whether that reads
  as a ring depends on how different the two biomes are. **The tell: stand where a biome edge
  crosses the horizon and compare Far on against Far off.** If it shows, the answer is a wider
  Close range, not a wider fade: `LodVisualSettings.CloseClimateTintRange`. That constant was
  briefly 1 (L0 alone) in 0.3.197-0.4.0 and is 4 again from 0.4.1; see G151 for why.
- **The pause-time loading mechanism needs a direct runtime witness.** The source and harness
  establish that it runs the same bounded owner-thread work at 50 ms cadence without unpausing
  simulation. Hold the single-player pause menu open and observe cache/mesh progress, then turn
  **Load terrain while paused** off and confirm progress stops while the world stays paused.
- **The residue is closed as an aggregate, not item by item.** All-features-off on 0.3.195
  measures exactly 0.3.127, so the three named costs are gone between them. Their individual
  shares were never separated, and doing so would need a shader edit per hypothesis.
- **`.vhcutout` is deliberately absent from the window.** It is a small win that reintroduces
  the seam holes it was added to fix. If it is ever wanted there, it needs the hole fixed
  first, not a warning label.

## Distant terrain colour: what is left after Session 76

Sessions 73-76, branch `codex/seasonal-tint-blend-weight`, committed through `f8d22f7`. The four causes
the plan set out to fix are fixed and human-accepted; what follows is the remainder. Read
`dev/sessions/SESSION_76.md` and `dev/plans/PLAN_DISTANT_TINT_SAMPLING.md` before touching this.

- **Remembered climate was inert from 0.3.185 to 0.3.190 and is fixed in 0.3.191, unseen.** The
  handle to the cache writer was taken in `StartClientSide`, but the pipeline builds it in `Open`
  when a world is joined, so it was always null and nothing crossed the disk in either direction.
  The in-memory half worked, so coverage still grew within a session and the owner's acceptance of
  0.3.185 was about behaviour that was really there - just not the durable part. See G147; the
  same null was also masking a crash on world leave. **The tell is now testable: explore, quit,
  rejoin, and `.vhclimate` should report remembered cells before you have gone anywhere.**
- **Distant leaves turn unevenly again, and the design question it raised is answered.** Built in
  0.3.187: the shader picks the game's own season map ROW per position using the engine's own
  `noise3d.ash`, and blends toward the averaged colour as one voxel grows, because G47's reasoning
  holds once a voxel is a neighbourhood. What the entry could not decide in advance was where that
  fade should sit; it is now settled by measurement rather than by eye. See G148.

  The reach is `LeafSeasonFadeBlocks`, a uniform, defaulting to 2 - level 0 alone. `.vhtint
  leafwide on` restores the old fade to 8 blocks so the two can be compared in place at one scene.
  Owner-measured: **292 fps across three levels, 300 at one, 310 with the feature off**, against
  315 before the feature existed, with **no visible difference between one level and three**.

  Two things it still will not do, both reasoned from the mesher rather than measured: match
  vanilla tree-for-tree beyond level 0, where one voxel is many blocks; and reproduce variation
  WITHIN one canopy, because merged leaf faces sample only at their corners. Distinct trees are
  separated by air and keep their own noise, which is the part that matters.

- **About 10 fps at level 0 is the seasonal row's residual cost, and nobody has attacked it.**
  That is the floor of a design that fetches a season row per vertex: two value noises and a
  scattered texture fetch into a 136 KiB paged map, on every vertex of every level-0 section. The
  only obvious way to remove it is to bake the row into the mesh at build time - the noise is a
  pure function of world position, so it is a per-vertex constant being recomputed every frame -
  but the season moves, so a baked row trades frame cost for a mesh rebuild whenever it does. Not
  obviously worth it at 10 fps. Do not start this without first re-reading G148: the last two
  attempts to price this feature both guessed wrong about where its cost was.

- **Five fps sit between "feature off" (310) and the pre-feature baseline (315), unexplained.**
  Small enough to be scene variance, and the owner's two readings were minutes apart rather than
  back to back. It is also where 0.3.186 through 0.3.191 sit. Worth isolating only if it grows or
  if it bothers the owner; the way to do it is `.vhtint leaves off` on 0.3.185 against the same on
  0.3.194 at one scene.

- **Slot sharing is fixed but unseen.** The key was the MEAN of the share's three channels
  rounded to eighths, which collapsed the base game's 29 top-soil blocks onto six shares and lent
  `soil-medium-verysparse` (0.176, 0.125, 0.079) the share of `forestfloor-5` (0.188, 0.142,
  0.107) - 35% too much blue, and blue is where it shows because the seasonal tint carries almost
  none. The key is now the exact byte triple the slot table publishes, and overflow falls back to
  the nearest registered share rather than to untinted. Measured against the game's own textures:
  24 distinct shares at 1/64 per channel across 29 blocks, so this costs about two dozen slots of
  64 rather than six. Shipped in 0.3.186; the tell would be sparse-grass soil no longer reading
  too blue.
- **`.vhtint`'s labels were corrected in 0.3.184.** A is now the flat fallback and says whether
  it is actually what draws; B is the lattice average; C is labelled as the single random season
  row it is, with a pointer to G145 saying not to hold it against an average. The C' line and the
  weight switch went with the height ramp. The D lines remain the report of what reaches the
  screen.
- **The plan's section 9 acceptance criterion cannot pass as written** - see G145. It should ask
  for the field path against the lattice mean, not against the engine's answer for one block.
- **The placed-block invalidation defect (G138) is fixed in 0.3.189 and unseen.** Content change
  and touched edges are separate answers now. The tell would be a block placed well inside a
  cached area updating in the distant terrain without needing an edge mutation to flush it.
- **Nothing has been measured since 0.3.183.** The owner's verdict is "much better"; no post-fix
  screenshot has been differenced, so the size of any remaining mismatch is unknown.
- **No shader is compile-verified offline.** The vertex stage's tint path was rewritten again on
  all three render paths. It draws, so it compiles, but nothing offline can say so.
- **The climate field's coverage figures are one world, one machine, one afternoon.**

## Distant terrain colour: verification debt carried from Session 73

- **0.3.170 changed the stored colour of every non-TopSoil block in the game.** Stone and grassy
  soil were measured; nothing else was. Any block whose thirty-pixel sample happened to be badly
  unrepresentative moves further than gravel's 8%. The tell would be one block type looking wrong
  rather than a general shift.
- **The 4% residual on gravel is unattributed.** Ambient occlusion, shadow-map filtering and the
  atlas mip chain are the candidates. A screenshot cannot separate them, and it is below the level
  worth chasing unless something else makes it matter.
- **Sparse grass pattern: fixed in 0.3.188, unseen.** `TryTopSoilOverlay` preferred
  `particletop`, which for `soil-*` is always `normal-top` regardless of coverage, where the
  engine draws the right tile of the coverage-specific `specialSecondTexture`. It now takes the
  strip first and keeps `particletop` only as a fallback. Safe for full grass: `normal-top.png`
  and the right tile of `normal.png` are byte-identical, checked pixel by pixel. Colour was never
  affected - the mean the shader divides by is the mean of whatever is sampled - so the tell is
  pattern density on sparse ground inside the band, not hue.
- **Grey `soil-low-normal`: confirmed gone, and the stale colours behind it are inert.** Scanned
  the owner's live 446 MB cache on 2026-09-03: 7,201 sections, 3,804 entries for that block,
  **zero** still flat grey (134,134,134). What is there instead is 3,521 at (133,128,107) - the
  PRE-0.3.169 whole-strip composite, arithmetically confirmed - against 283 at the corrected
  (152,154,127). So the disk is 92.5% stale, and it does not matter: both load paths call
  `ResolvePendingPalette` before a section is installed, and `Reclassify` re-derives the colour
  from the live block there, so the stale value never reaches a mesh. Session 73's "writes back"
  is true only for sections that happen to be re-saved; its "corrects itself on load" is true for
  every section that draws, which is the part that was load-bearing. No action; recorded so the
  next person does not re-scan.

## Exact draw-range ownership: what 0.3.160 has not proven

Ownership reads the engine's own bounding sphere and view distance instead of bounding them, and
`.vhexact audit` measured 100% coverage with zero holes. That is one world, one view distance, one
camera mode.

- **The water footprint's dilation now derives to zero.** It follows the refused ring, which the
  exact rule took from 62 blocks to 16 and Session 70's overlap default takes to zero, so G132's
  arithmetic rounds the growth to nothing either way. That is the correct consequence and it has
  not been seen in play. 0.3.151's chunk-wide dry strip came from exactly this quantity;
  `.vhwater edge <chunks>` overrides it live.
- **The band's inner offset moved with the ring**, 46 blocks to 16 and now to zero, and the band
  was human-tuned against the first of those. `.vhband inner <blocks>` pins it, `auto` hands it
  back. The owner has been running at a zero ring by hand since before Session 70 without
  reporting the band, so this is stale-looking rather than unobserved.
- **Third person is untested** and is where it should matter most: the engine measures range from
  the player and the mod used to measure from the camera, which trail each other by several blocks.
  That correction is unobservable in first person.
- **No benchmark.** The range sweep's own counter fell from a 1.899 ms peak to 0.386 ms once the
  world settled, and probe rate is unchanged at roughly 28,000/s. Neither is a frame-time
  measurement, and nothing has been benchmarked since 0.3.9.
- **The stored sphere can go stale** if a chunk retessellates into a different shape, bounded by
  the 1 Hz revalidation and usually corrected sooner by `ChunkDirty`. Fails toward overlap in one
  direction and a brief hole in the other; neither observed.
- **0.3.160's shader-success line has not been seen in a log.**

## Seam holes: what Session 70 has not proven

Both causes are fixed and `.vhband overlap` defaults to 0 on the strength of it. 0.3.162 is
human-tested and was reported as excellent; 0.3.163 is not.

- **0.3.163 is not human-tested.** The flax-field hole is the acceptance case: stand at a seam
  running through a crop field and look for sky through the plants.
- **The crop coverage rule costs 2.2-4.4% geometry and has no frame-time measurement.** Draw
  clusters do not move in any of four offline samples, which is the number that usually decides,
  but nothing has been benchmarked. `.vhcutout on` is the A/B and reports the live vertex total.
  The +4.37% sample is roughly double the median with no cause established; four samples is a
  small n (G98).
- **Leaves carry G137's defect and are excluded on cost. NOW PRICED - it is the owner's call.**
  Measured 2026-09-03 against the owner's live cache with `cutoutfield --families
  leaves-,leavesbranchy-`, the measurement this entry asked for:

  | | crops (accepted, 0.3.163) | leaves |
  |---|---|---|
  | share of level-0 sections | 42% | **78%** |
  | palette entries in the cache | 4,855 | **88,322** |
  | opaque geometry | +2.19 to +4.37% | **+19.95 / +20.24 / +22.02%** (three seeds, 120 sections) |
  | draw clusters | +0.00% | **+0.00%** |

  So it is six to ten times what crops cost, and the cost lands exactly where the entry predicted:
  X and Z walls +25 to +27%, tops nearly free at +9%, because every pair of adjacent leaves in a
  canopy gains a wall between them. Draw clusters do not move at any level, which is the number
  that usually decides - here it does not, because this is vertex memory and upload rather than
  submissions.

  **It does not fall off with distance:** +19.80% at level 1, +19.27% at level 2, +15.99% at
  level 3. So restricting the flag to level 0 - fixing the seam, which is where holes are visible,
  and leaving the far field alone - is a real option and a real saving rather than a free one.
  Still nobody has observed a seam hole on a tree; the harness reports the entire cost and cannot
  report the benefit.
- **The blend band's own address carries G136's off-by-one** at 8-block granularity, so a wall on
  an 8-block plane takes its neighbour's band weight. Left alone as colour-only and not verified
  as harmless in play. Fixing it means choosing what a distance field should answer at a boundary,
  which is a look decision, not arithmetic.
- **`OverlapMarginBlocks = 0` is source-only.** The owner directed no new artifact for a default
  change, so the installed 0.3.163 zip still starts at 16 and `.vhband overlap 0` is still needed
  in that build. The next packaged artifact carries the new default.
- **No shader this session was compile-verified offline.** There is no GLSL validator in the
  toolchain; 0.3.162 loading in game is the only proof, and 0.3.163 has not been run.

## Half-vanilla trees at the seam: investigated, accepted, not fixed

Reported in Session 70 and closed as correct behaviour rather than a defect. Recorded so it is not
rediscovered.

Ownership is decided per 32-block cell against that cell's OWN bounding sphere, whose centre
`TesselatedChunk` places at the geometry extents midpoint - so a cell holding only a tree canopy
and the cell holding the trunk beneath it can have centres up to `32 * sqrt(2)` apart and fall on
opposite sides of the engine's range test. Vanilla genuinely draws the canopy and genuinely does
not draw the trunk; the mask reads both correctly and the cache fills in the rest.

The only safe correction is to deny a column whose cells disagree, which reinstates most of the
46-block sphere slack the exact rule exists to remove, concentrated on exactly the sparse-geometry
columns that produce trees. `.vhexact off` already demonstrates that trade in one command, at the
cost of the full ring. Do not rebuild it as a new rule without a product decision first.

## Complete columns are unreachable, and two features wait on them

Measured this session and unsolved. Both the whole-mesh skip (`.vhskip`) and the radial handoff
need a column owned from bedrock to sky, and the underground cells cannot be owned: solid stone
with no exposed face holds no mesh, and a chunk nobody has visited was never tesselated, so
neither reports drawn.

- `0 owned draws skipped` in **every log ever recorded**, and `handoff 0 blocks` with `nearest
  incomplete 0 blocks`, so `.vhmask off` is not a working fallback either.
- Counting only the band each cached mesh occupies was proposed and **measured worthless before
  being built**: `.vhskip why` reported 0 of 1248 sections would gain, because the cells the mesh
  occupies were not owned either. Do not rebuild that without re-running it.
- 0.3.159 produced the first fully-owned columns this world has recorded - five of eighty-one - by
  fixing the air/no-sphere threshold. Whether that is enough for an aligned 2x2 block at L0 is
  unmeasured; `.vhskip why` answers it.

## Terrain slabs: optional exact-mode verification debt

Surface-only terrain slabs are implemented, human-accepted, and the 0.3.143 default. The former
sink experiment and default decision are closed. Two non-blocking measurements remain:

- **The half-block cave-boundary branch has no dedicated check.** `CollectSideWithCaveCoverage`
  handles a neighbouring slab covering only the lower half of its top cell, and nothing exercises
  that specific branch. Its non-slab behaviour is regression-covered: `slabfield --half-levels 0`
  produced bit-identical meshes to the baseline over 120 real sections with cave culling on.
- No controlled frame-time benchmark isolates the packed shader early exit. The owner did test the
  complete surface-mode 0.3.142 build in the scene that exposed the regression and reported very
  little performance cost; paired cache geometry supplies the quantitative result.

## The blend band is circular by construction

Not a defect introduced this session, and the owner accepts the band as it stands - recorded so it
is not rediscovered a fourth time. Confirmed from a live `.vhband map`: concentric rings, full
strength to about 190 blocks, fading to nothing at about 310.

Two independent causes were recorded. **The first is closed as of 0.3.159**: ownership is no
longer refused by a radius, so the footprint the band measures from now follows vanilla's own
chunk edges rather than a disc 62 blocks inside them. The band has not been looked at since, and
its inner offset moved with the ring (46 blocks to 16), so its shape and placement both want a
fresh look.

The remaining cause is intrinsic:

- A chamfer distance field's level sets are smooth curves however blocky the seed is, and the
  weights are chosen specifically to stay within 2% of Euclidean.

If it is ever revisited: step the ramp on 32-block chunk edges rather than 8-block cells. The
"seed from vanilla's loaded footprint" half of this note is done.

## Seam blending, block textures and the master toggle: what is open

The base pipeline shipped and was human-tested through 0.3.139. Forest Floor's top-texture fix in
0.3.164, the saved master switch in 0.3.165, and the lifecycle corrections in 0.3.166 are
source/check verified only. The five findings from the 0.3.165 review are complete and recorded in
`dev/history/DONE.md` and Session 72.

### Placed-block recapture invalidation: FIXED, entry removed

The correction this section asked for - return changed/not-changed separately from the edge
mask, and call MarkChanged for every content change including EdgeNone - is implemented in
`LodPipeline.ApplyOneCaptureResult` and carries G138 in its comment. Verified against the
source on 2026-09-04; the section duplicated one the top of this file already marked fixed in
0.3.189. Recorded rather than silently deleted so the next reader does not re-derive it.

### Blend-toggle acceptance still open

- Exercise startup-off, on-to-off, off-to-on, rapid double toggles, a world change with workers busy,
  table-refresh failure, and Forest Floor in game. Run a controlled performance comparison before
  describing the switch's benefit quantitatively.

### Earlier verification debt

- **No benchmark was run.** The band costs one texture fetch inside it plus a bounded field rebuild;
  block textures add a fetch on textured fragments and a mip-level computation. Both are expected to
  be small and neither is measured. A paired route would settle it.
- **Only the packed render path draws block textures.** The legacy path and the non-packed indirect
  path carry no block slots and are unchanged; they have not been exercised against this work.
- **Near-field suppression now defaults to zero.** The artefact it guarded against - coarse cached
  geometry at arm's length in cells vanilla never proves, typically underground - has not been
  re-tested. `.vhnear 48` restores it.
- ~~**The unload-hole fix is a mitigation.**~~ **Answered in Session 70 and closed.** It was
  never about unload timing. The 16-block margin was burying two seam defects - a wall charged to
  the wrong ownership cell (G136) and a crop cube claiming an occlusion vanilla does not provide
  (G137) - and with both fixed the margin defaults to zero. `.vhholes` remains the diagnostic if a
  hole recurs, but a hole that only `.vhband overlap` closes is now evidence of a third cause
  rather than of loss latency.
- **46 blocks resolve no texture and 241 keep per-position colour.** Neither figure has been checked
  against what ground it actually covers.
- **Grass keeps the established olive tone.** The distant colour pipeline blends soil and grass by
  the SIDE texture's coverage, which is long-standing tuned behaviour and was deliberately not
  touched while the texture work was in flight. If grass now reads correct in pattern but brown in
  tone, this is where to look.
- Water, thin cover and cliff sides: water and plants are untextured by design, and side faces use
  the up-face texture rather than a side one.

## Renderer optimisation sweep is closed; one optional subtree A/B remains

Session 61 measured every unfunded candidate before building it. Sixteen-MiB pages saved only two
of 76 observed batches while increasing committed cluster-arena memory from 200 to 368 MiB. The
camera-relative command/record/box stream is 116 bytes per command and peaked at about 239 KiB per
frame, below 100 MiB/s even around 400 FPS. Direct GPU timestamp pairs then measured split-far cull
dispatch at 25 us p95 and p99, with 108 us as the largest interval maximum. Larger pages, a
region-anchored record rewrite, and two-tier cluster culling are therefore rejected on measured
payoff. Narrative and exact route figures are in `dev/history/DONE.md` and Session 61.

The only surviving obligation from this candidate list is a controlled
`VINTAGEHORIZONS_SUBTREE_HEIGHT_CULLING=0` A/B in one settled view. It is optional confirmation of
an already answered feature whose complete theoretical ceiling was about 2% of frame time; do not
mistake it for a funded optimisation.

**Explicitly not recommended, with reasons, so they are not re-proposed:**

- **No third depth stage or previous-frame HZB for far-on-far occlusion.** Split-far already removes
  most far commands, front-to-back order plus early-Z caps the shading cost of what remains, and the
  plan permits only one cached-on-cached strategy. Diminishing returns against real synchronisation
  cost.
- **No verdict hysteresis or temporal damping.** With a conservative mapping restored, a remaining
  knife-edge flip is draw-safe by construction - a hidden thing drawn for a frame, never a visible
  thing missing. Damping would add cross-frame GPU state to suppress something that no longer
  reaches pixels, and would mask the next real conservatism defect.
- **No finer-than-4x4 clusters, no GPU-owned LOD selection, no water batching.** Each is a
  measured-branch decision the plan already gates correctly, and nothing measured so far justifies
  any of them.

## Cached terrain lighting: the sky band is the only untested part left

The four terrain lighting corrections are **done and human-tested** - shipped in 0.3.51, and
the owner reported the result substantially better with nothing else regressed. The
mechanism, the measurements and the disproved theory are in `dev/history/DONE.md` and
session 40; G50 carries the durable rule.

**Owed: one look at the far dissolve band**, the edge where cached terrain fades into sky, at
dusk and at night, flipping `.vhlight sky`. Shipped in 0.3.52 and not yet seen. It is
identical in full daylight, so midday shows nothing. Measured effect: about 20% too dim
through dusk and about 60% too bright on a moonlit night before the correction.

**Open, small, and deliberately not done here:** all five switches default on and none is
persisted. If the owner keeps them on after testing, they can collapse into the shader
unconditionally and the command can go. Nothing depends on that; it is cleanup.

**Sequencing note, now spent.** The Phase 3 fast shader was gated on this lighting being
settled, and it has been written (0.3.53). It is not a port: both paths compile from one
body file and differ only in where the per-section values come from, so a pixel difference
is a wiring bug rather than a transcription slip. The `sky` correction above is the one
part of that body a human has not accepted yet - if it changes, both variants change with
it, which is the point of the arrangement.

## Batched terrain drawing is established; its legacy-occlusion A/B has not been run

Phase 3 of `dev/plans/PLAN_GPU_DRIVEN_TERRAIN_RENDERER.md` is complete in source and, since
0.3.69/0.3.70, has been played. Opaque cached terrain draws from the regional arenas with one
multi-draw per page set instead of one call per section, behind `.vhgpu on` and
`.vhindirect on`, saved per install since 0.3.71.

**Established:** the indirect shader variant compiles on the owner's hardware (RX 9070 XT, GL
4.3 core, GLSL 4.60), the batches draw, and the picture was reported correct across two
sessions. The measured draw-call collapse from session 39 stands: 87-182 opaque submissions
per frame become 8-19 multi-draw batches on the frozen route.

**Still owed:** an honest comparison against the former path. Batching suspends the delayed
occlusion queries, which were worth 170 to 500 FPS on a hill view in session 34, so the useful A/B is
the product-default batching-plus-cluster-culling path against the established per-section path with
occlusion queries - not batching against itself. Nothing has run that comparison; the current
default was accepted from the complete renderer's broader evidence, not from this isolated A/B.

## Phase 7: packed format and regional-memory policy complete

The source path is complete. Greedy opaque rectangles now have an exact three-word record:
four 7-bit X/Z endpoints, two quarter-block 16-bit heights, three face bits, and the unmodified
32-bit RGBA/tint payload. That is **12 bytes instead of 88**, an 86.4% reduction from the accepted
four-vertex/six-index representation. Eight bytes cannot retain those semantics; sixteen adds no
fidelity. Workers write packed records directly beside the expanded arrays, a separate bounded
regional arena publishes them, and `lodterrainpacked` pulls four unique corners per quad while a
shared index pattern emits the same six triangle indices.

`.vhpacked on|off` provides an immediate, session-only visual comparison. The scriptable gate is
`VINTAGEHORIZONS_GPU_PACKED=0|1` or `bench-windows.ps1 -GpuPacked 0|1`; keep arenas and indirect
drawing on in both halves. Version 0.3.105 retains only the selected regional representation. A
missing payload, arena, page, shader, upload, or draw falls back to the established per-section
renderer rather than requiring a second regional copy.

**Source evidence:** the fast tier passes 3,911 assertions. The 782 packed-format assertions cover
all six faces, L0/L1/L3/L6, the 14-bit height limit, every tint/material alpha band, quarter-block
cover, degenerate rectangles, rejection cases and exact decoded equality with the expanded mesh.
Arena replacement/fence lifetime, byte-for-byte upload, padded indirect commands, batching and
static shader structure and the indexed-only backend are separately pinned.

**Primary-driver result from 0.3.85:** visual parity passed, but performance did not. The owner
reported the same scene at about 300 FPS expanded and 260 FPS packed, approximately 0.51 ms or 13%
slower. The first backend decoded six independently generated vertices per quad while the expanded
indexed path shaded four unique corners. That draw-arrays implementation is rejected.

0.3.86 keeps the 12-byte record and uses one reusable `0,1,2,0,2,3` index pattern, rebased by four
virtual corners per packed quad. This restores four decoder invocations per quad without restoring
per-section index storage. The owner's same-view comparison reports exact visual parity and the
exact same FPS with packing off and on. The indexed revision therefore recovered the full 0.3.85
loss. Packing is performance-neutral in this scene rather than a standalone FPS optimization, and
the primary-driver format/draw-topology gate is accepted.

**Final primary-driver memory decision, 2026-08-25:** a controlled ABBA route with clusters pinned
off measured 2.4392 ms expanded and 2.4367 ms packed, a -0.10% delta below repeat variation. The
indexed decoder is therefore timing-neutral on this route. The mirror now retains exactly one
regional form: expanded, whole-packed, or clustered-packed according to the selected path. On the
same route the product-default cluster copy was about 90.33 MiB live against about 833.7 MiB for all
three former copies, removing roughly 89% of live regional duplicate bytes. This is not a claim
about total process memory because legacy meshes remain resident.

The owner retired the second-driver requirement on 2026-08-25. This does not establish portability;
it removes that evidence from the project scope.

### Phase 8 cluster results: the correctness gate is closed

The implementation, preset ladder, rejected margin search, real-command suppression measurements,
failed 0.3.99 sky guard, and the 0.3.100 capture instrumentation are completed history in
`dev/history/DONE.md` and Sessions 47-50.

**The flicker is fixed in 0.3.101 and human-accepted.** The cause was the HZB texel-mapping defect
recorded as G96, not the cluster ranges, the depth margin, the sky silhouette, or a missing texture
barrier. The 0.3.100 two-bucket capture was never run: the defect was found by source tracing and
proven on a deterministic fixture instead. `.vhflicker` remains available and is now most useful for
confirming that the sky guard has gone idle.

Phase 8's implementation and correctness branch is closed. `.vhsplitbias` is gone and the sky
guard was measured, found costly, and removed before 0.4.0. The owner retired a corrected-mapping
suppression/FPS re-measurement on 2026-08-25, so the older figures remain historical and must not be
quoted as current. Metadata/dispatch cost and turning/streaming behavior now belong to the remaining
Phase 9 hardening coverage rather than to a reopened Phase 8 experiment.

Do not repeat the stage ladder or split-bias values, re-add a sky guard, or add a texture barrier
without new evidence. The margin ladder behaved the same at 4/16/64/256 steps because the flip was
between real depth and the exact clear value; the guard inspected outside the rectangle while the
hole lay inside it; and the HZB reduction reads and writes disjoint, explicitly clamped mip levels,
so the same-texel feedback condition for a barrier has never been established.

## Change-locality has one cheap cold/moving verification left

The owner reports that join-time warm-up is smooth after the loading-protocol rebuild, so the old
237-MiB-per-15-seconds burst remains sizing evidence rather than an open player-facing stutter.
Change-locality itself still has one cheap evidence gap: the one-edge-per-change result comes only
from a frozen, warm
profile, which produces no first-time captures. A cold or moving route should show two edges
per change and a smaller saving; `ChangeLocalityChecks` pins both cases, but neither has been
run in game.

**Corrected in the GPU plan too.** `dev/plans/PLAN_GPU_DRIVEN_TERRAIN_RENDERER.md` carried
the same withdrawn inference and has been fixed, with the consequence spelled out there:
arena retirement and reclamation must be sized for a burst during load rather than a
sustained trickle, and settled play does not exercise that path enough to validate it.

## The mask default is settled; what remains is coverage, not the decision

**The band is closed** (0.3.16, human-confirmed 2026-08-19). It was the engine's per-frame
range cull, which no per-chunk signal reflects; see G43 and session 29. **The mask became the
default in 0.3.17** on the owner's decision.

**The frame-rate question is now answered well enough to stop blocking on it** (2026-08-20,
in-game averages on 0.3.23, mask off against on):

| vanilla render distance | mask off | mask on | |
|---|---|---|---|
| 320 | ~315 FPS | ~310 FPS | mask costs ~1.6% |
| 1024 | ~180 FPS | ~190 FPS | mask gains ~5.6% |

The sign flip is the useful part, and it is not "the mask culls more at range" - that would
predict a gain at both distances. With the mask off, cached terrain is suppressed inside a
plain circle; with it on, that circle is pulled in to `MaskNearFloor()` and the per-cell mask
decides instead, so the mask SUBMITS MORE cached geometry and discards it per fragment. That
trade is paid in fragment shading, which is GPU work. At 320 the scene is CPU-bound at
~315 FPS, so only the mask's own overhead is visible; at 1024 it is GPU-bound and the saving
dominates. See G52.

**The default is therefore defensible in the regime that matters:** the distance where the
mask costs anything is a distance nobody runs this mod for. The owner's verdict was that the
difference is negligible either way and the picture is much better, so it stays.

Still owed, but as ordinary coverage rather than a blocker:

- **A controlled benchmark, demoted.** Two samples, one per condition, no alternation, and
  the in-game average may have started before the mask texture finished rebuilding - which
  penalises the "on" figure, so the 1024 result is if anything understated. Run it with
  `-ChunkMask` against a run without; the harness pins the variable in both directions since
  0.3.17, so an unflagged run measures the radial path rather than whatever the sandbox had
  saved. Nothing has been benchmarked since 0.3.9, so the culler rule, the atlas resync, the
  air exclusion, the draw-range clause and the stored exclusion bit still have no number.
- **The visual matrix.** Water, shoreline and cliff were confirmed individually on 0.3.23.
  Boundary flicker, approach popping, cave and structure have never been.
- **Anything that only appears away from this machine:** multiplayer, other drivers, long
  sessions. A default reaches all of them.

Reverting the default remains one line and the saved setting keeps working, but there is no
longer a live reason to expect that to be needed.

### Standing constraints

- Keep air owned in the tracker. The column aggregate feeds the radial handoff and 0.3.3
  proved what happens when it collapses. Exclude air from the mask texel only, and keep the
  exclusion stored per cell so the wholesale rebuild honours it without a chunk lookup (G45).
- Any new ownership rule stays gated on `ChunkMaskEnabled` until it is visually confirmed.
- Do not trust a CPU-side diagnostic to prove a rendering subsystem healthy. Through two
  sessions every one of them reported health while a band of world was missing, because they
  all consumed the same incomplete signal. Make the picture answer.
- Before adding an ownership rule from reasoning, read the engine's IL. Every confirmed
  finding across sessions 28 and 29 came from the decompiled game or from the owner's
  screen; none came from reasoning about the mod.

### Reading the periodic log

- `stale committed found` non-zero once settled means committed ownership is surviving after
  vanilla stopped drawing and the one-second re-confirmation is not reaching those cells.
- `count repairs` should always be zero; any figure is a bug in the ownership aggregate.
- `mask resyncs` should settle near zero. A steady non-zero figure means an incremental
  update path into the atlas is still missing and the once-per-second rebuild is hiding it.
- `denied beyond view distance` counts cells refused ownership because the engine
  range-culls them. A steady non-zero figure while moving is normal and is the trailing
  annulus being released; zero while travelling means the draw-range clause is not running,
  which is how the band returns.
- `drawn-but-empty chunks` is expected to be large and means nothing is wrong: it counts
  sky. Read the corrected G40 first.
- `drawn-without-geometry` and `owned without geometry ... per Y` are dominated by buried
  chunks with no exposed faces, which hold no mesh and are invisible. A blanket rule denying
  those cells ownership would strip it from everything underground and repeat 0.3.3.

## Renderer scaling after delayed occlusion

The accepted current-renderer sequence remains
`dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md`; the staged follow-on is
`dev/plans/PLAN_GPU_DRIVEN_TERRAIN_RENDERER.md`. Phase 0 capability feasibility and Phase 1
source/harness work are complete and recorded in `dev/history/DONE.md`. Open work is:

- Owner-check that the next correctly versioned playable artifact is visually and
  behaviorally indistinguishable with `VINTAGEHORIZONS_GPU_RENDERER=off` and `shadow`,
  including shader reload, a world change, mod deferral and shutdown.
- Let the owner establish FPS baselines/noise. A temporal-occlusion comparison must place
  cached terrain behind vanilla foreground and prove the client is uncapped (G58); the
  aerial Bodanboys pair does neither feature attribution nor Phase 1 acceptance.
- Do not begin a visible fast path until its phase is approved. Phase 2's real regional GL
  shadow buffers also need an explicit bounded dual-residency memory ceiling.

The renderer rejects off-screen quadtree nodes, distance-capped sections, fully
vanilla-owned sections and back-facing opaque triangles; submits opaque sections nearest
first; runs after vanilla terrain; and reuses asynchronous exact-geometry query results to
skip later opaque submissions. Completed rendering narrative lives in `dev/history/DONE.md`.
The accepted owner evidence is:

| change | off | on | frame-time reduction | owner verdict |
|---|---:|---:|---:|---|
| opaque back-face culling | 218 FPS | 260 FPS | about 0.74 ms | no visual difference |
| opaque front-to-back submission | 149 FPS | 173 FPS | about 0.93 ms | no visual difference |
| post-vanilla cached pass | 148 FPS | 179 FPS | about 1.17 ms | minute distant changes; entirely acceptable |
| delayed exact-geometry occlusion, stationary | about 170 FPS | nearly 500 FPS | about 3.88 ms | accepted after motion/seam/edge guards |

All four are default-on in 0.3.37, with `.vhbackface off`, `.vhfront off`, `.vhocclusion off`,
and `.vhtemporal off` as session-only fallbacks. Delayed occlusion defaults to the aggressive
profile; `.vhtemporalprofile safe|aggressive|extreme` exposes the safety and artifact limits.
The first three rows are same-view A/Bs. The temporal row and its roughly 250-350 FPS motion
range are sequential owner playtests across evolving builds and areas, not a controlled
benchmark. The rejected same-frame proxy/query prototype remains rejected: delayed queries
over real geometry after vanilla depth are a different design. Remaining work:

- Run a controlled alternating 0.3.37 `.vhtemporal off/on` comparison in the same settled
  view, plus a second pair during sustained streaming. Quantify the final edge guard rather
  than inferring its cost from the earlier profile ranges.
- Repeat on another driver/machine and cover multiplayer, caves/structures, vertical
  look transitions, long turns, teleports and long sessions. The accepted evidence is one
  owner, machine and world.
- Watch `.vhinfo`: global invalidations should follow profile/view thresholds rather than
  mesh-upload cadence; stale results may occur but must never hide terrain; seam and
  turning-edge protection should rise only in their intended cases.
- Measure remaining CPU draw-submission time before choosing regional combined buffers,
  multi-draw, or instancing. Use the proposed GPU-driven plan's Phase 0 noise floor and
  independent regional/HZB go/no-go gates rather than treating the whole vision as one
  rewrite. Keep visibility independent from residency and persistence.

### Existing near-handoff coverage

The approved rendering design is
`dev/plans/PLAN_CHUNK_AWARE_VANILLA_HANDOFF.md`. Its phases are now built: GPU mask
publication, shader sampling and the CPU whole-mesh skip all exist and, since 0.3.6,
actually run, and since 0.3.16 they do so without the band. What remains is evidence and the
ordinary coverage recorded at the top of this file; the ship/shelve decision is closed.

Measurement still owed:

- Convergence of the readiness-driven handoff at join and after a teleport. The derived
  radius starts small, showing more cached terrain near the camera than the old constant
  did until the tracker converges; duration and visibility are unknown.
- The single-unowned-pocket case: one column near the camera that never becomes ready
  collapses the global radius and restores overlap everywhere. How often that happens in
  play is the evidence for whether the per-cell mask earns its cost at all.
- Teleport and live view-distance change at runtime; both are harness-only today.
- Whether state-agnostic maintenance and the derived handoff cost measurable frame time.
  Repeated alternating runs are required; the existing pair is one run per side inside the
  lap spread.
- Cache-only, vanilla-settled, moving-frontier, large-cache, teleport and
  view-distance-change scenarios, before claiming neutral or improved performance. Nothing
  has been benchmarked since 0.3.9, so the culler-based ownership rule, the once-per-second
  atlas resync and the air exclusion are all unmeasured.
- Independent residency must stay preserved: suppressed fallback should remain warm without
  triggering unload, reload or remesh churn.

Human-reported and still open:

- Approaching terrain fast, cached and vanilla briefly fight where vanilla has just loaded.
  This is ownership gain latency, resolves within a moment, and fails in the safe direction,
  so it is accepted. If it becomes objectionable, confirm cells near the camera on a shorter
  path rather than widening the budget again.
- **Land colour: four causes found and fixed, 0.3.18 to 0.3.20.** The tile-to-tile step is
  human-confirmed fixed ("it looks so much better"); the follow-up green correction in
  0.3.19 has not been seen yet. Original diagnosis follows. Neighbouring cached
  sections rendered as dramatically different flat colours - one green, the one beside it
  brown. `Block.GetColorWithoutTint` answers grass-covered ground with a RANDOM pixel of the
  grass texture, and a palette entry is registered once per section, so each section painted
  its whole surface with its own draw. Measured in the 2026-08-19 cache: 38 stored colours
  for `soil-low-normal` across 1,041 sections, per-channel sd 30-39, while deterministic
  blocks had sd 0. One colour per block id now, averaged and cached, with existing caches
  corrected on load. See G46.

  The same person then reported the agreed-on green itself slightly off, and named the season
  as a suspect, correctly. A seasonal colour map is sixteen shades per point in the year with
  the row picked per block from a position hash, so a field is all sixteen mixed and one
  sample was up to a quarter off in red. 0.3.19 averages the tint over 64 positions. See G47.
  A screenshot on 0.3.19 showed distant grass still substantially greener than vanilla's,
  and the owner guessed the grass and tree colours looked swapped - which was literally true.
  `GetRandomColor` and `GetAverageColor` return opposite channel orders (G48), and grass-
  covered ground is the main thing the engine answers with the former, so grass alone had red
  and blue exchanged. Under that sat a larger fault: vanilla composites untinted dirt with a
  two-thirds-opaque grass overlay and tints only the grass (G49), and the mod tinted
  everything, which removed the olive and nearly all the blue. 0.3.20 fixes both.
  0.3.20 was reported better but still not perfect, and the reason was measurable:
  `GetAverageColor` samples four pixels, and for the see-through grass layer those four also
  decide how much dirt shows through - 0.573 against a true 0.687, a fifth too much dirt.
  0.3.21 reads the whole texture and now reproduces vanilla's blend exactly.
  **What the playtest has to say:** whether distant grass now reads the same as the meadow
  underfoot, and whether sparse/very sparse ground and the non-grass surfaces still look
  right - the change touches every block vanilla draws in the TopSoil pass.

- **Lighting, not colour, was the next thing - and it is resolved.** 0.3.21's albedo was
  accepted as correct, and the owner then found the match held at some times of day and broke
  at others by sweeping the daylight cycle with the season held still. That is the lighting
  half: vanilla never darkens up-facing ground as the sun drops and the mod did. 0.3.22
  adopts vanilla's rule, and the owner confirmed it across the daylight cycle and asked for it
  to stay default. Still unjudged: cached water takes the same floor and was not looked at
  separately, and no one has checked whether flat ground now reads too bright at dawn against
  its own surroundings rather than against vanilla.

- **The seasonal blend weight: FIXED, and the entry that was here is superseded.** Carried as a
  hypothesis from Session 31 and settled in Sessions 74 and 75. The divergence was real and
  larger than the "roughly a third" estimated here - up to 0.85 of weight, with the sign flipping
  around temperature 128, which is why no single global correction had ever worked. The shader now
  computes the weight itself from the climate field, so there is no CPU weight left to disagree
  with. The general lesson outlived the instance and is G144: the engine's CPU colour call is not
  what the engine draws, and it has now differed twice.
- **Water seams are fixed and human-confirmed (0.3.23); one measurement is still owed.**
  See G51 and session 31 for the cause. The 2026-08-20 log reads `974 seam repairs` beside
  `599 meshes` at the 30-second mark - non-zero as designed, and the same join reached its
  100-mesh mark in 6.6 s, so the repairs did not cost visible fill-in time. Whether the count
  SETTLES is still unknown: `Stats after 30s` fires once, so one sample is all an ordinary
  session produces. A second reading needs `VINTAGEHORIZONS_STATS=1`, which prints every 15
  seconds; a figure still climbing after terrain stops arriving would mean a repair is
  re-queueing itself.
- Re-run the visual matrix. The 2026-08-18 playtest was reported acceptable overall, but
  seams, boundary flicker and approach popping were not separately confirmed, and no cliff,
  water, cave or structure case was reported individually. Nothing in the matrix has been
  re-run since the mask began working. Water, shoreline and cliff were each confirmed
  individually on 0.3.23; seams elsewhere, boundary flicker, approach popping, cave and
  structure remain unconfirmed.

### Documentation and baseline

- Repeat the recorded hardware/settings/config benchmark on the eventual release candidate if its code or sandbox state differs materially.

### Renderer scaling

- Human-check clipping and turn-around behavior on the thousands-section build. Automated
  scaling now covers 3,132 persisted sections; the controlled 601-section pair remains the
  causal traversal comparison.
- Complete the owner runtime-equivalence check for Phase 1 using the next correctly
  versioned playable artifact. Keep owner-run FPS/noise evidence separate from source and
  capability evidence. After that gate, seek approval before Phase 2 creates real regional
  GL shadow buffers; no visible fast-path phase is approved.
- Decide whether startup configuration should change from unlimited cached drawing. The
  `.vhconfig` scale and its `Defaults` button now use 32,768 blocks, but that player-facing
  choice is not a controlled far-cap benchmark. The mask's benefit scales with vanilla
  render distance (above), so the two interact: a larger cap makes the mask worth more,
  not less.

### Low-priority coverage and verification debt

- Keep the report of cached terrain becoming coarser during extreme fast flight. The owner
  has played extensively since and does not see it in normal gameplay; those speeds are not
  normally achievable, so this is deliberately demoted rather than removed. If it becomes
  visible again, `.vhcoarse` already separates storage, mesh-worker, scheduling-slot and
  unexplained waits. Read `coarse cover waits` before changing a budget.
- The brief cached/vanilla fight on extremely fast approach remains recorded for the same
  reason. It resolves toward safe overlap, not a hole, and is outside ordinary movement.

## Flagged decisions awaiting human evidence

- **The arenas are gated on capability the indirect path does not use.**
  `LodGpuCapabilityPolicy.Evaluate` only reports `Tier1RuntimeValidated` when the HZB
  requirements - compute shaders, image load/store, a validated depth copy - all pass, and
  the arenas only attach behind that flag. Indirect drawing needs multi-draw and the arenas
  and nothing else, so on a driver with MDI but no compute it would be refused for no
  reason. Not touched here deliberately: that flag also gates the measurement shadow whose
  accepted numbers were taken under it, and everything validates on the owner's machine, so
  changing it now would alter a validated path to fix a case nobody has hit. Revisit when
  Phase 4 splits the gates anyway, or if a second machine refuses.
- Should new/startup configuration remain unlimited, or adopt the `.vhconfig` Defaults
  value of 32,768 blocks after a controlled benchmark and playtest?
- Does visual quality permit more aggressive off-screen GPU eviction without noticeable turn-around stalls?
- Is the hybrid's one-time chunk handoff pop preferable to any residual overlap? The
  "cliff/water seams" half of this question is closed: the seams were the mesher's frontier
  rule, not the handoff, and both cases were confirmed clean on 0.3.23.

## Verification debt

- The readiness tracker has five isolated runs across moving and stationary routes: no probe
  errors, no dropped events, no renderer phase at 25 ms, 18-28 microseconds average frame
  cost, zero steady-state allocation after a single 377 KiB construction, and 232 of 441
  columns fully owned. A recurring multi-millisecond outlier is attributed to a blocking
  `IsChunkRendered` call by inference only. Teleport, live view-distance change, multiplayer,
  and long soaks remain uncovered.
- The readiness-driven handoff held 192 blocks stationary and 160-192 blocks moving against
  a 64-block constant, with zero fallback samples and the applied radius never exceeding the
  measured ownership bound. Frame rate is neutral within noise rather than proven neutral,
  and no CPU or GPU saving exists yet because suppressed fragments are still rasterized.

- Two one-way 1,600-block capture-frontier runs started with zero active VH databases and
  `0 sections from cache`. Both had zero VH ticks at or above 25 ms; capture backlog stayed
  within 20 results / 1.61 MiB / 234 ms and converged during the endpoint cooldown. The
  first run generated server-save terrain and the second reused it, so aggregate FPS is
  not controlled A/B evidence. No person watched the cold route in motion.
- Asynchronous mip propagation now has a 120-second movement/capture convergence soak,
  graceful restart, deliberate interruption after a durable `ApplyToParent` write,
  successful recovery of one persisted obligation, and a third fresh process reporting
  zero obligations. Both dedicated client/server and integrated-singleplayer durability
  are established for the guarded routes.
- The complete game-backed Release tier passes 1,533 assertions, including the Session 24
  handoff/shader coverage, readiness and water-frontier suites, delayed-occlusion state/view/
  seam/edge coverage, and static renderer/query/default wiring. A real game process still
  supplies the only end-to-end proof of callback ordering, renderer cost, and GPU behavior.
- The world-stable color coordinate, removed approach sink, and conservative radial
  handoff have source/build evidence and a packaged playtest. The user found the first
  render-fixes package better but still observed cached/vanilla mixing; the latest radial
  package has no completed human result. The hybrid's readiness model now has source/
  harness evidence and pixel-neutral renderer wiring, but runtime convergence/timing,
  visual seams, GPU cost, and net performance remain open.
- Live server-assist transfer exercises network request state end to end. An integrated
  command-generation run discovered 211 sibling keys, forced one retryable miss, and
  installed that exact key plus 62 others. Natural miss frequency and default-sweep
  sibling-cache behavior remain unmeasured.
- Cached bounds and projection hysteresis pass 30 isolated assertions. The corrected long
  warm-cache route completed with five projection resets and no tick hitches, and human
  review found motion smooth with no noticed clipping. The earlier route screenshots were
  sky-biased; cold terrain/coverage arrival still needs visual review.
- Visibility-aware traversal passes source/harness checks and a controlled same-cache
  601-section route: selected nodes fell 64.2%, average traversal 19.8%, and average draw
  submission 9.3%, while both sides retained 543 meshes with zero evictions. A later
  3,132-section automated route bounded renderer queues and converged, but aggregate FPS
  is not a controlled comparison and human motion review remains.
- Incremental render-dirty scheduling passes 20 focused assertions and a functional
  601-section moving/rotation route that settled every waypoint and converged all guarded
  queues. A later 3,132-section route supplied functional scaling evidence, but there is
  no controlled old/new scheduler comparison or human review of this build.
- Mesh snapshot/upload budgets pass deterministic accounting/progress checks and a
  zero-warning Release build. A 12,800-block cache-growth route processed 94,285
  snapshots/uploads, bounded sampled queues to 18/four items, measured direct GL upload
  below 6.9 ms, recorded no 25 ms renderer phase, and converged. No person watched visual
  replacement behavior and no second driver has been measured.
- Revisioned persistence passes injected failure/retry, repeated mutation, pending
  coalescing, 300-key drain, and newest-row restart checks. A 3,132-section runtime route
  reopened the cache, wrote 138 revisions, and ended with zero unsaved/backlog/errors.
  Persistent-failure timeout reporting has source/check evidence but has not been forced
  inside a game process.
- Capture publication is source-bounded and passed a warm-cache same-route before/after
  run: its maximum fell from 12.038 to 5.732 ms and backlog peaked at 9 results / 0.70 MiB /
  93 ms. One admitted result remains non-preemptible; unseen terrain and interrupted
  shutdown were not exercised.
- A warm-cache join adopted 558 sections with an 11.180 ms worst Vintage Horizons tick
  and no 25 ms hitch. Background-load backlog reached 181 sections / 51.93 MiB / 11.531 s
  old and drained by 30 seconds. This is one client-only warm sample, not a cold/warm A/B
  comparison. Integrated recovery/postcheck processes later loaded 168 sections and
  converged, but they were correctness routes rather than join-performance evidence.
- A pinned dedicated-server sweep examined 3,249 dependency-aware positions, loaded 1,018
  existing columns, skipped 377 frontier columns, generated nothing, and verified 256/256
  sampled absent positions. Server pipeline ticks peaked at 17.874 ms with no 25 ms hitch;
  probe/load issue maxima were 3.945/6.004 ms. The server cache was warm, and
  savegame-sweep cadence remains unprofiled in integrated singleplayer.
- Completed transient generation produced 289/289 columns with zero timeouts or unusable
  height maps and preserved 256/256 sampled absences. This is one radius-8 dedicated-server
  absence-preservation run. A radius-12 integrated run separately generated 414 columns
  with no timeout/height-map failure and fed live sibling adoption, but all absence samples
  were excluded near the player; neither is a long/default-radius soak.
- Saturated assist now has off-thread-reader separation plus correlated setup/publication/
  admission, send, allocation, and GC-crossing evidence. The reproduced progress-log tail
  is fixed, but the old 32.450 ms sample lacks its raw correlated log and cannot be
  relabelled conclusively. All accepted runs cover one machine and one player at the
  elevated 64/s stress rate; default-rate, multiplayer, long-soak, and human review remain
  open.
- Two warmed steady-stationary on/off pairs measured about 0.7% lower average FPS and
  1.0% lower median FPS with allocation telemetry at roughly 445 uncapped FPS. Their 1%
  lows reversed direction, and ordinary capped-frame-rate overhead remains unmeasured.
- GPU bottleneck attribution remains unmeasured; CPU/render-thread findings must not be presented as proof that the shader or GPU is innocent.
