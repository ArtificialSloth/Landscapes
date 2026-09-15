# Plan - Horizon silhouettes and terrain ahead

**Status:** Active. Revised 2026-09-14 with the owner's decisions D8-D15, against 0.4.76
(`9ea75c5`; documentation `229c04b`).
- P1, the first look, landed in 0.4.77 (`4c7720c`). The owner accepted the classic look apart from
  the joins.
- P2 landed in 0.4.79-0.4.80, with a stall guard and an error brake in 0.4.78 and 0.4.81 (Session
  93).
- P3 began in 0.4.82 with the P0.3 comparison command. In 0.4.83 the comparison ran twice on
  isolated test servers (Session 94); the owner's verdict is pending.
- Silhouettes come first (D14).
- Terrain Ahead is a separate, later feature (D15).

**How the build differs from this text (Sessions 93–94),** until a revision folds it in:
- **A dedicated test server can measure heights** with the developer switch
  `VINTAGEHORIZONS_SILHOUETTE_MEASURE` (0.4.83). It keeps heights and runs `compare`, and offers
  players nothing (G206).
- **Heights of loaded columns** come from the server's column-loaded event, not the
  `ScheduleCaptures` site in 4.4. Previewed cache columns come through
  `LodPipeline.PreviewHeightsCaptured`. This works with the sweep off and without the server cache
  open.
- **The 4.3 fill** replaced P1's lattice blend in P2 rather than P4, because captured heights are
  invisible without it.
- **The tile store** is created on a world's first silhouette request, and a save merges into the
  stored row.
- **The coarse pass (4.5)** previews one chunk per unexplored 512-block region, not four, because a
  preview rebuilds its map region and throws it away (G203). At most four previews are in flight.
- **Previews stop for the session** after 20 s of generator silence or three logged errors (G204).
- **The server command** is `/vhsilhouetteserver [status|compare]`, not `/vhsilhouette`; the client
  owns that name.
- **`EnableSilhouettes`** is `null` (automatic) as planned, but dedicated servers stay off whatever it
  says until P3's test.

The first version was written from a report-only investigation on 2026-09-12 against 0.4.64
(`dd0394c`).
**Created:** 2026-09-12. **Revised:** 2026-09-14.
**Scope:** Two independent features that share server generation capacity. Each has its own
switch and settings tab, and turning it off removes its cost.

- *Silhouettes* adds:
  - a new silhouette tile data type with its own store and transport;
  - height sampling from tiered sources, built far in advance of the player;
  - a Farseer-style pass inside the existing renderer.
- *Terrain Ahead* is automatic generation of real cached terrain in the level-0 band ahead of a
  travelling player. Earlier text, and the code's `FeatureFrontierGeneration`, call it the
  frontier.

Section blob format 4, database schema 6 and assist protocol 1 stay unchanged. Behaviour against
servers without the mod stays unchanged.

## 0. In one paragraph

Two features, each with its own switch.

**Silhouettes.** As soon as you are in a world, the server works out ground heights sparsely in
every direction out to the silhouette reach (8,192 blocks by default, adjustable on the distance
scale). It keeps them, so the outline of distant land is already there when you look. Heights come
from the cheapest source available, in this order:
1. terrain the server captures anyway;
2. an optional accelerator mod, when one is installed;
3. the engine's own world-generator preview.

Your client draws them as a Farseer-style silhouette wherever real cached terrain is missing, so
from a boat you can see which way the land is. Real cached terrain always wins, and the two blend
at the join.

**Terrain Ahead.** As you travel into land nobody has generated, the server previews the ground
just ahead of you. It turns it into ordinary cached terrain before you arrive, bare of trees as
`/vhgen` terrain is today.

**Both features:**
- need the mod on the server (singleplayer counts);
- are on by default in singleplayer and off on dedicated servers until the admin enables them;
- never write to the savegame.

Each switch in `.vhconfig` turns its feature off, and off means the work stops, not merely that it
is hidden.

## 1. Goal and decisions

### 1.1 Goal

1. **Navigation.** Show the outline of distant land beyond everything cached terrain draws, on sea
   and on land. Have it ready in advance, before the player has chosen a direction.
2. **Terrain ahead.** Keep the complete columns the generator produces. Use them to build real
   cached terrain in the level-0 band ahead of the travelling player.

### 1.2 Owner decisions (binding)

| ID | Date | Decision |
|---|---|---|
| D1 | 2026-09-12 | Silhouettes draw **wherever real cached terrain is missing**: unexplored ground close by, gaps between explored areas, and beyond the cached-terrain draw distance. Real cached terrain always wins; the two cross-fade. |
| D2 | 2026-09-12 | Automatic generation is **on by default in singleplayer/LAN-hosted worlds, opt-in on dedicated servers**, and switchable in `.vhconfig` (player) and the server config (admin). Applies to each feature separately (D15). |
| D3 | 2026-09-12 | Ground generated by Terrain Ahead stays **bare in the first delivery**. A statistical canopy is a later phase with its own acceptance. |
| D4 | 2026-09-12 | The client-only coastline for servers without the mod is a **separate, later plan**. Facts gathered for it are in Appendix A. |
| D5 | 2026-09-06, restated 2026-09-12 | Terrain Ahead fills the **level-0 band ahead** of the player and keeps the full terrain it generates. |
| D6 | 2026-09-06 | Farseer's renderer is not ported as its own system. Its data role and its look are. |
| D7 | 2026-09-12, split 2026-09-14 | **Each feature can be switched off by the player, for the performance saving.** Off means no client work, no RAM held, no GPU resources, no shader cost, and no server work on that player's behalf for that feature (section 4.10). |
| D8 | 2026-09-13 | Silhouette heights need only be **fairly representative**. Being off somewhat is acceptable, possibly better artistically, and cheaper, faster routes are preferred. The caution that wrong-but-confident terrain is worse than none does not apply to silhouettes; it still applies to voxel LOD. |
| D9 | 2026-09-14 | **Farseer is a guideline, not a specification.** We write our own code. Farseer is inspiration, or the direct pattern where we find nothing better. |
| D10 | 2026-09-14 | The **silhouette reach is a handle on the General-tab distance scale**: adjustable, default 8,192 blocks. The cached draw distance also moves onto the scale, and the silhouette band starts where cached terrain ends. |
| D11 | 2026-09-14 | Farseer's **classic colour scheme** for now: sky-derived haze, no snow or biome tint. |
| D12 | 2026-09-14 | **Keep small islands and inlets** where possible. |
| D13 | 2026-09-14 | Horizons stays **standalone with no required dependency**. Other mods may be used as **optional accelerators** when installed (for example Algernon's Terrain Sampler or Watersheds' own sampler), never required. |
| D14 | 2026-09-14 | **Silhouettes come first and are built far in advance** for the whole reach, so distant terrain already tells the player where to travel. They do not wait on travel. |
| D15 | 2026-09-14 | **Terrain Ahead is a separate feature** with its own switch in a different settings tab from Silhouettes, so a player can use either without the other. Supersedes the single master switch (former A1). |

Standing rules that apply throughout:
- No hard-coded rendering distances: key rendering to on-screen size, pixel ownership, or the
  player's own settings.
- Ask before any game launch.
- Diagnostics must reach the log, on demand.
- Documentation changes only when the owner asks.

### 1.3 Working assumptions (the owner may override any of these)

- **A1** - *Superseded by D15.* Was: one master switch with two finer switches beneath it.
- **A2 - Terrain Ahead densifies only the level-0 band by default.** Ahead when travelling, all
  round when stationary. Densifying the whole draw distance is a server setting
  (`TerrainAheadDenseReachBlocks`, default 0), because it costs roughly 0.8-1.5 GB of server
  cache per 4,096-block disc (section 5).
- **A3 - Silhouette samples keep heights only** (section 4.2). "Keep everything" applies to
  Terrain Ahead, where the cached terrain can use it.
- **A4** - *Confirmed as D10* (default reach 8,192).
- **A5 - Names:** *silhouette* for the height-sampled drawing and *Terrain Ahead* for the
  corridor. The settings tabs are "Silhouettes" and "Terrain Ahead".
- **A6 - The Performance preset in `.vhconfig` switches both features off.** Balanced and Quality
  leave them on. This follows D7's stated motive.
- **A7 - No reach ceiling in singleplayer.** A dedicated server clamps the requested reach to
  `SilhouetteReachBlocks`. A singleplayer or LAN host does not; this is Farseer's rule.
- **A8 - A rough first slice before the full build** (P1). The owner judges the look before the
  rest is built around it.

## 2. Source-traced starting point

Everything here is **source-traced** unless marked **measured**. The original text was traced
against 0.4.64 and re-checked against 0.4.76 on 2026-09-14. Engine facts come from decompiled
`VintagestoryLib` 1.22.x and `VSEssentials`.

### 2.1 Transient generation already exists

- `LodPlayerPregen` walks probe -> work -> verify.
  - `EnumPurpose { Generate, RefreshExisting }` and `SelectAction` decide per position.
  - `LodColumnMap.Classify` returns `Peek` (absent), `Load` (present with a complete 4-chunk
    neighbourhood) or `SkipFrontier`.
  - An existing column is never peeked.
- Peeks run to `EnumWorldGenPass.Terrain` and are captured through `LodPipeline.CaptureColumn`,
  which records `SnowStateObserved = false`.
  - Caps: `GenerateColumnsPerSecond` 16; `GenerateMaxInFlight` 64 (1-2 MB held per landed peek);
    the capture-backlog gate; a 1 ms per-tick issue budget; a 300 s stuck-peek timeout.
  - The engine saturates near 23 columns/s (recorded).
- Engine `ServerSystemSupplyChunks.PeekChunkAreaLocking` generates a square of side
  `2 * (min(pass, 5) - 1) + 1` for the requested pass: Terrain 1x1, TerrainFeatures 3x3,
  **Vegetation 5x5**, Done 9x9. This is why real trees cost about 25x per column.
- The peek builds map regions transiently (`GetOrCreateMapRegionNoStore`).
  - So a peeked column's `MapChunk.MapRegion` carries forest and climate maps (P7 input).
  - `ServerMapChunk` allocates both `RainHeightMap` and `WorldGenTerrainHeightMap`
    (`ServerMapChunk.cs:139-140`).
- Peeks use their own engine queue (`peekingChunkColumns`, capacity
  `RequestChunkColumnsQueueSize / 5` = 400).
  - `CurrentGeneratingChunkCount` counts only real generation (`requestedChunkColumns`), so it
    never sees peeks.
  - A producer therefore counts its own peeks in flight, and reads the counter only as the "real
    loads are pending" signal.
- `OnSeparateThreadTick` dequeues at most one peek per chunk-thread tick.
  - It runs on the same thread as real generation, so peeks and real loads interleave on one
    thread.
  - The peek callback runs on the main thread (`EnqueueMainThreadTask`).
- Engine world generation stays single-threaded. Session 91 measured the built-in multi-thread
  pipeline at about 1.02x, with scheduling-dependent terrain, and rejected it. The preview budget
  is therefore one thread's.
- The owner's world-generation mods fill `WorldGenTerrainHeightMap` on the Terrain pass
  (decompiled 2026-09-13): TerraPrety 7.11.1 `GenTerraPrety` and Watersheds 7.0.4
  `WatershedsGenTerra`.
- A loaded old-save column with no `WorldGenTerrainHeightMap` gets a clone of `RainHeightMap`
  (`ServerMapChunk.cs:153-155`). Heights read from such columns are rain heights.
- `LodAbsenceVerifier` re-probes a sample of promised-absent positions away from players. "A peek
  persists nothing" is therefore measured, not trusted.

### 2.2 World generation shipped in 0.4.71-0.4.76

- `/vhworldgen` is the one path that writes real terrain, after a privileged player prepares and
  confirms it (settled decision 38).
- `EnumWorldGenAuthority` is `None`, `StartupPregen`, `TransientGeneration`, `WorldGeneration`.
  It has no member for automatic producers: a travelling producer observes the authority, yields,
  and never acquires it (settled decision 39).
- `LodWorldGenGates.Select` is the pacing gate. Both features reuse it rather than growing a second
  set of gates. It checks, in order:
  - faulted and paused;
  - server overload;
  - capture backlog;
  - save backlog, by count and by age;
  - engine queue (`CurrentGeneratingChunkCount` against a target);
  - in-flight work.
- `LodWorldGenPlan.TileChunks` is 16. A Generate World tile is 16x16 chunks, globally aligned:
  exactly one L3 section footprint, and exactly one silhouette tile (4.3).
- The tile-boundary gate calls `LodPipeline.RequestPersistenceCheckpoint()`.

### 2.3 Delivery already reaches connected players

- Server `OfferNewKeys` runs every 30 s.
  - It calls `SnapshotKeys()` (`HasDataSet.ToArray()`), and `ManifestLedger.Delta` then iterates
    every key: **O(total keys) per offer**.
  - `OfferUpdatedKeys` sends `Refresh = true` manifests for changed durable L0 rows.
- The client merges every manifest it receives (`LodAssistClient.PumpCore`), so a later manifest
  is already a delta.
- Singleplayer sections reach the client through `LodLocalOfferSource`. It is a read-only
  sibling-database reader that scans new and rewritten rows by rowid (0.4.68).
- The assist handshake also completes in-process (`Greet()` runs at LevelFinalize).
- New keys enter the server's tree in one place: `LodWorld.RegisterInTree`.

### 2.4 The channel tolerates appended messages

- Seven message types are registered in the same order on both sides: Hello, Welcome,
  KeyManifest, SectionRequest, Section, WorldGenRequest, WorldGenStatus.
  - The engine numbers types by registration order (`nextHandlerId++`) and performs no
    cross-check at handshake.
  - The server ignores a message id beyond its handler table (`OnPacket` bounds check).
- Append-only fields have been added several times without a protocol bump: `Retryable`,
  `Refresh`, `ServerSettings`, `Features`.
- `AssistWelcome.Features` is field 7:
  - bit 0 `FeatureWorldGeneration`;
  - bit 1 `FeatureFrontierGeneration`, reserved for Terrain Ahead;
  - bit 2 `FeatureSilhouetteTiles`, reserved.

  Field 8 is `CanControlWorldGeneration`.

### 2.5 Rendering and settings facts that shape the design

- **Sparse real capture draws as a dotted grid.**
  - `CollectDrawNodes` lets a parent stop drawing once every visible child that is **present in
    `HasDataSet`** is covered; absent children are skipped.
  - Mips of sparse real data stay sparse: a one-in-sixteen chunk lattice is 1/16 covered at L5
    and at most 1/4 at L6.
  - A continuous picture from sparse samples needs interpolated heights, which the voxel cache
    does not do. This is the reason for a separate tile type.
- **The far edge.** `lodsurfacefinish.fsh` mixes terrain toward `getSkyColorAt(...)` by
  `max(smoothstep(0.75, 1.0, dist), edgeFade)`.
  - This covers both the far edge and the edges of explored ground.
  - It evaluates the sky only where the fade is non-zero.
  - It is included by `lodterrainbody.fsh` and `lodwaterfragment.fsh`.
- **Far distance.** `EffectiveFarDistance = min(farthest mesh extent, FarViewDistanceCap)`, never
  below vanilla plus a margin (`LodFarDistance`). The projection grows immediately and shrinks only
  after 5 s, in 512-block steps (`LodFarPlaneState`, G5).
- **Samplers.** All terrain programs share one fixed sampler layout (`LodTerrainSamplerLayout`),
  configured before the first `Use()` (G130, G158). The engine's `Uniform()` throws on an
  undeclared name (G174).
- **The General-tab distance scale** (`LodThresholdScaleElement`):
  - It is logarithmic from 32 to 32,768 blocks (`LodWorld.MaxLevelThreshold`).
  - It has a neutral Vanilla band and handle (settled decision 37, G185), and six L1-L6 handles
    that alternate above and below the band.
  - A boundary marker takes the colour of the region it introduces (G184).
  - It does not show the draw distance. That is a separate slider (`FarViewDistanceCap`),
    1,024-32,768 in 512-block steps, default 4,096; the owner uses 8,192.
  - Levels beyond the draw distance are drawn on the scale although they never render.
- **Settings tabs** today are General, Anti-Aliasing and Generate. Tab switching re-composes
  through the main-thread queue (G178). Session 77's first window clipped a crowded tab strip, so
  five tabs need a width check.
- **Master-switch precedent.** `.vhconfig` already has a saved master switch: `BlendPipeline`
  controls several passes and releases their resources when off.

### 2.6 Storage

- Blob format 4 carries two append-only 512-byte tails (snow provenance, snow coverage) that old
  readers ignore. That is the template for any future per-column fact (G167).
- **Measured 2026-09-12**, read-only, from two existing server caches:
  - L0 sections average **40.5 KiB** (5,342 sections) and **78.4 KiB** (1,262).
  - In both, all levels together are **1.53x** the L0 bytes.
  - These are mixed swept/captured caches from August, not peek-only terrain.

## 3. Farseer and other mods

### 3.1 Farseer as a guideline (D9)

Farseer 1.4.0 (https://github.com/ViciousBadger/VSMod-Farseer, MIT, (c) 2026 Badgerson):

- **Data.** One heightmap per 512-block map region at 4-block cells, read from each column's
  `WorldGenTerrainHeightMap` and clamped to sea level. Tile edges stitch from neighbours.
- **Colour.** Derived from the sky:
  - `getSkyColorAt`, offset by height relative to sea level;
  - a tint mix;
  - a sun-and-daylight light bias;
  - fog;
  - a fade to sky;
  - a globe curve.
- **The join.** The surface sinks a few blocks near real terrain.
- **Client defaults:** SkyTint 5.0; ColorTint (0.26, 0.29, 0.45, 0.40); LightLevelBias 0.70;
  FadeBias 0.4; GlobeEffect 0.05; FarViewDistance 4,096.

We start from its data role and look (D6, D11), and differ where we can do better:

- no second renderer and no direct `MainCamera.ZFar` writes (G5): one pass inside the existing
  renderer;
- no mesh builds on the main thread;
- culling and on-screen resolution, instead of drawing every loaded region (about 254 draws and
  8.3M triangles at its 4,096 default);
- sparse and adaptive sampling, instead of a full 256-peek region at a time;
- an in-flight cap that counts its own peeks. Farseer's only brake,
  `CurrentGeneratingChunkCount > 64`, never sees them;
- tiered height sources (4.2) instead of one;
- no requirement for the mod on every client;
- real cached terrain always wins, and the cached fade blends into the silhouette (4.8).

### 3.2 Optional accelerators (D13)

An accelerator is another mod's height sampler, used through reflection only when it is
installed. Horizons never requires one and never copies its code.

**Algernon's Terrain Sampler.** A library used by Farseer, ChunkLOD and FastMap. Source: its
GitHub source and ModDB page, read 2026-09-14.
- **Method:** version 1.3.0 recomputes vanilla GenTerra heights from the `GenMaps` region maps,
  without generating chunks.
- **API:** `TerrainSamplerMod.Instance.GetBlockColumnHeight(int x, int z)`, and `SampleColumn(x, z)`
  (height plus climate, forest and shrub values).
- **Sides:** server-side. Its modinfo says `RequiredOnClient: true`, so a server that installs it
  makes every client install it.
- **Version:** listed for 1.22.0-1.22.2; the owner runs 1.22.7.
- **Speed:** the author quotes 1-2 ms per sample, once a region's maps are built. Session 94
  measured 0.01-0.02 ms through Watersheds, in freshly generated land.
- **Other mods:** with Watersheds loaded it calls Watersheds' `GetPreWatershedsBlockColumnHeight`.
  Despite the name, that includes river valleys and runs the same steps as Watersheds' own sampler,
  so with Watersheds installed the two accelerators are one function (Session 94, G205). It has no
  TerraPrety handling.
- **Licence:** the repository publishes none, so reflection-only use is the only option.

**Watersheds' sampler.** Source-traced from decompiled 7.0.4, 2026-09-13.
- `WatershedsMod.Instance.terrainSampler` is a public `GenTerraSampler`.
- `SampleTerrainHeightsBatch(IEnumerable<(x, z)>, ignoreRivers = false)` groups points by chunk and
  runs the generation height steps, rivers included.
- Differences from real generation:
  - erosion is a separate call;
  - generation passes TerraPrety coast corners, where the sampler uses its own.
- Thread safety, and behaviour on ungenerated regions, are unknown.

**Why accelerators matter.** A Terrain-pass preview produces 1,024 heights for about 43 ms of the
single worldgen thread. An accelerator produces one height where it is wanted, on spare cores,
without touching that thread. For sparse sampling that is roughly 20-40x cheaper per used height,
and it does not slow vanilla chunk loading.

**Neither is trusted blind.** Each is compared against real previews on the same points before it
is used (P0.3). TerraPrety coasts are the expected weak spot. D8 sets the bar at fairly
representative.

## 4. Design

### 4.1 Overview

```text
SILHOUETTES
client: switch, reach (scale handle)
   -> AssistSilhouetteRequest (appended message, Features bit 2)

server SilhouetteBuilder (one per world; state per requesting player)
   target: the disc of reach + margin around each player, nearest first
   sources, cheapest first:
      captured heights (every server capture path, free)
      accelerator (optional: Algernon's Terrain Sampler, Watersheds' sampler)
      engine preview (Terrain-pass peek, heights only)
   coarse pass -> coast refinement (islands and inlets, D12)
   yields: explicit authority, real loads, gates, tick budget
   -> SilhouetteStore (own file) -> manifest / tile request / tile

client SilhouetteSet (RAM) -> worker mesher (3 resolutions) -> render-thread upload
renderer Opaque callback: cached opaque terrain -> silhouette pass (coverage-cut)
cached far/edge fade -> mixes toward the silhouette colour when silhouettes are on

TERRAIN AHEAD (separate feature, later phase)
client: switch, applied first LOD threshold
   -> AssistTerrainAheadRequest (appended message, Features bit 1)
server TerrainAheadPlanner: travel estimator -> corridor -> peek/load -> CaptureColumn
   -> ordinary sections -> incremental key offers (MP) / sibling scan (SP)
   heights from these captures also feed the silhouette store
```

The two features share only server generation capacity. When both want previews, Terrain Ahead
goes first because it is near and time-critical; silhouettes take what is left.

### 4.2 Height sources

Every height sample carries an **origin**. A better origin replaces a worse one in the same cell,
never the reverse. The order, best first:

1. **Captured:** real terrain the server captured, including Terrain Ahead.
2. **Preview:** the engine's own Terrain pass, which follows every installed worldgen mod.
3. **Accelerator:** fast, but a reimplementation.

How each source is used:

- **Captured heights** are free: every server capture path already holds the column (4.4).
- **Accelerators** are detected once per world by reflection.
  - One is used only if its P0.3 comparison is acceptable and the admin has not turned
    accelerators off.
  - A failure (exception, timeout, wrong type) disables it for the session, with a named log line.
    The builder then falls back to previews.
  - Calls run on a small worker pool, never the main thread. The pool size is
    `clamp(cores - 4, 1, 4)`, Farseer's choice, until measured.
- **Previews** are the standalone path.
  - One Terrain-pass peek per sampled chunk; the builder copies its 32x32 heights and skips
    `CaptureColumn` (A3).
  - Existing columns are never peeked. Their heights come from capture, or from a load only where
    Terrain Ahead is loading anyway.

### 4.3 The silhouette tile

- **Footprint:** 512x512 blocks: one L3 section footprint and one `LodWorldGenPlan` tile. Keyed
  by the packed L3 key, reusing `LodWorld` key math and neighbour helpers.
- **Cells:** 128x128 cells of 4 blocks. Each value is the unclamped worldgen terrain height
  (`ushort`), sampled at the cell's minimum corner (Farseer's sampling).
- **Cell state:** 2 bits per cell: unknown, captured, preview or accelerator.
  - A captured or previewed chunk fills its 8x8 cells; an accelerator sample fills one cell.
  - Unknown cells are filled when meshing, by a deterministic multi-resolution fill from the known
    cells (coarse averages pushed down, then interpolated).
  - The fill works across tile borders where the neighbour is present. Filled values are never
    stored.
- **Revision:** server-owned, incremented whenever known cells are added or replaced. Every
  asynchronous result carries tile key, revision and world epoch, and stale results are rejected
  (concurrency invariants).
- **Encoding (disk and wire):** format byte, cell states, known heights, deflated. A fully known
  tile is at most 36 KiB before compression.
- **Server store:**
  - a separate file, `ModData/vintagehorizons/<savegame>-silhouette-server.db`, with table
    `(TX, TZ, Revision, Data, ModifiedMs)`;
  - one connection, owned by a dedicated thread (G9), with batched writes;
  - a bounded RAM set of tiles near requesting players;
  - nothing shared with the section database.
- **Client store:** RAM only until P5, then `<savegame>-silhouette.db` for instant rejoin.

### 4.4 Height extraction from captures

- `LodPipeline` gains an optional server-only callback, in the same pattern as the existing
  `SnowStateCaptured` and `SectionPersisted` hooks that `LodServerCaptureSystem` sets.
  - It is invoked at the two capture-job creation sites, `ScheduleCaptures` and `CaptureColumn`.
  - It receives `(cx, cz, heights)`: 1,024 `ushort`s copied from `MapChunk.WorldGenTerrainHeightMap`.
- This covers every server capture path. Heights are a by-product; no extra worldgen.
  - chunk-load capture;
  - the sweep;
  - `/vhgen` and pregen loads and peeks;
  - `/vhrefresh`;
  - `/vhworldgen` (through `QueueColumn`, then `ScheduleCaptures`);
  - Terrain Ahead.
- The callback is attached only while the server-side silhouette feature resolves to enabled
  (`EnableSilhouettes`, 4.9). A server that does not use it pays nothing.
- The owning thread merges height batches into tiles FIFO, under a time and byte budget, with the
  one-item progress guarantee (settled decisions 12 and 14).
- A loaded old-save column may carry rain heights (2.1).
  - That reads the water surface rather than the seabed, so the seabed rule in 4.7 degrades to
    sea level there.
  - Report the count in `.vhsilhouette stats`.

### 4.5 The silhouette builder (server)

Hosted by `LodServerCaptureSystem`. It opens the server pipeline on demand, exactly as `/vhgen`
does (`OpenPipeline()`), so it works in singleplayer with the sweep off.

**Far in advance (D14).**
- **Target:** for each requesting player, the disc of (reach + margin) around them. The margin is
  one tile ring beyond the reach, so ordinary travel never reveals an unbuilt edge.
- **Start:** work begins as soon as the player's request arrives, nearest tile first, and continues
  in the background until the disc is complete.
- **Kept:** the store keeps the result, so each area is paid for once per world.
- **Movement:** as the player moves, the ring they gain is queued at once.
- **Idle:** a completed disc leaves the builder idle.

**Sampling plan, per tile:**

1. Take every captured height already in the store.
2. **Coarse pass**, with starting values for P0 to adjust:
   - with an accelerator, one sample per 64 blocks (64 per tile);
   - with previews only, one chunk in every 8x8 (4 per tile, 256-block spacing).
3. **Coast refinement (D12).** Wherever two neighbouring known samples straddle sea level, add
   samples between them and repeat. Stop at the source's finest useful spacing: 8 blocks for an
   accelerator, one chunk for previews.
4. **Island hint.** A land/sea hint catches islands smaller than the coarse spacing:
   - with an accelerator, a cheap sparse extra pass;
   - without one, the vanilla ocean map (`GenMaps.GetOceanMapGen`, one value per 32 blocks, no
     chunk generation; Appendix A). It is used only where it agrees with real samples on that
     world, and not at all where it disagrees, as on modded coasts. The server knows the world's
     creation version, so it can build the map exactly.
5. No uniform refinement passes (D8). Captured terrain replaces samples as the player explores.

**Scheduling rules**

- **Priority:** the nearest incomplete tile first, and the coarse pass before refinement, across
  all requesting players. Players take turns, and none exceeds a configured share.
- **Yield.** Hold new work in any of these cases (settled decision 39):
  - `EnumWorldGenAuthority` is not `None`;
  - `LodWorldGenGates.Select` returns anything but `None`;
  - `CurrentGeneratingChunkCount` exceeds a small yield threshold, set in P0, meaning real loads
    are pending.

  Terrain Ahead previews go first when both features run.
- **Budgets:**
  - previews through `LodTickAllowance` at `SilhouettePreviewsPerSecond`;
  - a separate in-flight cap, counted by the builder itself (2.1);
  - the capture-backlog gate;
  - at most 1 ms of owning-thread issue work per tick;
  - accelerator calls on the worker pool, under a time budget;
  - merges on the store thread.
- **Idle when unused:** with no requesting player, the builder registers no tick work at all.
- **The savegame promise is measured continuously.**
  - `LodAbsenceVerifier` periodically re-probes a sample of recently peeked absent positions away
    from players.
  - Any regrowth disables preview sampling for the session, with a named log line.
  - Accelerators and captured heights continue.

### 4.6 Delivery without a protocol bump

- **Feature bits.** `AssistWelcome.Features` bit 2 (`FeatureSilhouetteTiles`) says the server
  serves silhouettes; bit 1 (`FeatureFrontierGeneration`) says it accepts Terrain Ahead.
  - A client sends a feature's messages only when its bit is set.
  - A server sends tile messages only to players whose request arrived.
  - Old/new mixtures never exchange the new messages (2.4).
- **Silhouette message types**, appended after `AssistWorldGenStatus` in this order on both
  sides:
  - `AssistSilhouetteRequest` (client -> server): switch, reach, mod version.
    - Sent after the welcome and on change, at most once a second.
    - An off request is the last silhouette message the client sends until the player switches it
      back on.
  - `AssistSilhouetteManifest` (server -> client): tile keys with revisions, chunked with
    sequence/last, deltas after the first.
  - `AssistSilhouetteTileRequest` (client -> server): tile keys.
  - `AssistSilhouetteTile` (server -> client): key, revision, payload.
    - An empty payload is a refusal; `Retryable` marks "not yet".
    - Every accepted request is answered (G6, R4).
- **Terrain Ahead message.** `AssistTerrainAheadRequest` (client -> server) is appended after those
  four when Terrain Ahead lands (P6). It carries the switch, the lead distance, the applied first
  threshold and the mod version.
- **Reach clamp.** A dedicated server clamps the requested reach to `SilhouetteReachBlocks`; a
  singleplayer or LAN host does not (A7).
- **One tile path.** Tiles use the channel in singleplayer too (the handshake is in-process). A
  sibling-file reader like `LodLocalOfferSource` stays available if measurement shows the channel
  costs too much.
- **Serving** reads tiles from the server's RAM set. Misses go to the store thread, never to a
  main-thread SQLite read (the assist blob-reader pattern).
- **Documentation.** `WIRE_HISTORY.md` gains rows for the new message types at the documentation
  ritual. The protocol number stays 1 unless mixed-version testing proves otherwise.

### 4.7 Client silhouette set and mesher

- **Request producer:** the Silhouettes switch and the reach. Sent on change.
- **Silhouette set:** tile key -> (revision, decoded tile).
  - Bounded by reach.
  - Nearest-first requests under an in-flight cap.
  - Stale revisions and old world epochs are rejected.
- **Coverage:** a 64x64-block cell is covered when its L0 key is in `LodWorld.HasDataSet`, the
  same "has data" answer the renderer descends on. A tile's covered flags are recomputed when L0
  keys inside it are added or removed, rolling and bounded (settled decision 21).
- **Anchoring (D8).** Along the edge of covered ground, the mesher pulls filled heights toward the
  cached terrain's own edge heights, which the client already holds. A loose interior then still
  meets real terrain cleanly. At 60 degrees and 1,080 rows, a 20-block error costs about 2.6 px at
  8,000 blocks and about 41 px at 500 blocks, so the join is where looseness matters.
- **Mesher.** Arrays only, built on a worker thread and uploaded on the render thread within the
  existing completed-work budget (settled decisions 5 and 16).
  - Three resolutions per tile: 128, 32 and 8 cells per edge, chosen by on-screen cell size against
    the LOD target pixels. No fixed distances.
  - Uncovered cells clamp to sea level (Farseer's flat sea). Covered cells keep the unclamped
    height, so under water they lie beneath the cached seabed.
  - Edges stitch from neighbour tiles when present.
  - Cells next to covered cells sink a few blocks, so real terrain wins the depth test at the join
    (Farseer's sink).
  - Vertex = local x/z, height, covered flag.

### 4.8 Rendering and the handoff

- **Where it draws:** inside `LodTerrainRenderer`'s existing Opaque callback, after cached opaque
  terrain.
  - Water and OIT are unchanged.
  - Not a separate `IRenderer`: no second render-order registration and no second far-plane
    writer.
- **Per visible tile:** a frustum test on its box (real height span), one draw at the chosen
  resolution, and a camera-relative transform.
- **Shaders:** `lodsilhouette.vsh` and `lodsilhouette.fsh`, with a shared include
  `lodsilhouettecolor.fsh` holding the classic colour (D11). It consists of:
  - terrain colour from `getSkyColorAt`, offset by height relative to sea level;
  - a tint mix;
  - a sun-and-daylight light bias;
  - fog and sphere fog;
  - glow;
  - a fade to sky at the reach;
  - an optional globe curve, starting at the cached far edge.

  Shader rules:
  - ASCII only (G10).
  - Configured through `LodTerrainSamplerLayout` before the first `Use()`, because the sky and fog
    includes bind engine samplers (G130, G158).
  - The program is compiled only when silhouettes are first enabled (4.10).
  - Colour is computed per fragment at first. Colour per vertex is an option to measure in P5, if
    the pass is too expensive.
- **Coverage in the shader:** a covered cell's fragment is discarded inside the cached far edge
  minus the cross-fade band. Beyond the cached far edge, covered cells draw, because cached
  terrain does not draw there.
- **Handoff.** With silhouettes on, the cached terrain's existing far fade and explored-edge fade
  mix toward `silhouetteColor(worldPos)` instead of the sky colour. With silhouettes off, the
  cached shader's result is unchanged. At the join both sides paint the same colour, so there is no
  seam.
  - `lodsurfacefinish.fsh` is shared by the terrain and water programs. Every program that
    includes it must gain the new include and declare its uniforms (G161, G174).
- **Far plane:** projection distance = max(cached far extent, silhouette extent), through the
  existing stepped policy (G5). The cached shader's far edge stays the cached distance, so the
  accepted cached fade does not move.
- **Failure:** a compile or draw failure disables silhouettes for the session, with a named log
  line. Nothing plausible is drawn in their place (settled decision 26, G160).
- **Distant anti-aliasing:** silhouette pixels are drawn after the pre-cached depth snapshot, so
  they fall under settled decision 34. Verify in P4; do not assume.
- **Deferral** to another LOD mod is unchanged: if one draws, this mod is idle, silhouettes
  included (settled decision 10).

### 4.9 Settings and commands

**General tab, distance scale (D10).** Two handles join the Vanilla and L1-L6 handles on the
scale:

- **Draw distance.** The cached-terrain draw distance (`FarViewDistanceCap`) becomes a handle, with
  today's slider range and step (1,024-32,768, 512 blocks).
  - Levels beyond it are dimmed, because they never render.
  - Whether today's slider row stays as a readout is settled on owner screenshots, as the scale
    itself was (Session 88).
- **Silhouettes.** The reach: default 8,192, up to the scale's end (32,768), in 512-block steps.
  - A haze-coloured band runs from the draw-distance handle to it.
  - Hidden when silhouettes are off.
  - Colours follow G184's one-palette rule.

**Silhouettes tab** (`vintagehorizons.json`):

- **Horizon silhouettes** on/off (default on). Off removes the feature (4.10). It does nothing
  unless the server allows it.
- **Reach:** the same value as the scale handle.
- **Look dials,** in the style of the anti-aliasing tab: tint colour and strength, sky tint, light
  bias, fade bias, globe curve. Defaults are Farseer 1.4.0's (D11) until the owner tunes them.
- **Status line:** server availability, tiles held, fill ratio, active height source.

**Terrain Ahead tab** (separate, D15):

- **Terrain ahead** on/off (default on). Off removes the feature (4.10). It does nothing unless
  the server allows it.
- **Status line:** server availability, columns generated this session, whether it is yielding.

**Both tabs:**
- The Performance preset switches both features off; Balanced and Quality switch both on (A6).
- The tab strip grows to five tabs; check its width (2.5).

**Commands** (session-only switches plus log dumps, because chat cannot be copied):
`.vhsilhouette [on|off|log|stats]` and `.vhterrainahead [on|off|log]`.

**Server** (`vintagehorizons-server.json`, sanitized, mirrored append-only into
`AssistServerSettings`):

| Setting | Default | Meaning |
|---|---|---|
| `EnableSilhouettes` | `null` | `null` = automatic: on for singleplayer/LAN host, off for dedicated. `true`/`false` override. Off also detaches height extraction. |
| `SilhouetteReachBlocks` | 8,192 | Ceiling on player-requested reach on a dedicated server (A7). |
| `SilhouetteUseAccelerators` | `true` | Use an installed accelerator mod when its comparison passed (4.2). |
| `SilhouettePreviewsPerSecond` | 16 | Preview issue rate; the engine still saturates near 23/s. |
| `EnableTerrainAhead` | `null` | As `EnableSilhouettes`, for Terrain Ahead. |
| `TerrainAheadColumnsPerSecond` | 16 | Corridor issue rate. |
| `TerrainAheadSectorDegrees` | 90 | Corridor width as an angle around the heading, so it scales with the band. |
| `TerrainAheadDenseReachBlocks` | 0 | 0 = corridor only. Larger values also densify all round (disk cost, section 5). |

**Server commands** (`controlserver` privilege): `/vhsilhouette [status|pause|resume]` and
`/vhterrainahead [status|pause|resume]`. Status reports, in chat and the log:
- tiles and fill ratio;
- sources;
- disk used;
- rates and yields;
- verifier verdicts.

### 4.10 Switching off (D7, per feature)

"Off" must deliver the saving, so every cost a feature adds has a named off state. Switching off
takes effect at once, without a restart.

| Where | Silhouettes off | Terrain Ahead off |
|---|---|---|
| Client network | One final off request, then nothing: no tile requests, no manifests processed. | One final off request, then nothing. |
| Client memory | The silhouette set is cleared; no tile data is retained. | Nothing held. |
| Client workers | No mesh jobs are queued; queued ones are dropped by revision. | None. |
| GPU | Tile buffers are released through the renderer's normal bounded disposal path (as `.vhgpu off` releases its arenas). | None. |
| Render pass | Returns before touching any GL state. | Unaffected. |
| Shaders | The program is not compiled until first enabled. The cached fade takes its existing sky path, identical to today. | Unaffected. |
| Far plane | The silhouette extent contributes nothing. | Unaffected. |
| Coverage tracking | Not run. | Not applicable. |
| Scale | The Silhouettes handle and band are hidden. | Unaffected. |
| Server | The player's demand is dropped immediately; with no requesting player the builder registers no tick work. `EnableSilhouettes = false` also detaches height extraction. Tiles on disk stay. | The player's demand is dropped immediately; with no requesting player the planner registers no tick work. |
| Section cache | Unaffected. | Terrain it already generated stays; it is ordinary cached terrain. |

**Evidence required (P4 and P6):**
- With a feature off, a fixed sandbox route measures the same frame time and server tick time as
  the same build without that feature, within run-to-run spread.
- Quote the spread (G98).
- Use a deterministic scenario rather than a noisy cross-check.

### 4.11 Terrain Ahead (separate feature, P6)

Hosted by `LodServerCaptureSystem`, opening the pipeline on demand like the builder.

**Per requesting player:**
- the latest request: switch, lead distance, applied first threshold;
- a session identity, so a completed result cannot cross a reconnect (as in the assist);
- a travel estimator.

A player whose request says "off", or who disconnects, is removed at once, and their queued demand
is dropped.

**Travel estimator**

- Samples the player position every 2 s. Heading and speed come from displacement across a 30 s
  window. Boats and mounts need nothing special: the player moves with them.
- Below a minimum speed the player is stationary, and the corridor is the whole ring.
- A single-sample jump larger than the window can explain (teleport, death) resets the window.
- Camera direction is never an input (ARCHITECTURE demand rule; G93).

**Demand, highest priority first**

1. **Corridor (dense).** Chunk columns in the level-0 band.
   - **Extent:** from vanilla's view distance out to the client's first LOD threshold, plus a
     latency margin (speed times measured offer-to-install latency).
   - **Shape:** when travelling, a sector of `TerrainAheadSectorDegrees` around the heading; when
     stationary, the whole ring.
   - **Order:** by deadline, meaning when the player's band will reach the column at current speed.
   - **Band edge:** comes from the client's automatic thresholds, so it follows FOV, resolution and
     view distance (settled decision 35), never a fixed distance.
2. **Dense breadth (optional).** Nearest first out to `TerrainAheadDenseReachBlocks` (0 by default,
   A2).

**Scheduling rules**

- Queue the ground a moving window gains at the moment it gains it. A cursor covers the interior at
  its own pace. Budget by elapsed time (G37).
- Probe existence ahead with `TestMapChunkExists` into a `LodColumnMap`, budgeting the 4-chunk
  safety border (G22).
- Act through `Classify`: absent -> peek; present with an intact neighbourhood -> load;
  frontier -> skip.
- The same yields as the builder (authority, gates, real loads; settled decision 39), the same
  continuous absence verification, and idle when unused.
- **A column is fully captured if and only if it lies inside the dense area when its peek is
  issued.** Isolated captured columns would draw as a dotted grid (2.5). Its heights also feed the
  silhouette store (4.4) when silhouettes are enabled on the server.

**Delivery.** Terrain Ahead sections use the existing section path.
- For Terrain Ahead players, the server keeps an incremental queue of new keys, fed from
  `LodWorld.RegisterInTree`. It offers them every few seconds (target at most 5 s, from P0),
  instead of copying and diffing the whole key set.
- The queue lives in `ManifestLedger`, where it also helps Generate World on dedicated servers.
- Other players keep the 30 s cadence. Singleplayer already scans by rowid.

## 5. Cost model

Estimates unless marked measured. Peek times assume the recorded ceiling of about 23 columns/s;
accelerator times assume the author's 1-2 ms per sample.

| Item | Figure |
|---|---|
| Silhouette coarse pass, previews, 256-block spacing to 8,192 | ~3,200 peeks, ~2.3 min at saturation, longer while yielding |
| Silhouette coarse pass, previews, 128-block spacing to 8,192 | ~12,900 peeks, ~9.3 min |
| Silhouette coarse pass, accelerator, 64-block spacing to 8,192 | ~51,500 samples, ~13-26 s on 4 worker threads; no worldgen thread time |
| Same, accelerator, 32-block spacing | ~206,000 samples, ~1-1.7 min on 4 worker threads |
| Coast refinement | Unknown until P0/P5; proportional to coastline length, not area |
| Reach 16,384 instead of 8,192 | 4x every silhouette figure above |
| Server disk, silhouette | ~804 tiles to 8,192 reach, at most 36 KiB each before compression: at most ~29 MiB fully known, far less when sparse |
| Terrain Ahead corridor, sustained | band width x speed / 1,024 columns/s. A 90-degree sector at a 624-block band edge is ~880 blocks wide: ~4.3/s at 5 blocks/s, ~8.6/s at 10 blocks/s |
| Terrain Ahead, standing start | ring from 256 to 624 blocks (owner's settings) ~990 columns, ~45 s |
| Server disk, Terrain Ahead (**measured basis**: 40.5-78.4 KiB x 1.53 per L0 section) | ~13-25 MiB per km of corridor travel; ~0.8-1.5 GiB per 4,096-block dense disc (optional) |
| Peek memory | at most the in-flight cap x 1-2 MB |
| Network | tiles are small; corridor ~1-3 new L0 sections/s per travelling player plus ancestors, under the 8/s per-player cap |
| Client GPU | unknown until P4; the budget is agreed there against the owner's scene |
| Feature off | intended zero; measured in P4 and P6 (4.10) |

## 6. Phases

Each phase is independently measurable. Every playable test build follows the version rule in
`CLAUDE.md`. Every game run uses the isolation scripts (G11) and happens only after asking.

### P0 - Engine facts and source measurements

Dev-only logging; no player-visible change.

- **0.1** On the first peek per session, log how many of its 1,024 `WorldGenTerrainHeightMap`
  values are zero. Source-tracing says they are populated with the owner's mods; this confirms it
  at runtime. Also record whether `MapChunk.MapRegion.ForestMap` is present (P7 input).
- **0.2** Throughput of sparse preview lattices at the builder's in-flight cap:
  - columns/s;
  - issue-to-callback p50/p95;
  - server tick p95;
  - retained memory;
  - the effect on vanilla chunk arrival while flying.
- **0.3** Accelerator comparison. It needs the accelerator installed by the owner; ask first.
  - Heights from Algernon's Terrain Sampler and from Watersheds' sampler, against peeked
    `WorldGenTerrainHeightMap` on the same points: coast, river, mountain, TerraPrety coast.
  - Time per 1,024 samples on 1.22.7.
  - The owner judges the tolerance by eye on a side-by-side (D8).
  - Status (Session 94): run twice on isolated test servers against captured heights, with
    `scripts/silhouette-compare.ps1`, at about 10-20 ms per 1,024 samples. Still to do: mountains,
    rivers, land nobody has generated, and the side-by-side.
- **0.4** Dedicated server: time from a section's persistence to the client's install at the 30 s
  cadence (Terrain Ahead input).

**Gate:** numbers recorded in the session record, and P1-P3 parameters set from them.

### P1 - First look (rough slice)

**Deliver:**
- Singleplayer only, behind a session command (`.vhsilhouette on`), default off.
- The client sends `AssistSilhouetteRequest`.
- The server previews a coarse lattice around the player and pushes minimal tiles.
- The client meshes one resolution on a worker and draws it with the classic colour.
- Message types are appended in their final order (4.6).
- Not yet: handoff blending, coverage refinement, settings, persistence or accelerators.

**Checks:** message registration order is identical on both sides; the request is ignored without
Features bit 2; sparse columns never reach `CaptureColumn`.

**Gate (human-tested):** the owner judges the look at sea and over unexplored land. Changes to the
look are decided here, before the rest is built around it.

### P2 - Tile model, store and captured heights

**Deliver:**
- the tile type (no game types), with encode/decode, the deterministic fill, stitching and
  revision rules;
- the server store thread and file;
- height extraction from every capture path, attached only while the server feature is enabled;
- `/vhsilhouette status`.

**Checks:**
- round trip;
- rejection of corrupt and partial payloads;
- deterministic fill and stitching fixtures;
- origin replacement order;
- revision monotonicity;
- the cell sampling offset against a hand-computed column;
- extraction is detached when `EnableSilhouettes` resolves off.

**Gate:** harness-tested; integration-tested by a `/vhworldgen` run whose columns appear in tiles.

### P3 - The builder and tile transport

**Deliver:**
- the far-in-advance builder (4.5), with previews, the coast refinement and the land/sea hint;
- accelerator adapters, behind P0.3's verdict;
- Features bit 2;
- the manifest, tile-request and tile messages;
- the client silhouette set (RAM);
- `.vhsilhouette log|stats`.

**Checks:**
- every request is answered (success, refusal or retryable);
- in-flight caps;
- stale-revision and old-epoch rejection;
- reach clamping only on dedicated servers;
- manifest chunking;
- nearest-first order and the margin ring;
- refinement terminates and stays within its spacing floor on fixture coastlines;
- an accelerator exception falls back to previews;
- "Silhouettes" off clears the set and sends no tile requests;
- the builder yields to each authority value and gate.

**Gate:** integration-tested in singleplayer and a dedicated sandbox. The disc to the requested
reach arrives complete, and the log reports counts, sources and fill ratio.

### P4 - Drawing, the handoff and settings

**Deliver:**
- the mesher: three resolutions, coverage, sea rule, stitching, sink, anchoring;
- the draw pass;
- shaders and sampler layout, with lazy compilation;
- the fade-target switch in the cached shader;
- the combined far projection;
- the scale's draw-distance and Silhouettes handles;
- the Silhouettes tab and preset wiring;
- the silhouette off states from 4.10.

**Checks:**
- shader ASCII and sampler configuration (`StaticAssetChecks`);
- every uniform declared where it is uploaded;
- with silhouettes off, the cached fade expression is unchanged and the silhouette program is never
  compiled;
- GPU tile buffers are released on switch-off;
- the coverage and discard rule as a pure function;
- resolution choice by on-screen size;
- the projection policy does not reset on ordinary movement (G5);
- scale handles map to the right values and colours (G184).

**Gate (human-tested; small, specific observations):**

- a. At sea with no land inside the draw distance, silhouettes show toward land you know is there.
- b. On land, a silhouette fills an unexplored gap between explored areas.
- c. No visible line where cached terrain meets the silhouette, at the far edge or at the edge of
  explored ground.
- d. Night, rain or fog, and winter.
- e. With silhouettes off, cached terrain looks exactly as before (A/B screenshots), and frame
  time matches the same build without the feature (4.10).
- f. The silhouette pass's GPU time in the log, against a budget agreed with the owner.
- g. The scale reads correctly with both new handles.

**Fallback if (c) cannot be met:** the other D1 option, silhouettes only beyond the draw distance.
That is an owner decision, not a quiet change.

### P5 - Tuning and persistence

**Deliver:**
- islands and inlets measured against real terrain on the owner's world (a dense `/vhworldgen`
  area compared with its silhouette), with refinement tuned from that;
- reach and dials tuned by the owner;
- client tile persistence for instant rejoin, not opened while silhouettes are off.

Only if P4 measured a need:
- colour per vertex;
- a GPU path: one shared grid mesh per resolution, with per-tile height textures and multi-draw;
- coverage finer than 64 blocks.

### P6 - Terrain Ahead (separate feature)

**Deliver:**
- `AssistTerrainAheadRequest` and Features bit 1;
- the Terrain Ahead tab and switch;
- the planner (4.11), with its travel estimator, corridor, optional breadth, yields, continuous
  absence verification and the automatic rule;
- the incremental new-key queue in `ManifestLedger`;
- `/vhterrainahead`;
- its off states from 4.10.

**Checks** (fast tier, pure where possible):
- the travel estimator: still, heading, turn, teleport reset;
- deadline ordering;
- `Classify` authority: an existing column is never peeked and a frontier column never loaded,
  extending `GenerateChecks`;
- budget arithmetic;
- config sanitize and the automatic rule;
- an off request drops the player's demand, and an idle planner registers no tick work;
- the incremental offer queue equals the full diff on a fixture;
- mixed versions exchange no new messages;
- with both features on, corridor previews are issued before silhouette previews.

**Gate (human-tested, singleplayer, then a dedicated sandbox):** travel on foot and by boat into
never-generated land. Then check:
- Bare cached terrain appears in the level-0 band ahead before vanilla chunks arrive.
- Switching it off mid-journey stops generation in the server log within a second or two.
- The log shows absence verdicts with no regrowth.
- Server tick p95 stays within the P0 budget.
- The owner reports no stutter.
- Off cost is measured per 4.10.

### P7 - Statistical canopy (separate acceptance, D3)

**Deliver:**
- a per-column "generated" fact, as a third append-only format-4 tail. It is cleared by real
  capture and propagated with the two-fact discipline (G167), with a mip rule chosen on a fixture;
- forest density from the peeked column's map region (P0.1);
- canopy geometry on generated columns only, with deterministic placement and a leaf palette entry
  so seasonal tint applies unchanged;
- a switch point keyed to on-screen tree size and tuned by the owner;
- its own switch under the Terrain Ahead tab.

Not started before P6 is accepted.

## 7. Risks and unknowns

| Risk | Retired or bounded by |
|---|---|
| Heights missing on peeks | P0.1 runtime line; rain-map fallback (4.4) |
| Previews slow the first fill and vanilla chunk arrival | yields (4.5); P0.2; accelerators where installed |
| An accelerator disagrees with real terrain (TerraPrety coasts, 1.22.7 changes) | P0.3 comparison; origin order lets previews and captures replace it; admin switch |
| An accelerator throws or is not thread-safe | session disable with a named log line; worker-pool isolation; preview fallback |
| Small islands lost between samples | coast refinement and land/sea hint (4.5); measured in P5 |
| Offer latency makes the corridor late | P0.4; incremental queue; latency in the deadline margin |
| Disk growth on busy servers | dense reach 0 by default; opt-in on dedicated; status reports disk |
| "Off" still costs something | the 4.10 table; A/B in P4 and P6 |
| 64-block coverage leaves gaps at explored edges | P4 observation; P5 refinement |
| Silhouette plane visible through cached water in the fade band | covered cells keep the seabed height (4.7); P4 observation |
| Silhouette pass too expensive at long reach | culling and on-screen resolution; P4 budget; P5 GPU path |
| Many players multiply the server cost | per-player share; admin opt-in; caps |
| Engine change to peek areas or message numbering | fast-tier checks pin what can be pinned; a mixed-version run each release |
| Map reveal on servers | admin opt-in; reach ceiling on dedicated servers |
| Modded worldgen | previews are the server's own worldgen, so they follow it; accelerators are compared first; the absence verifier still runs |
| An admin `/vhworldgen` pauses both features | by design (settled decision 39); stated in both status commands |

## 8. Out of scope

- The client-only coastline (D4; Appendix A).
- Real trees through the vegetation stage (about 25x per column; D3).
- Keeping silhouette samples' blocks in a holding store.
- Uniform stride-2 and stride-1 heights-only refinement (D8). Refinement is coast-only.
- Requiring any other mod (D13).
- Deleting already-generated terrain when a feature is switched off. It is ordinary cached terrain
  (4.10).
- Any change to terrain ownership or the vanilla handoff (settled decision 28).
- Cooperating with Farseer. This mod still defers to it when it draws.

## 9. Settled decisions this plan must respect

- **1, 2** - Vanilla-server compatibility and additive assist: nothing new runs unless the server
  has the mod and advertises the feature.
- **5** - OpenGL on the render thread: tile meshes are worker-built arrays.
- **6** - Parent covers until children: the voxel cache is untouched; the silhouette is a separate
  layer.
- **7, 13** - Local observed data wins: Terrain Ahead sections arrive as foreign data, and local
  capture replaces them.
- **8, 9, 38** - The sweep generates nothing, transient generation persists nothing, and only
  `/vhworldgen` writes terrain. Both features use peeks or loads through `Classify`, and are
  measured by the absence verifier.
- **10** - Deferral to competing LOD mods is unchanged.
- **11, 12, 14, 16, 21** - Evidence-driven, time/byte-budgeted, progress-guaranteed, rolling work.
- **26** - The silhouette fails visibly, never plausibly.
- **28** - No change to terrain ownership.
- **34** - Distant anti-aliasing interaction is verified, not assumed.
- **35** - The corridor edge and tile resolutions follow the screen.
- **37** - Settings has one editor. The new tabs and scale handles live in it, and each setting
  keeps its real owner.
- **39** - Both features yield; neither ever holds the generation authority.

## 10. Files expected to change

Orientation, not a mandate.

**New:**
- `src/Silhouette/`: `LodSilhouetteTile.cs`, `LodSilhouetteStore.cs`, `LodSilhouetteBuilder.cs`,
  `LodHeightSources.cs` (captured, preview and accelerator adapters), `LodSilhouetteSet.cs`,
  `LodSilhouetteMesher.cs`.
- `src/Net/`: `LodTerrainAheadPlanner.cs` and `LodTravelEstimator.cs` (P6).
- `src/Render/LodTerrainRenderer.Silhouette.cs`.
- Shaders: `assets/vintagehorizons/shaders/lodsilhouette.vsh` and `.fsh`, and
  `shaderincludes/lodsilhouettecolor.fsh`.
- Checks: `SilhouetteTileChecks`, `SilhouetteBuilderChecks`, `HeightSourceChecks`,
  `SilhouetteMesherChecks`, `SilhouetteProtocolChecks`, `TerrainAheadPlannerChecks`,
  `TravelEstimatorChecks`.

**Changed:**
- `LodAssistProtocol.cs`, `LodAssistClient.cs`, `LodAssistServerSystem.cs`.
- `LodServerCaptureSystem.cs`, `LodServerConfig.cs`.
- `LodPipeline.cs` (height callback).
- `VintageHorizonsModSystem.cs` (config, commands, request producers).
- `VintageHorizonsConfigDialog.cs` (Silhouettes and Terrain Ahead tabs, scale wiring).
- `LodThresholdScaleElement.cs` (draw-distance and Silhouettes handles).
- `LodVisualSettings.cs` (presets).
- `LodTerrainRenderer.cs` (pass, projection, lazy program) and `LodFarDistance.cs` (combined
  extent).
- `lodsurfacefinish.fsh` and every program including it (fade target); `LodTerrainSamplerLayout.cs`.
- `StaticAssetChecks.cs`, `ConfigChecks.cs`, `tests/.../Program.cs`.
- For P6 also: `ManifestLedger.cs` (incremental queue), `LodWorld.cs` (`RegisterInTree` hook),
  `LodPlayerPregen.cs` and `GenerateChecks.cs`.

## 11. Documentation at completion

Only when the owner asks for the documentation ritual:
- `WIRE_HISTORY.md` rows: the new message types, and the Features bits in use.
- `ARCHITECTURE.md` settled decisions for:
  - silhouette sources and their origin order;
  - silhouette ownership and the handoff;
  - the two independent features;
  - the off-means-no-work rule.
- README: commands, settings and accelerator credits.
- GOTCHAS, as traps are proven.
- STATUS, TODO, CHANGELOG and the session record, per the ritual.

## 12. Attribution

- **Farseer** (MIT, (c) 2026 Badgerson): the tile representation and the silhouette shader terms
  are adapted from it. Credit goes in the shader headers and in README Credits, which already lists
  Farseer.
- **Algernon's Terrain Sampler and Watersheds** are called, not copied. README credits them as
  optional accelerators if P3 ships their adapters.
- **TopoHorizon's** peek constants are already credited in `LodPlayerPregen`.

## Appendix A - Recorded for the later coastline plan (D4)

Source-traced 2026-09-06 and 2026-09-12, not runtime-verified:

- The ocean map is generated in the map-region pass, at one sample per 32 blocks
  (`TerraGenConfig.oceanMapScale` = 32), with no chunk generation.
- `GenMaps.GetOceanMapGen(seed + 1873, landcover, 32, oceanscale, requireLandAt,
  requiresSpawnOffset)` is public static and returns `MapLayerBase`.
  - `MapLayerOceans` itself is internal.
  - `XZ` is a public struct in `VSEssentials`, which the project already references.
- Every client receives the world seed (`Packet_ServerIdentification.Seed` -> `capi.World.Seed`)
  and the same `WorldConfiguration` tree `GenMaps` reads (`capi.World.Config`: `landcover`,
  `oceanscale`).
- `requireLandAt`, for worlds created on 1.20.0-pre.14 or later, comes from
  `ForceRandomLandArea`, which uses only the seed, map size, region size and chunk size.
- `CreatedGameVersion` is **not** sent to clients.
  - So `requiresSpawnOffset`, which shifts the whole field for older worlds, is unknown on the
    client.
  - Resolve it by comparing both candidates against map regions the client really holds (measured
    client reach: 512-1,024 blocks, partial to 3,072).
  - The same comparison is the guard against modded worldgen.
  - The server knows the creation version, which is why the silhouette builder's land/sea hint
    (4.5) can build the map exactly.
- It gives land/sea only. The landform generator needs `ICoreServerAPI`, so client-side height is
  not available this way.
- The coastline would reuse this plan's silhouette renderer and fall under the Silhouettes switch.
