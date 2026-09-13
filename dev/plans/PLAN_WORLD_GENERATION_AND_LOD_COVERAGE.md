# Plan - World generation and complete distant terrain

**Status:** In progress. P1-P3 are complete (0.4.66-0.4.69, Sessions 89-90); P4-P8 are open and
W1-W8 undecided. Written at the owner's request on 2026-09-12 from report-only investigations against
0.4.64 (code `dd0394c`, docs `b6c2950`). The owner decides the order of work (decision W6).
**Created:** 2026-09-12
**Scope:** Three defects seen on a new world during vanilla `/wgen pregen` (the first join drew only
water; strips and single-chunk holes in distant terrain), their fixes, and a player-started
"Generate World" feature that drives the vanilla world generator faster through the mod, with its
own page in the Horizons settings editor. Section blob format 4 is unchanged. The Section table's
schema meaning is unchanged; one additive client table is proposed (3.3). Assist protocol stays 1;
new messages are appended (3.7).
**Related plan:** [PLAN_HORIZON_SILHOUETTES_AND_FRONTIER.md](PLAN_HORIZON_SILHOUETTES_AND_FRONTIER.md)
(proposed, queued behind the water work) uses the same server generation machinery. Section 3.8
says how the two fit together.

## 0. In plain words

On a brand-new world, while vanilla's `/wgen pregen` was running, three things went wrong and one
opportunity turned up.

1. **The first join showed only water.** When a world loads, the mod checks the graphics card once.
   On that join the check measured the screen at a moment when the game's drawing area was the
   whole window (2560x1440) instead of the 3D picture (1920x1080, because the game renders at 75%).
   The check failed. Since 0.4.27 a failed check hides all distant terrain for the rest of the
   session and never checks again. Distant water has its own drawing path, so it still showed.
   Reloading ran the check at a normal moment and it passed.
2. **The distant strips and holes were not capture failures.** The server half of the mod captured
   almost every column vanilla finished. The terrain was lost on its way to the screen:
   - The server half saves at most 256 distant-terrain sections about every 47 seconds, and while
     generation runs it keeps skipping some of them. The player's side only sees what is saved.
   - The player's side copies each distant section once, usually while it is still half filled,
     and never updates it. That is what the thin parallel walls in the 18:23 screenshot are.
   - Near the player, a section the player's side partly captured itself blocks the server's
     complete version.
3. **Vanilla pregen is slow because the engine generates on one thread.** On a 16-thread CPU the
   game kept about 2 threads busy. The engine has a built-in multi-threaded mode, switched off by
   default.

The plan fixes 1 and 2 first, because they affect every world, pregen or not. Then it adds
**Generate World**: a page in the Horizons settings where the player chooses an area and how much
of the machine to use. The mod then drives the vanilla generator: it keeps it busy, optionally
uses more threads, paces itself so distant terrain keeps up, and shows progress, time left and disk
used. It can be paused, stopped, and resumed after a restart. Nothing is generated unless a player
starts it.

## 1. Goals and decisions

### 1.1 Goals

1. Distant terrain draws on every join. A passing condition during start-up never switches it off
   for the session.
2. Everything the server half captures reaches the player's distant view, and stays current, within
   a bounded delay, including while generation is running.
3. A player-started world generation that is faster than `/wgen pregen` on multi-core machines,
   never outruns distant-terrain capture, and lets the player choose how much of the machine it
   uses.

### 1.2 Owner decisions needed (proposals)

| ID | Proposal | Why it needs the owner |
|---|---|---|
| W1 | Generate World writes real terrain into the savegame, exactly as `/wgen pregen` does. Only a player with permission can start it, after a confirmation that shows the area, time and disk it will take. Every automatic path (the sweep, `/vhgen` peeks, the frontier plan) keeps the "generates nothing" promise. | `DESIGN.md` 13.1 and 14 promise the mod writes nothing to the world. This would be a deliberate, player-started exception. |
| W2 | More worldgen threads are offered only if the P0 test produces identical terrain with Watersheds and TerraPrety installed, and then labelled Experimental. The count is set per world and takes effect when the world is next loaded. | The engine ships this switched off and labels its settings file "use at your own risk". |
| W3 | Singleplayer: always available. Multiplayer: requires the `controlserver` privilege and a server running a mod version with this feature. Other players see progress read-only. | Decides who may make the world grow. |
| W4 | The default preset is Balanced (3.7). | A player-facing default. |
| W5 | Generation stops while the game is paused, as vanilla's does. An option "Keep generating while the pause menu is open" (off by default) would keep the world running behind the menu, including time and creatures. The alternative is to leave the option out. | Changes vanilla pause behaviour. |
| W6 | Order: the fixes (P1-P4) before the generation feature; P0 whenever a restart can be spared. Where all of this sits against the water work and the frontier plan is the owner's call. | Priorities. |
| W7 | The page is a tab called "Generate" in the Horizons editor, after General and Anti-Aliasing (and the planned Silhouettes tab). | The tab strip has clipped labels before (Session 77). |
| W8 | On quit, the server half may take up to 60 s to finish saving distant terrain, with a progress line in the log. Today it gets 15 s, and anything unsaved is dropped. | A slower exit against a lost cache. |

### 1.3 Standing rules that apply throughout

- Ask before any game launch, including sandbox runs. The owner runs his own playtests.
- Diagnostics reach the log, on demand, one line at a time (G72, G76). Game chat cannot be copied.
- An optional feature has a real off switch: with no generation job, nothing runs and nothing is
  held.
- No hard-coded rendering distances. The generation area is the player's choice. Tile sizes follow
  cache geometry, not view distance.
- No plausible renderer fallback (G160). Fix A makes the check correct; it does not bring back the
  retired renderer.
- Every playable build takes the next patch number and gets a checkpoint commit (G57, CLAUDE.md).
- Water development must not reinterpret terrain ownership. Nothing here touches the readiness mask.

## 2. What was found (evidence)

### 2.1 The first join drew only water

- **Log-measured.** Client log of 17:19-17:24 (archive `2026-09-12_17_16_41`): the GPU probe
  reported "private depth copy/mips not validated ... depth texture 1920x1080 does not contain
  viewport 0,0 2560x1440", then "Capability result: GPU renderer shadow is off", then "REQUIRED
  PACKED TERRAIN PATH FAILED ... regional GPU publication is disabled". The 30 s report shows
  opaque 0 calls against water 20,801 calls. Every other join that day, including the reload at
  17:24, logged "private depth copy/mips validated 1920x1080 ... Fast-path prerequisites
  validated".
- **Source-traced (mod).** `LodGpuDepthCopyProbe.Probe` compares the GL viewport with the depth
  texture. `LodGpuTelemetry.ProbeAndReport` runs once (`probeAttempted`), and
  `LodTerrainRenderer.ConfigureRenderPaths` runs once. `LodRenderPathPolicy` turns "Tier 1 not
  validated" into "shadow off", which `DescribeRequiredTerrainFailure` reports as a required-path
  failure. Water uses its own dedicated path and was unaffected.
- **Source-traced (engine, decompiled 1.22.7).**
  - `GuiScreenRunningGame.RenderToPrimary` sets the viewport to the window size times SSAA.
  - `ClientMain.UpdateResize` calls `OnResize`, which sets the viewport to the full window. It does
    this on a session's first `MainRenderLoop` and after any resize, just before the Before stage
    in which the probe runs.
  - `ClearFrameBuffer(Default)` rebinds the primary framebuffer without resetting the viewport.
  - Which of these reached the probe's frame is not pinned. That join was a brand-new player, and
    the game paused in the same second.
- **Causing change: 0.4.27** (2026-09-08). It is the first build in `dist/` that contains the
  required-path suppression. The viewport check is present in every scanned build back to 0.3.180;
  in those builds a failure left terrain on the older renderer.
- **Related.** `LodGpuDepthPyramid` also sizes its per-frame depth copy from the GL viewport, so
  the running path relies on the same assumption.

### 2.2 Distant strips and holes: capture works, delivery does not

**Measured offline** from savegame and cache snapshots, with columns classified by the savegame's
generation pass (pass 6 = finished):

- At 17:52 the server cache held 8,214 of the 8,701 columns vanilla had finished.
- At 18:24, behind the pregen front, every finished column missing from the server's disk belonged
  to a whole missing L0 section: 1,840 columns in 473 sections. 95% of those columns were already
  present in the server's saved L5 rows, which is only possible after capture.

### 2.3 The server half cannot save fast enough, and starves some sections

- **Source-traced.** `LodPipeline.PumpPersistenceCheckpoint`:
  - freezes at most `MaxStorageBacklog` = 256 keys per checkpoint, in the enumeration order of the
    `SaveDirty` `HashSet`;
  - snapshots one section per tick (`PersistenceSnapshotsPerTick`);
  - waits 30 s after each checkpoint drains.

  That caps saving at about 256 sections every ~47 s (about 5.4 per second).
- **Measured.** In three snapshots taken during pregen, the newest save round held exactly 256
  rows. A run of 44 L0 sections at chunk x 7856-7857 stayed off disk for more than 40 minutes while
  sections generated later were saved.
- **Inferred, not observed.** Freed `HashSet` slots are reused by newly dirtied keys, so keys
  inserted late can be passed over for as long as generation keeps dirtying new ones.
- The shutdown flush waits 15 s (`StorageShutdownTimeoutMs`).

### 2.4 The client never updates distant sections it already copied

- **Source-traced.**
  - In singleplayer the client reads the server half's cache file through `LodLocalOfferSource`,
    whose scan publishes changed rows for level 0 only.
  - `LodRemoteKeySet.AddRemoteKeys` skips any key the client already holds.
  - The only replacement path is the level-0 update.

  A far section is copied the first time the view wants it, usually while the pregen has filled
  only its western edge, and after that it never changes.
- **Measured.**
  - Client rows with fewer captured columns than the server's row for the same key: 150 at 18:05,
    187 at 18:24. Only 2 of the first 150 caught up.
  - Far L3 and L4 copies held only the first 1-3 chunk columns of each 16-chunk section; the server
    held about 100%.
- **Human-observed.** The owner's screenshot `2026-09-12_18-23-18` shows thin parallel walls of
  terrain with sky between them. A reload does not help, because the stale rows sit in the
  client's own cache.

### 2.5 Near the player, a partial own copy blocks the complete one

18 client L0 rows hold only the part the client captured itself (around the edge of vanilla's
view), while the server's row for the same key is complete. The server row did not change after
the client first saw it, so no update was ever published. (Measured offline.)

### 2.6 Smaller findings

- `LodPipeline.ScheduleCaptures` silently drops a queued column whose map chunk unloaded before
  scheduling. Capture exceptions are counted but never reach the server log. One finished column
  (8019, 7977) was never captured; its cause is not attributed.
- The sweep's warning "6 of 256 sampled positions ... now exist" was a false alarm: vanilla pregen
  was generating at the same time.
- The client thread that scans the server cache (`vintagehorizons-local-offers`) re-reads the
  whole table every 2 s. With a 1.7 GB server cache it used about 25% of one core (measured per
  thread).
- The server half writes no capture or save backlog to the log unless `VINTAGEHORIZONS_STATS=1`.
- `dev/GOTCHAS.md` contains G184-G186 twice. This is doc hygiene, for the doc pass.

### 2.7 Why vanilla pregen is slow (engine facts)

- **Measured** during `/wgen pregen 150`:
  - 9.5 columns per second (one 201-column batch every ~21 s).
  - The game kept 2.2 of 16 logical CPUs busy. `chunkdbthread`, the engine's only worldgen thread,
    ran at 75-98% of one core. The 14 Watersheds helper threads sat at about 4% each.
  - The server was suspended for 4.5 minutes in total; `pauseGameOnLostFocus` is off.
- **Source-traced (decompiled 1.22.7).**
  - `MagicNum.MaxWorldgenThreads` comes from `servermagicnumbers.json` (default 1, read once per
    process). `ChunkServerThread` sets `additionalWorldGenThreadsCount = min(5, value - 1)`.
  - `ServerSystemSupplyChunks.OnBeginGameReady` starts one extra thread per generation pass:
    Terrain, TerrainFeatures, Vegetation, NeighbourSunLightFlood, PreDone. `chunkdbthread` keeps
    column creation, loading and any passes beyond those.
  - Those threads are created at GameReady, after mods start, from the field. A mod can therefore
    set the count per world before then, through reflection (`ServerMain.chunkThread` is internal).
  - `--reducedthreads` forces 0. Singleplayer does not pass it.
  - The engine's generating queue (`RequestChunkColumnsQueueSize` 2000) also holds neighbour
    requests. When it overflows, the engine saves and clears the whole generating queue.
  - Vanilla pregen (`WgenCommands.LoadColumnsSlow`) calls `LoadChunkColumn` for 201 columns only
    when fewer than 10 are queued, then checks again a second later. With one thread that loses
    little, but it would drain a multi-thread pipeline on every batch.
  - `LoadChunkColumnPriority` runs a blocking area load on the chunk thread and does not suit bulk
    work.
  - `ChunkColumnLoaded` fires on the main thread when a requested column completes. A finished
    column with no player near is unloaded after about 6-9 s.
  - `LoadChunkColumn(x, z, keepLoaded: true)` and `UnloadChunkColumn` exist.
  - A suspended singleplayer server generates nothing.

### 2.8 Measured costs on this world (Watersheds and TerraPrety installed)

| | Per generated column | Radius 150 (90,601 columns) |
|---|---|---|
| Savegame | about 50 KB | about 4.5 GB |
| Server distant-terrain cache | about 21 KB | about 1.9 GB |
| Time with one worldgen thread | 9.5 columns/s | about 2.6 h |

These figures calibrate the page's estimates until the running job measures its own rate.

## 3. Design

### 3.1 Fix A - the GPU check measures the frame, and a passing failure is retried

- Validate the depth copy against the frame the engine renders (`capi.Render.FrameWidth` and
  `FrameHeight`) and the depth attachment's own size, and copy the region (0, 0, frame). Record the
  GL viewport but do not trust it.
- `LodGpuDepthPyramid` takes its copy size from the same frame source. When the GL viewport
  disagrees, it logs both sizes once.
- A passing condition does not latch. A size mismatch or a zero-area window retries on later frames
  (a small bounded number of attempts over a few seconds) before the failure becomes final. A real
  capability failure (a missing entry point, or the compute/SSBO probe failing) latches at once, as
  today.
- There is no fallback renderer (G160). A final failure keeps today's REQUIRED error, adds the
  viewport, frame and texture sizes, and the chat hint names `.vhgpu on` as a way to re-check.
- **Checks:**
  - rule tests: a 2560x1440 viewport with a 1920x1080 frame and texture passes; a frame larger
    than its texture fails;
  - retry-policy tests;
  - the existing probe tests still pass.
- **Owner test:** create a new world, minimise the window while it is being created, then join.
  Distant terrain appears, and the log shows the probe validated, possibly after one retry.

### 3.2 Fix B - server saving keeps up and never starves

- **Oldest first.** `SaveDirty` keeps the order in which keys were first dirtied (an ordered set:
  key and first-dirty time). A key dirtied again keeps its original place. Checkpoints take the
  oldest keys.
- **Keep up.** When more is dirty than one checkpoint holds, start the next checkpoint at once
  instead of waiting 30 s. Snapshot several sections per tick under the existing elapsed-time and
  byte budgets (G3, G15), and keep one transaction per checkpoint.
- **Report.** `/vhserver` gains the dirty count, oldest dirty age, saves per second, capture
  backlog, drops and capture errors, and writes each line to the server log (G72). While a sweep or
  generation job runs, the same line goes to the log once a minute.
- **Shutdown (W8).** Flush for up to 60 s with a progress line every 5 s. The existing "unresolved
  saves" warning stays.
- **Checks:**
  - a harness with inflow above the checkpoint cap proves every dirtied key is saved within a
    bounded number of checkpoints;
  - order and throughput tests against a fake storage thread;
  - the existing persistence and revision tests (G16, G25) still pass.
- **Evidence:** a vanilla pregen with the build installed. The server log shows the oldest dirty
  age staying bounded (target: under 2 minutes) and save rounds no longer pinned at 256. An offline
  snapshot shows no whole-section gaps behind the pregen front.

### 3.3 Fix C - the client's copies stay complete

- **Incremental scan.** `SELECT Detail, SX, SZ, ModifiedMs FROM Section WHERE rowid > @last`
  (`INSERT OR REPLACE` gives a rewritten row a new rowid), with a full scan once at start and rarely
  afterwards as a safety net. Changes are published at every level, not only level 0.
- **Fill, never overwrite.** For a key the client already holds, copy in from the server's row only
  the columns the client has not captured: the union of captured columns, with palettes remapped
  the way the mip merge already does it. This path never replaces a column the client captured
  itself. The effects:
  - far sections copied half-filled become whole;
  - near sections the client partly captured gain the server's other columns;
  - the client's own mip propagation then updates their parents.

  **As built in 0.4.68 (owner decision, 2026-09-12, Session 90):** only first sightings and parents
  are fill-only. A level-0 row the server rewrote after the client saw it wins column by column, so
  server-side snow refreshes still reach held ground; it never erases a column only the client holds
  and never replaces observed snow with an unobserved peek.
- **Remember what was merged.** A small additive client table records, per key, the server rowid
  last merged, so a restart does not redo the work. It is an additive table, noted in
  `WIRE_HISTORY`, and the blob format is unchanged. In the first session after upgrading, shared
  keys are merged lazily, visible and nearest first, under the existing install budget.
- **Safety.** Decoding and merging run off the owning thread. A result is applied only if the
  client section's content revision has not changed since (G7); otherwise it is recomputed.
- **Dedicated servers.** Audit the network assist path for the same partial-copy case, and apply the
  same fill rule to level-0 replacements there.
- **Checks:**
  - merge tests covering palette remap, captured bits and the snow tails;
  - rowid behaviour under REPLACE on a real SQLite file;
  - remote key-set tests for held keys at every level;
  - G6: every request still ends in success, refusal or retry.
- **Evidence:** after a pregen with the player in the world, the offline comparison reports the
  client rows behind the server near zero within minutes of the server saving. The owner looks at
  the same far area as the 18:23 screenshot.

### 3.4 Fix D - smaller fixes

- **D1.** Count "unloaded before capture" and capture errors on both halves, and report them
  through 3.2. Generation jobs keep their columns loaded until captured (3.5).
- **D2.** The sweep's absence check reports "inconclusive" instead of warning when the engine was
  generating other columns during the sweep.
- **D3.** The server report in 3.2.
- **D4.** The incremental scan in 3.3 brings the scanner's cost down. Target: under 2% of one core,
  measured per thread as in this investigation.

### 3.5 The world generation job (server)

Hosted by `LodServerCaptureSystem`. It opens the server pipeline on demand, as `/vhgen` does.

- **Area.** A centre (my position, world spawn, or typed coordinates), a radius in chunks, and a
  shape (square or circle).
- **Order.** Tiles of 16x16 chunks (one L3 section, 512 blocks), nearest tile first in a spiral,
  rows inside a tile. Each tile completes its part of the distant-terrain pyramid before the next
  starts. A far section is then whole the first time the client sees it, which also blunts the
  problem Fix C addresses.
- **Skip.** Columns the server cache has already captured, or that the job's own state records as
  done. Columns that exist but are not captured are loaded rather than skipped, which costs little.
- **Issue.** `LoadChunkColumn(x, z, keepLoaded: true)`: the engine generates and saves the column
  as it does for `/wgen pregen`. The job holds each column loaded until the pipeline reports it
  captured, then releases it. P5 must verify that `UnloadChunkColumn` saves before it unloads, or
  release through the normal ageing path instead. This removes the capture drop for generated
  columns, and RAM stays bounded by the in-flight cap.
- **Depth.** Keep the engine's generating queue (`CurrentGeneratingChunkCount`, which includes
  neighbour requests) near a target depth set by the preset. The hard ceiling stays well below the
  engine's 2,000 (for example 1,200), because an overflow clears the engine's whole queue.
- **Completion.** Tracked through `ChunkColumnLoaded` for the job's own coordinates. Each column has
  a timeout (for example 5 minutes), one retry, then it is reported. Every issue ends in done,
  refused or retried (G6).
- **Pacing gates.** Each is named in the status when it is holding the job:
  1. the engine queue is at its target depth;
  2. the capture backlog is full (`CaptureBacklogFull`);
  3. the save backlog is too old or too large (from Fix B);
  4. the server is overloaded (tick time);
  5. the player is playing (Light preset only: holds while the player travels faster than walking
     or tick time rises).
- **Control.** Pause, resume and stop. A state file,
  `ModData/vintagehorizons/<world>-worldgen.json`, holds the area, preset, cursor and a done bitmap
  per tile. After a restart the job offers to resume; it never resumes silently.
- **Status.** Columns done and total, rate (one-minute average), time left, disk used so far
  (savegame and cache growth since start), and the active gate. Written to the log on demand and at
  every 5%, from the owning thread (G29).
- **One worldgen authority.** While a job runs, `/vhgen`, startup pregen and the frontier planner
  (once built) hold. A job refuses to start while another of them runs, and says why. It also warns
  when the engine is already generating heavily for other reasons, such as vanilla `/wgen pregen`.
- **Disk check.** Before starting, compare the estimate (2.8, refined by the live rate) against free
  space on the savegame's drive, and refuse with the numbers when it will not fit.
- **Command.** `/vhworldgen start|pause|resume|stop|status`, with the `controlserver` privilege. It
  is a new name; a duplicate would disable the rest of the mod's startup (G41).
- **Off state.** With no job, nothing is registered: no tick listener, no status traffic.

### 3.6 Worldgen threads (only if P0 passes)

- The page shows how many worldgen threads the engine is currently using.
- **Per world.** The desired count is stored in the world's mod data. At server start, before
  GameReady, the mod sets `additionalWorldGenThreadsCount` through reflection, provided that:
  - the field exists;
  - `--reducedthreads` is not set;
  - the owner has enabled the option.

  The count is clamped to 1-6 and to the logical CPU count minus 2. It takes effect the next time
  the world loads, and the page says so.
- **Fail safe.** If the reflection fails, the mod logs one line and leaves the engine alone.
  Dedicated servers can keep using `servermagicnumbers.json`.
- **Expected gain.** Unknown until measured. The threads form a pipeline, one per generation pass,
  so the gain is limited by the slowest pass and will be less than 6x. With one thread, keeping the
  queue full should add only a few percent over vanilla's pregen.

### 3.7 The Generate World page (client) and its messages

**Where.** A "Generate" tab in the Horizons editor (vanilla Settings and the `.vhconfig` window),
after General and Anti-Aliasing (W7). P7 must check the width of every label in the tab strip,
including the planned Silhouettes tab.

**When it is available.** The tab is greyed out, with a one-line reason, in any of these cases:

- in the main menu ("Load a world to generate it");
- when the server does not advertise the feature (an append-only `Features` bit in
  `AssistWelcome`, shared with the frontier plan);
- when the player lacks permission, in which case progress is shown read-only.

**Controls (proposed).**

- **Area:** centre (my position, world spawn, or coordinates), radius slider in chunks with the
  distance in blocks beside it, and shape (square or circle). Below it: the column count, the
  estimated disk use (savegame and distant-terrain cache, from 2.8), and the estimated time at the
  chosen preset.
- **Resources:** a preset (Light, Balanced, Fast, Maximum or Custom), each with a one-line plain
  description. Custom shows the advanced controls:
  - worldgen threads (1-6, Experimental, applies after reloading the world);
  - columns kept in progress (the engine queue depth; roughly the RAM used);
  - "Pause while I am travelling";
  - "Keep generating while the pause menu is open" (W5);
  - capture pacing: follow the generator, or a limit.
- **Buttons:** Start opens a confirmation that shows the area, disk and time, says "This adds
  terrain to your world permanently", and suggests a backup. Pause/Resume and Stop act at once.
  The page's choices (preset, radius, centre) are remembered through Save like other settings, and
  Defaults resets them.
- **Live status** while a job runs: a progress bar, done and total, rate, time left, disk used, the
  worldgen threads in use, and the gate currently holding the job in plain words (for example,
  "waiting for distant terrain to finish saving").
- **Refresh.** The server sends a status message about once a second, only while the page is open
  or a job is running.

**Presets (proposed; P0 and P5 set the real values):**

| Preset | Engine queue target | Worldgen threads | Yields to play | Meant for |
|---|---|---|---|---|
| Light | 24 | unchanged | Yes: holds while travelling or under load | Playing while it works |
| Balanced (default) | 120 | unchanged | Only when the server is overloaded | Most players |
| Fast | 400 | 3, if enabled | No | Staying nearby, not playing |
| Maximum | 900 | 6, if enabled | No | Walking away from the PC |
| Custom | slider | slider | toggle | Players who want to tune |

"Resources" in the player's terms: threads are CPU cores, queue depth is RAM (P0 measures the RAM
per column), and the area is disk.

**Messages.** Appended on channel `vintagehorizons`; the protocol stays 1:

- `WorldGenRequest` (start, pause, resume, stop, with the options);
- `WorldGenStatus` (progress);
- the `Features` bit.

Every request ends in success, refusal with a reason, or retryable (G6). Player-visible text avoids
bare angle brackets (G67, G142). Tab switching keeps the deferred re-compose already used for the
existing tabs (G178).

### 3.8 How this fits with the frontier plan

- **One worldgen authority.** The frontier plan already suspends its planner while `/vhgen` or
  startup pregen runs. A Generate World job joins the same rule (3.5).
- **Shared pieces:** the capture-backlog gate, Fix B's save-backlog gate,
  `CurrentGeneratingChunkCount` as the engine-yield signal, the `Features` bit, and status to the
  log.
- **Different purpose.** The frontier plan peeks: nothing is saved, and trees are left out.
  Generate World builds the real world, saved and complete.
- **Benefit.** Fixes B and C help the frontier plan directly, because its corridor terrain travels
  the same save and delivery path.

## 4. Phases

Each playable build takes the next patch number, is installed copy-only, and gets its checkpoint
commit. Phases P1-P4 are independent of each other and of the generation feature.

### P0 - Engine measurements and the thread test (owner-assisted)

- **Deliver:** steps and scripts only. The owner restarts the game and runs the commands; nothing is
  launched without asking.
- **Steps:**
  1. Two copies of one world. The owner makes them, or approves copying; at today's size that is
     about 4 GB each.
  2. The same `/wgen pregen 30` (3,721 columns) over the same ungenerated area in each copy: one
     with `MaxWorldgenThreads` 1, one with 6.
  3. Measure columns per second from the chat timestamps, CPU per named thread (the
     `GetThreadDescription` method used here), and process RAM.
  4. Compare the generated chunks of the two copies offline by decoding the savegame's chunk rows.
     Worldgen is seeded, so the terrain should match.
  5. Watch the engine at queue depths of about 100, 400 and 1,200 for warnings and stalls.
- **Gate:** identical terrain, or every difference explained, decides W2. The measured rates set
  the preset values and the time estimates.

### P1 - Fix A (GPU check)

- **Deliver:** 3.1.
- **Checks:** the rule and retry tests.
- **Gate:** the owner's new-world test in 3.1.

### P2 - Fix B (server saving) with D1's counters and D3's report

- **Deliver:** 3.2 and D1's counters.
- **Checks:** the starvation, order and throughput tests.
- **Gate:** the pregen evidence in 3.2, read from the owner's server log and an offline snapshot.

### P3 - Fix C (client copies) with D4

- **Deliver:** 3.3 and 3.4 D4.
- **Checks:** the merge, rowid and key-set tests.
- **Gate:** the offline comparison shows client rows behind the server near zero, and the owner
  confirms the far area from the 18:23 screenshot draws complete.

### P4 - D2 (sweep false alarm)

- **Deliver:** D2, riding with P2 or P3.
- **Gate:** a sweep during vanilla pregen logs "inconclusive" instead of the warning.

### P5 - The generation job and `/vhworldgen` (no page yet)

- **Deliver:** 3.5, including the shared worldgen authority.
- **Checks:**
  - area enumeration and tile order;
  - skip rules;
  - gate arithmetic and in-flight bookkeeping;
  - resume from the state file;
  - release of every kept column on stop and at shutdown;
  - refusal while another generator runs.
- **Evidence:** a side-by-side against vanilla at one thread, on copies of the same world and area.
  The job must match or beat vanilla's rate, drop zero captures, show far sections whole on first
  sight, name the gate that is holding it, resume after a restart, and leave no column force-loaded
  after a stop.
- **Gate:** the owner's run.

### P6 - Worldgen threads (only if P0 passed)

- **Deliver:** 3.6.
- **Checks:** clamping, and the fail-safe when the engine field is missing.
- **Evidence:** the engine logs the extra threads, and the per-thread CPU sample shows them working.
- **Gate:** P0's terrain comparison repeated on the build.

### P7 - The Generate World page, its messages and presets

- **Deliver:** 3.7.
- **Checks:**
  - message round trips and refusals;
  - availability rules;
  - preset mapping;
  - label widths;
  - zero traffic when idle.
- **Evidence:** the owner uses the page in singleplayer. A dedicated-server check runs in the
  sandbox, after asking.
- **Gate:** the owner's acceptance of the page.

### P8 - Verification matrix and documentation

Run when the owner calls the doc pass (section 8).

## 5. Risks and unknowns

| Risk | Handling |
|---|---|
| Multi-thread worldgen produces wrong terrain with a worldgen mod | P0's offline comparison is the gate; the option stays Experimental (W2). |
| Kept-loaded columns hold RAM, or leak after a crash | Bounded by the in-flight cap; released on stop and shutdown; leftovers cleared at start. P5 checks whether the engine persists its force-loaded list. |
| Reflection into engine internals breaks on an engine update | Fail safe: log one line and leave the engine default. |
| Engine queue overflow clears the whole generating queue | Hard ceiling well below 2,000, including neighbour requests. |
| A faster generator exposes the next bottleneck (capture thread, client mesh and upload) | Gates hold the job. The capture thread used about 15% of a core at 9.5 columns/s, so there is estimated headroom, which P5 measures. |
| Disk fills | Estimate and free-space check before start; disk used shown live. |
| The world only generates while the game is unpaused | Stated on the page; W5. |
| The engine path behind 2.1 is still not pinned | Fix A removes the dependence; P1 logs viewport, frame and texture sizes to settle it. |
| A merge copies stale content | The fill rule only fills uncaptured columns and never overwrites; revision-checked (G7). |

## 6. Settled decisions and rules this plan must respect

- `DESIGN.md` 13.1 and 14, the "generates nothing" promise: kept for every automatic path.
  Generate World is the player-started exception in W1, recorded as a settled decision in
  `ARCHITECTURE.md` at the doc pass.
- Settled decision 33 (vanilla-to-LOD ownership) and the readiness mask: untouched.
- G160: no plausible renderer fallback.
- G6, G7, G15, G16, G25: request, revision and persistence rules in Fixes B and C and in the job.
- G29, G72, G76: status is owning-thread work and reaches the log on demand.
- G41: new command names are unique.
- G57: every playable build takes a new patch number.
- `WIRE_HISTORY`: appended messages, the `Features` bit, and the additive client table are recorded
  there.

## 7. Files expected to change

- **Fix A:** `Render/Gpu/LodGpuDepthCopyProbe.cs`, `Render/Gpu/LodGpuTelemetry.cs`,
  `Render/LodTerrainRenderer.cs` (probe retry), `Render/Gpu/LodGpuDepthPyramid.cs`.
- **Fix B:** `Lod/LodPipeline.cs` (checkpoint and dirty ordering), `Lod/LodWorld.cs` (`SaveDirty`),
  `Storage/LodStorageThread.cs` if batching changes, `Net/LodAssistServerSystem.cs` (`/vhserver`),
  `Net/LodServerCaptureSystem.cs`.
- **Fix C:** `Lod/LodLocalOfferSource.cs`, `Lod/LodRemoteKeySet.cs`, `Lod/LodPipeline.cs` (merge
  install), `Lod/LodSection.cs` (column fill), `Storage/LodStore.cs` (merge-record table),
  `VintageHorizonsModSystem.cs` (`PumpLocalOffers`).
- **Fix D:** `Lod/LodPipeline.cs` (drop counters), `Net/LodAbsenceVerifier.cs`,
  `Net/LodSavegameSweep.cs`.
- **Generation:** new `Net/LodWorldGenJob.cs` and `Net/LodWorldgenThreads.cs`;
  `Net/LodServerCaptureSystem.cs` (host, command, authority); `Net/LodServerConfig.cs`;
  `Net/LodAssistProtocol.cs` (messages, `Features`); `Net/LodAssistServerSystem.cs` and
  `Net/LodAssistClient.cs` (routing); `Gui/VintageHorizonsConfigDialog.cs` (tab);
  `Gui/VintageHorizonsSettingsIntegration.cs` (embedded page).
- **Tests:** new and extended check suites in `tests/VintageHorizons.Checks`.

## 8. Documentation at completion

At the owner's doc pass, following the `CLAUDE.md` ritual:

- a session record;
- `CHANGELOG`;
- `GOTCHAS`, with the new traps: viewport is not frame; `HashSet` order can starve a queue;
  sibling-cache updates were level-0 only; an engine queue overflow clears generation; force-loaded
  columns must be released; and the duplicate G184-G186 fix;
- `WIRE_HISTORY`;
- `ARCHITECTURE`: W1 as a settled decision, and the shared worldgen authority;
- `STATUS`;
- `TODO`;
- `DocCheck`.

## 9. Evidence sources

- **Client and server logs:**
  - archive `2026-09-12_17_16_41` (first join): `client-main.log` lines 400, 417 and 440;
  - the live session from 17:24;
  - the chat log (pregen batch timing).
- **Offline snapshots of the savegame and both caches** at 17:52, 18:05 and 18:24-18:27:
  - savegame chunk keys are `x | z << 27`, and mapchunk protobuf field 4 (`currentpass`) is 6 when
    finished;
  - cache blobs were decoded to captured bits per 32x32 quadrant;
  - save rounds were grouped by `ModifiedMs` gaps over 2 s.
- **Engine, decompiled 1.22.7:** `ChunkServerThread`, `ServerSystemSupplyChunks`, `MagicNum`,
  `ServerSystemUnloadChunks`, `ServerSystemLoadAndSaveGame`, `ServerMain`, `WorldAPI`,
  `ClientMain`, `ClientPlatformWindows`, `ScreenManager`, `GuiScreenRunningGame`, and VSEssentials
  `WgenCommands`.
- **`dist/` build scan:** the first build containing `REQUIRED PACKED TERRAIN PATH FAILED` is 0.4.27.
- **Per-thread CPU:** sampled from the running game with thread names read through
  `GetThreadDescription`.
- **Owner screenshot:** `2026-09-12_18-23-18.png`.
