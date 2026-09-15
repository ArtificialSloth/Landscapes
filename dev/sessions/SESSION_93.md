# Session 93 — Silhouettes: stall guard, captured heights, tile store and the accelerator comparison

**Date:** `2026-09-14`
**Branch/commit:** `codex/water-depth-light`. Checkpoints `3664aa2` (0.4.78), `612770f` (0.4.79), `f93bbbf` (0.4.80), `3db5e3e` (0.4.81) and `96f8e06` (0.4.82). This documentation pass is uncommitted.
**Mod version:** `0.4.82`
**Assist protocol / blob / schema:** `1 / 4 / 6`. The silhouette tile store is a separate file with its own schema `1`.

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

Session 92 left three silhouette items open: a hang at game exit, poor joins with adjacent terrain,
and slow previews. At the start the owner decided:
- the hang: add a guard, then reproduce on `bodanboys`, ideally with a dump;
- the next phase: P2 (captured heights and the tile store);
- the look: the classic look is accepted apart from the joins, which closes P1's look gate.

After the 0.4.80 test, the owner chose an error brake and fewer previews per region (0.4.81). They
then set `bodanboys` aside and asked for P3 (0.4.82). They asked first for this record alone, then
for the full documentation pass. After the pass they allowed test-server runs for the tests the
work needs.

Sources:
- the owner's game logs, current and archived (`Logs/Archive/<start time>`);
- decompiled `VintagestoryLib` 1.22.7;
- Watersheds 7.0.4, TerraPrety 7.11.1 and Algernon's Terrain Sampler 1.3.0, decompiled outside the
  repository and read only, with no code copied;
- the `bodanboys` savegame and the silhouette store, both read-only with the game closed.

## Work narrative

### 1. The 0.4.77 hang was a different launch

Session 92 and TODO describe the 16:11 quit on `test2`. That quit was clean: "Stopped the
server!", then "Exiting game now", like every archived quit. The hang was the first 0.4.77 launch,
16:04–16:08 on `bodanboys` (G201).

Log-measured:
- Silhouettes went on at 16:06:06. The builder's preview was that session's first world
  generation.
- From 16:06:09 to 16:06:26 there were 99 `IndexOutOfRangeException`s. All came through Watersheds'
  `TerraPretyCoastMapBridge.TryBuildLazily`, then TerraPrety `CoastMap.GenLayer`, then
  `LerpedWeightedIndex2DMap.WeightsAt`, and the bridge caught and logged each one.
- From about 16:06:26 the world-generation thread answered nothing more:
  - the builder froze at 220 issued, 204 landed, 16 in flight;
  - the savegame sweep, which uses the same thread, stopped at 30%;
  - no world-generation debug lines followed.
- The owner left at 16:07:39. The server never logged "Server ticking has been suspended", and the
  owner closed the game about 55 s later.

Source-traced:
- `ServerMain.Suspend` waits up to 60 s for every server thread to pause (G204).
- An exception escaping a preview would have stopped the server with a Fatal line. None was logged,
  so the thread was busy or blocked, not crashed.

### 2. The stall guard (0.4.78)

`LodGenerationStallWatch` declares a stall when 20 s pass with probes or previews outstanding and no
answer.
- A gap of more than 5 s between builder ticks restarts the count.
- The server clock it uses stops while the game is paused (G202).

On a stall the builder does three things:
- it issues nothing more for the session;
- it warns requesting players in chat;
- it logs the previews still waiting, oldest first, and the last one that came back.

A later answer is logged as "slow rather than stuck". The guard cannot restart the thread. The
threshold was checked against the healthy 16:08 `test2` launch: while the builder held for real
generation, all 16 in-flight previews still landed.

### 3. Captured heights and the fill (0.4.79)

- **Server.** It keeps the worldgen heights of every column it loads (`ChunkColumnLoaded`) and of
  every column its cache previews (`LodPipeline.PreviewHeightsCaptured`, in `CaptureColumn`).
  - These are captured cells, which outrank previews.
  - Old-save columns whose worldgen heights are a copy of the rain map are counted.
- **A deviation from plan 4.4.** Loaded columns come from the event, not `ScheduleCaptures`, so
  heights arrive with the sweep off and without the server cache open.
- **`EnableSilhouettes`** (server config, `bool?`). Null means automatic: singleplayer and LAN-hosted
  worlds. Dedicated servers stay off until P3's test. The builder is created at `RunGame`, because
  the capture system loads the config and nothing orders the two systems' start.
- **Client.** Plan 4.3's deterministic fill replaces P1's lattice blend.
  - It is a push-pull over a tile and its eight neighbours at 4-block cells, with levels aligned to
    the world grid.
  - Known cells keep their heights exactly.
  - A changed tile re-meshes its eight neighbours.
  - It was pulled forward from P4, because captured heights are invisible without it.
  - Its arrays are pooled, because the full grid is past the large-object threshold.
- **The fill's reach.** An empty tile beside a known one fills almost to its far edge, and the fill
  stops before the next tile.

### 4. The tile store (0.4.80)

- **The store.** `LodSilhouetteStore` keeps `ModData/vintagehorizons/<savegame>-silhouette-server.db`,
  with table `Tile (TX, TZ, Revision, Data, ModifiedMs)`.
  - One thread owns an unpooled connection (G9, G13).
  - Every load is answered, with a tile or null.
  - Saves are batched, and each merges into the stored row (`LodSilhouetteTile.MergeFrom`): the
    older copy fills unknown cells and those it knows from a strictly better source. The revision
    never goes back.
- **The builder:**
  - holds previews until the store lists its tiles;
  - reads a tile's stored copy before previewing in it;
  - writes changed tiles at every world save and at shutdown, waiting at most 5 s and reporting any
    leftovers;
  - lets go of saved tiles no reach covers once more than 2,048 are in RAM.
- **The file** is created only on a world's first silhouette request. This is the principle behind
  `LodServerCaptureSystem`'s startup guard.
- **`/vhsilhouetteserver [status]`** (controlserver) reports the builder, heights, previews and
  store. The plan's `/vhsilhouette` could not be used: `StaticAssetChecks.ChatCommandNamesAreUnique`
  forbids reusing the client's name.

### 5. The owner's test of 0.4.80 on bodanboys

The owner played from 19:13 to 19:21. Log-measured unless stated:

- **Start.** Silhouettes went on at 19:17:12; the store opened empty. The first preview was again
  chunk 8044,8036, and again the session's first world generation.
- **Errors.** 144 coast-map errors, all from 19:17 on, on the same bridge path as §1.
- **Progress at 19:17:43.** 342 previews issued, 326 landed, 16 in flight; captured heights from 168
  columns; 463 of 844 probes found existing terrain.
- **The thread went quiet.** World-generation debug lines stop at 19:17:35.
- **Suspends failed.** A pause at 19:17:45 left the server's suspend waiting until 19:18:48, when it
  gave up with "The following threads were not suspended: chunkdbthread". The same warning came
  again at 19:20:57 and 19:21:09.
- **The guard fired at 19:19:02**, with 16 previews and 32 existence checks outstanding. The
  previews listed as waiting were all in chunk column 7884 (block x 252,288), about 3.7 km west of
  the player.
- **Leaving.** The owner left at 19:19:04. The server stopped at 19:21:10, and the game quit
  normally at 19:21:21 because the owner waited.
- **Slow, not dead.** The world-generation thread answered again at 19:21:10, during shutdown. No
  answer arrived between 19:17:43 and then, so one tick lasted about 3.5 minutes, or the thread was
  blocked.
- **Repeated placements.** The server debug log shows the same dungeon placements repeated at the
  same coordinates, for example `/tp =253208 106 =258064` four times. They are identical to the
  16:04 launch's.

Checked afterwards, read-only with the game closed:
- **The savegame** holds 482 map regions. None of the six regions with repeated placements is saved.
  Of the regions under the listed stuck previews, 492,501 and 492,502 are saved, and 492,503 to
  492,505 are not.
- **The silhouette store** holds 96 tiles (61 KB, revisions 1–80). These are the tiles that were
  waiting at 19:20:57, so the shutdown save worked.
- **The healthy `test2` launch at 16:08** shows the same repetition: 68 placement lines, 20
  distinct, up to 4 repeats. It landed 420 previews with no stall.
- **Only `bodanboys` logged the coast-map errors** (99 and 144; none on `test2`). On both of its
  launches they came just before the stall.

Source-traced: a preview builds its chunk's map region, including every worldgen mod's region step,
and drops it when the preview ends (`GetOrCreateMapRegionNoStore`). The next preview in the same
unsaved region builds it again (G203). The lattice put four previews in each 512-block region.

### 6. Error brake and fewer rebuilds (0.4.81)

The owner chose this from three options.

- **The error brake.** `LodWorldgenErrorBrake` reads the server log (`ILogger.EntryAdded`) only while
  a player asks for silhouettes.
  - Three Error or Fatal entries written while previews wait on the generator stop previews for the
    session. A chat line says so, and a log line quotes the first error.
  - The mod's own lines (prefix `[vintagehorizons]`) never count.
  - It stops all previews, not only those into unexplored regions, because the logs do not show
    which regions throw.
- **One preview per region.** A tile is previewed only while it holds no heights, and a tile
  claimed by a probe or preview gets no other.
  - A tile is one map region, so each unexplored region is built once per world; the store
    remembers it.
  - Unexplored outlines coarsen to one height per 512 blocks until coast refinement. The owner
    accepted that.
- **Four previews in flight** (was 16). The engine works one at a time, so throughput does not
  depend on the queue, and a stop leaves at most four behind.

The owner could not say whether new land on `bodanboys` stalls without silhouettes: they had not
explored there lately.

### 7. P3 begins: the accelerator comparison (0.4.82)

The owner set `bodanboys` aside and asked for P3. Sampler facts, from decompiled code:

- **Algernon's Terrain Sampler 1.3.0.**
  - Entry point: `TerrainSamplerMod.Instance.GetBlockColumnHeight(int x, int z)`, on the server.
  - It keeps per-thread scratch data and concurrent caches, and locks its vanilla map generators.
  - With Watersheds loaded it answers `WatershedsGenTerra.GetPreWatershedsBlockColumnHeight`, the
    height before rivers.
  - A failure of its own generator answers sea level silently. A Watersheds failure logs a warning
    and falls back.
- **Watersheds 7.0.4.** Entry point:
  `WatershedsMod.Instance.terrainSampler.SampleTerrainHeightsBatch(IEnumerable<(int, int)>, bool ignoreRivers = false)`.
  Rivers are included by default, and it uses `WatershedsGenTerra`'s region caches.
- **Shared state.** Both call into the `WatershedsGenTerra` instance the world generator also uses.
  Whether that is thread-safe alongside real generation is unknown.

What 0.4.82 adds:
- **`LodHeightAccelerators`** finds either sampler by reflection, only when installed.
- **`/vhsilhouetteserver compare [points]`** (controlserver; 16–4,096 points, default 512).
  - **Points.** It picks real heights: the first cell of each chunk the builder holds as captured
    or previewed, thinned evenly.
  - **Sampling.** It samples each accelerator one point at a time, on a background thread.
  - **Report**, per accelerator, to chat and the server log:
    - error median, 90%, 95% and maximum, and bias;
    - land/sea disagreement, overall and on the coast (real height within 4 blocks of sea level);
    - exact sea-level answers;
    - time per sample;
    - the five worst points.
- **The builder uses no accelerator** until the owner judges the figures.

Proposed P3 order:
1. the comparison;
2. coast refinement and the land/sea hint (`GenMaps.oceanGen` is public on the server);
3. the manifest and tile-request transport, the dedicated opt-in and the gates.

### 8. Checks

- **Results.** 0.4.78: 24,847; 0.4.79: 24,863; 0.4.80: 24,891; 0.4.81: 24,902; 0.4.82: 24,916.
  All pass.
- **New pure checks:**
  - the stall watch on a fake clock;
  - the fill: known cells kept, bounded, deterministic, borders agreeing, reach;
  - the mesh built from the fill;
  - merge and snapshot;
  - config resolution;
  - the error brake: counts only errors during previews, never its own lines, trips once;
  - the comparison's arithmetic on a hand-computed set.
- **New fixture.** The store on a temporary SQLite file: reopen, merge-on-write, missing and
  undecodable rows, and the format purge.
- **New source-wiring checks:**
  - the captured-height hook is attached only while enabled;
  - there is one column-load subscription;
  - the stored copy is read before previews;
  - saves happen at world save and shutdown;
  - the store is created lazily;
  - the brake listens only while someone asks;
  - one preview per region, and four in flight;
  - the comparison is an admin subcommand run on its own thread;
  - the builder uses no accelerator.

---

## Delivered

- **0.4.78 (`3664aa2`):** the stall guard. Zip SHA-256
  `4b798d2732eb7135a64a3f7e1add76ffc73eb449fa1851a98311eade8c651d9d`.
- **0.4.79 (`612770f`):** captured heights, the fill and `EnableSilhouettes`. Zip SHA-256
  `6475ce2fa81d0c6dc83358b1d30f70ac62afc93444f4acae15210b0f52145857`.
- **0.4.80 (`f93bbbf`):** the tile store, its lazy creation and `/vhsilhouetteserver`. Zip SHA-256
  `ae92efabd94c505b83513a9866bcb5ecef8bd81d160d956ad2681614cffd46a3`.
- **0.4.81 (`3db5e3e`):** the error brake, one preview per region, four in flight. Zip SHA-256
  `5c59daa5973a3b5f2eb7e3d37b107db1b958eb65cb08af401b91d2a03068e1c0`.
- **0.4.82 (`96f8e06`):** the accelerator adapters and `/vhsilhouetteserver compare`. Zip SHA-256
  `d519acde999ddb708bd25c8420a2d1a620f32c1ca340199fbfeb68b26571a6eb`.
- All five copy-installed; older zips untouched.
- **This documentation pass** (uncommitted). It includes the owner's TODO note on Algernon's Terrain
  Sampler's other values.

## Decisions

- **Owner, 2026-09-14:**
  - guard, then reproduce;
  - P2 next;
  - the classic look accepted apart from the joins;
  - error brake plus one preview per unexplored region, accepting coarser unexplored outlines until
    coast refinement;
  - `bodanboys` set aside;
  - P3 next;
  - test-server runs allowed for needed tests, after this pass.
- **Heights** come from `ChunkColumnLoaded` plus the preview hook, not `ScheduleCaptures`.
- **The fill** was pulled into P2 from P4's mesher.
- **The store** is created on a world's first request and merges on write.
- **The error brake stops all previews.**
- **Dedicated servers stay off** until P3's test.
- **Accelerators** are compared before any use.
- **Rejected:**
  - opening a store in every singleplayer world at startup;
  - blocking stored-copy reads before merging captured heights;
  - braking only unexplored-region previews, because the logs cannot tell which regions throw;
  - trusting either sampler before the comparison.

## Traps

Promoted to `dev/GOTCHAS.md`:
- G201: the log folder holds only the latest launch.
- G202: server elapsed time stops while the game is paused.
- G203: a preview never keeps the map region it builds.
- G204: a busy world-generation thread delays pausing and quitting.

## Flagged and unverified

- **Judgement calls awaiting owner review:**
  - the server command name `/vhsilhouetteserver`;
  - in singleplayer the server keeps captured heights in RAM for every world by default (the file
    is created only on first use);
  - an empty tile beside a known one fills almost to its far edge;
  - dedicated servers kept off regardless of the setting until P3;
  - the proposed P3 order.
- **Below established level:**
  - **Why one world-generation tick lasted about 3.5 minutes on `bodanboys`.** Both stalls followed
    coast-map errors that only `bodanboys` shows, which is a correlation. Only a dump of the stalled
    process shows the cause.
  - **An idea that does not fit.** The engine rebuilding already-saved regions in worlds created
    before 1.22.7 does not match the evidence: the repeated placements are all in unsaved regions,
    and `test2` shows them too.
  - **Not yet observed in game:**
    - whether the joins look better;
    - whether stored tiles come back after a restart;
    - the error brake and one-preview-per-region rule;
    - the comparison.
  - **The comparison needs silhouettes on the server.** A dedicated test server has them forced
    off, so the comparison cannot run there without a change.
  - **Thread safety.** Both samplers call Watersheds' generator code from a background thread;
    whether that is safe is unknown.
  - **P2's integration gate** (a `/vhworldgen` run whose columns appear in tiles) is not checked.
  - **Unchanged from Session 92:** GPU cost, the off-state A/B, and a dedicated server.
  - **Leak lines at quit.** Four "MeshRef … leaking memory" debug lines (6 indices, VAO ids 5–8)
    appeared at quit in the 0.4.80 launch only. Their low ids suggest early interface
    allocations; not established.
