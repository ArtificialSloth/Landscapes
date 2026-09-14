# Session 92 — Horizon silhouettes: the revised plan and the first look

**Date:** `2026-09-14`
**Branch/commit:** `codex/water-depth-light`. Session 91 docs are `229c04b`, the revised plan is `9c02e48` and the 0.4.77 source checkpoint is `4c7720c`. This documentation pass was uncommitted at close.
**Mod version:** `0.4.77`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The world-generation plan was complete at 0.4.76 (Session 91), and the owner chose Farseer
silhouettes next. Session 91's documentation pass was still uncommitted, so it was committed first
(`229c04b`) to keep it apart from new work. The owner asked for a shared picture of the feature
before work began. They answered the open questions and asked for the plan to be revised before any
code.

Sources read:
- the plan;
- the preparation notes kept outside the repository (`_silhouette-prep`);
- Farseer 1.4.0's source;
- Algernon's Terrain Sampler (its GitHub source and ModDB page);
- the code at 0.4.76.

## Work narrative

### 1. Owner decisions and the revised plan

The plan now records the owner's decisions as D8–D15 (`9c02e48`):

- D8 (2026-09-13): silhouette heights need only be fairly representative; cheaper routes are
  preferred.
- D9: Farseer is a guideline, not a specification; the code is our own.
- D10: the silhouette reach is a handle on the General-tab distance scale, default 8,192 blocks.
  The cached draw distance also moves onto the scale, and the silhouette band starts where cached
  terrain ends.
- D11: Farseer's classic sky-derived colour for now.
- D12: keep small islands and inlets where possible.
- D13: Horizons stays standalone; other mods may serve as optional accelerators.
- D14: silhouettes come first, and are built far in advance for the whole reach.
- D15: Terrain Ahead is a separate feature, with its own switch and its own tab.

The phases were reordered:
- P0: measurements;
- P1: a rough first look;
- P2: tiles, store and captured heights;
- P3: the builder and tile transport;
- P4: drawing, the handoff and settings;
- P5: tuning and persistence;
- P6: Terrain Ahead;
- P7: canopy.

### 2. Optional accelerators

Algernon's Terrain Sampler 1.3.0:
- recomputes vanilla GenTerra heights from the GenMaps region maps without generating chunks;
- exposes `TerrainSamplerMod.Instance.GetBlockColumnHeight(x, z)`;
- runs on the server but declares `RequiredOnClient: true`;
- is listed for 1.22.0–1.22.2;
- takes 1–2 ms per sample by the author's figure;
- calls Watersheds' `GetPreWatershedsBlockColumnHeight` when Watersheds is loaded;
- has no TerraPrety handling;
- publishes no licence, so it can only be called through reflection.

Watersheds' own `GenTerraSampler.SampleTerrainHeightsBatch` is the other candidate. Neither was
installed or measured this session.

### 3. The first look (0.4.77)

- **Server.** `LodSilhouetteBuilder`, created only on singleplayer and LAN-hosted servers in this
  build.
  - For each requesting player it walks a spiral over a lattice of one chunk in every 8x8 (chunks 4
    and 12 of each 16-chunk tile), out to the reach plus one tile.
  - It probes each lattice chunk with `TestMapChunkExists`. An absent chunk gets a Terrain-pass
    preview; the builder copies its height map and captures and saves nothing.
  - It yields while the world-generation authority is held, or while `CurrentGeneratingChunkCount`
    is 8 or more.
  - It runs at 16 previews per second with 16 in flight, and has no tick listener while nobody is
    asking.
  - Tiles cover 512 blocks at 4-block cells, stored per chunk. They are pushed nearest first.
- **Wire.** Four message types are appended in the plan's order, Features bit 2 is advertised, and
  the tile payload is format 1. The manifest and tile-request types are registered but unused.
- **Client.** `LodSilhouetteClient` keeps one node per lattice chunk (its mean height).
  - It interpolates bilinearly and ignores unknown nodes, so neighbouring tiles agree exactly along
    their shared border.
  - It drops quads in level-0 cells that cached terrain covers, and sinks vertices next to them.
  - It builds 33x33-vertex meshes on the thread pool.
- **Renderer.** `LodTerrainRenderer.Silhouette.cs`.
  - The pass is its own step after the cached draw, because the cached draw returns early when no
    cached terrain is in view (G200).
  - The shaders are derived from Farseer; the globe curve starts at the cached far edge.
  - The far plane takes the larger of the cached and silhouette extents, but only while silhouettes
    draw.
  - The program compiles the first time silhouettes are switched on.
- **Command.** `.vhsilhouette on|off|log|stats`, for the session only.
- **Checks.** 88 new assertions; 24,835 pass. They cover:
  - tile mapping, source order, round trip and refusals;
  - continuous interpolation across tiles;
  - mesh coverage and the flat sea;
  - the feature gate and message-order parity;
  - that the builder never captures;
  - declared uniforms, the sampler layout and the off state.

### 4. The owner's test

The owner played singleplayer on 2026-09-14 from 16:09 to 16:11. Log-measured unless stated:

- **Drawing.** The shader compiled and silhouettes drew: 117 tiles, 401 sample points and 33–41
  tile draws per frame.
- **P0.1.** The first preview (chunk 7980,7988) had 0 of 1,024 worldgen heights at zero, and a
  forest map was present. This is confirmed on the owner's Watersheds and TerraPrety world.
- **Throughput.** Previews landed at about 5 per second with 16 always in flight (145 in the first
  30 s, 161 in the next). That is a third of the issue rate, so the engine was the limit. From
  16:10:32 the builder held for real chunk generation (266, then 705 held ticks) and added no more
  samples.
- **Joins** (owner's words). Silhouettes "don't connect well with immediately adjacent terrain".
- **Exit hang** (owner's report). Quitting while silhouette data was still generating hung the game
  at the exit screen until the process was killed.
  - The owner had switched silhouettes off at 16:11:39, with 12 previews in flight.
  - The server log reports a clean stop at 16:11:47 ("All threads gracefully shut down", "Stopped
    the server!").
  - The client's last line is "Exiting game now", at 16:11:48.
  - The cause is not diagnosed, and it is not proven that silhouettes are involved.

---

## Delivered

- **`229c04b`:** Session 91 documentation and P0 tooling committed.
- **`9c02e48`:** the plan revised with D8–D15 and the new phase order.
- **0.4.77 (`4c7720c`):** the horizon silhouette first look described above. Zip SHA-256
  `b6d17bf3b5920a5d634b4100d562b8d9ded6d9f893a59eee099570d5a00b552e`, copy-installed.
- **This documentation pass.**

## Decisions

- D8–D15 as recorded in the plan; see section 1.
- **P1 scope.** Singleplayer only and session-only, with no settings; Terrain Ahead is untouched.
- **Interpolation.** The lattice-bilinear fill is a stand-in. The plan's fill over known cells at
  any spacing replaces it when sampling becomes adaptive.
- **Existing columns.** Previews skip them; their heights arrive through capture in P2.
- **Draw order.** Silhouettes draw after the whole cached frame, so distant anti-aliasing does not
  yet touch them.

## Traps

- **G199.** `CurrentGeneratingChunkCount` never sees peeks, so a producer braked only by it never
  slows. Count your own peeks in flight.
- **G200.** The cached draw returns early when no cached terrain is in view. A pass that must draw
  out to sea needs its own step after it.

## Flagged and unverified

- **Judgement calls awaiting owner review:**
  - the classic look, including the fade toward the sky with distance and the default globe curve;
  - Terrain Ahead in its own tab rather than the Generate tab;
  - no reach ceiling in singleplayer.
- **Below established level:**
  - the exit hang's cause, and whether previews in flight or anything else of this feature is
    involved;
  - silhouette GPU cost;
  - the off-state A/B;
  - accelerator accuracy and speed;
  - dedicated servers (not offered in 0.4.77).
