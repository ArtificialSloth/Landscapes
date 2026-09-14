# Vintage Horizons - status

> Tier 2: current state and verification evidence. Historical measurements and development
> narrative live in the sessions; durable design lives in `dev/ARCHITECTURE.md`.

**Status date:** `2026-09-14`
**Mod version:** `0.4.76` — current private test build, verified, copy-installed and checkpointed; the owner reported success for the repaired core Generate controls, while final completion/restart and dedicated-server matrices remain open
**Last accepted baseline:** `0.4.64` at commit `dd0394c`, accepted by the owner on 2026-09-12
**Active branch:** `codex/water-depth-light`; current checkpoints 0.4.71 `6a510ae`, 0.4.72 `1cd1040`, 0.4.73 `30858f4`, 0.4.74 `439bc68`, 0.4.75 `088923a`, 0.4.76 `9ea75c5`; nothing pushed
**Target:** Vintage Story 1.22.5+, .NET 10
**Source files:** `112` C# files under `VintageHorizons/src`
**Assist protocol:** `1`
**Blob format:** `4`
**Database schema:** `6`

## Current result

**The core world-generation/LOD-coverage plan is complete.** P1–P5 and P7 landed in
0.4.66–0.4.76. P0 proved the engine's multi-thread pipeline was really active but rejected it for
negligible speedup and scheduling-dependent terrain, so P6 is closed without exposing thread
controls. P8's documentation is Session 91; the remaining runtime matrix is explicit verification
debt, not a gate for beginning the Farseer silhouette plan.

`/vhworldgen` is the deliberate, confirmed exception which writes real terrain to the savegame.
It works in persistence-friendly 16×16-chunk tiles; the default circle begins at the player's tile,
moves through clockwise circular radial bands, and visits nearest columns first. Engine-queue,
capture, durability, load and disk gates bound the job. A durable bitmap supports pause, resume,
stop and an offered manual restart resume. The Generate page shares the Settings editor; if normal
integrated Settings has paused the server, it hands off to standalone `.vhconfig` live controls.
Versions 0.4.74–0.4.76 fixed the L0 2×2-chunk progress map, transient-cache skip provenance,
force-loaded release, action/status races and boundaries waiting on unrelated global dirtiness.

| Generate evidence | Result | Level |
|---|---|---|
| Core owner test | After earlier start and pause/resume failures were fixed, the owner reported “Success” following stop/reopen/fresh-start recovery | Human-tested; full completion/restart matrix still open |
| Full stack, radius 30 | One-thread controls: 448.45 s / 444.81 s; six threads: 437.73 s, 8.50 columns/s versus 8.30–8.37 | Controlled runtime benchmark |
| Minimal stack, radius 15 | One-thread controls: 99.38 s / 99.25 s; six threads: 99.87 s; two threads: 111.46 s | Controlled runtime benchmark |
| Thread activation | Six-thread runs named `worldgen1`–`worldgen5`; two-thread run named `worldgen1`; files held the requested `MaxWorldgenThreads`; no `--reducedthreads` | Config-, launch- and runtime-verified |
| Terrain identity | Full-stack six-thread: 4,050 rows differ where controls agree (3,215 block payloads); minimal six-thread: 1,055/892; minimal two-thread: 408/288 | Offline semantic save comparison; P0 failed |

**P1-P3 remain complete, and distant terrain fills in.**
0.4.66 fixed GPU depth-copy validation under render scaling and 0.4.67 made server cache saving
oldest-first (Session 89). 0.4.68 makes the client take the server half's fuller rows for sections
it already holds. The sibling cache is scanned by rowid at every level; a first sighting and every
parent fill only the columns the client lacks, and a level-0 row the server rewrote after the client
saw it wins column by column (the owner's choice), never erasing columns only the client holds or
replacing observed snow with a generated peek. Merges are recorded in the additive `SiblingMerge`
table once durable. 0.4.69 paces the savegame sweep to the capture queue, and 0.4.70 retires the
`.vhsnowline` comparison, fixing the current snow-line design in place.

| Evidence | Result | Level |
|---|---|---|
| Caches before the fix | 645 of 2,912 client rows behind the server, 1,471,451 columns lacking (L4 95/100, L5 34/36, L6 9/9) | Measured offline (`siblingfield`) |
| 0.4.68 in play | "The vast majority of terrain is filled in now"; by 23:31, 2,207 sections merged, 1,468,006 columns filled, 10,231 replaced by newer saves | Human-tested; log-measured |
| Remaining thin lines | 2,461 chunk columns at vanilla pass 5 of 6 in one-chunk lines, 80 finished but never captured, none absent (23:35) | Measured offline, read-only against live files |
| Scanner (D4) | Old full read 828 ms CPU; key read 15.6 ms; incremental about 0.1 ms; 0.056% of one thread over 130 s offline, 0.04-0.11% in play | Measured offline; log-measured |
| Sweep drops | 4,043 unloaded-before-capture drops in the first 0.4.68 session; paced in 0.4.69 | Log-measured; harness-tested only |

The accepted 0.4.64 Settings result and all earlier rendering results remain in place.

## Distant structures and chiselled blocks

Ruins drew near-white at distance while looking right up close. A chiselled block, a clutter piece
or a cluttered bookshelf has no colour of its own: its look is stored in its block entity, which
only the side holding the loaded chunk can read. The server half has no texture atlas and stores
colour 0, and the client coloured such sections by block type, whose only texture is the game's
missing-texture image (G181). The check meant to catch that image compared an alpha-free atlas
value exactly against an opaque repack and never fired (G180), and a section's palette keeps its
first colour per block id, so the client's own later capture reused the white entry (G182).

- **Stand-ins (settled decision 36).** Where a capture job is created, the mod copies each chiselled
  block's cuboids and materials; the capture worker records the block as the eligible material with
  the most exposed voxel faces, summed per block, ties to the first material. Eligible means
  something distant terrain draws as ground: not snow, liquid, markers, flowers or a block-entity
  block. The chisel's own flags still decide the column's snow. Server and client store the same
  ordinary block, so its colour, tint and band texture come from the existing paths.
- **Placeholder fallbacks.** The missing-texture colour is recognised on RGB against both the atlas
  average and the file mean. A block-entity block whose answer is the placeholder takes another of
  its own textures or neutral grey, and a real entity answer is kept. A plain block whose own answer
  is the placeholder keeps it, because the game draws a removed mod's block the same way up close. A
  block with no colour texture stays grey.
- **Existing caches.** Stored placeholder colours on block-entity blocks are repaired to that
  fallback on load and written back. Their real materials arrive when the ground is captured again.

| Evidence | Result | Level |
|---|---|---|
| Cache, 0.4.61 | 2,078 structure columns of 10+ chiselled, clutter or bookshelf blocks stored near-white against 62 real; the whites are exactly the missing-texture average | Measured offline |
| Engine, 1.22.7 | A chiselled block's type-level colour is its placeholder texture; its contents are cuboids plus material ids | Source-traced (decompiled) |
| Checks | 51 assertions: engine encoding parity, exposed faces, most-visible against majority-by-volume, placeholder forms, load repair, capture over proxy chunks | Harness-tested |
| Owner's first 30 s on 0.4.62 | 4,496 chiselled blocks captured as their material, 28 with no material that can stand in, 0 failed reads; 1,105 stored placeholder colours repaired | Log-measured (client captures only) |
| 0.4.62 in play | "Excellent! Looks fantastic." | Human-tested |

Limits: clutter, bookshelves, ground storage and lamps have no single material and take one of their
own textures' colours or grey; structures cached before 0.4.62 stay grey until recaptured; the
server half's stand-in counts are not in the client log; the cost of copying chisel contents where
capture jobs are created is not measured.

## Distant anti-aliasing

The shimmer was sub-pixel aliasing. Past about a thousand blocks one rendered pixel covers several
blocks but shows only the one under its centre, so a camera movement smaller than a pixel swaps a
white snow top for the dark wall beside it. The fix nudges distant cached terrain by a different
fraction of a pixel every frame and blends each pixel with where the same ground was in earlier
frames, so it settles on the average of what it covers.

- **Ownership.** A depth snapshot taken before the first cached draw marks the pixels cached terrain
  drew. Only those, and sky pixels beside them, are nudged, blended and written back; vanilla
  terrain, creatures and water keep their pixels (settled decision 34).
- **Strength** follows on-screen block size against the reach: none where a block is the reach or
  wider, full where it is half as wide. History reaches full share over the first half of that fade
  and the nudge comes in over the second.
- **Resolve.** Catmull-Rom history re-sampling; a safety check against the mean +/- clip standard
  deviations of the 3x3 neighbourhood, inside its range; history only from cached terrain or sky;
  the still memory only once the whole camera has held still, eased in over 16 frames; fast turns
  keep 85% of the turning memory.
- **Composite.** Writes back only the pixels the resolve owns, with contrast-adaptive sharpening
  that never reaches the history.
- **Failure.** A shader compile failure or an early GL error stops the feature for the session with
  one log line, and terrain draws as it did without it. No GLSL validator is installed here, so the
  shaders are only ever compiled in game.

| Setting | Command | Default (owner's tuning) | Range | Before 0.4.61 |
|---|---|---|---|---|
| Reach | `.vhtaa reach` | 2.5 px | 1-32 | 2 |
| Nudge | `.vhtaa nudge` | 0.5 | 0-1 | 1 |
| Sharpen | `.vhtaa sharpen` | 1 | 0-1 | 0.5 |
| Memory while turning | `.vhtaa blend` | 0.97 | 0-0.97 | 0.9 |
| Memory when still | `.vhtaa still` | 0.99 | 0-0.99 | 0.97 |
| Smear guard | `.vhtaa clip` | 2.25 | 0.5-4 | 1.25 |

All are saved, all are sliders on the `.vhconfig` Anti-Aliasing tab, and `.vhtaa reset` restores
the defaults. The owner kept these ranges on 2026-09-12, although sharpen and both memories sit at
their tops. `.vhtaa` with no argument reports the state, the reach in blocks, the dials, the share of
frames with the camera fully still, history resets and GPU cost, in chat and the client log.
`.vhtaa show` tints what it touches.

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

With **Automatic** on (the default), L1 is where a level-0 column reaches 1.5 px on screen
(`.vhdetail target`), and each later level doubles it, so every level hands over at the same
on-screen size (settled decision 35). Level 0 is held past the vanilla view distance, the blend band
and one section. At the owner's settings (60 degrees, 1,080 rendered rows, view distance 128, band
128) that gives 624 / 1,248 / 2,496 / 4,992 / 9,984 / 19,968 blocks; his own list stays in the file
for `.vhdetail manual`. It re-selects at most once a second, only when the render height, FOV, floor
or target changes, and logs what it applies. In use since 0.4.57 without complaint; not evaluated
at other FOVs, render scales or view distances.

## Settings integration and editor

In an active world, the vanilla Settings header has a dedicated **Horizons** tab. Vintage Story
1.22 exposes no public page registry, so one guarded adapter extends the private
`GuiCompositeSettings.ComposerHeader` seam and the established Settings leave paths. It validates
every reflected member before patching; if the seam moves, vanilla Settings remains usable and the
log directs the player to `.vhconfig`. Both entries compose one stateful editor (settled decision
37), so behavior cannot drift between two implementations.

- **General** holds automatic/manual LOD distances, far draw cap, the saved **Load terrain while
  paused** switch, and visual features with presets. **Anti-Aliasing** holds a slider for every
  dial. Descriptions appear on hover; secondary tabs sit below the vanilla header; compact rows and
  independently placed actions keep text and controls inside the page. Defaults resets the current
  page. Save keeps mod changes; Cancel or closing restores live anti-aliasing previews.
- **Distance map.** A logarithmic map begins at 32 blocks. Vanilla's range is neutral grey; the
  coloured and labelled L0-L6 spans begin at its edge, and each L1-L6 marker frame matches the
  region it introduces. This picture is explanatory and does not remove the renderer's internal
  seam overlap.
- **Vanilla render distance.** The white Vanilla marker is a second control for
  `ClientSettings.ViewDistance`, not mod configuration. It uses the game's exact 32-1,536 range,
  32-block increments, mouse-up commit and Graphics-Custom side effect, then refreshes automatic
  thresholds immediately. Mod Cancel deliberately does not undo it.
- **Paused loading.** Render stages continue when ordinary single-player game ticks stop. When the
  saved default-on switch is enabled, the renderer invokes the same bounded owner-thread LOD work
  at 50 ms cadence; it neither unpauses nor advances simulation. Source and harness verification
  are complete; a direct runtime progress observation remains open.
- **Presets.** Quality now equals the deliberate default visual set and leaves costly Far Biome
  Colors off. Players may enable the feature individually, producing a Custom configuration.

The owner's first 0.4.63 screenshots exposed the remaining tab, colour and Vanilla/L0 separation
defects. He reviewed the corrected 0.4.64 window and said, "This looked good."

## Tree and glacier corrections: accepted

Version 0.4.54 gave a block's top, X walls and Z walls their own atlas rows, so standing logs show
bark on their sides; 0.4.55 makes wall lookup skip the already-selected top texture, so Redwood
quarter-trunks do too, while a lying-log control preserves a real wall-facing cut end. 0.4.55 also
limits liquid classification to Water and Lava: every `EnumBlockMaterial.Ice` block, including the
opaque packed glacier ice of mountain summits, had been drawn as water and written to the liquid
depth that drives underwater murk, painting a dark rim round glacier summits (G173). The owner
confirmed all three on 2026-09-12: Redwood bark on all four sides with a cut-end top, no dark rim on
the captured snowy peak with murk on, and frozen lake and glacier ice reading right as solid terrain.

## Handoff flash result retained

The one-frame flash of open sky at the edge of vanilla's draw range came from deciding range
ownership at Before/0.994, before the player's physics step at Before/1.0 (G170); the lag dates from
0.4.13. 0.4.53's loss-only late range check at Before/1.001 fixed it, and the owner confirmed.
`.vhlaterange off` restores the old timing for comparison. [Session 84](dev/sessions/SESSION_84.md)
holds the evidence and settled decision 33 owns the rule. The owner's earlier build bisection to
0.4.40 is not supported by the code, and why 0.4.39 was judged clean is not established (G172).

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
relevant weather changes. During a finite campaign, `RefreshColumnsPerSecond` defaults to 1, clamps
to 0–16, and 0 disables automatic refresh. Work stays inside the sweep's generated-column map and
shares a 1 ms owning-thread issue budget.

## Storage, wire, and replacement semantics

Section blob format 4 has two optional append-only 512-byte maps before compression: observed snow
provenance and covered state. Old readers ignore the tail. Current readers treat a missing tail as
unknown, reject partial tails, and downgrade provenance-only 0.4.43 rows to unknown rather than
falsely asserting bare terrain. Derived snow palette flags use bits 32 and 64 and are reclassified
from live blocks. No cache purge or schema-meaning change is required.

Protocol 1 added optional `AssistServerSettings` field 16 for the refresh rate and
`AssistKeyManifest` field 4 to distinguish newer durable contents from a newly discovered key. Older
protobuf peers ignore them. Versions 0.4.74–0.4.75 append `AssistWelcome.Features` field 7 and
`CanControlWorldGeneration` field 8, server-settings fields 17–23, the
`AssistWorldGenRequest`/`AssistWorldGenStatus` message types, and status `Action` field 28. Feature
bits reserve 0 for Generate, 1 for frontier and 2 for silhouettes. Since 0.4.68 a refreshed row is merged into the client's copy column by
column instead of replacing it, and wins only while the client's content revision is unchanged since
the row was handed to the decoder; otherwise it only fills. Only L0 is transferred; parents rebuild
locally. The compatibility numbers remain `1 / 4 / 6`; `dev/WIRE_HISTORY.md` records the
append-compatible extensions.

Since 0.4.62 a chiselled position stores its stand-in's block code, for example planks where the
world holds a chisel. Every reader already resolves an ordinary block code, so neither the blob
format nor the schema meaning moved; `dev/WIRE_HISTORY.md` carries the note. Sessions 84-89 changed
nothing on the wire or in the blob format; Sessions 86 and 88 added client configuration keys only.
Session 90 added the client table `SiblingMerge`, recording which server-cache rows a stored copy
already contains; it is additive. Session 91's worldgen state is a separate mod-data plan, not a
section-schema meaning. The numbers stay `1 / 4 / 6`.

## Diagnostics

Session-only unless stated; all write their findings to the client log.

- `.vhtaa` reports distant anti-aliasing (above); `.vhtaa show` tints what it touches.
- The 30-second report's `chiselled blocks:` line (0.4.62) counts chiselled blocks captured as their
  material, those with no material that can stand in, failed reads, and stored placeholder colours
  repaired on load.
- `.vhdiag on|off` (0.4.56) shows or hides the diagnostics side panel for the session; off by
  default. The panel lists every `.vhtaa` dial, saved and live.
- `.vhsky on|off` counts sky below the horizon after all opaque terrain is drawn and logs each flash
  with the captured mask; `.vhsky verify on|off` reads the mask texture back after every upload.
- `.vhlaterange on|off` switches the 0.4.53 fix; on by default.
- `.vhgap on|off` and `.vhgap release on|off` log, and optionally release, cells the mask gives to
  vanilla while vanilla is not drawing them. The scan still runs before the physics step.
- `.vhwater capture` stores a frame's colour and depth stages with the projection; Session 86 measured
  the shimmer from one.

## Performance and artifact evidence

The final fast tier passed **24,747 assertions with zero failures**, and the 0.4.76 Release and
Debug builds completed with zero warnings and zero errors. The sibling-cache scanner's cost is in the
current-result table; merging into held sections runs under the existing install budget and is not
separately measured. Distant anti-aliasing's measured cost is in its table above; automatic
thresholds cost nothing per frame unless their inputs change. The pause-time path
runs the already-bounded owner-thread work at 50 ms cadence but has no paired frame-time capture.
Chisel stand-ins add a copy of each chiselled block's contents where a capture job is created and
arithmetic on the capture worker; neither is measured. The late range check of 0.4.53 sweeps only on
frames where the physics step changed the player's block; snow adds 1,024 uncompressed bits-array
bytes per resident section before compression; no paired large-world capture yet isolates winter
campaign cost.

The verified `dist/vintagehorizons_0.4.76.zip` is 583,065 bytes and has SHA-256
`E0E9A9EC98A554334EA5E466627CD851C9DD54165B327D3456CF5F3E4CB40028`; `modinfo.json` reports
0.4.76 and the verified artifact was copy-installed during its build workflow. Older test zips
remain in that folder. Every incremented private
test build receives an exact Git checkpoint after verification and installation; this does not
authorize a push, tag, publication or public release. The assistant did not launch the game in
Session 91's controlled servers ran only after the owner's approval; no additional game launch was
needed for this documentation pass.

Documentation validation passed **9,488 checks** with no failures at the Session 91 doc pass.
Current version, changelog coverage, source count, compatibility, links, session routing, shader
ASCII and active-plan routing pass.

## Established product state retained

- Version 0.4.64 is the accepted baseline (owner, 2026-09-12). Its water keeps the settled seam and
  narrowly scoped loading guard accepted in 0.4.39.
- The client remains useful on an unmodified server. Server capture, safe sweeping, optional
  generation, dormant snow refresh and live assist remain additive.
- Packed clustered terrain is still the required visible opaque path; dedicated OIT water remains
  required. Automatic plausible renderer fallback remains disabled.
- Distant anti-aliasing never changes a vanilla pixel and never keys anything to a fixed distance.
- A block is recorded as what it shows: a chiselled block as its most visible material (settled
  decision 36).
- Settings has one editor with two entry points, while the mirrored vanilla distance remains
  vanilla-owned and pause loading reuses bounded owner-thread work (settled decision 37).
- The handoff fix changes when ownership's range half is decided, not what ownership means. Water
  development still may not redefine shared terrain ownership.
- A held section is merged, never replaced: first sightings and parents fill only, and a level-0 row
  the server rewrote after the client saw it wins column by column (owner, 2026-09-12; G188).
- Real saved terrain is generated only by an explicit, privileged, confirmed `/vhworldgen` job;
  sweep, `/vhgen` and automatic frontier work retain the generates-nothing promise (settled
  decision 38).
- The automatic frontier never monopolizes generation authority; it yields to explicit
  `/vhworldgen` and `/vhgen` work (settled decision 39).
- Multi-thread generation is not a player option. The engine setting was active in P0, but failed
  both the material-speedup and terrain-identity gates (G198).

## Next work

Begin with [Session 91](dev/sessions/SESSION_91.md), the top of [TODO](dev/TODO.md), and
[the horizon-silhouette/frontier plan](dev/plans/PLAN_HORIZON_SILHOUETTES_AND_FRONTIER.md).

1. The owner chose Farseer silhouettes next. Preserve the shared-authority rule: an automatic
   travelling frontier yields immediately to explicit `/vhworldgen` and `/vhgen` jobs.
2. Worldgen's remaining checks are not gates for silhouette work: final durable completion, a real
   restart/offered resume, dedicated permissions/read-only status, and a long-run memory/disk pass.
3. The fixed 15-second shutdown-save deadline still needs the W8 config setting if this plan is
   revisited. The owner deferred starting a new tile while the preceding tile saves.
4. Runtime-check the 0.4.69 sweep pacing/drop line and preserve older TODO debt including P3's
   after-comparison, winter convergence, pause loading and Session 84 diagnostic cleanup.

[Session 91](dev/sessions/SESSION_91.md) is the authoritative continuation point. Sessions 90-82
retain the delivery, Settings, structure-colour, anti-aliasing, tree/ice, handoff, winter and water
history. Architecture owns the established invariants, `dev/WIRE_HISTORY.md` owns compatibility,
and G166-G198 in `dev/GOTCHAS.md` preserve the recent traps.

The active performance route remains
[the main-thread performance plan](dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md); GPU renderer work
is further specified by
[the GPU-driven terrain renderer plan](dev/plans/PLAN_GPU_DRIVEN_TERRAIN_RENDERER.md).
