# Doden's Landscapes — architecture

> Tier 1: durable design and settled invariants. Change this document when the design changes, not when a task merely advances.

## Product boundary

Vintage Horizons extends visible terrain beyond Vintage Story's normal chunk view distance by maintaining a persistent level-of-detail cache.

The client must remain useful against an unmodified server. It may only derive terrain from chunk data the server actually sent. An optional server installation can build its own cache, index previously generated terrain, generate transient terrain on request, and offer stored sections to clients.

The mod does not replace the vanilla near-terrain renderer. Its distant terrain currently
keeps a conservative fallback overlap, hands a close radial core back to vanilla as a
playtest stopgap, and fades into fog or the edge of known coverage. Cached geometry no
longer sinks near the transition.

## System flow

```text
client ChunkDirty / server ChunkColumnLoaded / transient generation
    -> column-key deduplication
    -> owning-thread chunk and rain-map snapshot
    -> capture worker: blocks -> vertical RLE runs + loaded-column snow observation
    -> owning-thread palette registration and leaf merge
    -> immutable mip snapshot -> mip worker -> revision-validated owning-thread publication
    -> immutable mesh snapshot -> mesh workers
    -> render-thread GPU upload and draw
    -> 30-second RAM checkpoint -> immutable save snapshots
    -> storage worker serialization/compression -> one SQLite transaction
```

Optional server assist adds:

```text
server cache key manifest
    -> client remote-key skeleton
    -> visibility-driven section requests
    -> bounded dedicated server read-only SQLite reader
    -> ordered owning-thread packet publication
    -> stored compressed section blobs on the client
    -> bounded storage-owner inflation and structural decode
    -> owning-thread block-code resolution, recolor, install, and local persistence
```

Integrated singleplayer sibling-cache discovery adds a dedicated read-only SQLite reader.
It scans new and rewritten rows by `rowid` at a coarse cadence, with a periodic full-key backstop,
computes key deltas on its own thread, and publishes bounded immutable batches for owning-thread
registration. A newer durable level-zero server capture may merge into an older client row without
a reconnect. First sightings and parents fill only missing columns; a changed L0 source may replace
the columns it carries only while the client's submitted content revision is unchanged, and never
erases client-only columns or observed snow. Visibility-driven blob reads use a
second bounded worker and worker-owned read-only connection; the game thread only requests
keys and publishes completed immutable blobs.

Owning-thread section installation is FIFO and bounded by elapsed time and content bytes.
The oldest item is allowed to exceed a ceiling once so an unusually large section cannot
starve itself and every result behind it. Foreign decoder acceptance transfers
responsibility but does not mean installation: deferred world and transport request state
remains in flight until the owning thread publishes or terminally rejects that item.

## Thread ownership

### Client or server owning thread

The owning game thread is the sole mutator of `LodWorld` and live `LodSection` state. It owns:

- Section dictionaries and quadtree key sets.
- Dirty sets and load-in-flight bookkeeping.
- Block registry access and block-code resolution.
- Client palette color and tint classification.
- Publication of completed worker results.

### Capture worker

The capture worker reads chunk references supplied by the owning thread and produces raw
block-id runs. Jobs/results carry a world epoch; the owning thread rejects a result from
a job that completed after world teardown. Publication is bounded at result boundaries
by elapsed time, estimated run bytes, and item count. Queued/in-progress jobs, completed
results, and reload-deferred results share one backpressure limit. Chunk lifetime is controlled by the
engine, so capture tolerates disposal races and records failures rather than mutating game
state.

### Mesh workers

Mesh workers receive immutable section snapshots. They build CPU vertex/index arrays only. They
never call OpenGL or mutate `LodWorld`. Jobs/results carry both the pipeline world epoch and the
renderer texture-mode revision, plus the section content revision and exact render-obligation
revision. Snapshot submission rechecks the render revision, and publication rejects a stale
same-world result while retaining the current live mesh and restoring the exact dirty obligation.
Cross-world and texture-mode identities are rejected before touching new-world in-flight or dirty
bookkeeping.

### Mip worker

The mip worker receives immutable child-section arrays, a copied captured-column mask and palette, the child identity/revision, and a world epoch. It performs boundary collection, sorting, occupancy selection, and merged-run construction. Its output still uses child palette ids. The owning thread rejects stale epochs/revisions, remaps palette ids into the live parent, publishes the quadrant, and only then clears the child's durable `ApplyToParent` obligation.

### Storage worker

The storage worker holds coalesced immutable save snapshots in RAM until the owning
pipeline publishes a checkpoint. It then serializes and compresses the whole batch and
writes its rows in one SQLite transaction. Background loads deserialize block codes
without touching the live block registry; the owning thread resolves those codes before
publication.

The same owner inflates and structurally deserializes compressed sections received from
server assist or the integrated-singleplayer sibling cache. Network and local producers
have separate bounded outstanding allowances. Results carry their world epoch, key,
source, deferred block codes, estimated content bytes, and ready time. The owning thread
rejects cross-world, corrupt, or local-win results, then resolves live ids/flags/tints,
recolours client data, filters skipped runs, and publishes valid sections.

### Local-offer discovery worker

The integrated-singleplayer sibling-cache scanner exclusively owns its read-only SQLite
connection and its discovered-key set. It never touches `LodWorld`; the owning thread
applies each published key batch once. A second below-normal worker exclusively owns the
visibility-driven blob connection, caps outstanding reads, publishes key-tagged byte arrays,
and enforces retry cooldowns. Neither SQLite connection is used by the owning thread.

### Server-assist blob reader

The assist reader exclusively owns one unpooled read-only SQLite connection and prepared
blob command. Its single consumer preserves FIFO request order. Queued, executing, and
completed reads share one bounded outstanding allowance. The server owning thread checks
current player/radius policy before admission, publishes packets under its serving
deadline, and turns a reader failure into an explicit retryable response. Per-player
session identity prevents a completed read from crossing disconnect/reconnect, and an
ordered in-flight batch prevents a later refusal from overtaking an earlier read.

### Render thread

The render thread schedules mesh demand, accepts bounded completed mesh data, owns GPU
resources, traverses the LOD tree, and issues draw calls. Snapshot production is bounded
at job boundaries by 1 ms, 2 MiB of estimated retained arrays, and four jobs. Completed
GPU work is bounded at result boundaries by 2 ms, 4 MiB of live vertex/index data, and
four results. One first item may exceed a ceiling so it cannot starve. The complete new
opaque/water pair is uploaded and published before the previous pair is disposed. Work
here is frame-critical even if the game exposes it separately from game ticks.

## Data model

`LodSection` is a 64×64 grid of vertical columns. Each column contains packed RLE runs. A section-local palette maps run ids to live block ids, untinted colors, material flags, and tint slots.

Each section also holds two 4,096-bit snow maps. `SnowStateObservedBits` says a loaded
client/server/savegame column supplied actual vanilla snow state; transient generated peeks leave
it clear. `SnowCoveredBits` carries the yes/no answer and is meaningful only under that
provenance. Keeping the answer outside the material runs lets thin snow coverage survive mip
downsampling even if the snow block itself does not win a material vote.

Level 0 stores the finest LOD representation. Higher levels double horizontal column size. A parent section receives one quadrant from each child. `ApplyToParent`/`MipDirty` preserves propagation obligations across persistence and restart.

Packed section keys contain level, section X, and section Z. `HasDataSet` includes real rows and synthesized ancestors so the quadtree can descend without loading every section into RAM.

## Persistence

The cache uses SQLite with one row per level/section coordinate. Palette block codes, not numeric block ids, are stored because ids are world- and registry-dependent.

The serialized blob contains:

- Palette codes, colors, and material flags.
- Per-column run counts and packed runs.
- Captured-column bits.
- Optional append-only observed-snow provenance and snow-covered bitsets.

The format-4 snow tails are backward compatible. Old readers ignore data after captured bits.
Current readers treat a missing tail as unknown and reject a partial one. A provenance-only
0.4.43 row is also downgraded to unknown because provenance without its paired yes/no answer
cannot safely assert bare terrain. The authoritative compatibility reasoning is recorded in
`dev/WIRE_HISTORY.md`.

Database schema meaning, blob format, and assist protocol are independent compatibility numbers. Their authoritative history is `dev/WIRE_HISTORY.md`.

Unreadable or semantically outdated cache data may be discarded and rebuilt from future capture. Any such destructive compatibility action must be explicit and measurable.

## Mip propagation

Each group of up to four child columns is merged by vertical boundary slices. A slice survives by majority occupancy, with a reduced threshold at incomplete captured frontiers. Adjacent slices of the same block merge back into a run.

Propagation keeps the child's pending flag set while work is queued or executing. A content revision increments whenever `MarkChanged` accepts a live section mutation. Worker results carry that revision and a world epoch; failed, stale, or cross-world results cannot clear the obligation. Parents are pinned in RAM until their in-flight child results complete. The expensive boundary sweep is off-thread; palette remapping and atomic column-array replacement remain on the owning thread.

## Rendering

The renderer uses a quadtree over section levels. Distance selects the desired detail level. A parent remains as coverage until the required child slots are ready, preventing holes during level transitions.

Renderer resource publication/removal, frame preparation, opaque/water drawing, clear and
disposal cross one path coordinator. The default visible path mirrors CPU-approved opaque terrain
into regional GPU arenas and submits it through indirect multi-draw. Clustered packed geometry is
the required product opaque representation. Missing packed data or any arena, upload, shader or
draw failure suppresses the affected cached opaque draw and reports a required-path failure; the
expanded-indirect and per-section opaque renderers cannot silently substitute. This deliberate
development invariant keeps a broken product path visually obvious. An explicit diagnostic mode
may disable the packed path, but it must name the failure and leave cached opaque terrain absent.
The old per-section `MeshRef` allocation/publication lifecycle remains transitional scaffolding;
it has no visible opaque draw authority and is scheduled for physical removal. Direct OpenGL
probes share exact capture, ordered restoration and verification of every binding they touch.

Opaque and translucent terrain use separate mesh buffers and passes. Seasonal/climate tint data is refreshed from live game color maps and applied in the shader. The camera uses relative section transforms so large world coordinates do not enter mesh vertex data. Cosmetic terrain noise combines section-local vertices with a stable section world origin; it must not sample camera-relative geometry coordinates.

Snow is surface appearance, not LOD geometry. Capture may retain a snow-layer run for backward
compatibility, but current meshing emits no slab, cube, wall, or occlusion cover for it. When an
observed column is snow-covered, the existing highest surface carries the fourth material band;
otherwise the shader uses the snow threshold stored in the climate field at that terrain
position. Frostable tint slots carry a separate material bit, and the shader derives vanilla-like
frost strength from that cell's current temperature, altitude, season temperature, and value
noise before composing it over the ordinary live seasonal tint. Snow therefore does not freeze
or replace the seasonal color system.

Cached water uses dedicated surface and depth programs. OIT is the normal product stage; the
named opaque pipeline is retained only as an explicit dedicated-shader comparison. The legacy
water shader selector is retired. When a requested surface, liquid-depth, junction, repair or OIT
stage is unavailable or fails, cached water and thin cover are suppressed and the required-path
failure is logged. No older water renderer is selected automatically.

Farseer terrain remains opaque fallback scenery, but its ownership seam has a separate
atmospheric volume. The stable 32-block replacement proof emits the exact directed boundary; its
paths are simplified, rounded and resampled into one bounded RGBA field carrying signed lateral
distance, seam-ground height, conservative skip distance and open-end fade. A full-screen
depth-aware raymarch integrates this continuous medium once, so there are no internal sheets,
corner caps or alpha-stacking intersections. Organic billows and rim wobble shape the outside;
a broad width-normalized core guarantees extinction at the exact handoff. The veil draws at
`AfterOIT` order 1.0, after the water capture at 0.99, into a single-color framebuffer attached to
primary RGB. It samples primary depth read-only and never writes primary alpha or depth. Its
explicit single-attachment blend state must not be replaced by the engine's SSAO-aware general
helper (G217-G222, G244-G245).

The settled near handoff is exclusive per-vanilla-render-chunk ownership. Cached terrain
covers uncertain/unready cells, vanilla owns confirmed render-ready cells, fully replaced
cached meshes are skipped before submission, and only mixed frontier meshes pay for a GPU
readiness mask. Per-cell ownership is the default; the measured radial cutoff remains the
explicit fallback. Visibility/ownership remains separate from mesh residency and
persistence; see `dev/plans/PLAN_CHUNK_AWARE_VANILLA_HANDOFF.md`.

Ownership is decided each frame by the preparation at Before/0.994, which has to precede
vanilla's liquid-depth pass at 0.995. The engine moves the local player later in the same stage
(Before/1.0) and fills its frustum culler only after that, so a loss-only late range check at
Before/1.001 re-runs the exact range sweep whenever the physics step changed the player's block
and publishes the ground texels before anything draws. Water's footprint follows at the next
preparation, so every water pass in a frame reads the same footprint.

Opaque terrain faces use outward counter-clockwise winding and render with GPU back-face
culling enabled. Water and thin/cutout surfaces stay two-sided. Selected opaque sections are
then submitted front-to-back from a reusable allocation-free ordering list so nearer depth
can reject farther fragments; the translucent water pass retains traversal order. The whole
cached pass runs at opaque order 0.38, immediately after vanilla terrain at 0.37, so current
chunks populate depth before cached fragments hidden behind them reach the shader. The
renderer can be re-registered at its old 0.36 order with `.dlocclusion off`; re-registration
is required because the engine sorts a renderer only when it is registered.

Post-vanilla ordering supplies the depth used by delayed exact-geometry occlusion. Selected
ordinary opaque draws are wrapped in asynchronous `AnySamplesPassed` queries; results are
polled only after the driver reports them available, so the render thread never waits for a
same-frame answer. A zero-sample result suppresses later mesh submissions. Hidden sections
periodically draw their real geometry again, making one operation both the visibility probe
and the correct terrain draw for that frame. Water is not queried.

OpenGL query commands cannot leave the render context, so their remaining owning-thread
work is explicitly bounded: at most eight new queries and sixteen result-availability checks
per frame. GPU mesh eviction likewise uses a rolling four-key queue instead of periodic
full dictionary sweeps. CPU section eviction examines two rolling resident keys per game
tick. Both policies trade slightly later reclamation for the removal of collection-sized
frame/tick bursts.

Seasonal/climate sampling uses game APIs and therefore also remains on the owning thread.
Stable published slots and the 128×128 field refresh no more than once every 30 seconds. The field
stores raw temperature, rainfall, spread distance, and a quantized current snow threshold at one
64-block cell per level-zero section edge; one existing texture sample serves both biome tint and
snow. Newly arrived authoritative snow coverage coalesces an earlier field refresh after a
one-second debounce rather than waiting for the next calendar interval. A tint slot registered by
a later cache load bypasses the cadence immediately; sampling still advances one slot or field
row per frame into staging arrays and publishes complete tables/field atomically. Mesh scheduling
retains a section's exact dirty obligation while any tint slot in its palette is unpublished, so
geometry is not first revealed with identity-white tint and recoloured later. Untinted sections
do not wait, and existing parent coverage remains live during a refined replacement's appearance
preparation.

The saved blend-pipeline master controls both seam resources and packed-L0 block textures. Off
gates owning-thread atlas resolution and omits texture-slot arrays from immutable mesh snapshots;
live L0 meshes move through the ordinary bounded remesh path to plain colour. On repairs resident
L0 palettes incrementally before textured publication resumes. The block lookup table is uploaded
to a replacement GPU object and swapped only on success, while a previous table remains available
until old slot-bearing meshes retire. World teardown clears both registry and GPU table state.

Visibility state is fail-open and remains independent from residency and persistence. A
projection change or profile-specific camera threshold advances a scene epoch, and results
from an older epoch are consumed but cannot hide anything. Streamed mesh replacement
invalidates only the replaced section; chunk-dirty events and readiness-mask uploads do not
globally erase unrelated results. This distinction is required for convergence during active
exploration. Query allocation/driver failure disables the optimization and draws everything.

The default `aggressive` policy retains results through rotation, invalidates after two
blocks of translation, checks visible sections every eight frames, hidden sections every
sixteen frames, and shortens hidden probes to four frames while turning. A section with mixed
vanilla/cache ownership always draws, protecting the handoff seam from a whole-section
answer. While turning, a narrow normalized band at the horizontal frustum edges also draws,
protecting newly revealed terrain without sacrificing central or stationary culling.
`safe` invalidates on small camera changes; `extreme` retains results through all camera
motion and deliberately removes the edge guard. `.dltemporal` and
`.dltemporalprofile` change these session-only controls; `.dlinfo` exposes their counters.

The earlier same-frame bounding-box experiment remains a recorded rejection, not a
contradiction: 83% hidden proxy boxes produced no FPS gain because proxy raster, same-frame
dependency and retained submission cost replaced the saved work, and it ran before the
nearby vanilla occluder existed in depth. The accepted delayed path queries the real draw
after that depth exists and skips later CPU/GPU submissions.

The renderer maintains a horizontal world-space rectangle over all opaque and water mesh
keys. Additions expand it in constant time; removing an extreme marks it for one rebuild
from the surviving keys. Ordinary frames derive the farthest required distance from the
rectangle without scanning resident meshes. The camera far plane rounds upward in
512-block steps, grows immediately, and shrinks only after a lower step remains stable for
five seconds. The shader's effective far edge remains continuous, and `.dlfar` remains an
explicit culling/far-edge cap rather than a different residency policy.

Render-dirty membership is exact owning-thread state, while scheduling uses a separate
nearest-first priority index. New dirty keys enter incrementally. Existing priorities
rebuild only after the camera crosses a 256-block cell, detail distance changes, or the
world is cleared. A dequeued heap entry must still exist in the exact set, and a key with
mesh or load work already in flight is restored rather than losing its dirty obligation.
The scheduler examines only a finite prefix sized to include the bounded in-flight work.

Mesh jobs estimate every array they retain, including immutable run and column arrays
shared with live sections. Completed results estimate only live vertex/index counts, not
pooled backing capacity. Telemetry records scheduled/uploaded items and bytes, pending
bytes and oldest age, direct GL upload calls, and resource disposal separately. The
frame-local budget state is a value type so enforcing the limits does not create steady
render-thread heap traffic.

Visibility, residency, and persistence are different concerns:

- Visibility decides what is traversed and drawn now.
- Residency decides which CPU/GPU resources remain available.
- Persistence decides whether a section can be reconstructed after eviction.

They must not share one timestamp or state flag if doing so makes turning the camera trigger remesh storms.

Persisted mesh demand is likewise independent of draw visibility. Exact loadable/offered rows are
tracked separately from structural quadtree ancestors. Two bounded eight-lane planners split one
fixed allowance: an inner foundation advances the first configured band through L0 even while
vanilla suppresses it, while the outward wave pipelines nearer refinement with the next coarse
band. The foundation receives 24 of 32 unresolved slots and six of eight new requests per frame;
the outward reservation retains at least one unresolved slot per lane, so prioritizing near
sharpness cannot stop panoramic progress. Camera orientation is not an input. Both feed exact render-dirty ownership before the
empty-mesh return and never bypass asynchronous loads, snapshot/upload budgets, or parent coverage.

Persistence has its own monotonically increasing per-section revision, separate from the
content revision used by mip jobs. This distinction is required because clearing the
persisted `ApplyToParent` flag changes a row without changing terrain content. Snapshot
enqueue reserves a revision but leaves `SaveDirty` set. Normal mutations stay solely in
authoritative RAM until a 30-second checkpoint begins. The owning thread freezes a bounded
oldest-first batch—up to eight snapshots per pump, subject to its elapsed-time/byte admission
budget—because live section and registry state cannot be read safely by the worker; serialization,
compression, and I/O do not run there. A drained checkpoint can be followed immediately when more
work is ready, while an idle pipeline retains the coarse cadence. One checkpoint admits at most
256 distinct dirty keys. The storage owner
coalesces newer pending snapshots for the same key and publishes the completed checkpoint
as one SQLite transaction. Additional dirty keys remain in RAM for the next checkpoint.

Every executed snapshot receives a success/failure acknowledgement. Only a successful
acknowledgement matching the current persistence revision clears dirty state; stale success
and all failure paths retain it. Failures retry with bounded exponential delay. A successful
local write retires a foreign fallback only after that acknowledgement. A client and an
integrated-server cache are separate active pipelines and therefore each owns its own
checkpoint transaction when both are capturing.

Shutdown applies the same protocol rather than using a separate best-effort path: drain
accepted writes, publish their acknowledgements, queue remaining dirty revisions, and
repeat until clean or the fixed deadline. A timed-out close reports every unresolved key
and revision before world state is cleared. Persistence revisions are runtime identity,
not part of the SQLite schema, section blob, or assist protocol.

## Optional server behavior

Server capture and serving are additive. Players without the mod remain unaffected. A first server
offer or a parent merge only fills missing columns. A newer durable server L0 may replace the
columns it carries only if the client's content revision has not advanced since submission; later
client observation wins that race, and remote data never erases client-only columns or observed
snow.

Savegame sweeping may load only terrain already known to exist with a safe generated neighborhood.
Transient generation uses `PeekChunkColumn` and must not write generated terrain to the savegame.
Runtime absence verification measures this promise, and reports inconclusive when another generator
was already active.

`/dlworldgen` is the sole deliberate terrain-writing exception. After a permitted player prepares
and confirms the permanent operation, it requests real chunk columns with `keepLoaded: true`, works
in 16×16-chunk tiles, and records durable progress. A circle is the default: the player's tile is
first, tiles advance through clockwise circular radial bands, and columns are nearest-first within
each tile. Engine-queue, capture, persistence, server-load and disk-space gates bound the job. It
can pause, resume and stop; an interrupted plan is offered after restart and never resumes silently.
Kept leases are released through the validated force-loaded-list seam only after the job's exact
capture/durability obligation is safe. A server-cache row alone is not completion provenance,
because transient peeks populate the same cache.

After the sweep has built its generated-column map, dormant snow refresh is change-driven rather
than a perpetual terrain scan. Loaded chunks take the exact path through vanilla's chunk-dirty
event. Every 30 seconds the server reads public regional snow-accumulation snapshots, reduces
corner deltas to accumulation/melt band crossings, and starts a finite campaign over eligible
existing columns. One dormant map-region canary is loaded per poll so unloaded weather history
eventually becomes visible. Campaign work is rate-limited by `RefreshColumnsPerSecond` (default
1, clamped 0–16), shares the sweep's 1 ms owning-thread issue budget, and cannot generate an
absent column. There is no “can accept snow” classification: unknown, wholly bare, wholly covered,
and mixed observed columns already provide the safe directional filter.

The server must answer every accepted section request, including explicit refusal. Silence strands the client's in-flight slot indefinitely.

Persisted changed level-zero rows are offered live to connected clients with the
append-compatible refresh flag. Only L0 is transferred; each client rebuilds its own parents.
A replacement may overwrite an older remote/local row only if the submitted content revision is
still current, so a later client capture remains authoritative. The remote-update route remains
live until the exact replacement persistence revision is acknowledged.

## Settled decisions register

1. **Vanilla-server compatibility is a product requirement.** The client pipeline cannot depend on server installation.
2. **Optional server assist is additive.** It may improve coverage but never become required for ordinary client capture.
3. **Block codes are persisted; block ids are resolved live.** Numeric registry ids are not stable storage identity.
4. **The block registry remains on the owning thread.** Some registry reads lazily mutate internal engine state.
5. **OpenGL work remains on the render thread.** Workers produce arrays, not GPU objects.
6. **A parent covers until children are ready.** Detail transitions may be temporarily coarse but must not open holes. Since 0.4.135 it covers only the quarters whose children are waiting and descends into the rest whenever the clustered path, which can draw a quarter exactly, is drawing; any other opaque path and `.dlstandin off` keep the whole-footprint cover (G269).
7. **Local observed data wins over remote offered data.** A remote source cannot overwrite newer client truth.
8. **Sweeping must not generate terrain.** Frontier columns without a complete known neighborhood are skipped.
9. **Transient generation must not persist world terrain.** The LOD cache is the only intended output.
10. ~~**Competing LOD mods cause this renderer to defer.**~~ **Superseded by 55 (2026-10-10):** the mod never steps aside; it names the other mod and the player keeps one.
11. **Performance work is evidence-driven.** Count budgets are not accepted as frame budgets without elapsed-time measurements.
12. **Owning-thread install drains are FIFO, time/byte bounded, and progress-guaranteed.** One oversized oldest item may exceed a tick's ceiling; later work waits.
13. **Foreign decode acceptance is not publication.** Request responsibility and source
    fallback survive background decode until the owning thread installs or terminally
    rejects the exact result. Local observed data still wins the race.
14. **Capture publication is boundary-budgeted.** The owning thread stops after its
    elapsed-time, raw-run-byte, or item ceiling. One admitted result remains atomic and
    may exceed a ceiling; later results wait under combined job/result backpressure.
15. **Render-dirty membership and priority are separate.** Exact membership owns the
    obligation; a coarse-cell-refreshed heap orders available work. Stale or temporarily
    blocked heap entries cannot clear the exact dirty state.
16. **Mesh production and upload are boundary-budgeted.** Time, retained/uploaded bytes,
    and item caps all apply. One atomic first item may exceed a ceiling; later work waits.
    A replacement becomes live before the previous GPU resources are disposed.
17. **Save enqueue is not durability.** `SaveDirty` remains the exact reconstruction
    obligation until the storage owner acknowledges the current persistence revision.
    Failures retry, superseded pending snapshots coalesce, and close either reaches clean
    state or reports exact unresolved revisions.
18. **Near terrain has one renderer owner per vanilla chunk.** The intended handoff keeps
    uncertain cells cache-owned, transfers confirmed cells exclusively to vanilla, skips
    wholly replaced cached meshes, and masks only mixed frontier meshes. Ownership does
    not itself evict resident cache resources.
19. **Opaque cached terrain rejects back faces and submits nearest first.** Outward CCW
    winding makes culling safe for all six solid face directions; water and thin surfaces
    remain two-sided. Front-to-back ordering affects opaque submission only and reuses its
    storage rather than allocating per frame.
20. **Ordinary persistence is a coarse RAM checkpoint, not a mutation stream.** Each active
    pipeline starts no more than one checkpoint every 30 seconds, freezes no more than one
    section per tick, caps the retained batch, and commits it in one storage-thread SQLite
    transaction. Shutdown remains the explicit immediate durability boundary.
21. **Collection maintenance is rolling where reclamation may lag safely.** Mesh eviction
    checks four keys per frame and CPU section eviction checks two per tick. Seasonal tint
    tables refresh one slot per frame on a 30-second cadence. Full periodic sweeps are not
    reintroduced merely for exact reclamation timing.

22. **Cached terrain carries no per-position light, and water is the one exception.** One
    sky-exposed level from the world's own ramp multiplies every cached surface; nothing in
    the capture format stores a light value. Submerged ground is shaded anyway, because water
    absorbs one light level per block and the depth is arithmetic on water runs the cache
    already holds - so it needs no capture change and no re-exploration. The factor is baked
    into the stored colour at mesh time, which is exact rather than an approximation:
    `SunBrightness` is the world's MAXIMUM sun level rather than the clock, and with no block
    light vanilla's own light maths is linear in that level. Overhangs, cave mouths and
    foliage remain unshaded; only water is derivable this way.
23. **A seam can only land on a 32-block vanilla chunk plane**, because ownership is decided
    per chunk. That makes it a STATIC, section-local property available when a mesh is built,
    unlike the blend band, which is a distance field rebuilt per frame from vanilla's current
    footprint and moves with the player. Mesh detail that only matters at a seam is therefore
    confined to that grid rather than paid for everywhere or gated on the band: cached water
    keeps one quad per block within a block of it so the surface can carry the engine's own
    liquid warp, which measured +0.99% of the mesh against +16.32% for doing it everywhere.
24. **Water is not shaded by its normal, because vanilla's is not.** `chunkliquid.fsh` has no
    normal term at all. This is invisible while cached water is a flat plane and becomes a
    bright crease along every quad boundary the moment the surface is displaced, so the two
    must stay in step: any future change that tilts cached water must leave its shading alone.
25. **Vanilla's shoreline foam is a shallow-water term and must not be applied to open water.**
    The engine reads its depth buffer, caps the reading and multiplies by 1500, so anything
    more than a block or two deep drives the term to exactly zero. A shader with no depth
    buffer to read has to supply the DEEP answer, not the shallow one; substituting zero depth
    pins the effect at maximum and adds roughly a third of the water's own brightness to every
    lake and sea.
26. **Required renderer paths fail visibly during development.** Clustered packed terrain and
    dedicated water suppress cached output and report a named required-path failure when their
    selected resources or stages are unavailable. Expanded-indirect, per-section opaque and
    legacy water rendering cannot activate automatically. An explicit diagnostic comparison
    must say which path it selects.
27. **A missing vanilla neighbour is not an ocean shoreline.** The liquid tessellator may emit
    a vertical side against a substitute empty chunk when real neighbour data is absent. Only an
    outward horizontal liquid face at such a genuinely missing client chunk is suppressed. A
    loaded empty neighbour, top/bottom face or interior liquid face remains engine-owned.
28. **Water continuity must not redefine terrain ownership.** A same-frame water correction may
    use immutable vanilla images inside the dedicated water stage, but water development does not
    add weaker facts to the shared readiness mask or change ground ownership, publication or
    stabilization semantics. Version 0.4.38 crossed this boundary and produced severe terrain and
    seabed holes; 0.4.39 completely restores the 0.4.37 terrain-safe state. Any future terrain
    ownership change is separate work requiring explicit owner authorization and terrain-first
    runtime evidence.
29. **Observed snow provenance and snow coverage are separate facts.** Generated peeks remain
    unknown; loaded captures may assert bare or covered. Both facts persist and propagate, and
    a coarse parent may assert either only when all contributing captured children were observed.
30. **Snow changes surface material, not topology.** Snow layers do not add LOD geometry. Actual
    observed coverage marks the existing top face; unknown terrain uses a spatial climate
    threshold. Frost composes over live seasonal tint.
31. **Dormant snow refresh follows weather transitions.** Loaded chunks recapture from dirty
    events; unloaded regions use 30-second snapshot signals, finite rate-limited campaigns and
    sparse canaries. Continuous full-world column cycling is intentionally absent.
32. **Live server refresh transfers durable L0 only.** Connected clients may replace older rows
    after a refresh notification, but local capture wins revision races and mip ancestors rebuild
    locally. Since 0.4.126 a syncing client learns of those rows from versioned spans (decision 52).
33. **Range ownership is decided from the player block the engine will draw with.** An ownership
    decision that predicts vanilla's range clause must use the block position the culler is
    given, which exists only after the player's physics step at Before/1.0. The preparation stays
    at 0.994 for the water passes; the late range check at 1.001 is loss-only, goes through the
    ordinary publication handshake, and defers water's footprint by one frame (G170).
34. **Distant anti-aliasing belongs to the pixels cached terrain drew.** A depth snapshot taken
    before the first cached draw decides which pixels cached terrain drew (nearer than the
    snapshot). Only those, and sky pixels beside them, are nudged, blended and written back;
    vanilla terrain, creatures and water keep the pixels they were drawn with. Strength follows
    on-screen block size, from the live projection and render height against the player's reach,
    never a distance in blocks, so it holds at any vanilla view distance, FOV or render scale.
    History reaches full share before the nudge begins, is re-sampled by Catmull-Rom, and settles
    fully only once the whole camera holds still; sharpening applies to the output, never to the
    history (G175).
35. **Automatic LOD thresholds follow the screen.** L1 is where a level-0 column reaches the target
    on-screen width (1.5 px by default) and each later threshold doubles it, so every level hands
    over at the same size on screen. Level 0 is held past the vanilla view distance, the blend band
    and one section, because band textures exist only on level-0 quads. The player's own
    thresholds stay in the file for when automatic is off.
36. **A block is recorded as what it shows.** A block whose look lives in its block entity has no
    type-level colour, so capture reads the entity where the job is created and records a chisel as
    its most visible material: the eligible material with the most exposed voxel faces, summed per
    block, ties to the first. Server and client then store the same ordinary block, and nothing on
    disk or on the wire changes shape. The chisel's own flags still decide the column's snow. A
    block-entity block that reaches a type-level colour takes another of its own textures or neutral
    grey, never the missing-texture image; a plain block whose own answer is that image (a removed
    mod's block) keeps it, matching what the game draws up close (G180-G182).
37. **Settings has one editor and each setting keeps its real owner.** The guarded in-game
    **Horizons** tab and `.dlconfig` compose the same editor; the command remains the fallback if
    Vintage Story's private Settings header seam moves. The Vanilla distance marker writes the
    actual game setting with the game's range, step, mouse-up commit and Custom-preset side effect,
    so mod Cancel never rolls it back. Its neutral visual band ends before the coloured L0 band.
    The saved pause-loading choice reuses the same bounded owner-thread LOD work from a render stage
    at 50 ms cadence; it never unpauses or advances simulation (G184-G186).
38. **Real terrain generation is explicit and permanent.** Savegame sweeping, `/dlgen` and every
    automatic frontier path retain the generates-nothing promise. `/dlworldgen` alone may write
    new terrain, only after a player with `controlserver` privilege prepares and confirms the area.
    Its durable plan, kept leases and job-owned persistence obligations make pause, resume, stop and
    restart recovery bounded and visible; no interrupted job resumes silently (G193-G197).
39. **The frontier yields; explicit jobs own generation authority.** A future travelling frontier
    may borrow otherwise idle generation capacity but never holds the exclusive authority merely
    because it is enabled. It yields immediately to `/dlworldgen` and `/dlgen`; simultaneous
    producers cannot make each other's provenance, completion or absence diagnostics ambiguous.
40. **Silhouette height truth is ordered, and accelerators are optional.** Captured terrain wins,
    then the engine's own Terrain-pass preview, then an accepted accelerator. Horizons prefers
    Watersheds' native sampler when both supported samplers are installed and accepts Algernon's
    Terrain Sampler when Watersheds is absent; neither is a dependency. Accelerator work stays off
    the main thread, and one named failure disables that adapter for the session and returns to
    previews. Representative shape is the product requirement; it is not voxel-LOD truth.
41. **Silhouette delivery is demand- and session-owned.** A current peer offers tile revisions and
    answers bounded nearest-first requests; every accepted key ends as success, explicit refusal or
    bounded retry. Transport generation and world-session identity are append-only protocol-1
    fields, so a stale world cannot release a current request slot. An older peer's direct-push
    packets remain a deliberate compatibility path. Dedicated servers offer the feature only after
    explicit admin opt-in and clamp reach; integrated/LAN servers retain the automatic default.
42. **Ready real terrain owns the Farseer join; only the unresolved edge slopes.** Farseer is a
    separate fallback height field, never section-cache data and never terrain ownership. It may
    not remain visible over ready vanilla or LOD terrain, including below excavated vanilla ground,
    but it remains until live replacement coverage is proven so a pending mesh cannot expose sky.
    At an unresolved frontier, an adjustable buried apron slopes only the near Farseer edge down;
    the distant field keeps its representative height. A global sink, visible join bands, exact
    coplanar contact and a separately visible skirt are rejected designs. The cached fade and
    Farseer pass retain the shared classic-colour handoff, and the existing stepped far-plane policy
    owns both extents.
43. **A silhouette off switch removes the feature's live cost.** It sends one final off demand,
    then clears client offers, requests, tile data, worker obligations and GPU meshes; its render
    pass returns without GL work, its shader is not compiled merely because the saved switch is on,
    and its extent contributes nothing to projection. The server drops that player's demand and is
    idle when nobody asks. Stored server tiles remain reusable. Terrain Ahead is an independent,
    later feature with a separate switch and acceptance.
44. **Farseer is intentionally coarse and incrementally regenerated.** Representative rolling
    shape, not terrain detail, is the requirement. A tile stores 16/8/4-quad meshes and draws one;
    outline detail chooses by projected size, while Hill softness changes broad worker-only
    feathering. Decode and mesh work stay capped off-thread, owner-thread snapshots/publication and
    uploads stay bounded, shape changes debounce and roll through dirty tiles, and reach changes
    retain the intersection instead of clearing the disc. The client tile store is lazy and
    off-owned: it opens only for an enabled offered feature and closes with live state on off/world
    teardown.
45. **Farseer suppression is proven by the exact installed mesh, at every LOD level.** Each L0-L6
    mesh result carries a compact 32-block mask derived from the opaque top surfaces the worker
    actually emitted, and that immutable mask travels through publication with the mesh identity.
    Suppression selects a distance-appropriate resident target from camera position, then a
    coarser resident parent, then finer resident descendants. Cached data, requested work, mutable
    section contents, current-frame draw selection, frustum state and camera yaw are not ownership
    proof. Unknown or partial cells fail open and keep Farseer (G212-G215).
46. **Farseer ridge contrast is world-directed and geometry-free.** The fragment shader
    reconstructs the coarse surface's geometric normal, shades it from a world/sun direction and
    adds gentle world-distance depth separation. Contrast may not depend on camera yaw, and it may
    not add terrain detail merely to distinguish overlapping hills (G216).
47. **The Farseer/LOD seam veil is derived from ownership but never changes it.** The exact stable
    32-block replacement answer emits a unique directed boundary. Maximal paths become a centreline
    by a fixed-window moving average of the staircase, corner-cut once and densely resampled; only
    true open ends fade longitudinally. The fog spills visually across both owners, with a wider
    shoulder over Farseer, but never suppresses more terrain, invents a boundary beside a missing
    tile or dilates ownership. Surface paint, hard outer walls and ownership dilation are rejected
    designs (G217, G221-G222).
48. **Seam fog is a post-water RGB overlay with read-only depth.** It draws at `AfterOIT` order
    1.0, after the water capture at 0.99, so water cannot repaint the seam over it. It marches at
    half resolution into its own two-target framebuffer (premultiplied fog, and each ray's length)
    with blending off, then a depth-aware upsample composites into a framebuffer that owns only the
    primary `ColorAttachment0`, with premultiplied blending configured explicitly. Both passes
    sample primary depth read-only; the composite writes RGB only and preserves primary alpha/depth.
    A shader/draw/GL failure disables Farseer for the session rather than continuing with a
    plausible no-fog fallback. Fog work may not change water shaders, water depth/revealage, water
    ownership or the shared terrain readiness mask (G218-G220, G250).
49. **Seam fog is one continuous field and one integrated medium, not layered geometry.** An
    owning-thread snapshot copies the traced paths and all live seam-height inputs. One bounded
    worker rasterizes them, runs a separable squared Euclidean distance transform, measures how far
    the real staircase strays from the centreline, and converts the field to half floats; results
    carry a revision, stale work cannot publish, and only the render thread uploads the `RGBA16F`
    field and the baked, mipmapped 3D noise texture. The field is eight blocks per texel unless the
    reach needs more, bounded at 2,048x2,048. Farseer waits for the first valid field, while an
    established field may remain during replacement. Card/sheet intersections, corner fans and
    horizontal slices are rejected because source-over accumulation exposes them as hard gradation
    lines (G244). The medium has two parts with separate densities (0.4.125). A **seam strip** a few
    blocks deep hides the seam on its own: its vertical column is 3.5 optical depth over a band of
    the measured stray plus a small margin, noise varies but never opens it there, and past the band
    it frays out in lobes (G245, G247). It rests on the surface each ray lands on near its landing
    point, clamped to the sampled seam height, and march steps may not jump it (G252-G253). The
    **bank** is scenery: over a core of the measured stray plus a margin it does not thin, beyond it
    it fades exponentially, coverage erosion turns its rim into strands, and a faint haze continues
    the fade. It is quoted along a level crossing, not its vertical column, so it stays see-through
    from the ground (G251). A dense body filling the bank's full height is rejected as a painted wall
    (G246). Sunlight with one probe toward the sun shades both below the horizon fog colour. Steps are
    optically thin in dense fog, grow with distance in thin fog, and are sampled at their middle
    (G249). `Fog quality` controls march precision; the old persisted `FogLayerDetail` name remains
    only for settings compatibility.
50. **The seam fog's outline is a function of the terrain, not of the view.** It is built from every
    Farseer tile within reach (or, without Farseer, every tile of decision 57's disc), whichever way
    the camera faces; each tile caches its replacement
    coverage, refreshed as it draws or a few off-screen tiles per frame. Every point of the
    centreline depends only on the staircase near it, and the field's texel size is fixed by the
    reach. The frustum, camera yaw and the momentary extent of the tile window may not move it; the
    only legitimate movers are real ownership changes and the billows' drift (G248; the fog-side
    counterpart of decision 45).
51. **Fog left behind by a moved seam dissolves; fog over a new seam appears at once.** A field is
    never simply replaced. When a build begins, the render thread snapshots the shown field and any
    residual still fading, and the same worker carries them onto the new field's lattice as a
    residual. Its weight is the old fog's open-end fade plus a head start for the build's own time,
    or what an earlier residual had left, and it is zero wherever the new field is unchanged. The
    shader subtracts the fade durations elapsed since the build began, applies the weight to the old
    bank's envelope and, squared, to its strip, and shows the thicker of old and new. A second
    update arriving mid-fade carries the fade on instead of cutting it (G254). Fields on different
    lattices, which only a reach change produces, do not blend. The new seam is never left
    uncovered while old fog fades.
52. **A returning client is owed exactly what its server's cache changed since it last caught up.**
    Every server-cache row carries a version that only grows, committed with the counter in the same
    transaction, and the cache has a generation that changes whenever its versions stop being one
    ordered lineage (a format purge, or a write by a build without versions). A client records the
    generation and version it caught up to, plus the held sections announced but not yet merged and
    saved, and writes a span's debts with its version in one transaction. The server answers a join
    with every level-0 section written since that version, then keeps each player's live spans
    contiguous: joins and broadcasts share one FIFO and are applied in that order. A client with no
    record, or one for another generation, re-checks every section it holds, newest winning (owner,
    2026-09-22). This was chosen over keeping what it has; the re-check is resumable, so it happens
    once. A debt settles only when a copy at least as new as announced is durable (G16, G7); a merge
    frees its fetch slot at once (G256). Owed sections are fetched nearest first, inside the serving
    radius. Integrated play does not take part, because the sibling cache's rowids already say what
    changed.
53. **The mod rests the game's chunk thread, and only where the engine would spin** (owner,
    2026-09-28). With one world-generation thread on 8+ logical processors the engine re-scans
    parked neighbour requests back to back while players explore (G261). A postfix on the queue scan
    sleeps the thread for the engine's own `ServerThread.SleepMs` after a pass that found nothing and
    only while the engine's interval is negative; a pass with work never rests, and generation order
    and output are untouched. It is on by default where this mod's server side runs, switchable per
    session (`/dlchunkrest`) and removable by config (`RestIdleChunkThread`). Enabling a second
    generation thread also stops the spin but changes terrain, and was rejected (Session 91). The
    hook sits on the scan, not on the interval getter, because the getter can be inlined (G262).
54. **The all-cores garbage collector is offered by editing the game's own start-up file, opt-in**
    (owner, 2026-09-28). The collector is fixed when the process starts, so a running mod cannot
    choose it; `Vintagestory.runtimeconfig.json` beside the game is the only place that sets it for
    every later start without a launcher. The Performance tab's switch, off by default, adds
    `System.GC.Server` with a marker property naming this mod, keeps a one-time backup, and switching
    off removes only what the mod added; a collector property set by anyone else is never touched.
    Each client start keeps the file in step with the setting, because a game update replaces the
    file. A file that cannot be read or written leaves the setting unchanged and says why. Only the
    adaptive Server GC is offered: a larger gen0 budget and a fixed heap count both lengthen pauses
    (G265).
55. **This mod never steps aside for another LOD mod, and keeps no switch to** (owner,
    2026-10-10; supersedes 10). Two LOD mods drawing at once still fight over the far plane, but the
    player manages the mod list: a known LOD mod that is switched on (`OtherLodMods.Known`) is named
    in the log and in a login notice offering only OK; without one, a general reminder can be
    dismissed (`HideLodModNotice`), which never hides a named conflict. A mod whose own switch reads
    off does not count; "cannot tell" counts as on. Detection is the known list only: scanning other
    mods' code was rejected by the owner.
56. **Mod shaders are first built on the engine's shader reload, never at mod start** (2026-10-13,
    G274). Every failure reports at full volume; nothing is excused as a provisional attempt. A
    first-frame fallback covers a session the reload never reached.
57. **The seam fog does not need Farseer** (owner, 2026-10-14; 0.4.146). Its outline is the cached
    terrain's ownership edge, which Farseer's tiles only framed. With Farseer off, or on with no tile,
    the fog traces that edge over every 512-block tile in a disc to 0.75 of the cached far distance
    (where cached terrain fades into the sky); a cell outside the disc is no owner, so the rim never
    draws fog; heights are the highest cached surface at each 32-block corner, with no globe drop.
    Farseer's tiles take over the moment they arrive. Farseer off frees only Farseer's meshes; the fog
    switch off frees the fog. Its colour is the game's natural fog colour (owner, 2026-10-16), with
    the player-local darkening taken in part since decision 59. Loading Farseer's tiles only to shape
    the fog was written and rejected for this.
58. **Without Farseer, mist hangs down the cached terrain's wall as a veil** (owner, 2026-10-18;
    0.4.149-0.4.156). Past the cached edge stands a wall from the lip to y = 1, every column's foot.
    For a field built from that edge only (`fogCurtainActive`), the fog hangs a curtain over it: a
    sheet from just behind the outermost stretch of wall outward, dense in falling streams and nearly
    clear between them, so the wall shows dimly (owner: less coverage looks more real; it is never
    hidden whole). Beside empty sky the bank's floor curves down past the band so the fog rolls over
    the lip, and the dense strip ends there. The curtain never rides the wind: it crosses the top
    slowly along a path in the edge's own frame, falls at the drift dial's pace, is bent by a slower
    unseen eddy field so its shapes change as they fall, and ends in tendrils 48 blocks below the foot.
    Farseer's ground hides the wall, so Farseer's fog has no curtain and is unchanged. Rejected: a
    cross-faded flow map, the nearest edge point as place everywhere, a bent dense strip, and tendrils
    cut from the flowing pattern (G281-G283).
59. **The seam fog takes the player's own weather and cave darkness only in part** (owner,
    2026-10-18; 0.4.152). The game's fog colour includes modifiers describing only the player's spot
    (`weather`, `desertstorm`, `lightningambient`, `blackfogincaves`; G284), which turned the whole
    horizon bank grey. `LodSteadyFogColour` repeats the game's blend with those at 30% of their weight;
    time of day, the world's saved ambient and water or lava over the eyes count whole. The cached
    terrain's own haze keeps the game's colour.

## Concurrency invariants

- No worker mutates `LodWorld` or a live section.
- No background task reads the live block registry.
- Every asynchronous request eventually produces success, explicit failure, cancellation, or retryable state.
- A completed server-assist read cannot cross player-session identity or reorder a later
  response ahead of an earlier accepted read.
- Queue backpressure is applied before retaining large chunk, mesh, or section snapshots.
- Dirty state is cleared only when responsibility for that exact revision has transferred safely.
- A stale result cannot overwrite a newer section revision.
- Every worker result carries a world epoch or an equivalent teardown identity; clearing
  a queue alone cannot stop an in-progress job from publishing afterward.
- Shutdown either persists acknowledged work or reports exactly what remained.
- Diagnostics must not be capable of killing a worker thread.

## What is deliberately derived from source

File inventories, method lists, queue field names, and module counts are not maintained here. They are cheap to obtain accurately with repository search and expensive to keep correct in prose.
