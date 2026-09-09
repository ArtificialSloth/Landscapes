# Handoff - accepted water seam and terrain-safe streaming guard at 0.4.39

**Updated:** `2026-09-09`, Session 82
**Branch/source checkpoint:** `codex/water-depth-light`, accepted 0.4.39 source in Session 82 commit
**Installed and human-accepted:** `0.4.39` private test build
**Compatibility:** assist protocol / blob / schema `1 / 4 / 6`, unchanged

Read [Session 82](../sessions/SESSION_82.md) for the streaming correction and terrain-safe rollback,
[Session 81](../sessions/SESSION_81.md) for the material and seam investigation,
[Session 80](../sessions/SESSION_80.md) for the broad-band/required-renderer work, and
[the renderer plan](PLAN_LOD_WATER_RENDERER.md) for the remaining acceptance matrix.

## Current result

The broad rectangular band is closed; it was vanilla's false vertical liquid wall at a genuinely
missing neighbour and was suppressed in 0.4.23. The narrower settled vanilla/cache handoff is
closed too: since 0.4.36, the owner cannot tell where vanilla water ends and LOD begins in the
tested scene.

Version 0.4.39 is the current accepted checkpoint. It keeps 0.4.37's water-only reduction of the
split-second new-chunk light patch and completely removes 0.4.38's rejected changes to shared
terrain readiness. The owner reports the remaining flicker is acceptable and terrain/seabed
continuity is restored.

The accepted material defaults are:

| Setting | Value | Evidence |
|---|---:|---|
| Maximum pre-texture alpha | 1.3 | Owner A/B across steep and shallow viewing angles |
| Foam strength | 1.6 | Owner A/B against vanilla's white surface texture |
| Clarity | 40 blocks | Owner seabed comparison after alpha and foam were corrected |

The owner's current wave-strip value was zero during some comparisons. That is a separate saved
choice, not one of the accepted material defaults above.

## What closed the material mismatch

1. The repaired dedicated surface/OIT path compiled and ran, so later screenshots no longer came
   from a plausible fallback.
2. Cached water samples and animates the live standard-water atlas tile at world-block scale.
3. Climate pages and fallback tints retain RGBA. Vanilla multiplies the sampled water texture
   alpha by climate-water tint alpha; omitting that channel made cached water about 1.5 times too
   opaque in the measured comparison.
4. Cached opacity follows vanilla's view-angle formula. Camera height and render distance only
   changed the angle; they are not separate response inputs.
5. Diagnostic alpha and foam ranges extend to 2.0. The owner selected 1.3 and 1.6 rather than
   accepting the former artificial cap of 1.0.
6. Clarity 25 looked best before the material corrections. With the final composite in place,
   the owner retested and selected 40. Preserve that ordering when interpreting old screenshots.

## What closed the dotted seam

The final staged capture under
`.testdata/water-captures/20260909-004837-302-22ebc07a` identified 960 full-resolution pixels where
both vanilla and cached OIT water contributed, exactly tracing the pale dotted seam. Their mean
luminance was 0.2155, versus 0.1782 for adjacent vanilla-only water and 0.1761 for cached-only
water—about 21% brighter.

Vanilla's liquid-depth target is quarter-resolution and its OIT revealage is full-resolution.
The surface rule allowed a coarse "clear" depth answer to admit cached water even where
full-resolution revealage proved vanilla had drawn. In 0.4.36, the immutable revealage copy
**replaces** the depth answer for OIT surface coverage. Quarter-resolution liquid depth remains
the authority for underwater murk. Do not merge these answers back together.

The capture also confirmed the depth path was active: junction geometry added 102 pixels and
bounded repair added 23 in that frame. The accepted seam fix is therefore surface ownership, not
a claim that depth repair did nothing.

## Streaming result and rejected extension

Version 0.4.37 implemented the narrow correction proposed in Session 81. Before durable ownership
catches up, cached OIT water is suppressed only when the current frame's liquid-depth and
full-resolution revealage images both prove vanilla liquid at the pixel. Revealage alone could be
another transparent material; coarse liquid depth alone cannot prove full-resolution coverage.

The owner saw no holes and found the light patch much shorter—barely perceptible, though still
present. Version 0.4.38 then tried to bridge the remaining frame by carrying engine-announced
chunk presence through a spare channel of the shared readiness mask. The water looked fine, but
terrain and seabed developed severe holes. Focused checks had all passed; the owner's visual test
was the decisive regression evidence.

Version 0.4.39 removes the entire presence-channel experiment from the tracker, readiness mask,
publication path and shared ownership shader. Apart from version identity it restores 0.4.37
behavior, and the owner accepts that result.

## Hard boundary for future water work

Do not shorten, extend or reinterpret the two-frame terrain readiness rule, and do not add
water-specific facts to the shared readiness mask. Water development may not change opaque terrain
ownership, mask publication, or terrain/seabed continuity. A residual water flicker is preferable
to any missing ground.

If the residual flicker is ever revisited, the experiment must remain inside the dedicated water
stage and preserve the exact shared terrain state used by 0.4.39. A proposal that truly needs a
terrain-ownership change is separate work: stop, explain the terrain risk, obtain explicit owner
authorization, and build a terrain-first runtime regression case before implementation.

## Watch item: open-water moving blips

The owner briefly saw small marks while moving, including away from the seam. A later capture
placed one white mark in surface composition/glint rather than seabed/depth, but the owner then
could not reproduce the symptom and asked not to hold the seam work for it. Do not tune glint,
foam or repair from that one observation. If it returns, capture while moving and compare vanilla
revealage, combined revealage, raw/repaired liquid depth and final color at the marked pixel.

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
| `.vhwater alpha <0.05-2>` | Maximum pre-texture view-angle opacity; default 1.3 |
| `.vhwater foam <0-2>` | White surface texture strength; default 1.6 |
| `.vhwater clarity <blocks>` | Underwater absorption distance; default 40 |
| `.vhgpu off` | Explicit fail test; cached opaque terrain should disappear |

`.vhwater shader dedicated|legacy` remains retired. Record effective values in captures because
some appearance commands persist.

## Remaining engineering work

- Preserve the accepted 0.4.39 baseline. The residual new-chunk flicker is not a blocker at its
  current barely perceptible severity.
- Complete the Phase 2 matrix: repeated loading/unloading, movement on both sides, corners, LOD
  joins, shallow/deep shores, above/below water, stale cache, resize/fullscreen, shader reload and
  world change.
- Capture movement-only open-water blips if they become reproducible.
- Exercise transparency-disabled and deliberate required-resource failure cases.
- Keep physical removal of non-drawing per-section opaque `MeshRef` lifecycle scaffolding and old
  Phase 9 fail-open fixture updates separate from water development; they are terrain-renderer work.

## Source and artifact map

- `Render/LodTerrainRenderer.WaterOit.cs`: vanilla revealage snapshot, OIT uniforms and draw stage.
- `Render/LodTerrainRenderer.WaterDepth.cs`, `WaterRepair.cs`: liquid depth, junction and repair.
- `Render/LodTerrainRenderer.WaterCapture.cs`: staged captures and metadata.
- `Render/VanillaRenderReadiness.cs`, `VanillaReadinessMask.cs` and shared `lodownership.fsh`:
  terrain ownership baseline; frozen for water-only work after the rejected 0.4.38 experiment.
- `assets/vintagehorizons/shaderincludes/lodownershiptest.fsh`: surface/depth ownership decisions.
- `assets/vintagehorizons/shaderincludes/lodsurfacecolor.fsh`: texture, mapped alpha and Fresnel.
- `Render/LodClimateMapPages.cs`, `LodTintRegistry.cs`: RGBA climate/fallback tint transport.

Paths above are relative to `VintageHorizons/src`, except `assets`, relative to
`VintageHorizons`. The installed artifact is `dist/vintagehorizons_0.4.39.zip`, 32 entries, with
DLL, manifest and LICENSE, no PDB or game DLLs, SHA-256
`1791924D1FFAEF6575EA70013079E660DE212B81ACD8229FFBF8B9AE0BB62863`.

Read `CLAUDE.md` before work. Do not launch the game, push or publish without owner authorization.
Test artifacts advance the patch exactly once and are copy-installed without removing old zips.
Shader sources stay ASCII. Runtime GLSL compilation and the owner's visual test remain separate
gates from C# and static-source checks.
