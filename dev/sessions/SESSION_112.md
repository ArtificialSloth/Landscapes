# Session 112 — Coarse stand-ins over the game's ground: quarter-limited stand-ins and a fast hand-over of fresh chunks

**Date:** `2026-09-29`
**Branch/commit:** `codex/water-depth-light`; `9951c05` (0.4.135)
**Mod version:** `0.4.135`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The owner pointed at his newest screenshot (`2026-09-28_07-15-59`, taken on 0.4.129 while
exploring during the stutter work of Sessions 110-111): rough, coarse LOD terrain drawn over the
game's own terrain for a moment. It is rare, disappears after a short time, and is jarring when it
happens. He asked for a thorough investigation with no changes, then approved fixes 1-3 of the four
proposed, asked that the diagnostic (3) be temporary, and held 4. He asked for this doc pass after
the build. The game was not launched.

That session's log had rotated out (the game keeps five sessions; the oldest retained archive is
2026-09-28 16:58). The evidence was therefore the screenshot, the source, the decompiled engine
(1.22.7), the five retained logs, and a read-only decode of his current world's client LOD cache
(`950c5c5b`: 7,353 L0, 1,950 L1, 537 L2, 164 L3, 53 L4, 20 L5 and 7 L6 sections). The logs had no
direct trace: the once-at-30-seconds `coarse cover waits` line is taken during the join load and
cannot tell new ground from children not yet requested, and the readiness line only confirmed
`near suppression 0 blocks`. This record was written after a context compaction and checked
against the full session transcript.

## Work narrative

### 1. The screenshot

A box of coarse terrain stands above lower real ground. Its faces show several grass, soil and stone
layers stacked on one face, which only the coarse levels draw; level 0 draws one block per column. A
thin floating wall stands near the top of the frame and coarse strips lie close to the player at the
lower left.

### 2. Whole-footprint stand-ins (source-traced)

`CollectDrawNodes` drew a section above its wanted level in place of its children whenever any
visible child lacked a mesh (`AllVisibleChildrenCovered`), and it drew the parent's whole footprint:
settled decision 6's "a parent covers until children are ready", applied to the whole section. A
level-4 section is 1,024 blocks across with 16-block columns, a level-5 one 2,048 with 32-block
columns; the owner's automatic thresholds draw those levels only beyond 4,992 and 9,984 blocks. One
missing child at the edge of loaded ground therefore put coarse columns over all the ground around
the player, including where the other children were ready and the game was drawing (G269).

Brand-new ground makes this routine. `RegisterInTree` adds every ancestor of a newly registered key
to `HasDataSet` so the walk can descend to it, while `AvailableDataSet` holds only keys a store
actually has. Those placeholder ancestors have nothing to draw until mip propagation reaches them,
and on the way each one cost a synchronous store miss on the game tick (`GetOrCreateSection`) and a
background read that came back empty (`EnsureResident`), once per level, in front of the first mesh
for the new ground (G271).

### 3. Fresh chunks were confirmed late (source-traced)

The mask hides cached terrain in a chunk only once the probe has confirmed the game draws it.
`OnReadinessChunkDirty` queues a probe on the engine's `ChunkDirty` event, which `ClientWorldMap`
raises after installing the chunk and before tessellation. The first probe therefore found no
geometry in the pool (G40's geometry gate) and the cell stayed pending until an ordinary sweep came
back to it: the interior sweep covers 32 cells a frame over the 21x14x21 window (6,174 cells), which
is up to about 0.6 s at 340 FPS and 2 s at 100 FPS. That is arithmetic, not a measurement. Meanwhile
the game drew the chunk as soon as its mesh reached the pool, and the cache drew it too.

`dev/TODO.md` already carried this as an accepted "brief fight" on fast approach, with the note to
confirm cells near the camera on a shorter path if it became objectionable. It became jarring only
where the cache was drawing a coarse stand-in rather than fine terrain.

Decompiled 1.22.7: `ChunkTesselatorManager.OnBeforeFrame` runs at Before 0.99 and moves tessellated
chunks into the pool (`AddTesselatedChunk`, then `AddCenterToPools`/`AddEdgeToPools` set the pool
locations). `ClientChunk.queuedForUpload` is true from tessellation until that upload,
`enquedForRedraw` marks a pending re-tessellation, and `quantityDrawn` advances at tessellation
start (G40). Together they say whether a mesh is still coming (G270).

### 4. Measured from the cache (read-only)

- **Decoder sanity:** of 1,086,464 L1 columns with all four children, 842,666 equal their tallest
  child, 243,798 are lower and none is higher.
- **Coarse tops against the real ground,** over 28,573 fully captured chunk columns: L1-L5 never rise
  into a 32-block chunk that is empty in the real world. L6 does in 1.0% (at least 8 blocks 0.3%, at
  least 16 blocks 0.1%, at most 33 blocks), because a 64-block column spans four chunk columns.
  So at L1-L5 a coarse top seen over the game's ground means a cell not yet confirmed (G272).
- **Chunk-edge walls,** 112,356 chunk faces per level: a wall rises above the neighbouring chunk
  column's highest non-empty chunk on 0.3% of faces at L0 (worst 14 blocks), 0.3% at L1 (12), 0.4%
  at L2 (16), 0.4% at L3 (20), 0.6% at L4 (24) and 1.0% at L5 (50). That part of the wall stands in
  the neighbour's air cell, and an air cell never hides anything (G40, G136), so it can show even
  over confirmed ground. The thin floating wall in the screenshot is probably this route.

The three decoders were scratch scripts and are not in the repository.

### 5. Not a regression

Stand-ins have covered their whole footprint for as long as the quadtree walk has existed. The
per-chunk mask became the default in 0.3.17, with the air rule from 0.3.15 (G40). The 48-block near
floor that used to hide the closest cached geometry has defaulted to off since 0.3.139 (`.vhnear 48`
restores it); it would have hidden the lower-left strips, by inspection of the screenshot. Exploring
a lot of new ground during the stutter work is what brought the artifact out.

### 6. The proposal and the owner's choice

1. Confirm fresh chunks within a frame or two, leaving the ownership rules unchanged.
2. Draw a stand-in only over the quarter that is waiting, and skip the placeholder disk reads.
3. Log it automatically.
4. Optional: let empty air directly above ground the game draws hide cached terrain, closing the
   chunk-edge walls. That changes what the game owns, the area where 0.4.38's mask extension caused
   severe terrain holes (settled decision 28), so it would need its own hole test first.

"We can build 1-3. Are we only building 3 to use temporarily? I don't see a need for 3 to be
permanent. Probably hold off on 4."

### 7. 0.4.135: stand-ins cover only the waiting quarters

`UncoveredVisibleChildren` replaces `AllVisibleChildrenCovered` and returns the waiting quarters as
a bitmask. The population is unchanged: an absent child, one out of view at full height and one
with nothing captured still count as covered, and `CountCoarseWait` still counts the rest. A stand-in
with some quarters waiting draws only those and descends into the others
(`CollectChildren(key, skipQuarters)`); with all four waiting it draws whole, as before.

- **Opaque ground** selects packed clusters. Clusters tile a section 4x4 and quads are split at
  cluster edges, so a quarter is exactly 2x2 clusters (`LodStandInPolicy`). A stand-in with no
  selected cluster adds nothing and is not a failure.
- **Water, thin cover, depth and OIT** draw per section, so they take a `vhStandInQuarters` uniform,
  tested in `lodownershiptest.fsh` against the fragment's section-local position. Zero means the whole
  section, which is also what an unset uniform reads, so a pass that never sets it is unchanged.
- **Only while the clustered path draws** (`StandInFix && clusterDrawingThisFrame`). Another opaque
  path cannot honour a quarter, and splitting there would draw the coarse section whole over the fine
  ground its ready quarters also draw. The self-review caught this before packaging.
- `.vhcoarse`'s coarse-draw report names the quarters a stand-in covers.

### 8. 0.4.135: fresh chunks are handed over within a frame or two

`VanillaRenderReadiness` gained a watch list: a ring of cells the engine announced, each with its
announcement time and a flag. `OnReadinessChunkDirty` calls `Watch`; each frame, just before
`PromoteObserved`, `PromoteWatched` puts up to 64 watched cells at the front of the probe queue, for
up to 10 seconds. A watch ends when the probe hands the cell over, when the range rule refuses it,
when the cell leaves the window or expires, or when the geometry gate refuses it and
`VanillaChunkGeometry.TryMeshStillComing` says no mesh is coming (not queued for redraw, not queued
for upload, drawn at least once: tessellated to nothing). A refused cell returns to the ordinary
sweeps. Only the moment of the probe changes; what it decides does not. The two engine fields are
bound like the existing pool fields, and the check tier asserts them against the installed game.

### 9. 0.4.135: placeholders skip the disk

`GetOrCreateSection` asks the store only for keys in `AvailableDataSet`, and `EnsureResident`
answers "nothing to load" at once for a key that is neither resident nor available. This part is
always on.

### 10. `.vhstandin` and the temporary recorder

`.vhstandin on|off` switches the quarter split and the fast hand-over together, is logged when
switched, and is not saved: every session starts on. With no argument it reports the state.

`LodTerrainRenderer.StandInRecorder.cs` is temporary, by the owner's instruction. For each minute in
which anything happened it writes a `stand-in check` line: how many stand-ins covered ground the game
draws, by level, the longest, the nearest, and why the finer piece was missing (new ground, loading,
meshing, waiting for a slot, a failed load, not yet requested); and how many fresh chunks were handed
to the game, half within how long, 95% within how long and the slowest, with any over 30 s counted
apart. A stand-in over the game's ground lasting 0.5 s or more also gets a `stand-in:` line when it
ends, at most 20 a minute. Fresh-chunk waits are recorded with the fix off too, so `.vhstandin off`
still measures the old delay. Lines are written on a background task, as the hitch recorder's are.
To remove it: delete the file and its three call sites (`NoteStandIn`, `EndStandInFrame`,
`RecordFreshChunkConfirmed`).

### 11. Verification

- **Build:** Release, 0 warnings, 0 errors.
- **Fast tier:** 26,156 assertions with 14 failures, all in the all-cores collector check (14 of
  21). It drives the editor against the installed game's start-up file and asserts the file as
  shipped asks for the default collector; the owner switched the collector on at 2026-09-29 07:08.
  Every new and affected suite passed: residency 28, vanilla render readiness 334, packed opaque
  clusters 1,067, stand-in quarters 106, stand-in wiring 20, assets and constants 1,535.
- **New checks:** `ResidencyChecks.PlaceholderAncestorsHaveNothingToLoad`;
  `VanillaReadinessChecks.FreshChunksAreRecheckedUntilHandedOver` and `FreshChunkWatchesEndCleanly`,
  with the watch list added to `SteadyStateAllocation` and the upload-state fields to
  `ChunkGeometryBinding`; `PackedClusterChecks.StandInQuartersSelectClusters`; `StandInChecks`
  (quarter bits follow child keys, the policy, clusters partition a section by quarter at L0, L3 and
  L6) and its static wiring suite.
- **Self-review before packaging** found two defects, both fixed: the split on a non-cluster path
  (section 7), and a recorder detail line formatted later on the background task from a pooled
  episode the render thread could reuse (its values are now copied first).
- **Artifact:** `vintagehorizons_0.4.135.zip`, 758,823 bytes, 45 files, no pdb, SHA-256
  `4A8B2CEE78B3648ED6F6B6A97029CFCBA453396A6904E7D39CA922CC9C0827D6`; copied beside 0.4.130-0.4.134
  with a matching hash; checkpoint `9951c05`. Nothing pushed.
- **Not run in game.** Nothing in this session is human-tested.

---

## Delivered

- **0.4.135** (`9951c05`): quarter-limited stand-ins (clusters for opaque ground, the
  `vhStandInQuarters` uniform for the per-section passes); the fresh-chunk watch list and
  `TryMeshStillComing`; placeholder ancestors skip the store; `.vhstandin`; the temporary stand-in
  recorder; five new check groups. Verified, copy-installed, not played.
- **Documentation:** this record, the index row, changelog, G269-G272, TODO, DONE, status, settled
  decision 6 and a CLAUDE.md routing row.

## Decisions

- **Build 1-3, hold 4** (owner, 2026-09-29). Letting air above owned ground hide cached terrain is a
  terrain-ownership change and waits for evidence that the chunk-edge walls still show.
- **The recorder is temporary** (owner): remove it once his log confirms the fix.
- **The fast hand-over changes when a cell is probed, never what the probe decides.** This is the
  "shorter path" the old TODO asked for, rather than a larger probe budget for every cell.
- **Stand-ins split by child quarter, only on the clustered path.** A quarter is the unit the walk
  already decides by; a finer split has nothing to key on, and a path that cannot honour a quarter
  keeps whole stand-ins.
- **One switch for both behaviours, session-only,** so an A/B in one session compares old and new
  without touching saved settings. The disk-read shortcut is not switchable.

## Traps

- **A parent standing in for one missing child covered its whole footprint.** G269.
- **The engine announces a chunk before it tessellates it,** so the first probe fails. G270.
- **A quadtree placeholder has no stored row.** G271.
- **A coarse top over the game's ground is an unconfirmed cell, except at L6 and on chunk-edge
  walls.** G272.
- **A background-formatted log line must not read a pooled object.** Trigger: a closure queued on a
  task that reads an object the render thread returns to a pool. Failure: the line reports another
  episode's values. Safer action: copy the values into locals before queuing.
- **The all-cores collector check reads the live game file.** Trigger: running the fast tier while
  the owner has the collector on. Failure: 14 of 21 assertions fail with nothing wrong in the code.
  Safer action: read the failures as environmental until the check stops depending on the install.
- **`git commit -F -` with a PowerShell here-string** passed the message to git as a pathspec and
  committed nothing. Write the message to a file and use `-F <file>`.

## Flagged and unverified

Judgement calls awaiting human review:

- **Whether 0.4.135 removes the artifact in play.** The owner's exploring test, with a few minutes of
  `.vhstandin off` for comparison and the client log sent afterwards.
- **Whether the collector check should stop reading the installed game file.** Asked, not answered.

Claims below the evidence level they would need:

- **The 0.6-2 s confirmation delay is arithmetic.** The recorder measures the real wait, on or off.
- **The thin floating wall is attributed to the chunk-edge route, and the lower-left strips to the
  near floor's removal,** by inspection of one screenshot.
- **The chunk-edge wall route is reduced, not closed,** and L6 stand-in tops can still rise into air
  over confirmed ground (1% of chunk columns). Both are what fix 4 would address.
- **The watch list's cost** ("a few tens of microseconds" for 64 rechecks) is an estimate, and its
  started, promoted, expired and refused counters are not written to the log.
