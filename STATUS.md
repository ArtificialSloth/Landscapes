# Vintage Horizons - status

> Tier 2: current state and verification evidence. Historical measurements and development
> narrative live in the sessions; durable design lives in `dev/ARCHITECTURE.md`.

**Status date:** `2026-09-29`
**Mod version:** `0.4.135` - current private test build: coarse stand-ins cover only the quarter that is waiting instead of their whole area, chunks the game has just announced are rechecked every frame until their mesh is up, new ground skips disk reads for placeholder ancestors, `.vhstandin on|off` switches the first two for an in-session comparison, and a temporary recorder logs a `stand-in check` line each minute. Verified, copy-installed and checkpointed; **not yet played**. Previous build 0.4.134 (the opt-in all-cores garbage collector, decision 54) is owner-played: the owner called the Server GC "much smoother"
**Last accepted baseline:** `0.4.64` at commit `dd0394c`, accepted by the owner on 2026-09-12
**Active branch:** `codex/water-depth-light`; recent checkpoints 0.4.121 `a5f528d`, 0.4.122 `d46b776`, 0.4.123 `b46cf0a`, 0.4.124 `1fb10f5`, 0.4.125 `92eb9c3`, 0.4.126 `e668f81`, 0.4.127 `89509e9`, 0.4.128 `6e1d718`, 0.4.129 `5e4d72c`, 0.4.130 `5fe56a8`, 0.4.131 `bceed3e`, 0.4.132 `92e47ac`, 0.4.133 `f2e47e9`, 0.4.134 `ad6b02b`, 0.4.135 `9951c05`; nothing pushed
**Target:** Vintage Story 1.22.5+, .NET 10
**Source files:** `147` C# files under `VintageHorizons/src`
**Assist protocol:** `1`
**Blob format:** `4`
**Database schema:** `6` (column fingerprints carry their own rule version, now `2`)

## Current result

**Coarse terrain briefly drawn over the game's own ground: a fix is built and waits for the owner's
exploring test (0.4.135).** Session 112.

- **Report:** a screenshot from 0.4.129 while exploring showed rough coarse LOD terrain over the
  game's terrain for a moment; rare, but jarring. That session's log had rotated out.
- **Cause (source-traced):** a section standing in for a child without a mesh drew its whole
  footprint, 1,024 blocks across at L4 and 2,048 at L5, so one new piece of ground at the loaded edge
  put coarse columns around the player (G269). New ground's placeholder ancestors also paid a store
  miss and an empty background read per level before the first mesh (G271). And a chunk the engine
  announces is not yet tessellated, so its first ownership probe failed and it waited for the
  interior sweep, about 0.6-2 s by arithmetic, while both renderers drew it (G270). Not a regression.
- **Measured read-only on his cache:** L1-L5 tops never rise into a chunk that is empty in the real
  world (0 of 28,573 chunk columns); L6 does in 1.0%; chunk-edge walls rise into a neighbour's air
  cell on 0.3-1.0% of chunk faces, which the air rule never hides (G272).
- **0.4.135:** stand-ins cover only the waiting quarters while the clustered path draws (packed
  clusters for opaque ground, a per-section uniform for water, thin cover, depth and OIT); announced
  chunks are rechecked at the front of every frame, up to 64 a frame for up to 10 s, until the probe
  hands them over or the engine's upload state says no mesh is coming; placeholders skip the store.
  The ownership rules are unchanged. `.vhstandin on|off` switches the first two, session only.
- **Held (owner):** letting air above ground the game draws hide cached terrain, which would close
  the chunk-edge walls; it is a terrain-ownership change.
- **Evidence:** harness-tested; 26,156 assertions, whose 14 failures are the environmental
  all-cores collector check. Not run in game. The recorder is temporary and goes once his log
  confirms the fix.

**The stutter while exploring is solved for the owner by the all-cores garbage collector
(0.4.128-0.4.134).** Sessions 110-111.

- **Cause:** the slowest frames are .NET gen1 collections, 10-20 ms each. With the default
  (workstation) collector, a small collection runs after every 48 MB on the owner's CPU, 450-750 a
  minute while exploring new terrain, and briefly-living objects (the game's chunk meshes waiting to
  upload) survive them into gen1. While gen1 collections outnumber 1% of frames they set the 1% low
  (G263).
- **Chunk thread (0.4.132, decision 53):** with one generation thread on 8+ processors the engine
  re-scanned parked neighbour requests up to 168,000 times a second, building a closure per request
  (G261). The rest sleeps it after an idle pass only where the engine would spin. Headless: the
  thread's garbage 713-816 MB/s to 9-35, its CPU 0.97 to 0.19 cores, pregen 100.7 s against vanilla's
  101.2. In play total garbage fell (803 to 339-644 MB/s) but the owner felt no difference.
- **All-cores collector (0.4.133 launcher, 0.4.134 setting, decision 54):** with the Server GC
  (adaptive heap count), collections fell to 25-38 a minute, time frozen from 2.5-5.2 s to 0.26-0.40 s
  a minute, the longest freeze from 35-40 to 22-23 ms and the 1% low to 92-138 FPS. The owner:
  "Felt MUCH smoother." It costs about 0.3-0.5 GB (whole game 9.3-9.5 GB against 8.8-9.2), and the
  first minute after joining has 56-139 ms collections (G267). The Performance tab's switch, off by
  default, edits `Vintagestory.runtimeconfig.json` with a backup and applies from the next start.
- **Recorder:** `.vhhitch` names the collector at start, writes its minute report in the background,
  and each minute gives the longest freeze in one frame, frames held 10 ms or more, memory, exact
  allocation (G264), allocation by thread and type, and this mod's jobs. The chunk thread rest adds
  idle passes and the thread's exact allocation to the server-work line.
- **Also:** this mod's own garbage was cut in 0.4.131 (seam-fog rebuilds from about 100 MB/s to 1-2);
  claim re-reads work during the sweep (0.4.128, G257); terrain-change acceptance tests 2/3/16 and 5
  passed in the sandbox; A4 is dropped.
- **Evidence:** owner-played and human-tested for feel; the rest headless-integration-tested; 25,967
  assertions.

**Section sync is built and passed a dedicated sandbox test (0.4.126-0.4.127).** Session 109; the
plan's Phase B, previously unstarted.

- **The defect it fixes:** a player's game asked a server only for sections it did not hold, and the
  live refresh reached only players online at the time. A section changed while someone was away was
  never offered to them again, and their cached copy stayed stale for good.
- **Server:** every server-cache row carries a version, stamped once per checkpoint and committed with
  the counter. A generation names the cache's lineage and is re-minted on a format purge or a
  version-less write by an older build, which a trigger catches. The server answers a join with
  every level-0 section written since the client's recorded version, then broadcasts every 30
  seconds. Joins and broadcasts share one FIFO, which keeps each player's spans contiguous.
- **Client:** it records the generation and version it caught up to, plus the held sections still
  owed, written with each span's version in one transaction. It fetches owed sections nearest
  first, 32 at a time, inside the serving radius. A debt settles only when a copy at least as new
  as announced is merged and durable. A merge frees its fetch slot at once (G256). Integrated play
  does not take part.
- **No record** (first join after the update, or a rebuilt cache) re-checks every held section,
  newest winning: the owner's choice on 2026-09-22, over keeping what the client has. It is
  resumable, so it happens once.
- **Measured** on a copy of the owner's 1.85 GB server cache: the in-place migration takes 3.6 ms
  for the column and 62 ms for the index. A full re-check list reads in 9.5 ms and a live broadcast
  in 0.02 ms, from a covering index. The first index shape was ignored by the planner (G255).

| Sandbox join (dedicated, default serving limits) | Result |
|---|---|
| 1, 0.4.126: first join, fresh caches | Server's live saves arrived as spans; 71 sections merged; catch-up measured at ~1 section/s (G256) |
| 2, 0.4.127: 10 held sections rewritten while away | Exactly 16 changed sections announced; 18 owed merged and saved within ~30 s |
| 3, 0.4.127: nothing changed by the test | Exactly the 19 rows of the server's final save announced; caught up in 31 s |
| 4, 0.4.127: a version-less write, as an older build makes | Generation re-minted; all 59 held sections re-checked in 31 s |

- **Not established:** a real second player, the owner's own play, and the large-world re-check
  time. About 15,000 held sections at 8/s is an estimate of roughly 30 minutes and 700 MB per player
  on a bodanboys-sized world.

**Farseer's seam fog is in a state the owner is happy with; exploring shows hitches of unknown
cause.** Sessions 107-108.

- 0.4.123 redesigned the fog as a natural bank by column optical depth, and the owner called its
  shape fantastic. 0.4.124 split its density, built its outline from every in-reach tile rather
  than the view (G248), and marched it at half resolution. 0.4.123 cost 400 against 550 FPS in one
  scene, about 0.68 ms per frame at 1440p.
- He then found the wall too dense to see Farseer through, though it had to stay dense to hide the
  seam, and saw its fog vanish in one frame whenever the seam moved. Offline, 0.4.124 on his 60/60
  dials showed 1.7% of the terrain behind it from ground level: a bank normalized by its column is
  a wall from the side (G251). And replacing the field removed the old fog outright (G254).
- 0.4.125 made three changes:
  - a seam strip about 3 blocks deep hides the seam on its own; it rests on the surface each ray
    lands on, and steps cannot jump it (G252-G253);
  - the bank is quoted along a level crossing, so Farseer shows through it;
  - a worker-built residual of the previous field dissolves over Fog fade time, 0.8 s by default.
- Offline: the seam shows at most 1% from five viewpoints and 6% straight down (0.4.124 at 60/60:
  12%), and about half of the terrain behind the fog shows from ground level. On a 10-block ramp the
  strip leaked up to 91% before it was anchored to the landing surface.
- His 0.4.125 session compiled all three fog programs, reset the densities once and logged no fog
  failure. He said "Everything looked good", and his tuning then became the defaults.
- He also reported hitches while exploring new terrain, with the minimum FPS dropping low
  periodically. The cause is unknown (the fog or Farseer, the savegame sweep changes, or something
  else); it is noted in TODO for future performance work and has not been measured.

**The startup sweep's payoff is demonstrated in play, on the owner's own world.** Session 105,
production-observed 2026-09-20 from his `test2` logs and measured offline from the files afterwards.

| `test2` start | columns read | skipped as already captured | loaded | sweep duration |
|---|---:|---:|---:|---:|
| 0.4.118, first adoption | 17,219 in 1.2 s | 0 | 10,439 | ~25 min |
| 0.4.118, restart | 17,296 in 1.6 s | 10,040 | 399 | 37 s |
| 0.4.119, restart | 17,296 in 1.1 s | 10,172 | 267 | 27 s |

- Both restarts verified 256/256 sampled absent positions still absent, and generated nothing.
- Capture errors and unloaded-before-capture drops were zero across all three.
- After a clean quit on 0.4.119, **35 of 10,423 claims no longer matched the savegame - 0.34%**,
  against 399 of 10,439 (3.8%) for the equivalent measurement one build earlier. The stale ones
  cluster where the player stood: 16% of claims within three chunks of him, 4% within seven, 0.28%
  of the 8,190 beyond forty.

**The fingerprint now covers the landscape rather than the whole stored row (0.4.118).** Session
105; the measurement that motivated it was taken offline on his own savegames, read-only.

- Over 25 days of his play and generation, 13,754 of 62,390 columns moved their fingerprint -
  22.05% of the world, and **43.5% of the 31,583 columns inside his 128-chunk sweep square**, which
  is where all of the change is.
- Of those changed columns: 81.87% changed blocks, fluids or decors; 12.08% changed only non-chisel
  block entities; 5.98% changed only entities, light or mod data; 0.07% changed chiselled contents.
  The block-entity churn is berry bushes, mycelium, transients, tree hollows, locust nests, beehives.
- The hash now keeps blocks, fluids, chiselled-block contents, the layer count and the structural
  markers, and drops light, light saturation, entities and their count, the block-entity count, mod
  data, server mod data, light positions, decors and the placed/removed counters. **Decors are
  excluded by the owner's decision** of 2026-09-20: ground gaining decor is being worked on for
  other reasons anyway.
- The rule is an exclusion list, so a field a future game version adds still counts as a change.
  Chisel classes come from the game's own block registry, so a mod's own microblock subclass is
  covered; hard-coding the two vanilla names would have dropped it silently.
- Verified with the shipping code against the same two real savegames: **13,754 flagged columns
  become 11,307**, matching an independent offline prediction exactly, with **no column flagged that
  the whole-row hash did not already flag**. Without the chisel filter it would be 12,967. About 45%
  of the bytes are no longer hashed; other mods' chunk data (Watersheds groundwater and flow
  vectors) was 84% of the bytes in a sample of his rows.
- 17.8% is a **lower bound** on capture-irrelevant churn: any change to the packed block bytes was
  counted as relevant, and some of those cannot alter a distant section.

**A claim used to be wrong the moment it was made; 0.4.119 fixes that.** Session 105.

- The sweep fingerprints the savegame once at world start, then loads a column. Loading a dormant
  column is itself what changes it - the engine catches up transient blocks, snow and block entities
  on the way in - and the capture reads the corrected column, so the cache is right. The claim
  carried the pre-load reading, so the next start loaded the column again for nothing.
- Measured at 7.0% of claims on 0.4.116 (162 of 2,323, his own world) and 3.8% on 0.4.118 (399 of
  10,439). In the sandbox, where the whole sweep square sits inside the player's load radius, 26.3%.
- Three signals agreed on the mechanism: claims made in the sweep's first 90 s were stale at 10.6%
  against 5.2% for later ones; columns the sweep had loaded changed at 33.6% against 0.019%
  elsewhere; and 48 of 162 disputed columns had had their cached section rewritten after the claim,
  against 2.2% of agreeing claims (G240).
- 0.4.119 separates taking a durable capture from claiming it. The world-save event schedules a
  re-read a few seconds later - `GameWorldSave` fires *before* the engine writes and there is no
  "finished" event - and the claim is taken from what the file then holds. A column with a capture
  queued, or no longer durable, is left settling.
- Fallbacks: no read path claims from the startup reading as before; a session ending with columns
  still settling claims them the same way, because losing a save interval's proven captures would be
  worse than a stale claim; and re-read batches unanswered three times say so once and turn the
  refresh off for the session.
- **Under load (Session 110):** a save during a long load pass turned claim refresh off until
  0.4.128 (G257); since then a sandbox sweep read back 5,459 of 5,459 claims after saves. Whether a disputed claim's recapture is *wasted* is measured for 48 of 162 columns and
  argued from `ChunkDirty` semantics for the rest; it is not proven.

**The background audit and the dormant-terrain snow refresh report themselves for the first time
(0.4.119).** Both begin when the startup sweep finishes, and the server printed a status line only
while a job was unfinished, so on 2026-09-20 both ran nine minutes on his world with the log silent
about either - G237's shape a second time (G241).

| Arm, `test2` 0.4.119, about 12 minutes | Result |
|---|---|
| Background audit | 10,591 columns re-read at 16/s, **81 no longer matched** and were queued for recapture, one full pass over 10,431 claims |
| Audit dispute rate | 0.76% of re-reads, against 0.71% in the sandbox |
| Dormant-terrain snow refresh | 550 recaptures, 81 regional campaigns, 23 dormant-region probes over 12,551 candidates, **12,104 still queued at 1/s** |
| Claims | 273 recorded, 273 read back after a world save, 0 from the startup reading, 0 waiting |

- The snow-refresh backlog is the debt of six weeks unreachable behind an uncleared flag. About
  three hours of background priority loads on that world; **no FPS or frame-time run has yet been
  taken with that trickle running.**
- The mod's own arms are a floor on claim staleness: each dormant column they wake is updated by the
  engine and written at the next save, which disputes that column's claim (G243).
- Claim re-reads queue behind the audit's 16/s stream: claims for the 20:54 save were not written
  until 20:56:39. Not a correctness fault, but a session ending in that window falls back.

**Terrain change detection: phase A is built and played (0.4.113-0.4.119).** Sessions 103-105.
Phase B is section sync, at the top of this section.

- **The probe pass is gone.** The sweep reads the savegame itself through a private read-only
  connection on its own thread, rather than asking the engine 70,225 times at ~320/s through the
  chunk thread that also loads player terrain. On the owner's `test2` the whole index reads in
  1.1-1.6 s; on his 3.22 GB `bodanboys` it measured 73.3 s cold. The index covers the whole world
  rather than the probe square, so edge columns have better neighbour knowledge than before.
- **The read path detects the journal mode and never sets it**, paces reads in short batches with
  yields (relaxed under WAL), waits out `SQLITE_BUSY`, validates the schema rather than guessing at
  an unexpected one, and disables itself for the session with one named line on any failure, after
  which the sweep probes exactly as it did before. Its checks prove the connection cannot write.
- **A column is skipped only when the savegame's content fingerprint and the cache's proven one are
  both known and equal.** Either unknown means "assume changed". A column earns its claim only once
  its capture reached the durable row, never when its savegame row was read (G232) - and since
  0.4.119, only from a re-read taken after the world has saved (G240).
- **Claims live in the `ColumnFingerprint` table** created with `CREATE TABLE IF NOT EXISTS`, and
  carry a rule version in `Meta`. No schema bump: an existing cache gains the table empty, a missing
  entry reads as "assume changed", and a rule change drops the claims with a line naming how many.
  A cache-format purge now drops them alongside the sections they describe.
- **The background audit** re-reads a rotating slice against those claims and queues a mismatch for
  recapture under the sweep's own safety and scheduling rules. It disputes only claims this cache
  makes. `AuditColumnsPerSecond` defaults to 16 and 0 disables it with no residual cost. Its
  production numbers are in the current result above.
- **Dropped: A4's clean-shutdown fast path** (owner, 2026-09-26). There is no hook after the
  engine's final save (G236), so its marker cannot record the file's post-save state, and A3 cut the
  scan it would skip to 73.3 s of off-thread reading. A4's other half - re-fingerprinting after the
  engine writes - is what 0.4.119 built, in the shape the plan's own 2026-09-19 correction describes.

**The owner played 0.4.116 on `test2` and called it good.** Session 104. First play verdict on
phase A, superseded for sweep numbers by the 0.4.118/0.4.119 sessions above.

- The read path opened on a **WAL** world - change counter 100 - and read all **15,112 columns in
  9.9 s**, with no probe progress lines in the log at all. A3 works on his machine.
- He reported consistent minor FPS dips while exploring new terrain. Attribution: mostly vanilla,
  plus the ~4% of the 1% low the frontier A/B already measured for the sweep. His session was the
  worst case - the sweep was doing first-adoption work throughout, capturing 3,986 sections with its
  capture backlog at 31-40 against a limit of 32 - and that cost is one-time per world.
- The sweep did not finish in that ~10 minute session. It has since finished twice on that world.
- One unloaded-before-capture drop, no capture errors, no client-side errors. The 0.4.118 and
  0.4.119 sessions had none at all.

**A status number read as an alarm and was not.** Session 104; 0.4.117 fixes the diagnostic.

- His log read `52 dirty, oldest dirty 307.6s, 7.38 saves/s`. Those cannot describe one queue.
- A section keeps its first-dirty timestamp while it continues to change, and `CompleteSave` clears
  dirty state only when the completed revision is still the newest. During a sweep the coarse parent
  sections are re-dirtied continuously by mip propagation, so one ages without bound while the
  writes keep up (G238).
- **No storage-pressure brake was added**, reversing plan §11.3. Braking on that age would have
  throttled the sweep permanently for a benign reason; the dirty count never approached its limit;
  and the save queue is already bounded at `MaxStorageBacklog` = 256. This was a diagnostic defect.
- The line now reports the oldest as *changing* for that long and prints the storage queue depth.
  The sweep's own status logs once a minute without `VINTAGEHORIZONS_STATS`; the phase and
  allocation dumps stay development-only.

**Dormant-terrain snow refresh has never run, and now does.** Found 2026-09-20 while building the
audit into the same code path.

- `verifying` was set when the absence verifier started and never cleared. The sweep's tick handler
  returns immediately while it is set, so **every tick after the startup sweep completed has done
  nothing since `0596d30` (2026-08-02)**, including in the accepted 0.4.64 baseline. Automatic snow
  refresh, its canary and its regional campaigns have therefore never executed once.
- Nothing reported it: the status line reads configured state — candidate counts, rates, campaign
  totals — rather than whether any work happened, so it always printed as healthy (G237).
- This is the likely reason winter convergence has sat in the debt list unresolved.
- Reviving it switches on a path that issued engine priority loads with none of the brakes added
  since, so the refresh arm now takes the capture-backlog brake, the engine-generation gate and the
  player-activity hold with its rate floor. Sandbox-observed executing for the first time;
  convergence is **not** demonstrated.

**Farseer: P1-P4 and most P5 built (0.4.77–0.4.125).** Sessions 92–100 and 106–108.
- **Availability and switches.** The saved client choice defaults on. Integrated/LAN servers offer
  silhouettes automatically unless `EnableSilhouettes = false`; dedicated servers default off and
  require `EnableSilhouettes = true`. Their requested reach is clamped by
  `SilhouetteReachBlocks` (8,192 by default). `.vhsilhouette` is a session-only override.
- **Height sources, best first:** captured terrain, the engine's Terrain-pass preview, then an
  accepted optional accelerator. Watersheds is preferred when both supported samplers are present;
  Algernon's Terrain Sampler is accepted alone; neither is required. The accelerator supplies a
  64-block coarse grid off the main thread, and one engine preview refines likely low/coastal
  ground. A named failure disables it for the session and previews take over.
- **Builder and store.** Work is nearest-first through a reach-plus-one-tile disc, yields to every
  shared automatic-generation gate, and stops preview work on generator silence or worldgen errors.
  Tiles persist in `<savegame>-silhouette-server.db` (its own schema 1) and merge better origins.
- **Transport.** Current peers exchange bounded revision manifests and nearest-first tile requests;
  16 may be in flight. Every accepted request ends in success, explicit refusal or bounded retry.
  World-session identity rejects stale packets, and tiles/offers outside the moving reach are
  pruned. The old direct-push form remains for 0.4.77-0.4.85 peers.
- **Client and rendering.** Worker meshes exist at 16/8/4 quads per 512-block tile and are selected
  by projected size. Frustum/back-face culling remain. At a data frontier, only the Farseer edge
  slopes down through an adjustable buried apron; the distant field keeps its representative
  height. Every installed L0-L6 LOD mesh carries exact 32-block coverage derived from the opaque
  top surfaces it actually emitted. A camera-position-only target/parent/descendant selector and
  stable vanilla readiness suppress Farseer only where replacement can draw; unknown coverage
  leaves the fallback present. World-directed slope shading and gentle depth shading separate
  overlapping ridges. The pass follows cached opaque terrain through the existing far-plane policy
  and shared classic colour.
- **Seam fog.** It is built from every Farseer tile within reach, whatever the camera faces.
  - **Outline.** The exact directed replacement frontier is traced into maximal paths and smoothed
    by a fixed-window moving average.
  - **Field.** A bounded worker-built field stores signed lateral distance, seam-ground height,
    conservative skip distance and open-end fade. The worker also measures how far the staircase
    strays from the centreline.
  - **Medium.** A seam strip a few blocks deep hides the seam: its column is 3.5 optical depth over
    the measured stray plus a small margin, it rests on the surface each ray lands on, and march
    steps cannot jump it. Above and around it the bank is scenery, quoted along a level crossing so
    it stays see-through: beyond its core it fades exponentially, wider over Farseer, and erodes
    into wind-stretched strands with a faint haze. Sunlight shades both below the horizon fog
    colour.
  - **Dissolve.** Each build also carries the previous field onto the new lattice as a residual
    whose weight wears down over Fog fade time; the thicker of old and new shows, and new fog
    appears at once.
  - **Draw.** A half-resolution depth-aware raymarch with a checkerboard depth choice feeds a
    depth-aware upsample. The noise comes from a baked mipmapped 3D texture, and billow drift is 6
    blocks per second at speed one.
  - **Composition.** The veil draws after water/OIT, sampling primary depth read-only and
    preserving primary alpha/depth.
- **Persistence, scheduling and settings.** A lazy client store reads nearest saved tiles for
  instant rejoin and closes when off. Decode/mesh work is capped off-thread; owner publication,
  coverage, pruning, remesh and upload are bounded. The **Farseer** tab persists enable, reach,
  Outline detail, Hill softness, apron width/depth, classic-look dials, the Seam fog switch and twelve
  fog dials: Seam strip density and height, Fog density, coverage, spread, height, floor depth,
  billow size, drift speed, Fog fade time, Fog quality and edge softness. `.vhfog` toggles the fog for the session. Cached draw and reach
  remain scale handles.
  Performance turns it off;
  Balanced/Quality turn it on. Off sends one final demand and clears network, RAM, workers, GPU
  meshes and projection extent. The shader is compiled only after a drawable tile arrives.
- **Not built:** optional P5 island/inlet refinement, complete player-facing Farseer rename/license
  bundling, P6 Terrain Ahead, and P7 canopy.

| Silhouette evidence | Result | Level |
|---|---|---|
| Owner test 0.4.77, singleplayer | Silhouettes drew; they "don't connect well with immediately adjacent terrain" | Human-tested |
| 0.4.77 exit hang | The 16:04 `bodanboys` launch, not the 16:11 quit. Previews stopped being answered after 99 coast-map errors, the server never suspended, and the owner killed the game (G201) | Log-measured (Session 93) |
| Owner test 0.4.80, `bodanboys` | The stall guard fired. The world-generation thread was silent about 3.5 min after 144 coast-map errors; three suspend timeouts named `chunkdbthread`; leaving took about 2 min and completed (G204) | Log-measured |
| Store after that test | 96 tiles written at exit (61 KB) | Read offline, read-only |
| Region rebuilt per preview | Identical dungeon placements repeated 4× per region on `bodanboys` and `test2`; `test2` did not stall (G203) | Log-measured; source-traced |
| Worldgen heights on previews (P0.1) | 0 of 1,024 zero on the first preview; forest map present (Watersheds and TerraPrety, 1.22.7) | Log-measured |
| P0.3 comparison, 0.4.83, two fresh worlds (1,680 and 4,096 chunk corners) | Median error 1 and 2 blocks, 95% within 5, worst 21 and 11; lean +1.5 and +1.9; land/sea disagreement 11.6% and 5.9%, almost all at the waterline; 0.01–0.02 ms per sample; both accelerators identical. The worst points were most likely shallow water and low land, not the sea floor first recorded (G208) | Integration-tested on isolated dedicated servers (Session 94) |
| P0.3, 0.4.84, `p03-cold` (270,336 cells, every 4 blocks) | Median error 1, 95% within 4, max 63; lean +1.4; land/sea disagreement 3.7%; 0.16% of cells 8+ blocks off, almost all on land or at the coast. Answers taken before the land existed matched later ones at every cell, and a new region's first sample took under 1 ms. Sea level 82 | Integration-tested over two server sessions (Session 95) |
| Accelerators with Watersheds installed | Algernon's sampler answers Watersheds' height function. Both include river valleys, neither applies the channel carve, and both return the top solid block, as the stored map does | Source-traced (decompiled) |
| Owner's accelerator decision | Accuracy accepted; use Watersheds when both supported samplers exist and Algernon alone otherwise | Human decision; implementation source/harness-tested |
| Client persistence | Leaving and rejoining immediately restored the prior Farseer terrain | Human-tested; cold process/full-disc restart still open |
| 0.4.93 geometry reduction | Owner saw no visual difference after the 16/8/4 reduction and called the performance-oriented removal successful | Human-tested visually; no controlled final timing |
| Early performance observation | About 550→450 FPS at 8 km before the final simplification | Informal owner observation, not a controlled A/B |
| 0.4.94 join | Visible bands and an over-sunken far field; rejected. The owner clarified that only the Farseer edge should slope down near real terrain | Human-observed; removed/reworked in 0.4.95-0.4.97 |
| 0.4.98 suppression | Substantially more stable than 0.4.99, but broad suppression could precede replacement readiness | Human comparison; superseded |
| 0.4.99 readiness handoff | Farseer shifted as the camera turned and a gap reopened beside LOD terrain because ownership came from current-frame/frustum selection | Human-observed; source-traced; superseded |
| 0.4.100 stable handoff | Camera reaction fixed, but broad Farseer geometry remained over visible LOD because only L0 resident meshes were recognized | Human-observed in screenshots; source-traced; superseded |
| 0.4.102 surface fog | Exact boundary location worked, but the visible result was bright, flat and angular with chunk-shaped outlines | Human-observed in screenshots; design rejected |
| 0.4.103 volumetric fog | The layered mesh occupied air, but cards stayed visible and broad water became a near-black void; the client logged repeated OpenGL `InvalidOperation` | Human-observed and production-observed; superseded |
| 0.4.104–0.4.105 guarded draw | Shader compiled, then the first fog draw still failed. The single-attachment blend-helper error was fixed first; the next log proved a second error remained | Production-observed; source-traced; superseded |
| 0.4.106 typed-uniform fix | Corrected integer upload to a declared float uniform. The owner reported that the fog looked “very good” | Human-tested; source/static regression-tested |
| 0.4.107 post-water fog and radial corners | Fog composites after OIT and water stays visible; the owner called the result excellent. Screenshots still showed a rounded/missing diagonal quadrant at turns | Human-tested; corner limitation human-observed |
| 0.4.109 swept ends | The owner called the fog's ends excellent, then reported sharp corners everywhere. The square swept end protrudes at every turn of a staircase frontier | Human-tested; the sawtooth is human-observed and reproduced offline |
| 0.4.110 rounded ends and rim wobble | Plan-view rasterization of the real mesh over a staircase seam: the regular sawtooth is gone and the outline is irregular and continuous. Peak plan density falls 2.71 to 1.50 as the spikes go, so the core still saturates and only the rim thins | Measured offline from the real mesh |
| **Owner acceptance of the fog, 2026-09-19** | Played 0.4.111 and accepted the fog work. The itemised sub-observations the gate asked for — Fog drift speed at 0% and 300%, and the aerial staircase/ninety-degree views — were not separately reported, so the acceptance is a whole-result verdict rather than per-dial evidence | Human-tested |
| Client log for that session | `lodsilhouette` and `lodsilhouettefogvolume` both compiled at 16:13:27; zero `InvalidOperation`, zero GL errors, Farseer never disabled, clean exit at 16:18:36. The client store held 1,097 tiles from earlier sessions and the client asked for a 4,096-block reach | Production-observed (read offline) |
| 0.4.110 drift rate | The previous 0.006 noise-units/second offset is 0.6 blocks/s at a 100-block billow size, and was time multiplied by the live dial, so only a dial change moved it. Drift is now integrated and applied as 6 blocks/s at speed one | Source-traced and static-checked; not human-tested |
| 0.4.120 rounded path | The owner reported the ribbon shape better, but two distant screenshots showed hard internal gradation lines from card intersections | Human-tested; superseded by 0.4.121 |
| 0.4.121 continuous field/raymarch | The owner reported the fog itself much better and the old internal lines absent; one screenshot showed the bright stepped ownership handoff still readable, and trying every available dial could not make it dense enough | Human-tested; core failure human-observed |
| 0.4.122 normalized core | The owner found it too bright, too dense and unnatural (`2026-09-21_09-44-39.png`). Offline, its seal was 10-30 times the bank's density and its column reached optical depth 9-20: an unlit ribbon with a 6-8-block edge (G246) | Human-observed; reproduced offline; superseded |
| 0.4.123 natural bank | Shape "fantastic"; at 35% density the seam showed from above (`16-50-02`), at 60% it was covered (`16-51-46`); the ribbon deformed with the camera; 400 FPS with fog against 550 without in one scene | Human-tested |
| 0.4.124 split density, terrain-built outline, half-resolution march | All three fog programs compiled and loaded, the one-time density notice appeared, and no fog failure was logged in his session; he said "getting better" and later saved seam 60% / outer 60% | Production-observed; human verdict general |
| Offline mock-up of 0.4.124 defaults | At most 5% worst-pixel seam show-through from above, at a distance, at ground level, from the Farseer side and along the seam; 6.5% average and 12.5% worst straight down at 60% seam density | Measured offline (`scripts/fog-mockup`), not in game |
| 0.4.124's bank from the side | On his 60/60 dials, 1.7% of the terrain behind the fog showed from ground level in the mock-up; the owner found it too dense to see Farseer through and saw its fog vanish as the seam moved | Human-observed; measured offline |
| 0.4.125 strip, see-through bank, dissolve | His session compiled all three fog programs and applied the one-time density reset; no fog failure; clean exit after 30 minutes. "Everything looked good" | Production-observed; human verdict general |
| Offline mock-up of 0.4.125, his old shape dials | Seam at most 1% worst from five viewpoints, 1.8% average and 6.0% worst straight down; terrain beyond the fog 48% from ground level, 67% along the seam, 80% from the Farseer side (0.4.124: 1.7%, 9.7%, 38%). On a 10-block ramp, 1% worst except 5.9% straight down, against up to 91% with the strip on the sampled height | Measured offline (`scripts/fog-mockup`), not in game |
| Exploring hitches on 0.4.125 | Hitches with the minimum FPS dropping low periodically while exploring new terrain; cause unknown | Human-observed; not measured |
| Final checks | 25,443 assertions pass; Release mod build has zero warnings/errors | Harness-/build-tested at 0.4.125 and after the defaults change |
| 0.4.125 artifact | 708,897 bytes, 45 root-relative entries, one DLL, all four fog shaders present and matching source, no PDB/wrapper, matching installed copy; SHA-256 `03BC8AE3A662549B9A7A0A7F207DCCBD6FFF0A17B040B4F5D75C889392E8B1C4` | Artifact-verified |
| Farseer's exploring cost | 2 lattice probes and 2 previews in eight minutes, standing aside on about 73% of its ticks, about 0.3 tiles/s, its 4,096-block disc already exhausted with 1,086 stored tiles. The savegame sweep was loading 7-14 columns/s in the same window; 0.4.111 now gates new sweep loads on player terrain activity | Original production log plus controlled Session 102 follow-up |
| Rapid handoff/excavation, weather look, controlled GPU/off A/B, cold/full-disc read-back, dedicated players and mixed versions | Not completed | — |

**The core world-generation/LOD-coverage plan is complete.**
- P1–P5 and P7 landed in 0.4.66–0.4.76.
- P0 proved the engine's multi-thread pipeline was really active, but rejected it for negligible
  speedup and scheduling-dependent terrain. P6 is therefore closed without exposing thread
  controls.
- P8's documentation is Session 91. The remaining runtime matrix is explicit verification debt, not
  a gate for the silhouette plan.

`/vhworldgen` is the deliberate, confirmed exception which writes real terrain to the savegame.
- **Tiles.** It works in persistence-friendly 16×16-chunk tiles. The default circle begins at the
  player's tile, moves through clockwise circular radial bands, and visits nearest columns first.
- **Gates.** Engine-queue, capture, durability, load and disk gates bound the job.
- **Resume.** A durable bitmap supports pause, resume, stop and an offered manual restart resume.
- **Settings.** The Generate page shares the Settings editor. If normal integrated Settings has
  paused the server, it hands off to standalone `.vhconfig` live controls.
- **Fixes in 0.4.74–0.4.76:** the L0 2×2-chunk progress map, transient-cache skip provenance,
  force-loaded release, action/status races, and boundaries waiting on unrelated global dirtiness.

| Generate evidence | Result | Level |
|---|---|---|
| Core owner test | After earlier start and pause/resume failures were fixed, the owner reported “Success” following stop/reopen/fresh-start recovery | Human-tested; full completion/restart matrix still open |
| Full stack, radius 30 | One-thread controls: 448.45 s / 444.81 s; six threads: 437.73 s, 8.50 columns/s versus 8.30–8.37 | Controlled runtime benchmark |
| Minimal stack, radius 15 | One-thread controls: 99.38 s / 99.25 s; six threads: 99.87 s; two threads: 111.46 s | Controlled runtime benchmark |
| Thread activation | Six-thread runs named `worldgen1`–`worldgen5`; two-thread run named `worldgen1`; files held the requested `MaxWorldgenThreads`; no `--reducedthreads` | Config-, launch- and runtime-verified |
| Terrain identity | Full-stack six-thread: 4,050 rows differ where controls agree (3,215 block payloads); minimal six-thread: 1,055/892; minimal two-thread: 408/288 | Offline semantic save comparison; P0 failed |

**P1-P3 remain complete, and distant terrain fills in.**
- **0.4.66** fixed GPU depth-copy validation under render scaling.
- **0.4.67** made server cache saving oldest-first (Session 89).
- **0.4.68** makes the client take the server half's fuller rows for sections it already holds.
  - The sibling cache is scanned by rowid at every level.
  - A first sighting and every parent fill only the columns the client lacks.
  - A level-0 row the server rewrote after the client saw it wins column by column (the owner's
    choice). It never erases columns only the client holds, or replaces observed snow with a
    generated peek.
  - Merges are recorded in the additive `SiblingMerge` table once durable.
- **0.4.69** paces the savegame sweep to the capture queue; **0.4.111** also yields its startup
  priority loads for five seconds after player join/movement and at the first visible engine
  generation request.
- **0.4.70** retires the `.vhsnowline` comparison, fixing the current snow-line design in place.

| Evidence | Result | Level |
|---|---|---|
| Caches before the fix | 645 of 2,912 client rows behind the server, 1,471,451 columns lacking (L4 95/100, L5 34/36, L6 9/9) | Measured offline (`siblingfield`) |
| 0.4.68 in play | "The vast majority of terrain is filled in now"; by 23:31, 2,207 sections merged, 1,468,006 columns filled, 10,231 replaced by newer saves | Human-tested; log-measured |
| Remaining thin lines | 2,461 chunk columns at vanilla pass 5 of 6 in one-chunk lines, 80 finished but never captured, none absent (23:35) | Measured offline, read-only against live files |
| Scanner (D4) | Old full read 828 ms CPU; key read 15.6 ms; incremental about 0.1 ms; 0.056% of one thread over 130 s offline, 0.04-0.11% in play | Measured offline; log-measured |
| Sweep drops | 4,043 unloaded-before-capture drops in the first 0.4.68 session; paced in 0.4.69 | Log-measured; harness-tested only |

The accepted 0.4.64 Settings result and all earlier rendering results remain in place.

## Distant structures and chiselled blocks

Ruins drew near-white at distance while looking right up close. Three things combined:
- **No colour of its own.** A chiselled block, a clutter piece or a cluttered bookshelf stores its
  look in its block entity, which only the side holding the loaded chunk can read.
- **Coloured by block type.** The server half has no texture atlas and stores colour 0, and the
  client coloured such sections by block type. That type's only texture is the game's
  missing-texture image (G181).
- **The guard never fired.** The check meant to catch that image compared an alpha-free atlas value
  exactly against an opaque repack (G180).
- **The white stuck.** A section's palette keeps its first colour per block id, so the client's own
  later capture reused the white entry (G182).

- **Stand-ins (settled decision 36).** Where a capture job is created, the mod copies each chiselled
  block's cuboids and materials.
  - The capture worker records the block as the eligible material with the most exposed voxel
    faces, summed per block; ties go to the first material.
  - Eligible means something distant terrain draws as ground: not snow, liquid, markers, flowers or
    a block-entity block.
  - The chisel's own flags still decide the column's snow.
  - Server and client store the same ordinary block, so its colour, tint and band texture come from
    the existing paths.
- **Placeholder fallbacks.** The missing-texture colour is recognised on RGB against both the atlas
  average and the file mean.
  - A block-entity block whose answer is the placeholder takes another of its own textures, or
    neutral grey. A real entity answer is kept.
  - A plain block whose own answer is the placeholder keeps it, because the game draws a removed
    mod's block the same way up close.
  - A block with no colour texture stays grey.
- **Existing caches.** Stored placeholder colours on block-entity blocks are repaired to that
  fallback on load and written back. Their real materials arrive when the ground is captured again.

| Evidence | Result | Level |
|---|---|---|
| Cache, 0.4.61 | 2,078 structure columns of 10+ chiselled, clutter or bookshelf blocks stored near-white against 62 real; the whites are exactly the missing-texture average | Measured offline |
| Engine, 1.22.7 | A chiselled block's type-level colour is its placeholder texture; its contents are cuboids plus material ids | Source-traced (decompiled) |
| Checks | 51 assertions: engine encoding parity, exposed faces, most-visible against majority-by-volume, placeholder forms, load repair, capture over proxy chunks | Harness-tested |
| Owner's first 30 s on 0.4.62 | 4,496 chiselled blocks captured as their material, 28 with no material that can stand in, 0 failed reads; 1,105 stored placeholder colours repaired | Log-measured (client captures only) |
| 0.4.62 in play | "Excellent! Looks fantastic." | Human-tested |

Limits:
- Clutter, bookshelves, ground storage and lamps have no single material, so they take one of their
  own textures' colours or grey.
- Structures cached before 0.4.62 stay grey until recaptured.
- The server half's stand-in counts are not in the client log.
- The cost of copying chisel contents where capture jobs are created is not measured.

## Distant anti-aliasing

The shimmer was sub-pixel aliasing. Past about a thousand blocks, one rendered pixel covers several
blocks but shows only the one under its centre. So a camera movement smaller than a pixel swaps a
white snow top for the dark wall beside it. The fix nudges distant cached terrain by a different
fraction of a pixel every frame. It then blends each pixel with where the same ground was in
earlier frames, so the pixel settles on the average of what it covers.

- **Ownership.** A depth snapshot taken before the first cached draw marks the pixels cached terrain
  drew. Only those, and sky pixels beside them, are nudged, blended and written back. Vanilla
  terrain, creatures and water keep their pixels (settled decision 34).
- **Strength** follows on-screen block size against the reach.
  - There is none where a block is the reach or wider, and full strength where it is half as wide.
  - History reaches its full share over the first half of that fade, and the nudge comes in over
    the second.
- **Resolve:**
  - Catmull-Rom history re-sampling;
  - a safety check against the mean +/- clip standard deviations of the 3x3 neighbourhood, inside
    its range;
  - history only from cached terrain or sky;
  - the still memory only once the whole camera has held still, eased in over 16 frames;
  - fast turns keep 85% of the turning memory.
- **Composite.** It writes back only the pixels the resolve owns, with contrast-adaptive sharpening
  that never reaches the history.
- **Failure.** A shader compile failure or an early GL error stops the feature for the session,
  with one log line, and terrain draws as it did without it. No GLSL validator is installed here,
  so the shaders are only ever compiled in game.

| Setting | Command | Default (owner's tuning) | Range | Before 0.4.61 |
|---|---|---|---|---|
| Reach | `.vhtaa reach` | 2.5 px | 1-32 | 2 |
| Nudge | `.vhtaa nudge` | 0.5 | 0-1 | 1 |
| Sharpen | `.vhtaa sharpen` | 1 | 0-1 | 0.5 |
| Memory while turning | `.vhtaa blend` | 0.97 | 0-0.97 | 0.9 |
| Memory when still | `.vhtaa still` | 0.99 | 0-0.99 | 0.97 |
| Smear guard | `.vhtaa clip` | 2.25 | 0.5-4 | 1.25 |

- **Saved and on sliders.** All the dials are saved and have sliders on the `.vhconfig`
  Anti-Aliasing tab. `.vhtaa reset` restores the defaults.
- **Ranges kept.** The owner kept these ranges on 2026-09-12, although sharpen and both memories
  sit at their tops.
- **Reporting.** `.vhtaa` with no argument reports, in chat and the client log: the state, the reach
  in blocks, the dials, the share of frames with the camera fully still, history resets and GPU
  cost.
- **Showing.** `.vhtaa show` tints what it touches.

| Evidence | Result | Level |
|---|---|---|
| Capture, 0.4.54 | One block is one pixel at 935 blocks (60 degrees, 1,080 rendered rows); past 512 blocks 79-89% of snow pixels were one row tall | Measured offline |
| FOV 60 to 20 | Shimmer greatly diminished at nearly unchanged depth precision | Human-tested |
| 0.4.57 | Cleaner, but vibrated with a still camera; 17.9-23.1% of anti-aliased pixels failed the per-pixel depth test (G175) | Human-tested; capture-measured |
| 0.4.58 | Steady when still; smeared, even standing still | Human-tested |
| 0.4.59-0.4.60 | No vibrating spots; tuned "to look very good"; both tabs and all sliders work | Human-tested |
| Cost | Resolve and composite p95 100 us, depth snapshot p95 25 us, over about 464,000 frames at 1920x1080 rendered on an RX 9070 XT | Log-measured, owner's session |
| Stillness | 63-67% of resolved frames had the camera fully still | Log-measured, owner's session |
| 0.4.61 | Defaults equal the owner's saved values; the packaged DLL's constants verified by decompiling; the defaults have been in play since 2026-09-12 | Harness- and artifact-verified; in play |

## Automatic LOD thresholds

With **Automatic** on (the default), each level hands over at the same on-screen size (settled
decision 35).
- **Where levels start.** L1 is where a level-0 column reaches 1.5 px on screen
  (`.vhdetail target`), and each later level doubles it.
- **Level 0** is held past the vanilla view distance, the blend band and one section.
- **At the owner's settings** (60 degrees, 1,080 rendered rows, view distance 128, band 128) that
  gives 624 / 1,248 / 2,496 / 4,992 / 9,984 / 19,968 blocks. His own list stays in the file for
  `.vhdetail manual`.
- **Re-selection.** It re-selects at most once a second, only when the render height, FOV, floor or
  target changes, and logs what it applies.
- **Evidence.** In use since 0.4.57 without complaint; not evaluated at other FOVs, render scales or
  view distances.

## Settings integration and editor

In an active world, the vanilla Settings header has a dedicated **Horizons** tab.
- **The seam.** Vintage Story 1.22 exposes no public page registry. One guarded adapter therefore
  extends the private `GuiCompositeSettings.ComposerHeader` seam and the established Settings leave
  paths.
- **Guarding.** It validates every reflected member before patching. If the seam moves, vanilla
  Settings remains usable and the log directs the player to `.vhconfig`.
- **One editor.** Both entries compose one stateful editor (settled decision 37), so behaviour
  cannot drift between two implementations.

- **The pages.** **General** holds automatic/manual LOD distances, the far draw cap, silhouette
  reach, the saved **Load terrain while paused** switch, and visual features with presets.
  **Anti-Aliasing** holds every TAA dial; **Farseer** holds its saved switch, reach, status,
  Outline detail, Hill softness and classic-look dials; **Generate** holds permanent
  world-generation controls; **Performance** holds the opt-in **All-Cores Garbage Collection**
  switch (0.4.134, decision 54), which applies from the next start and says what the current
  session runs. The tab strip is 760 px for five tabs.
  - Descriptions appear on hover.
  - Secondary tabs sit below the vanilla header.
  - Compact rows and independently placed actions keep text and controls inside the page.
  - Defaults resets the current page. Save keeps mod changes; Cancel or closing restores live
    anti-aliasing and silhouette previews.
- **Distance map.** A logarithmic map begins at 32 blocks.
  - Vanilla's range is neutral grey. The coloured and labelled L0-L6 spans begin at its edge, and
    each L1-L6 marker frame matches the region it introduces.
  - Cached draw distance and Farseer reach are handles; the Farseer band runs between them, levels
    beyond cached draw distance dim, and the Farseer parts hide when the feature is off. L1-L6 put
    their numeric distance on a second line and use the vanilla tab typography.
  - This picture is explanatory, and does not remove the renderer's internal seam overlap.
- **Vanilla render distance.** The white Vanilla marker is a second control for
  `ClientSettings.ViewDistance`, not mod configuration.
  - It uses the game's exact 32-1,536 range, 32-block increments, mouse-up commit and
    Graphics-Custom side effect, then refreshes automatic thresholds immediately.
  - Mod Cancel deliberately does not undo it.
- **Paused loading.** Render stages continue when ordinary single-player game ticks stop.
  - When the saved default-on switch is enabled, the renderer invokes the same bounded owner-thread
    LOD work at 50 ms cadence. It neither unpauses nor advances simulation.
  - Source and harness verification are complete; a direct runtime progress observation remains
    open.
- **Presets.** Quality equals the deliberate default visual set and leaves costly Far Biome Colors
  off. Performance disables silhouettes; Balanced and Quality enable them. Players may change a
  feature individually, producing a Custom configuration.

The owner's first 0.4.63 screenshots exposed the remaining tab, colour and Vanilla/L0 separation
defects. He reviewed the corrected 0.4.64 window and said, "This looked good." Session 97 then
iterated the Farseer scale from screenshots; the final 0.4.91 typography/reach result still needs a
focused acceptance look.

## Tree and glacier corrections: accepted

- **0.4.54** gave a block's top, X walls and Z walls their own atlas rows, so standing logs show
  bark on their sides.
- **0.4.55** makes wall lookup skip the already-selected top texture, so Redwood quarter-trunks show
  bark too. A lying-log control preserves a real wall-facing cut end.
- **0.4.55 also** limits liquid classification to Water and Lava. Every `EnumBlockMaterial.Ice`
  block, including the opaque packed glacier ice of mountain summits, had been drawn as water. It
  was also written to the liquid depth that drives underwater murk, painting a dark rim round
  glacier summits (G173).

The owner confirmed all three on 2026-09-12:
- Redwood bark on all four sides, with a cut-end top;
- no dark rim on the captured snowy peak with murk on;
- frozen lake and glacier ice reading right as solid terrain.

## Handoff flash result retained

The one-frame flash of open sky at the edge of vanilla's draw range came from deciding range
ownership at Before/0.994, before the player's physics step at Before/1.0 (G170). The lag dates
from 0.4.13.
- **The fix.** 0.4.53's loss-only late range check at Before/1.001 fixed it, and the owner
  confirmed.
- **Comparing.** `.vhlaterange off` restores the old timing for comparison.
- **Records.** [Session 84](dev/sessions/SESSION_84.md) holds the evidence, and settled decision 33
  owns the rule.
- **Not supported.** The owner's earlier build bisection to 0.4.40 is not supported by the code, and
  why 0.4.39 was judged clean is not established (G172).

## Winter data and update paths

| Component | Current behavior | Evidence / limit |
|---|---|---|
| Spatial fallback | The 128×128 climate field carries raw climate and a quantized snow threshold at 64-block spacing; one existing GPU texture sample serves tint and snow | Source and executable checks; owner confirmed global repaint regression no longer describes the result |
| Actual snow | Loaded columns authoritatively record observed/bare-or-covered state; generated peeks stay unknown | Source/check coverage and owner comparisons after travelling; full block/mod survey remains open |
| Coarse LOD | Parent snow requires all contributing captured children observed, then uses areal majority with snow on ties | Deterministic mip checks; mixed real-world boundary matrix remains open |
| Geometry | Snow layers are retained for compatibility but emit no geometry; existing top faces carry the snow material marker | Mesher/packed checks and owner direction |
| Frost | Frostable grass/foliage use local climate, altitude, season temperature and engine value noise, composed over seasonal tint | Shader/static checks and positive owner appearance report |
| Loaded updates | Vanilla `ChunkDirty` queues exact recapture | Source/check evidence; movement-driven updates mostly matched vanilla |
| Dormant updates | Every 30 seconds, vanilla regional snow snapshots may start a finite accumulation/melt campaign; one dormant region is woken as a canary per poll | Built and bounded; a complete real-time accumulation and melt were not clearly observed |
| Manual update | `/vhrefresh [radius]`, default 32 chunks/max 128, recaptures only proven-existing safe columns; `status` and `stop` are available | Source/check evidence; human command matrix remains open |
| Live delivery | Durable changed L0 rows reach clients: integrated play through the sibling cache's rowids; dedicated play through versioned spans since 0.4.126, at join for changes made while away and every 30 s while online (older clients still read them as refresh manifests). A held section merges them column by column, the newer copy winning, never erasing client-only columns (0.4.68); clients rebuild parents | Source/check evidence; integrated merge human-tested in 0.4.68; dedicated delivery sandbox-integration-tested in Session 109; no second-player run |

Snow accumulation and melt are rare transitions, so the scheduler does no column work between
relevant weather changes.
- During a finite campaign, `RefreshColumnsPerSecond` defaults to 1 and clamps to 0–16; 0 disables
  automatic refresh.
- Work stays inside the sweep's generated-column map and shares a 1 ms owning-thread issue budget.

## Storage, wire, and replacement semantics

**Section blob format 4** has two optional append-only 512-byte maps before compression: observed
snow provenance and covered state.
- Old readers ignore the tail.
- Current readers treat a missing tail as unknown and reject partial tails.
- They downgrade provenance-only 0.4.43 rows to unknown, rather than falsely asserting bare terrain.
- Derived snow palette flags use bits 32 and 64 and are reclassified from live blocks.

No cache purge or schema-meaning change is required.

**Protocol 1** has grown only by append-compatible additions:
- optional `AssistServerSettings` field 16, for the refresh rate;
- `AssistKeyManifest` field 4, which distinguishes newer durable contents from a newly discovered
  key;
- in 0.4.74–0.4.75: `AssistWelcome.Features` field 7 and `CanControlWorldGeneration` field 8,
  server-settings fields 17–23, the `AssistWorldGenRequest`/`AssistWorldGenStatus` message types,
  and status `Action` field 28;
- in 0.4.77: four silhouette message types, initially used as a direct-push prototype;
- in 0.4.85: `AssistServerSettings` field 24 reports accelerator use;
- in 0.4.86: settings fields 25-26 report the dedicated reach and preview rate; append-only
  transport-generation and world-session fields activate the registered manifest/request path
  between current peers while retaining direct push for an older peer;
- in 0.4.126: section sync. `AssistHello` fields 3-5 carry the client's recorded generation and
  version, `AssistWelcome` field 9 the cache generation, `AssistKeyManifest` fields 5-7 a versioned
  span's bounds and per-key versions, and `AssistSection` field 4 the served row's version. The
  refresh flag stays set on spans for older clients.

Older protobuf peers ignore the new fields. Feature bits reserve 0 for Generate, 1 for frontier and
2 for silhouettes.

**Refreshed rows.** Since 0.4.68 a refreshed row is merged into the client's copy column by column
instead of replacing it. It wins only while the client's content revision is unchanged since the row
was handed to the decoder; otherwise it only fills. Only L0 is transferred, and parents rebuild
locally. The compatibility numbers remain `1 / 4 / 6`, and `dev/WIRE_HISTORY.md` records the
append-compatible extensions.

**Chiselled positions.** Since 0.4.62 a chiselled position stores its stand-in's block code, for
example planks where the world holds a chisel. Every reader already resolves an ordinary block code,
so neither the blob format nor the schema meaning moved; `dev/WIRE_HISTORY.md` carries the note.

**Additive storage since then:**
- Sessions 84-89 changed nothing on the wire or in the blob format; Sessions 86 and 88 added client
  configuration keys only.
- Session 90 added the client table `SiblingMerge`, which records which server-cache rows a stored
  copy already contains. It is additive.
- Session 91's worldgen state is a separate mod-data plan, not a section-schema meaning.
- Session 93 added the silhouette tile store, a separate server file with its own schema `1`.
  - It is created on a world's first silhouette request.
  - A save merges into the stored row.
  - It shares nothing with the section cache.
- Session 94 changed no stored or transmitted meaning. Features bit 2 now marks a server that
  offers silhouettes, not every server holding a builder (`dev/WIRE_HISTORY.md`).
- Sessions 95-98 changed no section blob or section database meaning. Silhouette transport
  generation 1 is append-compatible protocol 1; the tile payload and tile-store schema remain 1.
- Session 109 added section versions without a schema-meaning bump:
  - Server caches gain `Section.Version` in place, the covering index `SectionVersions`, the
    `SectionLegacyWrite` trigger and `Meta` keys for the generation and version.
  - Client caches gain the `AssistOwed` table and `Meta` keys for the caught-up generation and
    version.
  - A format purge clears both halves' records.

The numbers stay `1 / 4 / 6`.

## Diagnostics

Session-only unless stated; client commands write their findings to the client log, server commands
to the server log.

- `.vhtaa` reports distant anti-aliasing (above); `.vhtaa show` tints what it touches.
- `.vhhitch` (on by default) writes a report a minute to the client log (Current result above); it
  logs the collector in force when it starts. `/vhchunkrest on|off` (controlserver) switches the
  chunk thread rest for the session and logs who did; each server logs the rest's session totals on
  shutdown.
- Each client start logs "All-cores garbage collection: ..." with the setting, whether this session
  runs it, and the state of the game's start-up file.
- `.vhstandin on|off` (0.4.135, on by default, not saved) switches quarter-limited stand-ins and the
  fast hand-over of fresh chunks together and logs the change; with no argument it reports the
  state. **Temporary, until the fix is confirmed:** each minute with anything to report, a
  `stand-in check` line gives the stand-ins that covered ground the game draws (by level, longest,
  nearest, why the finer piece was missing) and the fresh-chunk hand-over wait (half, 95%, slowest,
  and any over 30 s); a stand-in over the game's ground lasting 0.5 s or more gets a `stand-in:` line
  with its position, at most 20 a minute. Both are written in the background and measure the old
  delay with the switch off too. `.vhcoarse` names the quarters each stand-in covers.
- `scripts/vs-collector-test.cmd` starts the game normally, with a 128 MB gen0 budget, or with the
  Server GC, changing nothing in the install. `scripts/worldgen-thread-gate.ps1 -ChunkThreadRest
  On|Off|Absent` compares the rest headless.
- The 30-second report's `chiselled blocks:` line (0.4.62) counts:
  - chiselled blocks captured as their material;
  - those with no material that can stand in;
  - failed reads;
  - stored placeholder colours repaired on load.
- `.vhdiag on|off` (0.4.56) shows or hides the diagnostics side panel for the session; it is off by
  default. The panel lists every `.vhtaa` dial, saved and live.
- `.vhsky on|off` counts sky below the horizon after all opaque terrain is drawn, and logs each flash
  with the captured mask. `.vhsky verify on|off` reads the mask texture back after every upload.
- `.vhlaterange on|off` switches the 0.4.53 fix; on by default.
- `.vhgap on|off` and `.vhgap release on|off` log, and optionally release, cells the mask gives to
  vanilla while vanilla is not drawing them. The scan still runs before the physics step.
- `.vhwater capture` stores a frame's colour and depth stages with the projection. Session 86
  measured the shimmer from one.
- `.vhsilhouette on|off|log|stats` switches silhouettes for the session. It reports tiles, cells
  by source, offered/in-flight/refused transport, pending and resident meshes, per-resolution draws
  and the shader. It does not rewrite the saved Settings choice.
- `/vhsilhouetteserver [status]` (controlserver) reports the builder, heights, previews, the store
  and any stop, plus accelerator and delivery counters.
- `/vhsilhouetteserver compare [radiusChunks]` compares every cell the server holds in a square
  around the caller (the world centre from the console), and writes a map for the side-by-side.
- `/vhsilhouetteserver presample [radiusChunks]` asks the accelerators about that square before it
  is generated.
- Each of those two logs "Accelerator comparison finished." or "Accelerator presample finished."
  after its last report (0.4.84).
- `VINTAGEHORIZONS_SILHOUETTE_MEASURE=1` (developer-only, dedicated servers) keeps silhouette
  heights so the comparison can run there, offering players nothing. The status line then starts
  "measuring heights only".
- `scripts/silhouette-compare.ps1` runs presample and compare in two isolated dedicated server
  sessions with the owner's enabled mods. `scripts/silhouette-compare-render.py` then draws the
  side-by-side. `-PlanOnly` lists the mods and launches nothing.
- The builder logs a status line every 30 s while a player asks for silhouettes.
- `ModConfig/vintagehorizons-server.json` owns `EnableSilhouettes`,
  `SilhouetteUseAccelerators`, `SilhouetteReachBlocks` and `SilhouettePreviewsPerSecond`. On a
  dedicated server only explicit `EnableSilhouettes = true` offers the feature.

## Performance and artifact evidence

**Checks and builds.** At 0.4.135 the fast tier ran **26,156 assertions with 14 failures, all
environmental**: `GameCollectorChecks` drives the editor against a copy of the installed game's
start-up file and asserts that it asks for the default collector, and the owner's file has the
all-cores collector switched on (TODO). Every new and affected suite passed, including
`StandInChecks`, the watch list in `VanillaReadinessChecks` (zero steady-state allocation included),
quarter selection in `PackedClusterChecks` and the placeholder shortcut in `ResidencyChecks`. At
0.4.134, before his switch, the tier passed 25,967 assertions with zero failures. The Release mod
build completed with zero warnings and zero errors.

**Chunk thread rest, headless (Session 111).** One run each, vanilla plus this mod, 961 columns:
patch absent 101.2 s, 1.25 server cores; installed but off 80.9 s, 1.30 cores, 75,734-168,385 idle
passes/s, 713-816 MB/s on the chunk thread; on 100.7 s, 0.57 cores, 15-18 idle passes/s, 9-35 MB/s.
The installed-off run's speed is unexplained.

Measured costs, and what is not measured:
- **Sibling-cache scanner.** Its cost is in the current-result table. Merging into held sections
  runs under the existing install budget and is not separately measured.
- **Distant anti-aliasing.** Its measured cost is in its table above.
- **Automatic thresholds** cost nothing per frame unless their inputs change.
- **Pause-time loading** runs the already-bounded owner-thread work at 50 ms cadence, but has no
  paired frame-time capture.
- **Chisel stand-ins** add a copy of each chiselled block's contents where a capture job is created,
  plus arithmetic on the capture worker. Neither is measured.
- **The late range check (0.4.53)** sweeps only on frames where the physics step changed the
  player's block.
- **Snow** adds 1,024 uncompressed bits-array bytes per resident section before compression. No
  paired large-world capture yet isolates winter campaign cost.
- **Savegame sweep.** At the default/current 128-chunk radius it repeats on every enabled world
  start: 70,225 existence probes, then a 66,049-position candidate scan at up to 16 safe priority
  loads/s. Its 50 ms cadence, 1 ms cooperative issue budget and 32-column capture brake bound
  submissions and protect capture correctness. The controlled load-phase A/B measured no average
  FPS difference but a 19.9% worse 1% low, 24.8% worse frame-time tail and 16.4% higher RSS with the
  original sweep. Version 0.4.111 pauses new loads for five seconds after any player joins or
  crosses a chunk boundary and at the first visible engine generation request, while preserving the
  capped no-catch-up allowance. Its 109.0 FPS / 9.17 ms tail result matched the sweep-off control's
  110.3 / 9.07 within run variation. The route was mostly saved terrain but added 178 map columns
  at its endpoint; a sustained virgin-frontier comparison remains open. See
  [the formal analysis §18](dev/plans/ANALYSIS_SAVEGAME_SWEEP_SCHEDULING.md).
- **Farseer:**
  - GPU cost and the off-state A/B are not measured.
  - Captured heights cost one 2 KB copy per column the server loads.
  - Accelerator calls, network decode, fill and three-resolution mesh builds run off the main
    thread. Decode and mesh work are capped at two jobs each; owner-thread publication snapshots at
    most one mesh job per pump.
  - A tile stores 672 base triangles across 16/8/4 meshes and draws one. The edge apron changes
    those existing vertices and adds no separate visible band.
  - Every installed LOD mesh carries a 32-block ground mask built during its existing worker mesh
    pass. It ranges from 2×2 cells at L0 to at most 128×128 cells / 2 KiB at L6. One visible
    Farseer tile performs about 89 bounded resident-map lookups plus bit tests; it does not scan
    geometry, add a render pass or read back the GPU. Live cost is unmeasured.
  - Slope contrast reconstructs the geometric normal with shader derivatives and adds one gentle
    depth term. It adds no terrain geometry; live shader/GPU cost is unmeasured.
  - Seam fog is two post-OIT full-screen triangles: a half-resolution march of at most 192 steps
    per pixel, then a full-resolution depth-aware upsample. Outline changes snapshot the paths on
    the owner thread; one worker builds the distance field, converts it to half floats and builds
    the residual; only the newest revision uploads, the residual as a second texture of the same
    size. While a residual fades, the march evaluates the old field's shape again wherever it still
    has weight. The field covers the whole reach, eight blocks per texel at a
    4,096-block reach (about 1,200 texels square, about 11 MB per upload), with a 2,048x2,048 cap.
    Off-screen tile coverage refreshes 8 tiles per frame.
  - Measured: 0.4.123 cost 400 against 550 FPS in one scene at 1440p (about 0.68 ms per frame), by
    the owner's `.vhfog` switch. 0.4.124-0.4.125 are unmeasured. On 0.4.125 the owner reported
    hitches with low minimum FPS while exploring new terrain; whether the fog's rebuilds and
    residual, Farseer, the savegame sweep or something else causes them is unknown. The owner
    judges fog cost with `.vhfog`, not a timing harness.
  - Render upload is bounded to four tiles, 2 MiB and 2 ms per frame; live GPU time is unmeasured.
  - An accelerator height took 0.01–0.02 ms in freshly generated land (Session 94). On land nobody
    had generated it took under 0.01 ms, and under 1 ms for a new region's first sample (Session 95).
    Sampling beside real generation is not measured.

**The 0.4.127 artifact.** The verified `dist/vintagehorizons_0.4.127.zip` is 725,515 bytes, 45
entries, SHA-256 `B3B4A01AE824A563B6393E597A373E91A4944D9D7A7203D3465253DC21AE8CAF`. Its DLL matches
Release and contains the merge-frees-the-slot fix. It has no PDB or wrapper directory, reports
0.4.127, and was copy-installed as an identical file. 0.4.126's artifact is 725,445 bytes, SHA-256
`3484247FC240B01C4675CA01CB5C1893B82309A347A2448F631A6BD6B922831D`.

**The 0.4.125 artifact.** The verified `dist/vintagehorizons_0.4.125.zip` is 708,897 bytes and has
SHA-256 `03BC8AE3A662549B9A7A0A7F207DCCBD6FFF0A17B040B4F5D75C889392E8B1C4`. Its 45 root-relative file
entries are readable, its DLL matches Release and carries the new dial strings, `modinfo.json`
reports 0.4.125, all four fog shaders are present and match source, it contains no PDB or wrapper
directory, and it was copy-installed as an identical file. 0.4.124's artifact is 701,843 bytes,
SHA-256 `8A15E534A49CDA2DCAD07C1A628B4EBA97C2A8823B206E2A165348A505862C7C`. Every older test build
remains in the Mods folder as a rollback artifact.

**Checkpoints.** Every incremented private test build receives an exact Git checkpoint after
verification and installation. This does not authorize a push, tag, publication or public release.

**Launches.**
- Sessions 94 and 95 launched headless, isolated dedicated test servers under the owner's standing
  permission for needed tests: two runs in Session 94, and one two-session run in Session 95.
- Session 96 launched no server or game client; the owner was unavailable to test and no other
  players were available.
- Session 97 launched no server or game client. The owner ran the private builds and supplied the
  visual/performance observations recorded above.
- Session 98 launched no server or game client. The owner supplied the handoff and contrast
  observations; 0.4.101 remains unrun.
- Session 99 launched no server or game client. The owner ran the fog builds and supplied the
  screenshots, logs and visual verdicts recorded above; 0.4.108 remains unrun.
- Session 100 launched no server or game client. The owner supplied both screenshots and the server
  log that cleared Farseer; 0.4.109 and 0.4.110 remain unrun. The fog's before/after outline was
  rasterized offline from the real mesh instead.
- Session 101 launched no game. It formalized the scheduling analysis and built the seeded A/B
  harness.
- Session 102 launched the integrated client repeatedly after the owner's explicit authorization,
  always through the isolated benchmark harness and a restored frozen profile. It measured probe,
  load, engine-gate and player-aware conditions, then closed all test processes.
- Session 106 launched no game. The owner ran 0.4.120 and 0.4.121 and supplied the three screenshots
  and visual verdicts.
- Session 107 launched no game. The owner ran 0.4.122, 0.4.123 and 0.4.124 and supplied the
  screenshots, the FPS comparison and the verdicts; his 0.4.124 client log was read offline.
- Session 108 launched no game. The owner ran 0.4.125 and supplied the verdict and the exploring-hitch
  report; his client log and saved config were read offline.
- Session 109 launched a dedicated sandbox server and client four times, through
  `scripts/bench-windows.ps1` and a sandbox profile seeded from the default benchmark world. The
  owner asked for the tests to be run for him. His game was confirmed closed first, and no test
  process was left running.
- Session 91's controlled servers ran only after the owner's approval.

**Documentation.** Validation passed **10,919 checks** with no failures at the Session 109 doc pass.
It covers:
- current version and changelog coverage;
- source count;
- compatibility numbers;
- links;
- session routing;
- shader ASCII;
- active-plan routing.

## Established product state retained

- **Accepted baseline.** Version 0.4.64 (owner, 2026-09-12). Its water keeps the settled seam and
  narrowly scoped loading guard accepted in 0.4.39.
- **Server independence.** The client remains useful on an unmodified server. Server capture, safe
  sweeping, optional generation, dormant snow refresh and live assist remain additive.
- **Renderer paths.** Packed clustered terrain is still the required visible opaque path, and
  dedicated OIT water remains required. Automatic plausible renderer fallback remains disabled.
- **Distant anti-aliasing** never changes a vanilla pixel, and never keys anything to a fixed
  distance.
- **Recorded as shown.** A block is recorded as what it shows: a chiselled block as its most visible
  material (settled decision 36).
- **One Settings editor.** Settings has one editor with two entry points. The mirrored vanilla
  distance remains vanilla-owned, and pause loading reuses bounded owner-thread work (settled
  decision 37).
- **Ownership.** The handoff fix changes when ownership's range half is decided, not what ownership
  means, and 0.4.135's watch list changes only when an announced chunk is probed. Water development
  still may not redefine shared terrain ownership, and letting air above owned ground own its cell
  is held (owner, 2026-09-29).
- **Stand-ins.** A parent covers until its children are ready, so a transition can be coarse but never
  a hole; since 0.4.135 it covers only the quarters that are waiting (settled decision 6).
- **Held sections merge.** A held section is merged, never replaced. First sightings and parents
  fill only; a level-0 row the server rewrote after the client saw it wins column by column (owner,
  2026-09-12; G188).
- **Section sync.** A client on a dedicated server is owed exactly what its server's cache changed
  since it last caught up, settled only by a durable, new-enough merge. A client with no record
  re-checks everything it holds, newest winning (owner, 2026-09-22; settled decision 52).
- **Writing real terrain.** Real saved terrain is generated only by an explicit, privileged,
  confirmed `/vhworldgen` job. Sweep, `/vhgen`, automatic frontier work and silhouette previews keep
  the generates-nothing promise (settled decision 38).
- **Yielding.** Automatic producers never monopolize generation authority; the frontier and the
  silhouette builder yield to explicit `/vhworldgen` and `/vhgen` work (settled decision 39).
  Startup-sweep priority loads also yield for five seconds after any player joins or crosses a
  chunk boundary, and at the first visible engine generation request, without accumulating a
  catch-up burst (0.4.111; runtime-measured, not yet owner-human-tested).
- **Farseer ownership.** Farseer is fallback scenery and may never show over ready vanilla or LOD
  terrain, including below excavated vanilla ground. It remains until live replacement coverage is
  proven. The proof comes from stable vanilla readiness and exact all-level installed-mesh
  coverage, never cached data alone, the current frustum or camera yaw (settled decisions 42 and
  45–46).
- **Farseer seam fog.** The veil is a continuous field-driven medium derived from the exact directed
  ownership boundary; it never changes ownership. The outline comes from every in-reach tile and
  local smoothing, never from the view. One signed-distance/height field drives a half-resolution
  depth-aware raymarch and a depth-aware upsample. A shallow strip resting on the landing surface
  hides the seam; the bank above is quoted along a level crossing so it stays see-through; fog left
  by a moved seam dissolves through a worker-built residual while new fog appears at once. It draws
  after OIT as an RGB-only overlay with read-only primary depth, so a fog correction cannot rewrite
  water or terrain ownership (settled decisions 47–51).
- **Thread count.** Multi-thread generation is not a player option. The engine setting was active in
  P0, but failed both the material-speedup and terrain-identity gates (G198).

## Next work

Begin with [Session 112](dev/sessions/SESSION_112.md) and the top of [TODO](dev/TODO.md).

1. **The owner's exploring test of 0.4.135.** New mountain or cliff terrain in flight, a few minutes
   with `.vhstandin off`, then his client log: judge by the `stand-in check` and `stand-in:` lines.
   Once they confirm the fix, remove the temporary recorder in a new build. The held
   air-above-owned-ground change comes back only if walls still show over confirmed ground.
   Separately, he was asked whether the all-cores collector check should stop reading his installed
   game file.
2. **Ask the owner what comes next.** The stutter is solved for him; what it left:
   - **The bug report** to the game's developers is drafted; the owner sends it.
   - **The Server GC's first-minute collections** (56-139 ms while the world loads, G267).
   - **Before recommending it to players:** a longer paired memory look, a smaller machine, Linux and
     macOS, and the README and mod description at release.
   - **The next slowest frames:** entity ticking and this mod's `vh:prepare`, 2-4 ms spikes.
   - **This mod's remaining garbage:** 55-70 MB/s of large `UInt64[]` from the capture rebuild.
   - **The installed-off headless run was 20% faster**; repeat each mode before claiming speed.
3. **Section sync's remaining evidence:** a dedicated server with a second player changing terrain
   while the first is offline, and a measured large-world re-check. The singleplayer first run is
   done.
4. **Terrain-change acceptance by the owner:** tests 2/3/16 and 5 have sandbox evidence only; 1, 6,
   7, 9, 10 and 14 harness or sandbox evidence at most.
5. **Prove the audit catches a real fault.** Every dispute so far is ordinary change.
6. **Close P3's deferred integration debt** when another player is available: a complete
   singleplayer disc and restart read-back, then dedicated opt-in, reach clamp, fairness,
   disconnect/off cleanup and mixed versions. The P2 `/vhworldgen`-to-tile check also remains.
7. **Finish P4/P5 evidence:** complete camera-yaw, rapid replacement, excavation and weather
   observations beneath the veil; tune Outline detail, Hill softness and apron once the fog is
   accepted; record Farseer off/on and seam fog off/on FPS in one scene.
8. **Finish P5 credit:** refine islands/inlets only if needed, then complete the deliberate
   player-facing Farseer rename plus bundled MIT notice.
9. **P6 is Terrain Ahead,** a separate feature; P7 canopy follows its acceptance.
10. **Worldgen's remaining checks** are not gates: final durable completion, a real restart and
    offered resume, dedicated permissions and read-only status, and a long-run memory and disk pass.
11. **Older TODO debt:** P3's after-comparison; winter convergence, re-judged once the snow
    refresh's backlog has drained; pause loading; Session 84 diagnostic cleanup.

[Session 112](dev/sessions/SESSION_112.md) is the authoritative stand-in and fresh-chunk continuation
point;
[Session 111](dev/sessions/SESSION_111.md) is the authoritative stutter and collector continuation
point;
[Session 110](dev/sessions/SESSION_110.md) retains the hitch recorder and the claim-path fix;
[Session 109](dev/sessions/SESSION_109.md) is the authoritative section-sync continuation point;
[Session 108](dev/sessions/SESSION_108.md) is the authoritative Farseer-fog continuation point;
[Session 107](dev/sessions/SESSION_107.md) retains the natural bank and the terrain-built outline, and
[Session 106](dev/sessions/SESSION_106.md) retains the continuous-medium replacement of the cards;
[Session 105](dev/sessions/SESSION_105.md) retains the terrain-change measurement continuation;
[Session 104](dev/sessions/SESSION_104.md) retains the first phase-A play verdict and the rejected brake;
[Session 103](dev/sessions/SESSION_103.md) retains phase A, the frontier A/B and the snow-refresh bug;
[Session 102](dev/sessions/SESSION_102.md) retains the first sweep A/B and the 0.4.111 gate;
[Session 101](dev/sessions/SESSION_101.md) retains the pre-measurement sweep analysis,
[Session 100](dev/sessions/SESSION_100.md) retains the preceding fog and attribution work, and
[the silhouette handoff](dev/plans/HANDOFF_HORIZON_SILHOUETTES.md) orients a new agent.
- Sessions 98-82 retain ownership/persistence/tuning, the silhouette comparison, P2 and the first
  look, and the Generate,
  delivery, Settings, structure-colour, anti-aliasing, tree/ice, handoff, winter and water history.
- Architecture owns the established invariants, and `dev/WIRE_HISTORY.md` owns compatibility.
- G166-G272 in `dev/GOTCHAS.md` preserve the recent proven traps, including client-store startup,
  buried joins, bounded regeneration, stable all-level ownership, post-water volumetric fog, the
  distinction between pacing priority-load submissions and yielding their downstream engine work,
  the engine generation counter's saved-load blind spot, route-halo generation, and now what a
  whole-row hash really reports, why a claim recorded before the work describes a state nobody
  held, why an arm that starts when a job finishes needs its own voice, why smoothed translucent
  cards still expose their intersections, why organic fog noise cannot own seam opacity, why a seal
  many times denser than its fog paints a wall, why the vertical column is not the shortest path at
  a band's edge, why a frustum-built outline moves with the camera, why a fixed step budget misses
  far fog, that the engine's uniform collector ignores `sampler3D`, why a column-normalized bank is
  a wall from the side, why a thin layer is stepped over or buried on slopes, why replacing a
  field makes its fog vanish, why SQLite reads a column added after a blob from the row unless an
  index leads with the equality, why a fetch slot released only at durability throttles
  catch-up to the save checkpoint, why a shared reader's answers must be collected in every phase,
  why `array[Lookup()] += value` writes past a grown array, why a diagnostic in the render loop needs
  its own safety net, why an allocation tick names a closure without its class, and now why the
  engine's chunk thread spins with one generation thread on 8+ processors, why a tiny virtual getter
  is the wrong thing to patch, why removing throwaway garbage does not change the felt stutter, why
  the approximate allocation count under-reads under the Server GC, why a bigger gen0 budget
  lengthens pauses, why "Optimize RAM" is not a collector setting, why the Server GC's first minute
  is not its steady state, why an A/B's off side must keep its instrumentation, and now why a parent
  standing in for one missing child covered its whole footprint, why the engine announces a chunk
  before tessellating it, why a quadtree placeholder has no stored row, and what a coarse top over
  the game's ground means.

The active performance route remains
[the main-thread performance plan](dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md). GPU renderer work
is further specified by
[the GPU-driven terrain renderer plan](dev/plans/PLAN_GPU_DRIVEN_TERRAIN_RENDERER.md).
