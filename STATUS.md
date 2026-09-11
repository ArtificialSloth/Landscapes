# Vintage Horizons - status

> Tier 2: current state and verification evidence. Historical measurements and development
> narrative live in the sessions; durable design lives in `dev/ARCHITECTURE.md`.

**Status date:** `2026-09-11`
**Mod version:** `0.4.55` — current test build, verified and copy-installed; not yet human-tested
**Last accepted baseline:** `0.4.39` at base commit `8ace702`; 0.4.53 is human-tested for the handoff-flash fix but baseline promotion remains the owner's call
**Active branch:** `codex/water-depth-light`; exact 0.4.54 checkpoint `21d2e66`, 0.4.55 checkpoint created with the Session 85 documentation pass
**Target:** Vintage Story 1.22.5+, .NET 10
**Source files:** `104` C# files under `VintageHorizons/src`
**Assist protocol:** `1`
**Blob format:** `4`
**Database schema:** `6`

## Current result

**Version 0.4.55 fixes the remaining Redwood wall texture in source and removes solid ice from the
water/liquid-depth path that drew a dark rim around glacier summits. It is installed and awaiting
the owner's visual check.** The vanilla-to-LOD handoff flash remains fixed in the owner's 0.4.53
test, and spatial winter terrain retains its positive appearance review.

## Tree and glacier corrections

Version 0.4.54 added separate live-atlas rows for a block's top, X walls and Z walls, so standing
logs use bark on their sides rather than their top cut end everywhere. The owner confirmed the
ordinary-tree correction but found Redwood still wrong. Redwood quarter-trunk shapes use bark on
the outside while their unused block-file face names call `north` and `up` the same cut-end
texture. Version 0.4.55 makes wall lookup skip the already-selected top texture and continue to the
bark candidate. A lying-log control preserves a real wall-facing cut end.

The snowy-peak rim was isolated by a staged 0.4.54 capture. The vanilla liquid-depth target was
entirely clear (`min=max=1`), while the mod-added combined target contained 16,074 non-clear
quarter-resolution texels. Spatially overlaying that coverage on the frame painted the entire
glacier-shaped summit. `LodBlockPolicy` classified all `EnumBlockMaterial.Ice` as `FlagWater`,
including the `glacierice` and opaque `packedglacierice` blocks the installed game's worldgen uses
for mountain layers. Those solid blocks therefore received water waves and material, entered OIT,
and wrote the liquid depth that drives underwater murk.

0.4.55 limits the liquid classification to Water and Lava; ice follows ordinary solid terrain.
Stored palette flags are re-derived from the live block registry on load, so existing caches gain
the correction without re-exploration. Three policy cases pin lake, glacier and packed-glacier ice
as non-water. This mechanism is source-traced and harness-tested; absence of the rim and the opaque
distant-ice appearance still require human review.

## Handoff flash result retained

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
`1 / 4 / 6`; `dev/WIRE_HISTORY.md` records the append-compatible extensions. Sessions 84–85
changed nothing on the wire or on disk.

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

The final fast tier passed **22,984 assertions with zero failures**, and the 0.4.55 Release build
completed with zero warnings and zero errors. The late range check captures the range inputs once
per frame and sweeps only on frames where the physics step changed the player's block; its cost
was not separately measured. Snow adds 1,024 uncompressed bits-array bytes per resident section
before compression and corresponding immutable snapshot storage. The snow threshold rides in the
existing climate texture's alpha; since 0.4.40 the vertex shader fetches the field for every
vertex rather than only tinted ones (`.vhsnowline fetch off` restores 0.4.39's placement for
comparison). Snow adds no geometry, though a bare/snow boundary can reduce greedy horizontal
merging. Frost adds vertex arithmetic/noise only for frostable material slots. No paired
large-world runtime capture yet isolates campaign cost.

The verified `dist/vintagehorizons_0.4.55.zip` is 507,880 bytes with 34 entries and SHA-256
`F6D61EDEC20E46CDC9BF563F1F24FE52F63287284E4F6AD296462B247BC4F9B5`; its packaged DLL matches
the Release output and the installed Mods-folder copy has the same hash. Eleven older test zips
remain in that folder. Exact 0.4.54 was recovered and checkpointed as `21d2e66`; under the owner's
new standing instruction, 0.4.55 and every later incremented private test artifact receive an
exact Git checkpoint after verification and installation. This does not authorize a push, tag,
publication, or public release. The assistant did not launch the game in Session 85.

Documentation validation passed **2,282 of 2,287 checks**. The five failures are the
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

Begin with [Session 85](dev/sessions/SESSION_85.md) and the top of [TODO](dev/TODO.md).

1. Human-test 0.4.55: Redwood side bark with a cut-end top; the captured snowy peak with murk on;
   and the distant appearance of lake/glacier ice now that solid ice no longer uses water.
2. Owner decision: accept 0.4.53 or a later accepted build as the new baseline. Session 83's criterion was a clean
   vanilla-to-LOD transfer with no new terrain holes; the owner has confirmed the first.
3. Winter temporal verification, unchanged from Session 83: one controlled unloaded-region
   accumulation and melt through the 30-second signal, finite campaign, durable L0 replacement,
   live client offer and local mip convergence; campaign tick, storage and network cost in a
   large explored world; `/vhrefresh` at its boundaries.
4. Session 84 follow-ups: decide whether to keep `.vhsky` and retire `.vhsnowline` and
   `.vhgap release`; move or retire the pre-physics `.vhgap` scan; the stale-sphere and
   eviction-gap cleanups.

[Session 85](dev/sessions/SESSION_85.md) holds the tree/ice work and checkpoint rule,
[Session 84](dev/sessions/SESSION_84.md) the flash investigation and fix,
[Session 83](dev/sessions/SESSION_83.md) the 0.4.40–0.4.49 winter work, and
[Session 82](dev/sessions/SESSION_82.md) the accepted water baseline. Architecture owns the
winter/update invariants and settled decision 33, `dev/WIRE_HISTORY.md` owns compatibility, and
G166–G173 in `dev/GOTCHAS.md` preserve the new traps.

The active performance route remains
[the main-thread performance plan](dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md); GPU renderer work
is further specified by
[the GPU-driven terrain renderer plan](dev/plans/PLAN_GPU_DRIVEN_TERRAIN_RENDERER.md).
