# Handoff - accepted water seam at 0.4.36; transient loading overlap next

**Updated:** `2026-09-08`, Session 81
**Branch/source checkpoint:** `codex/water-depth-light`, `049d0c5`
**Installed and human-accepted:** `0.4.36` private test build
**Compatibility:** assist protocol / blob / schema `1 / 4 / 6`, unchanged

Read [Session 81](../sessions/SESSION_81.md) for the material and seam investigation,
[Session 80](../sessions/SESSION_80.md) for the broad-band/required-renderer work, and
[the renderer plan](PLAN_LOD_WATER_RENDERER.md) for the remaining acceptance matrix.

## Current result

The broad rectangular band is closed; it was vanilla's false vertical liquid wall at a genuinely
missing neighbour and was suppressed in 0.4.23. The narrower settled vanilla/cache handoff is now
closed in the owner's tested scene too. Version 0.4.36 is the accepted checkpoint: the owner
reports that he cannot tell where vanilla water ends and LOD begins.

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

## New issue: split-second light chunk during streaming

After accepting 0.4.36, the owner supplied a screenshot of a light chunk-sized water patch that
appears only for a split second as movement makes vanilla draw a newly loaded water chunk. This is
separate from the settled seam.

The current diagnosis is source-traced and **not yet human-proven**:

1. The cached water mesh remains resident; ownership masks its fragments rather than evicting it.
2. A chunk-dirty event queues readiness after the engine installs the chunk but before its render
   mesh is guaranteed ready.
3. Vanilla ownership is committed only after two `IsChunkRendered` observations in distinct
   render frames and successful mask publication. The delay is deliberate protection against a
   hole.
4. Vanilla OIT water can therefore draw for one frame while the mask still says cache-owned.
   Two translucent surfaces produce the temporary lighter square; the next ownership publication
   hides cached water and the color settles.

Do not shorten the readiness rule globally. Ground and long-lived ownership still need the
two-frame proof.

## Next implementation

Use the immutable same-frame images already available in the dedicated OIT water pass:

- full-resolution vanilla OIT revealage proves that vanilla contributed a transparent pixel;
- vanilla liquid depth proves that the contribution belongs to liquid rather than glass, smoke or
  another unrelated transparent object.

Before cached OIT water shades an otherwise cache-owned pixel, suppress it only when **both**
signals indicate vanilla liquid is present. Keep 0.4.36's owned-junction rule unchanged:
full-resolution revealage alone remains authoritative when deciding whether an owned surface pixel
is missing. These are two related but different questions.

Tests should pin at least four cases:

- pending/cache-owned cell + vanilla liquid depth + non-clear revealage: cached surface suppressed;
- pending/cache-owned cell + non-clear revealage but no liquid depth: cached surface retained;
- pending/cache-owned cell + liquid depth but clear revealage: cached surface retained;
- vanilla-owned junction behavior remains exactly the 0.4.36 replacement rule.

Once source is ready, advance the private test artifact exactly once to 0.4.37, run the Release
build and fast checks, verify the archive, copy-install beside older zips, and ask the owner to
cross newly loading water chunks while moving. The essential verdicts are whether the flash is
gone and whether any dark hole replaces it.

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

- Build and human-test the same-frame streaming-overlap correction without regressing the settled
  seam or creating holes around unrelated transparency.
- Complete the Phase 2 matrix: repeated loading/unloading, movement on both sides, corners, LOD
  joins, shallow/deep shores, above/below water, stale cache, resize/fullscreen, shader reload and
  world change.
- Capture movement-only open-water blips if they become reproducible.
- Exercise transparency-disabled and deliberate required-resource failure cases.
- Physically remove remaining non-drawing per-section opaque `MeshRef` lifecycle scaffolding and
  update old Phase 9 fail-open fixtures after water continuity closes.

## Source and artifact map

- `Render/LodTerrainRenderer.WaterOit.cs`: vanilla revealage snapshot, OIT uniforms and draw stage.
- `Render/LodTerrainRenderer.WaterDepth.cs`, `WaterRepair.cs`: liquid depth, junction and repair.
- `Render/LodTerrainRenderer.WaterCapture.cs`: staged captures and metadata.
- `Render/VanillaRenderReadiness.cs`: two-frame vanilla ownership stabilization/publication.
- `assets/vintagehorizons/shaderincludes/lodownershiptest.fsh`: surface/depth ownership decisions.
- `assets/vintagehorizons/shaderincludes/lodsurfacecolor.fsh`: texture, mapped alpha and Fresnel.
- `Render/LodClimateMapPages.cs`, `LodTintRegistry.cs`: RGBA climate/fallback tint transport.

Paths above are relative to `VintageHorizons/src`, except `assets`, relative to
`VintageHorizons`. The installed artifact is `dist/vintagehorizons_0.4.36.zip`, 32 entries, with
DLL, manifest and LICENSE, no PDB or game DLLs, SHA-256
`E971ADFACE300CE9EB897C055A5DAF518467EA32152DB293EC1505C388454904`.

Read `CLAUDE.md` before work. Do not launch the game, push or publish without owner authorization.
Test artifacts advance the patch exactly once and are copy-installed without removing old zips.
Shader sources stay ASCII. Runtime GLSL compilation and the owner's visual test remain separate
gates from C# and static-source checks.
