# Vintage Horizons - status

> Tier 2: current state and verification evidence. Historical measurements and development
> narrative live in the sessions; durable design lives in `dev/ARCHITECTURE.md`.

**Status date:** `2026-09-08`
**Mod version:** `0.4.36`
**Active branch:** `codex/water-depth-light`, accepted source checkpoint `049d0c5`
**Target:** Vintage Story 1.22.5+, .NET 10
**Source files:** `97` C# files under `VintageHorizons/src`
**Assist protocol:** `1`
**Blob format:** `4`
**Database schema:** `6`

## Current result

**The settled vanilla/cached-water handoff is human-accepted in 0.4.36.** After the earlier broad
vanilla missing-neighbour wall was removed, the dedicated renderer reduced the remainder to a
narrow dotted seam. A full-frame capture proved that line was double-drawn OIT water rather than
a hole. Version 0.4.36 makes full-resolution vanilla revealage authoritative for visible surface
coverage, and the owner reports that he cannot tell where vanilla water ends and LOD begins in the
tested scene.

The water material now uses the live animated standard-water tile, preserves climate-map alpha
and follows vanilla's view-angle opacity. Owner comparisons selected maximum alpha **1.3**, foam
**1.6** and clarity **40** as the current defaults. Height and render distance were confirmed to be
proxies for the same variable—the angle at which the water surface is viewed—not separate color
responses.

A new movement-only issue is open: when vanilla first draws a newly loaded water chunk, a lighter
chunk-sized patch can appear for a split second. Source tracing fits one frame of overlap before
the conservative readiness tracker publishes vanilla ownership. The next authorized change is a
same-frame per-pixel OIT water veto using the vanilla revealage and liquid-depth snapshots already
captured by the renderer. It has not yet been built or human-tested.

## Water evidence and remaining gate

| Component | Established evidence | Current limit |
|---|---|---|
| Broad band | Reproduced in pure vanilla; false missing-neighbour liquid wall suppressed in 0.4.23; owner confirms gone | Closed independently from cached-water material |
| Required renderer | Dedicated water and packed terrain compiled and ran on the owner's GPU after 0.4.27 | Second-driver coverage remains open |
| Material | Live water tile/animation, climate tint alpha and view-angle opacity now share vanilla's inputs | Modded liquids and broader scenes not surveyed |
| Appearance defaults | Human A/B selected alpha 1.3, foam 1.6 and clarity 40 after the material path was corrected | Judgement values, not universal physical constants |
| Seabed/depth | Junction and bounded repair are active; accepted composite matches well in the tested view | Full shallow/deep and underwater matrix incomplete |
| Narrow seam | Capture found 960 double-drawn pixels, about 21% brighter than either side; 0.4.36 replaces coarse surface coverage with full-resolution revealage | Settled scene accepted; movement lifecycle still open |
| Open-water blips | Briefly reported only while moving, then could not be reproduced | Watch item; no change without a moving capture |
| New-chunk flash | Screenshot shows a split-second light chunk patch; readiness source path explains a plausible one-frame overlap | Diagnosis only; corrective build and human test pending |

The dedicated-water plan remains at its Phase 2 continuity boundary. The stationary visual seam is
closed; repeated streaming, underwater, lifecycle and unrelated-transparency cases still gate the
phase as a whole.

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
Physical removal remains open after current water continuity work.

## Runtime and artifact evidence

The accepted 0.4.36 Release build completed with zero warnings and zero errors. Fast checks passed
**22,651 assertions with zero failures**. The verified 32-entry archive contains the root DLL,
manifest and LICENSE plus the required water/ownership shaders, and excludes the PDB and game
DLLs.

`dist/vintagehorizons_0.4.36.zip` and the copy under the user's Vintage Story Mods directory share
SHA-256 `E971ADFACE300CE9EB897C055A5DAF518467EA32152DB293EC1505C388454904`. Older test zips were
preserved. The assistant did not launch Vintage Story.

The accepted source was committed as `049d0c5 Match distant water and eliminate seam overlap`.
No push, tag or public release was made. The assist protocol, blob format and database schema
remain `1 / 4 / 6`.

## Established product state retained

- The client-only persistent multilevel LOD cache and optional server assist/generation remain
  intact. This water work changes no protocol, blob or database meaning.
- Versioned workers, bounded publication/upload, asynchronous persistence and parent coverage
  remain the concurrency baseline.
- Packed indexed terrain, regional arenas, clustered HZB culling, ownership masks and current
  vanilla depth remain the accepted terrain direction. Required paths fail visibly.
- Surface-only L0 slabs, block textures, biome/season tint and remembered climate remain in the
  product. Close Biome Colors covers L0-L2; far tint defaults off.
- The terrain render-threshold wall correction and missing-neighbour liquid-side suppression are
  separate geometry hooks and preserve loaded shorelines.

## Next work

Implement the narrow same-frame OIT water veto described in
[the water handoff](dev/plans/HANDOFF_WATER_SEAM.md): preserve two-frame chunk readiness, but reject
a cached water pixel immediately when the immutable vanilla revealage shows a transparent
contribution and vanilla liquid depth confirms it is liquid. Add checks that unrelated transparent
objects do not hide cached water, package the next patch, and leave the visual verdict to the owner.

Then repeat movement across new chunk loads and verify the accepted settled seam remains absent.
If open-water blips return, capture them while moving before changing glint, foam or depth repair.
Complete the remaining continuity matrix across corners, LOD joins, shores/deep water,
above/below-water travel, stale cache, resize, shader reload and world change.

[TODO](dev/TODO.md) contains the full open work. [Session 81](dev/sessions/SESSION_81.md) preserves
the material/seam investigation, [Session 80](dev/sessions/SESSION_80.md) preserves the required
renderer and broad-band attribution, and [the water plan](dev/plans/PLAN_LOD_WATER_RENDERER.md)
owns the phase gates. Architecture and gotchas own durable decisions;
`dev/WIRE_HISTORY.md` remains the compatibility authority.

The active performance route remains `dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md`; renderer work is
further specified by `dev/plans/PLAN_GPU_DRIVEN_TERRAIN_RENDERER.md`.

Documentation validation passed **2,187 of 2,192 checks**. The five remaining failures are the
known personal Windows paths inside ignored `.testdata/water-extraction` and `.testdata/water-oit`
diagnostic logs. Version, source count, compatibility, links and active-plan routing pass; private
evidence was not rewritten to make the checker green.
