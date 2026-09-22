# Vintage Horizons - status

> Tier 2: current state and verification evidence. Historical measurements and development
> narrative live in the sessions; durable design lives in `dev/ARCHITECTURE.md`.

**Status date:** `2026-09-21`
**Mod version:** `0.4.125` - current private test build: the Farseer seam fog hides the seam with a shallow strip resting on the ground, under a bank thin enough to see Farseer through, and fog left behind by a moved seam dissolves. Verified, copy-installed and checkpointed; **owner-played: "Everything looked good"**. The fog defaults changed to his tuning afterwards, in source only
**Last accepted baseline:** `0.4.64` at commit `dd0394c`, accepted by the owner on 2026-09-12
**Active branch:** `codex/water-depth-light`; recent checkpoints 0.4.121 `a5f528d`, 0.4.122 `d46b776`, 0.4.123 `b46cf0a`, 0.4.124 `1fb10f5`, 0.4.125 `92eb9c3`; nothing pushed
**Target:** Vintage Story 1.22.5+, .NET 10
**Source files:** `138` C# files under `VintageHorizons/src`
**Assist protocol:** `1`
**Blob format:** `4`
**Database schema:** `6` (column fingerprints carry their own rule version, now `2`)

## Current result

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
- **Not established:** the settle path under load. That session captured 273 columns and all 273
  were claimed after a save with no fallbacks, but a session with a large sweep behind it is the
  real test. Whether a disputed claim's recapture is *wasted* is measured for 48 of 162 columns and
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
- **Not built: A4's clean-shutdown fast path**, and it should not be built as specified. There is no
  hook after the engine's final save (G236), so its marker cannot record the file's post-save state.
  A4's other half - re-fingerprinting after the engine writes - is what 0.4.119 built, in the shape
  the plan's own 2026-09-19 correction describes. Recording the fast path as dropped is still the
  owner's decision.

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
  world-generation controls.
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
| Live delivery | Durable changed L0 rows reach clients: integrated play through the sibling cache's rowids, dedicated play through refresh manifests. A held section merges them column by column, the newer copy winning, never erasing client-only columns (0.4.68); clients rebuild parents | Source/check evidence; integrated merge human-tested in 0.4.68; dedicated multiplayer temporal run remains open |

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
  between current peers while retaining direct push for an older peer.

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

The numbers stay `1 / 4 / 6`.

## Diagnostics

Session-only unless stated; client commands write their findings to the client log, server commands
to the server log.

- `.vhtaa` reports distant anti-aliasing (above); `.vhtaa show` tints what it touches.
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

**Checks and builds.** The final fast tier passed **25,443 assertions with zero failures** at
0.4.125 and again after the defaults change. The Release mod build completed with zero warnings and zero errors; no benchmark build was
needed for the fog work.

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
- Session 91's controlled servers ran only after the owner's approval.

**Documentation.** Validation passed **10,803 checks** with no failures at the Session 108 doc pass.
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
  means. Water development still may not redefine shared terrain ownership.
- **Held sections merge.** A held section is merged, never replaced. First sightings and parents
  fill only; a level-0 row the server rewrote after the client saw it wins column by column (owner,
  2026-09-12; G188).
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

Begin with [Session 108](dev/sessions/SESSION_108.md), [Session 105](dev/sessions/SESSION_105.md) and the top of [TODO](dev/TODO.md),
[the Farseer handoff](dev/plans/HANDOFF_HORIZON_SILHOUETTES.md), and
[the horizon-silhouette/frontier plan](dev/plans/PLAN_HORIZON_SILHOUETTES_AND_FRONTIER.md).

1. **Ask the owner what comes next.** The seam fog is in a state he is happy with. Two things are
   recorded in TODO for when he wants them:
   - **Hitches while exploring new terrain**, with low minimum FPS, cause unknown: separate the fog,
     Farseer, the savegame sweep and vanilla on one route, and ask him for the hitches' frequency and
     depth from his frame-time graph.
   - **Seam fog questions** not separately reported: the strip from above, straight-down coverage,
     edges taller than the strip, camera steadiness, upsample edges, the fade time and a fade-in.

   Use `scripts/fog-mockup` before and after any change to the medium.
2. **Play a world with a real sweep behind it.** The claim path now waits for a world
   save and re-reads the file, and that ran end to end on `test2` - but only 273 columns passed
   through it. A fresh world, or one with a lot of newly generated terrain, puts thousands through
   it in one session. Afterwards the status line should show claims "read back after a world save"
   far outnumbering "from the startup reading", and the next start should load only a few hundred
   columns.
3. **Give claim re-reads priority over audit re-reads.** Measured 2026-09-20: claims for a 20:54
   save were not written until 20:56:39, because they queue behind the audit's steady 16/s stream.
   A session ending in that window falls back to the index-time claim unnecessarily.
4. **Measure the snow refresh's backlog cost.** It has about 12,000 columns queued at 1/s on
   `test2`, roughly three hours of background priority loads, and no FPS or frame-time run has yet
   been taken with that trickle running - the owner stood still deliberately for both builds. This
   is the most likely source of any new in-play cost, and the arm has only been reachable since
   0.4.116.
5. **Record A4's fast path as dropped**, or rebuild it around "record what the next save will touch,
   treat those as unverified". Its other half is built. Owner's call.
6. **Prove the audit catches a real fault.** It is now proven to run and to dispute claims - 81 in
   one pass - but every dispute so far is ordinary change, not a cache that was wrong. Staging real
   corruption was not possible in a live world; its decision rules are covered offline.
7. **Close P3's deferred integration debt** when another player is available: a complete
   singleplayer disc and restart read-back, then dedicated opt-in, reach clamp, fairness,
   disconnect/off cleanup and mixed versions. The P2 `/vhworldgen`-to-tile check also remains.
8. **Finish P4/P5 evidence:** complete camera-yaw, rapid replacement, excavation and weather
   observations beneath the veil; tune Outline detail, Hill softness and apron once the fog is
   accepted; record Farseer off/on and seam fog off/on FPS in one scene.
9. **Finish P5 credit:** refine islands/inlets only if needed, then complete the deliberate
   player-facing Farseer rename plus bundled MIT notice.
10. **P6 is Terrain Ahead,** a separate feature; P7 canopy follows its acceptance.
11. **Worldgen's remaining checks** are not gates: final durable completion, a real restart and
   offered resume, dedicated permissions and read-only status, and a long-run memory and disk pass.
12. **Older TODO debt:** keep the debt including:
   - P3's after-comparison;
   - winter convergence, which G237 showed was never reachable and which the refresh arm has now
     begun working through - re-judge once its backlog has drained;
   - pause loading;
   - Session 84 diagnostic cleanup.

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
- G166-G254 in `dev/GOTCHAS.md` preserve the recent proven traps, including client-store startup,
  buried joins, bounded regeneration, stable all-level ownership, post-water volumetric fog, the
  distinction between pacing priority-load submissions and yielding their downstream engine work,
  the engine generation counter's saved-load blind spot, route-halo generation, and now what a
  whole-row hash really reports, why a claim recorded before the work describes a state nobody
  held, why an arm that starts when a job finishes needs its own voice, why smoothed translucent
  cards still expose their intersections, why organic fog noise cannot own seam opacity, why a seal
  many times denser than its fog paints a wall, why the vertical column is not the shortest path at
  a band's edge, why a frustum-built outline moves with the camera, why a fixed step budget misses
  far fog, that the engine's uniform collector ignores `sampler3D`, why a column-normalized bank is
  a wall from the side, why a thin layer is stepped over or buried on slopes, and why replacing a
  field makes its fog vanish.

The active performance route remains
[the main-thread performance plan](dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md). GPU renderer work
is further specified by
[the GPU-driven terrain renderer plan](dev/plans/PLAN_GPU_DRIVEN_TERRAIN_RENDERER.md).
