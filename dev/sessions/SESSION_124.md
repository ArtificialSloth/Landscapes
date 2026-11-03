# Session 124 — 0.5.2-dev.32: what each setting costs, Farseer's coverage remembered, and new presets

**Date:** `2026-11-02..03`
**Branch/commit:** `main`; `fa66720` (dev.28), `7d845ac` (dev.29), `c20b06f` (dev.30), `38c0bd1` (dev.31), `a99b88e` (dev.32);
bench tools `547df8f`, `afc5f35`, `fb9f272`. Not pushed.
**Mod version:** `0.5.2-dev.32` (a test build; the owner played dev.31 on 1.22.7 with his AMD card)
**Assist protocol / blob / schema:** `1 / 4 / 6` (unchanged)

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

Session 123 designed a per-setting benchmark and left it for this session. The owner wanted the cost of every setting
before he re-designated the presets. The benchmarks ran in the isolated sandbox on a frozen copy of `vintage cave tales-2`
(cache `71c0c6af`) with his eight enabled mods (algernonsterrainsampler, betterruins, placeonslabs, synergy, terrainslabs,
terraprety, tungsten, watersheds), his graphics settings, a Release build, the sweep off and the clock stopped at noon.
Each launch was asked for and approved. His spot: the valley at 513012, 225, 514130 facing north, closed in by mountains.
He chose view distance 32 instead of Session 123's 256, because it "puts the most load onto our mod". Frame times are on
his RX 9070 XT at 2560x1440 at 0.75 render scale. The bench's GPU statistics wrote nothing, so there is no GPU/CPU split.

## Work narrative

### 1. The tools (dev.28, bench `547df8f`, `afc5f35`)

`.dlset <name> <value>` sets any settings-window row by its key and saves through the window's own path, plus band reach,
shore foam and the water depth pass. Band reach got a slider in a new Developer group, Seam band (owner: Developer options
only). The bench gained `-Configuration Release`, `-ExtraMods`, `-PlayerSettings`, `-ViewDistance`, and marks the mod's
first-run prompt and login notice seen. The pilot (13:27-13:42) ran on Quality because that prompt replaced the route's
first preset, and the clock ran to sunset. Routes now freeze time. Settled stops agreed to about 0.05 ms.

### 2. One setting at a time (2026-11-02)

**From Ultra down** (`setting-sweep.txt`, 14:22-15:27). Baselines 4.56-4.63 ms. Savings: detail target 1.5/2/3 =
0.57/0.80/1.03 ms; Cache 2,048 0.29, 4,096 about 0; seam fog 0.11; seam blending 0.10; Close Biome (taking Far) 0.05-0.09;
tree colours and slabs about 0.05. Everything else read under the floor: anti-aliasing, Farseer, its reach, band reach,
every plant switch, water effects, shore foam, the depth pass and Distant Lights. Leaf Crossed Planes with Tops cost 0.16
mean / 0.22 median more than Clusters with Tops, steadily. Clusters' +0.24 was one 0.8 s game block-tick freeze.

**From Performance up** (`setting-climb.txt`, 16:21-17:25). The floor was Performance with Farseer, plants, foam and the
depth pass off: 1.55 ms, thresholds 304/608/1216/2432/4864/9728. Costs turned on alone: Farseer reach 4,096 to 8,192 at Cache
2,048 +0.94; detail 3 to 2/1.5/1 +0.29/+0.57/+1.10; Farseer on +0.34; Cache 4,096 +0.17 and 8,192 the same; anti-aliasing
+0.12; leaf shapes against cubes +0.04 to +0.12; band 33% to 100% +0.07; Close biome +0.06, Far about +0.01; seam fog +0.05;
seam blending +0.04; the rest about 0.

### 3. Farseer's cost was its coverage answer (dev.29-dev.31)

`farseer-cache.txt` (18:04-18:30) disproved the idea that Farseer cost the gap between the Cache and its reach. Its cost
followed its reach at every Cache distance: 2,048 +0.08-0.13, 4,096 +0.34-0.41, 8,192 +1.4-1.5 ms. Mesh density changed
nothing. Drawing was cheap: 247 tiles at reach 8,192, about 5 us a tile. dev.29 skips the coverage question for tiles
wholly past the terrain's end, skips drawing tiles wholly covered, and logs a "Farseer per frame" line. It took Cache 2,048
with reach 8,192 from 3.14 to 1.60 ms. At Cache 8,192 the prepare step still took 1.75-1.8 ms: every in-view tile inside
the terrain re-asked which cached sections covered it, every frame, about 8 us a tile (G335).

dev.30 remembers each tile's answer. Each tile keeps masks of which sections are resident per level, updated as a mesh is
installed or removed. The answer is reused while the camera has moved less than the distance to the nearest level handover
or cull edge, and while the terrain's end has moved less than its own margin. Readiness is still applied fresh every frame.
`.dlsilhouette coverage cached|fresh|check`: check compares every remembered answer with a fresh one and logs differences.
The owner asked why crossing a 32-block chunk line should redo 512-block tiles. dev.30 had put the snapped camera into the
context, so it did redo every tile (G336). dev.31 spends the snapped camera's jump out of the end margin, so only tiles near
the terrain's end redo. An offline check walks 600 random cases of eight moves inside the margins with 0 differences; moving
past them gave 445 (dev.30) and 850 (dev.31) differences, so it can fail.

dev.31 medians against dev.29: Cache 8,192 + reach 8,192 detail 3 2.991 to 1.727 ms (Farseer off 1.746); detail 2 3.339 to
2.187; Cache 4,096 + reach 8,192 2.097 to 1.728 and 2.413 to 2.206. Prepare is about 35 us at any Cache. The checker compared
about 30 million answers over eight stops, including 40- and 120-block teleports and a turn, with 0 differences. The owner
played dev.31 in his own world with the checker on from 21:36, flew into new land: 0 differences at 21:44:50, "Everything
looked good". The checker stays, off by default (decision 78).

### 4. Leaves, level 0 and Far Biome Colors

Clusters with Tops costs at most about 0.1 ms and the owner finds it best at every distance, so his idea of cubes or
clusters past the seam band was dropped. Level 0 is tied to vanilla's view distance because automatic thresholds hold it past
vanilla's edge, the band and one section (decision 35). The owner felt Quality's handover at 624 blocks was too close; by
hand at 960/1536/2496/4096/8192/16384 it "feels much better".

He asked whether Far Biome Colors had broken, since a "most expensive" option measured free. It has not. The switch reaches
the shader, and the cut-off uses the column size the geometry is built from. Screenshots with it on and off differ only by
noise, while Close off changes the picture a lot. The client holds real climate only for map regions it was sent (about
1,000 blocks on arrival, toward 3,000 as the player explores) and fills the rest from the nearest known cell. Far starts at
L3, 1,216 blocks or more, so far ground mostly gets the same climate either way, and far mountains are mostly untinted rock
and snow (G337). Its old label came from Session 77's 80 us for all biome colour, the largest of five small costs. It stays
its own switch (owner): a small saving left off, and useful for custom settings.

### 5. Each proposed preset measured whole, then built (dev.32)

The proposal followed his goals: Performance fast at the expense of looks; Balanced more to see without losing frame rate;
Quality a significant step up sparing the very costly; Max (Ultra renamed) the finest, with nothing uselessly expensive;
anything essentially free in every preset. The single-setting costs were measured at one set of distances, and level-0
features grow with level 0, so each preset was measured whole (`preset-proposal.txt`, 2026-11-03 01:40-02:25). It used the
valley and a spot 330 high looking across the plain to far mountains. Bench `-LodThresholds` gave Quality his hand-tried
distances. The source save had changed since the seed was frozen, so the frozen copies were passed as their own sources (G338).

Frame ms, valley / open spot: today's Performance 1.63, proposed 1.69 / 1.50; today's Balanced 2.83, proposed 2.96 / 2.64;
today's Quality 3.38, proposed at 624 3.41 / 3.32, at 960 3.92 / 3.79; Max 4.57 / 4.45. Switching features off inside a preset
saved 0.02-0.11 for leaf cubes, 0.10-0.11 for plants, 0.05 / 0.15 / 0.18 for seam blending on Balanced / Quality / Max, and
0.05 or less for far biome. Level-0 features did not grow with level 0; the distance cost is the full-detail terrain itself,
+0.5 ms for Quality's level 0 at 960 (G340).

dev.32 builds the presets (decision 79). Quality's rule is a new setting, `Level0Pixels`: the on-screen width where L1
begins when finer than the detail target, with every later level on the target. Quality is 1 px, giving 928/1248/2496/... at
1080 rows and 60 degrees. It travels with the visual switches, so presets set it, preset matching compares it and Save keeps
it. A config without it loads as following the target, so nobody's distances move until they pick a preset (G339). Owner's
OK before the build: "Yes, build it".

---

## Delivered

- **0.5.2-dev.28** (`fa66720`): `.dlset` for every window row plus band reach, shore foam and the depth pass; the Developer
  tab's Seam band group with a Band reach slider.
- **0.5.2-dev.29** (`7d845ac`): Farseer skips tiles past the terrain's end and tiles wholly covered; reused uniform vectors;
  the periodic "Farseer per frame" line.
- **0.5.2-dev.30** (`c20b06f`): Farseer's per-tile coverage remembered between frames
  (`LodTerrainRenderer.SilhouetteCoverage.cs`), `.dlsilhouette coverage cached|fresh|check`, the "Farseer coverage" line.
- **0.5.2-dev.31** (`38c0bd1`): chunk-line crossings redo only tiles near the terrain's end.
- **0.5.2-dev.32** (`a99b88e`): the four presets; `Level0Pixels` with a "Level 0 detail target" slider in Developer > Seam band
  and `.dlset level0-target`; Ultra renamed Max (`.dlset preset max`).
- Bench (`547df8f`, `afc5f35`, `fb9f272`): Release builds, extra mods, the player's settings, view distance, prompt marked
  seen, `-LodThresholds`; routes `setting-sweep-pilot`, `setting-sweep`, `setting-climb`, `farseer-cache`, `farseer-timing`,
  `farseer-cache-check`, `preset-proposal`.
- Each build verified, copy-installed to both Mods folders, compiled against 1.22.7; none pushed. Checks on 1.22.0 and
  1.22.7, 0 failures: 27,001 (dev.29), 27,003 (dev.31), 27,014 (dev.32).
- Documentation (2026-11-03, at the owner's request): this record, CHANGELOG, GOTCHAS G335-G340, ARCHITECTURE decisions
  78-79 with 35, 63 and 77 brought up to date, TODO, DONE, STATUS and the private working rules.

## Decisions

- **Bench at view distance 32** (owner), with his mods and settings, on a Release build.
- **Farseer's tile coverage is remembered between frames inside margins** (decision 78); the checker stays, off by default.
- **Leaf shapes are not split by distance** (owner); Clusters with Tops everywhere it is on.
- **Far Biome Colors stays its own switch** (owner).
- **The presets** (owner, decision 79): Performance keeps every free switch and Farseer reach 8,192; Balanced Cache 8,192,
  seam blending over a third of level 0, far biome, flowers; Quality far biome, flowers and level 0 to 1 px; Max is Ultra.
  Rejected: Quality's L1 by a block distance (decision 63 keys presets to the screen); a fixed rule tying level 0 to the target.
- **Level 0's own size sits in the Seam band group** beside Band reach (agent's call; the anti-aliasing group has no room
  within the 640-unit window).

## Traps

- **Farseer re-asked each tile's coverage every frame** (G335). `.dlsilhouette stats` omits the prepare time while the
  seam fog is off; the periodic line has it.
- **A remembered answer keyed to the snapped camera is forgotten at every chunk line** (G336).
- **Far Biome Colors measures free because it changes almost nothing** (G337).
- **Bench sandbox traps:** the first-run prompt, a running clock, `-ChunkMask`, a changed source save, and statistics that
  fire once at 30 s (G338).
- **A new config key must load as no change** (G339).
- **Costs measured one setting at a time at one set of distances do not price a preset** (G340).
- **Moving code to a new file broke source-text checks** that read one file; the checks' `pass` strings now join both.

## Flagged and unverified

Judgement calls awaiting human review:

- The window's cost tags disagree with the measurements: biome colours read "High" (0.05 ms or less), seam blending
  "Moderate" (0.05-0.18). The owner wants them relabelled (TODO).
- The Level 0 detail target slider's place and wording; Performance's 1% lows at the valley went from 2.7 to 3.4 ms with the
  free switches on, on 20-second measures.
- Existing players' saved settings no longer match a preset, so the window shows Custom until they pick one.

Claims without evidence at the level needed:

- **dev.32 unplayed.** Quality's built distances (928/1248/2496 at 1080 rows) differ from his hand-tried
  960/1536/2496/4096/8192/16384; its cost was measured on his list, not on the built rule.
- **One machine, one world, 1080-row rendering, view distance 32.** No GPU/CPU split, so card-bound costs may read free on his
  RX 9070 XT. NVIDIA untested since dev.22.
- **The coverage cache under continuous walking** is covered by his play-test and the offline check; the bench only teleports.
- **Far Biome Colors' look** was never compared at a biome edge 1-3 km away inside known climate.
