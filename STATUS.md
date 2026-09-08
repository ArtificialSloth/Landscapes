# Vintage Horizons - status

> Tier 2: current state and verification evidence. Historical measurements and development
> narrative live in the sessions; durable design lives in `dev/ARCHITECTURE.md`.

**Status date:** `2026-09-08`
**Mod version:** `0.4.27`
**Active branch:** `codex/water-depth-light`, HEAD `bc9309c`; implementation is uncommitted.
**Target:** Vintage Story 1.22.5+, .NET 10
**Source files:** `95` C# files under `VintageHorizons/src`
**Assist protocol:** `1`
**Blob format:** `4`
**Database schema:** `6`

## Current result

**The broad rectangular water band is fixed.** The owner reproduced it in pure vanilla and at a
vanilla loaded-world edge with no LOD neighbour. Vanilla was tessellating a vertical liquid side
against its substitute empty chunk. Version 0.4.23 suppresses only that false outward face when
the adjacent client chunk is genuinely absent. The owner confirmed the band disappeared.

A narrower seam between vanilla and cached water remains. Its latest screenshot is not evidence
for the intended dedicated renderer: the 0.4.26 log showed that the refactored water shaders had
failed to compile and automatic fallback drew plausible older output. Version 0.4.27 repairs the
known shader contracts and retires automatic visible substitution. It is built, checked,
packaged and installed, but has not yet been run in game.

The 0.4.27 Release build completed with zero warnings/errors. Fast checks passed **22,576
assertions with zero failures**. The verified 32-entry package was copy-installed beside older
versions. No public release, commit or push is implied.

## Water evidence and remaining gate

| Component | Established evidence | Current limit |
|---|---|---|
| Broad band | Reproduced in pure vanilla; missing-neighbour liquid wall suppressed in 0.4.23; owner confirms gone | Separate from the narrower handoff |
| Liquid-depth murk | Cached seabed can receive vanilla-like underwater treatment; captures linked pale blips to clear depth texels before surface composition | Fresh 0.4.27 raw/repaired baseline required |
| Junction depth | 0.4.18 owner test removed seam blips with repair off | Predates later shader refactor; full continuity matrix incomplete |
| Bounded repair | Owner later reported scattered blips gone with repair enabled | Cause of any remaining raw gaps is not closed |
| Dedicated surface/OIT | 0.4.16/0.4.17 comparisons ran and looked unchanged | 0.4.26 refactor broke runtime GLSL; repaired 0.4.27 is untested |
| Texture/lighting work | Dedicated inputs and L0 texture-slot plumbing are source-complete | Later visual comparison ran through failed water shaders; repeat on active path |
| Residual seam | Clearly visible after broad-band fix | Cause and accepted appearance remain open |

The project remains at the dedicated-water plan's mandatory **Phase 2 boundary continuity
gate**. Foam, shadows, extra waves and general opacity tuning remain deferred until the residual
seam is evaluated under a positively identified dedicated path.

## Required renderer policy

Packed clustered terrain is the sole visible cached opaque product path. Missing packed data or
an arena, upload, shader or draw failure suppresses cached opaque terrain and logs
`REQUIRED PACKED TERRAIN PATH FAILED`. Expanded-indirect and per-section opaque rendering cannot
substitute. `.vhgpu off` is an explicit failure test and should make cached opaque terrain absent.

Dedicated water is likewise required. Failure of a requested surface, liquid-depth, junction,
repair or OIT stage suppresses cached water and thin cover and logs `REQUIRED WATER PATH FAILED`.
The legacy water shader selector is retired. `.vhwater pipeline opaque` remains only as a named
comparison of draw stages using the dedicated shader body.

The old per-section opaque `MeshRef` allocation/publication/disposal lifecycle remains as
transitional scaffolding, but there is no visible per-section opaque `RenderMesh` submission.
Physical removal is open after the packed path passes the immediate runtime gate.

## Runtime and artifact evidence

The 0.4.26 owner log isolated the previous startup OpenGL uncertainty:

- advanced entry points validated;
- compute and SSBO operations validated;
- private depth copy and mip generation validated;
- packed clustered terrain reported complete command coverage;
- dedicated water vertex programs failed on undeclared `vhSurfaceColor`;
- the OIT fragment failed to resolve `vhShadeWater`.

Version 0.4.27 initializes the shared vertex inputs in every water wrapper and calls OIT
composition directly from the common fragment body. Static checks pin those source contracts,
but no offline checker in this workflow compiles the engine-expanded GLSL. A successful game log
is still required.

Artifact: `dist/vintagehorizons_0.4.27.zip` and
`%APPDATA%/VintagestoryData/Mods/vintagehorizons_0.4.27.zip` share SHA-256
`FE9B34EEEF57837F4D4EEF7A6AE3762BF7D50E06C1C1A07A6F50320870070727`. The archive contains the
DLL and LICENSE and no PDB. The assistant did not launch Vintage Story.

## Established product state retained

- The client-only persistent multilevel LOD cache and optional server assist/generation remain
  intact. This session changes no protocol, blob or database meaning.
- Versioned workers, bounded publication/upload, asynchronous persistence and parent coverage
  remain the concurrency baseline.
- Packed indexed terrain, regional arenas, clustered HZB culling, ownership masks and current
  vanilla depth remain the accepted terrain direction. The former fail-open renderer policy is
  superseded for current development.
- Surface-only L0 slabs, block textures, biome/season tint and remembered climate remain in the
  product. Close Biome Colors covers L0-L2; far tint defaults off.
- The terrain render-threshold wall correction remains scoped to vanilla terrain shaders. The
  new liquid-side suppression is a separate geometry hook and preserves loaded shorelines.

## Next work

Run 0.4.27 and preserve the first `.vhwater log` plus game log. Confirm positive packed-terrain,
dedicated surface/depth and OIT readiness with no required-path failure. Missing cached output is
now the intended diagnostic for failure; do not interpret the seam until the active paths are
proved.

If all required paths run, capture the residual seam stationary and moving after terrain settles.
Compare raw junction and repaired depth, texture, lighting and murk on both sides. Then complete
the continuity matrix across corners, LOD joins, shore/deep water, above/below water, loading,
resize, shader reload and world change. Remove remaining per-section opaque MeshRef scaffolding
after the packed path passes this gate.

[TODO](dev/TODO.md) contains the full open work. [The current handoff](dev/plans/HANDOFF_WATER_SEAM.md)
is the entry point for water, [the water plan](dev/plans/PLAN_LOD_WATER_RENDERER.md) owns its gates,
and [Session 80](dev/sessions/SESSION_80.md) preserves the investigation. Architecture and gotchas
own durable decisions; `dev/WIRE_HISTORY.md` remains the compatibility authority.

The active performance route remains `dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md`; renderer work
is further specified by `dev/plans/PLAN_GPU_DRIVEN_TERRAIN_RENDERER.md`.

Documentation validation ran **2,127 checks with five failures**. All five are personal Windows
paths inside ignored `.testdata/water-extraction` and `.testdata/water-oit` diagnostic logs.
`.gitignore` line 24 ignores `.testdata/`; version, source count, compatibility, links and active
plan routing pass. Those private evidence logs were not modified to silence the checker.
