# Session 99 — Volumetric Farseer seam fog through 0.4.108

**Date:** `2026-09-17`
**Branch/commit:** `codex/water-depth-light`; source checkpoint `6708b19`
**Mod version:** `0.4.102–0.4.108`; 0.4.108 built, verified, copy-installed and awaiting its focused corner check
**Assist protocol / blob / schema:** `1 / 4 / 6`; both silhouette stores remain schema `1`

> Session records are Tier 3 history. This record follows Session 98's exact ownership work and
> records the decision to conceal the residual visual join with an explicit atmospheric volume.

## Context and investigation

The exact 0.4.101 ownership handoff did not make the transition visually natural enough. The owner
asked for heavy fog centred on the real Farseer/LOD ownership seam, spilling roughly a chunk into
both terrain systems and fading away from the centre. This was a presentation layer over the exact
handoff, not permission to weaken ownership or move the boundary.

The first implementation proved that the live all-level ownership coverage could identify the
seam exactly, but painting fog into the two terrain surfaces looked flat, bright and angular in
screenshots `2026-09-17_17-04-53.png` and `17-05-11.png`. Replacing that paint with translucent
geometry gave the fog height and depth, but also exposed several render-stage mistakes: black or
missing water, OpenGL `InvalidOperation`, visible card layers, water repainting over the fog, and
corner quadrants not reached by straight edge strips.

The session therefore became both a visual iteration and a stage-ownership audit. The final design
keeps terrain ownership exact, builds one bounded fog volume from that exact boundary, and composites
the atmospheric veil only after cached/vanilla water has finished OIT.

## Work narrative

### 1. Mark the exact live ownership frontier (0.4.102)

0.4.102 builds one byte per 32-block visible Farseer cell from the same stable replacement answer
introduced in 0.4.101. It records whether Farseer data exists, whether ready real terrain replaces
it, and which cardinal neighbours have the opposite owner. Missing neighbour tiles are not invented
as seams. A content signature avoids rebuilding or uploading an unchanged mask.

The first fog used this mask directly in the cached-terrain and Farseer surface shaders. It proved
the location was available to both sides of the join, but it could only colour existing surfaces;
it could not hang in the air or spill naturally over cliffs and water. The chunk-aligned ramps were
plainly visible, so this visual approach was rejected.

### 2. Replace surface paint with a real volume (0.4.103)

0.4.103 removed the per-surface blend. The seam mask now also emits a unique list of exact boundary
edges, and a worker-safe array builder turns those edges into one translucent mesh:

- vertical crossing sheets provide depth from ground-level views;
- horizontal crossing sheets provide a top and aerial body;
- density is strongest at the exact seam and fades to zero at the lateral, upper and lower rim;
- the default volume spills 48 blocks to each side, rises 88 blocks and starts 16 blocks below the
  sampled surface;
- three-dimensional world-anchored noise and slow drift break up repeated layers;
- the mesh is rebuilt only when the ownership boundary or shape settings change.

The first volume used five vertical and four horizontal layers. The owner asked for more natural,
voluminous fog with height and no hard chunk lines. The resulting 0.4.103 screenshot showed that
the geometry was present, but its cards were still bright and obvious and broad water had become a
near-black void. The matching client log reported OpenGL `InvalidOperation` after composition.

### 3. Isolate fog from water and expose every tuning dimension (0.4.104)

0.4.104 moved fog drawing into a color-only framebuffer. It samples primary scene depth read-only
in the fragment shader, writes RGB but not primary alpha, and restores every captured GL binding.
The first 240 fog frames check for GL errors; one failure disables Farseer for the session with a
named log line instead of silently corrupting later stages. Defaults increased to seven vertical
and five horizontal layers and reduced the minimum noise coverage.

The Farseer page gained eight persisted, live-previewed controls:

1. **Fog density** — overall opacity; zero disables the fog pass.
2. **Fog coverage** — the minimum veil left in low-density noise pockets.
3. **Fog spread** — lateral spill to each side of the seam.
4. **Fog height** — height above the sampled terrain surface.
5. **Fog floor depth** — reach below the surface for cliffs, shores and vertical gaps.
6. **Fog billow size** — broad world-space cloud scale.
7. **Fog drift speed** — motion of the world-anchored billows.
8. **Fog layer detail** — crossing-layer count and its geometry/blending cost.

This restored the intended separation in source, but the 0.4.104 runtime log still showed the fog
shader compiling and the first draw failing with `InvalidOperation`.

### 4. Remove two independent OpenGL errors (0.4.105–0.4.106)

The first error was the engine blend helper. With SSAO enabled it configures indexed blend state
for the engine's extra attachments, while the custom fog framebuffer exposes only
`ColorAttachment0`. 0.4.105 therefore uses explicit standard alpha blending for attachment zero
and keeps the RGB-only color mask. The next owner log still failed.

The remaining error was a uniform type mismatch. The shader declares `farViewDistance` as a
`float`, but the fog draw selected the integer overload when uploading the reach. OpenGL rejects
that upload even though the shader compiled correctly. 0.4.106 explicitly uploads a float and adds
a static regression check for the typed call. The owner then reported that the fog looked “very
good,” establishing the first successful volumetric result in game.

### 5. Cover water and ninety-degree turns (0.4.107–0.4.108)

Screenshots `2026-09-17_19-36-33.png` and `19-36-55.png` showed two remaining gaps:

- straight strips ended at a ninety-degree turn and left its diagonal quadrant empty;
- water/OIT composited after opaque fog and could paint a sharp chunk edge over the veil.

0.4.107 added low-density horizontal corner caps only at vertices where perpendicular boundary
edges meet. It also moved the fog draw to `AfterOIT` order 1.0, after Vintage Horizons' water
capture at 0.99. Fog now acts as an atmospheric overlay on already-composited water; water shaders,
depth, revealage, ownership and the terrain readiness mask are unchanged. The owner described the
result as excellent, confirming intact visible water and the general fog/water composition.

Screenshot `2026-09-17_20-02-06.png` still showed the corner taper as a missing adjacent section.
The radial cap reached only `spill × 0.7071` on each diagonal axis while the two straight bands each
reached the full spill distance. 0.4.108 changes only that perimeter to the full spill-by-spill
square footprint. All perimeter vertices remain zero-density, so the extra diagonal reach fades
away instead of ending as a hard square.

### 6. Verification and artifacts

- Final Release build: zero warnings and zero errors.
- Final fast tier: **25,168 assertions, zero failures**.
- Focused checks cover exact seam marking, no invented seam at missing tiles, bounded layered
  geometry, zero-density outer rims, one cap per real L-turn, full diagonal spill, render-stage
  order, explicit blend state, float-uniform upload and fog-setting normalization.
- `dist/vintagehorizons_0.4.108.zip`: 666,934 bytes, 43 root-relative entries, one DLL, no PDB or
  wrapper directory, manifest version 0.4.108.
- SHA-256: `3ADAB55D48C80E173400A9F690C6146FF9EB282A26DA2E000155D6FA217A088E`.
- The archive was copy-installed beside 0.4.101–0.4.107; all rollback files were retained.
- Exact source/test/version checkpoint: `6708b19`.

The agent launched no game. The owner supplied every runtime screenshot, log and visual verdict.

---

## Delivered

- **0.4.102 (`fe60a69`):** exact live seam mask and first two-sided surface fog.
- **0.4.103 (`bf0bd4b`):** one layered vertical/horizontal volume with world-space billows and
  zero-density outer edges; removed the rejected surface paint.
- **0.4.104 (`23bfb30`):** isolated RGB-only fog target, primary-depth sampling, guarded GL error
  handling, denser default layering and all eight fog controls.
- **0.4.105 (`3d0298c`):** explicit single-attachment blending instead of the SSAO-aware helper.
- **0.4.106 (`0257a0f`):** corrected the integer-to-float uniform upload; first owner-confirmed
  successful volumetric fog.
- **0.4.107 (`3ba4a1e`):** corner caps and post-water `AfterOIT` composition; owner described the
  result as excellent.
- **0.4.108 (`6708b19`):** full square-footprint corner fade, built, checked and installed.

## Decisions

- **Fog is presentation, not ownership.** The exact stable 32-block handoff remains authoritative;
  the fog volume is derived from it and may spill visually across both owners.
- **The seam veil is geometry, not terrain paint.** Crossing vertical and horizontal layers give
  it height and volume; every exposed rim fades to zero.
- **Water completes before the veil.** Seam fog draws after OIT so transparent water cannot repaint
  a sharp boundary over it. The fog may not rewrite water shaders, water ownership or shared
  terrain readiness to achieve that result.
- **Custom framebuffer state is explicit.** The fog pass owns one color attachment, writes RGB
  only, samples primary depth read-only and configures only the blend state it actually owns.
- **Corner reach follows the straight bands.** A turn fills the full spill-by-spill footprint, but
  density still reaches zero at its outer perimeter.
- **All material fog dimensions are player controls.** The eight controls persist, preview live and
  are normalized to bounded ranges; density zero is the explicit off state for the pass.

## Traps

- **Trigger:** tint the two terrain surfaces around a cell mask. **Failure:** the effect stays flat,
  follows chunk geometry and has no atmospheric height. **Safer:** derive separate translucent
  volume geometry from the exact boundary.
- **Trigger:** draw into the primary scene target before water and alter alpha/depth. **Failure:**
  OIT may disappear, turn black or repaint a sharp edge over the fog. **Safer:** preserve primary
  alpha/depth and composite the veil after OIT.
- **Trigger:** use the engine's general blend helper on a framebuffer with only attachment zero.
  **Failure:** SSAO-enabled indexed blend calls target attachments that do not exist. **Safer:** set
  ordinary alpha blending explicitly for the owned attachment.
- **Trigger:** trust shader compilation as proof that uniform uploads are valid. **Failure:** an
  integer upload to a declared float uniform raises `InvalidOperation` at draw time. **Safer:**
  match every GLSL uniform's type at the C# call site and keep a static regression check.
- **Trigger:** end independent edge strips at a turn or cap them with only a circular footprint.
  **Failure:** the square diagonal quadrant reads as a missing fog chunk. **Safer:** fill the full
  square footprint and put zero density on its perimeter.

## Flagged and unverified

Judgement calls awaiting human review:

- Whether 0.4.108 turns now read as one continuous sweep with no rounded or square missing adjacent
  section and no newly obvious square outer boundary.
- Whether the current defaults should stay after the corner is accepted; tune through the eight
  dials rather than changing geometry for a density, scale or motion preference.

Claims lacking required evidence:

- 0.4.108 has not run in game. Its full-corner result is source- and harness-tested only.
- A clean 0.4.108 client log with no seam-fog GL failure has not been preserved.
- The fog's GPU/blending cost and the final Farseer off/on cost are unmeasured.
- The earlier rapid replacement, excavation, weather/night/winter, cold-restart/full-disc,
  dedicated-player and mixed-version Farseer matrices remain incomplete.
