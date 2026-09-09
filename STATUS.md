# Vintage Horizons - status

> Tier 2: current state and verification evidence. Historical measurements and development
> narrative live in the sessions; durable design lives in `dev/ARCHITECTURE.md`.

**Status date:** `2026-09-09`
**Mod version:** `0.4.39`
**Active branch:** `codex/water-depth-light`, 0.4.39 human-tested and accepted
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

The movement-only light patch is now reduced to an accepted limitation. Version 0.4.37 suppresses
a cached OIT water pixel before durable ownership catches up only when same-frame vanilla liquid
depth and full-resolution revealage both prove liquid coverage. The owner found no holes and said
the patch became much shorter and barely perceptible.

Version 0.4.38 tried to remove the remaining frame by carrying engine-announced chunk presence
through the shared readiness mask. The water looked fine, but the owner found severe holes in
terrain and the seabed. Version 0.4.39 completely removes that extension and restores 0.4.37
behavior. The owner tested 0.4.39 and called it acceptable. Water development may not alter shared
terrain readiness, ownership or mask-publication semantics; a residual water-only flicker is
preferable to any terrain hole.

## Water evidence and remaining gate

| Component | Established evidence | Current limit |
|---|---|---|
| Broad band | Reproduced in pure vanilla; false missing-neighbour liquid wall suppressed in 0.4.23; owner confirms gone | Closed independently from cached-water material |
| Required renderer | Dedicated water and packed terrain compiled and ran on the owner's GPU after 0.4.27 | Second-driver coverage remains open |
| Material | Live water tile/animation, climate tint alpha and view-angle opacity now share vanilla's inputs | Modded liquids and broader scenes not surveyed |
| Appearance defaults | Human A/B selected alpha 1.3, foam 1.6 and clarity 40 after the material path was corrected | Judgement values, not universal physical constants |
| Seabed/depth | Junction and bounded repair are active; accepted composite matches well in the tested view; 0.4.39 restores terrain-safe ownership | Full shallow/deep and underwater matrix incomplete |
| Narrow seam | Capture found 960 double-drawn pixels, about 21% brighter than either side; 0.4.36 replaces coarse surface coverage with full-resolution revealage | Settled scene accepted; movement lifecycle still open |
| Open-water blips | Briefly reported only while moving, then could not be reproduced | Watch item; no change without a moving capture |
| New-chunk flash | 0.4.37's water-only depth-plus-revealage veto made it much shorter with no holes; accepted again as 0.4.39 | Barely perceptible residual flicker accepted; shared terrain state is off-limits |

The dedicated-water plan remains at its Phase 2 continuity boundary. The stationary visual seam
and tested streaming correction are accepted; repeated streaming, underwater, lifecycle and
unrelated-transparency cases still gate the phase as a whole.

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

The accepted 0.4.39 Release build completed with zero warnings and zero errors. Fast checks passed
**22,656 assertions with zero failures**. The verified 32-entry archive contains the root DLL,
manifest and LICENSE plus the required water/ownership shaders, and excludes the PDB and game
DLLs.

`dist/vintagehorizons_0.4.39.zip` and the copy under the user's Vintage Story Mods directory share
SHA-256 `1791924D1FFAEF6575EA70013079E660DE212B81ACD8229FFBF8B9AE0BB62863`. Older test zips were
preserved. The assistant did not launch Vintage Story.

The 0.4.36 settled-seam source remains checkpointed at
`049d0c5 Match distant water and eliminate seam overlap`; the accepted 0.4.39 water-only guard and
rollback are recorded by Session 82's commit. No push, tag or public release was made. The assist
protocol, blob format and database schema remain `1 / 4 / 6`.

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

Preserve 0.4.39 as the accepted baseline. Any future work on the residual new-chunk flicker must
stay inside the dedicated water stage and leave terrain readiness, ownership and publication
unchanged. Do not reintroduce engine-announced presence into the shared mask. If a proposal needs
terrain behavior to change, stop and seek explicit owner authorization as a separate project.

Repeat movement across new chunk loads and confirm the settled seam remains absent. If open-water
blips return, capture them while moving before changing glint, foam or depth repair. Complete the
remaining continuity matrix across corners, LOD joins, shores/deep water, above/below-water travel,
stale cache, resize, shader reload, world change and unrelated transparency.

[TODO](dev/TODO.md) contains the full open work. [Session 82](dev/sessions/SESSION_82.md) records
the streaming correction, rejected terrain regression and accepted rollback;
[Session 81](dev/sessions/SESSION_81.md) preserves the material/seam investigation, and
[the water plan](dev/plans/PLAN_LOD_WATER_RENDERER.md) owns the phase gates. Architecture and
gotchas own durable decisions;
`dev/WIRE_HISTORY.md` remains the compatibility authority.

The active performance route remains `dev/plans/PLAN_MAIN_THREAD_PERFORMANCE.md`; renderer work is
further specified by `dev/plans/PLAN_GPU_DRIVEN_TERRAIN_RENDERER.md`.

Documentation validation passed **2,189 of 2,194 checks**. The five remaining failures are the
known personal Windows paths inside ignored `.testdata/water-extraction` and `.testdata/water-oit`
diagnostic logs. Version, source count, compatibility, links and active-plan routing pass; private
evidence was not rewritten merely to make the checker green.
