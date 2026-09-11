# Session 83 — spatial winter terrain built; handoff flicker remains open

**Date:** `2026-09-09–10`
**Branch/commit:** `codex/water-depth-light`; uncommitted 0.4.40–0.4.49 worktree based on `8ace702`
**Mod version:** `0.4.40–0.4.49`; 0.4.49 installed and human-tested, not accepted
**Assist protocol / blob / schema:** `1 / 4 / 6` (append-compatible fields/tails added)

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The session began with a clear seasonal regression. The owner stood in one snowy chunk and, after
the scheduled seasonal refresh, all visible distant terrain became snowy. Moving to a nearby bare
area made all visible snow disappear again. Grass and foliage already used a position-specific
climate field, so the initial suspicion was either a disabled spatial setting or snow never having
been included in that mechanism.

Source inspection confirmed the latter. Distant snow still used one `snowLineY` uniform computed
from the player's current position. Every terrain vertex read that same threshold. The climate
field had made biome/season color spatial without making the snow decision spatial.

Correcting that global input exposed the second, larger product question: a calculated snow line
can estimate seasonal snow where the client has never seen the actual blocks, but it cannot match
local vanilla accumulation, melt, shelter, or snow-covered variants reliably. The owner supplied
screenshots in which estimated LOD snow extended into terrain vanilla left bare. Later captures
showed a grey/sparse pattern where vanilla had continuous snow, and vanilla grass and trees were
frosted while the LOD was not. The owner directed that snow use color/texture on existing LOD
blocks rather than add vanilla-style snow geometry.

By the end of the winter work the owner said the snow visuals looked fine. A separate, conspicuous
vanilla-to-LOD flash remained whenever vanilla meshes were evicted. The owner emphasized that
this was a new regression introduced during this session, based on extensive earlier playtesting,
not merely an old transition made more visible by white snow. Occasional brief terrain holes were
also observed, though the later report described them as very few.

All in-game observations and screenshots came from the owner. The assistant did not launch
Vintage Story.

## Work narrative

### 1. The snow line joined the spatial climate field

`LodClimateField` now stages raw temperature, rainfall, spread distance, and a current snow
threshold at one 64-block cell per level-zero section edge. Its existing 128×128 texture stores
the quantized threshold in alpha, so the shader's four climate texel fetches also answer snow and
no new sampler or texture lookup was introduced. The field still spreads the nearest known
climate answer through client knowledge gaps, but observation provenance does not spread.

The renderer derives each cell's threshold from the cell's own raw climate, the current calendar
date, the engine's supplied-date temperature mode, and the same lapse-rate calculation previously
used at the player. The old uniform remains only as a no-field fallback. The climate field stays
available for snow even when optional far-biome tint is disabled; otherwise a performance switch
for color would silently reinstate global player-local snow.

The ordinary seasonal cadence remains 30 seconds. A newly arrived authoritative snow observation
may request an earlier field refresh after a one-second debounce, coalescing a burst of capture
results rather than rebuilding for every column.

### 2. Loaded vanilla terrain became the authoritative snow source

The capture path now distinguishes provenance:

- Loaded client/server/savegame columns contain the real blocks vanilla currently uses and set
  `SnowStateObserved = true`.
- Transient `PeekChunkColumn` world generation supplies useful terrain but not current
  accumulation and deliberately remains unknown.

`LodBlockPolicy` classifies both snow material and blocks whose live
`Variant["cover"] == "snow"`. During top-down capture, a snow layer marks the first real surface
below it as covered; a snow-covered variant marks itself. Buried snow cannot repaint a newer
surface above it.

Each 64×64 section holds two 4,096-bit maps. One records that the snow answer was actually
observed; the other records bare versus covered. An authoritative bare recapture can therefore
clear old snow, while a later generated peek cannot erase stronger observed truth. Provenance-only
changes persist and propagate without forcing an identical bare mesh to rebuild. An actual
bare/covered transition advances the render obligation.

This paired representation corrected the grey/sparse result. The first 0.4.43 extension stored
only provenance and relied on snow material to survive the mip. A thin snow layer could lose the
material vote while the provenance bit survived, causing the coarse section to assert
authoritative bare ground. The second bitset keeps the answer independent. Mips assert a parent
answer only when every contributing captured child was observed, then choose snow by areal
majority and retain it at an exact tie.

Both maps are append-compatible tails on section blob format 4: 512 bytes each before deflate.
Old readers stop after the captured-column map and ignore them. New readers treat no tail as
legacy unknown, reject a partial tail, and clear a provenance-only 0.4.43 answer rather than
misreading it as bare. Derived palette flags use previously unassigned bits 32 and 64 and are
recomputed from the live block registry. Blob format and database schema therefore remain 4/6.

### 3. Winter uses existing terrain geometry and preserves seasonal color

Current meshing does not emit geometry for snow layers. The real surface below receives an
observed-snow material marker in the packed representation's fourth 64-value alpha band. That
marker splits merged horizontal faces only where the appearance differs; it adds no snow slab,
cube, wall, side coverage, or additional vertical layer. The retired expanded comparison stream
cannot carry both its historical texture encoding and the new marker, so it uses a safe flat snow
fallback; packed clustered terrain remains the required visible product path.

Unknown terrain continues to use the per-position climate threshold with the existing 24-block
vertical blend. Observed-covered surfaces are white independent of that estimate, and
observed-bare surfaces suppress it.

Frost is also a shader color effect. Tint metadata now packs a frostable bit separately from the
leaf bit. At each vertex the shader uses the local climate sample, altitude, current season
temperature, and the engine's value-noise pattern to derive frost strength. It composes the frost
ratio over the existing climate/season tint so grass and foliage continue to change with the
season beneath the winter overlay. No captured color is frozen into the mesh.

The static and executable checks cover snow block classification, capture authority, old/new blob
round trips, mip provenance and majority/tie rules, mesher material bands, packed-quad transport,
climate-field encoding/spread, frost shader contracts, and no-extra-geometry behavior.

### 4. Existing terrain first used a rolling refresher, then a finite event-driven scheduler

The first automatic catch-up mechanism continuously loaded and recaptured one generated chunk
column per second after the startup savegame sweep. One “column” is a 32×32 vanilla chunk
footprint through the full world height, not one block. It was bounded and inexpensive at any
instant, but the owner correctly challenged the lifetime cost: almost all regions either never
snow, remain snow-covered, or spend most of the year unchanged. A perpetual disk/load/capture/
persistence/mip/mesh cycle was the wrong response to two rare transitions.

The continuous sweep was removed. The current design has two paths:

1. Loaded terrain recaptures immediately when vanilla raises `ChunkDirty`, which includes its
   actual snow commits.
2. Dormant generated terrain is indexed by the startup sweep. Every 30 seconds the server reads
   the vanilla weather system's public `SnowAccumSnapshots` for active map regions. Corner
   deltas cross coarse accumulation/melt bands and enqueue one finite regional campaign. One
   dormant map region is woken as a canary per poll so an unloaded region eventually reveals new
   snapshots. No columns cycle between changes.

Campaign candidates remain the sweep's proven-safe generated-column set and cannot peek or
generate missing terrain. Unknown columns are eligible in either direction; after capture,
accumulation skips wholly covered columns and melting skips wholly bare columns. Mixed columns
remain eligible. The requested “can accept snow” metadata was rejected: virtually every terrain
column has some accepting surface, and observed covered/bare state plus bounded weather activity
already supplies the useful filter.

`RefreshColumnsPerSecond` defaults to 1, clamps to 0–16, and 0 disables automatic snow refresh
without disabling the one-time sweep. Campaign issue work shares a 1 ms owning-thread budget.
Snapshot polling examines at most 480 accumulated weather entries per region; truncated history
conservatively signals both directions.

### 5. A manual refresh and live replacement route were added

`/vhrefresh [radius]` gives an administrator a deterministic catch-up command. Its default is
32 chunks, maximum 128, with `status` and `stop`. It uses the same two-pass existence probe and
safe-neighbourhood classification as savegame sweeping. Missing columns are skipped; refresh has
no action that can select transient generation. It reports that visible results follow capture,
persistence, and the next refresh offer.

Existing rows posed a delivery problem: the original assist manifest was a monotone key set, so
connected clients would never request a key they already held. The server now collects durable
level-zero changes and sends the same manifest message with append-compatible field
`Refresh = true`. Only L0 is transferred; the client rebuilds its mip ancestors rather than
receiving six redundant levels. `AssistServerSettings` also gained optional field 16 for the
refresh rate. Protocol remains 1 because old protobuf peers ignore both fields and retain their
old one-shot behavior.

Integrated singleplayer cannot use two unversioned sources indiscriminately. Its read-only sibling
cache scanner now compares `ModifiedMs` for known L0 rows and publishes update deltas; when that
reader exists, it is the sole freshness authority and the network duplicate is ignored.

Remote replacements are decoded off-thread but carry the content revision that existed at
submission. The owning thread rechecks before and after live palette resolution, so a client
capture that arrives later wins. The update route stays active until the exact installed
persistence revision is acknowledged; an older save acknowledgement cannot retire a newer
replacement still in flight.

### 6. Performance analysis and resulting budgets

The chosen snow representation adds two 512-byte arrays per resident section plus corresponding
immutable snapshot copies. Deflate reduces their stored cost, especially for uniform unknown,
bare, or covered regions. The climate field adds one float per staging cell and one provenance
bit in CPU memory but reuses the existing GPU texture alpha and its existing four interpolated
fetches. Snow itself adds no geometry; differing covered/bare cells can reduce greedy merge size.
Frost adds arithmetic and value noise to frostable vertices using the climate sample already
needed for snow.

The scheduler does no column work between detected weather changes. While a finite campaign is
active, the default ceiling is one full-height vanilla chunk column per second, under the same
1 ms issue budget used by the safe sweep. Persistence retains its 30-second checkpoint and
bounded worker path. Network sends only changed durable L0 rows to greeted clients. This makes the
dominant cost proportional to actual accumulation/melt campaigns rather than real play time.

These are source-derived bounds, not a completed runtime benchmark. A large-world paired
measurement of campaign tick, storage, mesh, and transfer cost remains open.

### 7. The new vanilla-to-LOD flicker survived two attempted fixes

The snow update path increased section replacements, so an old asynchronous mesh result became
the first hypothesis. Version 0.4.48 added exact render revisions to mesh jobs/results, rechecked
before submission, and rejects a stale same-world result at upload while keeping the current live
mesh and restoring the dirty obligation. The owner still saw the flash. The later 0.4.49 runtime
log counted 79 stale results rejected while the symptom remained, proving the guard runs but does
not solve the visible transition.

The 0.4.48 log then appeared to identify a readiness loss: 98 gains and 28 losses, zero public
false commits, zero range-sweep losses, and 28 owned-without-geometry cases in the same interval.
Inspection found a real semantic error. The “geometry” rule used
`TryIsVanillaDrawing`, which included mutable `CullVisible` and `Hide`, as a continuing
condition. One broad false observation could revoke an established cell even though uploaded
pool geometry still existed.

Version 0.4.49 made this rule gain-only and used `TryHoldsGeometry`; after vanilla first gains
ownership, the geometry guard cannot revoke it. Diagnostics were corrected to count actual pool
geometry rather than cull visibility. Automated checks pinned both statements. The owner again
reported the same visible flash.

The latest 0.4.49 log is decisive against continuing that hypothesis. It confirms 0.4.49 loaded
and the required packed path active. In the first 30-second interval it recorded 158 readiness
gains, **0 readiness losses, 0 range-sweep losses, 0 stale committed cells, 0 owned-without-
geometry cells, 0 mask resyncs, and 0 whole-section owned draws skipped**. It still recorded 2,643
ownership denials for chunks without pool geometry, 15 mask uploads, 1,146 mesh uploads, 558
cluster replacements, and 79 stale result rejections. The owner saw the flicker in this run.
Therefore the mutable geometry loss was a valid cleanup but not the visible regression's
sufficient cause.

Diagnostic A/B evidence also survives:

- `.vhskip off` did nothing, so whole-section skip is not the symptom's required path.
- `.vhmask off` stopped it. This says the mask-enabled ownership composite is necessary, but
  mask-off also leaves cached LOD underneath vanilla and can conceal a missing replacement. It
  does not yet distinguish an incorrect mask texel from absent LOD coverage on the newly exposed
  side.
- Replacing the perpetual snow sweep with finite campaigns did not remove the symptom, so
  continuous one-column-per-second churn is not a sufficient cause.

The next session must capture one exact chunk transition frame-by-frame and distinguish ownership,
GPU mask publication, vanilla pool geometry, LOD selection/residency, parent/child coverage, and
appearance revision before another fix is proposed.

### 8. Validation and artifacts

The final fast tier passed **22,812 assertions with zero failures**. The Release build completed
with zero warnings and zero errors. `git diff --check` reported only the repository's expected
LF-to-CRLF warnings and no whitespace errors.

The verified 32-entry `dist/vintagehorizons_0.4.49.zip` is 484,152 bytes with SHA-256
`6057CBD380DEF8D745EB5650389E38EDC92E5F7B42B86C5C3413273F805855DD`. The copy installed in the
owner's Mods directory matches. The preceding 0.4.48 archive has SHA-256
`903524D7EA963FC5DCFEE7B03BF31EA846D0DBDA348B519D65904CA1003C73E3`. Older test zips were
preserved.

The live source remains uncommitted over base `8ace702`: 51 tracked files changed plus the new
`VintageHorizons/src/Net/LodSnowRefreshScheduler.cs`, approximately 2,634 insertions and 261
deletions before this documentation close. Nothing was pushed, tagged, or publicly released.

---

## Delivered

- Fixed the global player-local snow regression by carrying position-specific snow thresholds in
  the existing climate field.
- Added authoritative loaded-column snow capture for vanilla snow material and snow-covered block
  variants, with generated peeks remaining unknown.
- Added separate per-column observation and coverage maps; propagated them through mips,
  persistence, save snapshots, foreign decode, memory accounting, and mesh snapshots.
- Added append-compatible format-4 snow tails and append-compatible protocol-1 refresh/settings
  fields without changing compatibility numbers.
- Rendered snow and frost as color/material on existing LOD surfaces, with no snow geometry and
  with ordinary seasonal tint preserved underneath. The owner reports the winter look is fine.
- Added `/vhrefresh [radius]`, safe existing-terrain-only recapture, and live durable L0
  replacement delivery to connected network and integrated clients.
- Replaced perpetual one-column-per-second recapture with loaded-chunk dirty capture plus
  30-second weather-triggered finite regional campaigns and dormant-region canaries.
- Added stale mesh-result rejection and corrected the geometry guard to gain-only pool-geometry
  semantics. Both are source-validated but neither fixed the reported handoff flash.
- Passed 22,812 assertions and a zero-warning/error Release build; verified and copy-installed
  the 0.4.49 artifact.
- Updated README, architecture, compatibility ledger, changelog, TODO/DONE, gotchas, status, and
  session records for the full 0.4.40–0.4.49 sequence.
- Documentation validation passed 2,195 of 2,200 checks. The five failures are the pre-existing
  personal Windows paths in ignored `.testdata` water-diagnostic logs; all current documentation
  consistency checks pass.
- Compatibility remains 1/4/6. The assistant launched no game process; nothing was committed,
  pushed, tagged, or publicly released.

## Decisions

- Use actual captured vanilla snow whenever available and position-specific climate estimation
  only for unknown terrain.
- Keep observation provenance separate from the observed covered/bare answer.
- Represent snow and frost with material/color on existing LOD geometry; do not reproduce
  vanilla snow-layer geometry.
- Compose frost over the existing live seasonal system instead of replacing or baking it.
- Preserve generated-peek terrain as snow-unknown; a generated approximation cannot erase a real
  loaded observation.
- Refresh loaded terrain from vanilla dirty events and dormant terrain from finite weather
  campaigns. Do not restore continuous background column cycling.
- Do not add “can accept snow” metadata. It has little exclusion value and would create brittle
  block-policy guesses.
- Transfer only durable changed L0 rows and rebuild parents on the client.
- Keep protocol/blob/schema at 1/4/6 because every wire/storage addition is safely append
  compatible; record the extensions explicitly in the compatibility ledger.
- Treat 0.4.49 as an unaccepted test build. Preserve 0.4.39 as the last accepted pre-snow
  baseline until the terrain handoff regression is fixed and human-verified.

## Traps

- A subsystem described as “spatial climate” can still leave one player-local global input
  behind. Inventory every seasonal input independently.
- Provenance alone cannot answer yes/no state after downsampling. Snow material and areal snow
  coverage have different mip semantics.
- A low constant background rate is not free when it runs forever and the underlying state changes
  only twice in a season.
- Disabling the ownership mask can hide a missing LOD replacement by drawing cache underneath
  vanilla. It proves the composite is involved, not which producer is wrong.
- Equal aggregate counter totals are a clue, not causation. The 0.4.49 runtime eliminated the
  measured readiness-loss class while leaving the visible flash unchanged.
- A semantically valid internal correction is not a product fix until the original visible
  symptom changes in the owner's runtime.

## Flagged and unverified

- **Release blocker:** vanilla-to-LOD terrain still flashes conspicuously in 0.4.49; occasional
  brief holes may share the cause. Exact frame/chunk attribution is not yet captured.
- **Winter temporal convergence:** the owner did not clearly observe an unloaded area accumulate
  and melt in real time. Movement-driven capture mostly matched vanilla, but the full
  snapshot→campaign→persistence→live-offer route needs a controlled human run.
- **Performance:** source bounds and budgets are established; no paired large-world runtime
  capture isolates automatic snow campaign cost.
- **Coverage:** authoritative snow/frost has not been surveyed across the full block/mod set,
  mixed mip boundaries, accumulation/melt, reconnect, dedicated multiplayer, or a second GPU.
- **0.4.48/0.4.49 corrections:** stale result rejection and gain-only pool-geometry gating are
  retained in the worktree because they enforce coherent invariants, but their independent visual
  benefit is unverified and they must not be described as the flicker solution.
- **Worktree state:** all changes are uncommitted over `8ace702`; the next session must preserve
  and inspect that live worktree rather than reapplying a clean baseline over it.
