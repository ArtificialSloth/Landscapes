# Session 80 - vanilla water wall, shader regression, and fail-loud renderers

**Date:** `2026-09-06-08`
**Branch/commit:** `codex/water-depth-light`, base HEAD `bc9309c`; implementation uncommitted
**Mod version:** `0.4.19`-`0.4.27`; latest private artifact `0.4.27` not yet human-run
**Assist protocol / blob / schema:** `1 / 4 / 6`, unchanged

> Session records are Tier 3 history. This session continues Sessions 78-79 and records the
> separation of the broad vanilla band from the narrower cached-water handoff, the discovery
> that later water comparisons were running through a silent fallback, and the owner's decision
> to make required renderer failures visually obvious.

## Context and investigation

Session 79 ended with three visible symptoms that had to stay separate: seam blips, scattered
blips and a broad rectangular band. Junction depth removed seam blips with repair off, while
scattered blips remained and the band did not change. Subsequent private builds extended the
dedicated surface, depth, junction, texture and lighting work. The owner reported that repair
removed the scattered blips in the tested view, but the broad band stayed exactly the same.

The key change in evidence came from moving away from the apparent handoff. The owner placed a
vanilla water chunk at the loaded-world edge with no adjacent LOD chunk, and the same band was
still present. He then tested pure vanilla and reproduced it there. This disproved the working
assumption that cached-water surface, murk, ownership or repair created the broad rectangle.

After 0.4.23 removed that band, the narrower vanilla/LOD seam remained. Work then focused on
dedicated water appearance and terrain texture coverage. The owner found the water appearance
worse and reported no terrain texture in the blend band; `.vhtexture` also reported roughly fifty
refused textures in the live scene. That prompted a direct question: why was the legacy renderer
being drawn at all when the intended direction was to retire it?

The 0.4.26 log resolved the ambiguity. The GPU capability path was healthy, but the dedicated
water shaders were not:

- Advanced entry points, compute/SSBO validation and the private depth-copy/mip probe passed.
- Packed clustered terrain reported complete command coverage in the observed frame.
- Each dedicated water vertex program failed with `vhSurfaceColor` undeclared.
- The OIT fragment failed because `vhShadeWater` had no matching overload.
- The renderer silently chose plausible legacy/opaque water output after those failures.

That last behavior invalidated the later screenshot as evidence for the intended renderer. It
also established a development-process defect: a fallback that looks reasonable can make a
broken product path appear to work and can misattribute visual improvements or regressions.

## Work narrative

### 1. The broad band was a vanilla missing-neighbour liquid wall

Source tracing followed the pure-vanilla reproduction into `LiquidTesselator`. For visible-face
calculation the engine substitutes its shared empty chunk when a neighbouring chunk is absent.
At a water frontier this creates a vertical liquid side against invented air. Viewed nearly
edge-on, that wall appears as the pale rectangular, chunk-stepped band.

Version 0.4.23 added a narrow Harmony prefix at `DrawLiquidBlockFace`. It considers only the four
horizontal sides and only when the liquid block lies on the matching chunk boundary. It looks up
the adjacent client chunk and suppresses the side only when that chunk is genuinely absent. A
loaded empty chunk remains real shoreline information; top, bottom and interior faces are never
touched. Failure of the lookup leaves vanilla behavior intact. The owner reported: "The band is
gone!" The narrower vanilla/cache seam remained visible.

### 2. Dedicated water and texture work continued after the band fix

Versions 0.4.24-0.4.25 extended the dedicated water shading path with texture and lighting inputs
and made the expanded level-zero terrain representation carry texture slots. These builds were
intended to remove dependencies on old representations and improve the handoff appearance.

The owner's pictures and commands did not show the expected result. At that point the visual
evidence alone could not distinguish a bad new shader from a new shader that was not active. The
later compile log proved the latter for water. The reported refused texture count remains a scene
observation to re-check after the required paths are positively identified; it is not promoted to
a root cause here.

### 3. GPU capability failure was separated from pre-existing GL error state

Version 0.4.26 isolated and restored OpenGL error state around the runtime probes. On the owner's
GPU the required advanced entry points, compute and SSBO operations, and private depth copy/mip
generation all validated. Telemetry also showed packed clustered command coverage rather than an
unexplained terrain capability fallback. This closes the earlier 0.4.17 startup uncertainty for
the tested machine.

The same run supplied the first decisive runtime-expanded GLSL evidence after the refactor. The
shared vertex body consumed `vhSurfaceColor` and `vhBlockSlot`, but the water wrapper had not
initialized them as the terrain wrapper did. The OIT wrapper used macro-renamed function entry
points that the AMD compiler did not resolve into the intended overload. Static source checks had
not compiled the engine-expanded GLSL and therefore did not catch either failure.

### 4. Version 0.4.27 repairs the shader contracts

`lodwaterbody.vsh` now initializes the shared surface color from `vertexColorIn` and sets the
texture slot sentinel before entering the shared body. The OIT wrapper now declares its mode and
includes the common water fragment directly; the common fragment calls
`OIT(outColor, vhWaterGlow.r)` at the composition point. Ownership and band-debug early returns
compose through that same direct call. Checks pin every wrapper initialization, the three OIT call
sites, and the absence of macro entry-point renaming.

This is built and harness-tested. The game has not yet runtime-compiled 0.4.27's shaders, so it is
not described as a successful hardware fix until the owner log proves it.

### 5. Required renderer paths now fail visibly

At owner direction, version 0.4.27 removes automatic visible substitution during renderer
development.

For opaque cached terrain, `RequirePackedTerrainRenderer` is true and frame selection requests the
packed path directly. With clusters enabled, clustered packed drawing is required. Missing packed
data, allocation/upload failure, shader failure or draw failure suppresses the cached opaque draw
and logs `REQUIRED PACKED TERRAIN PATH FAILED`. Expanded-indirect leftovers are counted and
rejected rather than drawn. The per-section opaque submission method contains no `RenderMesh`
call. `.vhgpu off` is retained as an explicit failure test; it makes cached terrain disappear.

For water, `DedicatedWaterShader` is a getter-only true invariant. The `.vhwater shader` command
reports that the legacy selector is retired. Requested surface, liquid-depth, repair, junction or
OIT failure suppresses cached water and thin cover and logs `REQUIRED WATER PATH FAILED`.
`.vhwater pipeline opaque` remains a deliberate stage comparison using the dedicated shader; an
unavailable requested OIT stage does not select it automatically.

The old per-section opaque `MeshRef` allocation, publication and disposal lifecycle remains in
the source as transitional scaffolding. It cannot draw opaque terrain. Its physical removal is an
open follow-up after the required packed path passes current runtime acceptance.

### 6. Verification and artifact

The 0.4.27 Release build completed with zero warnings and zero errors. The fast check suite passed
22,576 assertions with zero failures. The new checks pin shared water initialization, direct OIT
composition, the absence of runtime legacy water selection, packed-only terrain selection,
required-path failure messages and the absence of a per-section opaque `RenderMesh` call.

`dist/vintagehorizons_0.4.27.zip` contains 32 entries, includes the DLL and LICENSE, excludes the
PDB, and contains mod version 0.4.27. It was copy-installed beside older versions at
`%APPDATA%/VintagestoryData/Mods/vintagehorizons_0.4.27.zip`. Both copies have SHA-256
`FE9B34EEEF57837F4D4EEF7A6AE3762BF7D50E06C1C1A07A6F50320870070727`.

No game process was launched by this work. No commit, push, tag or public release was made.

`dev/DocCheck.ps1` ran 2,127 checks with five failures. Every failure is a personal Windows path
inside one of five ignored diagnostic logs under `.testdata/water-extraction` or
`.testdata/water-oit`; `git check-ignore -v` identifies `.gitignore` line 24 (`.testdata/`) for
all five. Version, source count, compatibility, links and active-plan routing pass. The private
evidence logs were not changed to make the checker green.

---

## Delivered

- Versions 0.4.19-0.4.22 preserved the separation of depth/blip and broad-band experiments.
- Version 0.4.23 suppresses false vanilla liquid sides at genuinely missing chunk neighbours;
  the owner confirmed the broad band gone.
- Versions 0.4.24-0.4.25 extended dedicated water appearance and L0 texture-slot plumbing.
- Version 0.4.26 isolates OpenGL probe error state, validates the GPU path on the owner's machine,
  and exposes the dedicated water shader failures in the live log.
- Version 0.4.27 repairs the known shader interfaces and makes packed terrain and dedicated water
  required, fail-loud visible paths.
- Release build, 22,576 fast assertions and the verified/copy-installed 0.4.27 package completed.
- Documentation checks passed 2,122 of 2,127 checks; the five remaining failures are the known
  ignored `.testdata` personal-path diagnostics recorded above.
- Documentation was reconciled across the handoff, water/GPU plans, architecture, changelog,
  gotchas, TODO/DONE, status, session index and working agreement. Compatibility stayed 1/4/6.

## Decisions

- Treat the broad rectangle as closed independently from the remaining vanilla/cache seam. Its
  cause and fix are vanilla liquid-frontier geometry, not cached-water color, murk or OIT.
- A visual comparison counts only after logs positively identify the renderer and shader stages
  that produced it.
- Required packed terrain and dedicated water fail visibly during development. An older renderer
  may not activate automatically after a required-path failure.
- Keep only explicitly named diagnostic comparisons. The dedicated opaque water stage remains;
  the legacy water shader and visible per-section/expanded terrain fallbacks do not.
- Defer physical removal of non-drawing per-section MeshRef lifecycle code until the required
  packed path completes the immediate 0.4.27 runtime gate.

## Traps

- A band located at an apparent mod boundary may be vanilla geometry at the streaming boundary.
  Reproduce it without the mod and without adjacent LOD geometry before assigning ownership.
- Plausible fallback output destroys the evidentiary value of screenshots and A/B commands. Make
  a broken required path visibly absent and require a positive active-path log.
- A shared GLSL body has an input contract even without a language-level interface declaration.
  Initialize every consumed value in every wrapper and test the expanded source. Runtime compile
  remains a separate gate when no offline compiler matches the engine.
- Macro-renaming shader entry points can behave differently on the target compiler. Prefer a
  direct composition call at the common body's actual output point.

## Flagged and unverified

- **Judgement awaiting human review:** 0.4.27's water appearance and the remaining seam under a
  positively identified dedicated OIT path.
- **Missing evidence:** successful runtime compilation of every 0.4.27 water shader and absence of
  both required-path failure messages in a settled owner run.
- **Missing evidence:** the continuity matrix across motion, both sides, corners, LOD joins,
  shore/deep water, above/below water, loading, resize, shader reload and world changes.
- **Open cause:** any residual raw liquid-depth blips after 0.4.27; the last accepted observation
  says repair removed scattered blips, but later shader-fallback evidence requires a fresh baseline.
- **Open implementation:** physical removal of per-section opaque MeshRef allocation/publication/
  disposal scaffolding and revision of old Phase 9 fixtures that still assume fail-open rendering.
- **Evidence limit:** 0.4.26 validates the GPU capability path on the owner's current machine, not
  other drivers and not a controlled current-renderer performance comparison.
