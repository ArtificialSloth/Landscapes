# Handoff - water renderer at 0.4.27

**Updated:** `2026-09-08`, Session 80
**Branch/HEAD:** `codex/water-depth-light`, `bc9309c`; implementation uncommitted
**Installed, not yet played:** `0.4.27` private test build
**Compatibility:** assist protocol / blob / schema `1 / 4 / 6`, unchanged

Read [Session 80](../sessions/SESSION_80.md) for the full investigation and
[the renderer plan](PLAN_LOD_WATER_RENDERER.md) for the acceptance gates. Sessions
[78](../sessions/SESSION_78.md) and [79](../sessions/SESSION_79.md) preserve the earlier
water-depth, repair, shader-extraction and OIT work.

## Current result

The broad rectangular band is fixed. It was vanilla geometry, not cached water: the owner
reproduced it in pure vanilla and at a loaded-world edge where a vanilla water chunk had no
neighbour and no LOD chunk beside it. The engine substituted an empty neighbour and emitted a
vertical liquid wall. Version 0.4.23 suppresses only that false outward side, and the owner
confirmed the band disappeared.

A narrower visual seam between vanilla and cached water remains. The most recent screenshot
cannot be used to judge the intended dedicated renderer because the 0.4.26 log showed its water
programs did not compile. Automatic fallback drew plausible older water and hid the failure.
Version 0.4.27 repairs the known shader defects and makes required path failures visually obvious,
but it has not yet been run in game.

## What 0.4.27 changes

1. The shared water vertex wrapper initializes `vhSurfaceColor` and `vhBlockSlot` before the
   common vertex body consumes them. This fixes the `vhSurfaceColor undeclared` failures in the
   dedicated surface, depth and OIT vertex programs.
2. The OIT fragment calls `OIT(outColor, vhWaterGlow.r)` directly. It no longer macro-renames a
   fragment entry point, which fixes the AMD compiler's unresolved `vhShadeWater` overload.
3. Packed clustered terrain is the required visible opaque path. Missing packed data or any
   arena, upload, shader or draw failure suppresses cached opaque terrain and logs
   `REQUIRED PACKED TERRAIN PATH FAILED`. Expanded-indirect and per-section opaque rendering
   cannot substitute. The per-section opaque `RenderMesh` call is gone.
4. Dedicated water is required. A requested surface, depth, junction, repair or OIT failure
   suppresses cached water and thin cover and logs `REQUIRED WATER PATH FAILED`. The legacy water
   shader selector is retired; `.vhwater shader ...` now reports an error.
5. `.vhwater pipeline oit|opaque` remains a deliberate comparison of draw stages using the
   dedicated shader body. `.vhgpu off` remains a deliberate failure test that makes cached opaque
   terrain disappear. Neither command activates a retired renderer.

The old per-section opaque `MeshRef` allocation/publication/disposal lifecycle still exists as
transitional scaffolding. It has no visible opaque draw authority. Physical removal is a separate
open task after the packed path passes current runtime acceptance.

## Evidence to preserve

- `depth on` followed by `clarity 0`, after rebuilding: the old seam stayed.
- `surface off`: the broad band stayed while the cached surface and its thin junction vanished.
- `wave 0`, after rebuilding: neither pale blips nor the repeating seam pattern changed.
- Earlier `walls`, depth, Fresnel and land-blend toggles did not remove the broad band.
- 0.4.14-0.4.17 captures located sampled pale blips in seabed color at clear liquid-depth
  texels before surface composition. This did not explain the broad band.
- 0.4.18 junction depth removed seam blips with repair off; scattered blips elsewhere remained.
- Later, with repair on, the owner reported scattered blips gone while the broad band remained.
- Moving to the edge of LOD coverage left the broad band beside vanilla water. Pure vanilla then
  reproduced it. This was the decisive attribution test.
- 0.4.23 removed the broad band according to the owner. The narrower water handoff remained.
- 0.4.26's isolated GPU log validated advanced entry points, compute/SSBO, private depth copy and
  mip generation. Packed clustered terrain reported complete command coverage.
- The same 0.4.26 log reported the exact dedicated water shader failures above. Any visual claim
  made after that refactor must be repeated on a positively identified required path.

## First 0.4.27 test

1. Start normally with the highest installed version and let terrain settle.
2. Run `.vhwater log` and preserve the game log. Before judging the picture, require positive
   dedicated surface/depth/OIT readiness and the packed clustered terrain path. There must be no
   `REQUIRED PACKED TERRAIN PATH FAILED` or `REQUIRED WATER PATH FAILED` message.
3. If cached terrain or water is absent, stop visual comparison and use the named failure from the
   log. That absence is intentional and prevents another false success through fallback.
4. If all required paths are active, inspect the narrower vanilla/cache seam while stationary and
   while moving. Record whether texture, lighting, murk, thin-line behavior or blips differ.
5. Run `.vhwater capture` at the same settled view if the seam remains. Keep raw junction and
   repaired depth distinct from surface color.

Do not repeat the pure-vanilla broad-band investigation unless the broad rectangle returns. The
current target is the narrower handoff under a confirmed dedicated renderer.

## Current controls

| Command | Purpose |
|---|---|
| `.vhwater log` | Effective shader, pipeline, depth, junction, repair, texture and draw status |
| `.vhwater capture` | One frame's staged depth/color outputs and metadata |
| `.vhwater pipeline oit|opaque` | Explicit dedicated-shader stage comparison |
| `.vhwater junction on|off` | Geometry-backed missing junction depth |
| `.vhwater repair on|off` | Bounded image-based depth-hole correction |
| `.vhwater murk on|off` | Underwater treatment of cached ground |
| `.vhwater coverage on|off` | Cached liquid-depth extension |
| `.vhwater surface on|off` | Cached water visibility; also gates its depth extension |
| `.vhwater texture on|off` | Dedicated water surface texture contribution |
| `.vhgpu off` | Explicit fail test; cached opaque terrain should disappear |

`.vhwater shader dedicated|legacy` is retired and must not be used as a comparison. Existing
wave, clarity, depth and appearance settings remain separate; record effective values because
some commands persist them.

## Remaining engineering work

- Prove 0.4.27's shaders compile and the required paths draw on the owner's GPU.
- Re-establish the seam baseline and complete the Phase 2 continuity matrix: both sides of the
  boundary, movement, corners, LOD joins, shore/deep water, above/below water, loading and reload.
- Explain any remaining raw depth gaps before broadening repair. Color concealment is not closure.
- Test overlapping transparency, resize/fullscreen, shader reload, world changes and intentional
  resource failures under the fail-loud policy.
- After required packed rendering is accepted, remove the remaining per-section opaque `MeshRef`
  lifecycle and revise old Phase 9 failure fixtures that still assume fail-open rendering.
- Defer foam, shadows, extra waves and general opacity tuning until the residual seam gate passes.

## Source and artifact map

- `Render/LodTerrainRenderer.cs`: required packed selection and remaining MeshRef scaffolding.
- `Render/LodTerrainRenderer.WaterSurface.cs`: dedicated surface and required-path checks.
- `Render/LodTerrainRenderer.WaterOit.cs`: OIT selection/composition and stage lifecycle.
- `Render/LodTerrainRenderer.WaterDepth.cs`, `WaterRepair.cs`: liquid depth, junction and repair.
- `Render/LodTerrainRenderer.WaterCapture.cs`: staged captures and metadata.
- `Render/VanillaHorizonEffects.cs`: false missing-neighbour liquid-wall suppression.
- `assets/vintagehorizons/shaderincludes/lodwaterbody.vsh`: water wrapper initialization.
- `assets/vintagehorizons/shaderincludes/lodwaterfragment.fsh`: direct surface/OIT composition.

Paths above are relative to `VintageHorizons/src`, except `assets`, relative to
`VintageHorizons`. The installed artifact is `dist/vintagehorizons_0.4.27.zip`, 32 entries,
with DLL and LICENSE, no PDB, SHA-256
`FE9B34EEEF57837F4D4EEF7A6AE3762BF7D50E06C1C1A07A6F50320870070727`.
Release build passed with zero warnings/errors and fast checks passed 22,576 assertions.

Read `CLAUDE.md` before work. Do not launch the game, commit, push or publish without owner
authorization. Test artifacts advance the patch exactly once and are copy-installed without
removing old zips. Shader sources stay ASCII. Runtime GLSL compilation remains a separate gate
from C# and static-source checks.
