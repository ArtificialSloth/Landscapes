# Vintage Horizons - status

> Tier 2: current state and verification evidence. Historical measurements and development
> narrative live in the sessions; durable design lives in `dev/ARCHITECTURE.md`.

**Status date:** `2026-09-17`
**Mod version:** `0.4.101` — current private test build (Farseer P1-P4 and most P5 work), verified, copy-installed and checkpointed; the all-level mesh-exact handoff and ridge contrast have not run in game
**Last accepted baseline:** `0.4.64` at commit `dd0394c`, accepted by the owner on 2026-09-12
**Active branch:** `codex/water-depth-light`; recent checkpoints 0.4.95 `020f3e5`, 0.4.96 `8cb90e7`, 0.4.97 `62d5f29`, 0.4.98 `d6aae26`, 0.4.99 `85b7f20`, 0.4.100 `df4bf43`, 0.4.101 `a137181`; nothing pushed
**Target:** Vintage Story 1.22.5+, .NET 10
**Source files:** `131` C# files under `VintageHorizons/src`
**Assist protocol:** `1`
**Blob format:** `4`
**Database schema:** `6`

## Current result

**Farseer: P1-P4 and most P5 built (0.4.77–0.4.101).** Sessions 92–98.
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
- **Persistence, scheduling and settings.** A lazy client store reads nearest saved tiles for
  instant rejoin and closes when off. Decode/mesh work is capped off-thread; owner publication,
  coverage, pruning, remesh and upload are bounded. The **Farseer** tab persists enable, reach,
  Outline detail, Hill softness, apron width/depth and classic-look dials; cached draw and reach
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
| Final checks | 25,137 assertions pass; Release mod and benchmark projects have zero warnings/errors | Harness-/build-tested at 0.4.101 |
| 0.4.101 artifact | 41 root-relative entries, one DLL, no PDB/wrapper, matching installed copy; SHA-256 `14CAC44C0C41DC26F48155DC2352A22E90EF123CE0C5B72F21B4734A99C8B392` | Artifact-verified |
| 0.4.101 exact suppression, no-gap loading handoff, ridge contrast, final GLSL, weather look, controlled GPU/off A/B, cold/full-disc read-back, dedicated players and mixed versions | Not observed or measured | — |

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
- **0.4.69** paces the savegame sweep to the capture queue.
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

**Checks and builds.** The final fast tier passed **25,137 assertions with zero failures** at
0.4.101. The Release mod build and benchmark project build completed with zero warnings and zero
errors.

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
  - Render upload is bounded to four tiles, 2 MiB and 2 ms per frame; live GPU time is unmeasured.
  - An accelerator height took 0.01–0.02 ms in freshly generated land (Session 94). On land nobody
    had generated it took under 0.01 ms, and under 1 ms for a new region's first sample (Session 95).
    Sampling beside real generation is not measured.

**The 0.4.101 artifact.** The verified `dist/vintagehorizons_0.4.101.zip` is 654,148 bytes and has
SHA-256 `14CAC44C0C41DC26F48155DC2352A22E90EF123CE0C5B72F21B4734A99C8B392`. Its 41 root-relative file
entries are readable, its DLL matches Release, `modinfo.json` reports 0.4.101, it contains no PDB or
wrapper directory, and it was copy-installed with a matching hash. Older test zips remain in that
folder.

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
- Session 91's controlled servers ran only after the owner's approval.

**Documentation.** Validation passed **10,664 checks** with no failures at the Session 98 doc pass.
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
- **Farseer ownership.** Farseer is fallback scenery and may never show over ready vanilla or LOD
  terrain, including below excavated vanilla ground. It remains until live replacement coverage is
  proven. The proof comes from stable vanilla readiness and exact all-level installed-mesh
  coverage, never cached data alone, the current frustum or camera yaw (settled decisions 42 and
  45–46).
- **Thread count.** Multi-thread generation is not a player option. The engine setting was active in
  P0, but failed both the material-speedup and terrain-identity gates (G198).

## Next work

Begin with [the Farseer handoff](dev/plans/HANDOFF_HORIZON_SILHOUETTES.md),
[Session 98](dev/sessions/SESSION_98.md), the top of [TODO](dev/TODO.md), and
[the horizon-silhouette/frontier plan](dev/plans/PLAN_HORIZON_SILHOUETTES_AND_FRONTIER.md).

1. **Run 0.4.101's focused ownership gate:** at the same screenshot locations, turn in place,
   approach vanilla/LOD edges obliquely, watch rapid loading/unloading and look into excavated
   vanilla ground. There must be no view-dependent shift, Farseer over ready terrain, or sky gap
   before replacement. Judge whether the new contrast separates overlapping ridges naturally.
   This also proves the changed shader compiles.
2. **Close P3's deferred integration debt** when another player is available: a complete
   singleplayer disc and restart read-back, then dedicated opt-in, reach clamp, fairness,
   disconnect/off cleanup and mixed versions. The P2 `/vhworldgen`-to-tile check also remains.
3. **Finish P5 evidence/credit:** after suppression is accepted, tune final Outline detail, Hill
   softness and apron defaults; measure controlled GPU/off and regeneration hitches; refine
   islands/inlets only if needed; finish the deliberate player-facing Farseer rename plus bundled
   MIT notice.
4. **P6 is Terrain Ahead,** a separate feature; P7 canopy follows its acceptance.
5. **Worldgen's remaining checks** are not gates: final durable completion, a real restart and
   offered resume, dedicated permissions and read-only status, and a long-run memory and disk pass.
6. **Older TODO debt:** runtime-check the 0.4.69 sweep pacing and drop line, and keep the debt
   including:
   - P3's after-comparison;
   - winter convergence;
   - pause loading;
   - Session 84 diagnostic cleanup.

[Session 98](dev/sessions/SESSION_98.md) is the authoritative continuation point, and
[the silhouette handoff](dev/plans/HANDOFF_HORIZON_SILHOUETTES.md) orients a new agent.
- Sessions 97-82 retain Farseer persistence/tuning, the silhouette comparison, P2 and the first
  look, and the Generate,
  delivery, Settings, structure-colour, anti-aliasing, tree/ice, handoff, winter and water history.
- Architecture owns the established invariants, and `dev/WIRE_HISTORY.md` owns compatibility.
- G166-G216 in `dev/GOTCHAS.md` preserve the recent proven traps, including client-store startup,
  buried joins, bounded regeneration and the stable all-level Farseer handoff.

The active performance route remains
[the main-thread performance plan](dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md). GPU renderer work
is further specified by
[the GPU-driven terrain renderer plan](dev/plans/PLAN_GPU_DRIVEN_TERRAIN_RENDERER.md).
