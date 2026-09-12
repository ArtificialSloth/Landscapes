# Vintage Horizons - status

> Tier 2: current state and verification evidence. Historical measurements and development
> narrative live in the sessions; durable design lives in `dev/ARCHITECTURE.md`.

**Status date:** `2026-09-12`
**Mod version:** `0.4.61` — current test build, verified, copy-installed and checkpointed; it makes the owner's in-game anti-aliasing tuning the defaults
**Last accepted baseline:** `0.4.39` at base commit `8ace702`; 0.4.53's handoff-flash fix and Session 86's distant anti-aliasing are human-tested, but baseline promotion remains the owner's call
**Active branch:** `codex/water-depth-light`; test-build checkpoints 0.4.56 `223bfbb`, 0.4.57 `1dc2907`, 0.4.58 `f255b08`, 0.4.59 `c4fbbdd`, 0.4.60 `092f518` and 0.4.61 `b1fd007`; nothing pushed
**Target:** Vintage Story 1.22.5+, .NET 10
**Source files:** `107` C# files under `VintageHorizons/src`
**Assist protocol:** `1`
**Blob format:** `4`
**Database schema:** `6`

## Current result

**Distant terrain no longer shimmers as the camera moves.** Distant anti-aliasing (0.4.57-0.4.61)
blends the mod's distant terrain over several frames. The owner tuned it by eye on 0.4.60's new
Anti-Aliasing tab until it looked "very good", with no remaining vibrating spots, and 0.4.61 makes
his values the defaults. Automatic LOD thresholds, on by default since 0.4.57, hand every level over
at the same size on screen. 0.4.55's Redwood and glacier corrections still await his visual check,
and 0.4.53's handoff-flash fix stands.

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
the defaults. `.vhtaa` with no argument reports the state, the reach in blocks, the dials, the
share of frames with the camera fully still, history resets and GPU cost, in chat and the client
log. `.vhtaa show` tints what it touches.

| Evidence | Result | Level |
|---|---|---|
| Capture, 0.4.54 | One block is one pixel at 935 blocks (60 degrees, 1,080 rendered rows); past 512 blocks 79-89% of snow pixels were one row tall | Measured offline |
| FOV 60 to 20 | Shimmer greatly diminished at nearly unchanged depth precision | Human-tested |
| 0.4.57 | Cleaner, but vibrated with a still camera; 17.9-23.1% of anti-aliased pixels failed the per-pixel depth test (G175) | Human-tested; capture-measured |
| 0.4.58 | Steady when still; smeared, even standing still | Human-tested |
| 0.4.59-0.4.60 | No vibrating spots; tuned "to look very good"; both tabs and all sliders work | Human-tested |
| Cost | Resolve and composite p95 100 us, depth snapshot p95 25 us, over about 464,000 frames at 1920x1080 rendered on an RX 9070 XT | Log-measured, owner's session |
| Stillness | 63-67% of resolved frames had the camera fully still | Log-measured, owner's session |
| 0.4.61 | Defaults equal the owner's saved values; the packaged DLL's constants verified by decompiling | Harness- and artifact-verified; not run |

## Automatic LOD thresholds

With **Automatic** on (the default), L1 is where a level-0 column reaches 1.5 px on screen
(`.vhdetail target`), and each later level doubles it, so every level hands over at the same
on-screen size (settled decision 35). Level 0 is held past the vanilla view distance, the blend band
and one section. At the owner's settings (60 degrees, 1,080 rendered rows, view distance 128, band
128) that gives 624 / 1,248 / 2,496 / 4,992 / 9,984 / 19,968 blocks; his own list stays in the file
for `.vhdetail manual`. It re-selects at most once a second, only when the render height, FOV, floor
or target changes, and logs what it applies. In use since 0.4.57 without complaint; not evaluated
at other FOVs, render scales or view distances.

## Settings window

`.vhconfig` has had two tabs above the window since 0.4.60. **General** holds the distances with the
Automatic switch, the draw distance, and the visual features with their presets; it applies on
Save. **Anti-Aliasing** holds a slider per dial, previewed live; Save keeps them and Cancel or
closing puts them back. Defaults resets the tab that is showing. Session 77 had removed an earlier
tabbed version whose strip clipped its labels; the owner asked for tabs again and confirmed in
0.4.60 that both labels read in full.

## Tree and glacier corrections, awaiting review

Version 0.4.54 gave a block's top, X walls and Z walls their own atlas rows, so standing logs show
bark on their sides; 0.4.55 makes wall lookup skip the already-selected top texture, so Redwood
quarter-trunks do too, while a lying-log control preserves a real wall-facing cut end. 0.4.55 also
limits liquid classification to Water and Lava: every `EnumBlockMaterial.Ice` block, including the
opaque packed glacier ice of mountain summits, had been drawn as water and written to the liquid
depth that drives underwater murk, painting a dark rim round glacier summits (G173). Stored palette
flags are re-derived from the live block registry on load, so existing caches are corrected without
re-exploration. Source-traced and harness-tested; the visual results await the owner.

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
| Live delivery | Durable changed L0 rows are offered to connected clients; integrated play notices sibling `ModifiedMs`; clients rebuild parents | Source/check evidence; dedicated multiplayer temporal run remains open |

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
protobuf peers ignore them. New clients accept an existing-row replacement only while its submitted
content revision is current, so a later local capture wins. Only L0 is transferred; parents rebuild
locally. The compatibility numbers remain `1 / 4 / 6`; `dev/WIRE_HISTORY.md` records the
append-compatible extensions. Sessions 84–86 changed nothing on the wire or on disk; Session 86 added
client configuration keys only.

## Diagnostics

Session-only unless stated; all write their findings to the client log.

- `.vhtaa` reports distant anti-aliasing (above); `.vhtaa show` tints what it touches.
- `.vhdiag on|off` (0.4.56) shows or hides the diagnostics side panel for the session; off by
  default. The panel lists every `.vhtaa` dial, saved and live.
- `.vhsky on|off` counts sky below the horizon after all opaque terrain is drawn and logs each flash
  with the captured mask; `.vhsky verify on|off` reads the mask texture back after every upload.
- `.vhlaterange on|off` switches the 0.4.53 fix; on by default.
- `.vhgap on|off` and `.vhgap release on|off` log, and optionally release, cells the mask gives to
  vanilla while vanilla is not drawing them. The scan still runs before the physics step.
- `.vhsnowline` undoes parts of the 0.4.40 spatial snow line. It served a disproved hypothesis and
  is a candidate for removal.
- `.vhwater capture` stores a frame's colour and depth stages with the projection; Session 86 measured
  the shimmer from one.

## Performance and artifact evidence

The final fast tier passed **23,129 assertions with zero failures**, and the 0.4.61 Release build
completed with zero warnings and zero errors. Distant anti-aliasing's measured cost is in the table
above; automatic thresholds cost nothing per frame unless their inputs change. The late range check
of 0.4.53 sweeps only on frames where the physics step changed the player's block; snow adds 1,024
uncompressed bits-array bytes per resident section before compression; no paired large-world capture
yet isolates winter campaign cost.

The verified `dist/vintagehorizons_0.4.61.zip` is 529,199 bytes with 38 entries and SHA-256
`C00F84D4024E4DC4EF9980C1C6C2AF4090BBB2272C2301BD4F3E8E8F1347B715`; its DLL's anti-aliasing
defaults were checked by decompiling, and the installed Mods-folder copy has the same hash. Older
test zips remain in that folder. Every incremented private test build receives an exact Git
checkpoint after verification and installation; this does not authorize a push, tag, publication or
public release. The assistant did not launch the game in Session 86.

Documentation validation passed **2,300 of 2,305 checks**. The five failures are the pre-existing
personal Windows paths in ignored `.testdata/water-extraction` and `.testdata/water-oit`
diagnostic logs. Current version, changelog coverage, source count, compatibility, links, session
routing, shader ASCII and active performance-plan routing pass.

## Established product state retained

- Version 0.4.39 remains the last accepted baseline until the owner accepts a later build. Its
  settled water seam and narrowly scoped loading guard remain accepted.
- The client remains useful on an unmodified server. Server capture, safe sweeping, optional
  generation, dormant snow refresh and live assist remain additive.
- Packed clustered terrain is still the required visible opaque path; dedicated OIT water remains
  required. Automatic plausible renderer fallback remains disabled.
- Distant anti-aliasing never changes a vanilla pixel and never keys anything to a fixed distance.
- The handoff fix changes when ownership's range half is decided, not what ownership means. Water
  development still may not redefine shared terrain ownership.

## Next work

Begin with [Session 86](dev/sessions/SESSION_86.md) and the top of [TODO](dev/TODO.md).

1. Optional: confirm 0.4.61 looks as tuned; `.vhtaa reset` should change nothing.
2. Human-test 0.4.55: Redwood side bark with a cut-end top; the captured snowy peak with murk on;
   the distant appearance of lake and glacier ice now that solid ice no longer uses water.
3. Owner decisions: accept a later build than 0.4.39 as the baseline; whether the anti-aliasing
   ranges should reach further than his tuning, which put three dials at their tops.
4. Winter temporal verification: one controlled unloaded-region accumulation and melt through the
   30-second signal, finite campaign, durable L0 replacement, live client offer and local mip
   convergence; campaign cost in a large explored world; `/vhrefresh` at its boundaries.
5. Session 84 follow-ups: keep `.vhsky`, retire `.vhsnowline` and `.vhgap release`, move or retire
   the pre-physics `.vhgap` scan, and the stale-sphere and eviction-gap cleanups.

[Session 86](dev/sessions/SESSION_86.md) holds the anti-aliasing work,
[Session 85](dev/sessions/SESSION_85.md) the tree/ice work and checkpoint rule,
[Session 84](dev/sessions/SESSION_84.md) the flash investigation and fix,
[Session 83](dev/sessions/SESSION_83.md) the 0.4.40–0.4.49 winter work, and
[Session 82](dev/sessions/SESSION_82.md) the accepted water baseline. Architecture owns the winter
and update invariants and settled decisions 33–35, `dev/WIRE_HISTORY.md` owns compatibility, and
G166–G179 in `dev/GOTCHAS.md` preserve the recent traps.

The active performance route remains
[the main-thread performance plan](dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md); GPU renderer work
is further specified by
[the GPU-driven terrain renderer plan](dev/plans/PLAN_GPU_DRIVEN_TERRAIN_RENDERER.md).
