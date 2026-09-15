# Session 95 — P0.3 side-by-side and the unexplored-land test (0.4.84)

**Date:** `2026-09-15`
**Branch/commit:** `codex/water-depth-light`. Session 94's documentation `58d0729`; checkpoint `8cd445f` (0.4.84). This documentation pass is committed on top.
**Mod version:** `0.4.84`
**Assist protocol / blob / schema:** `1 / 4 / 6`. The silhouette tile store keeps its own schema `1`.

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

After Session 94 the owner said "Let's continue with development" without choosing among the
proposed next builds. I took option C, my recommendation, which does both of the others:
- A: a side-by-side picture for the owner's by-eye verdict (D8);
- B: sampling land before it is generated, then generating it and comparing.

The owner then asked for this documentation pass, a handoff for another AI, and a commit.

Sources:
- the 0.4.84 test run `p03-cold` (§3);
- decompiled `WgenCommands` (VSEssentials 1.22.7), for where pregen centres.

## Work narrative

### 1. Why two sessions

- Silhouettes are built far in advance. The land they describe is usually generated later, in
  another session.
- Watersheds keeps region data in memory that the world generator also uses (G205).
- In one process, terrain generated after a presample might therefore be built from the sampler's
  cached data, and agree with it for the wrong reason.
- So the presample runs in one server session and is written to disk. A fresh process then
  generates the land and compares.

### 2. What 0.4.84 changed

- **`/vhsilhouetteserver presample [radiusChunks]`** (1–48, default 32).
  - Centre: the caller's chunk, or from the console the world centre. The centre is computed
    exactly as `/wgen pregen` computes it: `MapSizeX / 32 / 2` on both axes, a vanilla quirk.
  - A background thread asks every accelerator about every 4-block cell of the square. Each call is
    timed, and one accelerator finishes before the next starts.
  - It records whether the server held each cell's chunk at the time, and writes
    `ModData/vintagehorizons/silhouette-compare/<world>-presample.csv.gz`.
  - Per accelerator it reports mean, 95% and maximum time per sample, the first sample in each
    512-block map region, and answers exactly at sea level.
- **`compare [radiusChunks]`** replaces the thinned `[points]` sample of chunk corners. It now
  measures every cell the server holds in the square.
  - If a presample exists, it also reports the presampled answers for cells whose chunk was not
    loaded when the presample was taken, and how many presampled answers differ from present ones.
  - It writes `<world>-map-<utc>.csv.gz` with every cell.
- **File format.** `LodHeightSampleFile` writes the gzip CSV files with magic lines
  `# vintagehorizons silhouette presample 1` and `# vintagehorizons silhouette map 1`. Each file is
  written to a temporary copy and moved into place.
- **One run at a time.** Both runs share `ILodBackgroundReport` and one pump. Each ends with an
  "Accelerator presample finished." or "Accelerator comparison finished." line.
- **Labels (G205, G207).**
  - Algernon's sampler now reads "(answering through Watersheds)".
  - Watersheds' sampler has no parenthesis.
  - The rain count reads "matched their rain heights, like an old save's copy or bare ground".
- **`scripts/silhouette-compare.ps1`** runs three steps:
  1. the presample session, which then stops;
  2. the compare session: `/wgen pregen`, wait for the queue, then `compare`;
  3. `scripts/silhouette-compare-render.py`.

  `-NoPresample` skips the first session.
- **`scripts/silhouette-compare-render.py`** (standard library only) draws three PNG panels and an
  HTML page:
  - the real terrain;
  - the accelerator's terrain (presampled where available), with the real coastline in black;
  - the difference: red reads high, blue low, full colour at 8 blocks, magenta where land and sea
    disagree.

### 3. The run (`p03-cold`, 0.4.84)

The world was fresh, with a random seed and the owner's enabled mods as in Session 94.
- **Sea level: 82.** It comes from the world (`sapi.World.SeaLevel`) and is recorded in the map file.
- **Logs.** No warning or error appeared in either session's server log.
- **Stops.** Both sessions exited after `/stop` without being forced.

**Session 1 (presample).**
- **Square.** 270,400 cells around chunk 16000,16000 (radius 32). The server held none of them: a
  dedicated server with no players loaded no terrain at start.
- **Algernon's sampler.** 1.1 s in all: mean under 0.005 ms, 95% 0.01 ms, max 9.1 ms. The first
  sample in each of 25 regions took a median of 0.0 ms and at most 0.8 ms.
- **Watersheds' sampler.** 1.0 s in all: max 3.3 ms; the first sample in a region took at most
  1.0 ms.
- **Sea-level answers.** 21,435 exactly at sea level, from each.

**Session 2 (compare).**
- **Pregen.** Range 32, idle after 448 s.
- **Heights.** From 5,489 column loads, in 25 tiles.
- **Cells.** 270,336 compared; one chunk's 64 cells held no heights.

| | Result (both accelerators, identical) |
|---|---|
| Error median / 90% / 95% / max | 1 / 4 / 4 / 63 blocks |
| Bias | +1.4 (reads high) |
| Land/sea disagreement | 10,060 cells (3.7%); 9,738 of them within 4 blocks of sea level |
| Cells within 4 blocks of sea level | 154,637 (57%) |
| Answers exactly at sea level | 21,435 (real: 29,179) |
| Presampled against present answers | differ at 0 of 270,336 cells |
| Cells off by 8+ / 16+ blocks | 430 (0.16%) / 63 (0.02%); 422 and 61 of them on land or at the coast (counted offline from the map file) |

From the side-by-side, which is the owner's by-eye material. The causes are inferred from the
picture, not traced in code.
- **River valleys.** The sampler reads slightly high over hills, and more along river valleys:
  real generation carves the channels last (G205).
- **Lakes.** The magenta patches are mostly lakes in the real terrain that the sampler turns into low
  land.
- **Coastlines.** Elsewhere the sampled coastlines follow the real ones closely.

### 4. Correction to Session 94

- **What Session 94 said.** It inferred a sea level of about 110 from the default map height, and
  called the worst points "sea floor".
- **What was measured here.** 82.
- **The earlier runs.** They used the same mods and configs, so their sea level was very likely 82
  too; it was not logged. At 82, their worst points (real 63–87) were shallow water and low land.
- **What stands.** Their statistics used the world's real sea level, so only the reading was wrong
  (G208).
- **Fixed.** Session 94 and STATUS are corrected.

### 5. Checks

- **Result.** 24,936 pass (+13). The build has no warnings.
- **New:**
  - the square's cells;
  - presample and map files round-trip: a failed answer reads back as none, no temporary copy is
    left, and a map is never read as a presample;
  - the presample subcommand and its background thread;
  - the console centre matches pregen's;
  - the comparison reads the presample and writes the map;
  - no accelerator is labelled "before rivers".

---

## Delivered

- **0.4.84 (`8cd445f`):** the presample, the cell-by-cell comparison, the map file, the renderer, the
  two-session script and the label fixes. Zip SHA-256
  `135187f8f94863172c60e26ebb064c764a3ddbaf0305b580e0489a4473408a31`, 624,243 bytes,
  copy-installed; older zips untouched.
- **The test run `p03-cold`,** under `.testdata/silhouette-compare/`. Its side-by-side page was sent
  to the owner.
- **Documentation:**
  - this pass;
  - the Session 94 correction;
  - [the silhouette handoff](../plans/HANDOFF_HORIZON_SILHOUETTES.md) for the next agent.

## Decisions

- **Owner, 2026-09-15:**
  - continue development (taken as option C);
  - then the documentation pass, a handoff for another AI, and a commit.
- **The presample runs in a separate session** (§1). Rejected: one session, which could agree for the
  wrong reason.
- **Every cell, not thinned chunk corners,** for both the picture and the statistics. The `points`
  argument became `radiusChunks`.
- **The console centre copies `/wgen pregen`,** quirk included, so all three commands cover one
  square.
- **Awaiting the owner:**
  - the D8 verdict on the side-by-side;
  - if accepted, whether silhouettes use Watersheds' sampler, with previews kept for coasts and
    lakes;
  - whether to correct the +1.4 lean.

## Traps

Promoted to `dev/GOTCHAS.md`:
- G208: sea level comes from the world, not the map height.

Kept here:
- **No terrain at start.** A dedicated server with no players loads no terrain when it starts; the
  presample found nothing held.
- **Pregen's centre.** `/wgen pregen` from the console centres both axes on `MapSizeX / 32 / 2`. On a
  non-square map its square is not at the map's centre.
- **"0.00 ms".** `LodHeightComparison.Describe` prints "0.00 ms" for means under 0.005 ms, and dense
  runs in chunk order are that fast.
- **DocCheck reads the test folder.** DocCheck scans every text file under the repository,
  git-ignored `.testdata` included. 0.4.84's first `compare-report.txt` carried a full path from the
  server log, and failed the personal-path check. With this pass the script writes the test folder
  as `<profile>`, and the one existing report was fixed the same way.

## Flagged and unverified

- **Judgement calls awaiting owner review:**
  - option C, taken without an explicit pick;
  - the file locations under `ModData/vintagehorizons/silhouette-compare/`;
  - the radius cap of 48 chunks.
- **Below established level:**
  - **P0.3's verdict.** The owner has the page, but no verdict yet.
  - **Lakes and river beds.** Why they differ is inferred from the picture, not source-traced.
  - **The earlier runs' sea level.** Very likely 82, but not logged.
  - **Why sea level is 82.** Neither the TerraPrety nor the Watersheds config names it; not traced.
  - **The owner's own worlds' sea level.** Not checked.
  - **Representativeness.** One more world at the world centre; mountains are still thin.
  - **Concurrency.** All sampling ran with generation idle; the presample session generated
    nothing.
  - **Unchanged from Session 94:**
    - the joins;
    - stored-tile read-back after a restart;
    - the error brake;
    - P2's integration gate;
    - GPU cost and the off-state A/B;
    - a dedicated server with players;
    - the `bodanboys` stall.
