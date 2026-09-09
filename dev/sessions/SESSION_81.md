# Session 81 — distant water reaches an accepted seam and material match

**Date:** `2026-09-08`
**Branch/commit:** `codex/water-depth-light`; accepted source checkpoint `049d0c5`
**Mod version:** `0.4.28-0.4.36`; 0.4.36 human-tested and accepted
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

Session 80 ended at 0.4.27 with the broad vanilla missing-neighbour wall fixed, but the
dedicated water shaders had only just been repaired and made fail-loud. The next work had to
establish that the intended renderer really ran, then solve the narrower vanilla/cache handoff
without confusing color, underwater depth, surface coverage and renderer selection again.

The owner performed every game test and supplied screenshots, command output and staged water
captures. The assistant did not launch Vintage Story. Successive private builds 0.4.28-0.4.36
were used as instruments as well as candidates; visual conclusions below are human observations,
while pixel counts and source causes come from the captured buffers and source tracing.

## Work narrative

### 1. The dedicated path became real and changed the whole comparison

The repaired required path compiled and ran. The broad band stayed gone, and the remaining
handoff collapsed to a very narrow line. The owner also reported that essentially every material
component differed from the old fallback picture: top-water color, seabed treatment and opacity
all moved. This was the first valid visual baseline for the dedicated surface/OIT path.

The narrow seam changed slightly and became darker in the explicit opaque-stage comparison. That
showed composition mattered, but did not by itself identify whether the OIT image had a hole or
an overlap. There were no stable blips in the first settled comparisons.

### 2. Height and render distance were proxies for the same variable: view angle

At 32 render distance, a lower alpha ceiling looked closer because the cached water began near
the camera and was viewed more steeply. At 192, a higher ceiling looked closer because the same
water was farther away and viewed more shallowly. Changing camera height at one render distance
produced the same behavior. The owner corrected the working description: this was not a
low-altitude response and not a render-distance response; both controls changed the angle between
the eye ray and the surface.

The shader now follows vanilla's view-angle opacity arithmetic. The diagnostic ceiling was
widened to vanilla's real pre-texture range, 0-2 rather than 0-1. Side-by-side owner comparisons
settled on **1.3** as the best cross-angle maximum. This is the saved default.

### 3. The live water tile needed its alpha path, not merely its RGB

The dedicated surface sampled and animated the game's standard water atlas tile at world-block
scale. Matching the visible tile and RGB tint was still insufficient: vanilla's color-mapped
texture path multiplies the sampled texture alpha by the climate-water tint alpha, while the
cached path had forced that map channel opaque. In the captured comparison the effective alpha
ratio was about 0.665, explaining why cached water remained roughly 1.5 times too opaque even
after its RGB looked plausible.

Climate map pages now preserve RGBA rather than RGB alone, the fallback tint registry retains
the alpha channel, the vertex/fragment interface interpolates `tintAlpha`, and the water shader
uses the mapped alpha at the same point as vanilla's material path. This was a rendering change,
not a cache-format change.

The foam diagnostic range was widened from 0-1 to 0-2 after the owner found vanilla carried a
little more white surface texture than a capped value could reproduce. **1.6** was the closest
human match and is now the default. Clarity first looked best at 25 while other material terms
were still wrong; after texture alpha, view-angle opacity and foam were corrected, the owner
retested and chose **40**. The change in preferred clarity was therefore retained as the result
of a different final composite, not treated as contradictory evidence.

At the accepted settings, the owner described the high/top-down comparison as excellent and the
shallower view as much improved. Seabed and surface could finally be judged together instead of
compensating one wrong layer with another.

### 4. The last dotted seam was two water surfaces, proved by a full-frame capture

The final staged capture was preserved under the ignored diagnostic area as
`.testdata/water-captures/20260909-004837-302-22ebc07a`. It found **960 full-resolution pixels**
where vanilla OIT water and cached OIT water both contributed, exactly tracing the pale broken
seam. Mean luminance in those overlap pixels was 0.2155, against 0.1782 in adjacent vanilla-only
water and 0.1761 in cached-only water: about 21% brighter. This separated double composition from
missing seabed depth or a raw surface hole.

The cause was a resolution mismatch in `lodownershiptest.fsh`. Vanilla liquid depth is
quarter-resolution, while OIT revealage is full-resolution. The surface test ORed a quarter-depth
"clear" answer with a full-resolution revealage "clear" answer. At pixels where revealage proved
vanilla water actually drew, the coarse depth sample could still admit cached water, producing
the brighter double layer.

Version 0.4.36 makes the full-resolution revealage copy authoritative for OIT surface coverage:
when available it **replaces**, rather than supplements, the quarter-resolution answer. The
quarter-resolution liquid target remains authoritative for underwater depth. The capture also
showed the depth machinery was active rather than inert: junction geometry supplied 102 pixels
and bounded repair supplied 23 in that frame.

The owner tested 0.4.36 and reported: "I can't tell where vanilla water ends and LOD begins."
That closes the narrow settled handoff for the tested scene and settings. It does not claim the
full movement/reload/underwater matrix is complete.

### 5. The moving-blip report remains a watch item, not a tuning target

The owner briefly saw small blips while moving, including in open cached water away from the
seam. A capture at the scene associated one visible white mark with surface composition/glint
rather than seabed color or a liquid-depth hole. The owner then had difficulty reproducing the
blips and asked to continue with the seam. No speculative widening or suppression was applied.
Future work should collect moving evidence only if the symptom returns reliably.

### 6. A separate one-frame loading overlap appeared after acceptance

After the seam was accepted, the owner supplied a screenshot showing a light chunk-sized patch
for only a split second when movement caused vanilla to draw a newly available water chunk. The
current source diagnosis is intentionally recorded as unverified: the cached mesh is not evicted
at the handoff. Per-fragment ownership masks it only after the readiness tracker has observed the
vanilla chunk rendered in two distinct frames and published the ownership texture. That cautious
delay prevents holes, but it permits one frame in which newly drawn vanilla OIT water and cached
water can both contribute.

The proposed correction keeps the two-frame chunk readiness rule for terrain and long-lived
ownership. For cached OIT water only, the already captured same-frame full-resolution vanilla
revealage will veto a cached surface pixel when the vanilla liquid-depth image also shows liquid.
The depth gate matters: revealage can contain unrelated transparent objects, which must not hide
water. This change is authorized next, but was not part of 0.4.36 and has no human evidence yet.

### 7. Verification and artifact

The accepted 0.4.36 Release build completed with zero warnings and zero errors. The fast suite
passed **22,651 assertions with zero failures**. The verified package has 32 entries, with the
root DLL, manifest and LICENSE, all required water/ownership shaders, no PDB and no game DLLs.

`dist/vintagehorizons_0.4.36.zip` and the copy installed beside older versions share SHA-256
`E971ADFACE300CE9EB897C055A5DAF518467EA32152DB293EC1505C388454904`. Source was committed as
`049d0c5 Match distant water and eliminate seam overlap`. No push, tag or public release was made.

`dev/DocCheck.ps1` passed **2,187 of 2,192 checks** after the documentation reconciliation. The
five failures are the pre-existing personal Windows paths inside ignored diagnostic logs under
`.testdata/water-extraction` and `.testdata/water-oit`. Version, the 97-file source count,
compatibility, links and active-plan routing pass. Private evidence was not rewritten merely to
silence the checker.

---

## Delivered

- The dedicated packed-terrain and water paths compiled and produced the owner-tested picture.
- Cached water now samples and animates vanilla's standard water tile and carries climate-map
  alpha through the packed tint path.
- View-angle opacity, mapped texture alpha, foam and underwater clarity were brought into one
  coherent material result. Accepted defaults are alpha 1.3, foam 1.6 and clarity 40.
- A staged capture proved the residual dotted seam was full-resolution OIT overlap, quantified
  its 960 affected pixels, and ruled out a missing-depth explanation for that line.
- Version 0.4.36 makes full-resolution revealage authoritative for OIT surface coverage. The
  owner accepted the settled handoff as visually indistinguishable in the tested scene.
- Release build, 22,651 fast assertions and the verified/copy-installed 0.4.36 package completed.
- Accepted source was committed at `049d0c5`. Compatibility stayed 1/4/6.
- Documentation passed 2,187 of 2,192 checks; all five failures are known ignored private-path
  evidence logs, while version, source count, compatibility, links and routing pass.

## Decisions

- Describe alpha behavior by view angle. Camera height and render distance are only ways of
  changing that angle and must not become independent correction curves.
- Match the complete color-map result, including alpha. An RGB-only water tint is not material
  parity.
- Use the highest-resolution authoritative signal for the stage it describes: OIT revealage for
  the visible water surface, liquid depth for underwater absorption.
- Accept 1.3 / 1.6 / 40 as the current water alpha / foam / clarity defaults based on the owner's
  side-by-side comparisons after all three paths were corrected.
- Leave the non-reproducible movement-only open-water blips untouched until they can be captured.
- Preserve conservative two-frame chunk ownership, and address the new transient water overlap
  with same-frame per-pixel evidence rather than making all ownership optimistic.

## Traps

- Matching a color-mapped texture's RGB while discarding its map alpha produces a different
  translucent material. Preserve and apply RGBA through every fallback and packed path.
- A coarse coverage texture and a fine coverage texture do not become safer when their "clear"
  answers are ORed. If the fine signal describes the output stage, it must replace the coarse
  answer there.
- A screenshot series taken at different height or render distance can look like two response
  curves when both merely changed the water viewing angle. Name the physical variable first.
- An accepted settled seam does not prove movement handoff. Chunk readiness deliberately lags
  new engine geometry, so inspect same-frame composition before changing mesh residency.

## Flagged and unverified

- **Judgement awaiting human review:** the proposed same-frame OIT water veto for the split-second
  light patch when a new vanilla water chunk begins drawing.
- **Working diagnosis only:** the light patch is one-frame double composition during the
  readiness tracker's stabilization/publication delay. The source lifecycle and screenshot fit;
  no diagnostic build has tested the correction yet.
- **Watch item:** movement-only white blips in open cached water. They were reported once, were
  not reproducible afterwards, and should not drive a change without a moving capture.
- **Missing evidence:** continuity across repeated chunk streaming, corners, stale cache,
  shallow/deep shores, underwater travel, resize/fullscreen, shader reload and world change.
- **Evidence scope:** the owner's acceptance is one machine and the compared water scenes; it is
  not second-driver or broad biome/modded-liquid coverage.
- **Open implementation:** physical removal of the non-drawing per-section opaque `MeshRef`
  lifecycle and revision of old fail-open renderer fixtures remain separate renderer debt.
