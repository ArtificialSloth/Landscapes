# Plan - Horizon silhouettes and the generation frontier

**Status:** Proposed, not started. Queued by owner direction until the water work is finished; the
owner decides when it starts. Written from a report-only investigation on 2026-09-12 against 0.4.64
(`dd0394c`). No source was changed.
**Created:** 2026-09-12
**Scope:** Server-side automatic generation around travelling players (a dense *frontier* through
the level-0 band ahead, and sparse height sampling far out); a new *silhouette tile* data type with
its own store and transport; a Farseer-style silhouette pass inside the existing renderer; one
master switch that removes all of it. Section blob format 4, database schema 6 and assist protocol 1
stay unchanged. Behaviour against servers without the mod stays unchanged.

## 0. In one paragraph

Two features built on one engine call. As you travel into land nobody has generated, the server
runs the world generator for the ground just ahead of you, keeps every block of it, and turns it into
ordinary cached terrain before you arrive (bare of trees, as `/vhgen` terrain is today). At the same
time it samples the ground far more sparsely in every direction and turns those samples into a
Farseer-style silhouette, drawn wherever real cached terrain is missing, so from a boat you can see
which way the land is. Real cached terrain always wins where it exists, and the two blend at the
join. It needs the mod on the server (singleplayer counts). It is on by default in singleplayer and
off on dedicated servers until the admin enables it. One switch in `.vhconfig` turns the whole thing
off, and off means the work stops, not merely that it is hidden.

## 1. Goal and decisions

### 1.1 Goal

1. **Navigation.** Show the outline of distant land beyond everything cached terrain draws, on sea
   and on land, before the player has chosen a direction.
2. **Terrain ahead.** Keep the complete columns the generator produces and use them to build real
   cached terrain in the level-0 band ahead of the travelling player.

### 1.2 Owner decisions (binding)

| ID | Date | Decision |
|---|---|---|
| D1 | 2026-09-12 | Silhouettes draw **wherever real cached terrain is missing**: unexplored ground close by, gaps between explored areas, and beyond the cached-terrain draw distance. Real cached terrain always wins; the two cross-fade. |
| D2 | 2026-09-12 | Automatic generation is **on by default in singleplayer/LAN-hosted worlds, opt-in on dedicated servers**, and switchable in `.vhconfig` (player) and the server config (admin). |
| D3 | 2026-09-12 | Generated ground stays **bare in the first delivery**. A statistical canopy is a later phase with its own acceptance. |
| D4 | 2026-09-12 | The client-only coastline for servers without the mod is a **separate, later plan**. Facts gathered for it are in Appendix A. |
| D5 | 2026-09-06, restated 2026-09-12 | The frontier fills the **level-0 band ahead** of the player and keeps the full terrain it generates. |
| D6 | 2026-09-06 | Farseer's renderer is not ported as its own system. Its data role and its look are. |
| D7 | 2026-09-12 | **The whole component can be switched off by the player, for the performance saving.** Off means no client work, no RAM held, no GPU resources, no shader cost, and no server work on that player's behalf (section 4.10). |

Standing rules that apply throughout: no hard-coded rendering distances (key rendering to on-screen
size, pixel ownership, or the player's own settings); ask before any game launch; diagnostics must
reach the log, on demand.

### 1.3 Working assumptions (the owner may override any of these)

- **A1 - One master switch, two finer ones beneath it.** The master removes the component (D7).
  Under it, "Silhouettes" (sparse sampling and drawing) and "Terrain ahead" (the dense corridor) let
  a player keep one half without the other. Both halves use server worldgen; they differ in what it
  is for.
- **A2 - The frontier densifies only the level-0 band by default** (ahead when travelling, all round
  when stationary). Densifying the whole draw distance is a server setting
  (`FrontierDenseReachBlocks`, default 0) because it costs roughly 0.8-1.5 GB of server cache per
  4,096-block disc (section 5).
- **A3 - Sparse samples keep heights only** (section 4.2). "Keep everything" applies wherever the
  cached terrain can use it.
- **A4 - Default silhouette reach is 8,192 blocks**, a player setting clamped by the server. Farseer's
  default total reach is 4,096.
- **A5 - Names are placeholders:** *silhouette* for the height-sampled drawing, *frontier* for
  automatic generation, and a "Silhouettes" settings tab.
- **A6 - The Performance preset in `.vhconfig` switches the component off;** Balanced and Quality
  leave it on. This follows D7's stated motive.

## 2. Source-traced starting point

Everything here is **source-traced** against 0.4.64 unless marked **measured**. Engine facts come
from decompiled `VintagestoryLib` 1.22.x and `VSEssentials`.

### 2.1 Transient generation already exists

- `LodPlayerPregen` walks probe -> work -> verify. `EnumPurpose { Generate, RefreshExisting }` and
  `SelectAction` decide per position. `LodColumnMap.Classify` returns `Peek` (absent), `Load` (present
  with a complete 4-chunk neighbourhood) or `SkipFrontier`. An existing column is never peeked.
- Peeks run to `EnumWorldGenPass.Terrain` and are captured through `LodPipeline.CaptureColumn`, which
  records `SnowStateObserved = false`. Caps: `GenerateColumnsPerSecond` 16, `GenerateMaxInFlight` 64
  (1-2 MB held per landed peek), the capture-backlog gate, a 1 ms per-tick issue budget, a 300 s
  stuck-peek timeout. The engine saturates near 23 columns/s (recorded).
- Engine `ServerSystemSupplyChunks.PeekChunkAreaLocking` generates a square of side
  `2 * (min(pass, 5) - 1) + 1` for the requested pass: Terrain 1x1, TerrainFeatures 3x3,
  **Vegetation 5x5**, Done 9x9. The 1x1 figure matches the observation already recorded in
  `LodPlayerPregen`. This is why real trees cost about 25x per column.
- Before generating, the peek builds the map region transiently (`GetOrCreateMapRegionNoStore`), so
  a peeked column's `MapChunk.MapRegion` should carry forest and climate maps. Only P6 needs this;
  P0 confirms it.
- `LodAbsenceVerifier` re-probes a sample of promised-absent positions away from players, so "a
  peek persists nothing" is measured, not trusted.

### 2.2 Delivery already reaches connected players

- Server `OfferNewKeys` runs every 30 s. It calls `SnapshotKeys()` (`HasDataSet.ToArray()`), and
  `ManifestLedger.Delta` then iterates every key: **O(total keys) per offer**. `OfferUpdatedKeys`
  sends `Refresh = true` manifests for changed durable L0 rows.
- The client merges every manifest it receives (`LodAssistClient.PumpCore`), so a later manifest is
  already a delta.
- Singleplayer sections reach the client through `LodLocalOfferSource`, a read-only sibling-database
  reader that scans every 2 s. The assist handshake also completes in-process (`Greet()` runs at
  LevelFinalize; DESIGN section 10.11).
- New keys enter the server's tree in one place: `LodWorld.RegisterInTree`.

### 2.3 The channel tolerates appended messages

- Five message types are registered in the same order on both sides. The engine numbers types by
  registration order (`nextHandlerId++`) and performs no cross-check at handshake. The server
  ignores a message id beyond its handler table (`OnPacket` bounds check).
- Append-only fields have already been added three times without a protocol bump (`Retryable`,
  `Refresh`, `ServerSettings`).

### 2.4 Rendering facts that shape the design

- `CollectDrawNodes` lets a parent stop drawing once every visible child that is **present in
  `HasDataSet`** is covered; absent children are skipped. Mips of sparse real data stay sparse: a
  one-in-sixteen chunk lattice is 1/16 covered at L5 and at most 1/4 at L6. **Sparse real capture
  therefore draws as a dotted grid.** A continuous picture from sparse samples needs interpolated
  heights, which the voxel cache does not do. This is the reason for a separate tile type.
- The far edge: `lodsurfacefinish.fsh` mixes terrain toward `getSkyColorAt(...)` by
  `max(smoothstep(0.75, 1.0, dist), edgeFade)`, covering both the far edge and the edges of explored
  ground. The sky and fog includes a silhouette needs are already in use.
- `EffectiveFarDistance = min(farthest mesh extent, FarViewDistanceCap)`, never below vanilla plus a
  margin. The projection grows immediately and shrinks only after 5 s, in 512-block steps (G5).
- All terrain programs share one fixed sampler layout (`LodTerrainSamplerLayout`), configured before
  the first `Use()` (G130, G158).
- Harmony already ships (settings tab, vanilla horizon effects).
- `.vhconfig` already has a saved master-switch precedent: `BlendPipeline` controls several passes
  and releases their resources when off.

### 2.5 Storage

- Blob format 4 carries two append-only 512-byte tails (snow provenance, snow coverage) that old
  readers ignore. That is the template for any future per-column fact (G167).
- **Measured 2026-09-12**, read-only, from two existing server caches: L0 sections average
  **40.5 KiB** (5,342 sections) and **78.4 KiB** (1,262). In both, all levels together are **1.53x**
  the L0 bytes. These are mixed swept/captured caches from August, not peek-only terrain.

## 3. What is taken from Farseer, and what is not

**Taken** (Farseer 1.4.0, MIT, (c) 2026 Badgerson): heightmap tiles per 512-block region at 4-block
cells; heights read from the column's `WorldGenTerrainHeightMap`; a flat sea by clamping to sea
level; stitching tile edges from neighbour tiles; the sky-derived terrain colour, tint, light-bias,
fade and globe-curve shader terms; sinking the silhouette near real terrain; a player-facing on/off
switch.

**Not taken:** its own `IRenderer` and direct `MainCamera.ZFar` writes (a second far-plane owner, G5);
mesh builds on the main thread inside the network handler; drawing every loaded region with no
culling (about 254 draws and 8.3M triangles at its 4,096 default); issuing a region's 256 peeks at
once; requiring the mod on every client; peeking every column even for coarse sampling.

## 4. Design

### 4.1 Overview

```text
client: master + two switches, applied first LOD threshold, silhouette reach
   -> AssistSilhouetteRequest (appended message, capability-gated)

server FrontierPlanner (one per world; state per requesting player)
   travel estimator (displacement, never the camera)
   demand classes -> deadline / nearest-first order
   shared worldgen authority (/vhgen, pregen), engine yield, tick budget
   existence probe (TestMapChunkExists) -> LodColumnMap.Classify
      dense  peek -> CaptureColumn (full blocks) ----+--> ordinary sections
      dense  load -> ordinary capture path ----------+      -> incremental key offers (MP)
      sparse peek -> heights only                    |      -> sibling scan (SP)
   every server capture path -> height extraction --+--> SilhouetteStore (own file)
                                                         -> tile manifest / request / tile

client SilhouetteSet (RAM) -> worker mesher (3 resolutions) -> render-thread upload
renderer Opaque callback: cached opaque terrain -> silhouette pass (coverage-cut)
cached far/edge fade -> mixes toward the silhouette colour when silhouettes are on
```

### 4.2 Two kinds of generated column

- **Dense:** inside the frontier's dense area. Peeked (or loaded) and captured through the existing
  pipeline into ordinary sections, persisted, offered, recoloured on install. Its heights also go to
  the silhouette store.
- **Sparse:** silhouette sampling outside the dense area. Peeked; only the 32x32 heights are kept;
  `CaptureColumn` is not called.

**Rule: a column is fully captured if and only if it lies inside the dense area when its peek is
issued.** Isolated captured columns would draw as a dotted grid (2.4). The cost is that when a dense
area later covers a sparse sample, that column is peeked again. The stride-4 and stride-2 passes
sample 4/16 of all columns, so ground that later becomes dense pays at most 25% extra generation;
ground that stays silhouette-only pays nothing extra.

### 4.3 The silhouette tile

- **Footprint:** 512x512 blocks, one L3 section footprint, keyed by the packed L3 key (reusing
  `LodWorld` key math and neighbour helpers).
- **Cells:** 128x128 cells of 4 blocks. Value = unclamped worldgen terrain height (`ushort`),
  sampled at the cell's minimum corner (Farseer's sampling).
- **Bookkeeping:** a 16x16 chunk mask of sampled chunks; a per-chunk origin code (dense peek, sparse
  peek, load, capture) for diagnostics; a server-owned revision that increments whenever a chunk is
  added or replaced. Every asynchronous result carries tile key, revision and world epoch, and stale
  results are rejected (concurrency invariants).
- **Interpolation:** unsampled chunks are interpolated when meshing (bilinear between the nearest
  sampled chunk centres, across tile borders where the neighbour is present). Interpolated values are
  never stored.
- **Encoding (disk and wire):** format byte, mask, origin codes, the sampled chunks' 8x8 cells,
  deflated. A fully sampled tile is at most 32 KiB before compression.
- **Server store:** separate file `ModData/vintagehorizons/<savegame>-silhouette-server.db`, table
  `(TX, TZ, Revision, Data, ModifiedMs)`, one connection owned by a dedicated thread (G9), batched
  writes, and a bounded RAM set of tiles near requesting players. It shares nothing with the section
  database.
- **Client store:** RAM only until P5, then `<savegame>-silhouette.db` for instant rejoin.

### 4.4 Height extraction

- `LodPipeline` gains an optional server-only callback, in the same pattern as the existing
  `SnowStateCaptured` and `SectionPersisted` hooks that `LodServerCaptureSystem` sets. It is invoked
  at capture-job creation with `(cx, cz, heights)`: 1,024 `ushort`s copied from
  `MapChunk.WorldGenTerrainHeightMap`.
- This covers every server capture path: chunk-load capture, the sweep, `/vhgen` and pregen loads and
  peeks, `/vhrefresh`, and the frontier. Heights are a by-product; no extra worldgen.
- The callback is attached only while the server-side feature resolves to enabled
  (`EnableFrontier`, section 4.9). A server that does not use the feature pays nothing.
- Sparse peek callbacks copy heights directly and skip capture.
- The owning thread merges height batches into tiles FIFO, under a time and byte budget, with the
  one-item progress guarantee (settled decisions 12 and 14).
- If P0 finds `WorldGenTerrainHeightMap` empty on peeks, fall back to `RainHeightMap` (confirmed
  populated on peeks). That reads the water surface rather than the seabed, so the seabed rule in
  4.7 degrades to sea level. State this if it happens.

### 4.5 The frontier planner (server)

Hosted by `LodServerCaptureSystem`. It opens the server pipeline on demand, exactly as `/vhgen` does
(`OpenPipeline()`), so it works in singleplayer with the sweep off.

**Per requesting player:** the latest request (switches, lead distance, silhouette reach); a session
identity so a completed result cannot cross a reconnect (as in the assist); a travel estimator. A
player whose request says "off", or who disconnects, is removed at once and their queued demand is
dropped.

**Travel estimator**

- Samples the player position every 2 s. Heading and speed come from displacement across a 30 s
  window. Boats and mounts need nothing special: the player moves with them.
- Below a minimum speed the player is stationary, and the corridor is the whole ring.
- A single-sample jump larger than the window can explain (teleport, death) resets the window.
- Camera direction is never an input (ARCHITECTURE demand rule; G93).

**Demand classes, highest priority first**

1. **Corridor (dense).** Chunk columns in the level-0 band: from vanilla's view distance out to the
   client's first LOD threshold, plus a latency margin (speed times measured offer-to-install
   latency). When travelling, a sector of `FrontierSectorDegrees` (default 90) around the heading;
   when stationary, the whole ring. Ordered by deadline: when the player's band will reach the column
   at current speed. The band edge comes from the client's automatic thresholds, so it follows FOV,
   resolution and view distance (settled decision 35), never a fixed distance. Requires the player's
   "Terrain ahead" switch.
2. **Sparse silhouette, stride 4.** One chunk in every 4x4, all round, nearest first, out to the
   silhouette reach. Heights only. Requires the player's "Silhouettes" switch.
3. **Dense breadth (optional).** Nearest first out to `FrontierDenseReachBlocks` (0 by default, A2).
4. **Sparse refinement.** Stride 2, then stride 1, nearest first, outside the dense area. Heights
   only. Requires "Silhouettes".

**Scheduling rules**

- Queue the ground a moving window gains at the moment it gains it; a cursor covers the interior at
  its own pace; budget by elapsed time (G37).
- Probe existence ahead with `TestMapChunkExists` into a `LodColumnMap`, budgeting the 4-chunk
  safety border (G22).
- Act through `Classify`: absent -> peek; present with an intact neighbourhood -> load (dense classes
  only); frontier -> skip. Sparse classes skip present positions; their heights arrive through the
  sweep and capture paths.
- Skip columns already captured, or already sampled at the requested stride.
- **One worldgen authority:** a running `/vhgen` or startup pregen suspends the planner, which
  resumes when they finish. All generators share `GenerateMaxInFlight` and the capture-backlog gate.
- **Yield to real play:** hold new issues while `CurrentGeneratingChunkCount` exceeds
  `FrontierYieldGeneratingChunks`. Farseer uses 64; this mod does not read the counter today; P0
  sets the value.
- Owning-thread issue work at most 1 ms per tick (pregen's budget); rate from
  `FrontierColumnsPerSecond` through `LodTickAllowance`.
- **Several players:** classes merge across players; within a class players take turns; no player
  exceeds a configured share.
- **Idle when unused:** with no requesting player, the planner registers no tick work at all.
- **The savegame promise is measured continuously:** periodically re-probe a sample of recently
  peeked absent positions away from players with `LodAbsenceVerifier`. Any regrowth disables the
  frontier for the session with a named log line.

### 4.6 Delivery without a protocol bump

- `AssistWelcome` gains an append-only `Features` bitmask: bit 0 = frontier request accepted, bit 1 =
  silhouette tiles served. This lets the phases ship separately. A client sends a new message only
  when the matching bit is set; a server sends tile messages only to players whose request arrived.
  Old/new mixtures never exchange the new messages (2.3).
- Four message types appended after `AssistSection`, in the same order on both sides:
  - `AssistSilhouetteRequest` (client -> server): master and the two switches, lead distance,
    silhouette reach, mod version. Sent after the welcome and on change, at most once a second. A
    master-off request is the last message the client sends until the player switches it back on.
  - `AssistSilhouetteManifest` (server -> client): tile keys with revisions, chunked with
    sequence/last, deltas after the first.
  - `AssistSilhouetteTileRequest` (client -> server): tile keys.
  - `AssistSilhouetteTile` (server -> client): key, revision, payload. An empty payload is a refusal;
    `Retryable` marks "not yet". Every accepted request is answered (G6, R4).
- The server clamps every requested distance to its config. Silhouette reach also stays within
  `ServeRadiusBlocks` when that is nonzero.
- **Frontier sections use the existing section path.** For frontier players the server keeps an
  incremental queue of new keys fed from `LodWorld.RegisterInTree`, and offers them every few seconds
  (target at most 5 s, from P0) instead of copying and diffing the whole key set. Other players keep
  the 30 s cadence. Singleplayer already scans every 2 s.
- Tiles use the channel in singleplayer too (the handshake is in-process), so there is one tile
  path. A sibling-file reader like `LodLocalOfferSource` stays available if measurement shows the
  channel costs too much.
- Serving reads tiles from the server's RAM set; misses go to the store thread, never to a
  main-thread SQLite read (the assist blob-reader pattern).
- `WIRE_HISTORY.md` gains a row for the new field and message types at the documentation ritual. The
  protocol number stays 1 unless mixed-version testing proves otherwise.

### 4.7 Client silhouette set and mesher

- **Request producer:** the master and two switches; the applied first threshold, automatic or
  manual (the same source `WantedLevelForSq` uses); the silhouette reach. Sent on change.
- **Silhouette set:** tile key -> (revision, decoded tile); bounded by reach; nearest-first requests
  under an in-flight cap; stale revisions and old world epochs rejected.
- **Coverage:** a 64x64-block cell is covered when its L0 key is in `LodWorld.HasDataSet`, the same
  "has data" answer the renderer descends on. A tile's covered flags are recomputed when L0 keys
  inside it are added or removed, rolling and bounded (settled decision 21).
- **Mesher** (worker thread, arrays only; uploaded on the render thread within the existing
  completed-work budget, settled decisions 5 and 16):
  - Three resolutions per tile: 128, 32 and 8 cells per edge, chosen by on-screen cell size against
    the LOD target pixels. No fixed distances.
  - Uncovered cells clamp to sea level (Farseer's flat sea). Covered cells keep the unclamped height,
    so under water they lie beneath the cached seabed.
  - Edges stitch from neighbour tiles when present. Cells next to covered cells sink a few blocks so
    real terrain wins the depth test at the join (Farseer's sink).
  - Vertex = local x/z, height, covered flag.

### 4.8 Rendering and the handoff

- Drawn inside `LodTerrainRenderer`'s existing Opaque callback, after cached opaque terrain. Water
  and OIT are unchanged. Not a separate `IRenderer`: no second render-order registration and no
  second far-plane writer.
- Per visible tile: a frustum test on its box (real height span), one draw at the chosen resolution,
  a camera-relative transform.
- Shaders `lodsilhouette.vsh` / `lodsilhouette.fsh`, with a shared include
  `lodsilhouettecolor.fsh` holding the Farseer-derived colour: terrain colour from `getSkyColorAt`,
  offset by height relative to sea level; tint mix; sun-and-daylight light bias; fog and sphere fog;
  glow; a fade to sky at the silhouette reach; an optional globe curve that starts at the cached far
  edge. ASCII only (G10). Configured through `LodTerrainSamplerLayout` before the first `Use()`,
  because the sky and fog includes bind engine samplers (G130, G158). The program is compiled only
  when silhouettes are first enabled (4.10).
- **Coverage in the shader:** a covered cell's fragment is discarded inside the cached far edge
  minus the cross-fade band. Beyond the cached far edge covered cells draw, because cached terrain
  does not draw there.
- **Handoff:** with silhouettes on, the cached terrain's existing far fade and explored-edge fade mix
  toward `silhouetteColor(worldPos)` instead of the sky colour. With silhouettes off, the cached
  shader's result is unchanged. This is what removes a visible seam: at the join, both sides paint
  the same colour.
  - `lodsurfacefinish.fsh` is shared by the terrain and water programs, so every program that
    includes it must gain the new include and declare its uniforms (G161, G174). The engine's
    `Uniform()` throws on an undeclared name.
- **Far plane:** projection distance = max(cached far extent, silhouette extent), through the
  existing stepped policy (G5). The cached shader's far edge stays the cached distance, so the
  accepted cached fade does not move.
- **Failure:** a compile or draw failure disables silhouettes for the session with a named log line.
  Nothing plausible is drawn in their place (settled decision 26, G160).
- **Distant anti-aliasing:** silhouette pixels are drawn after the pre-cached depth snapshot, so they
  fall under settled decision 34. Verify in P4; do not assume.
- **Deferral** to another LOD mod is unchanged: if one draws, this mod is idle, silhouettes included
  (settled decision 10).

### 4.9 Settings and commands

**Client** (`vintagehorizons.json`, a new "Silhouettes" tab in `.vhconfig`; the master switch also
appears on the General tab beside the other master switches):

- **Horizon silhouettes and terrain ahead** - master on/off (default on). Off removes the component
  (4.10).
- Under the master: **Silhouettes** on/off and **Terrain ahead** on/off (both default on). Greyed
  when the master is off. Neither does anything unless the server allows it.
- Silhouette reach, 512-block steps (default 8,192; clamped by the server).
- Tuning dials in the style of the anti-aliasing tab: tint colour and strength, sky tint, light
  bias, fade bias, globe curve. Defaults are Farseer 1.4.0's until the owner tunes them.
- Presets: Performance switches the master off; Balanced and Quality switch it on (A6).
- Commands (session-only switches plus log dumps, because chat cannot be copied):
  `.vhsilhouette [on|off|log|stats]` and `.vhfrontier [on|off|log]`.

**Server** (`vintagehorizons-server.json`, sanitized, mirrored append-only into
`AssistServerSettings`):

| Setting | Default | Meaning |
|---|---|---|
| `EnableFrontier` | `null` | `null` = automatic: on for singleplayer/LAN host, off for dedicated. `true`/`false` override. Off also detaches height extraction. |
| `FrontierColumnsPerSecond` | 16 | Issue rate; the engine still saturates near 23/s. |
| `FrontierSectorDegrees` | 90 | Corridor width as an angle around the heading, so it scales with the band. |
| `FrontierDenseReachBlocks` | 0 | 0 = corridor only. Larger values also densify all round (disk cost, section 5). |
| `SilhouetteReachBlocks` | 8,192 | Ceiling on player-requested reach. |
| `FrontierSparseStrideChunks` | 4 | First sparse pass stride. |
| `FrontierYieldGeneratingChunks` | from P0 | Hold new issues while the engine is generating this many chunks. |

Server command: `/vhfrontier [status|pause|resume]`, controlserver privilege. Status reports tiles,
fill ratio, disk used, rates, yields and verifier verdicts, and writes the same to the log.

### 4.10 Switching it off (D7)

"Off" must deliver the saving, so every cost the component adds has a named off state. Switching the
master off takes effect at once, without a restart:

| Where | What stops |
|---|---|
| Client network | One final master-off request, then nothing: no tile requests, no manifests processed. |
| Client memory | The silhouette set is cleared; no tile data is retained. |
| Client workers | No mesh jobs are queued; queued ones are dropped by revision. |
| GPU | Tile buffers are released through the renderer's normal bounded disposal path (as `.vhgpu off` releases its arenas). |
| Render pass | The silhouette pass returns before touching any GL state. |
| Shaders | The silhouette program is not compiled until silhouettes are first enabled, so a player who never uses it pays no startup compile. The cached fade takes its existing sky path, and its result is identical to today's. |
| Far plane | The silhouette extent contributes nothing, so the projection is exactly what it is today. |
| Coverage tracking | Not run. |
| Server | The player's demand is dropped immediately; with no requesting player the planner registers no tick work. `EnableFrontier = false` also detaches height extraction. |
| Section cache | Terrain the frontier already generated stays. It is ordinary cached terrain and costs what any cached terrain costs; the corridor simply stops adding to it. |

The finer switches stop only their half: "Silhouettes" off stops sparse sampling, tiles and the pass;
"Terrain ahead" off stops the corridor.

Evidence required (P1 and P4): with the master off, a fixed sandbox route measures the same frame
time and server tick time as the same build without the component, within run-to-run spread. Quote
the spread (G98), and use a deterministic scenario rather than a noisy cross-check.

## 5. Cost model

Estimates unless marked measured. Peek times assume the recorded ceiling of about 23 columns/s.

| Item | Figure |
|---|---|
| Corridor, sustained | band width x speed / 1,024 columns/s. A 90-degree sector at a 624-block band edge is ~880 blocks wide: ~4.3/s at 5 blocks/s, ~8.6/s at 10 blocks/s |
| Corridor, standing start | ring from 128 to 624 blocks (owner's settings) ~1,140 columns, ~50 s |
| Stride-4 silhouette to 4,096 | ~3,200 peeks, ~2.3 min |
| Stride-4 silhouette to 8,192 | ~12,900 peeks, ~9.3 min |
| Stride-2 refinement to 8,192 | ~38,600 more peeks, ~28 min |
| Dense 4,096-block disc (class 3, optional) | ~51,500 peeks, ~37 min, once per area |
| Server disk, dense (**measured basis**: 40.5-78.4 KiB x 1.53 per L0 section) | ~13-25 MiB per km of corridor travel; ~0.8-1.5 GiB per 4,096-block dense disc |
| Server disk, silhouette | ~804 tiles to 8,192 reach, at most 32 KiB each before compression: at most ~25 MiB fully sampled |
| Peek memory | at most 64 in flight x 1-2 MB (existing cap) |
| Network | corridor ~1-3 new L0 sections/s per travelling player plus ancestors, under the 8/s per-player cap; tiles are small |
| Client GPU | unknown until P4; the budget is agreed there against the owner's scene |
| Master switch off | intended zero; measured in P1 and P4 (4.10) |

## 6. Phases

Each phase is independently measurable. Every playable test build follows the version rule in
`CLAUDE.md`. Every game run uses the isolation scripts (G11) and happens only after asking.

### P0 - Engine facts and measurements

Dev-only logging; no player-visible change.

- **0.1** `WorldGenTerrainHeightMap` is populated on Terrain-pass peeks on 1.22.5 and 1.22.6
  (Farseer relies on it; this mod has confirmed only the rain map). Also record whether
  `MapChunk.MapRegion.ForestMap` is present on peeks (P6 input).
- **0.2** Throughput of stride-4 and stride-2 lattices against the dense spiral at 64 in flight:
  columns/s, issue-to-callback p50/p95, server tick p95, retained memory.
- **0.3** Size of peek-only L0 sections (a fresh world, a small `/vhgen`) against the 40.5-78.4 KiB
  mixed figure.
- **0.4** `CurrentGeneratingChunkCount` while one player explores normally, to set the yield
  threshold.
- **0.5** Dedicated server: time from a section's persistence to the client's install at the 30 s
  cadence; cost of an incremental key queue against the snapshot diff at the measured key count.

**Gate:** numbers recorded in the session record, P1-P3 parameters set from them. A failed 0.1
switches height extraction to the rain map (4.4).

### P1 - Frontier corridor (smallest end-to-end slice)

**Deliver:** welcome `Features` bit 0 and `AssistSilhouetteRequest`; the client master switch and
"Terrain ahead" switch with the request producer; the planner with class 1 (and class 3 behind its
0 default); the travel estimator; worldgen authority shared with `/vhgen` and pregen; engine yield;
continuous absence verification; the `EnableFrontier` automatic rule; server settings and
`/vhfrontier`; incremental new-key offers for frontier players; the off states from 4.10 that apply
to this half.

**Checks** (fast tier, pure where possible): travel estimator (still, heading, turn, teleport
reset); deadline ordering; class admission; `Classify` authority (an existing column is never
peeked and a frontier column never loaded, extending `GenerateChecks`); budget arithmetic; config
sanitize and the automatic rule; a master-off request drops the player's demand and an idle planner
registers no tick work; the incremental offer queue equals the full diff on a fixture; mixed versions
exchange no new messages (old client/new server, new client/old server).

**Gate (human-tested, singleplayer, then a dedicated sandbox):** travel on foot and by boat into
never-generated land. Bare cached terrain appears in the level-0 band ahead before vanilla chunks
arrive. Switching the master off mid-journey stops generation in the server log within a second or
two. The log shows absence verdicts with no regrowth. Server tick p95 stays within the P0 budget. The
owner reports no stutter. Master-off cost is measured per 4.10.

### P2 - Silhouette tile model and store

**Deliver:** the tile type (no game types) with encode/decode, interpolation, stitching and
revision rules; the server store thread and file; height extraction from every capture path, attached
only while the server feature is enabled; `/vhfrontier status` reports tiles.

**Checks:** round trip; rejection of corrupt and partial payloads; deterministic interpolation and
stitching fixtures; revision monotonicity; the cell sampling offset against a hand-computed column;
extraction is detached when `EnableFrontier` resolves off.

**Gate:** harness-tested; integration-tested by a `/vhgen` run whose columns appear in tiles.

### P3 - Sparse sampling and tile transport

**Deliver:** classes 2 and 4 (heights only); `Features` bit 1; the manifest, tile-request and tile
messages; the client silhouette set (RAM); `.vhsilhouette log|stats`.

**Checks:** every request answered (success, refusal or retryable); in-flight cap; stale-revision and
old-epoch rejection; reach clamping; manifest chunking; sparse columns never reach `CaptureColumn`;
"Silhouettes" off clears the set and sends no tile requests.

**Gate:** integration-tested in singleplayer and a dedicated sandbox. A stride-4 disc to the
requested reach arrives, and the log reports counts and fill ratio.

### P4 - Silhouette drawing and the handoff

**Deliver:** the mesher (three resolutions, coverage, sea rule, stitching, sink); the draw pass;
shaders and sampler layout with lazy compilation; the fade-target switch in the cached shader; the
combined far projection; the "Silhouettes" tab, the General-tab master and the preset wiring; the
remaining off states from 4.10.

**Checks:** shader ASCII and sampler configuration (`StaticAssetChecks`); every uniform declared
where it is uploaded; with silhouettes off, the cached fade expression is unchanged and the
silhouette program is never compiled; GPU tile buffers are released on master-off; the coverage and
discard rule as a pure function; resolution choice by on-screen size; the projection policy does not
reset on ordinary movement (G5).

**Gate (human-tested; small, specific observations):**

- a. At sea with no land inside the draw distance, silhouettes show toward land you know is there.
- b. On land, a silhouette fills an unexplored gap between explored areas.
- c. No visible line where cached terrain meets the silhouette, at the far edge or at the edge of
  explored ground.
- d. Night, rain or fog, and winter.
- e. With the master off, cached terrain looks exactly as before (A/B screenshots), and frame time
  matches the same build without the component (4.10).
- f. The silhouette pass's GPU time in the log, against a budget agreed with the owner.

**Fallback if (c) cannot be met:** the other D1 option, silhouettes only beyond the draw distance.
That is an owner decision, not a quiet change.

### P5 - Refinement and persistence

**Deliver:** stride-2 and stride-1 refinement outside the dense area; client tile persistence for
instant rejoin (not opened while the master is off); resolution thresholds tuned by the owner. Only if
P4 measured a need: a GPU path (one shared grid mesh per resolution with per-tile height textures,
multi-draw) and coverage finer than 64 blocks.

### P6 - Statistical canopy (separate acceptance, D3)

**Deliver:** a per-column "generated" fact as a third append-only format-4 tail, cleared by real
capture and propagated with the two-fact discipline (G167), with a mip rule chosen on a fixture;
forest density from the peeked column's map region (P0.1); canopy geometry on generated columns only,
with deterministic placement and a leaf palette entry so seasonal tint applies unchanged; the
switch point keyed to on-screen tree size and tuned by the owner; its own switch under the master.
Not started before P4 is accepted.

## 7. Risks and unknowns

| Risk | Retired or bounded by |
|---|---|
| Heights missing on peeks | P0.1; rain-map fallback (4.4) |
| Sparse peeks slower per column than dense | P0.2; stride and reach defaults |
| Offer latency makes the corridor late | P0.5; incremental queue; latency in the deadline margin |
| Disk growth on busy servers | dense reach 0 by default; opt-in on dedicated; status reports disk |
| "Off" still costs something | the 4.10 table; master-off A/B in P1 and P4 |
| 64-block coverage leaves gaps at explored edges | P4 observation; P5 refinement |
| Silhouette plane visible through cached water in the fade band | covered cells keep the seabed height (4.7); P4 observation |
| Silhouette pass too expensive at long reach | culling and on-screen resolution; P4 budget; P5 GPU path |
| Many players multiply the server cost | per-player share; admin opt-in; caps |
| Engine change to peek areas or message numbering | fast-tier checks pin what can be pinned; a mixed-version run each release |
| Map reveal on servers | admin opt-in; reach within `ServeRadiusBlocks` |
| Modded worldgen | none needed: peeks are the server's own worldgen, so silhouettes follow it; the absence verifier still runs |
| An admin `/vhgen` pauses corridors | by design; stated in `/vhfrontier status` |

## 8. Out of scope

- The client-only coastline (D4; Appendix A).
- Real trees through the vegetation stage (about 25x per column; D3).
- Keeping sparse samples' blocks in a holding store.
- Deleting already-generated terrain when the component is switched off (it is ordinary cached
  terrain; 4.10).
- Any change to terrain ownership or the vanilla handoff (settled decision 28).
- Cooperating with Farseer; this mod still defers to it when it draws.

## 9. Settled decisions this plan must respect

- **1, 2** - Vanilla-server compatibility and additive assist: nothing new runs unless the server
  has the mod and advertises the feature.
- **5** - OpenGL on the render thread: tile meshes are worker-built arrays.
- **6** - Parent covers until children: the voxel cache is untouched; the silhouette is a separate
  layer.
- **7, 13** - Local observed data wins: frontier sections arrive as foreign data, and local capture
  replaces them.
- **8, 9** - The sweep generates nothing, and transient generation persists nothing: the frontier
  uses `Classify` and is measured by the absence verifier.
- **10** - Deferral to competing LOD mods is unchanged.
- **11, 12, 14, 16, 21** - Evidence-driven, time/byte-budgeted, progress-guaranteed, rolling work.
- **26** - The silhouette fails visibly, never plausibly.
- **28** - No change to terrain ownership.
- **34** - Distant anti-aliasing interaction is verified, not assumed.
- **35** - The corridor edge and tile resolutions follow the screen.

## 10. Files expected to change

Orientation, not a mandate.

**New:** `src/Silhouette/LodSilhouetteTile.cs`, `LodSilhouetteStore.cs`, `LodSilhouetteSet.cs`,
`LodSilhouetteMesher.cs`; `src/Net/LodFrontierPlanner.cs`, `LodTravelEstimator.cs`;
`src/Render/LodTerrainRenderer.Silhouette.cs`; `assets/vintagehorizons/shaders/lodsilhouette.vsh`
and `.fsh`, `shaderincludes/lodsilhouettecolor.fsh`; checks `SilhouetteTileChecks`,
`FrontierPlannerChecks`, `TravelEstimatorChecks`, `SilhouetteProtocolChecks`.

**Changed:** `LodAssistProtocol.cs`, `LodAssistClient.cs`, `LodAssistServerSystem.cs`,
`ManifestLedger.cs` (incremental queue), `LodWorld.cs` (`RegisterInTree` hook),
`LodServerCaptureSystem.cs`, `LodServerConfig.cs`, `LodPlayerPregen.cs` (shared authority),
`LodPipeline.cs` (height callback), `VintageHorizonsModSystem.cs` (config, commands, request
producer), `VintageHorizonsConfigDialog.cs` ("Silhouettes" tab, General-tab master),
`LodVisualSettings.cs` (presets), `LodTerrainRenderer.cs` (pass, projection, lazy program),
`lodsurfacefinish.fsh` and every program including it (fade target), `LodTerrainSamplerLayout.cs`,
`StaticAssetChecks.cs`, `ConfigChecks.cs`, `GenerateChecks.cs`.

## 11. Documentation at completion

Only when the owner asks for the documentation ritual: a `WIRE_HISTORY.md` row (new `Features` field
and message types); `ARCHITECTURE.md` settled decisions for the two kinds of generated column,
silhouette ownership and handoff, frontier authority, and the off-means-no-work rule; README commands
and settings; GOTCHAS as traps are proven; STATUS, TODO, CHANGELOG and the session record per the
ritual.

## 12. Attribution

Farseer (MIT, (c) 2026 Badgerson): the tile representation and the silhouette shader terms are
adapted from it. Credit goes in the shader headers and in README Credits, which already lists
Farseer. TopoHorizon's peek constants are already credited in `LodPlayerPregen`.

## Appendix A - Recorded for the later coastline plan (D4)

Source-traced 2026-09-06 and 2026-09-12, not runtime-verified:

- The ocean map is generated in the map-region pass, at one sample per 32 blocks
  (`TerraGenConfig.oceanMapScale` = 32), with no chunk generation.
- `GenMaps.GetOceanMapGen(seed + 1873, landcover, 32, oceanscale, requireLandAt, requiresSpawnOffset)`
  is public static and returns `MapLayerBase`. `MapLayerOceans` itself is internal. `XZ` is a public
  struct in `VSEssentials`, which the project already references.
- Every client receives the world seed (`Packet_ServerIdentification.Seed` -> `capi.World.Seed`) and
  the same `WorldConfiguration` tree `GenMaps` reads (`capi.World.Config`: `landcover`,
  `oceanscale`).
- `requireLandAt` for worlds created on 1.20.0-pre.14 or later comes from `ForceRandomLandArea`,
  which uses only the seed, map size, region size and chunk size.
- `CreatedGameVersion` is **not** sent to clients, so `requiresSpawnOffset` (which shifts the whole
  field for older worlds) is unknown on the client. Resolve it by comparing both candidates against
  map regions the client really holds (measured client reach: 512-1,024 blocks, partial to 3,072).
  The same comparison is the guard against modded worldgen.
- It gives land/sea only; the landform generator needs `ICoreServerAPI`, so client-side height is
  not available this way.
- The coastline would reuse this plan's silhouette renderer and fall under the same master switch.
