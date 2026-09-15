# Session 94 — P0.3 begins: silhouette heights measured on test servers, first accelerator figures

**Date:** `2026-09-14`
**Branch/commit:** `codex/water-depth-light`. Session 93's documentation `7db7810`; checkpoint `dd1692b` (0.4.83). This documentation pass is committed on top.
**Mod version:** `0.4.83`
**Assist protocol / blob / schema:** `1 / 4 / 6`. The silhouette tile store keeps its own schema `1`.

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The owner asked to commit Session 93's documentation (`7db7810`) and begin P0.3, the accelerator
comparison. During the session they allowed any test servers the work needs ("You may launch
whatever test servers you need").

Session 93 left one blocker. `/vhsilhouetteserver compare` compares against heights the silhouette
builder holds, and dedicated servers have no builder, so a headless test server had nothing to
compare.

Sources:
- decompiled `VintagestoryLib` and `VSEssentials` 1.22.7 (`WgenCommands`);
- Watersheds 7.0.4 and Algernon's Terrain Sampler 1.3.0, decompiled read-only outside the
  repository, with no code copied;
- two isolated dedicated test servers (§5).

## Work narrative

### 1. Why the comparison could not run on a test server

Source-traced:
- `LodServerConfig.SilhouettesEnabled` is false on every dedicated server, so no builder exists and
  `compare` refuses.
- Honouring `EnableSilhouettes: true` there would do more than allow measurement. The welcome
  advertised Features bit 2 wherever a builder existed, and `LodAssistClient.SendSilhouetteRequest`
  sends whenever the bit is set. Every modded client that joined would be served silhouettes with
  no reach clamp, which is P3's unbuilt dedicated opt-in (G206).

### 2. The measuring switch (0.4.83)

- **The switch.** `VINTAGEHORIZONS_SILHOUETTE_MEASURE=1` in a dedicated server's environment
  (`LodServerConfig.MeasureSilhouettesVariable`) keeps silhouette heights there.
  `LodServerConfig.SilhouetteHeightsKept` resolves it, and `EnableSilhouettes: false` still wins.
  No config file can turn it on; like `VINTAGEHORIZONS_STATS`, it is developer-only.
- **Offered versus held.** `silhouettesOffered` now decides the feature bit and whether a request is
  answered. A measuring server holds a builder, advertises nothing and ignores requests.
- **Status.** The status line starts "measuring heights only, players are offered none", and the
  server log says so at start.
- **End marker.** The comparison logs "Accelerator comparison finished." after its last report, so a
  script knows when to stop.

### 3. The comparison script

`scripts/silhouette-compare.ps1` (PowerShell 7) runs one comparison on an isolated dedicated server
under `.testdata/silhouette-compare/<profile>`. It follows the Session 91 gate's process handling:
private TEMP, pidfile, and a stop of the exact process only.
- **Mods.** It copies the owner's enabled mods: each zip whose `modid@version`, read from its
  `modinfo.json`, is not in `clientsettings.json`'s disabled list. A zip whose identity cannot be
  read is left out and named. It adds `ModConfig` and the exact current Vintage Horizons zip.
  `-PlanOnly` lists the set and launches nothing.
- **Pinned config.** Vintage Horizons captures, serves, sweeps and generates nothing of its own.
- **Sequence:**
  1. `/wgen pregen <range>`;
  2. wait until the generation queue reads zero three times;
  3. `/vhsilhouetteserver status`, then `compare <points>`;
  4. wait for the finished line;
  5. `status` again, then `/stop`.
- **Proof.** It fails unless the server log lists the required mods (Watersheds, TerraPrety,
  Algernon's Terrain Sampler, Vintage Horizons) and the measuring line.
- **Idle comparison.** It compares only after generation is idle, because whether the samplers are
  safe beside real generation is unknown.

Source-traced for the script:
- `/wgen pregen` from the console generates around the world centre. It loads each column with
  `LoadChunkColumn`, so `ChunkColumnLoaded` fires and the builder captures the heights, as both runs
  confirmed.
- A tile cell holds the height of its own corner block (`LodSilhouetteTile.SetChunk`), and the
  comparison asks the samplers for that same block, so no offset enters the error.

### 4. What the two samplers compute

Source-traced from the decompiled mods:
- **Real heights.** With Watersheds installed, `WatershedsGenTerra.SetBlocksToChunks` writes
  `WorldGenTerrainHeightMap` as each column's top solid block.
  `CarveRiverChannelsAndStoreRiverDistance` then lowers it where a river channel is carved.
- **Algernon's Terrain Sampler** answers `WatershedsGenTerra.GetPreWatershedsBlockColumnHeight`
  whenever Watersheds is loaded.
- **Watersheds' own sampler** (`GenTerraSampler.SampleHeightWithContextCore`, rivers not ignored)
  runs the same steps:
  1. column distortion;
  2. river-valley influence with `RiverTerrainYThresholds`;
  3. `CalculateColumnHeight`;
  4. the climate sea-level adjustment.
- **One function.** With Watersheds installed, the two accelerators are one height function (G205).
  The 0.4.82 label "Watersheds' height before rivers" is wrong: both include river valleys, and
  neither applies the channel carve.
- **Same definition.** `CalculateColumnHeight` returns the top solid block, as the stored map does,
  so the measured lean is not a definitional offset.

### 5. Two test-server runs (0.4.83)

Both runs used:
- fresh worlds with random seeds (the console's `/seed` printed nothing);
- the owner's enabled mods: Algernon's Terrain Sampler 1.3.0, placeonslabs 1.1.3, terrainslabs
  1.0.17, TerraPrety 7.11.1 and Watersheds 7.0.4;
- the default map height, 256.

Neither server log held a warning or an error, so neither logged coast-map errors. Both servers
exited after `/stop` without being forced.

| | `p03-first` | `p03-wide` |
|---|---|---|
| Pregen | range 20 (1,681 columns), idle after 205 s | range 32 (4,225 columns), idle after 459 s |
| Heights offered | 2,003 column loads, 16 tiles | 5,438 column loads, 25 tiles |
| Points compared | 1,680, all captured | 4,096, all captured |
| Error median / 90% / 95% / max | 1 / 4 / 5 / 21 blocks | 2 / 4 / 5 / 11 blocks |
| Bias | +1.5 (reads high) | +1.9 |
| Land/sea disagreement | 195 (11.6%) | 241 (5.9%) |
| Coast points (real within 4 of sea level) | 1,347; median 0; 171 disagree | 1,958; median 1; 236 disagree |
| Answers exactly at sea level | 100 (real: 186) | 91 (real: 299) |
| Time per sample, Algernon / Watersheds | mean 0.02 / 0.01 ms; max 5.6 / 0.6 ms | mean 0.01 / 0.01 ms; max 3.1 / 0.8 ms |
| Worst five | real 63–87, read 7–21 high | real 74–85, read 8–11 high |

- **Identical accelerators.** The two gave identical figures in both runs, down to the worst five
  points; only timing differed. This matches §4.
- **Waterline.** Nearly every land/sea disagreement lies within 4 blocks of sea level (171 of 195,
  and 236 of 241).
- **Worst points.** All ten have real heights of 63–87. This record first called them sea floor,
  inferring a sea level of about 110 from the map height. **Corrected in Session 95:** a later world
  with the same mods and configs measured sea level 82, so these were most likely shallow water and
  low land (G208). This run's own sea level was not logged.
- **Speed.** At these rates 1,024 heights take about 10–20 ms of a spare core. A preview's 1,024
  heights take about 43 ms of the single world-generation thread (plan 3.2).
- **Rain count.** The status line counted 6 and 51 "rain heights from old saves" in these brand-new
  worlds (G207).

### 6. What the figures do not show

- **Terrain mix.** 80% and 48% of the points lay within 4 blocks of sea level. Console pregen always
  centres on the world centre, and both worlds had coastal land there. Mountains and rivers are
  thinly represented.
- **Unexplored land.** Every point was in land generated moments earlier, so the samplers' region
  data was already built. Silhouettes would sample land nobody has generated. Speed and correctness
  there are unmeasured.
- **Concurrency.** Both comparisons ran with generation idle.
- **The lean's cause.** The steady +1.5 to +1.9 is measured. Whether it comes from carved channels,
  the column's 3D density or the sea-level adjustment is not established.
- **The owner's verdict.** D8 asks for a by-eye side-by-side, which does not exist yet.

### 7. Checks

- **Result.** 24,923 assertions pass: 0.4.82's 24,916 plus 7.
- **New:**
  - the switch's resolution: dedicated with and without it, config off still winning, and
    singleplayer unchanged;
  - a measuring server neither advertises the bit nor answers a request;
  - the switch is read in one place;
  - the script sets the variable the server reads.
- **Changed.** The captured-heights wiring check now names `SilhouetteHeightsKept`.

---

## Delivered

- **Session 93's documentation,** committed as `7db7810` (DocCheck: 9,510 checks).
- **0.4.83 (`dd1692b`):** the measuring switch, the finished line and `scripts/silhouette-compare.ps1`.
  Zip SHA-256 `f13602ab1440287008ac20b8cad382c2051c358e511d2f9850de6c80cfa1d07b`, copy-installed;
  older zips untouched.
- **Two test runs,** `p03-first` and `p03-wide`, kept under `.testdata/silhouette-compare/`. The
  larger save is 196 MB.
- **This documentation pass,** at the owner's request.

## Decisions

- **Owner, 2026-09-14:**
  - begin P0.3;
  - any test servers the work needs may be launched;
  - update the documentation, then commit.
- **A developer-only switch, not the config.** Rejected: honouring `EnableSilhouettes: true` on
  dedicated servers, which would serve silhouettes to joining players with no reach clamp (G206).
- **The comparison runs only while generation is idle,** until concurrency is measured.
- **The test server gets only the owner's enabled mods,** identified by `modid@version`.
- **Awaiting the owner:** the next build.
  - A: a side-by-side picture of real and sampled heights.
  - B: sampling land before it is generated, then generating it and comparing.
  - C: both. Recommended, because both use a saved list of points.
- **Proposed, not decided:** where Watersheds is installed, call its sampler directly. Algernon's
  Terrain Sampler adds nothing there, and its modinfo requires every client to install it. It still
  matters for worlds without Watersheds.

## Traps

Promoted to `dev/GOTCHAS.md`:
- G205: with Watersheds installed, both accelerators are one height function.
- G206: a silhouette builder on a dedicated server would serve every modded client.
- G207: "rain heights from old saves" also counts bare columns in new worlds.

Kept here:
- **Unknown seeds.** The dedicated console's `/seed` printed nothing, so both runs' seeds are
  unknown. Read the seed from the save, or pin it, when a run must be repeated.
- **Git messages in PowerShell.** A here-string after `git commit -F -` is taken as a pathspec, not
  standard input. Pass it with `-m`.
- **Finding a type.** `ilspycmd` names the right assembly when a type is missing: `/wgen` lives in
  `VSEssentials`, not `VSSurvivalMod`.

## Flagged and unverified

- **Judgement calls awaiting owner review:**
  - the developer switch's name and limits (dedicated only, never from a config file);
  - the script copying enabled mods by their `modinfo.json` identity;
  - the next build's scope (A, B or C);
  - calling Watersheds' sampler directly where Watersheds is installed.
- **Below established level:**
  - **P0.3's verdict.** The owner has not judged, and D8's side-by-side does not exist.
  - **Representativeness.** Two coast-heavy areas at the world centre; mountains and rivers thin.
  - **Unexplored land.** Speed and correctness where no region data exists yet are unmeasured.
  - **Concurrency** beside real generation is unmeasured.
  - **The lean's cause,** +1.5 to +1.9, is not established.
  - **Labels.** The comparison still reports "Watersheds' height before rivers" and "rivers
    included"; the first is wrong (§4). The status line's rain count misleads in new worlds (G207).
    Both wait for the next build.
  - **Unchanged from Session 93:**
    - the joins;
    - stored-tile read-back after a restart;
    - the error brake;
    - P2's integration gate;
    - GPU cost and the off-state A/B;
    - a dedicated server with players;
    - the `bodanboys` stall.
