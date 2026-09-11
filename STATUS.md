# Vintage Horizons - status

> Tier 2: current state and verification evidence. Historical measurements and development
> narrative live in the sessions; durable design lives in `dev/ARCHITECTURE.md`.

**Status date:** `2026-09-11`
**Mod version:** `0.4.53` — current test build, installed and human-tested; the owner confirms it fixes the vanilla-to-LOD handoff flash
**Last accepted baseline:** `0.4.39` at base commit `8ace702`; 0.4.53 is the candidate to replace it
**Active branch:** `codex/water-depth-light`; the 0.4.40–0.4.53 work is committed on it
**Target:** Vintage Story 1.22.5+, .NET 10
**Source files:** `104` C# files under `VintageHorizons/src`
**Assist protocol:** `1`
**Blob format:** `4`
**Database schema:** `6`

## Current result

**The vanilla-to-LOD handoff flash is fixed in 0.4.53, and spatial winter terrain is built with a
positive appearance review.** The owner tested 0.4.53 on 2026-09-11 and reported the flash gone.

The flash was one frame of open sky on a chunk at the edge of vanilla's own draw range (65 to 67
blocks out at the owner's view distance of 64) in a cell the ownership mask had handed to
vanilla. The engine moves the local player inside the Before render stage
(`EntityBehaviorPlayerPhysics`, order 1.0, fixed 1/60 s steps) and only afterwards fills its
frustum culler from the player's block. Since 0.4.13 this mod's per-frame preparation, including
the exact range sweep that withdraws ownership when vanilla's range clause stops admitting a
chunk, has run earlier in that stage (order 0.994) so that it precedes vanilla's liquid-depth
pass. It therefore measured vanilla's range from where the player stood before the frame's
physics step. When a step crossed a block boundary that took a chunk past vanilla's range edge,
vanilla dropped the chunk in that frame and the mask released it in the next.

0.4.53 adds a loss-only late range check at Before/1.001, after the physics step and before
anything draws. It captures the range inputs again and, only when the player's block changed
since the preparation's sweep, runs the same sweep through the same publication handshake and
publishes the ground texels. Water's footprint waits for the next preparation, so every water
pass in a frame reads the same footprint. `.vhlaterange off` restores the old timing for
comparison. The change that introduced the lag was 0.4.13's move of the preparation into the
Before stage; 0.4.12 ran the sweep in the opaque stage, after the culler was filled.

The owner's build bisection placed the first bad build at 0.4.40. The code does not support that
window: 0.4.39 and 0.4.40 have the same preparation order and ownership code, and every archived
bisection session ran at the same ~235 fps. Why 0.4.39 was judged clean is not established.

## Handoff flash evidence

| Evidence | Result | Level |
|---|---|---|
| `.vhsky` witness, 0.4.52 | 83 single frames of 20,000–215,282 px of sky below the horizon, 1–8 per second while walking and none while standing. Each lay over loaded vanilla ground 65–67 blocks out that the captured mask gave to vanilla; 78 of the 83 had the mask unchanged that frame. The same in every comparison mode | Log-measured |
| `.vhsky verify`, 0.4.52 | 156 mask uploads read back while walking: 0 differed from the CPU copy, 0 GL errors | Log-measured |
| Engine frame order | Before stage: mod preparation 0.994, vanilla liquid depth 0.995, mod water depth 0.996, player physics 1.0; then shadows, `CalcFrustumEquations(player.Entity.Pos.AsBlockPos)`, opaque | Source-traced (decompiled 1.22.7) |
| Build history | Exact range rule first in 0.3.153; Before-stage preparation first in 0.4.13; 0.4.12 swept in the opaque stage; 0.4.39 and 0.4.40 identical in this respect | Source-traced (`dist/` DLL scan and decompiles) |
| 0.4.53 | Flash gone in the owner's test; no `.vhsky` count was taken on it | Human-tested |

Disproved or ruled out along the way (Sessions 83–84): stale mesh-result rejection (0.4.48), the
gain-only geometry guard (0.4.49), vanilla occlusion culling, temporal occlusion, the depth
pyramid and its split, every part of the 0.4.40 spatial snow line including the vertex climate
fetch, and same-frame release of every gap `.vhgap` could see. `.vhgap` and its release ran in
the same pre-physics slot as the sweep and shared its blind spot (G171).

Session 83 read the 0.4.49 log's "158 gains, 0 losses" as decisive against readiness loss. That
block is the one written 30 seconds after joining; its interval contained no chunk crossing and
therefore no handoff. G169 now records the correction.

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

The initial automatic design continuously recaptured one full-height 32×32 vanilla chunk column
per second. It was removed after review. Snow accumulation and melt are rare transitions, so the
current scheduler does no column work between relevant weather changes. During a finite campaign,
`RefreshColumnsPerSecond` defaults to 1, clamps to 0–16, and 0 disables automatic refresh. Work
stays inside the sweep's generated-column map and shares a 1 ms owning-thread issue budget. There
is no speculative “can accept snow” metadata.

## Storage, wire, and replacement semantics

Section blob format 4 now has two optional append-only 512-byte maps before compression: observed
snow provenance and covered state. Old readers ignore the tail. Current readers treat a missing
tail as unknown, reject partial tails, and downgrade provenance-only 0.4.43 rows to unknown rather
than falsely asserting bare terrain. Derived snow palette flags use bits 32 and 64 and are
reclassified from live blocks. No cache purge or schema-meaning change is required.

Protocol 1 added optional `AssistServerSettings` field 16 for the refresh rate and
`AssistKeyManifest` field 4 to distinguish newer durable contents from a newly discovered key.
Older protobuf peers ignore them and retain one-shot behavior. New clients accept an existing-row
replacement only while its submitted content revision is current, so a later local capture wins,
and retain the remote update route until that exact replacement persistence revision is
acknowledged. Only L0 is transferred; parents rebuild locally. The compatibility numbers remain
`1 / 4 / 6`; `dev/WIRE_HISTORY.md` records the append-compatible extensions. Session 84 changed
nothing on the wire or on disk.

## Diagnostics added in Session 84

All are session-only chat commands that write their findings to the client log.

- `.vhsky on|off` counts sky below the horizon after all opaque terrain is drawn, logs each flash
  with the captured mask and the ground its ray should have hit, and summarises once a second
  while anything happens. `.vhsky verify on|off` reads the mask texture back after every upload.
  Under 0.1 ms CPU per frame while on, off by default, and it draws nothing.
- `.vhlaterange on|off` switches the fix; on by default.
- `.vhgap on|off` and `.vhgap release on|off` log, and optionally release, cells the mask gives to
  vanilla while vanilla is not drawing them. The scan still runs before the physics step.
- `.vhsnowline` (`cells`, `shader`, `alpha`, `early`, `fetch`, `legacy`, `spatial`) undoes parts of
  the 0.4.40 spatial snow line. It served a disproved hypothesis and is a candidate for removal.

## Performance and artifact evidence

The final fast tier passed **22,941 assertions with zero failures**, and the Release build
completed with zero warnings and zero errors. The late range check captures the range inputs once
per frame and sweeps only on frames where the physics step changed the player's block; its cost
was not separately measured. Snow adds 1,024 uncompressed bits-array bytes per resident section
before compression and corresponding immutable snapshot storage. The snow threshold rides in the
existing climate texture's alpha; since 0.4.40 the vertex shader fetches the field for every
vertex rather than only tinted ones (`.vhsnowline fetch off` restores 0.4.39's placement for
comparison). Snow adds no geometry, though a bare/snow boundary can reduce greedy horizontal
merging. Frost adds vertex arithmetic/noise only for frostable material slots. No paired
large-world runtime capture yet isolates campaign cost.

The verified `dist/vintagehorizons_0.4.53.zip` is 505,483 bytes with SHA-256
`97B16452998681AF2BE3C04F3DE7475A1308DD80602EC31458082234C8096685`; the installed copy in the
owner's Mods directory matches. The 0.4.50–0.4.52 diagnostic builds and older test artifacts were
preserved; their hashes are in Session 84. Nothing was pushed, tagged, or publicly released.
Sandbox runs in Session 84 were owner-authorised; the assistant did not launch the owner's own
game.

Documentation validation passed **2,274 of 2,279 checks**. The five failures are the
pre-existing personal Windows paths in ignored `.testdata/water-extraction` and
`.testdata/water-oit` diagnostic logs. Current version, changelog coverage, source count,
compatibility, links, session routing and active performance-plan routing pass.

## Established product state retained

- Version 0.4.39 remains the last accepted baseline until the owner accepts 0.4.53. Its settled
  water seam and narrowly scoped loading guard remain accepted; the barely perceptible
  water-only loading flash is distinct from the terrain handoff flash fixed in 0.4.53.
- The client remains useful on an unmodified server. Server capture, safe sweeping, optional
  generation, dormant snow refresh and live assist remain additive.
- Packed clustered terrain is still the required visible opaque path; dedicated OIT water remains
  required. Automatic plausible renderer fallback remains disabled.
- Parent coverage, versioned workers, bounded publication/upload, asynchronous persistence, local
  capture precedence, block textures, surface slabs, spatial climate and remembered climate stay
  in force.
- The handoff fix changes when ownership's range half is decided, not what ownership means. It is
  loss-only, uses the existing publication handshake and leaves water's footprint semantics
  untouched. Water development still may not redefine shared terrain ownership.

## Next work

Begin with [Session 84](dev/sessions/SESSION_84.md) and the top of [TODO](dev/TODO.md).

1. Owner decision: accept 0.4.53 as the new baseline. Session 83's criterion was a clean
   vanilla-to-LOD transfer with no new terrain holes; the owner has confirmed the first.
2. Winter temporal verification, unchanged from Session 83: one controlled unloaded-region
   accumulation and melt through the 30-second signal, finite campaign, durable L0 replacement,
   live client offer and local mip convergence; campaign tick, storage and network cost in a
   large explored world; `/vhrefresh` at its boundaries.
3. Session 84 follow-ups: re-measure the small cache-owned sky patches with `.vhsky` on 0.4.53;
   decide whether to keep `.vhsky` and retire `.vhsnowline` and `.vhgap release`; move or retire
   the pre-physics `.vhgap` scan; the stale-sphere and eviction-gap cleanups.

[Session 84](dev/sessions/SESSION_84.md) holds the flash investigation and fix,
[Session 83](dev/sessions/SESSION_83.md) the 0.4.40–0.4.49 winter work, and
[Session 82](dev/sessions/SESSION_82.md) the accepted water baseline. Architecture owns the
winter/update invariants and settled decision 33, `dev/WIRE_HISTORY.md` owns compatibility, and
G166–G172 in `dev/GOTCHAS.md` preserve the new traps.

The active performance route remains
[the main-thread performance plan](dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md); GPU renderer work
is further specified by
[the GPU-driven terrain renderer plan](dev/plans/PLAN_GPU_DRIVEN_TERRAIN_RENDERER.md).
