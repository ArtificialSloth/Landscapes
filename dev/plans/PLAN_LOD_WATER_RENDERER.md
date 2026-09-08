# Plan - a dedicated renderer for cached water

**Status:** The broad rectangular band is fixed. Dedicated water and OIT are implemented,
their 0.4.26 runtime compile defects are repaired, and automatic legacy substitution is retired.
Phase 2 remains open on the narrower vanilla/cache seam; 0.4.27 is not yet human-tested.
**Date:** `2026-09-08`; direction accepted `2026-09-05`, fail-loud policy accepted `2026-09-08`.
**Branch:** `codex/water-depth-light`, HEAD `bc9309c`, working tree/test version `0.4.27`.
Implementation remains uncommitted. See [Session 80](../sessions/SESSION_80.md).
**Scope:** Client water rendering. Reuse the cache, section selection, suitable existing
water meshes, sampler layout and diagnostics. No database, network, wire-format or general
terrain-renderer rewrite. Water-only mesh changes are conditional on demonstrated need
and measured cost; an extra vertex attribute is not an initial requirement.

This revision replaces the original 0.4.13 proposal and records the 0.4.27 development policy.
It makes boundary correctness an early
acceptance gate, removes the claim that OIT alone explains the seam, and separates vanilla's
near appearance from its finite-range fade. `STATUS.md` and `HANDOFF_WATER_SEAM.md` now describe
the 0.4.27 checkpoint; Sessions 78-79 preserve the earlier hypotheses and failed approaches.
Retrieved memory about a water radius also predates the live ownership-mask implementation.
The live source and the later evidence below take precedence.

## 1. Outcome and first milestone

Cached water should continue vanilla water across the ownership boundary without a thin
patterned line, a broad brightness band, missing murk, or artifacts as chunks load. It should
remain believable out to the cache edge without disappearing at vanilla's draw range.

**The first milestone is a minimal dedicated renderer with demonstrated surface and depth
continuity.** Separate the programs, integrate with the engine's transparency pipeline, and
use captures to resolve the remaining boundary defects. Do not add foam, shadows or extra
wave detail while missing coverage or unexplained boundary artifacts remain.

Visual acceptance belongs to the owner. A successful build, matching draw counts, or a
cleaner-looking screenshot does not prove continuity. Color changes that merely conceal a
pattern are not acceptance. Track the thin line and broad band separately throughout.

## 2. Current evidence and reusable work

### 2.1 What the tests established

| Observation | Evidence and limit |
|---|---|
| Broad band and thin seam are distinct | `surface off` removed the thin junction but left the band. The owner then reproduced the band at a vanilla loaded-world edge and in pure vanilla. Version 0.4.23 suppresses the false missing-neighbour liquid wall, and the owner confirms the broad band is gone. |
| Clarity was not the seam fix | Owner tried `depth on` then `clarity 0` after rebuilding; the seam stayed. Preserve this negative result. |
| Murk originally depended on the surface being viewed through | Owner observed useful, close-to-vanilla murk on cached seabed through vanilla water, but none through cached water. Extending liquid depth addressed that missing coverage. |
| Early blanket darkening was a regression | A sampler collision affected normally bright terrain. Keep the explicit sampler layout and check that dry terrain remains unaffected. |
| Captured scattered blips preceded cached-water color blending | The 0.4.14 capture showed bright ground pixels before cached surface drawing, associated with clear holes in combined liquid depth. This concerns sampled blips, not every remaining seam pixel. |
| Repair improved the image but did not finish the seam | Owner report on 0.4.15: blips gone except at the seam. In 0.4.18, scattered blips still remain when repair is off. |
| Dedicated extraction and OIT preserve the tested appearance | Owner found 0.4.16 legacy/dedicated and 0.4.17 opaque/OIT views identical; logs confirm OIT actually ran. Full transparency/lifecycle validation remains open. |
| Junction depth removes seam blips, not the broad band | Owner's 0.4.18 test with repair off: seam blips go away, scattered blips remain, broad band unchanged. Full thin-line/surface continuity is not established. |
| Later visual evidence was contaminated by fallback | In 0.4.26 all dedicated water vertex shaders failed on `vhSurfaceColor`, and OIT failed on `vhShadeWater`. Plausible legacy/opaque output was selected, so the later texture/seam picture was not evidence for the intended renderer. |
| Required render paths must fail visibly | Owner direction after the 0.4.26 discovery. Version 0.4.27 removes automatic legacy/opaque substitution and repairs the known shader contracts. Runtime compile and appearance remain unverified. |
| Wave width did not explain the moving pattern | Owner reports `wave 0`, after rebuilding, changed neither the blips nor the repeating seam pattern. Earlier negative wave testing for the broad band is separate. |
| Earlier toggles did not remove the broad band | `walls off`, `depth off`, `fresnel off`, and `.vhblend off` tested negative. Land blending is also source-gated away from water. |

The 0.4.16/0.4.17 captures attribute sampled seam blips to missing liquid depth before
surface composition. The broad band had a separate vanilla-geometry cause; do not transfer
the blip finding to it. Fresh evidence now needs to come from the fail-loud 0.4.27 path. Repeat old negative toggles only when a
changed implementation makes a new test informative. A screenshot's loading state and
intended subject matter must be established before treating other visible features as defects.

### 2.2 Reusable implementation

- `LodTerrainRenderer.WaterDepth.cs`: prepares selection at Before/0.994, before vanilla
  liquid depth at 0.995, then appends cached water at 0.996 without clearing vanilla depth.
  The target is a separate liquid-depth image, never primary terrain depth.
- `LodTerrainSamplerLayout.cs`: fixed engine/mod texture units across variants. Register
  every new program and rebind mod textures after program activation.
- `LodTerrainRenderer.WaterCapture.cs`: `.vhwater capture` records intermediate depth,
  color and frame metadata. Preserve raw float depth and stage identity.
- `LodTerrainRenderer.WaterRepair.cs`: immutable depth copy and bounded, non-iterative
  filling of small enclosed clear holes; `.vhwater repair on|off` is a live diagnostic.
- `LodTerrainRenderer.WaterSurface.cs` and `WaterOit.cs`: required dedicated shaders,
  explicit opaque/OIT stage comparison, deferred frame identity, state restoration,
  fail-loud suppression and timing.
- `WaterDepth.cs` plus shared ownership code: 0.4.18 junction depth uses an immutable
  vanilla-only snapshot. Clear pixels in owned columns touching the cache may receive real
  cached water depth. Existing vanilla depth, interior ownership and surface color remain intact.
- Existing section selection, water meshes, transforms, wave inputs and timing.

Captures include vanilla liquid depth, unrepaired/repaired combined depth, opaque scene
depth, color before/after cached water, and final composition. Moving stages requires
updating capture points and labels. 0.4.18 additionally captures a mask-only reference before
junction coverage, then unrepaired and repaired results. Do not present images from different frames as a
same-frame diagnosis.

### 2.3 Boundaries of this work

Keep vanilla meshes, shaders and draw order intact. Existing shared liquid-depth writes can
affect vanilla consumers; existing far-plane and view-distance hooks affect vanilla too.
The plan therefore cannot promise that all vanilla pixels remain untouched.

Keep land blending and the packed opaque-quad format outside this work. No blanket water
overlap into vanilla-owned chunks and no new camera-radius seam or quality fade. Correct
ownership using evidence of actual water coverage. Retain only the named dedicated
opaque/OIT stage comparison. A missing required water stage suppresses cached water; it does
not select the legacy shader. Game launches, including isolated benchmarks, require explicit
owner permission under `CLAUDE.md`.

## 3. Engine contracts and source map

Repository sources are under `VintageHorizons/src/` and
`VintageHorizons/assets/vintagehorizons/`. Engine references below are to the locally
inspected client and installed shaders; recheck these contracts on an engine upgrade.

| Contract | Source |
|---|---|
| Mesh format, depth and merging | `Lod/LodMesher.cs`: `HFace.Submerged`, horizontal/vertical merging, `SeamCellFor` |
| Current drawing and frame lifetime | `Render/LodTerrainRenderer.cs`: `DrawLegacyWater`, `SetupSectionTransform`, frame preparation |
| Water ownership | `Render/VanillaReadinessMask.cs` water footprint; `lodterrainbody.fsh` water mask branch |
| Land distance field | `Render/VanillaSeamDistanceField.cs`: ground cells of 8 blocks, chamfer weights 3 and 4 |
| Projection and vanilla liquid range | `Render/LodFarDistance.cs`, `Render/VanillaHorizonPolicy.cs`, `ApplyZFar` |
| OIT ordering | Engine `ClientMain`, `SystemRenderOITLayers`, `SystemRenderTerrain`, `ChunkRenderer` |
| Attachments and attribute layout | Engine `ClientPlatformWindows` |
| Samplers and includes | Engine `ShaderProgramBase`, `ShaderRegistry`; repository `TerrainSamplerChecks` |
| Appearance and composition | Engine `chunkliquid.vsh/fsh`, `oit.fsh`, `transparentcompose.fsh`, `underwatereffects.fsh`, `fogandlight.fsh` |

### 3.1 Transparency and ordering

The engine prepares liquid depth before opaque ground. After opaque drawing it conditionally
loads/clears Transparent, runs OIT, and composes it onto Primary. Within OIT, setup is order
0.0, vanilla liquid draws at 0.37, and resolve preparation is 1.0. Proposed cached water is
OIT/0.38, using `oit.fsh` outputs without competing terrain output declarations.

Transparent shares Primary's depth attachment. Cached water depth-tests against it but must
never enable primary depth writes. The entire transparent pass can be disabled; its callback
then never executes.

For one ordinary transparent layer with valid alpha and nondegenerate accumulation bins,
normalized OIT composition reduces to `mix(opaque, color, alpha)`. Moving an isolated
surface into OIT need not visibly change it. OIT supplies integration with vanilla's
overlapping transparent layers and composition; it is not a proven explanation of failed
seam tuning and cannot fill geometry or liquid-depth holes.

### 3.2 Opacity and finite-range fade

Vanilla's vertex formula includes:

```glsl
fresnel = min(fresnel, clamp(
    20 * (1.05 - length(worldPos.xz) / viewDistance) - 5
    + max(0, worldPos.y * 0.02), -1, 1.5));
rgba.a = clamp(0.8 * fresnel, 0, 2);
```

The fragment shader discards very low incoming alpha before later fog-alpha addition.
For water below the camera, the height bonus is zero and this distance term reaches zero
at `0.8 * viewDistance`. At configured 384 the current policy supplies 578 to
`chunkliquid`, giving zero at about 462.4 blocks. Copying that fade across cached water
extending thousands of blocks would remove it. Fog cannot recover discarded pixels.

Separate angle-dependent opacity and fog from finite-range disappearance. Inspect effective
vanilla inputs at the actual ownership boundary. Near matching and distant continuation are
both requirements; do not blindly transcribe the full formula. The intermediate alpha
ceiling of 1.2 is not a mandate for a 1.2 user ceiling or final blend alpha.

### 3.3 Depth appearance and GPU safety

Engine murk compares liquid depth with fragment depth. Its inspected linear-depth saturation
window is approximately `zFar / 350`; the scene-depth foam window is approximately
`zFar / 1500`. Projection 3000 versus 4608 gives roughly 8.6 versus 13.2 blocks for murk,
and 2.0 versus 3.07 for foam. These are source-derived scales, not measured visual results.

The predicate `getUnderwaterMurkiness() - fogAmount > 0.05` is not simply an underwater-camera
test: the inspected helper returns zero when `cameraUnderwater > 0.7`. Trace and test its
actual conditions above/below the surface before adding that discard.

**Sampling safety must be explicit.** `DepthMask(false)` alone does not make sampling an
image attached to the active draw framebuffer generally safe under OpenGL 4.3 feedback rules.
Vanilla doing so is not a portability guarantee. When a pass needs to sample its attached
depth, use a separate immutable depth copy as the baseline. A supported alternative requires
verified capabilities and synchronization semantics. Include copy time and memory in cost.
See [OpenGL 4.3 core specification, section 9.3](https://registry.khronos.org/OpenGL/specs/gl/glspec43.core.pdf).

Depth-only programs must have no executable sampling path to the depth image they write.
Check expanded shaders, including engine-added includes. Shared vertex geometry does not
require sharing surface-only fragment samplers.

## 4. Design decisions

### 4.1 Dedicated programs, shared geometry and ownership

Create `lodwater.vsh/fsh`. Extract small shared includes for section-local addressing,
water ownership and displacement. Surface and liquid-depth passes share position, base mask logic, frame inputs and mesh
revision. The explicit 0.4.18 depth-only junction exception admits real geometry at clear
vanilla-depth pixels; it does not relax surface ownership. Do not duplicate the giant terrain shader or add OIT
outputs to its other variants.

Initially retain the 16-byte water vertex: float xyz plus RGBA carrying color, band and tint
slot. Preserve appearance while extracting the program; move composition stage in a separate
comparison. No new discard, opacity default, foam, shadow or vertex format in that stage-only
comparison.

Shared code prevents arithmetic drift but does not prove raster coverage. Quarter-resolution
depth, vanilla's independent geometry, ownership edges and coarse LOD joins require captures
and visual validation. Fix demonstrated cracks and ownership errors at their source. Do not
make progressively wider hole filling the main boundary strategy.

### 4.2 Frame lifetime, state and failure

Replace the opaque callback's cleared `preparedFrameReady` lifetime with a validated frame
identity shared by preparation, depth, opaque and OIT consumers. Reject stale resources or
selection after reload, resize or world change. Traverse and upload once per frame.

Choose the path before opaque drawing from actual transparent-pass state and resource
readiness. Session-only `.vhwater pipeline oit|opaque` exposes a named comparison between
two stages using the same dedicated water shader body. When OIT is requested, missing or
disabled OIT suppresses cached water and thin cover and logs a required-path failure.
The legacy shader selector is retired and `.vhwater shader ...` reports that fact.

Preserve GL state, fixed sampler bindings, per-attachment OIT blend functions and the primary
depth-write prohibition. Log skipped-stage reasons and required-path failures. A failure
discovered after opaque drawing suppresses the remaining cached-water work; it does not redraw
through an older path on that frame or the next. Do not claim recovery from failed GL state
restoration without verifying it.

Measure all mod callbacks, depth preparation, copies and repair. End accounting after the
last participating callback even when OIT is disabled. Sum mod CPU work rather than charging
intervening vanilla stages to the mod.

### 4.3 Depth data and mesh changes are conditional

The mesher's `Submerged` key carries useful depth on submerged ground. Water-surface faces
currently explicitly carry zero. A large greedy water quad can span many seabed depths;
one constant thickness cannot faithfully represent them.

Defer the original proposal's four-byte depth/flags attribute until a consumer and accurate
data source are identified. Prefer existing screen-space depth for engine murk/foam where
valid. If per-vertex or sampled physical depth is necessary, specify how it handles merged
surfaces, missing samples and LOD boundaries. Measure extra merge splits and gate them with
the feature. G152 records a previous +75% water-buffer cost hidden by an invalid A/B.

Four added bytes are +25% of an unchanged 16-byte vertex, not automatically +0.6% of live
geometry memory. Include other geometry formats, indices, LOD distribution and vertex-count
changes. Do not consume packed opaque texture-slot bits for water-only data.

### 4.4 Appearance parity after boundary correctness

- **Murk and seabed:** preserve sunlight absorption, which differs from view-dependent murk.
  Evaluate removal of invented `WaterClarity` only after depth continuity, with a reversible
  comparison. The earlier clarity-zero seam test was negative. Do not default to a new
  angle-corrected invented extinction term.
- **Opacity/fog:** match effective near behavior and define distant continuation without the
  unload fade. Keep accepted ceiling 0.7 as the baseline. The rejected 0.4.8 result is real;
  its cause was not proved to be the blending operator.
- **Cache edge:** preserve existing behavior during extraction. Switching sky-color mixing to
  alpha fade is a later independent comparison: it can reveal cached seabed rather than sky.
- **Texture:** assess projected detail at the tested resolution, FOV and distance. A 32-texel
  tile is not necessarily sub-pixel at 192-384 blocks. If missing color/alpha detail causes a
  near mismatch, add stable block-tiled atlas sampling and verify filtering and seams. Mean
  vertex color does not automatically reproduce texture alpha.
- **Discard:** add any engine-derived murk discard separately after tracing its conditions.
  Test above/below water and overlapping water elevations.

### 4.5 Later shadows, foam and wave detail

Initialize shadow coordinates before reading shadow varyings. Respect cascade coverage and
shadow quality; provide a reversible switch. A missing shadow term is a possible appearance
mismatch, not a diagnosis of the moving seam.

Compare the engine 5x5 foam kernel, a cheaper kernel and off using a safe scene-depth source;
include copy cost. A distant shoreline can still be shallow. Distance from the camera does
not imply zero foam. Replace constant shallowness only when depth-based behavior is accepted.

Extra wave detail is optional after the boundary gate. Geometry corrections necessary for
continuity belong in that early gate. The negative `wave 0` test prevents assuming strip
width is the current cause.

### 4.6 Quality tiers only when justified

The minimal renderer introduces no quality boundary. Later simplification needs measured
cost and acceptable visible error, including distant shallow shores. A tier that creates
another visible seam fails even if it follows chunk edges.

If discrete tiers are necessary, classify whole 32-block columns from the water ownership
footprint using integer chunk coordinates and a defined Chebyshev distance. The existing land
field uses 8-block ground cells and chamfer weights 3/4 approximating Euclidean distance.
Rounding its values neither snaps spatial contours to chunk planes nor yields water ownership.
Derive the correct classification, test negative coordinates and mask wrapping, and debug-paint
it. Choose reach from quality/cost evidence, not the unrelated width of the mesh strip.

## 5. Implementation sequence and gates

Each independently testable change gets a reversible comparison and a small owner observation.
A phase may take several artifacts; do not combine unrelated defaults to meet a one-build
quota. Current comparison commands are listed in `HANDOFF_WATER_SEAM.md`; later-feature
commands remain proposals until implemented.

### Phase 0 - residual-seam baseline

**Progress:** sampled blips were localized in 0.4.14, 0.4.16 and 0.4.17 captures. The broad band
was reproduced in pure vanilla, attributed to missing-neighbour liquid sides, and removed by
0.4.23. The narrower seam and any raw-depth blips on the required 0.4.27 path remain the baseline.

Use current captures on the remaining seam. Record build, effective settings, projection,
camera, mesh readiness and repair state. Compare repair on/off where informative, retaining
unrepaired depth. Find the earliest stage containing each symptom. Add counters/skip reasons
only as needed; shader-bound and unavailable-framebuffer skips must not masquerade as success.

**Gate:** separate thin-line, broad-band and scattered-blip outcomes, with capture localization
or an explicit evidence gap. No unsupported root-cause claim. This enables implementation;
it does not accept the seam.
**Owner observation:** identify the residual pattern and intended loading state.

### Phase 1 - minimal dedicated renderer

**Progress:** steps 1 and 2 were implemented in 0.4.16/0.4.17 and effective OIT draws were then
verified. Later refactoring broke the dedicated programs; 0.4.26 exposed the compile failures.
0.4.27 repairs them and removes automatic substitution, but runtime compilation, overlapping
transparency and full lifecycle hardening remain open.

1. Extract dedicated surface shading and shared depth/ownership geometry, preserving the
   existing stage and appearance. Compare with the legacy path.
2. Move unchanged appearance to OIT/0.38. Add frame identity, safe state handling, sampler
   registration, pipeline selection, stage-correct captures and complete timing.

**Gate:** extraction introduces no unexplained visual change. OIT comparison covers an isolated
surface and overlapping transparency. Verify missing/disabled OIT, shader/resource failure,
resize and world reload. No primary depth writes, double draw, stale selection or undefined
feedback path. Report actual costs; the number of attachments is not a timing result.
**Owner observation:** boundary-pattern changes and visibility of objects/water behind cached
water. An overall color change or successful build does not prove a seam fix.

### Phase 2 - boundary continuity: mandatory first-milestone gate

**Progress:** 0.4.18 junction depth removed seam blips in the owner test with repair off, and
repair later removed the scattered blips in the tested view. The broad band is independently
fixed in 0.4.23. A narrower visual seam remains. Later observations were made while the intended
water shaders were unavailable, so the gate must be re-evaluated on 0.4.27 and is not passed.

Correct demonstrated ownership, geometry, depth-rasterization or projection inconsistencies
using the new renderer and captures. Change the smallest responsible component. If an existing
appearance term causes the boundary artifact, isolate its correction here and explain why;
do not proceed to decorative features while the boundary remains unresolved.

| Scenario | Required result |
|---|---|
| Stationary camera over continuous water | No moving thin pattern or blips; no unexplained junction depth holes |
| Moving across and looking along either side of the boundary | Continuous surface/murk; no overlap stripe or exposed seabed |
| Same cached seabed viewed through vanilla and cached water | Consistent underwater treatment; no broad band; dry terrain retains brightness |
| LOD/section joins and chunk corners | No cracks, inconsistent displacement or depth gaps hidden by repeated repair |
| Loading/unloading and readiness transitions | No persistent gap, duplicate surface or stale ownership after relevant meshes are ready |
| Above/below water, shallow shores and deep water | Correct occlusion/murk; genuine shoreline openings are not filled |

Keep repair as a diagnostic and bounded fallback. Show raw and repaired depth and explain
remaining dependence. Prefer passing without repair. If a bounded rasterization repair is
necessary, document cause, extent, shoreline preservation and measured cost, and obtain
owner acceptance of that specific tradeoff. A patterned residual seam blocks this milestone.

**Gate:** owner accepts the matrix and captures support continuity rather than color concealment.
Both thin line and broad band need explicit outcomes. If either survives, continue diagnosis
here or revise the architecture; do not declare success and move to foam.

### Phase 3 - near appearance and distant continuation

First separate the projection confounder if needed. At configured 384, `.vhfar 1024` gives
effective distance 1920/projection 3000; `.vhfar 4096` can give projection 4608 when resident
meshes reach that far. Verify logged values. This command persists settings: record and
restore the original. Prefer a temporary diagnostic override if available; agent-launched
game runs still need explicit approval.

Then compare independently: clarity removal while retaining sunlight absorption; near opacity
and fog; distant continuation without unload fade; texture contribution where necessary;
and any engine-derived discard. Preserve previous settings until acceptance.

**Gate:** fixtures cover the actual junction and distances through/beyond vanilla fade
thresholds. Water survives to the accepted cache edge without a new circular transition or
tier line. Owner accepts brightness, transparency, horizon and underwater views. Recheck
Phase 2 for regressions. Do not trade the accepted seam for the flat distant sheet rejected
in 0.4.8.

### Phase 4 - optional visual improvements

Add shadows, depth-based foam and extra wave detail separately, with live switches and cost
comparisons. Texture needed for near parity belongs in Phase 3 rather than indefinite deferral.
Recheck the seam after each addition.

**Gate:** owner accepts the specific feature: a shadow crossing the junction, shoreline foam
continuing across it, or improved motion without a new edge. Record GPU and geometry costs.

### Phase 5 - optimize and freeze

Decide from measurements whether tiers are needed. Validate cheaper paths on distant shallow
shores, ocean horizons and the seam matrix, with debug classification available. No assumed
foamless/opaque far asymptote substitutes for comparison.

Promote accepted defaults and selectively retire obsolete knobs. Preserve useful capture and
explicit failure diagnostics. Record final evidence when the owner requests session finalization.
Executable artifacts follow the standing patch/package/copy-install rule in `CLAUDE.md`;
this plan amendment itself changes no executable artifact.

## 6. Verification and performance

### 6.1 Historical measurements and limits

| Quantity | Historical result | Limit |
|---|---|---|
| Water vertices | 106,804 / 4,257,508 (2.5%) | 120 real level-0 sections, 2026-09-04; not live memory or all LODs |
| Columns holding water | 36.5% | Same cache sample |
| Submerged ground at depth clamp 24 | 36.5% | Does not establish distance from camera |
| Submerged ground within 3 blocks | About 20% | Does not justify disabling far-shore foam |
| Per-block water everywhere | +16.31% whole-mesh geometry | Same sample |
| Chunk-plane strip +/-1 block | +0.99% whole-mesh geometry | Same sample |
| Chunk-plane strip +/-2 blocks | +2.86% whole-mesh geometry | Same sample |
| Opaque / water GPU pass p95 | About 1.25 / 0.05 ms | One 0.3.41 isolated run, RX 9070 XT; not current performance |

Multiple OIT attachments do not establish a sixfold slowdown or 0.3 ms cost. Measure formats,
overdraw, pixel coverage and complete pass cost. A 5x5 foam kernel needs 25 taps per evaluated
pixel; copying, filtering, resolution and tiering change both quality and cost. Measure depth
preparation and repair rather than assuming quarter-resolution work is negligible.

### 6.2 Required verification

- Geometry: run `waterdepthfield --samples 120` with the same cache snapshot for both halves.
  Include water vertices, indices, bytes and relevant LOD levels. The owner plays on the cache;
  samples from different days are not controlled pairs. Gate geometry work with its feature.
- Correctness: extend meaningful checks for chunk edges/corners, negative coordinates, LOD
  joins, missing samples, frame identity and fail-loud suppression. Retain sampler/capture/repair checks.
  Static registration or string checks cannot prove raster correctness.
- Shaders: use engine include expansion and sampler collection for every new program. Check
  feedback paths and actual attribute layout if changed. No offline GLSL validator is available
  in this workflow; runtime compilation and output validation remain necessary.
- Diagnostics: on-demand logs must report effective settings, selection, projection,
  water/depth counts and skip reasons. Proposed `.vhwater log` resolves derived state rather
  than reading invalidated caches (G153). Existing capture metadata remains useful meanwhile.
- Performance: compare CPU/GPU p95 at the same viewpoint/settings, excluding capture readback.
  Include opaque, water, depth, copy and repair costs. Test both a seam and an ocean filling
  much of the screen; record resolution, loaded coverage and build configuration.
- Benchmark: `scripts/bench-windows.ps1` requires explicit game-launch approval. It deploys
  Debug artifacts and rejects stale ones. No post-0.4.1 controlled benchmark is established
  in the baseline evidence used here.
- Appearance: preserve build/settings with captures and screenshots. Source arithmetic,
  harness results and owner visual acceptance are different evidence levels.

Relevant durable traps remain G10 (ASCII shaders), G42 (scalar integer uniforms), G126
(primary-depth writes), G130 (sampler rebinding), G131/G155 (boundary geometry), G152
(cost-bearing geometry behind switches), G153 (resolved diagnostics), G154 (packed texture
slots), and G156 (screenshot context). A ground ownership signal does not prove water coverage.

## 7. Decisions still requiring evidence

These do not block the minimal renderer. Resolve them at the named gate with a concrete
comparison rather than asking the owner to choose technical constants in advance.

1. Residual-seam attribution, remaining raw-depth holes and long-term repair dependence: Phase 2.
   The broad band is closed. Junction depth has partial human acceptance; shore/loading/full-surface
   coverage and the 0.4.27 required-renderer baseline are unverified.
2. Near-to-distant opacity behavior and accepted clarity/ceiling defaults: Phase 3.
3. Whether missing texture detail contributes to near mismatch: Phase 3.
4. Foam quality, shadow cost, wave detail and any justified tier reach: Phases 4-5.

## 8. Evidence status and next action

**Built and harness-tested:** 0.4.27 Release, 22,576 assertions, zero failures; the 32-entry
private zip is verified and copy-installed. Checks cover frame identity, sampler collection,
capture contracts, bounded repair, junction predicates, shared shader initialization, direct
OIT composition and fail-loud path selection. They do not compile GLSL offline or prove raster
continuity on hardware.

**Captured:** sampled scattered and seam blips in the earlier renderer occur before water
composition and coincide with exact clear liquid-depth texels. The broad band was subsequently
reproduced in pure vanilla and removed by suppressing false missing-neighbour liquid sides.
No capture from a successfully compiled post-refactor dedicated water program is claimed yet.

**Human-tested:** the owner confirmed the broad band gone in 0.4.23. Earlier dedicated/legacy
and opaque/OIT comparisons looked unchanged, and 0.4.18 junction depth removed seam blips with
repair off. The owner later reported scattered blips gone with repair on and a narrower seam
remaining. The 0.4.26 log proved its intended water programs had failed to compile, so that
later picture cannot accept the new renderer. Version 0.4.27 is not human-tested.

**Runtime path evidence:** 0.4.26's isolated probe validated advanced entry points,
compute/SSBO and the private depth-copy/mip path on the owner's GPU; packed clustered terrain
reported complete command coverage. That closes the earlier unexplained startup probe failure.
It does not validate 0.4.27's repaired GLSL or provide a controlled current-path benchmark.

**Next action:** run 0.4.27 and preserve the first log. Require positive packed-terrain and
dedicated water surface/depth/OIT readiness with no `REQUIRED ... FAILED`; missing output is now
the intended signal for a broken path. Once active paths are proven, capture the narrower seam
stationary and moving, compare raw and repaired depth, and complete the continuity/runtime
matrices. Do not advance to foam, shadows or extra wave detail while this gate remains open.

Session 80 records the broad-band attribution, shader regression and fail-loud policy. The
implementation sequence and acceptance criteria remain in force; partial success does not relax them.
