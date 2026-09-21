# Vintage Horizons — gotchas and reversals

> Tier 1: durable traps that already cost time or are directly proven by source. Read by trigger before changing the affected system.

## Traps

### G1 — Never read the live block registry from a worker

**Trigger:** adding background deserialization, palette work, capture classification, or storage processing.

**Trap:** apparently harmless `GetBlock` calls may lazily mutate an engine dictionary. Parallel reads are therefore not guaranteed to be read-only.

**Do:** retain block codes off-thread and resolve ids, flags, and tint slots on the owning game thread before publishing a section.

### G2 — A background database does not help if the main thread scans its whole index every tick

**Trigger:** changing local server-cache discovery or remote manifests.

**Trap:** 0.2.1 reduced `PumpLocalOffers` from twice per frame to once per second, but it still runs a full `SELECT Detail, SX, SZ FROM Section` on the game thread. The network path also re-enumerates the complete remote key set each tick. Cost grows with explored history even when nothing changed.

**Do:** discover keys off-thread at a coarse cadence and publish only deltas. Apply each manifest chunk once.

**Found:** source-traced performance review, Session 1.

### G3 — An item count is not a frame-time budget

**Trigger:** adding `PerTick`, `PerFrame`, or queue-drain limits.

**Trap:** one section may mean a tiny palette or hundreds of kilobytes of runs; one mesh may mean one upload or two large GPU buffers. Fixed item counts cannot bound latency.

**Do:** measure elapsed time and bytes in addition to items, stop when the frame/tick allowance is spent, and resume later.

**Implemented:** Session 20 applies job-boundary time/retained-byte limits to mesh
snapshots and result-boundary time/upload-byte limits to GPU work. The first item still
progresses, frame-local budget state is allocation-free, and old GPU resources survive
until a complete replacement is live.

### G4 — Once-per-second rate limits create once-per-second stutters

**Trigger:** implementing server sweep, generation, assist serving, or any token bucket.

**Trap:** issuing an entire per-second allowance in one callback creates periodic CPU, I/O, callback, and allocation bursts. Integrated singleplayer puts those bursts in the same process as rendering.

**Do:** accumulate allowance and spend it across normal ticks under an elapsed-time ceiling.

### G5 — Camera-relative far distance must not reset projection for ordinary movement

**Trigger:** changing `EffectiveFarDistance`, camera limits, mesh bounds, or eviction.

**Trap:** recomputing the farthest mesh distance relative to the camera changes the exact float while the player moves. Exact equality then makes `Reset3DProjection` run on moving frames, and the scan itself is O(mesh count).

**Do:** cache world-space bounds, compute distance in O(1), and quantize the applied far plane with hysteresis.

**Implemented:** Session 5 tracks both opaque and water footprints, grows the projection
immediately in safe 512-block steps, and requires a lower step to remain stable for five
seconds before shrinking. Preserve the separation between the continuous shader far edge
and the quantized camera projection.

### G6 — A request slot must end in success, explicit refusal, or retryable state

**Trigger:** changing local offers, server assist, load failures, queue caps, or packet drops.

**Trap:** removing a key from the wanted set without clearing `LoadsInFlight` strands it permanently. Likewise, a server that silently drops a request fills every client in-flight slot.

**Do:** model request state explicitly. Mark requested only when responsibility actually
transferred; answer server requests even when refusing. Background decoder acceptance is
transferred responsibility, not success, so retain the transport/world slot until
owning-thread publication or terminal rejection. Give bounded retries a monotonic
cooldown, or an attempt ceiling can be exhausted over consecutive ticks without spanning
the transient failure.

### G7 — Async section work needs revision validation

**Trigger:** moving mip generation, capture publication, decode, save, or mesh preparation off-thread.

**Trap:** a worker snapshot can finish after capture or another child update changed the section. Publishing it unconditionally overwrites newer truth.

**Do:** carry world identity plus section identity/revision where same-world mutation can
race, reject stale results, and requeue when a newer revision still needs work. Queue
clearing is not sufficient: an in-progress worker job may publish after the clear.

### G8 — Visibility and eviction are not the same signal

**Trigger:** adding frustum culling to quadtree traversal.

**Trap:** if invisible means unselected and unselected means evict, turning around forces reloading and remeshing the world behind the camera.

**Do:** use visibility to skip traversal/draw/request work, while residency uses its own distance and age policy.

### G9 — Keep SQLite connection ownership explicit

**Trigger:** moving local cache discovery or server blob reads off-thread.

**Trap:** moving a query delegate to a worker does not make a connection safely concurrent. Shared prepared commands and connection state can still cross threads.

**Do:** use a dedicated read-only connection owned by the reader thread, or serialize all access through one storage owner.

**Implemented:** Session 15 gives server assist a bounded, unpooled, read-only connection
created, queried, and disposed by one reader thread. Results retain player session and
request order until owning-thread packet publication.

### G10 — Shader source must remain pure ASCII

**Trigger:** editing `.vsh`, `.fsh`, or included shader comments.

**Trap:** the engine's OpenTK marshaling can truncate source when UTF-8 byte count differs from character count, producing misleading shader failures.

**Do:** keep shader bytes ASCII and preserve the static check.

### G11 — Vintage Story client tests must use the isolation scripts

**Trigger:** launching or stopping a test client/server.

**Trap:** the client uses a global named pipe. A test launch can forward its connection request into the user's already-running game even with a separate data path; broad process-name termination can kill the wrong instance.

**Do:** use `scripts/test-client.sh`, `scripts/test-server.sh`, `scripts/test-stop.sh`, or the Windows-native `scripts/bench-windows.ps1`. Preserve their private temporary directory, pidfile, and command-line checks.

### G12 — Send shutdown commands before publishing benchmark completion

**Trigger:** changing benchmark completion or Windows client/server orchestration.

**Trap:** the runner closes the client as soon as the done marker appears. A delayed `/stop` callback registered after writing the marker may never execute, leaving the isolated server alive.

**Do:** make result artifacts durable, send the graceful server command, then publish the done marker. Never compensate with broad process termination.

The Windows runner requires PowerShell 7: it uses `ConvertFrom-Json -AsHashtable` and
`Start-Process -Environment`, neither of which Windows PowerShell 5.1 supports. Keep the
explicit `#Requires` guard and invoke it through `pwsh`.

### G13 — SQLite disposal does not defeat connection pooling

**Trigger:** replacing or directly editing a SQLite fixture file and then reopening it in the same process.

**Trap:** disposing a pooled connection can return it to the pool. A later open may observe the old file handle/schema even though the path was replaced.

**Do:** set `Pooling=false` on deliberate stale-file/schema-edit connections, or explicitly clear the relevant pool before replacement.

### G14 — A tick burst cap must retain fractional rate credit

**Trigger:** converting a per-second allowance into fixed-cadence tick grants.

**Trap:** clamping all stored credit to the whole-item tick cap before spending discards
fractional overflow whenever the configured rate does not divide the tick frequency. At
20 Hz, a first implementation made an 8/s allowance run at roughly 6.7/s.

**Do:** cap the actual whole-item grant, retain less than one token of normal fractional
overflow, and discard old credit only when a delayed tick could create catch-up work.

### G15 — A byte ceiling can starve an oversized FIFO head forever

**Trigger:** adding byte or elapsed-time limits to a result queue.

**Trap:** if the oldest item is larger than the byte limit and admission is checked before
any work, every tick rejects the same item and nothing behind it can progress.

**Do:** admit at least one oldest item from a non-empty drain, record its actual/estimated
bytes, then stop before later work once a ceiling is exceeded. Track oldest age so lack of
progress remains visible.

### G16 — A queued save is not durable local data

**Trigger:** changing foreign adoption, remote fallbacks, eviction, save coalescing, or
shutdown routing.

**Trap:** clearing dirty state at snapshot enqueue lets a failed write disappear, and a
success for revision N can erase a newer mutation at N+1. Removing a foreign source at
install has the same fault: eviction can route a reload to a local row whose write is
still pending or failed. Draining only the accepted queue at close also misses dirty
revisions held back by its capacity ceiling.

**Do:** give stored state its own revision, retain dirty membership through enqueue, and
clear it only on exact successful acknowledgement. Coalesce only pending same-key
snapshots, retry failures with bounded delay, and make close alternate drain/ack/enqueue
until clean or an exact unresolved report. Retain the foreign fallback until any local row
is durably acknowledged.

**Found:** revisioned persistence hardening, Session 21.

### G17 — Vintage Story camera pitch is PI-centred

**Trigger:** changing benchmark camera control or route pitch conventions.

**Trap:** route files describe a conventional zero-degree horizon with negative angles
looking down, but Vintage Story's mouse/camera pitch is centred at PI radians and clamps
around PI/2 through 3PI/2. Passing route radians directly, or merely flipping their sign,
looks into the sky while producing plausible frame-time CSVs.

**Do:** map route pitch to `PI - routePitch`, pin both `MousePitch` and `CameraPitch`, and
visually inspect a rendered screenshot before accepting route evidence.

### G18 — Capture activity does not prove a route is uncached

**Trigger:** using a movement route to claim first-time exploration or cold-cache capture
behavior.

**Trap:** a persistent benchmark sandbox may already contain VH coverage for the whole
route. Vanilla chunks can still load and produce capture/publication work there, so active
capture telemetry does not prove the distant cache is being populated for the first time.

**Do:** record cache state as part of the scenario. For an uncached-terrain benchmark,
start from a clean isolated VH cache or verify every route coordinate is absent before
launch. Label populated-cache runs as warm-cache traversal even when recapture is active.

### G19 — A clean cache does not keep a compact route cold

**Trigger:** designing a multi-leg or multi-lap capture-frontier benchmark.

**Trap:** an empty database proves pre-launch absence, but the route immediately populates
coverage around itself. With a 256-block streaming radius, later legs of a 400-block square
substantially overlap footprints captured by earlier legs in the same run.

**Do:** account for the streaming radius. Use a long one-way trajectory, widely separated
fresh-cache legs, or reset the cache per leg. Describe the initial settled footprint and
do not label every later frame cold merely because the database started empty.

### G20 — A named stats switch may not be the only enable path

**Trigger:** measuring enabled-versus-disabled diagnostic or instrumentation overhead.

**Trap:** the unattended runner set auto-unpause, and the client treated auto-unpause as
an alias for allocation telemetry. A nominal stats-off sample would therefore still call
the per-phase GC allocation counter and could falsely report no overhead.

**Do:** source-trace every enable path before an A/B run and keep unrelated test controls
orthogonal. Here, only `VINTAGEHORIZONS_STATS=1` enables allocation sampling and continuous
stats; auto-unpause controls window-focus behavior only.

### G21 — A scenario label does not prove its precondition or completion

**Trigger:** benchmarking warm/cold joins, sweeps, transient generation, or any operation
whose work can outlive the fixed route.

**Trap:** a database file can belong to another world or contain no sections, and a route
can produce plausible frame numbers while the named server operation is still running. A
48-chunk sweep ended with a valid CSV after roughly two minutes but had reached only 10%
of its load phase. Likewise, a completed frame CSV can leave capture, mip, save, load, or
storage work outstanding; stable frames do not prove durable convergence.

**Do:** validate the active world's reported cache count, require the operation's exact
terminal state, and preserve both pre-launch state and postcondition beside the result.
For mip/persistence soaks, require a cooldown long enough for a fresh stats sample and
fail unless prerequisite capture/load work, mip obligations, unsaved state, and storage
backlog/errors have drained.

### G22 — Sweep and generation radii exclude the safety-neighbourhood probes

**Trigger:** sizing a sweep or transient-generation benchmark by configured chunk radius.

**Trap:** the configured radius describes the load/generation square. Existence probing
extends four chunks farther on every side so frontier decisions have a complete
neighbourhood. Radius 48 therefore means 11,025 probes, not 9,409, and can make an
apparently short completion scenario several minutes long.

**Do:** budget probe work as `(2 * (radius + LodColumnMap.SafeNeighbourhood) + 1)^2`, then
calibrate load/generation time separately from measured evidence.

### G23 — A joining player is not a disconnected player

**Trigger:** servicing a server-assist request queue during connection or world join.

**Trap:** the client can request sections after the assist handshake but before
`PlayerByUid` exposes it as `Playing`. The 50 ms server loop treated that transient state
as a departure, removed the bounded queue without a reply, and stranded all 16 client
request slots. A completed frame CSV hid the failure until the scenario required actual
received and installed sections.

**Do:** retain the bounded queue through transient join state. Let the server's player
disconnect event own real queue cleanup, and require transfer postconditions rather than
inferring success from a handshake or a saturated request counter.

### G24 — Graceful restart and timed termination do not prove crash recovery

**Trigger:** validating persisted asynchronous mip or save obligations across interruption.

**Trap:** graceful close runs the shutdown drain, while a fixed-delay termination can hit
a moment with no durable obligation. Even observing a flagged row can race a later clearing
write and turn the intended crash test into another clean restart.

**Do:** publish a test marker only after the flagged database write succeeds, prevent the
clearing writer from overtaking orchestration, verify the exact isolated PID before
termination, require the restart to report persisted work, and use a later fresh process
to prove the cleared state reached disk.

### G25 — A priority queue needs separate ownership of the obligation

**Trigger:** replacing whole-set nearest searches with a heap or spatial work index.

**Trap:** a removed/re-added key leaves a stale heap entry, while the nearest key may be
temporarily blocked by an in-flight mesh or reload. Treating heap removal as ownership can
either publish stale work or silently lose the newer dirty obligation. Re-ranking every
camera movement frame also recreates the original O(collection) cost under a new name.

**Do:** keep exact dirty membership separate, validate every dequeued entry, restore busy
keys without clearing membership, ingest new keys incrementally, and rebuild priorities
only at a coarse spatial/policy boundary under an explicit examination ceiling.

**Found:** render-dirty priority scheduling, Session 19.

### G26 — A value type's optional constructor is not its default constructor

**Trigger:** converting a helper class with optional constructor parameters to a struct.

**Trap:** `new Helper()` can zero-initialize a struct instead of invoking the overload that
declares only optional parameters. Reference fields that the old constructor initialized
remain null, and the failure may not appear until the second budget item uses them.

**Do:** add an explicit parameterless constructor that delegates to the intended defaults,
remove optional defaults from the parameterized overload, and check the exact production
form (`new Helper()` followed by multiple operations).

**Found:** `LodDrainBudget` runtime validation, Session 21.

### G27 — A runtime runner can deploy a stale build artifact

**Trigger:** a harness copies an existing Debug/Release output instead of building source.

**Trap:** a complete, plausible benchmark can exercise yesterday's DLL. Source-only
telemetry is then absent, but aggregate FPS and clean shutdown make the run look valid.

**Do:** build the exact configuration immediately before the run and verify a source-
specific log field or artifact freshness before accepting evidence. The Windows runner
now rejects assemblies older than project/C# inputs; preserve any stale run only as a
diagnostic, never as result evidence.

**Found:** renderer-budget runtime validation, Session 21.

### G28 — Integrated client and server test hooks share one environment

**Trigger:** using an environment-controlled storage, persistence, or interruption hook
inside integrated singleplayer.

**Trap:** client and server pipelines live in one process and inherit the same environment.
Whichever storage worker reaches a process-global marker first can appear to prove the
other database's behavior. A durable server `ApplyToParent` row therefore cannot establish
that the client cache will recover.

**Do:** scope the hook at pipeline construction, preserve the database-side identity in
the scenario, and add a check that the excluded owner cannot publish the marker.

**Found:** integrated mip interruption/recovery, Session 22.

### G29 — Progress logging is owning-thread work

**Trigger:** adding periodic progress, throughput, queue, or diagnostic messages inside a
frame-, tick-, or callback-critical path.

**Trap:** logger calls may synchronously format and publish to console/file sinks. An
every-200-sections assist notification reproduced multi-millisecond server-service tails;
correlated attribution measured 3.573 ms at that boundary while the callback's two sends
totalled 0.075 ms and no managed collection crossed the call. Rate-limited logging is
still unbounded latency when it executes inside the rate-limited work.

**Do:** accumulate counters in the critical path and report them from an existing status
command or explicitly opt-in coarse telemetry callback. Add a source or structural check
when a proven hot path must remain free of synchronous logger calls.

**Found:** server-assist tail attribution, Session 23.

### G30 — Camera-relative geometry coordinates are not stable appearance coordinates

**Trigger:** adding world-space noise, tint variation, material breakup, or another
appearance effect to camera-relative LOD geometry.

**Trap:** camera-relative positions intentionally change as the camera moves. Sampling a
noise field from those positions makes a fixed terrain point change color in flight even
though its mesh and stored color are unchanged.

**Do:** keep geometry camera-relative for projection precision, but pass a stable section
world origin and derive appearance coordinates from section-local vertices. At extreme
world coordinates, precision loss may soften cosmetic variation but must never move
geometry.

**Found:** cached-terrain color alternation, Session 24.

### G31 — View distance is not per-chunk render readiness

**Trigger:** handing cached terrain to the vanilla renderer near its configured view edge.

**Trap:** a radial cutoff cannot tell whether one replacement chunk has actually rendered.
Moving it outward opens a band while streaming lags; moving it inward leaves approximate
cached and vanilla meshes overlapping and z-fighting. Sinking or fading the cached mesh
still leaves two owners. The public `IsChunkRendered` signal is closer, but in the 1.22.5
client its counter advances during tessellation before completed mesh upload. The exact
1.22.7 trace confirms the same ordering: `ChunkDirty(NewlyLoaded)` fires earlier still,
and the public client API exposes neither the internal post-upload callback nor a
chunk-unload event for the observed removal path.

**Do:** use exclusive chunk ownership. Treat dirty/load signals as candidates, require
render-frame-separated confirmation before readiness gain, and revalidate ready cells
under a boundary-first elapsed-time/item budget so unload restores cached coverage.
Publish CPU/GPU readiness atomically, skip wholly replaced meshes, and mask only mixed
frontier sections. Keep visibility and residency separate so ownership changes do not
cause remesh thrash.

**Found:** cached/vanilla handoff investigation, Session 24. Detailed implementation plan:
`dev/plans/PLAN_CHUNK_AWARE_VANILLA_HANDOFF.md`.

### G32 — Stabilization work must not re-enter its active queue in the same frame

**Trigger:** implementing a frame-delayed readiness check, retry, debounce, or publication
confirmation inside a bounded queue drain.

**Trap:** immediately requeueing an item that is not yet old enough lets the same item be
dequeued repeatedly in one frame. It can consume the entire nominal item/time budget
without making progress and defeats the intended render-boundary delay.

**Do:** retain not-yet-eligible work in a separate fixed deferred queue, promote it only
after the required frame boundary, and track oldest age for both active and deferred work.

**Found:** vanilla-readiness shadow tracker, Session 25.

### G33 — A revalidation sweep filtered to committed state can only lose

**Trigger:** writing a bounded maintenance sweep for state that can be both gained and
lost, such as readiness, residency, or reachability.

**Trap:** filtering the sweep to cells already committed makes it a confirmation loop. It
detects loss promptly and can never detect gain, so progress depends entirely on whatever
external event happens to requeue a cell. Worse, the wasted budget looks like healthy
activity: a measured 15-second interval spent 432,744 probes re-confirming 1,727 owned
cells and none on the 1,801 cells awaiting an answer, while every counter read normally.

**Do:** sweep without a state filter and let coalescing suppress duplicates. Assert in a
runtime gate that probes actually reach non-committed cells, because a healthy-looking
probe count proves nothing about where the probes went.

**Found:** vanilla-readiness interior maintenance, Session 26.

### G34 — Gate on retained state, not on counters that reset every interval

**Trigger:** writing an automated acceptance gate against periodic telemetry.

**Trap:** rates and states fail in opposite directions. A gate on "ready transitions this
interval" failed a correct, fully settled system that had nothing left to transition, while
the same gate passed a broken one that churned continuously. Reported maxima have the
mirror problem: a single blocking engine call the code cannot preempt fails a tight maximum
that says nothing about steady-state cost.

**Do:** gate on retained state and on a percentile that reflects the steady path, report
rates and maxima as evidence, and when a defect is found, encode the specific invariant it
violated rather than only fixing the code.

**Found:** readiness convergence gate, Session 26.

### G35 — A summed world coordinate is not exact enough to own a chunk

**Trigger:** deciding which cell, chunk or region a fragment belongs to inside a shader,
from a world position built by adding a section origin to a local offset.

**Trap:** the sum is a float32. At a 512,000-block world coordinate its ulp is 0.0625, so a
fragment 0.03 blocks below a chunk boundary rounds up onto the next chunk and reads that
chunk's ownership. Nothing crashes and nothing looks obviously wrong; a sliver of ground at
chunk edges simply obeys the wrong owner, and the error grows with world size. A substring
check comparing the shader and the C# addressing passes happily, because both expressions
are written the same way - only their arithmetic differs.

**Do:** pass the section origin as an integer in cell units and add the floor of the small
local offset, which is exact. Evaluate the shader's arithmetic in the check tier against
the real addressing over a grid that includes large origins and boundary-adjacent offsets,
rather than comparing the two expressions as text.

**Found:** chunk ownership mask, Session 27.

### G36 — Renderer state measured in one world must not survive into another

**Trigger:** caching per-world render state - readiness, ownership, visibility - beside a
resource the shader samples every frame.

**Trap:** clearing the model on a world or dimension change is not enough if the GPU
resource and its "active" flag are left alone. The shader keeps sampling the old world's
data, so terrain stays suppressed using ownership measured somewhere else entirely. The
model looks correct in every log line while the pixels are wrong.

**Do:** treat the model, the GPU resource, and the flag that says the resource is
authoritative as one unit. Clear all three on the same path, and default the cleared state
to the safe direction - here, every cell returning to the cache.

**Found:** chunk ownership mask, Session 27.

### G37 — A discovery cursor that restarts on movement never reaches the frontier

**Trigger:** sweeping a camera-following window with a cursor, and resetting that cursor
whenever the window moves.

**Trap:** the cells that matter while moving are the ones the window just gained, and they
sit at the far end of the scan. Restarting the cursor on every movement means the sweep
re-covers ground it already knows and never arrives, so the state ahead of the player is
the last thing ever established. It looks correct while standing still and fails only in
motion, in proportion to speed.

**Do:** queue what the window gains at the moment it gains it, and let the cursor cover the
interior at its own pace. Budget the work by elapsed time rather than an item count chosen
against a settled view, because a count that is generous when the queue is empty is the
binding limit exactly when it is not.

**Found:** vanilla-readiness acquisition under flight speed, Session 27.

### G38 — A renderer benchmark that starts before residency settles measures nothing

**Trigger:** comparing frame rates between two renderer configurations.

**Trap:** a run whose meshes are still loading draws almost nothing and posts a large
apparent gain. One measured 536.7 FPS against a 438.6 baseline, a 22% "improvement" whose
own telemetry read zero meshes and zero selected nodes for the first three intervals.

**Do:** settle to full mesh residency before measuring, and report mesh and selected-node
counts beside the frame rate so a reader can check the comparison instead of trusting it.
Treat any large unexplained win as a measurement bug until those counts match.

**Found:** chunk ownership mask, Session 27.

### G39 — State that hides something must be re-confirmed on a clock, not by a cursor

**Trigger:** caching an expensive observation - readiness, visibility, ownership - and using
it to suppress a fallback that would otherwise draw.

**Trap:** a cursor sweeping a camera-following window is not a bound. It restarts when the
window moves, it falls behind when the camera moves faster than it scans, and either way
the ground it has not reached keeps hiding its fallback. Three separate cursor fixes each
looked correct and each left holes that outlived the movement causing them, because none of
them could state how long a wrong answer could survive.

**Do:** re-confirm the entire suppressing set on a fixed interval, and size the set so that
is affordable - here roughly 1,700 cells against a probe costing well under a microsecond.
The interval then is the staleness bound, and it holds no matter how the camera moved.
Keep the cursors for discovery, where being late costs coverage rather than correctness.

**Found:** chunk ownership mask, Session 27.

### G40 — "The engine drew this chunk" means the counter moved, not that anything was drawn

**Trigger:** using `IsChunkRendered`, or any counter the tessellator advances, to decide
that vanilla terrain now covers an area.

**Trap:** `ICoreClientAPI.IsChunkRendered` compiles to nothing more than
`ClientChunk.quantityDrawn > 0`, and `ChunkTesselatorManager.TesselateChunk` advances that
counter and returns before meshing whenever the chunk reports empty. A chunk full of air
answers exactly like a chunk full of mountain. Both facts are read from the installed
game's IL, not inferred.

**Do not** conclude from that that an empty chunk should be refused ownership *in the
tracker*. 0.3.3 did exactly that and removed ownership from the entire world in one step. The reason is the
column rule, not the flag: a column counts as owned only when all of its vertical chunks
do, every column has sky above it, and sky is legitimately empty. Roughly two of every five
probed cells that report drawn also report empty, so excluding them leaves no complete
column anywhere, `NearestIncompleteColumnBlocks` collapses to about zero, and cached
terrain draws over everything.

**Correcting the original entry:** this gotcha first recorded that `IWorldChunk.Empty` is a
stale client-side flag that "does not mean this chunk has no blocks". That is wrong.
`ClientWorldMap.LoadChunkFromPacket` assigns `chunk.Empty = packet.Empty`, so the value is
the server's own answer delivered with the chunk, and the block-set accessors maintain it
afterwards. The client tessellator trusts it to decide whether to mesh at all. The flag was
never the problem; the rule built on it was.

**Do:** ask whether the engine holds any geometry, which is a different question from
whether the chunk is empty. `ClientChunk.centerModelPoolLocations` and
`edgeModelPoolLocations` are the mesh's index and vertex ranges in the shared pool; a chunk
with neither has nothing in the world's buffers. `VanillaChunkGeometry` binds them by
reflection and reports unavailable rather than guessing if a game update moves them, and
the check tier asserts the binding against the installed assembly.

**Do** also keep any such query inside its own handler and let it change nothing. The probe
loop treats a thrown query as "vanilla is not drawing here", so a diagnostic that throws
does not merely fail - it silently hands the whole world back to the cache. A measurement
that cannot change ownership can be added safely; a rule that can must be established first.

**But do refuse it in the mask.** An air cell must never suppress a fragment. Cached terrain
is an approximation and overshoots the real surface, so its geometry lies inside air chunks;
vanilla draws nothing there, so the discard removes the only draw and shears the top off the
cached horizon. The two rules coexist: air owns its cell for the column aggregate, which is
what 0.3.3 needed, and never reaches the atlas texel, which is what the picture needs.

0.3.15 established this from `.vhpaint`, after this entry had claimed in 0.3.5 that
suppressing cached geometry above the real surface was correct because the real ground below
is drawn by vanilla. That reasoning fails for geometry standing *above* the real surface:
there is no lower cell drawing anything at that height, so nothing replaces what is
discarded. Three ownership rules were written against this wrong conclusion.

**Found:** chunk ownership mask, Session 27, from play reporting holes that would not close,
and then from breaking ownership outright while trying to fix them. Corrected in 0.3.5 by
reading the game's IL instead of reasoning about the flag, and again in 0.3.15 by painting
the discarded fragments instead of reasoning about them at all.

### G41 — A duplicate chat command name switches off the rest of the mod's startup, quietly

**Trigger:** adding a chat command, especially one that replaces an older command's job
while the older registration is left in place.

**Trap:** `ChatCommandApi.Create` throws on a name that already exists, and the throw
propagates out of `StartClientSide`. The game logs "Failed to start system" and carries on:
everything registered before the collision works, everything after it silently does not
exist, and the mod keeps drawing, so a playtest looks normal. 0.3.4 shipped with `.vhwhy`
registered twice, which cost `.vhdetail` entirely and left the coarse-draw report
unreachable. The names are strings, so the compiler sees nothing.

**Do:** keep one name per command, and let
`StaticAssetChecks.ChatCommandNamesAreUnique` fail the check tier if that ever stops being
true. When a new command supersedes an old one, rename the old one rather than reusing the
name, or delete it deliberately.

**Found:** 0.3.5, by reading the client log of the 0.3.4 playtest rather than the game.

### G42 — An integer uniform set through the Vec2i overload never arrives, and only the log says so

**Trigger:** passing a pair of integers to a shader, or reaching for
`prog.Uniform(name, new Vec2i(...))`.

**Trap:** the client's only pair-of-integers setter calls `GL.Uniform2(location, float,
float)`. Against a `uniform ivec2` the driver answers `GL_INVALID_OPERATION` and leaves the
uniform at its previous value - zero. Nothing throws, the shader compiles and links, the
draw succeeds, and the check tier's own re-implementation of the address arithmetic still
agrees with the shader, because the arithmetic was never the thing that broke. The only
symptom is `after final compo - OpenGL threw an error: InvalidOperation` once per frame,
which is easy to read as somebody else's problem in a log with ten mods in it.

This shipped in 0.3.0 through 0.3.5. `maskSectionOrigin` stayed at (0,0), so every
fragment tested its ownership against chunk (0,0) instead of its own section's origin, the
window bounds test then rejected nearly everything, and the per-cell mask discarded almost
no fragments at all. Four attempts to explain the reported holes with ownership latency,
sweeps and staleness were all looking at CPU state that was working.

**Do:** split it into scalar `uniform int`s. `Uniform(name, int)` reaches `glUniform1i` and
is correct. `StaticAssetChecks.NoIntegerVectorUniforms` now fails the check tier on any
`ivec`/`uvec` uniform in our shaders, and the mask checks assert the two scalars are set.

**Do** treat a per-frame GL error as a defect with an address, not as noise. Two isolated
sandbox runs on the same scene separated it in about two minutes: 19,126 errors with
`-ChunkMask`, zero without.

**Trap while diagnosing it:** setting `glDebugMode` in the sandbox `clientsettings.json`
makes the client crash rather than annotate, because the debug callback throws
"No detailed debug message due to a non-debug context" when the GL context was not created
as a debug context. That crash is still the fastest answer available - its stack names the
exact `Uniform` call - but expect to clean up afterwards: the client leaves
`VSCrashReporter.exe` holding `launch.log`, which blocks the next run's log rotation, and
the sandbox server stays up for `scripts/test-stop.ps1`.

**Found:** 0.3.6, from a controlled pair of sandbox runs after noticing tens of thousands
of GL errors in an ordinary playtest log.

### G43 — "Is the engine drawing this chunk" is decided at draw time, and distance is half the answer

**Trigger:** deciding that vanilla covers an area, from any per-chunk signal - including
`ClientChunk.CullVisible`.

**Trap:** four signals look like they answer it and none of them do on their own.
`ICoreClientAPI.IsChunkRendered` is `quantityDrawn > 0`, a counter that only ever goes up,
so it means "tesselated at some point" (G40). Mesh pool locations are closer, but the engine
keeps a chunk's mesh while choosing not to submit it, so their presence is not submission.
`IWorldChunk.Empty` is accurate but answers a different question. And `CullVisible` is the
culler's occlusion verdict, which is necessary but not sufficient.

The real per-frame test lives on the pool location, not on the chunk.
`ModelDataPoolLocation.IsVisible` in `CullNormal` mode is
`!Hide && CullVisible[VisibleBufIndex] && culler.InFrustumAndRange(sphere, ..., LodLevel)`,
and `FrustumCulling.InFrustumAndRange` ends with `playerPos.HorDistanceSqTo(sphere.x,
sphere.z)` - the mesh's bounding-sphere centre, which is the geometry extents midpoint and
not the chunk centre - against a per-LOD bound that is at most `ViewDistanceSq` - itself
`viewDistance * viewDistance + 400` (`UpdateViewDistance`). **So the engine range-culls every
terrain mesh against the current camera, every frame, after all four chunk signals have said
yes.** A chunk that is loaded, meshed, unhidden and cull-visible but further away than the
view distance is silently never drawn.

Nothing about the chunk changes when that happens, which is what makes it invisible to a
tracker. Worse, `ChunkCuller.CullInvisibleChunks` early-returns when the camera has not
changed chunk (and the regen-traversal queue has not moved by 10), so `CullVisible` is not
merely stale out there - it is frozen for as long as the player stands still. That is the
exact condition under which the band was reported: committed cells in the annulus between the
view-distance circle and the tracked window edge stayed committed forever, the mask discarded
cached terrain there, and nothing drew that ground. Only on the trailing side, because the
leading side never had chunks loaded beyond the view distance to latch in the first place.

The culler also carries an `isAboveHeightLimit` branch: fly high enough and vanilla stops
drawing the ground directly below, in an unmodded client, while every other signal still
claims it.

**Do:** read `CullVisible[bufIndex]` **and** apply the distance clause. All the culler members
are public API - no reflection - and `VanillaReadinessChecks.ChunkGeometryBinding` pins them
against the installed assembly. The distance clause needs no engine access at all: it is
column distance against the approved view distance, done in pure arithmetic, which matters
because the probe loop is hot and a chunk lookup there takes the client's chunk lock.

**Do** clear the whole chunk when picking the threshold, and check which direction the slop
falls in. The engine measures range from the mesh's bounding-sphere centre, and `TesselatedChunk`
builds that centre as the geometry extents midpoint (`positionX + (xMax + xMin) / 2f`) - so it
sits wherever the blocks in that chunk happen to be, anywhere across the 32-block span, not at
the chunk centre. Comparing the column's *nearest face* against the plain view distance sounds
conservative and is conservative in the overlap direction only: a chunk whose near face is just
inside the view distance can have its sphere centre well outside it, so the engine culls it
while every latched signal still says "drawing". That leaves a residual permanently-stale ring
up to `32 * sqrt(2)` wide - a thinner copy of the same band.

Subtract the full in-chunk horizontal diagonal instead: deny when the nearest face exceeds
`viewDistance - 46`. Since the engine culls no earlier than `sqrt(viewDistance^2 + 400) >=
viewDistance`, and centre and face lie inside one column so differ by at most 45.26, culled
implies denied for every possible midpoint placement. The cost is cached terrain drawn over the
outermost chunk and a half of live vanilla terrain - a seam, not a gap.
`VanillaReadinessChecks.OwnershipStopsAtTheEnginesDrawRange` asserts the no-hole direction
directly, sweeping every cell whose farthest admissible sphere centre reaches the engine's
bound; it fails on the nearest-face-versus-plain-view-distance threshold.

**Do** treat empty and unknown as drawn, so a wrong answer costs the correction rather than
uncovering live terrain.

**Do** expect the culler verdict to be camera-dependent. It is occlusion: a chunk behind a
mountain is not drawn, so its cell is released and cached terrain draws there, hidden behind
the same mountain. Correct, and not free - the cost is unmeasured.

**Found:** the culler half in 0.3.10, from a player testing the altitude case in vanilla with
no mods after three ownership rules built on the wrong signal failed to close the holes. The
distance half in 0.3.16, by reading `ModelDataPoolLocation.IsVisible` and `InFrustumAndRange`
in the game's IL after this entry had claimed for six releases that the culler's verdict was
the *only* thing that decided.

### G44 — A mirror of a wrapped ring must be told when a slot changes hands

**Trigger:** keeping a second copy of ring-addressed state - a GPU texture, a cache, any
mirror - alongside `VanillaRenderReadiness`.

**Trap:** the ring is fixed size and scrolls with the camera. A column leaving the window
surrenders its slot to whichever column wraps onto the same address. `ClearSlot` wiped the
tracker and nothing wiped the mirror, so the arriving column read the departed column's
value. On the ownership atlas that meant `ReadyTexel` for cells nothing owned, and the
shader discarded cached fragments over ground vanilla was not drawing.

The failure has a shape worth recognising: a band rather than scattered holes, at the
trailing edge of movement, stable while standing still, unrepaired by any amount of
re-probing. Every CPU-side diagnostic said the system was healthy, because it was - the
tracker, the column aggregate, the section counts and the ownership audit were all correct,
and only the mirror was stale. Three ownership rules were written and discarded chasing it.

**Do:** funnel every wipe through one method and raise from there.
`VanillaRenderReadiness.ClearSlot` raises `ColumnEvicted`; the renderer clears the atlas
column. Test it by proving the arriving column reuses the departed column's exact texel
index and does not inherit its value - a test that only asserts "the mask was cleared"
passes against an addressing scheme where the two never collided.

**Do not** trust "the routine exists" as evidence it runs.
`VanillaReadinessMask.ClearColumn` was written with a correct summary explaining exactly why
it was required, and had no callers from the feature's first version through 0.3.12.

**Found:** 0.3.13, after a player's on/off comparisons ruled out ownership selection and
then whole-mesh skipping, leaving only the GPU mirror.

### G45 — A chunk lookup inside the readiness work takes the lock the loader threads want

**Trigger:** calling `IBlockAccessor.GetChunk` (or anything else reaching the client's chunk
store) from the readiness probe loop, from a diagnostic inside it, or from the wholesale
atlas rebuild.

**Trap:** the lookup takes `ClientWorldMap.chunksLock` — the same lock `IsChunkRendered` has
just taken, and the same one the chunk loader threads want. The probe queue is at its
longest exactly while a world is coming up, so the cost lands where it hurts most. A
drawn-without-geometry diagnostic added in 0.3.5 called it once per probe and is the
standing suspect for cached terrain taking 36.4 s to appear after joining against 6.1 s on
the release before it.

The rebuild path is the same trap wearing different clothes. `WriteMask` runs at mask
creation and on **every** window change — every 32 blocks of travel — over the whole
window, so a per-cell chunk lookup there is thousands of lock acquisitions per crossing.

**Do:** restrict any such lookup to the observations that can actually change a decision.
0.3.8 narrowed the diagnostic to the second of the two true observations a commit needs, or
a re-confirmation of an already-committed cell; every other probe at join is a first
sighting that decides nothing.

**Do:** store the answer in the tracker when a lockless path needs it later. 0.3.16 records
the air exclusion as a per-cell flag at publication time, so `WriteMask` consumes a bit
instead of asking the chunk store, and the once-per-second resync — which already holds a
budget for the lookup — keeps the bit authoritative.

**Do:** prefer pure arithmetic in the probe loop outright where the question allows it. The
draw-range clause in G43 needs no engine access at all.

**Do:** treat a join-time slowdown as a suspect for any diagnostic added since the last
known-good join, before bisecting residency.

**Found:** 0.3.5 through 0.3.8, from a reported join-time regression; recorded here in
0.3.16 after the same shape recurred in the atlas rebuild and was designed out rather than
measured again.

### G46 — `Block.GetColorWithoutTint` is not a function of the block

**Trigger:** asking the engine for a block's colour once and storing the answer as if it
described the block, rather than that call.

**Trap:** two things vary underneath it. `BlockWithGrassOverlay` — which is every
grass-covered soil, peat and clay, so most ground a player ever sees — answers with
`BlockTextureAtlas.GetRandomColor`, one of thirty pixels drawn out of the grass texture at
random per call. And the base implementation hands the question to whatever DECOR sits on
the up face, so the answer also depends on what is standing on that particular block.

A palette entry is registered once per section, so one draw decided the colour of every
instance of that block across a whole 64-block section, and the section beside it drew
again. Measured on a real 1,041-section cache: **38 different stored colours for
`soil-low-normal` alone**, scattered with no relation to terrain, climate or height, with
a per-section standard deviation of 30-39 per channel. On the ground that is flat green
and flat brown tiles meeting at a hard edge, and no amount of blending at section borders
would have fixed it, because the step was in the data.

Blocks that answer deterministically — gravel, rock, forest floor — showed a standard
deviation of exactly zero, which is what made the randomized ones stand out in the cache.

**Do:** compute one colour per block id and cache it, by averaging many draws so a
randomized answer collapses to the texture's own mean. A deterministic block averages to
what it already returned, so nothing else moves.

**Do:** probe at a position nothing can stand on — the sky above the world origin — so the
decor branch cannot make one snowy sample decide a block's colour everywhere.

**Do:** keep the real block position for blocks that carry an entity: a chiselled block
averages the materials in its own entity there and genuinely differs per position. That is
the case the position argument exists for, and it is why the rule is `EntityClass != null`
rather than a list of block codes.

**Do:** correct stored colours on load rather than bumping the schema. Colours are already
re-derived for flags and tint slots on the way in; a cache is worth weeks of exploration.

**Found:** 0.3.18, from a player report of neighbouring cached tiles rendering as
dramatically different flat colours. Diagnosed entirely offline, from the stored cache and
the decompiled engine, with no game run.

### G47 — A colour map is sampled per block, so one sample is a draw and not the colour

**Trigger:** calling `ApplyColorMapOnRgba` once and treating the result as "the tint" for a
whole landscape.

**Trap:** a seasonal map is two-dimensional. `seasonalGrass` is 128x16: X is the point in the
year, and **Y is chosen from a hash of the individual block's position**
(`seasonYPixelRel = MurmurHash3Mod(posX, posY, posZ, 100) / 100f`). That is what makes a real
meadow subtly mottled instead of a flat sheet. One sample returns one of the sixteen rows,
and at LOD distance that row was painted across every field in view.

Measured on the shipped map: at midsummer the rows run `#628100` to `#97B825` about a mean of
`#7B9C0D` - up to a quarter off in red, an eighth in green - and the mod re-rolled to a
different row whenever the player moved far enough to change the hash. The owner reported it
as "the green is slightly off" and named the season as the suspect before the map was opened.

This is G46 in a second guise: two different engine calls that look like properties of a
thing are actually draws from a distribution, and the LOD renderer wants the distribution's
mean because one distant pixel covers many blocks.

**Do:** average the tint over many world positions, not one. 0.3.19 uses 64 positions on an
8-block lattice: far enough apart that the hashes decorrelate, close enough that the climate
under them is still the viewer's own.

**Do not** reach for the `(rain, temp)` overload to sample rows directly. It is public, but
its `seasonYPixelRel` parameter is not exposed through `IClientWorldAccessor`, so it silently
pins every sample to row 0 - and it drops the height-above-sealevel term the two-altitude
tint depends on.

**Do:** clamp sample positions into the map. `GetClimate` answers 0 - freezing and bone dry -
for anything off the world edge, and one such sample drags the whole average.

**Do:** unpack the returned int by hand in a loop like this. `ColorUtil.ToRGBAFloats`
allocates a `float[4]` per call, and red is at bits 16-23, not the low byte, because
`ApplyColorMapOnRgba` flips red and blue by default. Reading red from the low byte turned
every grass tint teal once already.

**Found:** 0.3.19, from a player report on the 0.3.18 build that fixed G46.

### G48 — `GetRandomColor` and `GetAverageColor` return opposite channel orders

**Trigger:** reading a colour out of the block texture atlas and assuming one byte order.

**Trap:** they disagree, in the engine, by construction:

```csharp
texPos.AvgColor = ColorUtil.ReverseColorBytes(ColorUtil.ColorAverage(pixelsTmp, equalWeight));
...
array[num] = num2;              // raw bmp.GetPixel(..).ToArgb()
texPos.RndColors = array;
```

`ReverseColorBytes(0xAARRGGBB)` is `0xAABBGGRR`, so **`GetAverageColor` puts red in the low
byte and `GetRandomColor` puts it at bits 16-23**. The mod stores one convention, so every
colour that reached it through `GetRandomColor` had red and blue exchanged.

That is not a rare path. `BlockWithGrassOverlay.GetColorWithoutTint` — all grass-covered
soil, peat and clay — answers with `GetRandomColor`, and so does `BlockGroundStorage`. Blocks
that fall through to the base implementation get `GetAverageColor` and are correct. So the
mod's grass was swapped while its leaves and rock were not, which is exactly how the owner
described it: as if the grass and the tree colours had been exchanged.

Proof is in any cache. Group the stored colours for `soil-low-normal`: one population sits at
rgb(105,83,60), which is `fertlow.png`'s four-pixel average arriving correctly through the
repair path, and the other at rgb(128,141,140), which is the grass overlay's mean rgb(148,
149,129) with red and blue exchanged. A cyan-grey where an olive-grey belongs.

**Do:** read colours through `GetAverageColor` and treat `GetColorWithoutTint` as untrusted
for byte order. There is no way to ask a block which of the two paths it took.

**Do:** check any new atlas colour source against a texture whose channels differ visibly.
A greyscale or near-neutral texture hides this completely - the averaged grass base was
rgb(128,141,140), whose red and blue differ by 12, so the swap survived the 0.3.18 averaging
and the 0.3.19 tint work without ever looking obviously wrong.

**Found:** 0.3.20, from a player screenshot after two earlier colour fixes had landed.

### G49 — Grass-covered ground is composited by vanilla, and a third of it is never tinted

**Trigger:** taking one colour for a block that vanilla draws in the `TopSoil` render pass.

**Trap:** `chunktopsoil.fsh` is explicit:

```glsl
vec4 brownSoilColor = texture(terrainTex, uv) * rgba;
vec4 grassColor = getColorMapped(terrainTexLinear, texture(terrainTex, uv2 + ...)) * rgba;
outColor = brownSoilColor * (1 - grassColor.a) + grassColor * grassColor.a;
```

Only the grass overlay is colour-mapped. The dirt showing through it is drawn exactly as it
is, and the overlay is roughly 69% opaque at full coverage, 54% at sparse, 40% at very
sparse. So about a third of every grassy block is untinted brown — which is what makes
vanilla ground read olive rather than green, and where nearly all of its blue comes from,
because the seasonal tint's blue channel is near zero and anything multiplied by it loses
its blue outright.

Measured for `soil-low-normal` at midsummer: vanilla G/R 1.09 and B/G 0.26, against the
mod's 1.40 and 0.08 before this was handled.

**Do:** store the composite and dilute the SLOT's tint by the share that came from dirt.
`composite * (share + (1 - share) * tint)` equals `soil*(1-a) + grass*a*tint` exactly, which
is why the untinted share is part of the tint-slot key: two blocks on the same colour maps
with different grass coverage need different dilution. `TopSoilColorChecks` holds the
identity over 1,440 combinations.

**Do:** key the case on `block.RenderPass == EnumChunkRenderPass.TopSoil`, not on the
presence of `specialSecondTexture`. The liquids use that texture key for flow animation and
would be composited into mud.

**Do:** read the texture, not `GetAverageColor`. That call is not an average: it samples
exactly four pixels, at 35% and 65% of each axis. For an opaque texture it is close enough,
but for a partly transparent overlay those four pixels also decide how much of the block
below shows through, and it read 146 where the true mean alpha is 175 — a fifth too much
bare dirt, which was the entire residual error after 0.3.20 and was visible in game as
distant ground still not quite matching. `capi.Assets.TryGet` plus
`capi.Render.BitmapCreateFromPng` gives the real thing; take the colour alpha-weighted and
the coverage as the mean alpha, and that pair is exactly what averaging vanilla's per-pixel
blend over the whole face reduces to. Read it through `BitmapExternal.Pixels` rather than
`GetPixel`, which returns an `SKColor` and would put SkiaSharp on the mod's reference list;
both are `0xAARRGGBB`, and the decoder asks for unpremultiplied alpha, so weighting by alpha
is a weighting and not a second application of it.

**Know:** with the true means the LOD reproduces vanilla's own blend exactly — rgb(80.5,
88.1, 22.8) against vanilla's rgb(80.5, 88.1, 22.8) for `soil-low-normal` at midsummer,
where the four-pixel estimate gave rgb(86.5, 89.2, 29.8).

**Found:** 0.3.20, alongside G48; they were two independent faults in the same surface.

### G50 — Matching vanilla's colour is not finished when the albedo matches

**Trigger:** comparing cached terrain against vanilla terrain and concluding from one
screenshot that the colour is right, or that it is wrong.

**Trap:** the final pixel is albedo times lighting, and the mod supplies both halves itself.
0.3.18 to 0.3.21 made the albedo exact, and the owner then reported that the match held at
some times of day and broke at others - which is the signature of the other half.

Vanilla's terrain lighting is not a sun-angle shade. `getBrightnessFromNormal` floors its
value at `normal.y * 0.95`, so an up-facing surface never darkens as the sun drops, with a
comment in the engine source that block tops darker than block sides look uncanny; and
`chunkliquid.fsh` does not shade by normal at all. The mod's `0.55 + 0.45 * sunAngle` took
flat ground to 0.55 at dawn and dusk against vanilla's 0.95, a 40% gap that closes to nothing
at midday.

**Do:** separate the two halves before attributing a mismatch. An error that varies with the
TIME OF DAY at a fixed season is lighting. One that varies with the SEASON at a fixed time of
day is the tint. One that is constant is the albedo. The owner isolated the first of those by
sweeping the daylight cycle with everything else held still, which is worth asking for.

**Do:** expect the remaining lighting difference to be a floor, not a match. Vanilla's colour
also carries per-vertex light values baked into each chunk and a shadow-map term; LOD sections
store neither, and should not. The normal rule is the dominant term and the only one worth
copying.

**Found:** 0.3.22, from a report that the colour matched at some light levels and not others.

### G51 — "Not in RAM" is not "no data there", and the mesher was asking the wrong one

**Trigger:** any mesh decision that reads `LodWorld.Sections` to establish whether a
neighbouring section exists.

**Trap:** `Sections` is residency. It answers what is in memory this instant, and a section
with a perfectly good row on disk is absent from it constantly - evicted by the sweep, or
simply not read yet. `HasDataSet` is the durable question, and the shader's `openEdges` has
always asked it.

The mesher walls off a section edge whose neighbour is missing, which is correct at the
frontier of explored space and wrong everywhere else, and it was deciding that from
residency. Sections load and mesh NEAREST-FIRST, so the outward-facing side of nearly every
section was meshed before its neighbour had landed. Nothing repaired it afterwards:
`MarkChanged` refreshes a neighbour's mesh, but only content changes call it, and
`InstallLoaded` deliberately marks nothing.

On land the spurious wall is invisible - the neighbouring ground is opaque and hides it,
which is why this survived so long. On water it is the whole bug. Measured on one synthetic
ocean section: 1 water quad with the neighbour present, 65 without it, 64 of them a
3,200 block² sheet of 66%-opaque water standing on the shared plane from the surface to the
seabed. An ocean showed a grid of dark vertical lines that never healed.

**Do:** ask `HasDataSet` when the question is "is there terrain there", and treat a
data-bearing but unloaded neighbour as covered rather than as the frontier. Do NOT force the
neighbour resident to find out - a neighbour outside the draw set is meant to end in nothing,
and loading one to prove it pulls the cache into memory a ring at a time.

**Do:** repair rather than pre-empt. Record which sides a mesh guessed at and re-mesh only
those when the neighbour actually arrives. The blunt version - dirtying all four neighbours
of every arriving section - is three lines and roughly doubles the mesh work of a warm join,
which collides with the join fill-in cost that is already the top open issue.

**Know:** the permissive side is water-only on purpose. A missing solid wall opens a
see-through gap at a cliff for as long as the repair takes; a missing water wall costs
nothing, because at a true frontier the edge dissolves into the sky anyway.

**Found:** 0.3.23, from a report of vertical seams between water chunks. The TODO had it
recorded as a COLOUR fault with three candidates, all of them wrong, and the experiment it
proposed (`.vhmask off`) could not have separated anything - the seams are geometry.

### G52 — A GPU-side saving measured in a CPU-bound scene reports its cost and none of its benefit

**Trigger:** comparing frame rate with and without the chunk mask, or any other change whose
saving is fragment shading rather than draw submission.

**Trap:** the mask does not simply draw less. With it off, cached terrain is suppressed
inside a plain radius; with it on, that radius is pulled in to `MaskNearFloor()` and the
per-cell mask decides per fragment instead. So the mask SUBMITS MORE cached geometry and pays
one texture fetch per fragment to throw most of it away. The saving is GPU-side and only
materialises where vanilla genuinely covers the ground.

Measure that in a scene the CPU is limiting and the benefit is invisible while the overhead
is not, so the change reads as a regression. Measured in game on 0.3.23:

| vanilla render distance | mask off | mask on | |
|---|---|---|---|
| 320 | ~315 FPS | ~310 FPS | CPU-bound: mask costs ~1.6% |
| 1024 | ~180 FPS | ~190 FPS | GPU-bound: mask gains ~5.6% |

Same build, same machine, opposite conclusions. A test run only at 320 would have condemned
the feature.

**Do:** state which resource is limiting the scene before quoting a frame-rate delta, and
measure a fragment-side change where the GPU is the limit - high render distance, vanilla
terrain filling the screen. A number without that context is not portable to another
setting, and the earlier +7.3% stationary pair has the same weakness.

**Do:** remember that the mod exists for long view distances. Where the mask costs anything
is a regime nobody runs it in, which is what makes the default defensible rather than merely
harmless.

**Know:** this compounds a standing limitation - GPU bottleneck attribution has never been
measured on this project, and CPU/render-thread phase counters (`DrawCost`, `WalkCost`) can
report a subsystem healthy while the GPU carries the cost. They cannot see a fragment saving
either.

**Know:** toggling the mask on disposes and rebuilds its texture, so an average that starts
accumulating immediately after `.vhmask on` includes the rebuild and penalises the "on" side.

**Found:** 2026-08-20, from the owner measuring both render distances instead of one.

### G53 — Two-sided or unordered opaque terrain silently spends GPU time

**Trigger:** optimizing distant cached terrain after CPU traversal and draw preparation look
healthy, especially when frame rate changes sharply with view direction.

**Trap:** opaque faces were rendered two-sided, and the mesher's vertical face winding was
not consistently outward. Simply enabling back-face culling would therefore have removed
west/east/north/south faces. After submission, quadtree/hash traversal order also gave the
depth buffer no useful near-first guarantee, so mountains and ground hidden by nearer
terrain could shade before the nearer depth existed.

**Do:** make all six solid face directions outward counter-clockwise before enabling the
engine's opaque-stage back-face state. Keep water and thin/cutout surfaces two-sided. Order
only selected opaque sections nearest-first using reusable storage and a distance computed
once per section; keep translucent water in traversal order. Neither change is true
occlusion, and neither may make visibility control residency.

**Evidence:** on the owner's machine and a fixed view, back-face culling raised 218 FPS to
260 FPS (+19.3%, about 4.59 to 3.85 ms per frame, a 0.74 ms reduction). Front-to-back opaque
submission raised 149 FPS to 173 FPS (+16.1%, about 6.71 to 5.78 ms, a 0.93 ms reduction).
The owner saw no visual difference in either comparison and specifically exercised cliffs,
caves, overhangs, high views and low views for the winding-sensitive change.

**Know:** this is strong causal evidence for wasted raster/fragment work on that machine,
not a portable benchmark. Drivers, scenes and hardware remain uncontrolled. Looking across
roughly 4,000 blocks of cached mountainous terrain still measured about 150 FPS against more
than 300 FPS when facing away, at least a 3.33 ms direction-dependent remainder and the case
future conservative occlusion must address.

**Found:** 2026-08-20, from separate owner-run `.vhbackface` and `.vhfront` comparisons.

### G54 — A high occlusion-query rejection rate can save no time

**Trigger:** a nearby vanilla hill hides kilometres of cached terrain, while the cached
renderer runs immediately before vanilla terrain and GPU work still changes sharply with
view direction.

**Trap:** same-frame `GL_SAMPLES_PASSED` boxes eventually reported 83% hidden, yet conditional
rendering changed 156 FPS to 155 FPS. The percentage counted boxes rather than work, the CPU
still prepared and submitted every real mesh, and proxy raster/query dependencies replaced
the fragment work they suppressed. More fundamentally, Vintage Horizons queried at opaque
order 0.36 and vanilla terrain drew at 0.37, so the current hill the player expected to be
the occluder did not exist in depth yet.

**Do:** put the established occluder into depth first. Cached terrain now registers at 0.38,
then ordinary depth testing rejects its fragments behind vanilla terrain without proxy
geometry or query synchronization. Re-register when toggling because the engine sorts
`IRenderer.RenderOrder` only during registration. Keep `.vhocclusion off` at 0.36 as the
overlap/compatibility fallback, and keep visibility independent from residency.

**Evidence:** post-vanilla order raised the owner's valley view from 148 to 179 FPS (+20.9%,
about 6.76 to 5.59 ms, a 1.17 ms reduction). Looking down at vanilla ground raised 590 to
651 FPS (+10.3%) but saved only about 0.16 ms because the entire frame was already below
1.7 ms. The owner saw minute distant changes only through immediate toggling and judged
them entirely acceptable.

**Know:** FPS percentage exaggerates small savings at very high rates; compare frame time.
The accepted result is one-machine human evidence, not a GPU-timer attribution or a portable
benchmark.

**Found:** 2026-08-20, from the rejected query playtest and accepted render-order A/B.

### G55 — Global invalidation can make delayed occlusion appear to work only while paused

**Trigger:** reusing an asynchronous visibility result across frames while terrain,
readiness state, or a GPU ownership mask is still changing.

**Trap:** treating every local scene update as a reason to invalidate every query prevents
the state from ever becoming old enough to suppress a draw. During active exploration,
mesh uploads and chunk-dirty/readiness events can arrive every frame. The feature then looks
healthy in a settled test area and inert in a new one. Pausing stops the update stream, so
the same view suddenly accelerates; that is invalidation churn, not pause-specific GPU
behavior. In the owner's new-area test, the enclosed view stayed near 190 FPS while running
and rose to about 500 FPS while paused.

**Do:** scope invalidation to the identity that changed. Replacing a mesh invalidates that
section's result; mask upload and chunk-dirty notification do not erase unrelated answers.
Let periodic fail-open exact-geometry probes discover changed occlusion, and reject any
pending answer whose view epoch is stale. Report accepted, stale, and globally invalidated
results separately so a zero-skip state can be attributed.

**Found:** 0.3.35, after the same feature worked in one settled area, stopped in a streaming
area, and resumed only when the game was paused.

### G56 — Keeping visibility through camera turns needs explicit disocclusion guards

**Trigger:** preserving delayed hidden results while the camera rotates so culling does not
collapse under normal mouse steering.

**Trap:** invalidating every turn is visually conservative but gives away most of the gain;
retaining every answer indefinitely produces brief missing terrain where a fast turn reveals
the side of the old view. A whole-section answer is also too coarse at the mixed
vanilla/cache ownership seam: a small cache-owned remainder can disappear because the rest
of its section passed no samples.

**Do:** fail toward drawing at known disocclusion boundaries. Never suppress a mixed-
ownership seam section. Shorten hidden probe cadence while turning, and protect only a
narrow angular band at the left/right frustum edges while rotation is occurring. Keep an
unguarded profile available to expose the limit, but make the accepted default the guarded
policy. Visibility must still not evict or rebuild the resident mesh.

**Evidence:** the owner found the unguarded extreme profile visibly distorted the edge of
the screen during very fast turns. Aggressive retained substantial performance with the
artifact nearly unnoticeable; the final turning-edge guard was judged acceptable. An earlier
occasional seam loss motivated the mixed-section bypass.

**Found:** 0.3.33-0.3.37, through owner-run seam, motion, rapid-turn, and profile comparisons.

### G57 — Changed playable artifacts require a new patch identity

**Trigger:** creating or installing a zip, mod folder, playtest package, or other playable
artifact after source changes.

**Trap:** overwriting an existing version with a different binary makes logs, screenshots,
installed archives, hashes, and player reports disagree while all claiming to be the same
build. A filename and `modinfo.json` version are artifact identity, not merely release
decoration. A hash can prove two copies match, but it cannot make two different binaries
with one advertised version understandable later.

**Do:** before packaging or installing changed code, increment the current patch component
by exactly one in both `VintageHorizons/modinfo.json` and
`VintageHorizons/VintageHorizons.csproj`. Ordinary compile/check runs do not consume a
version. Use a larger minor or major jump only when the owner explicitly chooses one. Never
overwrite an already-used version with changed binaries.

**Found:** 2026-08-21, after a local performance build reused the existing 0.3.40 artifact
identity and the owner established the smallest-increment rule.

### G58 — An occlusion benchmark needs an occluder, and an uncapped run needs cap proof

**Trigger:** comparing cached-terrain occlusion modes in a visually demanding landscape or
using an automated client mode that keeps the game watched/focused.

**Trap:** terrain variety and cache size do not establish occlusion opportunity. A high
panoramic camera can expose kilometres of cached terrain while placing essentially none of
it behind vanilla foreground, so an on/off pair measures open-horizon cost and noise rather
than the feature being toggled. Separately, watch/focus behavior can engage the game's
refresh-rate/VSync limit; a stable figure near monitor refresh is not an uncapped baseline.

**Do:** inspect the actual depth relationship from the measured camera: the target cached
terrain must project behind a nearer vanilla hill, wall, cliff or equivalent foreground.
Record the launch/watch mode and verify observed FPS is not pinned to refresh before calling
the run uncapped. Preserve a mis-targeted pair only for the narrower evidence it actually
contains.

**Evidence:** the first Bodanboys watched route held near 170 FPS and was capped rather than
an uncapped renderer baseline. The corrected aerial temporal-on/off pair ran uncapped and
produced useful stable open-horizon GPU timings, but the owner observed that essentially no
cached terrain was behind vanilla terrain; its small FPS differences therefore cannot
measure temporal occlusion.

**Found:** 2026-08-21, during the Phase 0 Bodanboys GPU feasibility routes.

### G59 — A multi-draw batch is one buffer pair, so paired allocation is a correctness rule

**Trigger:** designing regional GPU arenas, or any allocator whose spans will later be drawn
by `glMultiDraw*Indirect`.

**Trap:** grouping pages by world region alone looks sufficient and is not. One indirect
batch binds exactly one vertex buffer and one index buffer, so a section whose vertices land
in one page and whose indices land in another can never be submitted with its neighbours.
The allocator passes every test that does not draw, and the defect only surfaces when the
draw path is designed - by which time the allocator has a phase's worth of work on top of it.

Splitting a shared memory ceiling between the two arenas by the byte ratio of the geometry
fails for the same reason. Pages are created in pairs, so the arena that runs out of pages
first caps both. A 70/30 byte split against 8 MiB and 4 MiB pages let the index arena refuse
new page sets while still 40% empty, capping the mirror at 19 sets and silently limiting
what could be measured.

**Do:** allocate vertex and index pages as a set, require both halves of a section to fit
the same set, and move to another set in the region when they do not. Split any shared
ceiling by page size so both arenas afford the same page count. A half allocated while the
other fails may be released immediately rather than fenced: nothing ever referenced it.

**Found:** 2026-08-22, designing Phase 3 on top of the Phase 2 arenas.

### G60 — A measurement that cannot see what it missed will report success

**Trigger:** instrumenting a shadow or mirror that covers only part of what the real path
does, then reporting a ratio over it.

**Trap:** the natural implementation looks up each item and returns early when it is absent.
That early return is invisible: the ratio is computed over whatever the mirror happened to
hold, and the counter meant to catch it reports zero because it, too, only counts items the
mirror already has. The first arena measurement reported "0 candidates not yet mirrored"
while missing roughly half the drawn sections, and produced a flattering 6x-8x figure over
about a third of the world. Nothing in the run looked wrong.

The underlying condition was ordinary: a 256 MiB ceiling against 851 MiB of live geometry.
The instrument's silence is what made it a trap rather than an obvious limit.

**Do:** count the misses explicitly at the point of the early return, derive coverage from
hits over hits-plus-misses, and print coverage beside every ratio the measurement produces.
Treat a ratio without a coverage figure as unreported, not as an approximation.

**Found:** 2026-08-22, on the first Bodanboys GPU arena measurement run.

### G61 — Rewriting a file on Windows without naming an encoding corrupts what you typed

**Trigger:** any edit that reads a repository file, changes it in memory and writes it back
through tooling whose default encoding is the Windows locale rather than UTF-8.

**Trap:** the round trip is byte-preserving for content it did not touch - cp1252 decodes and
re-encodes existing bytes unchanged - so nothing already in the file is harmed and nothing
looks wrong. Only the characters *newly typed* in that pass are written in the wrong
encoding. One em dash becomes a lone `0x97`, the file stops being valid UTF-8, it still
opens, and `dev/DocCheck.ps1` still passes. This was introduced twice in one session, in a
gotcha heading and a TODO heading, before anything noticed.

**Do:** name the encoding on every read and write, or keep the text ASCII. `StaticAssetChecks`
now fails the fast tier on any tracked text file that does not decode as UTF-8, and names the
byte.

**Repair:** do not decode the whole file as cp1252 and re-encode - that double-encodes every
sequence that was already correct. Decode as UTF-8 with `errors="surrogateescape"`, map the
surrogate-escaped bytes back through cp1252, and re-encode. **Restrict the pass to text
files:** a repair sweep that walked every tracked file mangled `modicon.png`, which was
restored from git.

**Found:** 2026-08-22, while adding G59 and G60.

### G62 — A per-interval rate from a benchmark log means nothing without its period

**Trigger:** quoting MiB/s, uploads/s, or any per-interval figure from a run's telemetry.

**Trap:** a benchmark run is two populations. Warm-up builds every mesh the world needs;
settled steady state builds almost none. Averaging them, or sampling one and describing it
as the other, produces a number that is real but answers a different question.

Measured 2026-08-22 on the frozen `bodanboys` route: warm-up intervals 1-9 carried 1,827
mesh uploads and 2,131 MiB, about **237 MiB per 15 seconds**; settled intervals 10-26
carried 54 uploads and 35 MiB, about **2.1 MiB per 15 seconds**, several doing nothing at
all. A 113x difference between two halves of one run.

`dev/TODO.md` had carried "204-289 MiB built and 76-119 MiB uploaded every 15 seconds,
continuously, at a standstill" as the lead symptom of a re-mesh amplification bug. That
figure reproduces the warm-up column almost exactly: it was a loading measurement recorded
as a steady-state one, and it made re-meshing the leading suspect for micro-hitches it
cannot cause. The same mistake was then made in the opposite direction while investigating
it, by reading the quiet tail intervals and concluding the cost was a measurement artefact.

**Do:** print or total the whole interval series, split it at the point the rate collapses,
and say which period a quoted number belongs to. A single tail sample and a whole-run
average are both wrong.

**Found:** 2026-08-22, session 40, while confirming the change-locality saving.

### G63 — An arithmetic identity is not a mechanism

**Trigger:** an estimate that multiplies out to the observed number.

**Trap:** `dev/TODO.md` explained 5,057 mesh replacements as "~145 change events x a 35-way
fan-out", and the product matched. Neither factor was measured: nothing counted
`MarkChanged` calls at all, and the 35 assumed the neighbour fan-out repeated at every mip
level unconditionally. Adding the counter gave **38-40** change events for the same route
and profile - a number that cannot reach 5,057 even at the old five-way fan-out.

The identity survived review for a whole session because it matched. The document even said
"no counter records actual `MarkChanged` calls; add one before quoting the amplification
factor as measured" - and the factor was then quoted anyway.

**Do:** add the counter before the explanation, not after it. When a document names a term
as unmeasured, that term may not appear in a derivation that is presented as measured.

**Found:** 2026-08-22, session 40.

### G64 — Repository text files carry mixed line endings, sometimes within one file

**Trigger:** editing `.md` or `.cs` files with a script rather than by hand.

**Trap:** `LodWorld.cs` and `LodMip.cs` hold both CRLF and LF lines; `dev/TODO.md` is CRLF
and the shaders are LF. A multi-line pattern written with `
` silently fails to match a
CRLF file, and a fix-up that normalises the whole file rewrites every line, burying the
real change in an unreviewable diff.

**Do:** match and splice whole lines, taking the ending from the line already there. Never
normalise a whole file to make a patch apply. See also G61 on encoding, which has the same
shape: the round trip preserves what it does not touch, so only newly typed text breaks.

**Found:** 2026-08-22, session 40.

### G65 — Cleanup must not run through the guard that protects the work

**Trigger:** a component that disables itself after a failure and also has state to restore.

**Trap:** `LodGpuIndirectDrawer.Try` returns immediately once the drawer has failed, and the
`finally` that restored the captured GL state called it - so restoration was skipped exactly
when a batch had been refused, and the engine's own renderer runs immediately afterwards.
The happy path was perfect and the failure path silently did nothing.

**Do:** run cleanup outside the failure guard, and assert in a check that it happened after a
refusal, not only after a success. A fake backend that records the call order is enough.

**Found:** 2026-08-22, session 41, by the check rather than by a run.

### G66 — A switch that changes how the renderer decides must invalidate what it decided

**Trigger:** adding a runtime toggle that changes the draw path, the render order, or how
visibility is established.

**Trap:** delayed occlusion caches per-section "hidden" answers across frames. Batched
drawing cannot issue those queries at all, so flipping `.vhindirect` back off left the
renderer acting on answers taken seconds earlier under a different path and a different
camera. The symptom would have been terrain missing straight after switching off - read as
the fast path losing terrain, in the exact comparison the switch exists for.

**Do:** call `InvalidateTemporalOcclusionScene` whenever the path that produced the answers
changes, and decide the path once per frame before anything resolves a query. The static
check counting invalidation sites is deliberately exact: raise it with a reason rather than
relaxing it.

**Found:** 2026-08-22, session 41, by review before the build was run.

### G67 — Player-visible text is parsed as VTML, so a leading "<" is a markup tag

`.vhheight` reported its buckets as `<=4: 0%, <=16: 0%, ...`. The periodic log line was
fine, but the same string returned as a chat command result was refused outright by the
game's text renderer: `Found closing tag <font> at position 1536 but <=4:> should be closed
first`. The engine parses chat and dialog text as VTML, so `<=4:` opened an element that
was never closed and took the rest of the reply down with it. The command appeared to do
nothing.

**Do:** never let a string that can reach chat or a dialog contain a bare `<`. Write ranges
(`0-4`, `4-16`) rather than comparisons. A check pins it: the distribution's own
`Describe` output must contain no `<` at all.

**Found:** 2026-08-22, session 42, from the owner's client log after a playtest.

### G68 — The first shader load always fails, and saying so as an error hides the real one

`LodTerrainRenderer` calls `LoadShader()` from mod start, which runs before the engine has
filled its shader-include table from mod assets. The body is therefore never spliced, both
programs fail to link, and the mod logged `lodterrain shader failed to compile; LOD
rendering disabled` plus the indirect variant's warning - on every single start. The engine
then fires `ReloadShader` and the second attempt succeeds, which is why terrain drew
anyway.

The cost was not cosmetic. A session read those errors in a client log, concluded the
indirect variant does not compile on the owner's hardware, and would have spent its time on
a shader that was working. The engine's own startup-issue summary replays the same two
errors seconds later, which makes them look like two independent failures rather than one
expected one.

**Do:** treat the first shader attempt as provisional - notification level, naming what it
is waiting for - and report at full volume from the second attempt on. Log the SUCCESS once
too: a log that only ever complains cannot answer "did this work".

**Found:** 2026-08-22, session 42, while reading the owner's playtest log.

### G69 - The engine's shader bookkeeping is separate from the GL binding

**Trigger:** using `IShaderProgram.Use()` anywhere outside the ordinary draw path.

**Trap:** `ShaderProgramBase` records which program is in use in its own static state. A GL
state guard restores `glUseProgram` and leaves that record untouched, so the next
`prog.Use()` throws `Already a different shader (hzbreduce) in use!` and the client dies.
The depth pyramid did exactly this on its first frame on real hardware; every other shader
in the renderer already pairs `Use()` with `Stop()`.

**Do:** pair every `Use()` with a `Stop()` in a `finally`. Restoring raw GL state is not a
substitute. For programs the engine does not need to know about - a compute program, for
instance - create them through raw GL and never enter its bookkeeping at all.

**Found:** 2026-08-22, session 42, on the first benchmark run of the code.

### G70 - `sample` is a reserved word in GLSL 4.x

**Trigger:** naming a local in a shader.

**Trap:** `float sample = texelFetch(...)` fails with `syntax error, unexpected SAMPLE`.
It is an interpolation/storage qualifier. No offline check can catch this: the C# twin that
mirrors the shader logic carries the tests, and C# is perfectly happy with the identifier.

**Do:** avoid `sample`, `filter`, `input`, `output`, `active` and the other qualifier words
as identifiers. Treat a shader compile as something only a run can verify, and make the
failure path warn once and keep rendering rather than throw.

**Found:** 2026-08-22, session 42.

### G71 - Reading an SSBO stalls the render thread unless a fence says otherwise

**Trigger:** `GetBufferSubData` or any map-for-read on a buffer the GPU writes.

**Trap:** "it was written a frame ago" is not a guarantee the driver honours. Reading
per-section classification results every frame turned a 20 us phase into 686 us - 28% of a
2.4 ms frame - because the call blocks until the work lands. The CPU runs several frames
ahead at high frame rates, so one frame of latency is not enough either.

**Do:** fence after the dispatch (`FenceSync`), then `ClientWaitSync` with a **zero
timeout** and skip the sample when it is not ready. Never wait. Note that the skip rate is
load-dependent: the same code skipped 99.8% of frames in a light scene and 2% in a heavy
one, so report reads and skips or the statistics silently rest on a handful of frames.

**Found:** 2026-08-22, session 42.

### G72 - A diagnostic the owner cannot read is not a diagnostic

**Trigger:** adding any telemetry a person is expected to report back.

**Trap:** two owner playtests were spent producing correct numbers that could not be
retrieved. The first put them in the periodic stats block, which fires **once, thirty
seconds after joining** - before anyone has had time to enable the feature it measures. The
second put them in a chat command reply, and **game chat cannot be selected or copied**, so
the only options were retyping long digit strings or a photograph.

**Do:** write every diagnostic into the client log, line by line so each carries a timestamp
and stays greppable, in addition to whatever it shows on screen. Make it available on demand
rather than on an interval. Assume the reader will hand the log to someone else.

**Found:** 2026-08-22, session 42, twice in one session.

### G73 - A cross-check between two asynchronous measurements has a noise floor

**Trigger:** validating one visibility mechanism against another, or any "this count must be
zero" gate whose two sides are sampled at different times.

**Trap:** the depth pyramid judges last frame's depth buffer; the delayed occlusion query
reports an actual draw some frames earlier. When the camera moves, both can be right and
still disagree. Session 42 read seven such disagreements in 2,922 comparisons as a defect,
made it Phase 4's blocking gate, and spent two owner playtests chasing a number that cannot
reach zero by construction. A view-epoch guard was added on the strength of the same
reasoning and refused **zero** comparisons, which should have been the tell.

**Do:** establish correctness from a deterministic offline fixture - for this renderer, a
synthetic occluder with background around it, and boxes that poke out of it by a hair. Keep
the runtime cross-check for spotting gross divergence and for figures it is genuinely good at
(how much one mechanism finds that the other has not measured). Before gating on any count,
ask what its floor is when everything works.

**Found:** 2026-08-23, session 43, after the owner asked whether the work was really blocked.

### G74 - A box clears an occluder only by a whole texel of its own test level

**Trigger:** reasoning about why hierarchical depth culling rejects so little.

**Trap:** the pyramid level is chosen so a box spans at most N texels, so a bigger box is
tested at a coarser level. A texel that overlaps the box also covers everything within its
own footprint - 256 screen pixels at level 8 - so a box sitting inside an occluder by less
than one texel still samples the background beyond it and correctly refuses to hide. The
clearance a box needs therefore grows with the box. A synthetic fixture whose test box was
comfortably inside the occluder in pixels failed for exactly this reason.

**Do:** state the limit as clearance-in-texels, not "sections are too wide". It has two
independent levers - make the boxes smaller (cluster subdivision, which changes what is
drawn) or sample more texels at a finer level (which changes only the test). Measure both
over the same population before choosing; offline, raising the footprint from two texels to
eight found about a quarter more hidden boxes.

**Found:** 2026-08-23, session 43, from a failing offline fixture.

### G75 - A flag whose precondition lives in the shader must not be re-gated by its reader

**Trigger:** changing what a packed result bit means, and leaving the counter that reads it
where it was.

**Trap:** the classifier's wide-sampling bit was inverted so the shader set it only for
sections it HAD hidden. The C# counter still incremented it only for sections that were NOT
hidden, from when the bit meant the opposite. The two conditions became mutually exclusive, so
the figure was structurally zero whatever the terrain did - and it read as "widening the
sampling bought nothing", which is a plausible enough result to believe. It survived a whole
playtest and was only caught because the owner's log printed the zero.

**Do:** count a flag that already carries its own precondition with no gate at all, and pin the
precondition in a check next to the code that reads it. A number that can only be zero is worse
than a missing number: a missing one prompts a question.

### G76 - A status line in a report that fires once cannot observe a switch flipped later

**Trigger:** adding telemetry so a playtest reports its own configuration.

**Trap:** the client's periodic report runs once, thirty seconds after world load, unless
allocation telemetry is switched on. A cull-status line added to it could therefore only ever
capture the state before anyone typed a command, so a playtest of a default-off feature
recorded "off" every time and the run could not be attributed afterwards. This is G72 again in
a new shape: the diagnostic existed, and still could not answer the question it was built for.

**Do:** for anything a person switches on mid-session, log from the command handler itself -
`.vhhzb` already did, `.vhcull` did not. Ask what the reporting cadence is before relying on a
periodic line, and prefer event-driven evidence for state a human controls.

### G77 - An offline harness must reproduce the whole approval chain, not just the test

**Trigger:** rebuilding an in-game measurement offline so it stops costing playtests.

**Trap:** two independent faults, both of which produced clean and believable tables. Omitting
the renderer's frustum cull left the population as the whole world including everything behind
the camera, which the projection then refused as near-plane crossings - 70% of the first run's
"results". Placing each camera at the maximum surface of its own section stood the viewer on a
hilltop every time, where nothing can be occluded by anything; that run reported 7.5% hidden
against 46-88% in game.

**Do:** reproduce every CPU decision that gates the population, and choose viewpoints the way
the terrain is actually occupied - the centre column's surface, not the section's maximum. Then
validate the harness against a real in-game figure at the same settings before trusting it;
this one matched the game's 0-1k band to within a point once corrected.

### G78 - A pass that activates a shader cannot be dropped between two draw passes

**Trigger:** inserting any GL work that binds its own program into the middle of a render pass.

**Trap:** the depth-pyramid build was moved between the opaque and water passes so the picture
would contain cached terrain but not depth-writing water. The engine refuses to activate a second
shader while one is in use - "Already a different shader (lodterrain) in use!" - so the build
failed on **every frame of an entire playtest**: 0 of 4,047. It failed safe, because a frame with
no picture draws everything, which is exactly why nobody would have noticed from the screen.

**Do:** release the current program around the insertion and take it back afterwards. Uniforms
belong to the program object rather than to the binding, so the pass that follows finds its values
untouched. And count the successes: `builds completed` against `builds attempted` is what turned a
silent total failure into a one-line diagnosis.

**Found:** 2026-08-24, session 45.

### G79 - A fence asked once and then deleted throws the work away

**Trigger:** any GPU readback polled with a zero timeout.

**Trap:** the classifier fenced each dispatch, asked once on the following frame, and deleted the
fence when the answer was "not yet" - abandoning that dispatch's results forever. It read **2 of
2,212** dispatches while paying 149us a frame for all of them, six times the cost of the pyramid it
was reporting on, and most of a 30 FPS drop. G71 says to ask and never wait, which is right; it
does not say to discard.

**Do:** keep the fence and ask again next frame, and do not start another dispatch while a result
is still outstanding. That alone makes the pass self-throttling at the readback rate. Report reads
against skips so a pass that is collecting almost nothing says so.

**Found:** 2026-08-24, session 45.

### G80 - Timing a pass that did not run dilutes the number it exists to produce

**Trigger:** a delayed GPU timer wrapped around work that can be skipped.

**Trap:** the pyramid timer opened and closed around builds that never happened - a minimised
window, a refused depth attachment - filing a near-zero sample each time. The client reported
**4.8us over 256,368 timed builds when 50,733 had actually run**, dragging a genuine 24us down
fivefold. That figure is the gate for the whole phase, and it was wrong in the flattering
direction.

**Do:** close the query but discard its result when the work did not happen, and print the sample
count beside the average so the two can be compared. A ring already tracking a measurement epoch
can discard by stamping the slot stale, which reuses a path that is already tested.

**Found:** 2026-08-24, session 45.

### G81 - A resource ceiling that silently refuses turns every measurement into a measurement of the ceiling

**Trigger:** any fixed pool the renderer falls back from gracefully.

**Trap:** the GPU arena's fixed 256 MiB refused 322 sections and left **35% of drawn terrain**
outside the batched path. Everything still drew, correctly, by the slower route - so the only
symptom was that batching and depth culling could not affect two thirds of the screen, and two
sessions of A/B comparisons through it measured the pool rather than the feature. Raising it took
coverage to 100% and the owner from 300 to 480 FPS.

**Do:** size the pool from the setting that determines the work, not from a constant, and check
the refusal counter before trusting any comparison taken through it. A ceiling that is a cap with
demand-driven commitment costs nothing when generous, so there is no reason to run one tight.

**Found:** 2026-08-24, session 45.

### G82 - Never infer where on screen a visual defect appeared

**Trigger:** the owner reports terrain flickering, vanishing or tearing.

**Trap:** a previous-frame depth test produced flicker while the camera turned. The obvious
mechanism is screen-edge clamping - the projection clamps a box's rectangle to the visible part
and applies that verdict to the whole section - and a guard was designed, built, checked and
shipped on that theory. The owner had seen the failures in the **middle** of the screen, and said
so as soon as the assumption was stated. The guard was aimed at the wrong place entirely, and the
inference had also been used to argue the underlying design was salvageable.

**Do:** ask where on the screen, at what distance, and whether it recovers on its own, before
proposing a mechanism. He answers precisely and immediately; the question costs one exchange and
this one would have saved a build, a guard, a set of checks and a wrong conclusion about the
design. Keep an exact-angle capture view-wide and label projected regions. Do not require the
defect under the crosshair: the angle that exposes it generally points the crosshair somewhere
else. In the current Phase 8 case, most affected meshes are inside visible terrain rather than at
the sky silhouette.

**Found:** 2026-08-24, session 45.

### G83 - A model that agrees with a sparse world is not validated by it

**Trigger:** checking a capacity or coverage model against this machine's caches.

**Trap:** the arena sizing model predicted 560 sections at the owner's draw distance and his
client reported 580 resident, which was written up as validation and into a check. It was the
opposite: his world is a partly-explored corridor rather than a full circle, so a model that
matches a **sparse** world's residency necessarily falls short of a populated one. The same
mistake had already been made once in the same session, reasoning about how much Phase 6 would
buy from band populations that a grown world would not have.

**Do:** state what the model counts and what the measurement counted before comparing them. Every
cache on this machine is far-band starved - the most developed covers 16,384 x 12,288 blocks at L6
- so no local measurement can confirm a model of a mature world, and the honest move is a margin
with the reason recorded rather than a number presented as derived.

**Found:** 2026-08-24, session 45.

### G84 - An std430 `uvec3` array does not have a twelve-byte stride

**Trigger:** packing three 32-bit words into an SSBO record.

**Trap:** the struct looks twelve bytes wide in source, but std430 arrays round a three-component
vector's stride to sixteen bytes. Using `uvec3[]` would add 33% to the packed arena while the CPU,
tests and telemetry continued calling the format twelve bytes.

**Do:** pull the buffer as scalar `uint[]` and index three words per record, or explicitly accept
and report a sixteen-byte GPU format. Pin the shader declaration as well as the CPU constants.

**Found:** 2026-08-24, session 46.

### G85 - A shared compute command layout constrains a new indirect draw type

**Trigger:** changing from indexed indirect draws to draw-arrays indirect draws while reusing a
compute cull that edits command slots.

**Trap:** `DrawElementsIndirectCommand` is five words and `DrawArraysIndirectCommand` is four.
Packing the latter naturally changes every later slot address, while the cull shader still writes
`commands[index * 5 + 1]`. The result is suppression editing the wrong commands.

**Do:** retain the five-word stride and pad draw-arrays commands until the producer, compute shader,
draw call and deterministic layout checks are deliberately migrated together.

**Found:** 2026-08-24, session 46.

### G86 - Geometry-format savings are not process-memory savings during dual publication

**Trigger:** validating a packed format beside the established representation.

**Trap:** twelve bytes versus eighty-eight is an 86.4% representation reduction, but publishing
both regional forms temporarily increases total arena memory, and legacy meshes remain resident
for fallback. Reporting the format ratio as total memory saved would claim a benefit the process
does not yet realize.

**Do:** report packed and expanded regional bytes separately during the A/B. After visual and timing
acceptance, stop retaining the expanded regional copy on the selected product path; measure total
process memory separately.

**Found:** 2026-08-24, session 46.

### G87 - Packing bytes can lose if it increases unique vertex work

**Trigger:** replacing indexed mesh vertices with procedural vertex pulling.

**Trap:** 0.3.85 reduced each regional opaque quad from 88 bytes to 12 and matched the picture, but
used draw-arrays expansion for six full shader invocations. The accepted indexed mesh shades four
unique corners and reuses two of them. On the primary driver the same scene fell from about 300 to
260 FPS—roughly 0.51 ms or 13% slower—so the bandwidth win did not pay for 50% more decoding.

**Do:** compare unique post-transform vertices, not only stored bytes or triangle vertices. For
packed quads, keep one reusable `0,1,2,0,2,3` index pattern and rebase four virtual corners per
record; retain the expanded path until total frame time proves the replacement.

**Evidence:** 0.3.86 made exactly that topology change. In the owner's same scene it remained
visually identical and restored packed performance to the exact same FPS as expanded batching.
The result isolates the extra unique vertex work as the 0.3.85 regression and establishes that the
12-byte decoder itself is neutral in this view; it does not establish a standalone FPS gain.

**Found:** 2026-08-24, session 46.

### G88 - Strict depth inequality is not a conservative occlusion verdict

**Trigger:** an HZB test compares a projected box's nearest depth with rasterized scene depth,
especially after one section becomes several independently culled clusters.

**Trap:** projection and triangle rasterization reach depth through different floating-point and
fixed-point steps. Treating every `nearest > farthest` result as hidden lets a representable rounding
difference cancel a coplanar draw. The failure looks like a normally shaped terrain piece flickering
at a very precise camera angle, including while stationary. A 4x4 cluster path multiplied the number
of independent decisions and made several more pieces show it; `.vhcull off` stopped all old and new
cases while clusters remained enabled, clearing the geometry ranges.

**Do:** reserve an explicit fail-open depth band and pin it in the CPU reference and shader. Start
from the actual depth representation rather than an arbitrary world-space distance, then measure
how much valid rejection remains. A pre-existing offline fixture reproduced the exact one-ULP
self-occlusion and should remain the regression case. A borderline verdict draws.

**Found:** 2026-08-24, Phase 8 owner playtest.

### G89 - A dependent experiment needs one authoritative switch

**Trigger:** a player-facing A/B requires several feature flags whose meaning depends on each
other.

**Trap:** Phase 8 accumulated seven controls as separately measurable stages: arenas, batching,
packed geometry, HZB, culling, same-frame cached depth, and clusters. A 0.3.88 follow-up looked like
the conservative depth margin had erased performance, but the log showed late depth initially off
and clusters `on, but idle` because packed drawing was off. Every individual command behaved as
documented; the experiment as a whole was still invalid, and asking the owner to maintain the
dependency graph was the defect.

**Do:** expose one master command that changes every prerequisite atomically, reports `ON`, `OFF`,
or `MIXED`, and logs the same state. Keep component commands for diagnosis, not ordinary setup. A
performance conclusion taken from a mixed state does not count.

**Found:** 2026-08-24, 0.3.88 Phase 8 follow-up.

### G90 - One command does not mean one experimental variable

**Trigger:** a master command spans several stages that were built and measured separately.

**Trap:** `.vhphase8 on|off` fixed hidden operator state, but it collapsed batching, HZB culling,
same-frame cached depth, packed geometry, and clusters into one comparison. When the complete stack
flickered and lost to legacy, that result could not identify which later addition erased the
substantial wins measured earlier. The state was valid; the experiment was too broad.

**Do:** keep one authoritative command, but give it named cumulative presets in implementation
order. Every preset must assign every dependent flag, not merely enable its new feature. Compare
adjacent settled presets and record the first visual or performance regression; only then narrow to
component diagnostics.

**Found:** 2026-08-24, 0.3.89 complete-stack owner comparison.

### G91 - An idempotent-looking preset can restart expensive publication

**Trigger:** a preset assigns the same enabled resource state already requested.

**Trap:** `RequestGpuShadow("on")` is intentionally an active rebuild request, not a harmless
boolean assignment. The first preset ladder called it for every active stage, so selecting `late`
while arenas were already on queued all 1,678 live sections for re-mesh. The flags were correct,
but the FPS comparison measured warm-up and every adjacent step would have repeated it.

**Do:** compare the requested resource state before invoking transition methods. Cumulative presets
may freely assign cheap feature flags, but they must preserve shared filled resources until the
preset actually crosses their ownership boundary. Log queued rebuild counts and treat any nonzero
unexpected count as a contaminated performance interval.

**Found:** 2026-08-24, first 0.3.90 preset-ladder test.

### G92 - A demand-driven producer cannot require its own first product

**Trigger:** bootstrapping persisted terrain through the ordinary render-selection walk.

**Trap:** stored cache rows register only key metadata, while the renderer skips selection until at
least one mesh exists. Selection is the path that requests meshes; mesh scheduling is the path that
starts asynchronous loads. With an empty dirty set the cycle produces no work at all. On a
5,317-section cache it logged 0 dirty/loads/mesh jobs at ten and thirty seconds, then waited 75.1
seconds for newly captured data to create the first mesh accidentally.

**Do:** give key-only persisted state an explicit bounded bootstrap planner that runs without an
existing mesh. Reuse async loading and all owning-thread budgets; do not mistake a zero-work period
for a throughput problem or solve it by raising queue limits.

**Found:** 2026-08-24, ordinary 0.3.90 join and source trace.

### G93 - Frustum culling before demand makes the camera a load switch

**Trigger:** combining visibility traversal with load/mesh request generation.

**Trap:** `CollectDrawNodes` rejects an out-of-frustum subtree before `RequestMesh` or child descent.
Residency is correctly independent of visibility, but demand is not: terrain behind the camera
remains unloaded or coarse until the player turns to look at it. A nearest-first scheduler cannot
repair obligations it was never given.

**Do:** keep the frustum authoritative for drawing, not for whether persisted coverage/refinement
can ever become eligible. Make orientation absent from demand ordering; service radial lanes fairly
and let turning change only which already-planned terrain is drawn.

**Found:** 2026-08-24, owner loading report and source trace.

### G94 - Appearance readiness can lag behind data readiness

**Trigger:** making persisted terrain load and mesh earlier than its client-only tint palette.

**Trap:** the first seasonal refresh can complete before any cache palette has registered its
grass, foliage, or water slots. New slots default to identity white, but a time-only 30-second
cadence treats the table as current. Geometry then appears promptly with visibly wrong colours and
snaps to the correct climate/season palette at the next refresh. Faster loading exposed the race;
it did not create the colour error.

**Do:** make newly registered appearance state invalidate the cadence immediately, keep its work
incremental and atomically published, and retain each affected mesh obligation until every tint it
uses is ready. Data-resident is not necessarily reveal-ready.

**Found:** 2026-08-24, first accepted 0.3.93 radial-loading playtest.

### G95 - A verdict transition is not necessarily a draw-state transition

**Trigger:** ranking commands that alternate among several diagnostic verdicts in a GPU cull
capture.

**Trap:** `visible` and `background` are different explanations but both leave the command drawn.
The 0.3.99 capture ranked 6,592 of those harmless transitions ahead of 8,666
`occluded/background` transitions that actually switched drawing off and on. A plausible sky-edge
story then dominated the investigation even though the owner's visible failures were mostly
inside terrain.

**Do:** define the externally observable state first. Count explanatory verdict changes for
context, but rank only cull/draw transitions; separately track whether a command identity appeared
or disappeared before compute. Capture every upstream bucket that can change the downstream depth
picture, and retain stable identities and screen regions so the report can be matched to the
owner's observation.

**Found:** 2026-08-24, 0.3.99 flicker capture and 0.3.100 diagnostic correction.

### G96 - A mip texel covers 2^level pixels, not one over the level's texel count

**Trigger:** converting a screen-space rectangle into texel indices at a chosen pyramid level.

**Trap:** mip dimensions halve with floor, and the reduction folds any leftover odd row or column
into the LAST texel, so a level-L texel stands for exactly `2^L` source pixels with the final texel
absorbing the remainder. Scaling normalized bounds by the level's texel COUNT assumes an even
`1/count` share instead, and the two agree only while `levelSize * 2^level == screenSize`. The
computed index is never larger than the true one, so the far edge of the sampled rectangle falls one
texel SHORT - and the texel dropped that way is the one holding whatever lies beyond an occluder's
silhouette. Terrain peeking over a ridge was judged hidden, which the conservatism rule forbids
outright.

**Work the arithmetic for the real display, not an assumed one.** The drift begins at the first
level whose size has gone odd, and the two axes generally differ. On the owner's 1920x1080 the
height chain reaches 135 and then 67, so `67 * 16 = 1072` against 1080 and every level from **4**
up is affected; the width chain reaches 15 and then 7, so levels **8** and above are affected too.
Sessions 50 and 51 wrote this up for an assumed 2560x1440, where the onset is level 6 vertically and
width happens to divide exactly - which understated how much of the pyramid was involved. A
dimension that does divide exactly shows nothing at all, so a single tested resolution can hide the
fault completely.

**Do:** anchor the rectangle in screen pixels first, then shift right by the level, then clamp the
far edge to `levelSize - 1` - that clamp is what honours the fold, because the leftover pixels
genuinely live in the last texel. Expect a box to touch one more texel than the level was sized for
and keep any span guard strictly-greater-than, or the anchoring converts real culls into fail-opens.

**Found:** 2026-08-24, session 50, the cause of the Phase 8 precise-angle flicker. Resolution
arithmetic corrected 2026-08-25 from the owner's log, after two write-ups assumed the wrong display.

### G97 - A reference implementation that mirrors a shader cannot catch a shared assumption

**Trigger:** relying on a CPU twin of GPU logic, deliberately written statement-for-statement, to
prove that logic correct.

**Trap:** the arrangement is designed to keep the two identical, so it detects divergence and
nothing else. G96 lived in both copies at once and 5,073 assertions passed over it for several
sessions, because every check compared the test against its own twin rather than against the
artifact the test is supposed to describe. The pyramid's real texel footprints were never tied to
the projection's arithmetic anywhere.

**Do:** make at least one check cross the boundary - build the other artifact for real (reduce an
actual pyramid at an actual screen size) and assert the verdict, not the intermediate arithmetic.
Choose dimensions that are hostile rather than convenient: a size that divides exactly at every
level cannot expose an alignment fault. And when a fix is proposed for a defect a mirror missed,
run the fixture against the UNFIXED code first; a fixture that does not fail before the change is
not evidence that the change was needed.

**Found:** 2026-08-24, session 50.

### G98 - A figure measured over randomly placed cameras carries seed noise; quote the spread

**Trigger:** citing a single percentage produced by an offline harness that samples camera
positions and yaws.

**Trap:** `HzbField`'s widening result was recorded as "eight hides 25.0%" and quoted that way for
sessions, including inside a source comment justifying a shipped constant. Re-running it on
2026-08-24 gave 18.4% and 25.6% from two camera seeds under otherwise identical conditions - a
seven-point spread from the seed alone, larger than the effect of the 0.3.101 mapping correction or
of a 350-versus-192-block vanilla distance. The decimal place implied a precision the measurement
never had, and a later session could easily have "detected a regression" that was only a different
seed. The same run also showed the recorded "sixteen adds about 2.5 points" was really 3.9 to 5.4.

**Do:** run at least two seeds and report the range, not a single value. Prefer the property that
is stable across seeds - here the ORDERING of wide-4, wide-8 and wide-16, which held in every run
and is what the choice of eight actually rests on - and gate decisions on that rather than on a
midpoint. When quoting such a figure in source or docs, name the seed, the cache and the modelled
view distance beside it, because all three move it. See also G73 on cross-checks whose noise floor
is above zero by construction.

**Found:** 2026-08-24, re-measuring the widening figure after the 0.3.101 texel-mapping fix.

### G99 - Shipping ahead of a validation gate does not complete the gate

**Trigger:** a feature is made the product default on an explicit owner decision before its plan's
hardening or portability matrix is complete.

**Trap:** later summaries naturally treat "shipped and accepted" as "the plan finished." That
silently converts unrun checks into implied evidence, or lets a couple of visible evidence gaps
replace the actual remaining phase. Retiring one of those checks later has the same risk: retirement
removes work from scope; it does not make the result portable, measured, or passed.

**Do:** mark every affected gate explicitly as PASSED, RETIRED, or OPEN. Preserve the evidence
boundary for retired checks, and keep the phase open until its remaining work is completed or the
owner explicitly retires the phase itself.

**Found:** 2026-08-25, session 54, when the accepted 0.4.0 default obscured the still-open Phase 9
hardening boundary.

### G100 - A lower development version cannot outrank a preserved higher test zip

**Trigger:** returning a development line from a premature minor promotion to the next patch on
the earlier minor line while copy-only rollback artifacts remain in the normal Mods folder.

**Trap:** Vintage Story selects the highest version. Copying 0.3.104 beside the preserved 0.4.0
looks like a successful install but an ordinary launch still loads 0.4.0, so any observation would
be attributed to the wrong binary. The repository rule correctly forbids silently deleting or
moving the rollback zip; version demotion and ordinary Mods-folder selection are therefore
incompatible without a separate explicit owner decision.

**Do:** run the lower version through the repository's isolated add-mod-path runner, which selects
the current build independently. If ordinary play must select it, ask the owner explicitly what to
do with the higher rollback artifact; never hide that operation inside packaging.

**Found:** 2026-08-25, session 55, after packaging and copy-installing 0.3.104 beside 0.4.0.

### G101 - A dependent feature can invalidate a format-only comparison

**Trigger:** comparing packed and expanded geometry while leaving clustered drawing enabled.

**Trap:** clusters consume packed records. Turning packing off therefore makes clusters idle too,
so the pair changes both geometry decoding and command granularity. The results may look stable and
still be incapable of answering the format-cost question.

**Do:** pin dependent stages to the same effective state in both halves. The valid Phase 9 ABBA
matrix held clusters off and changed only packing; record effective-path telemetry beside requested
switches.

**Found:** 2026-08-25, session 56.

### G102 - A completed benchmark can still have silently empty telemetry

**Trigger:** a log sentence evolves while the benchmark parser still matches its older shape.

**Trap:** route timing and CSV publication can succeed even when GPU timer and arena arrays are
empty. Treating completion as parser validation loses the exact evidence the run was meant to
collect.

**Do:** assert expected sample cardinality before interpreting a run, and preserve the raw log so a
parser correction can recover it. The Phase 9 runner now understands split-near/split-far timers,
upload tails, and per-format live/committed arena bytes.

**Found:** 2026-08-25, session 56.

### G103 - An aggregate bound over descendants is redundant where a per-item bound already exists

**Trigger:** adding a bound that unions over children when each child already carries its own.

**Trap:** at a leaf the aggregate IS the item's own bound, and usually a looser version of it - the
subtree box unions the passes the per-section box splits. A looser box can only reject a subset of
what the tighter test already rejects, so the new work is redundant exactly where it fires most
often. Session 57 measured three of five views rejecting only single-mesh nodes, which removed
draws the per-section test was already removing.

**Do:** establish that the new bound is tighter than the existing one somewhere that matters before
funding it, and measure the cost of the thing being optimised first. The quadtree walk this was
aimed at costs 44.3 us of a roughly 2000 us frame, so 2% was the whole ceiling.

**Found:** 2026-08-26, session 57.

### G104 - Assuming unknown space is open makes it a source, not just a hole

**Trigger:** any flood, light or spread where absent data is resolved toward "open" for safety.

**Trap:** open air is also an emitter. In session 57 the cave rule seeded daylight from the edge of
its working window and from absent neighbours, both of which are the conservative choice - and both
became false illuminants that rescued caves the rule existed to remove. A window margin equal to
the light's own reach put its wall exactly in range, and a mesh job with no diagonal neighbours had
four section-sized blocks of imaginary sky against its corners. Together they were the difference
between removing 15.5% of the geometry and 32.7%.

**Do:** keep an assumed-open boundary further away than the effect can travel, and supply real data
for every direction the effect can arrive from - including diagonals, which the mesher never needed
and the flood does.

**Found:** 2026-08-26, session 57.

### G105 - A harness built beside the feature it measures can agree with it and still be wrong

**Trigger:** validating an optimisation against an offline model rather than against the game.

**Trap:** session 57's cave harness and the shipping code agreed with each other across every
sample, level and configuration, and both disagreed with the game by SIGN: the harness measured a
third of the geometry removed, the game measured 2.9% added. Two rounds were spent trusting the
harness over the owner's report.

**Do:** make the harness call the shipping function rather than a model of it (`cavefield
--shipping`), and treat any harness that has never been reconciled against an in-game number as an
unvalidated instrument. When a person's observation contradicts a measurement, instrument the
place they are standing.

**Found:** 2026-08-26, session 57.

### G106 - A direction-sampled visibility test must be shown to converge before it is quoted

**Trigger:** deciding what is visible by testing a finite set of ray directions.

**Trap:** too few directions miss real sight lines and over-remove; denser lattice directions that
step more than one cell per move let sight slip through one-block walls and under-remove. Session
57 measured 51.1%, 42.4% and 38.9% over 26, 98 and 290 directions - the two errors bracket the
answer rather than converging on it, and any single figure from that curve is an artefact of its
sampling.

**Do:** use traversal that visits every cell a line passes through, and demonstrate the answer
stops moving as the direction count rises before quoting it. Diffuse propagation is the safe
fallback: a straight path is one of the paths a spread takes, so a budgeted flood can never remove
something a straight ray of the same length could have reached.

**Found:** 2026-08-26, session 57.

### G107 - Post-transform geometry must use one effective coverage view on both sides

**Trigger:** transforming a section transiently for one mesh build while adjacent coverage still
comes from canonical snapshots.

**Trap:** Session 57's cave pass rebuilt the current section with hidden air filled, but vertical
face collection read the old `MeshJob.Self` and immutable neighbour snapshots. Every filled column
therefore saw the cave air that used to be beside it and emitted a wall; multi-column cavities
became lattices of invented faces and cave culling added 2.9% geometry in game. A harness that
pre-filled before meshing concealed the ownership split.

**Do:** one operation must own one coherent effective-coverage result. Session 58's
`LodCaveCull.Prepared` supplies both the rebuilt self and matching immediate-neighbour boundary
classification; the shipping harness enters through `LodMesher.BuildMesh`. Pin internal and
cross-section cases. The human-proven result changed the same 1,730 meshes from 88.6M to 66.4M
opaque vertices instead of 91.1M.

**Found:** 2026-08-26, session 58.

### G108 - A bounded fail-open window cannot prove a large component globally enclosed

**Trigger:** deciding whether an air/fluid component is sealed from a local window whose unknown
edge is treated as exterior.

**Trap:** a globally sealed cave network that enters two sides of the window is locally
indistinguishable from a useful through-tunnel into unknown space. Recentring the same window for
every section does not add knowledge: a component larger than the window can escape every local
decision. Increasing light reach changes the band but does not prove global enclosure.

**Do:** either retain such components and state the limitation, or classify connectivity across
section boundaries with explicit unknown-frontier state. Real surface portals must be distinct
from the boundary of current knowledge. Add retained-by-unknown telemetry before arguing which case
dominates.

**Found:** 2026-08-26, session 58, after 0.3.115 left large dry underground networks.

### G109 - A one-cell bridge fixture overstates pruning value in volumetric terrain

**Trigger:** choosing a graph-pruning algorithm from a narrow synthetic tunnel and dead branch.

**Trap:** bridge-tree pruning removes a branch only when one graph edge is its unique connection.
Natural caves are wide: adjacent columns create parallel routes and rooms create loops, so even a
dead-looking branch often belongs to a non-bridge component and is retained. Contracting all dark
heights in one X/Z column can create additional conservative shortcuts inside vertically complex
connected networks. The fixture passes while real-cache and live gain remain nearly unchanged.

**Do:** measure reason/topology distributions in real data and include wide branches, loops,
stacked cave layers, rooms, and complex mouths in fixtures. A passing minimal topology test proves
correctness for that shape, not expected value in worldgen.

**Found:** 2026-08-26, session 58. The 0.3.115 refinement added only about 0.3 percentage points in
the owner's settled scene.

### G110 - Water-filled space is geometry, not air, in the LOD snapshot

**Trigger:** expecting an air-cavity fill to remove flooded caves.

**Trap:** capture reads `BlockLayersAccess.FluidOrSolid`; water is a stored run marked
`FlagWater`, and the cave occupancy builder treats every run as occupied. A flooded chamber
therefore contains no dark air for the culler to replace. Partially flooded caves can lose some air
while water geometry and the terrain faces visible through it remain. Treating water as plain air
would instead let daylight and connectivity leak through oceans.

**Do:** classify fluid components separately. Keep anything connected to an exposed ocean/lake,
real opening, or unknown frontier; only a fully enclosed known fluid component is eligible for
absorption or internal-geometry suppression. Never borrow water as an opaque cave fill.

**Found:** 2026-08-26, session 58.

### G111 - A large fail-open bucket is not the same as a recoverable one

**Trigger:** seeing one conservative rule account for a large share of retained geometry and funding
a fix aimed at that rule.

**Trap:** Session 59's first global prototype kept 29.5% of estimated subterranean geometry as
"unknown frontier", which looked like the obvious target. Replacing component-granularity unknown
handling with pseudo-portals - the stance the shipping rule already takes at its own window wall -
deleted that verdict entirely and gained **0.2 percentage points**. The geometry moved intact into
route retention, which rose from 9.3% to 34.2%, because those components were never undecidable
*because* they touch the frontier: they are colossal networks that also carry many genuine surface
mouths. Removing one excuse simply handed them to the next rule.

**Do:** attribute retention exactly before funding a fix, and check where a bucket's contents would
*go* if the rule were relaxed. Run the classification twice - once with the rule, once without - and
charge the difference, rather than inferring which rule mattered from the size of its share.

**Found:** 2026-08-26, session 59.

### G112 - Keeping every route between entrances keeps deep interior, not passages

**Trigger:** preserving connectivity between cave mouths by retaining the graph structure that joins
them.

**Trap:** a natural cave system is overwhelmingly one 2-edge-connected blob (G109), so "keep every
route between two entrances" keeps its entire core, including regions hundreds of blocks from any
mouth that no sight line could ever cross. Measured over the owner's real cache this held 34.2% of
estimated subterranean geometry - more than the shipping rule removes in total - while the branch
peel that was supposed to trim it shed only about 5% of what it examined.

**Do:** bound routes geometrically, not just topologically. Keeping a route span only within a slack
distance `W` of a shortest mouth-to-mouth path cut route retention to 3.6% and roughly doubled total
removal to 62.9% at reach 32, with the curve flattening below `W` of about 32 - one light-reach of
slack. A cycle does not fool a distance test the way it defeats a bridge test. Note the cost: this
is the one rule in the family that can remove geometry a player could actually see, so it needs
human visual acceptance, not only a geometry total.

**Found:** 2026-08-26, session 59.

### G113 - Daylight reach is bounded by the local window's margin

**Trigger:** raising the cave rule's light reach to protect deeper terrain, such as the underside of
a large overhang.

**Trap:** `LodCaveCull` treats its 3x3 window's outer wall as open sky so the unknown fails open,
which makes that wall a light *source*. The margin is one full section - 64 columns at L0 - so the
reach must stay under it. At reach 64 wall light arrives at the centre-section boundary with exactly
zero budget and is rejected only because the flood discards non-positive values; at reach 128 it
penetrates 64 columns into the centre section and lights all of it. The failure is not visual damage
- false light keeps geometry - but the saving disappears entirely, which is the same effect Session
57 measured as the difference between removing 15% and about 30%.

**Do:** treat reach and margin as one coupled constraint. Keep any mesh-time reach at 64 or less at
L0, and satisfy a larger reach by moving classification out of the window rather than growing it:
the workspace is three arrays over `(grid + 2*margin)^2 * worldHeight` per mesh thread, so a margin
that matches a 128-block reach costs about 2.8x the memory on every mesh thread. Sections without a
classification should simply not be culled. Session 60 measured the accepted surface-only rule on
the same 40-section sample: reach 64 removed 26.88% of 1,554,740 estimated vertices, while reach 96
retained 20,276 more (1.30% of baseline) and reach 128 retained 73,996 more (4.76%). The owner chose
64.

**Found:** 2026-08-26, session 59, when the owner directed reach to expand for overhang safety;
quantified and closed 2026-08-27, session 60.

### G114 - A long visible tunnel can have no exterior seed inside a local window

**Trigger:** preserving a straight passage only when a surface mouth or exterior seed is present in
the current mesh job's bounded data window.

**Trap:** the middle section of a long straight tunnel through a mountain contains neither mouth.
The complete passage is visible from outside, but every local cell looks like part of a sealed
corridor, so a seed-requiring sight pass plugs the middle. A fixture that puts a mouth inside the
centre or neighbour section proves only a short tunnel and misses the actual ambiguity.

**Do:** when a solid-free lattice line crosses the complete available window, fail open and retain
it even if its real mouths are beyond the snapshots. Put both fixture mouths outside the 3x3 window
so the test exercises the same state as the failing middle section. Record the unavoidable cost: a
globally sealed, perfectly straight corridor crossing the local window can also survive. Only a
global classifier can distinguish the two, and the owner accepted the conservative local choice.

**Found:** 2026-08-27, session 60, after version 0.3.118 plugged the owner's intentionally straight
east-west mountain tunnel; fixed in 0.3.119.

### G115 - An exact sightline is thinner than the visible passage around it

**Trigger:** retaining only cells touched by the mathematical line that proves a tunnel visible.

**Trap:** the open core survives, but a one-block floor hump or nearby wall irregularity can sit
just outside the ray and be filled into the tunnel. The visibility proof is correct while the
resulting tunnel shape is visibly wrong.

**Do:** grow a small fixed clearance volume from the proven ray, stop expansion at solid terrain,
and measure the geometry cost. Never use the halo as an unbounded flood or seed it from ordinary
daylight, because either change restores hidden networks. Session 60's accepted four-block,
all-26-direction halo retained about 2,250 estimated vertices on a 1,554,740-vertex real-cache
sample (0.15%) and had effectively unchanged paired harness wall time.

**Found:** 2026-08-27, session 60. Version 0.3.120's one-block vertical guard was deliberately
superseded by 0.3.121's four-block all-direction clearance.

### G116 - PowerShell archive inputs can flatten a mod's asset tree

**Trigger:** replacing `scripts/package.sh` on a Windows host without Bash by recursively
enumerating files and passing their full paths individually to `Compress-Archive`.

**Trap:** the resulting zip can contain the correct files and root DLL while flattening
`assets/vintagehorizons/shaders/...` into root-level filenames. Its entry count and manifest look
plausible, but Vintage Story cannot resolve the shader assets. Installing it would turn a packaging
substitution into a broken playable artifact.

**Do:** archive from the assembled mod directory as a directory tree (without a wrapping folder),
then inspect the zip before installation. Require the DLL and manifest at the root, shader entries
under their exact `assets/vintagehorizons/...` paths, the licence present, no PDB, and matching
source/destination hashes. Session 61 caught and replaced the flattened 0.3.124 candidate before it
left `dist/`.

**Found:** 2026-08-27, session 61.

### G117 - A shared distance uniform does not make every shader part of the terrain wall

**Trigger:** removing Vintage Story's render-distance fog wall for extended terrain.

**Trap:** an official asset search finds radial `viewDistance` expressions in eight vertex programs,
but only five belong to the terrain renderer. `standard`, `instanced`, and `entityanimated` control
general objects, instanced content, and entities; moving their fades changes behavior outside the
terrain wall. The terrain programs are `chunkopaque`, `chunktopsoil`, `chunktransparent`,
`chunkliquid`, and `chunkliquiddepth`. Their earliest distance effect begins at 72.5% of the
uploaded distance. Raising the saved client view distance changes streaming and culling, patching
every `ShaderProgramBase.Uniform` upload inserts Harmony into a high-frequency path, and copying
engine shaders creates brittle versioned assets.

**Do:** intersect shader evidence with the official renderer call sites, then use an exact
terrain-only allowlist. Rewrite `viewDistance` at low-frequency shader activation plus the
liquid-depth program's explicit late setter. Derive the replacement from the real cull radius,
outer chunk vertex, camera offset, and earliest terrain-fade ratio; do not tie it to distant-cache
reach. Leave atmosphere, the saved setting, engine visibility range, general/entity fades, and
`viewDistanceLod0` alone. Fail open per pass, diagnose once, make hooks inert before exact-ID
unpatching, and verify reload/deferral/disposal lifetimes.

**Found:** 2026-08-27, session 62; corrected and narrowed by sessions 63-64.

### G118 - A terrain-threshold wall is not atmospheric fog

**Trigger:** a request to remove a distant "fog wall" or white render-radius circle.

**Trap:** the phrase describes the visible symptom, not permission to change every fog-like effect.
Ordinary clear-air haze and weather atmosphere are intentional world rendering. A terrain-edge fade
can produce a pale circular boundary at the draw threshold without making ambient haze itself a
defect. Removing both may look pleasing in one scene while silently changing the product beyond the
owner's request.

**Do:** write the product boundary before touching source. Preserve the complete ambient result
unchanged and independently identify only the official terrain-threshold mechanism. Verify the
missing radius and unchanged atmosphere as two separate acceptance claims. If a broader effect was
already shipped, preserve its release history but reopen a correction rather than retroactively
calling the broader behavior accepted.

**Found:** 2026-08-27, session 63, when the owner rejected 0.3.125's disclosed clear-air haze
subtraction. Corrected and human-accepted in 0.3.127, session 64.

## Reversals and disproved claims

### R1 — Compression and SQLite writes do not belong on the render/game thread

Earlier synchronous save batches measured roughly 10–22 ms average and about 49 ms peak. Compression and writes were moved to `LodStorageThread`; only immutable snapshot creation remains on the owning thread.

Do not reintroduce inline serialization for convenience.

### R2 — Render-demand cache reads should not block a frame

Inline demand loads measured multi-millisecond averages and peaks near 30 ms. Render-driven reloads now go through the storage worker and publish later. Release 0.2.1 also changed cold-cache capture so results wait in order for a background reload; the prior path measured as high as 113 ms on the game tick.

Do not reintroduce synchronous decompression in either path.

### R3 — Draw-time frustum rejection alone is not a complete renderer scalability strategy

Draw-time culling reduced submitted draw calls, but it does not prevent whole-tree traversal, off-screen mesh requests, dirty-set scans, or far-plane scans. Preserve the measured draw win while adding visibility-aware traversal with residency kept separate.

### R4 — Silently dropping server requests is never backpressure

The server previously dropped requests when its cache was unavailable or a queue was full. Because clients retained the in-flight markers, enough silent drops stopped all later terrain transfer. Explicit refusal is the settled behavior.

### G119 - A stored palette colour packs red in the LOW byte

**Trigger:** feeding `LodPaletteEntry.Color`, `StableColorOf`, or any stored LOD colour into a new
consumer.

**Trap:** it is not `0xRRGGBB`. The mesher writes `color & 0xFF` as the RED vertex byte and the
vertex shader reads it the same way, so the packing is effectively `0xBBGGRR`. A new block-texture
table decoded it the conventional way, swapping red and blue on every textured surface: tan sand
(210,180,140) drew as (140,180,210), a light blue, and brown soil as blue-grey. It hit the whole of
level 0 rather than the band, because a textured quad takes its colour from that table too.

**Do:** decode through `LodBlockTextureRegistry.Channels`, and keep the check that ties it to
`LodPackedQuadFormat.Encode` rather than to a remembered convention.

### G120 - `TextureSubIdForBlockColor` is not the up face

**Trigger:** asking the engine for "the block's texture" to sample it.

**Trap:** that property answers "what colour represents this block" - for the map, for particles -
not "what does its top look like". For grass-covered soil it is the SIDE, whose texture is grass
across its top half and bare dirt across its bottom half. Tiled onto a top face it draws every
block split between grass and dirt, and its average is far browner than a top face, so
normalisation against it shifts the colour as well.

It is still worth having as a fallback: switching to it cut blocks with no resolvable texture from
976 to 46.

**Do:** resolve by face name first (`up`, `all`, `top`), and use the block-colour texture only when
a block names none of them.

### G121 - `specialSecondTexture` is the side fringe, not the top grass

**Trigger:** compositing the grass overlay that makes soil look grassy.

**Trap:** measured off the game's own `block/plant/grasscoverage/normal.png`, its mean alpha per row
is 255 for eight rows and then 128 for the remaining twenty-four. It is a grass edge designed to
hang over dirt down a vertical face. Composited flat onto a top face it draws solid grass across one
part of every block and half-transparent dirt across the rest. The owner reported "half dirt" twice
before this was found.

The same file keeps `normal-top.png` at alpha 255 throughout, and a soil block names it under the
`particletop` texture key.

**Do:** use `particletop` for top faces and treat `specialSecondTexture` as a last resort.

**Do:** read the actual PNG before reasoning about what an overlay contains. Two minutes of
measuring the alpha profile settled what two builds of inference had not.

### G122 - Correct minification erases the texture you added

**Trigger:** sampling a real block texture on distant terrain and wondering why nothing changed.

**Trap:** a 32-pixel tile shrunk into a few screen pixels is averaged by the hardware down to that
tile's own mean - which is the entire point of mipmapping. If the texture is applied as variation
around a block's colour, "texel equals its own mean" is "variation equals one", so the whole feature
multiplies by one and switching it off is indistinguishable from leaving it on. This happens far
closer than intuition suggests, because ground is seen at a grazing angle.

**Do:** choose the sample level explicitly rather than letting the hardware infer it, and expose how
far above the correct level to reach. Physically correct is invisible here.

**Do not** describe this averaging as a safety property. It was presented that way one build before
it became the central obstacle.

### G123 - Normalise by what is actually sampled, not by what was intended

**Trigger:** dividing a texture sample by an average to preserve a surface's colour.

**Trap:** the average has to describe the exact thing sampled. A grass overlay refused for any
reason - a different atlas page, a missing texture - while the normalisation had already switched to
the grass composite means dividing a *soil* texture by a *grass* average. Soil carries more blue
than grass, so blue inflated roughly threefold and grassy ground went pale.

**Do:** carry both means and let the code that decides whether the overlay is used pick the matching
one, rather than computing the mean where the decision is not yet known.

### G124 - Vanilla's ground answers for its water, and it is not telling the truth

**Trigger:** using per-chunk ownership to decide whether cached water should draw.

**Trap:** ownership asks whether the engine is drawing a chunk, and the chunk's *ground* answers.
The engine's water is a separate pass with a shorter reach, so there is a ring where it draws the
bed and no longer draws the river. Inside that ring the mod suppressed its own water because vanilla
"owned" the cell, and vanilla drew none - a measured stretch of dry riverbed with sharp
chunk-aligned edges, in both rivers and lakes.

**Do:** never let ownership suppress cached water, in the fragment shader or in the whole-section
skip. Cached water underneath vanilla's own water is hidden and costs a little overdraw; cached
water missing is a hole in a river.

**Superseded in 0.3.150, deliberately, and with the reasoning recorded.** Cached water is now
under the ownership mask at every distance, with no outward allowance at all. Two things changed
the balance. First, cached water no longer writes depth (G126), so suppressing it where vanilla
draws its own costs nothing and failing to suppress it is what put a flat sheet inside live
terrain - the owner's repeated report. Second, the ring this entry depends on is pushed out of
range by this mod's own horizon hook: `VanillaHorizonEffects` rewrites `chunkliquid`'s
`viewDistance` so the 0.725-to-0.8 alpha fade lands beyond the furthest vertex the engine's
culler will submit, which means vanilla's water does not fade at all while this mod runs.

That second argument is source-traced, not measured, and the hook fails open per pass. **If dry
riverbeds return, this entry is live again** - and the answer is a better ownership signal, never
a radius (G131).

### G125 - Per-cell ownership is far too sparse to hide anything the player is standing beside

**Trigger:** reaching for the vanilla readiness mask to suppress cached geometry in the near
field, for water or for anything else.

**Trap:** ownership is not "vanilla is drawing here". It is proven per 32-block cell only where
the engine reports a chunk both drawn AND holding mesh pool locations, and only once that
observation commits. A real client log during ordinary play showed **104 owned cells against
2,953 still pending**, with 33,506 cells denied for holding no geometry and 59,798 denied as
beyond the draw range. A rule gated on ownership therefore fires almost nowhere near the player,
and the change looks like it never shipped.

**Do:** read the `vanilla readiness:` line of the periodic log - it carries the owned and pending
counts - before designing against ownership at all.

**Amended in 0.3.150.** The original advice ended "use a plain radius for anything in the near
field", and that is now wrong twice over: a radius was rejected in play on sight (G131), and the
sparsity is far less dangerous than it looks once the failure mode is soft. Cached water no
longer writes depth, so a cell the mask has not proven costs a faint tint under real water rather
than a sheet in front of it - which is what made ownership usable for water after all. Where a
denser footprint is genuinely needed, dilate the one you have.

**Found:** 0.3.140, from a water fix that was measured, built, shipped, and did nothing.

### G126 - Vanilla's water is drawn in a LATER render stage than anything this mod draws

**Trigger:** any cached surface that writes depth and has a vanilla counterpart drawn outside the
opaque stage.

**Trap:** the mod registers at `EnumRenderStage.Opaque` at order 0.5 and vanilla terrain is 0.37,
so cached ground correctly draws after vanilla's ground. Vanilla's *water* is not in that stage:
`SystemRenderTerrain` renders the liquid pool from `RenderOIT`, in the later OIT stage. The
engine leaves `GlDepthMask(true)` on through the opaque stage and the mod never clears it, so
cached water writes depth before vanilla's water is drawn and depth-rejects it. Nothing errors,
nothing is missing, and the cache's flat sheet simply stands in for the real animated surface.

**Do:** "we draw after vanilla" is a claim about one stage. Check which stage the vanilla
counterpart actually renders in before relying on ordering, and treat a depth write in the opaque
stage as a claim over every later stage too.

**Found:** 0.3.141, source-traced from the decompiled client after the owner reported that nearby
water looked like cache water.

### G127 - Coverage changes cost walls, and the surface delta will not tell you

**Trigger:** costing any mesher change that alters what counts as cover, rather than one that
adds faces directly.

**Trap:** giving terrain slabs their real half height added 20% more flat surfaces and **38% more
walls**, for +32% overall. The walls are the whole point - a slab beside a full block exposes the
top half of that block's side, which used to be culled outright because the slab claimed the
entire cell - but nothing about "lower a surface by half a block" suggests that walls are three
quarters of the bill. Slabs buried under another block, where the added faces are sealed inside
solid ground and genuinely wasted, were only 12,134 of 158,754.

**Do:** split any geometry measurement by face orientation before quoting a number, and mesh the
real cache twice with the shipping mesher rather than counting faces in a reimplementation. See
`slabfield` in the checks project.

**Corrected in 0.3.143:** "buried" was too narrow a boundary for the useful work. The exact sample
contained 158,754 slab runs but only 65,624 columns whose highest solid run was a slab. Preserving
those outdoor slabs, including ground under water and thin cover, while leaving lower cave and
overhang slabs at the established full-block LOD approximation reduced the clustered delta from
31.52% to 7.84%. The landscape boundary, not merely whether a slab is immediately sealed, is the
high-value fidelity boundary.

**Found:** 0.3.140, after the owner measured 300 to 270 FPS in one scene.

### G128 - Better rectangle covers cannot recover geometry the fidelity rule requires

**Trigger:** optimizing a large geometry regression after a shorter block changes which neighbour
surfaces are exposed.

**Trap:** assuming a stronger greedy cover or merging render-equivalent materials will turn exact
slabs back into roughly cube-priced geometry. A dedicated alternate cover removed only a few
thousand quads and slightly worsened the relative clustered delta, 31.52% to 31.59%. Palette
equivalence removed 8,464 slab quads but worsened the relative delta to 35.34%. Both optimize the
packing of faces that still have to exist; neither restores the broad cover planes a full cube
provided.

**Do:** first decide where exact half-height fidelity is valuable. The highest-solid-run policy cut
the same clustered delta to 7.84% with no new draw clusters. Only then optimize representation, and
keep rejected prototypes measured and reverted.

**Found:** 0.3.143, by paired real-cache meshing of both rejected alternatives.

### G129 - A linked worktree has a `.git` file, not a `.git` directory

**Trigger:** a test or build helper discovers the repository root by walking upward until it finds
`.git`.

**Trap:** requiring that marker to be a directory. In a linked worktree `.git` is a text file that
points at the shared repository metadata. The slab static checks skipped it, walked into the
primary checkout, and silently validated the other branch's assets.

**Do:** accept either a file or a directory as the repository marker, and keep the check rooted in
the worktree whose binaries or assets are under test.

**Found:** 0.3.143, when a linked-worktree shader check disagreed with the file being edited.

### G130 - `Use()` re-binds the shared includes' samplers, per program, into global state

**Trigger:** binding a texture for a shader program and then activating any program - including
that same one - before the draw.

**Trap:** `ShaderProgramBase.Use()` re-binds every sampler the shared includes declare -
`shadowMapFar`, `shadowMapNear`, `sky`, `glow`, `liquidDepth` - to whichever texture unit the
LINKER assigned that name **in that program**. Those numbers are per program, and a mod's shader
variants declare different sampler sets, so a unit that is free in one is not free in another.
The sampler *uniform* is per-program state and survives; the unit *binding* it points at is
global and does not.

The mod's opaque pass never saw it, because the indirect path re-applies its frame uniforms
immediately after activating the packed program. The water pass did: `prog.Use()` in the
indirect bucket's `finally` re-ran the engine's binds, and nothing re-applied anything before
the water draws. The seam field sampled another texture, read approximately zero, and the shader
reads zero as "distance zero from vanilla" - full band strength, across the whole field window
and nowhere else. A flat square on water and thin cover, with correct ground beside it, for four
builds.

**Do:** treat `Use()` as clobbering every texture unit this mod owns, and re-bind them after any
program activation that is not immediately followed by a full frame-uniform apply. Choosing a
"high enough" unit number is not protection: the assignment is per program and not yours.

**Do not** attribute a wrong-looking region to the feature whose data is wrong. Paint the
candidate passes in different colours first; the defect was in the pass, not the feature.

**Found:** 0.3.149, after the owner reported that a debug square turned green on water but stayed
red on flowers - and thin cover is drawn in the water pass.

### G131 - A distance test is a circle wherever it is measured from

**Trigger:** hiding, fading, or correcting cached geometry by comparing a distance against a
threshold, in the shader or on the CPU.

**Trap:** the result is a perfect circle, and a perfect circle is a shape the world does not
contain. It reads as an effect painted over the terrain rather than as terrain, and shrinking it
does not help - 0.3.141 used 0.70 of the view distance, 0.3.144 used an absolute 48 blocks, and
both were rejected in play on sight. The same applies to a bound that only *permits* suppression
inside a radius: past it, nothing suppresses, and cached geometry draws into live vanilla chunks.

**Do:** express every such boundary in the world's own grid. A Chebyshev count of 32-block chunk
columns can only put its edge on a chunk edge. Where a footprint is wanted, dilate the footprint
rather than measuring a range from the camera - dilation also closes the uncommitted pockets
inside it, which a range test leaves open.

**Do not** use a Euclidean or chamfer dilation for this. Its level sets are smooth curves however
blocky the seed is, which is the same defect by another route; the seam field's chamfer weights
are chosen specifically to stay near-Euclidean, and its contours are correspondingly round.

**Found:** 0.3.150, after the third radius in the water path was removed.

### G132 - A chunk-level refusal measured to the nearest face already covers one chunk

**Trigger:** reconstructing, dilating, or compensating for a rule that refuses whole chunk
columns beyond a distance threshold.

**Trap:** `BeyondVanillaDrawRange` compares a column's NEAREST face against the threshold, so the
outermost column that still qualifies has its near face just inside it and its far face a whole
chunk further out again. Growing a footprint back by the full ring depth therefore overshoots by
exactly one chunk. Measured in play as a chunk-wide strip of missing water between cached water
and vanilla's.

**Do:** subtract one chunk from the ring before converting it to a dilation, and round up on what
remains. Check the arithmetic against the face the threshold actually uses, not against the
distance the constant is named for.

**Found:** 0.3.152, from a one-chunk gap the owner reported at a shoreline.

### G133 - An accurate rule gated on a state the inaccurate rule controls never runs

**Trigger:** applying a cheap approximate rule to items that have not yet reached the state where
the accurate rule is allowed to run - typically to avoid a lookup the accurate rule needs.

**Trap:** `LodTerrainRenderer` gated the exact draw-range test on `atOwnershipDecision`, because
reading the engine's bounding sphere needs the chunk and a first sighting had not fetched one.
Every other probe fell back to `BeyondVanillaDrawRange`, whose threshold is `viewDistance - 46`.
That is **18 blocks at a 64-block view distance, and zero at 46 or below**. A chunk 28 blocks
away - meshed, cull-visible, unhidden, well inside the 67 blocks the engine actually draws to -
was refused, so it stayed `Pending`; a `Pending` cell is not an ownership decision, so it never
reached the exact rule that would have admitted it. The approximation permanently prevented the
measurement, and the mod owned almost no ground at exactly the view distances this mod exists for.

It hid well. At a 192-block view distance the same gate is 146 blocks, loose enough that cells get
through and the exact rule takes over, so the feature looked healthy in the first log and failed
in the second.

**Do:** make any rule applied before the accurate one a **strict subset** of it, and assert that
subset relation over a dense sweep rather than reasoning about it. The replacement compares the
column's nearest face against the widest bound any LOD level draws to: the sphere centre lies
inside the column, so its distance is at least the nearest-face distance, and everything the early
gate refuses the exact rule refuses too. `VanillaDrawRangeChecks.TheEarlyGateCanOnlyRefuseWhatThe
ExactRuleRefuses` holds it.

**Do** use the widest bound, not the view distance. Level 0's clause is
`distanceSq < lod0BiasSq + 1024`, which with `LodBias` at 1 reaches **further** than level 1's
`viewDistanceSq` - 71.6 blocks against 67.1 at a 64-block view distance.

**Found:** 0.3.158, from `.vhexact audit` reporting seven uncovered chunks all in state `Pending`,
the nearest at 28 blocks. Shipped in 0.3.154; the bug was introduced by the fix for G43.

### G134 - A diagnostic that names a signal it does not print

**Trigger:** a report written to show "every signal side by side" so a conclusion can stop being
guessed at.

**Trap:** `VanillaChunkGeometry.DescribeSignals` documented the drawn counter as the first of its
signals - it is what `IsChunkRendered` reads, and it is the first gate every probe passes - and
printed `empty`, `mesh`, `cull`, `hide` and `frustum` without it. Two rounds of a log
investigation were spent reasoning about which gate had refused a chunk while the only gate named
in the method's own summary was absent from its output. A second bucket in the same session,
`refused for range`, was dead code (`&& !ExactDrawRange`, never true) and silently filed every
such cell under a different reason.

**Do:** assert that a report prints what its summary claims, and that every reason bucket is
reachable. A reason bucket naming a gate the item never passes through is worse than no bucket,
because it is believed.

**Do** make the reason reflect the rule that would actually run for that item in its current
state, not whichever rule is convenient to evaluate from where the report stands.

**Found:** 0.3.158, after the missing counter had already cost two rounds.

### G135 - A per-section aggregate is not a per-column fact

**Trigger:** comparing something measured over a cached section's whole footprint - a height span,
a bound, an average - against a fact about one chunk column inside it.

**Trap:** an overshoot report compared each section's `LodHeightSpan.MaxY` against the top of each
covered column's owned band. A section's span is the maximum over its entire footprint: 64 blocks
at L0 and **4,096 at L6**, so the comparison measured the terrain's slope and reported it as
cached geometry standing too tall. It also visited every column once per LOD level covering it,
turning nine columns into 63. The result - "54 of 63 columns overshoot" - was quoted in a handoff
before the arithmetic was checked against the nine cells the mask actually owned.

**Do:** restrict such a comparison to the finest level, state it as an upper bound, and sanity-
check its denominator against a number measured elsewhere. Any count that is a clean multiple of
the LOD level count is a per-level visit, not a population.

**Found:** 0.3.157, when a report claiming 63 columns sat beside telemetry showing nine.

### G136 - A face lying IN a cell boundary belongs to neither cell until you say which

**Trigger:** deciding which ownership cell, chunk, region or tile a rendered fragment falls in by
dividing its own position, when the geometry includes axis-aligned faces.

**Trap:** `floor(position / 32)` is right for a fragment inside a block and undefined for one on
the boundary between two. A mesher emits each wall at its own block's face - the near edge going
west or north, the FAR edge going east or south - so an east-facing wall standing on a chunk plane
is drawn at the next chunk's origin and floors into that neighbour. The ownership mask then judged
it by a cell that does not contain its block. Where the neighbour was owned and the wall's own
chunk was not, the wall was discarded as vanilla's work while vanilla drew nothing there: a
missing vertical face at the seam, on the -X and -Z sides only, because on the other two the
mis-charged face points away and is back-face culled anyway.

The asymmetry is the fingerprint. Nothing else in the renderer treats +X differently from -X, so
"the holes are always on the same two sides of me" identified this in one turn after four
sessions of hole-hunting had not.

**Do not** fix it by biasing the address a fixed direction. A uniform inward nudge is exactly
correct for east, south and top faces and exactly wrong for west, north and bottom ones - it moves
the hole to the opposite seam rather than closing it.

**Do:** treat the boundary as ambiguous and ask every cell that could hold the block behind the
fragment, hiding it only when they all agree. One lookup for an ordinary surface, two for a wall
in a plane, four on a chunk corner. It is conservative in the safe direction - the extra candidate
can only keep cached geometry drawn over ground vanilla is drawing too - and it removes a second
hazard for free: a constant attribute is not promised to interpolate to the last bit, so which
side of the plane a wall floored onto was a coin flip the hardware owned.

**Do** expect a shader-parity check to be blind to this. `VanillaReadinessChecks` asserts "an
exact 32-block boundary enters the next chunk" and the shader agrees with it; both share the wrong
assumption about which cell a face on the boundary belongs to, which is G97 with the reference
implementation on the CPU side.

**Found:** 0.3.162, from a player report of missing vertical faces after `.vhband overlap 0`
removed the margin that had been burying it since 0.3.139.

### G137 - Drawing a block as a cube does not entitle it to occlude like one

**Trigger:** an LOD, impostor or proxy that substitutes a simplified solid for geometry the real
renderer draws as a cutout, and then uses that solid in its own visibility or face-culling rules.

**Trap:** the cache draws a crop, tall grass or a vine as an opaque cube, deliberately, because a
meadow of flat mats reads as a green sheet at distance. The mesher then let that cube act as
COVER, dropping the soil's top face beneath it and the wall between two of them. Inside cached
terrain both drops are correct and cost nothing anyone can see. At the vanilla seam the game draws
in place of the cache, its crop is a transparent tuft, and those two dropped faces become a window
through the hollow cached shell and out to the sky. Reported in play as holes in a flax field.

Vanilla's own block data says so outright: `crop.json` carries `sideopaque: {all: false}` and
`renderpass: "OpaqueNoCull"`. The engine never claimed these blocks occlude; the cache invented it.

**Do:** read the engine's own answer - `Block.AllSidesOpaque` - rather than matching block codes.
The shipping thin-cover rule is a list of three name prefixes (`flower`, `fern`, `tallfern`) and
is blind to everything else; a palette scan of one real world found crops 4,855 palette entries,
tall grass 7,294, vines 3,841, berry bushes 2,617 and mushrooms 986, every one of them stored as a
solid cube. Keep the material guard: leaves, glass, fences and stairs are all non-opaque too, and
freeing every one of them from covering would put a wall between every pair of leaves in every
canopy.

**Do** measure the coverage change by face orientation before quoting it, per G127. Meshing 120
real cache sections twice with the shipping mesher measured +2.2% to +4.4% opaque geometry over
four samples, landing on walls (+2.9%) with tops nearly free (+0.6%), and **no change at all in
draw clusters** - which is the number that decides whether a geometry increase is affordable.

**Do** expect the cost to be pure waste everywhere except the seam. That is not an argument
against it; it is the argument for putting it behind a switch (`.vhcutout`) so the frame rate can
be weighed against a picture the numbers cannot show.

**Found:** 0.3.163, from a player standing at a seam through a flax field, after G136 had already
removed the other half of the same symptom.

### G138 - A zero edge mask does not mean no content changed

**Trigger:** returning which neighbours a mutation affects as a bitmask, especially when zero is
also used as the no-op result.

**Trap:** `LodSection.ReplaceColumns` returns the section edges touched by changed columns. An
interior block placement correctly produces `EdgeNone`: the section changed, but no neighbour can
see it. `ApplyOneCaptureResult` treated that zero as “nothing changed” and skipped `MarkChanged`,
so the new runs and palette could enter resident RAM without dirtying the L0 mesh, persistent row
or mip obligation. Texture registration could succeed while the terrain later shown outside
vanilla range remained stale. A later edge mutation could accidentally publish the accumulated
RAM state, hiding the failure during ordinary play.

**Do:** carry content change and affected edges as separate values. Advance the section's content
and persistence revisions for every real mutation, including `EdgeNone`; use the mask only to
decide which neighbouring meshes also become stale. Assert both halves: an interior change dirties
the section/save/mip but no neighbour, and a byte-identical replacement dirties nothing.

**Found:** source-traced after 0.3.166 while answering how newly placed blocks receive L0 textures.
The texture lookup path was correct; the independent invalidation path was not.

**Fixed in 0.3.189.** `ReplaceColumns` and `LodMip.ApplyToParent` now report content change and
touched edges as two separate answers, and both call sites act on the first. `MarkChanged` already
did the right thing with `EdgeNone` - it advances the revisions and skips all four neighbours - so
the defect was only ever in not reaching it. `LodMip.DownsampleIntoParent`, the helper the checks
use, carried the same conflation in its return value and now reports content change too.

### G139 - A derivation that cancels a factor must show the factor cancelling

**Trigger:** reducing an engine shader's arithmetic to a constant for the special case your own
shader is always in, and then asserting the reduced form in a check.

**Trap:** `applyLight` multiplies terrain by `lightColor.a`, the sun light level baked into the
vertex. 0.3.51 reduced it for the LOD case as "full sky light, every scale factor cancels except
the contrast constant" and shipped `1.05 * rgbaAmbientIn`. Two errors compounded. The `bMax`
renormalisation cancels the ambient colour's own MEAN, not the sun level, so the level survives and
multiplies the pixel. And sky-exposed ground is not level 31: it is the world's `sunBrightness`,
22 in the base game, which the engine's ramp puts at 0.806 and quantises into the vertex byte as
206/255. Cached terrain was 1.24x too bright at every hour, on every block, for four sessions -
and because the error is a flat factor it never moved with the time of day, which is what the
lighting work of 0.3.51 was looking for.

The static check was worse than useless: it asserted `1.05 * rgbaAmbientIn` verbatim, having been
written from the same reduction, so it agreed with the bug and would have blocked the fix.

**Do:** write the surviving factors out per term rather than declaring that they cancel, and keep
the engine's own expression beside yours in the comment. Derive any check of a reduced form from
the engine source, not from the reduction - a check that shares the derivation's premise cannot
detect the derivation's error. `IWorldAccessor.SunLightLevels` and `SunBrightness` are both public;
read them rather than assuming a level.

**Found:** measured on a flat two-block test patch at the seam in Session 73, then confirmed by
invoking `ColorUtil.LightUtil.ToRgba(22, 0)` offline through a reflection probe. Fixed in 0.3.169
behind `.vhlight sun`.

### G140 - Two tiles in one atlas rectangle, and only one of them is the top

**Trigger:** averaging a texture the engine addresses by tile, especially a TopSoil
`specialSecondTexture`.

**Trap:** that overlay is two tiles wide - side fringe on the left, top face on the right - and
`chunktopsoil.fsh` shifts one tile right when the normal points up. `TryTopSoilColor` averaged the
whole strip. The visible symptom was not the colour it returned but the COVERAGE: the fringe is
mostly transparent, so full grass measured 0.687 where the top tile alone is 1.000, sparse 0.537
against 0.843, very sparse 0.400 against 0.720. Coverage decides how much untinted soil shows
through the overlay, and the seasonal tint carries almost no blue, so believing a third of every
grassy block was bare dirt put roughly twice vanilla's blue into all distant grass.

The same file already knew: `ResolveBlockTexture` had taken the right half since Session 71. One
texture, two code paths, two independent decisions about it, and only one of them updated.

**Do:** derive tile-ness from the atlas rectangle's aspect rather than assuming it - taking the
right half of a texture that is not a strip reads a neighbouring block's tile - and make the
decision once, shared by every path that reads that texture. Check the answer against stored cache
colours rather than against the code: the wrong formula reproduced three real stored colours to
the byte, which is what identified it.

**Found:** Session 73, from the owner's own cache. Fixed in 0.3.169. See also G49, which found the
compositing itself, and G50 for the four-pixel coverage error beneath it.

### G141 - Thirty sampled pixels is not a texture's average

**Trigger:** using `Block.GetColorWithoutTint` or `BlockTextureAtlas.GetRandomColor` as the colour
a block should read as from far away.

**Trap:** `GetRandomColor` returns one of thirty pixels the atlas kept, and those thirty are
themselves a handful out of 1,024. Averaging many draws converges on the thirty, not on the
texture, and the two differ: granite gravel settles at 143,133,105 where the file's true average is
131.5,123.2,109.1 - about 8% high in red and green and 4% low in blue, which reads as a warm,
bright cast on distant stone. Averaging harder cannot fix it; the sample is biased, not noisy.

**Do:** read the texture. `TryReadTextureMean` already does this for the block-texture pattern
normaliser, so the exact average usually exists already. Prefer it for the colour too, because the
shader draws `colour * (texel / mean)` and a colour and a mean derived by different methods drift
apart - which is the same failure G140 records. Keep the sampled value where the texture cannot be
named: the fallback is a four-pixel atlas estimate, which is worse than the thirty.

**Found:** Session 73, as the residual after G139 and G140 were fixed. Corrected in 0.3.170. See
G48 for the channel-order trap on the same two functions.

### G142 - Chat text is markup, and a bare angle bracket wedges the panel

**Trigger:** writing a diagnostic report to chat with `TextCommandResult.Success`.

**Trap:** the client parses chat text as VTML. `.vhtint` wrote an arrow as `->`; a bare `>` is a
closing tag with nothing open, so it errored, mis-nested the client's own `<font>` tag after it,
and - because the scrollback is re-parsed on every redraw - kept erroring several times a second
until the message scrolled away. The command itself had worked and its report was in the log
already; the symptom was "the command returns nothing and then chat stops working."

**Do:** keep `<` and `>` out of any string that reaches chat. Prefer a separator that is not
markup, and strip the brackets from the chat copy on the way out so a later edit cannot reintroduce
it. Send the report to the log unmodified: the log is the copy that has to stay readable, and it is
not parsed.

**Found:** Session 73, on the owner's first use of 0.3.167. Fixed in 0.3.168.

### G143 - A texture upload binds on the current unit and steals whatever was there

**Trigger:** calling `LoadOrUpdateTextureFromBgra` or any sibling, anywhere near a run of
`BindTexture2D` calls.

**Trap:** `ClientPlatformWindows.LoadOrUpdateTextureFromPixels` reaches `GL.BindTexture` on both
its create and its update branch and never calls `GL.ActiveTexture`, so the texture it writes is
left bound to whichever unit happened to be current - and it never restores what it displaced.
`ShaderProgramBase.BindTexture2D` sets the uniform, activates its unit and binds, leaving that unit
current. Put an upload one line after a bind and the upload takes that bind's unit. The tint ramp's
upload sat after the block atlas was bound to unit 9, so the blend band - the only place the atlas
is sampled - drew a 64x96 tint ramp as its block texture and came out several times too bright,
while every fragment outside the band was perfect. No error, nothing in a diff, and it reads as a
colour bug rather than a binding one.

Alone it would have been one frame in eighteen hundred. It was permanent because a second defect
kept the upload running every frame: the version guard was one counter per shader program, carried
over from when the table was uniform arrays, and the upload method assigned the field while two
programs passed their own counters by ref.

**Do:** make the destination unit current before uploading, so a stray bind lands where the texture
belongs anyway. Keep every upload behind one helper that does it, and hold that to one call site
with a check. A texture is not program state: version it once, not per program.

**Found:** Session 75, from the owner's report that the blend band was "way off". Fixed in 0.3.177.

### G144 - The engine's CPU colour call is not what the engine draws

**Trigger:** taking a tint, a blend weight, or a season from `ApplyColorMapOnRgba` and expecting
the terrain shader to agree.

**Trap:** the CPU path and `colormap.vsh` disagree in at least two ways, and both were shipped
before either was found.

- **The season weight.** The CPU's two correction terms are integer divisions that evaluate to zero
  for every reachable temperature, and it has no altitude term at all; the shader computes both as
  floats and shifts the temperature by 1.5 per block above sea level. Up to 0.85 apart. Corrected
  in Session 74.
- **The season column.** The CPU reads `YearRel * width`; the shader reads
  `(seasonRel + b/40) * width`, where `b` is two value noises, so it draws the world up to 0.05 of
  a year - about ten in-game days - further into the season. Constant offset, but its colour cost
  depends entirely on how fast the season map is moving: near zero through midsummer, about ten
  counts of red through the autumn turn. It was as old as the tint and only became visible when the
  calendar reached a fast-moving part of the map, which is why four sessions of colour work had
  called the match "nearly perfect".

Both are invisible to any diagnostic that compares two quantities which both come from the CPU
call, because the error cancels. `.vhtint`'s A and C lines did exactly that for four sessions.

**Do:** for any quantity the terrain shader uses, read `colormap.vsh` and transcribe it, and hold
the transcription against the game's own file in a check rather than against a copy of it in this
repo. Where the shader is given a uniform - `seasonRel` is the case here - take the engine's own
`DefaultShaderUniforms` value rather than recomputing it from the calendar, so the two cannot
diverge at all. See also G139: a check written from the same wrong reduction agrees with the bug.

**Found:** Session 74 (the weight) and Session 75 (the column). Fixed in 0.3.172 and 0.3.183.

### G145 - An average cannot be held to within a few percent of one random draw

**Trigger:** validating a deliberately averaged summary against the engine's answer for a single
block, or writing an acceptance criterion that says the two should agree.

**Trap:** the engine picks a season map ROW per position from a hash, so its answer for one block
is one draw from a wide distribution - across the 64 lattice positions `.vhtint` samples, blue
spans 0.004 to 0.090. Distant terrain is a summary of many blocks and averages the rows on purpose
(G47), so the two quantities are not the same kind of thing. A 37% gap between them is the spread,
not an error, and chasing it costs whole rounds.

`dev/plans/PLAN_DISTANT_TINT_SAMPLING.md` section 9 still asks for A and C "within a few percent on
every channel". That criterion cannot pass and should not be treated as a target.

**Do:** compare an average against an average - the lattice mean, which the same report already
prints. Print the spread beside any single-sample reading so its width is visible. When a
comparison looks alarming, check first whether one side is a single sample.

**Found:** Session 75, after a 37% blue "error" turned out to be one row draw.

### G146 - Climate values carry height terms before anything reads a colour map

**Trigger:** sampling temperature, rainfall or any other climate quantity at one convenient height
and using it at another.

**Trap:** `Climate.GetRainFall(raw, y)` adds `(y - Climate.Sealevel)/2` plus a bonus of up to 40
that applies only within eight blocks of sea level, before the value ever indexes a colour map. The
climate field sampled rainfall at sea level - exactly where that bonus peaks - and spent it
unchanged everywhere: 35 counts too wet ten blocks up on a 256-count axis. Temperature has its own
term, `GetAdjustedTemperature`, keyed on a DIFFERENT sea level: the client world map's, not
`Climate.Sealevel`. The height ramp this replaced never had the fault, because it asked the engine
for a finished colour at each height and the engine applied the terms itself; storing climate
instead moves that responsibility into this mod.

**Do:** store the raw quantity and reconstruct at the height being drawn, with the reconstruction
in one named place that a check can hold against the engine's IL. Do not assume the two sea levels
are the same value; pass both.

**Found:** Session 75. Fixed in 0.3.181.

### G147 - A handle taken before its owner builds it is null, and null is a silent no-op

**Trigger:** caching a reference to something the pipeline creates lazily - the storage thread,
the store, a texture the first upload makes - especially when the consumer guards on null.

**Trap:** `renderer.ClimateStorage = pipeline.StorageThread` was assigned in `StartClientSide`.
`LodPipeline` builds its storage thread in `Open`, which runs when a WORLD IS JOINED, so the
reference was always null and remembered climate never reached disk in either direction. Nothing
failed: the consumer's `if (ClimateStorage == null) return` is a correct guard, the in-memory half
of the feature still worked, coverage still grew as the player explored within a session, and the
owner tested the build and called it good. The whole point of the feature - surviving a restart -
was absent with no symptom at all.

The second half is what makes this worth a gotcha. The same null was ALSO suppressing a real
crash: the flush that saves on the way out ran from the render path's mesh clear, and on the world
leave path `pipeline.Close()` disposes the storage thread BEFORE that clear is reached, so a live
handle would have set a disposed `AutoResetEvent`. Fixing the null on its own would have converted
an inert feature into an exception on every world leave.

**Do:** take a lazily-created handle where it is created, not where the consumer is constructed,
and drop it where the owner closes - a reopened pipeline builds a new one and a stale handle is
worse than a null one. Do not put a WRITE to it inside a callback whose ordering you do not own;
the mesh clear is a render-path concern and its position relative to storage disposal is not the
renderer's to choose. Where three statements' only requirement is which side of another statement
they fall on, assert the order: `StaticAssetChecks.ClimateStorageIsWiredInTheRightOrder` does, and
it fails on the code as shipped in 0.3.185 through 0.3.190.

**Found:** 2026-09-03, reviewing this session's own work after the owner had already accepted
0.3.185. Fixed in 0.3.191. See also G42 and G130: a guard that makes a wrong value look like a
legitimate answer is the shape all three share.

### G148 - A detail level costs what a section costs, so a fade is priced by levels, not by area

**Trigger:** any per-vertex effect faded out across LOD levels - a tint, a noise, a detail term -
and any sentence of the form "it only applies near the player, so it is cheap".

**Trap:** a section holds the same 64x64 grid at every level. A coarse section is not a smaller
section; it is the same amount of geometry stretched over more ground. So the cost of including a
level is roughly the cost of any other level, and what sets the bill is HOW MANY LEVELS the fade
reaches - not how much of the world they cover, which is the intuition the word "distant" invites.

The near levels are also not the small ones. `.vhtint leaves` faded to zero at 8 blocks per voxel,
which reads as a tight near-field restriction and is not one: the owner's own log showed 110 drawn
sections as `L0:31 L1:57 L2:9 L3:1 L4:5 L5:3 L6:4`. Level 1 alone had more sections in view than
level 0, and the fade was charging 97 of 110 for a variation already down to 0.57 strength by the
third level. Confining it to level 0 - 31 sections - cost no visible fidelity, because level 0 is
where one voxel is one block and the effect is drawn at the scale vanilla draws it anyway.

Two corollaries earned the hard way in the same session. Skipping the levels BEYOND the fade
(L3-L6) sounded like the big win and was 13 sections, 12%; the cheap end of a level distribution
is where the sections are not. And when a real 17 fps improvement did not visibly move the frame
counter, "I cannot see it" got treated as "it is not there", which sent the next change chasing
the wrong cause.

**Do:** read the drawn-level census before pricing anything that fades across levels - the
periodic report prints it as `selected [L0:.. L1:.. ..]`. Express the reach as a uniform the owner
can move at runtime rather than a constant, so the look and the price can be A/B'd in place at one
scene. And when a change's predicted effect is near the noise floor of the instrument, measure it
against a state that isolates it rather than concluding from a counter that did not move.

**Found:** 2026-09-03, after a 40 fps regression from 0.3.187. Fixed in 0.3.192 and 0.3.193.
See also G47, which is the reason the fade exists at all.

### G149 - The bench harness pins the chunk mask in BOTH directions, so a default is not a default

**Trigger:** reading any `bench-windows.ps1` result as "what the mod does", especially a
performance sweep, and any sentence of the form "the run used the shipped configuration".

**Trap:** `-ChunkMask` does not merely enable the mask when passed. The script writes
`VINTAGEHORIZONS_CHUNK_MASK` unconditionally - `1` with the switch, `0` without it - and the
renderer treats the variable as an override in both directions. So a run WITHOUT the switch
measures the radial handoff path with no ownership mask and no seam field, which is not what
any player has run since 0.3.17. The comment in the script says so and is easy to skim past,
because every other switch there is additive.

A whole sweep was measured this way in Session 77 before the readiness line's `mask off` gave
it away. Its per-feature deltas were wrong in the one place it mattered: the seam band cannot
cost anything when the field it reads was never built.

**Safer action:** read the configuration back out of the run's own log before trusting it.
`vanilla readiness:` carries `mask <state>` and `band <state>`; `mask off` means no mask and
`band pending` means the field never built. Do not grep a bare substring for it - `mask
[a-z]+` also matches `paints mask off`, the debug-paint field on the same line, which
produced the opposite error minutes later. Anchor on the field's own delimiters.

### G150 - A derived mirror has to be seeded everywhere the original is

**Trigger:** publishing the same data in two shapes - a texture and a uniform array, a packed
row and a decoded one - and filling the second one at the place the first is BUILT.

**Trap:** the build is not the only place the original gets a value. `LodTintRegistry` seeds
its slot table in the constructor, deliberately, so a slot registered but not yet sampled
draws white instead of whatever zero dilutes to. `BuildSlotTable` then fills it properly at
the first refresh. Mirroring only inside `BuildSlotTable` therefore left the uniform arrays at
zero for every frame before that refresh completes - which the shader reads as a black
fallback tint, and as climate map page 0, a real page it would then finish a tint from. Black
ground on every world load, for a second or two, on a change whose entire purpose was to be
invisible.

The seed exists because this exact mistake has been made before in the other direction (see
the constructor's own comment about 0.3.175): filling a gap with a value that looks like an
answer.

**Safer action:** mirror at every point the original is written, and hold it with a check that
compares the two shapes on a FRESH instance rather than on a refreshed one. The check written
for this change is what caught it, and it caught it because it constructed the registry and
compared immediately.

### G151 - A label is not a specification, and "your description is correct" is not a verification

**Trigger:** the owner supplies player-facing copy - a settings-window title, a tooltip, a
command's help text - that names something the code already does differently.

**Trap:** changing the code so the words come true. The owner's copy for the settings window
named "Close Biome Colors" as LOD L0 and "Far" as L1-L6, where the implementation split at L0-L2
and L3-L6. `CloseClimateTintRange` went from 4 to 1 to match the words, which halved the distance
distant ground wears its own biome colour - about 2,048 blocks down to about 1,024 on default
thresholds - and brought the handover to the standing biome twice as close to the player. Nobody
asked for a shorter range. It also invalidated the frame-time figure quoted alongside it, which
had been measured at L0-L2.

Copy describes behaviour. When the two disagree the default is to fix the copy, because the
behaviour is the thing that was measured and reasoned about; changing the behaviour is a product
decision and needs to be raised as one.

**The second half of the trap is the report.** Asked whether the setting really reached L2, the
answer given was "your description is correct" - true only because the code had been changed to
make it true, days after the description was written. That reads as a verification and is a
concealment. If a claim is true because of an edit, say the edit.

**Safer action:** when supplied copy disagrees with the code, say so and offer both - "it
currently does X, your text says Y, which do you want" - before either is changed. And when
reporting on a claim, state whether it was true when asked or became true in between.

### G152 - A switch whose cost lands in BOTH halves of an A/B cannot be measured at all

**Trigger:** adding a feature that changes the MESH, and gating only the part of it you think
of as the feature - the colour, the shading, the visible half - while the part that changes
the geometry runs unconditionally.

**Trap:** shading submerged ground by depth splits the greedy merge, because quads at
different depths must not merge and take one colour. The water surface derived its depth from
its own run rather than from the absorption table, so it split the merge with the feature
switched OFF as well as on. The harness measured `water off 104,428 vertices, water on
104,428, +0, 0.00%` and the obvious reading - it costs nothing - is exactly wrong: it cost
+75% of the water buffer in both halves and the subtraction cancelled it. An A/B can only see
what one side does and the other does not.

**Safer action:** gate the whole change on one flag, including the parts that only affect
geometry, and be suspicious of a delta of exactly zero. A feature that is genuinely free
usually measures as noise, not as nothing.

### G153 - A diagnostic that reads a cache its own setter just cleared answers the same way forever

**Trigger:** a setting whose setter invalidates a derived value and queues a rebuild, and a
command that sets it and then describes the result in the same breath.

**Trap:** `.vhwater clarity` cleared the absorption table and called `RemeshAll`, then printed
`DescribeWaterAbsorption()`, which read the field it had just set to null and reported "no
table resolved yet". Every call. In every case, working or broken. That line exists precisely
to tell a real failure from a working one, and it sat in the owner's chat log through a whole
testing session saying nothing at all - while he tried five clarity values and reported back
on a picture the diagnostic could not describe.

**Safer action:** a description RESOLVES, it does not read. Call the builder, not the field.
And when a report can only produce one string, that is a bug in the report even if the code it
describes is correct.

### G154 - A textured quad has no colour word, so a colour written there is read as an index

**Trigger:** writing a colour into a packed quad at level 0, where block textures live.

**Trap:** `LodPackedQuadFormat` spends word 2 on a block texture SLOT instead of an RGB colour
whenever the textured flag is set, and the vertex shader recovers the colour from the block
table by that slot: `vhBlockSlot = int(vhColor & 0x00FFFFFFu)`. Texture slots are emitted at
level 0 only. So depth shading baked into the colour worked at level 1 and beyond and was
silently discarded at level 0 - while still paying its whole geometry bill there.

The owner named the cause from the outside without seeing any code: the bed darkened at
distance, un-darkened as he walked toward it, and the boundary followed his L0/L1 threshold
when he moved it.

**Safer action:** anything written into word 2 has to say which of the two meanings it is. A
submerged quad now gives its texture slot up. The spare room is real if both are ever wanted -
real slot values are twelve bits of the twenty-four - but using it is a shader change on every
render path.

### G155 - The blend band moves with the player; the vanilla chunk grid does not

**Trigger:** wanting to confine expensive mesh detail to "the seam" or "the band".

**Trap:** the answer given was that it could not be done, because the blend band is derived
per frame from vanilla's current footprint and the mesh is built once and cached, so gating on
it would mean re-meshing as the player walks. All true, and it answers the wrong question.
Ownership is decided per 32-BLOCK VANILLA CHUNK, so a seam can land nowhere except a 32-block
plane - which is static, section-local, and known when the mesh is built. The owner pushed
back on the refusal and was right.

Measured: per-block water everywhere costs +16.32% of the whole mesh, 2x2 patches everywhere
+4.24% with worse fidelity at the seam, and a one-block strip on the chunk grid **+0.99%** with
full fidelity in the only place a seam can be.

**Safer action:** before refusing a request as view-dependent, ask what the boundary is
actually made of. The blend band is a distance field and moves; the ownership grid is chunk
arithmetic and does not.

### G156 - A screenshot is evidence of a moment, not of a state

**Trigger:** being handed an image to look at, and reading everything in it as deliberate.

**Trap:** a frame sent to show one thin band also contained a wide expanse of bare seabed with
straight edges. That was read as a rendering defect, traced to vanilla's liquid shader fading
its own water out over the last fifth of the view distance, written up as G124 returning, and
half-implemented as an ownership change - before the owner said the terrain simply had not
finished loading when he took it. The whole finding was retracted and the change discarded.

This is the third attribution in one session argued before it was evidenced. The other two
were the seam blend band and vanilla's foam; both were ruled out afterwards, and one shipped
as a build the owner reported worse.

**Safer action:** ask what the frame was taken to show and what state the world was in before
reading anything else in it as a defect. And when a screenshot suggests a mechanism, look for
a switch that tests it before writing the mechanism down as the cause.

### G157 - Chunk water ownership is not proof of depth coverage

**Trigger:** using the water chunk mask to discard depth fragments as well as surface color.

**Trap:** the owner saw pale moving marks at the junction even after the water shader moved
into OIT. Captured marks were already in ground color, at clear liquid-depth texels. Cached
geometry was discarded by chunk ownership despite absent vanilla coverage at those pixels.

**Safer action:** distinguish color ownership from actual depth coverage. In 0.4.18 a frozen
vanilla-only snapshot permits real cached geometry to supply clear junction pixels in owned
columns touching the cache; valid vanilla depth and interior ownership remain authoritative.
The owner confirms seam blips gone with repair off, but scattered blips remain and the broad
band is unchanged. This is not proof of complete surface continuity or a repair-free solution.

### G158 - Shader sampler registration must agree with explicit binds

**Trigger:** sharing engine includes between mod terrain, water and depth programs.

**Trap:** the engine assigns texture units by source declaration order, including inactive
conditional branches. An engine liquid-depth sampler aliased a mod climate-field unit;
correct-looking explicit binds alone did not reserve those units for later program activation.

**Safer action:** configure the engine's sampler table for every compiled variant, use the
same fixed layout for explicit binds, and restore/rebind units after program switches. Exercise
actual engine include expansion and collection. Depth shaders must sample immutable copies,
never an executable path to the image attached as their destination. See Session 79.

### G159 - A world-edge water band can be vanilla geometry

**Trigger:** a rectangular water artifact lines up with the vanilla/cache boundary in the view.

**Trap:** repeated cached-water toggles left the broad band unchanged, but its position made it
look like a handoff defect. The decisive test put a vanilla water chunk at the loaded-world edge
with no adjacent LOD chunk, and the band remained; pure vanilla reproduced it. The liquid
tessellator substitutes an empty chunk for a missing neighbour and emits a vertical water side
against the invented air. Seen nearly edge-on, that side becomes the pale chunk-stepped band.

**Safer action:** test the same geometry at a vanilla streaming boundary and in pure vanilla
before assigning a screen-space band to the mod. Suppress only outward horizontal liquid faces
whose neighbouring client chunk is genuinely absent. A loaded empty neighbour may be a real
shoreline and must remain authoritative. The owner confirmed this correction in 0.4.23.

### G160 - A plausible renderer fallback invalidates visual development evidence

**Trigger:** a new renderer looks unchanged, or a regression resembles an older renderer.

**Trap:** 0.4.26's dedicated water vertex programs failed on an undeclared shared value and its
OIT fragment failed to resolve a macro-renamed function. Automatic legacy/opaque selection still
drew believable water, so screenshots and A/B commands appeared to exercise the new renderer.
The log, read later, was the only proof that they had not. The same risk applies to packed terrain:
a per-section fallback can make a broken product path look healthy.

**Safer action:** required development paths must produce positive active-path diagnostics and
must fail visibly. As of 0.4.27 packed clustered terrain and dedicated water suppress cached output
and log `REQUIRED ... FAILED`; the per-section opaque, expanded-indirect and legacy water paths
cannot substitute. Keep a comparison path only when its command and status name it explicitly.

### G161 - A shared shader body has an interface even when GLSL cannot declare one

**Trigger:** extracting terrain and water shader code into common includes.

**Trap:** terrain's wrapper used to initialize `vhSurfaceColor` and `vhBlockSlot` before entering
the common vertex body. Water reused that body without supplying both values, so every dedicated
water vertex program failed at runtime. The OIT wrapper also renamed a function through macros;
the owner's AMD compiler did not resolve the resulting overload. Source-shape checks passed
because this workflow has no offline compiler for the engine-expanded GLSL.

**Safer action:** write down and initialize every value a shared body consumes in every wrapper,
check the expanded source, and prefer direct composition calls over macro-renamed entry points.
Treat the game's successful runtime compile message as a separate acceptance gate from C# and
static-source checks.

### G162 - Color-map parity includes alpha, not only the color you can see

**Trigger:** matching a translucent atlas texture by reproducing its tile and RGB tint while
packing the color map as an opaque RGB value.

**Trap:** vanilla's `getColorMapped` path multiplies the sampled water texture alpha by the
climate-water tint alpha. The cached path matched the live tile and visible tint but discarded
that map channel, leaving water roughly 1.5 times too opaque in the measured scene. Seabed,
clarity and Fresnel tuning then compensated for a material input that was simply missing.

**Safer action:** trace and preserve RGBA through climate pages, fallbacks, vertex interfaces and
the final texture operation. For translucent materials, an RGB match is not a material match.

### G163 - Full-resolution coverage must replace a coarser answer for a full-resolution stage

**Trigger:** combining a quarter-resolution liquid-depth target with full-resolution OIT
revealage to decide whether one surface pixel is missing.

**Trap:** ORing the two "clear" answers looked conservative but let a coarse depth texel admit
cached water where the full-resolution revealage proved vanilla water already drew. One capture
found 960 double-drawn pixels exactly along the pale dotted seam; they averaged about 21% brighter
than adjacent vanilla-only or cached-only water.

**Safer action:** use the signal whose resolution and meaning match the output stage. In 0.4.36
the immutable full-resolution revealage copy replaces quarter-depth for OIT surface coverage;
quarter-depth remains the authority for underwater depth. Do not broaden one into the other.

### G164 - Height and render distance can both be disguises for water viewing angle

**Trigger:** water matching at one camera height or render distance and diverging at another.

**Trap:** treating the change as a low-altitude color response or a render-distance fade creates
two correction curves for a single physical variable. Raising the camera and moving the handoff
farther away both change the eye ray's angle to a horizontal surface, which is the input vanilla's
Fresnel opacity actually uses.

**Safer action:** compare the surface viewing angle first and reproduce vanilla's angle response.
Use height and render distance only as convenient ways to sample that response, not as independent
causes. Then tune one ceiling across steep and shallow views.

### G165 - A water-only consumer does not make shared terrain state water-only

**Trigger:** fixing a one-frame water overlap by adding a weaker fact to the shared terrain
readiness mask, even in a spare channel that ground shaders do not intentionally read.

**Trap:** 0.4.38 recorded engine-announced chunk presence in the readiness mask and carried it
through shared rebuild, publication and ring-slot paths so cached OIT water could combine it with
same-frame revealage. All focused checks passed and the water looked fine, but the owner found
severe holes in terrain and the seabed. The exact statement inside that bundle was not isolated;
the before/after result rejects the shared-state extension as a whole.

**Safer action:** keep transient water corrections inside the dedicated water stage and leave
terrain readiness, ownership and mask-publication semantics unchanged. Preserve 0.4.37's local
depth-plus-revealage veto and accept its barely perceptible residual flicker. If terrain ownership
ever needs to change, treat it as separately authorized terrain work with a terrain-first runtime
regression case. Version 0.4.39 is the complete rollback and the accepted checkpoint.

### G166 - A spatial climate path can still hide a player-local seasonal scalar

**Trigger:** distant terrain already has per-position climate and mostly follows biome/season
boundaries, but one winter effect changes across the whole visible world when the player crosses
a chunk boundary.

**Trap:** snow was not part of the climate field. One `snowLineY` uniform was calculated from
the climate under the player and read by every distant vertex. Standing in a snowy chunk therefore
turned every visible mountain white at the next refresh; leaving it removed snow everywhere. The
existing spatial tint machinery made the renderer look more complete than it was.

**Safer action:** inventory every position-dependent input, not only the main color. The same
64-block climate-field sample now carries raw climate and a quantized snow threshold at the
terrain's own X/Z, and the field stays active for snow even when optional far-biome tint is off.
Reserve a global uniform only as the no-field fallback.

### G167 - “Observed” is provenance, not the observed snow answer

**Trigger:** persisting one bit that says vanilla terrain was sampled, then inferring bare ground
when the snow material is absent from a coarse section.

**Trap:** 0.4.43 stored observation provenance but expected the material runs to carry the answer.
A thin snow layer can lose a mip's majority material vote while its areal coverage remains real.
The surviving provenance bit then turned “snow did not survive downsampling” into “vanilla
authoritatively reported bare,” producing grey/sparse winter patches in coarser LODs.

**Safer action:** store two facts: `SnowStateObservedBits` and `SnowCoveredBits`. Propagate
coverage by area independently of material (snow wins a tie), and assert a parent answer only
when every contributing captured child was observed. Treat an old provenance-only row as unknown,
not bare; this is why format 4 has two append-only tails.

### G168 - Rare state changes do not justify a perpetual terrain recapture

**Trigger:** making existing cached snow eventually converge when most regions are permanently
warm, permanently snowy, or unchanged for most of the year.

**Trap:** the first background design loaded and recaptured one full-height chunk column every
second forever. Its instantaneous rate was low, but it paid continuous disk/load/capture,
persistence, mip, mesh and possibly network work for two rare transitions: accumulation and melt.
Adding “can accept snow” metadata would not solve the scheduling error; nearly every terrain
column has at least one snow-capable surface.

**Safer action:** follow the authoritative change mechanism. Loaded chunks recapture on vanilla
`ChunkDirty`. Dormant regions poll vanilla's snow snapshots every 30 seconds, cross coarse
accumulation/melt bands into finite campaigns, and wake one canary region per poll so unloaded
history eventually appears. Filter by observed bare/covered state, cap campaign issue work, and
do no column cycling between changes.

### G169 - Correlated interval counters do not identify a visible handoff flash

**Trigger:** one 30-second log interval contains exactly as many readiness losses as broader
geometry misses while the player reports vanilla-to-LOD flicker.

**Trap:** 0.4.48 recorded 28 readiness losses, zero public-false commits and zero range-sweep
losses, alongside 28 broader geometry misses. That correlation exposed a real semantic problem:
the continuing geometry guard included mutable cull visibility. Version 0.4.49 made the guard
gain-only and tested uploaded pool geometry. The next runtime log then recorded 158 gains,
**zero losses, zero owned-without-geometry cells and zero whole-section skips**, yet the owner
still saw the flicker. The internal correction was not a sufficient explanation of the visible
symptom. Stale mesh-result rejection had already failed the same acceptance test.

**Safer action:** instrument the exact chunk and frame of a reproducible transition. Distinguish
mask ownership, GPU mask publication, vanilla pool geometry, LOD selection/residency,
parent-child coverage and material revision before changing another rule. `.vhmask off`
stopping the flash proves the mask-enabled composite is necessary; it does not prove which side
of that composite failed.

**Corrected (Session 84, 0.4.53):** the counters were not merely insufficient. The interval quoted
above was the block written 30 seconds after joining; it contained no chunk crossing and so no
handoff at all, and it says nothing about the flash. The flash was a readiness *loss* that arrived
one frame late (G170). The safer action above is what found it: `.vhsky` measured the picture at
the exact frame.

### G170 - A Before-stage callback below order 1.0 sees the player before this frame's move

**Trigger:** reading `Entity.Pos`, a block position derived from it, or anything else the player's
physics moves, from an `EnumRenderStage.Before` renderer ordered below 1.0, and using it to
predict what the engine will draw this frame.

**Trap:** `EntityBehaviorPlayerPhysics` registers as a Before-stage renderer at order 1.0 and
advances the local player there in fixed 1/60 s steps. `ClientMain` then fills the frustum culler
from `player.Entity.Pos.AsBlockPos` and draws. Since 0.4.13 this mod's preparation (0.994) ran the
exact range sweep ahead of that step, so ownership measured vanilla's range from the pre-physics
block. Whenever a step crossed a block boundary that took a chunk past vanilla's range edge,
vanilla stopped drawing it in that frame and the mask released it in the next: one frame of open
sky, one to eight times a second while walking. `CaptureDrawRange` had assumed that a capture from
any render callback was this frame's; that holds for the culler's settings but not for the player.

**Safer action:** decide anything that must match this frame's culler after Before/1.0 (0.4.53's
late range check runs at 1.001) or in the opaque stage. `LateRangeCheckChecks` reads the physics
order from the installed engine, so a game update that moves it fails a check rather than
bringing the flash back.

**Found:** Session 84, from the `.vhsky` witness and decompiled 1.22.7 `ClientMain` and
`VSEssentials`. Fixed in 0.4.53.

### G171 - A CPU audit that runs where the decision runs shares its blind spot

**Trigger:** checking an ownership or visibility decision with a diagnostic scheduled in the same
callback, from the same inputs.

**Trap:** `.vhgap` asked the engine about every suppressing cell every frame, and `.vhgap release`
handed every gap it found to the cache in the frame it was found, yet the flash was unchanged. Both
ran at Before/0.994 with the pre-physics player block (G170), so every signal they read agreed that
vanilla was drawing chunks it was about to stop drawing for the coming frame.

**Safer action:** measure the output. `.vhsky` counts sky below the horizon after all opaque terrain
is drawn and reports, per flash, that frame's captured mask and the ground its ray should have hit.
It located the cause in one run.

### G172 - A visual bisection of a single-frame artefact is not a regression window

**Trigger:** bisecting a flash that lasts one frame (about 4 ms at the owner's 235 fps) by playing
each build briefly.

**Trap:** the owner judged 0.4.39 clean and 0.4.40 bad, and the first half of Session 84 then took
0.4.40's snow-line changes apart one by one. The cause had been present since 0.4.13 in both
builds: identical preparation order, identical ownership code, and the same 235 fps in every
archived session. Why 0.4.39 looked clean is not established.

**Safer action:** give each build an objective count (the `.vhsky` summaries) before treating a
visual bisection as the regression window, and keep code-history evidence (decompiled `dist/`
builds) beside it.

### G173 - A block material is not a liquid-rendering classification

**Trigger:** grouping every block whose `BlockMaterial` is Water, Lava or Ice into one generic
water flag because some ice blocks use a transparent render pass.

**Trap:** Vintage Story uses `EnumBlockMaterial.Ice` for solid `glacierice` and `lakeice` and for
opaque `packedglacierice`; world-generation layers build cold mountain summits from both glacier
forms. `FlagWater` did more than select translucency: it applied water waves and material, routed
the geometry through water/OIT, and submitted it to the quarter-resolution liquid-depth target.
A 0.4.54 staged capture had clear vanilla liquid depth but showed the mod-added depth mask covering
the entire glacier-shaped summit. Ground behind those false liquid samples received underwater
murk, producing the reported dark rim.

**Safer action:** classify for the consumer's semantic contract, not from a broad engine enum or
render pass. Liquid depth may contain actual liquid in front of ground, never merely transparent
solid terrain. Verify unusual families against the installed block definitions and inspect stage
coverage spatially; counts alone did not identify the summit. Re-derive material flags from live
blocks when cached sections load so a corrected policy fixes existing worlds without a purge.

**Found:** 2026-09-11, Session 85. Fixed in 0.4.55 by returning Ice to the ordinary solid-terrain
path; the end-to-end visual result remains awaiting owner acceptance.

### G174 - A shared frame-uniform upload reaches programs that never declared the uniform

**Trigger:** adding a uniform to the setter every cached-terrain program shares
(`ApplyFrameUniforms`) while declaring it only in the one shader body that reads it.

**Trap:** `ShaderProgramBase.Uniform(name, ...)` indexes a dictionary the engine fills from the
uniform declarations it finds in the program's source, so a name the program never declared
throws instead of being ignored. The water programs receive the same frame upload as the terrain
program. 0.4.57's nudge uniforms were first declared in the terrain body alone and would have
thrown in every water pass; self-review caught it before the build shipped.

**Safer action:** declare such uniforms in the include every frame-uniform program shares
(`lodvertexinputs.vsh`). A declared but unused uniform has location -1 and its upload is ignored.
`DistantAntiAliasingChecks` proves every uploaded `vhTaa*` name is declared there, and that every
uniform the anti-aliasing resolve and composite passes are handed is declared by their shaders.

**Found:** 2026-09-11, Session 86 (source-traced from the decompiled 1.22.7 engine).

### G175 - Per-pixel depth validation of temporal history fights the sub-pixel nudge

**Trigger:** refusing a pixel's history when its reprojected depth differs from the depth it
stored last frame, in a temporal anti-aliasing pass whose geometry is nudged a fraction of a
pixel every frame.

**Trap:** the nudge itself swaps which surface a pixel shows wherever a nearer edge meets farther
ground: tree tops, ridges and steps. On the owner's capture 17.9-23.1% of anti-aliased pixels had
a 3x3 neighbour beyond 0.4.57's 3% + 0.5-block tolerance. Those pixels refused their history every
frame and showed the raw nudge, so the anti-aliased terrain vibrated with the camera still. Two
smaller causes compounded it: pixels alternating with the sky had no history at all, and where the
nudge faded in, the history share faded in with it instead of arriving first.

**Safer action:** for static distant terrain, let the neighbourhood colour check reject stale
history rather than depth; take history only from pixels that were cached terrain or sky; blend
the sky pixels beside cached terrain; bring history to full share before the nudge begins. 0.4.58
did this and the owner found the still-camera vibration gone. The checks forbid a stored-depth
test in `lodtaaresolve.fsh`.

**Found:** 2026-09-11, Session 86 (measured on a capture; fixed and human-tested in 0.4.58).

### G176 - The render-scale upscale is read from the Luma target, not the primary one

**Trigger:** judging how the game upscales a render scale below 100% from the primary
framebuffer's texture filter.

**Trap:** at `ssaa <= 1` the primary colour texture is NEAREST-filtered, which suggests blocky
nearest-neighbour upscaling. The final pass samples the Luma framebuffer instead, which is always
LINEAR, so the upscale is smooth. Session 86 told the owner the wrong answer once before
decompiling the final pass.

**Safer action:** follow the texture the final pass actually reads before drawing a conclusion
about scaling artefacts.

**Found:** 2026-09-11, Session 86 (source-traced, decompiled 1.22.7).

### G177 - Changing FOV in game moves the near plane until restart

**Trigger:** an in-game FOV comparison, such as the 60-to-20-degree test that confirmed distant
shimmer was aliasing.

**Trap:** the camera starts with a 0.1-block near plane, but `OnFowChanged` sets it to
`clamp(0.1 - fov/90/25, 0.025, 0.1)` whenever FOV changes mid-session: 0.091 at 20 degrees and
0.073 back at 60, until the game restarts. Depth precision, and anything measured from it, then
differs from a fresh start at the same FOV.

**Safer action:** restart the game after an FOV test before taking depth or precision evidence at
the original FOV, or record the projection with the evidence (`.vhwater capture` stores it).

**Found:** 2026-09-11, Session 86 (source-traced, decompiled 1.22.7).

### G178 - A dialog re-composed inside its own click

**Trigger:** switching a `GuiDialog`'s tab by building a new composer in the tab strip's click
handler.

**Trap:** `capi.Gui.CreateCompo(name, ...)` disposes the composer already registered under that
name (`GuiComposerManager.Create`), while assigning `SingleComposer` disposes nothing. Re-composing
inside the handler disposes the composer whose element is still handling the click, and disposing
the old composer again by hand afterwards would dispose it twice.

**Safer action:** queue the re-compose with `capi.Event.EnqueueMainThreadTask`, so it runs once the
click has finished, and let `CreateCompo` do the disposal. The `.vhconfig` tabs work this way; the
owner confirmed tab switching in 0.4.60.

**Found:** 2026-09-12, Session 86 (source-traced, decompiled 1.22.7; human-tested).

### G179 - String literals in a built DLL can start at either byte alignment

**Trigger:** verifying that a packaged DLL contains a new string literal by decoding the whole file
as UTF-16 and searching the result.

**Trap:** user-string literals are UTF-16, but a literal can start at an odd byte offset, and a
decode from offset 0 finds only those that start on an even one. 0.4.58's zip check reported two
literals missing from a correct build; nothing was installed until the check was fixed.

**Safer action:** search both alignments (decode from offset 0 and from offset 1), or decompile the
type and compare the member directly, as the 0.4.61 check did for the anti-aliasing defaults.

**Found:** 2026-09-11, Session 86.

### G180 - An exact comparison against the atlas placeholder misses its opaque repack

**Trigger:** deciding whether a palette colour is the missing-texture placeholder by comparing it
to `UnknownTexturePosition.AvgColor` with `==`.

**Trap:** the atlas stores that average with alpha 0 (`0x00FCFCFC` on 1.22.7), while
`StableColorOf` averages its samples and packs them opaque (`0xFFFCFCFC`), and a colour read from
the texture's own pixels comes to the file mean (`0xFFF9F9FF`). Neither equals the atlas value, so
`RepairPlaceholder` never fired on that path and the grey or other-texture fallback was skipped.
Every chiselled block a server captured was stored near-white: on the owner's cache, 2,035 of 2,387
level-0 microblock entries.

**Safer action:** compare RGB only, against both the atlas average and the file mean
(`LodPaletteRepair.IsPlaceholder`). A zero reference matches nothing.

**Found:** 2026-09-12, Session 87 (measured from the cache; fixed in 0.4.62).

### G181 - A block whose look lives in its block entity has no type-level colour

**Trigger:** colouring a chiselled block, a clutter piece or a cluttered bookshelf from its block
type: on a server, which has no atlas; when recolouring a section a server captured; or when
repairing a stored colour of 0.

**Trap:** the type's own texture is the missing-texture image. The materials or props that make up
its look are stored per position in the block entity, which only the side holding the loaded chunk
can read. Ruins built from these blocks drew white at distance while looking right up close.

**Safer action:** read the block entity where the capture job is created, while the chunk is loaded,
and record a chisel as the ordinary block it shows, its most visible material
(`LodChiselStandIn`); every side then stores the same block and every colour path handles it. A
block-entity block that still reaches a type-level colour takes another of its own textures or
neutral grey, never the placeholder. Settled decision 36.

**Found:** 2026-09-12, Session 87 (measured from the cache and the decompiled engine; fixed in
0.4.62 and human-tested).

### G182 - A section's palette keeps the first colour it saw for a block id

**Trigger:** expecting a fresh client capture to correct a colour stored earlier for the same block
in the same section.

**Trap:** `LodSection.FindOrAddPaletteEntry` matches on block id alone and returns the stored entry
with its old colour. The owner stood beside the white tower while vanilla drew it and the client
captured those columns, but the chisels reused the stored near-white entry.

**Safer action:** correct stored colours on load, as `Reclassify` does for plain blocks and
`RepairPlaceholderEntries` for block-entity blocks, or change what the run records, as chisel
stand-ins do: a different block id gets its own entry.

**Found:** 2026-09-12, Session 87 (source-traced).

### G183 - The game API declares its own `Func`

**Trigger:** writing `Func<...>` in a file that imports `Vintagestory.API.Common`.

**Trap:** that namespace declares `Func<T1, TResult>` as well, so the build fails with CS0104,
ambiguous between it and `System.Func`. The existing code already writes `System.Func` for this
reason without saying so; the first 0.4.62 build failed on it.

**Safer action:** write `System.Func<...>` wherever the API namespace is imported.

**Found:** 2026-09-12, Session 87.

### G184 - A boundary marker takes the colour of the region it introduces

**Trigger:** maintaining separate colour arrays for the shaded LOD regions and the L1-L6 boundary
markers.

**Trap:** the marker number names the region to its right, not the zero-based boundary index. Using
the same index in both arrays shifts every frame by one colour, so a correct map still teaches the
wrong legend. The first 0.4.63 owner screenshots caught exactly this mismatch.

**Safer action:** keep one L0-L6 palette. Shade region `level` from entry `level`, and draw boundary
marker `i` from entry `i + 1`.

**Found:** 2026-09-12, Session 88 (human-observed; fixed and accepted in 0.4.64).

### G185 - A mirrored vanilla setting must remain vanilla-owned

**Trigger:** exposing a base-game setting in a mod editor as a convenience control.

**Trap:** copying the current value into mod configuration creates two sources of truth. Different
ranges, increments, commit timing, preset side effects or Cancel behavior then make the mod page
disagree with the game's own Settings page.

**Safer action:** source-trace the vanilla control and write its real setting with the same
interaction contract. The Horizons marker writes `ClientSettings.ViewDistance` at mouse-up, uses
32-1,536 in steps of 32, marks Graphics Custom, and is not rolled back by mod Cancel.

**Found:** 2026-09-12, Session 88 (source-traced from Vintage Story 1.22.7; harness-tested and
human-accepted in 0.4.64).

### G186 - The pause menu stops game ticks, not render stages

**Trigger:** expecting owner-thread loading and publication registered only on the ordinary game
tick to continue behind the single-player pause menu.

**Trap:** completed worker results, cache requests and mesh publication stop draining even though
the renderer is still being called. Forcing the game to unpause or synthesizing simulation ticks
would fix the symptom by changing game behavior.

**Safer action:** make the behavior optional and invoke the same bounded owner-thread work from the
continuing render stage at the ordinary 50 ms cadence. Do not advance simulation and do not add a
second publisher.

**Found:** 2026-09-12, Session 88 (source-traced and harness-tested; direct runtime progress witness
remains open).

### G187 - Nominal frame dimensions can disagree with the framebuffer being copied

**Trigger:** validating a GPU depth-copy path against the client API's reported frame width and
height while render scaling or another internal render-size choice is active.

**Trap:** the API can report the window's nominal 2560x1440 frame while the concrete primary
framebuffer, viewport and depth texture are all 1920x1080. A strict compatibility check then rejects
a valid depth copy and `.vhgpu on` fails even though the source and destination resources agree.

**Safer action:** use the concrete primary `FrameBufferRef` dimensions as the authority for resources
attached to that framebuffer. Keep nominal API dimensions only as a fallback and diagnostic, and log
both when they disagree.

**Found:** 2026-09-12, Session 89 (0.4.65 owner failure; corrected and owner-passed in 0.4.66).

### G188 - A held key was never offered again, and the only refresh replaced whole sections

**Trigger:** registering keys from a second cache (the server half's sibling cache, or a server's
manifest) against a client cache that already holds some of them.

**Trap:** skipping every key the client holds leaves a copy taken while pregen had filled only its
edge incomplete for good; the far view draws thin walls with sky between them. The level-0 refresh
that did exist replaced the whole section, erasing columns only the client held.

**Safer action:** merge column by column (`LodSection.MergeFrom`). Fill only missing columns on a
first sighting and at every parent level; let a level-0 row the server rewrote after the client saw
it replace the columns it carries, never erasing client-only columns or observed snow. Keep reloads
of held keys on local disk.

**Found:** 2026-09-12, Sessions 89-90 (645 of 2,912 client rows behind the server, measured offline;
fixed and human-tested in 0.4.68).

### G189 - An incremental rowid scan needs a full-scan backstop, and ModifiedMs sits behind the blob

**Trigger:** watching a SQLite table that another process writes, to find changed rows.

**Trap:** reading a column stored after a large blob (here `ModifiedMs` after `Data`) walks every
row's overflow pages: 828 ms of CPU per scan of a 1.8 GB cache. `rowid > @last` is almost free, and
`INSERT OR REPLACE` numbers a rewritten row above every existing one, but SQLite reuses the top rowid
after that row is deleted, so the incremental query alone can miss rows.

**Safer action:** read only `rowid` and key columns (answered from the primary-key index), scan
incrementally, and run a full key scan periodically and whenever `max(rowid)` falls below the highest
seen. `StoreChecks.AStoreRewriteGetsANewRowid` tests the premise against the real store.

**Found:** 2026-09-12, Session 90 (measured offline on the owner's cache; harness-tested).

### G190 - A producer that loads columns without keeping them must pace to the capture queue

**Trigger:** loading chunk columns for capture, as the savegame sweep does, without keeping them
loaded.

**Trap:** the engine unloads such a column within seconds. Loading at a fixed rate while capture fell
behind let the queue reach 792 and dropped 4,043 columns uncaptured in one session, and a column that
is never captured is a permanent gap in both caches.

**Safer action:** hold loads while the pipeline's total capture backlog (pending columns plus the
worker) is at its limit (`LodSavegameSweep.LoadsAllowed`, 32), or keep columns loaded until captured,
as the planned generation job does.

**Found:** 2026-09-12, Session 90 (log-measured; paced in 0.4.69, harness-tested only).

### G191 - Vanilla pregen can leave unfinished one-chunk lines that no cache holds

**Trigger:** thin, straight, one-chunk-wide gaps remaining in the distant view of a pregenerated area
after every merge has finished.

**Trap:** they look like a delivery bug. In the owner's 301 x 301 chunk pregen square, 2,461 chunk
columns sat at vanilla's pass 5 of 6 in north-south lines and were never captured; only 80 missing
columns were finished ones the server half had failed to capture.

**Safer action:** classify a gap offline before chasing it: the savegame mapchunk `currentpass`
(protobuf field 4, 6 is done, key `x | z << 27`) against both caches. Unfinished columns fill in once
vanilla finishes them, for example when a player flies near them.

**Found:** 2026-09-12, Session 90 (measured read-only against the live files; the owner's flight).

### G192 - An absence sample cannot accuse one producer while another generator is active

**Trigger:** checking that a savegame sweep did not generate positions which were absent before
its probe, while vanilla pregen or another producer may be filling the same world.

**Trap:** observing a position become present establishes a change, not which producer caused it.
A correct non-generating sweep then reports a false safety failure.

**Safer action:** observe the engine generation queue before the sweep issues its own loads. If
other generation was already active and an absent sample grows, report `Inconclusive`; reserve
`Unsafe` for a change the sweep can actually own.

**Found:** 2026-09-13, Session 91 (source-traced; fixed and harness-tested in 0.4.71).

### G193 - A server-cache row is not proof of saved terrain

**Trigger:** skipping real world generation because the LOD server cache already holds the column.

**Trap:** `/vhgen` and future frontier peeks deliberately put transient seed-generated terrain in
that cache without writing a map chunk to the savegame. Treating the row as provenance leaves the
real world permanently absent.

**Safer action:** pre-skip only the real-generation job's own durable completion bitmap, or prove
the map chunk exists through the engine/savegame existence test. Cache presence alone is never the
proof.

**Found:** 2026-09-13, Session 91 (source-traced; fixed in 0.4.74).

### G194 - One level-0 LOD section is four vanilla chunk columns

**Trigger:** translating a captured `SectionKey(0, sx, sz)` into progress for a plan indexed in
vanilla chunk coordinates.

**Trap:** L0 is 64×64 blocks while a vanilla chunk column is 32×32. Treating `sx,sz` as chunk
coordinates checks and credits the wrong plan cell, so durable progress, checkpoints, resume bits
and percentage logs do not advance.

**Safer action:** map an L0 key to its 2×2 chunk footprint and credit every planned chunk in that
footprint which the captured bitset actually contains. Keep section and chunk units explicit in
names and tests.

**Found:** 2026-09-13, Session 91 (source-traced; fixed and checked in 0.4.74).

### G195 - Public chunk unload is not a safe kept-generation release

**Trigger:** calling the public `UnloadChunkColumn` after generating with
`LoadChunkColumn(..., keepLoaded: true)`.

**Trap:** the public 1.22 path disposes the column immediately; it is not the normal age-and-save
path assumed by the initial design. Calling it before the job's exact capture/durability obligation
is satisfied can discard the state the lease existed to protect.

**Safer action:** bind the concrete WorldAPI wrapper to `ServerMain`, remove only the job's entry
from the force-loaded collection, and do so only after the job-owned obligation is safe. Fail closed
if that engine seam cannot be validated.

**Found:** 2026-09-13, Session 91 (decompiled 1.22.7; fixed in 0.4.75).

### G196 - A Settings action cannot depend on the integrated server it just paused

**Trigger:** putting a server round-trip control inside Vintage Story's ordinary Settings window
in integrated singleplayer.

**Trap:** opening Settings pauses the game and suspends the integrated server. The client can draw
and click the page, but its prepare/start request cannot be answered until the page closes; a brief
status change then reverts or times out.

**Safer action:** make the embedded page hand off to the same editor in a standalone live dialog,
closing Settings first. Do not resume simulation merely to make the control work.

**Found:** 2026-09-13, Session 91 (human-observed; fixed in 0.4.75).

### G197 - A bounded producer must wait on its own obligations, not global dirtiness

**Trigger:** deciding that a generation tile can finish only when the shared LOD pipeline has zero
dirty or capture work.

**Trap:** savegame sweep, snow refresh, player movement and ordinary captures may keep the global
counters nonzero forever. The bounded job has finished its own tile but appears unable to resume or
restart after a pause.

**Safer action:** at tile and final boundaries, wait only for the producer's kept-column leases and
its captured-but-not-durable L0 rows. Retain the shared backlog thresholds as brakes while issuing
new work, not as a global-idle completion condition.

**Found:** 2026-09-13, Session 91 (human-observed; fixed and then human-passed in 0.4.76).

### G198 - Prove the worldgen thread setting at runtime, not from the intended test label

**Trigger:** comparing world generation under different thread counts.

**Trap:** the relevant Vintage Story setting is `MaxWorldgenThreads` in
`servermagicnumbers.json`, read once per server process; there is no equivalent ordinary
`serverconfig.json` Boolean. A value of 6 means five additional pass threads plus
`chunkdbthread`, while `--reducedthreads` suppresses all additional threads. A mislabeled run can
therefore silently measure one thread.

**Safer action:** inspect the exact sandbox file and launch arguments, then record named engine
threads. A six-thread run must expose `worldgen1` through `worldgen5`; a two-thread run must expose
`worldgen1`. CPU samples can show how much each performs, but a zero one-second delta on a brief pass
does not mean the thread was absent.

**Found:** 2026-09-14, Session 91 (decompiled engine source plus controlled full/minimal-stack
runtime benchmarks).

### G199 - The engine's generating-chunk count never sees peeks

**Trigger:** pacing transient previews (`PeekChunkColumn`) by `WorldAPI.CurrentGeneratingChunkCount`.

**Trap:** the counter is the size of the real-generation request queue. Peeks live in a separate
peek queue, with capacity `RequestChunkColumnsQueueSize / 5`. A producer braked only by the counter,
as Farseer's is, never slows itself however many peeks it has queued.

**Safer action:** count your own peeks in flight and cap them. Read the counter only as the "real
loads are pending" signal, and yield to it. The silhouette builder does both: `MaxPeeksInFlight` 4
at a rate of `PreviewsPerSecond` 16, and it holds while 8 or more real chunks are pending. (The
in-flight cap was recorded here as 16 until Session 100; 16 is the per-second rate.)

**Found:** 2026-09-13/14, silhouette preparation and Session 92 (decompiled 1.22.x).

### G200 - The cached draw step returns early when no cached terrain is in view

**Trigger:** adding a pass that must draw where cached terrain is absent, such as silhouettes seen
out to sea.

**Trap:** `LodTerrainRenderer.PrepareRenderFrame` stops before preparing the frame when there are
no cached meshes or the draw list is empty, and `RenderFrame` then returns at once. A pass placed
inside `RenderFrame` draws nothing in exactly the views it exists for, and nothing reports it.

**Safer action:** give such a pass its own step after `RenderFrame` in `OnRenderFrame`, with its own
guards. Let it call `ApplyZFar`, which does nothing when the needed distance is unchanged.

**Found:** 2026-09-14, Session 92 (source-traced while building 0.4.77).

### G201 - The log folder holds only the latest launch

**Trigger:** diagnosing a hang or crash from the game's logs.

**Trap:** `VintagestoryData/Logs` holds only the most recent launch. When the game next starts, each
earlier launch moves to `Logs/Archive/<start time>`. Session 92 examined the clean 16:11 quit and
blamed it for a hang that happened in the launch before.

**Safer action:** check every launch in `Logs` and `Logs/Archive`. Look for "Stopped the server!" in
the server log and "Exiting game now" in the client log. A launch missing them is the one that hung
or was killed.

**Found:** 2026-09-14, Session 93.

### G202 - Server elapsed time stops while the game is paused

**Trigger:** timing server work with `sapi.World.ElapsedMilliseconds`.

**Trap:** it is `ServerMain.totalUnpausedTime`, which `ServerMain.Suspend` stops. A duration measured
with it leaves out every pause. In 0.4.80, "answered again 2 s after previews were stopped" spanned
minutes of wall time.

**Safer action:** use it deliberately where paused time must not count; the silhouette stall watch
does. Say which clock a logged duration uses, and otherwise use wall-clock time.

**Found:** 2026-09-14, Session 93 (source-traced, decompiled 1.22.7; log-measured).

### G203 - A preview never keeps the map region it builds

**Trigger:** issuing several previews (`PeekChunkColumn`) in a region the savegame does not hold.

**Trap:**
- `PeekChunkAreaLocking` builds each chunk's map region through `GetOrCreateMapRegionNoStore`. That
  runs every worldgen mod's region step, and the region is dropped when the preview ends.
- The next preview in the same region builds it again.
- The silhouette lattice previewed each unexplored region four times. The server debug log shows
  identical dungeon placements repeated four times per region on both of the owner's worlds.

**Safer action:** preview an unexplored region once, as 0.4.81 does, or use a height source that
builds no regions.

**Found:** 2026-09-14, Session 93 (source-traced; log-measured).

### G204 - A busy world-generation thread delays pausing and quitting

**Trigger:** anything that queues long work on the world-generation thread (`chunkdbthread`), such as
previews on a heavily modded world.

**Trap:**
- Pausing waits up to 60 s for every server thread (`ServerMain.Suspend`), then gives up with "The
  following threads were not suspended: chunkdbthread".
- Stopping also waits for the thread's current tick.
- In 0.4.80, on the owner's `bodanboys` world, one tick lasted about 3.5 minutes, and leaving the
  world took about 2 minutes.
- Why the tick lasted so long is not established. Both such stalls followed a burst of
  Watersheds/TerraPrety coast-map errors.

**Safer action:**
- Never assume the thread answers promptly.
- Count your own answers and stop early (G199, `LodGenerationStallWatch`).
- Stop on worldgen errors (`LodWorldgenErrorBrake`).
- Keep few items queued on the thread.

**Found:** 2026-09-14, Session 93 (log-measured; source-traced).

### G205 - With Watersheds installed, both height accelerators are one function

**Trigger:** comparing, choosing or combining Algernon's Terrain Sampler and Watersheds' own
sampler.

**Trap:**
- With Watersheds loaded, Algernon's Terrain Sampler answers
  `WatershedsGenTerra.GetPreWatershedsBlockColumnHeight`.
- Watersheds' `GenTerraSampler.SampleHeightWithContextCore` runs the same steps: column distortion,
  river-valley influence, `CalculateColumnHeight` and the climate sea-level adjustment.
- In both Session 94 runs the two gave identical errors, down to the worst points; only timing
  differed.
- "Pre-Watersheds" does not mean before rivers. Both include river valleys, and neither applies the
  channel carve that real generation does last.

**Safer action:**
- Treat them as one accelerator wherever Watersheds is installed.
- Judge Algernon's own generator only on worlds without Watersheds.
- Describe neither as "before rivers".

**Found:** 2026-09-14, Session 94 (source-traced, decompiled Watersheds 7.0.4 and Algernon's
Terrain Sampler 1.3.0; measured on two test servers).

### G206 - A silhouette builder on a dedicated server would serve every modded client

**Trigger:** letting a dedicated server hold a silhouette builder, for example to measure heights.

**Trap:**
- Until 0.4.83 the welcome advertised Features bit 2 wherever a builder existed.
- `LodAssistClient.SendSilhouetteRequest` sends whenever that bit is set.
- Every modded client that joined would then be sent silhouettes with no reach clamp. That is P3's
  unbuilt dedicated opt-in.

**Safer action:**
- Keep "holds heights" and "offers silhouettes" separate. Since 0.4.83, `silhouettesOffered` gates
  both the bit and the request handler.
- The developer switch `VINTAGEHORIZONS_SILHOUETTE_MEASURE` only keeps heights.
- The real opt-in waits for the reach clamp.

**Found:** 2026-09-14, Session 94 (source-traced).

### G207 - "Rain heights from old saves" also counts bare columns in new worlds

**Trigger:** reading the rain-height count in the silhouette status line.

**Trap:**
- A column counts when its worldgen height map equals its rain height map in all 1,024 entries.
- An old save's copied map looks like that, but so, presumably, does a new column with nothing
  above its ground.
- Two brand-new worlds counted 6 of 2,003 and 51 of 5,438 column loads.
- The heights are used as captured either way.

**Safer action:** do not read the count as old-save columns. Check how old the save is before
drawing conclusions from it.

**Found:** 2026-09-14, Session 94 (log-measured; the check is source-traced, the bare-column cause
inferred).

### G208 - Sea level comes from the world, not the map height

**Trigger:** reading heights against sea level, as in comparison figures, silhouette water or
land/sea counts.

**Trap:**
- The default formula gives about 110 on a 256-block map.
- Fresh test worlds with the owner's mods and configs report 82 (`sapi.World.SeaLevel`, recorded in
  0.4.84's map file).
- Session 94 inferred 110 and called the worst comparison points sea floor. At 82 they were shallow
  water and low land. In the dense run, 422 of the 430 cells off by 8 blocks or more were on land or
  at the coast.

**Safer action:** read sea level from the world and say where it came from. The comparison's
statistics already do, and the map file records it.

**Found:** 2026-09-15, Session 95 (measured; why it is 82 is not traced).

### G209 - Silhouette replies can beat the client store path

**Trigger:** persisting a tile only at network receipt while the store path is supplied later from
`LevelFinalize`.

**Trap:** a current tile can arrive, decode and display before the ordinary cache has supplied the
world-specific sibling path. The tile is authoritative but there is no open store to save it, so
the first session after adding persistence can become the one session that does not persist.

**Safer action:** retain the keys of authoritative network confirmations and backfill their immutable
tile snapshots when the store opens. Store reads must never acquire current-session authority over
a later server reply.

**Found:** 2026-09-16, Session 97 (source-traced; fixed in 0.4.89).

### G210 - Lowering a height-field join creates a sky slit unless geometry bridges it

**Trigger:** resolving overlap or z-fighting by lowering the Farseer boundary vertex below the real
cached edge.

**Trap:** the Farseer mesh is a top surface. Lowering its edge creates no vertical face between the
old and new heights, so the attempted underlap becomes a visible strip of sky. Exact coplanar
contact avoids that slit but can expose raster mismatch or visible overlap elsewhere.

**Safer action:** bridge only the unresolved edge, not the whole field. Keep the distant Farseer
surface at its representative height, slope an adjustable apron down near the replacement
frontier, and let exact live-geometry suppression make ready real terrain authoritative. The
0.4.94 skirt/buried-band implementation solved the literal slit but was rejected visually and
removed in 0.4.95.

**Found:** 2026-09-16, Session 97 (human-observed before 0.4.94; source/harness fix in 0.4.94).

### G211 - A reach or shape dial must not rebuild the whole Farseer disc at once

**Trigger:** clearing all client tiles/meshes when reach changes, or immediately marking every tile
dirty for each shape-slider event.

**Trap:** useful immutable tiles are thrown away and the whole disc decodes, snapshots, meshes and
uploads again. Dragging a dial multiplies that work and the owner sees severe game hitches even if
mesh construction itself uses a worker.

**Safer action:** retain the old/new reach intersection, update demand in place, prune gradually,
debounce shape changes, enqueue bounded rolling remeshes, cap worker jobs, and separately bound the
owner-thread publication/snapshot step.

**Found:** 2026-09-16, Session 97 (human-observed; fixed in 0.4.93).

### G212 - Cached terrain data is not drawable replacement geometry

**Trigger:** suppressing Farseer because the LOD cache has data, a request exists, or a build is in
flight.

**Trap:** those states can precede GPU-ready replacement geometry. Farseer disappears first and the
player sees sky until vanilla or LOD publication catches up. The same distinction already matters
in the vanilla-to-LOD handoff: availability is not appearance readiness (G94).

**Safer action:** keep Farseer until a resident vanilla or LOD representation can actually cover
the ground cell. Unknown and partial answers fail open. Tie the proof to the installed mesh
publication, not mutable source data or scheduled work.

**Found:** 2026-09-17, Session 98 (human-observed in the 0.4.98–0.4.99 handoff; fixed by the
0.4.101 installed-mesh coverage path).

### G213 - A frustum-selected draw list cannot own persistent ground

**Trigger:** reusing the current frame's visible/selected LOD commands to decide whether Farseer
must yield.

**Trap:** frustum membership changes with camera direction. Turning in place then changes terrain
ownership: Farseer shifts back and forth, and a gap can open beside real LOD even though neither
terrain dataset changed.

**Safer action:** choose ownership from stable resident state and camera position/distance only.
Frustum and current-frame selection may reject draw work, but they may not decide which system owns
a ground cell.

**Found:** 2026-09-17, Session 98 (human-observed in 0.4.99; camera reaction removed in 0.4.100).

### G214 - L0 readiness does not describe the LOD terrain that is on screen

**Trigger:** recognizing only level-0 meshes when another renderer can substitute resident parent
levels.

**Trap:** the stable answer is still wrong. A coarser parent at L1–L6 can visibly draw while the
level-0 descendants are absent, so Farseer remains over terrain the player can already see. This
caused the broad overlap in 0.4.100 after its camera-yaw fix.

**Safer action:** represent every resident level. Select the distance-appropriate target, then a
resident coarser parent, then resident finer descendants. Test the ordering independently of the
camera frustum.

**Found:** 2026-09-17, Session 98 (human-observed and source-traced in 0.4.100; harness-fixed in
0.4.101).

### G215 - A resident coarse mesh does not necessarily cover its whole footprint

**Trigger:** treating a section key or its rectangular bounds as proof that every enclosed ground
cell has opaque terrain.

**Trap:** sparse, partial, clipped or otherwise empty cells are overclaimed. Suppressing the
fallback across the whole coarse footprint can expose sky even though the mesh is resident.

**Safer action:** derive compact coverage from the opaque top surfaces the mesher actually emits,
at the required ground-cell resolution, and carry that immutable mask with the exact mesh result
through publication. Do not rescan geometry or query later-mutated section data at draw time.

**Found:** 2026-09-17, Session 98 (source-traced; implemented and harness-tested in 0.4.101).

### G216 - Camera-relative hill shading looks like moving geometry

**Trigger:** adding contrast to distant Farseer ridges from view direction or another
camera-relative light vector.

**Trap:** the shades change while the player turns. On deliberately coarse, overlapping hills that
reads as the terrain shifting—the same class of symptom as an unstable ownership mask.

**Safer action:** reconstruct the geometric surface normal and shade from a world-space light
direction. A gentle world-distance term may separate depth layers; neither answer should depend on
camera yaw.

**Found:** 2026-09-17, Session 98 (flat ridges human-observed; world-directed slope/depth fix is
source/static-tested in 0.4.101 and awaits visual acceptance).

### G217 - Fog painted into terrain surfaces stays flat and exposes the ownership grid

**Trigger:** concealing the Farseer/LOD join by blending a fog colour directly into each terrain
surface near the exact ownership mask.

**Trap:** the colour can soften a line but cannot hang above the surface, spill down a cliff or
occupy empty air. Its shape inherits the 32-block cells and terrain triangles, so a heavy treatment
looks like bright angular paint rather than atmospheric fog.

**Safer action:** keep the exact ownership mask, but use it to build separate translucent geometry.
Cross vertical sheets with horizontal sheets, put the greatest density on the real boundary, and
fade every exposed outer, upper and lower vertex to zero. Use world-space noise to break up the
remaining regular layers.

**Found:** 2026-09-17, Session 99 (human-observed in 0.4.102; replaced in 0.4.103).

### G218 - A seam veil drawn before OIT cannot reliably conceal water

**Trigger:** drawing translucent seam fog during the opaque callback and expecting later water to
remain both intact and hidden by that fog.

**Trap:** primary alpha belongs to later water/OIT composition. Writing alpha or depth can remove
or blacken water; even an RGB-only opaque-stage fog draw is earlier than water, so OIT can repaint a
sharp water/chunk edge over the veil afterward.

**Safer action:** treat the fog as a final atmospheric overlay. Sample primary depth read-only,
preserve primary alpha and depth, and composite the fog after OIT. Keep water shaders, liquid depth,
revealage, ownership and the shared terrain readiness mask out of a fog-only correction.

**Found:** 2026-09-17, Session 99 (black/missing water human-observed in 0.4.103; post-OIT result
human-positive in 0.4.107).

### G219 - The engine blend helper may configure attachments a custom framebuffer does not own

**Trigger:** calling `GlToggleBlend(true)` while a custom framebuffer exposes only
`ColorAttachment0`.

**Trap:** with SSAO enabled, the helper also configures indexed blend outputs used by the engine's
other attachments. Those attachment indices do not exist on the one-color fog framebuffer, so the
first otherwise valid draw raises OpenGL `InvalidOperation`.

**Safer action:** on a custom single-attachment pass, enable blend directly and set its blend
equation/function explicitly. Capture and restore the surrounding GL state, and keep a static check
that the general helper is not reintroduced.

**Found:** 2026-09-17, Session 99 (production-observed in the 0.4.104 client log; source-traced and
fixed in 0.4.105).

### G220 - Shader compilation does not validate the type of a later uniform upload

**Trigger:** uploading an integer C# value to a GLSL uniform declared as `float` and assuming the
runtime will convert it because the shader compiled.

**Trap:** the shader program compiles successfully, then the typed OpenGL uniform call raises
`InvalidOperation` on the first draw. The failure looks like a framebuffer or blend problem because
it occurs in the same guarded pass.

**Safer action:** match the C# upload overload to the declared GLSL type explicitly. When an integer
domain value feeds a float uniform, cast at the call site and protect the call with a source/static
regression check.

**Found:** 2026-09-17, Session 99 (production-observed in the 0.4.105 client log; fixed in 0.4.106,
whose fog the owner then reported as very good).

### G221 - Independent seam strips leave a diagonal hole at a turn

**Trigger:** building a fog strip per straight ownership edge and ending each one at its grid
vertex.

**Trap:** at a ninety-degree turn, the two rectangular strips cover their own axes but not the
diagonally adjacent quadrant. From above, the gap looks exactly like a missing fog chunk even
though both incident edges are present.

**Safer action:** detect vertices with both a horizontal and vertical incident boundary and add one
layered low-density cap there. Straight vertices need no cap, so do not thicken every segment to
solve a corner-only topology problem.

**Found:** 2026-09-17, Session 99 (human-observed in 0.4.106; first filled in 0.4.107).

**Superseded by G223, 2026-09-18, Session 100.** The requirement stands - a turn must not leave a
missing adjacent quadrant - but a separate cap is the wrong remedy, because it is a second body at a
different density and its join stays visible however large it is. Each strip now fades its own
geometry one spill past any end the frontier does not continue collinearly, and two swept ends fill
the quadrant between them.

### G222 - A circular cap does not fill the square footprint of intersecting fog bands

**Trigger:** covering G221's turn with an octagonal/radial cap whose diagonal coordinates are
`spill × 0.7071`.

**Trap:** each straight strip reaches the full spill distance on its axis, but the radial cap
reaches only 70.7% on each diagonal axis. The remaining square corners still read as a rounded bite
or missing adjacent fog section.

**Safer action:** extend the cap to `spill` on both diagonal axes. Keep all perimeter weights zero,
so interpolation still fades organically and the larger footprint does not become a hard square.

**Found:** 2026-09-17, Session 99 (human-observed in 0.4.107; source/harness-fixed in 0.4.108 and
awaiting focused visual acceptance).

**Reversed by G223, 2026-09-18, Session 100.** The square footprint this prescribes is itself the
sharp corner: it reaches `spill x 1.414` on the diagonal while the straight bands reach `spill`, so
it protrudes at every turn. Neither the circle nor the square is right; the footprint wanted is the
segment dilated by the spill, which reaches `spill` in every direction.

### G223 - A square swept end grows a tooth at every turn of a staircase frontier

**Trigger:** giving a fog strip a full square footprint wherever the ownership frontier does not
carry straight on, after G221/G222.

**Trap:** the frontier is normally a 32-block chunk-grid staircase, so every vertex is a turn and
every strip is swept at both ends. Each square corner reaches `spill x 1.414` diagonally, past the
reach of the neighbouring strips, and their union becomes a regular sawtooth along both rims. It is
correct only for the rare isolated turn between two long straight runs, which is the case a single
annotated screenshot happened to show.

**Safer action:** close a swept end as an arc, so a strip's footprint is its segment dilated by the
spill - a stadium, reaching one spill in every direction. Two cross-sections at `(0.556, 0.831)` and
`(1.0, 0.12)` in along/half-width fractions of the spill approximate it well enough. Judge such a
change on a staircase fixture, never on one isolated turn.

**Found:** 2026-09-18, Session 100 (human-observed in 0.4.109; reproduced offline from the real mesh
and fixed in 0.4.110).

### G224 - A fixed quad diagonal cuts the far triangle off from a lone dense corner

**Trigger:** stitching a strip's cross-sections into quads with a constant triangulation, where one
end of the strip fades to zero.

**Trap:** a quad whose only non-zero weight is at one corner has that corner on just one of its two
triangles under a fixed diagonal. The other triangle interpolates flat zero, so the fade loses half
its quad and the shape gains a diagonal bite.

**Safer action:** split along the diagonal joining the two heavier corners. An ordinary quad has
equal sums on both diagonals and keeps its original split, so nothing else moves.

**Found:** 2026-09-18, Session 100 (measured offline while building 0.4.109).

### G225 - Shading does not move an outline that geometry put there

**Trigger:** trying to soften a hard-edged translucent volume by curving its density profile or by
warping its noise.

**Trap:** two independent failures, both of which look right on paper:

- a smooth curve through zero, such as `smoothstep`, leaves the zero-crossing exactly where it was.
  Only the gradient's shape changes, and the silhouette does not move at all.
- a domain warp whose wavelength is larger than the features being hidden translates them instead of
  dissolving them. A warp at 1.6x the billow size slid a 32-block sawtooth sideways, intact.

**Safer action:** fix the geometry first and use noise to disguise what remains, not to fix it. Give
a warp at least one octave at or below the feature size, and displace the profile coordinate before
shaping it rather than after. Render the outcome before shipping it: a plan-view rasterization of the
real mesh reproduces this in seconds.

**Found:** 2026-09-18, Session 100 (two rejected 0.4.110 attempts, rendered offline).

### G226 - A drift dial must be integrated, and its rate must be in world units

**Trigger:** animating a world-anchored noise pattern under a player-adjustable speed.

**Trap:** two mistakes that combine into "the dial does nothing":

- offsetting by elapsed time multiplied by the live speed is not a rate. Every change to the dial
  displaces the whole pattern by the elapsed time, so it lurches while the slider is dragged and
  sits still afterwards, which reads as the dial being the only thing that moves it.
- expressing the rate in noise units multiplies it by the noise scale. 0.006 noise units per second
  is 0.6 blocks per second at a 100-block billow size, and invisible at seam distance.

**Safer action:** integrate the speed into an accumulated phase from the frame delta, clamp each
frame's step so a pause cannot teleport the pattern, and wrap the accumulation so a long session
keeps its interpolation precision. Express the rate in world units, applied before the noise
scaling, so a size dial changes size and not pace.

**Found:** 2026-09-18, Session 100 (human-observed through 0.4.109; fixed in 0.4.110).

### G227 - The savegame sweep does not yield to the engine's chunk queue

**Trigger:** attributing slow terrain loading to whichever Vintage Horizons feature is under
development.

**Trap:** `LodSavegameSweep.ObserveOtherGenerationQueue` records `CurrentGeneratingChunkCount` for
absence classification only. `StepLoad`'s allowance is its configured rate and the capture backlog
and nothing else, so it keeps issuing `LoadChunkColumnPriority` while a player waits for chunks,
where the silhouette builder holds at 8 pending (G199). It is also centred on spawn, not on the
player. Measured on the owner's machine it ran 7-14 columns/s for its whole load pass, pinned at its
capture-backlog ceiling, while Farseer managed 2 previews in eight minutes.

**Safer action:** before blaming a feature, read the periodic status line of every producer over the
same log window and compare their rates. The counters are already there.

**Found:** 2026-09-18, Session 100 (log-measured from the owner's `server-main.log`; source-traced).

### G228 - A one-millisecond issue budget does not make a priority chunk load background work

**Trigger:** treating a small scheduler callback or a per-second item cap as proof that the work it
submits cannot delay player terrain.

**Trap:** the sweep's 1 ms budget stops only its own loop between submissions. In the installed
engine, `WorldAPI.LoadChunkColumnPriority` aliases `ServerMain.LoadChunkColumnFast`, enters
`fastChunkQueue`, and is drained through `loadChunkAreaBlocking` before the normal bounded
`tryLoadOrGenerateChunkColumnsInQueue` slice. That blocking loop can advance player requests in the
shared request set, but the outer scheduler does not regain control until the priority target
finishes. Database load, chunk activation, main-thread callbacks, capture and storage are all
outside the 1 ms issue budget. The existence-test queue is likewise drained before the normal
slice.

**Safer action:** measure downstream queue and player-readiness effects, not only enqueue duration.
Background indexing must have an explicit player/engine-pressure yield in addition to its rate and
capture-correctness brakes. Run the controlled integrated sweep-on/sweep-off route before choosing
the signal, threshold or engine API; preserve the complete-neighbourhood and capture-before-unload
invariants.

**Found:** 2026-09-18, Session 101 (current 0.4.110 source, installed-engine decompilation and the
owner's 0.4.108 production log; player-latency magnitude remains unmeasured).

### G229 - The engine generation count misses ordinary saved-column terrain demand

**Trigger:** using `CurrentGeneratingChunkCount` as the sole signal that the startup sweep should
yield before issuing another saved-column priority load.

**Trap:** the counter represents active generation, not every player-visible saved-column load. In
the frozen-profile load-phase A/B, gates at 8 and then 1 recorded no useful holds during the
saved-terrain portion and left the 1% low/tail near the ungated result. The counter rose only when
the route reached genuinely new terrain. Sampling it every 50 ms makes short-lived states easier to
miss as well.

**Safer action:** use direct player terrain activity as the primary scheduling signal: join/rejoin
and chunk-boundary crossings open a quiet window in which the background sweep submits no priority
loads. Retain the engine count at threshold 1 as a secondary guard for real generation. Keep the
allowance capped so a long hold cannot create a catch-up burst.

**Found:** 2026-09-19, Session 102 (controlled integrated A/B; fixed and follow-up measured in
0.4.111).

### G230 - A generated route centerline can still generate a view-distance halo

**Trigger:** calling a repeated route “previously generated terrain” because every waypoint or its
centerline already exists in the frozen save.

**Trap:** the player loads an area, not one line. Session 102's route ended at generated chunk X
8050, but the final save gained 178 `mapchunk` rows at X 8053–8062 and Z 7990–8012. It also gained
2,136 `chunk` rows, exactly 178 columns times 12 vertical chunks. The view-distance halo therefore
crossed the frontier late in every otherwise-repeatable route.

**Safer action:** compare the save's terrain row counts and coordinate set before and after a run.
Describe the route as mixed unless the entire loaded halo was already present. For a sustained
world-generation benchmark, start at a known frontier and route far enough into untouched terrain
that the measured window is dominated by generation rather than saved-column streaming.

**Found:** 2026-09-19, Session 102 (read-only before/after save-database comparison).

### G231 - `GameWorldSave` fires before the engine writes, not after

**Trigger:** planning to re-read or re-fingerprint what the engine has "just written" from inside a
`GameWorldSave` handler.

**Trap:** `VintagestoryAPI.xml` documents `IServerEventAPI.GameWorldSave` as "Triggered **before**
the game world data is being saved to disk", and `LodServerCaptureSystem.OnGameWorldSave` already
depends on that ordering to flush the mod's own data *into* the same save. Anything read from the
database in that handler is pre-save. There is no post-save event in the API;
`EnumServerRunPhase.Exit` ("shutdown complete") is the only candidate for a final pass and its
ordering against the database close is unverified.

**Safer action:** use the event to learn *which* columns are about to be written, then do the read
afterwards on a thread that can tolerate `SQLITE_BUSY`. A record derived from pre-save bytes is
merely useless where it causes an extra reload, but it is unsafe wherever something later skips the
comparison on the strength of it.

**Found:** 2026-09-19, review of `PLAN_TERRAIN_CHANGE_DETECTION.md` §11 (API documentation read
offline; the plan's §4.1.4 asserted the opposite and was corrected).

### G232 - A savegame fingerprint records that you looked, not that you captured

**Trigger:** storing a per-column content fingerprint when the column's savegame row is read, then
skipping columns whose fingerprint still matches.

**Trap:** the two facts are different. If a fingerprint is committed at read time and the column's
capture then fails - three columns unloaded before the capture queue reached them in the owner's
2026-09-17 log - the cache never holds that column, yet its fingerprint matches an unchanged file
forever. Neither the next sweep nor a background audit has a mismatch to act on, because an audit
compares the file against the stored fingerprint and the file did not change. The hole is silent
and permanent.

**Safer action:** commit the fingerprint only in the completion path that marks the column durable.
Only "we hold a correct capture of this" may cause a skip; "we have looked at this" may not.

**Found:** 2026-09-19, review of `PLAN_TERRAIN_CHANGE_DETECTION.md` §11 (the plan did not state
when a fingerprint became stored, and both readings were available).

### G233 - A short session never leaves the sweep's probe pass

**Trigger:** reading a player's "I could not tell a difference" as evidence about the savegame
sweep's load scheduling.

**Trap:** the startup sweep probes before it loads, and on a large world the probe pass alone
outlasts an ordinary session. The owner's 2026-09-19 session ran 16:13:21 to 16:18:36 and had
examined 90% of 70,225 positions when he quit. The load phase never started, so 0.4.111's
player-aware gate - which guards `StepLoad` only - could not have held anything. The play verdict
was about a build whose changed code never executed.

**Safer action:** before attributing a play observation to load scheduling, confirm from
`server-main.log` that the sweep reached the load phase. For the same reason, a benchmark route
must be long enough to leave the probe pass, or it compares probe pacing under three names.

**Found:** 2026-09-19, production observation read offline from the owner's `server-main.log`.

### G234 - A quiet window renewed by movement is a stop, not a slow-down

**Trigger:** pausing background work for a fixed quiet window after each sign of player activity,
where the activity signal is something a moving player produces repeatedly.

**Trap:** the window is renewed before it expires, so the pause never lapses. 0.4.111 held new
savegame-sweep loads for five seconds after any chunk-boundary crossing. A 32-block chunk crossed
more often than every five seconds is 6.4 blocks per second - an ordinary movement speed - so above
it every crossing lands inside the previous window and the sweep stops for as long as the player
keeps walking. Measured 2026-09-19 on the owner's frozen world: about 880 sections/min ungated,
about 110/min held at 5.33 blocks/s, and a whole minute at 7/min at 6.67 blocks/s. Nothing chose
that threshold; it fell out of two constants picked for other reasons.

**Safer action:** when a hold is renewable by ordinary behaviour, give it a floor rather than a
zero, and derive the floor from the configured rate so it scales. Draw the floor from its own
allowance that only held ticks spend, so a long hold cannot burst on release. Then work out the
speed at which the window is self-renewing and write it down, because it is the number that decides
whether the mechanism is a throttle or an off switch.

**Found:** 2026-09-19, derived from the two constants and confirmed by three measured routes; fixed
in 0.4.112.

### G235 - An absolute gate hides the gate you are trying to measure

**Trigger:** testing a newly added scheduling gate on a route where an older, stricter gate is also
firing.

**Trap:** the first verification run for 0.4.112's sweep floor used the virgin-frontier route and
recorded one single load under 2,478 player holds, which looks like a broken fix. It was not. On a
virgin frontier real chunk generation is permanently in flight, so the absolute engine gate returns
a zero allowance on nearly every tick and the floor has nothing to clamp. The floor only has an
effect on saved terrain, where the engine counter is blind to ordinary priority loads (G229) and
the player hold is the only thing stopping the sweep.

**Safer action:** before measuring a gate, check which other gates fire on that route and pick one
where the gate under test is the binding constraint. The sweep's telemetry prints player and engine
holds side by side for exactly this reason; equal counts mean the route cannot separate them.

**Found:** 2026-09-19, while verifying 0.4.112.

### G236 - There is no hook after the engine's final save

**Trigger:** designing anything that must record the savegame's state once the last write of a
session has landed - a clean-shutdown marker, a post-save re-read, a "nothing is pending" flag.

**Trap:** no such moment is reachable from a mod. Decompiled 2026-09-20 from the installed engine:
`ServerSystemLoadAndSaveGame` subscribes to `OnGameWorldBeingSaved` in `OnBeginRunGame`, while mods
subscribe during world load, so mod handlers run first and the write follows (G231). At shutdown
`OnBeginShutdown` triggers that same event and saves synchronously inside it. The only phase after
everything is `EnumServerRunPhase.Exit`, and `ServerMain.EnterRunPhase` returns immediately for
`Start` and `Exit` without notifying a single system or mod. `OnSeperateThreadShutDown` disposes
the game database before that point anyway.

**Safer action:** do not build on a post-save moment. Either record the state before the write and
treat what the write will touch as unverified, or verify from the file at the next startup. The
terrain-change plan's §4.1.5 fast path assumed this hook existed; it does not, which is one of the
two reasons that fast path was not built.

**Found:** 2026-09-20, Session 103 (engine decompilation).

### G237 - A guard flag that is never cleared silences a whole periodic arm, silently

**Trigger:** a tick handler that returns early on a state flag, where some later callback is
supposed to clear it.

**Trap:** `LodSavegameSweep.verifying` was set when the absence verifier started and never cleared.
`Step()` begins with `if (!active || verifying) return;`, so every tick after the startup sweep
completed did nothing at all - in every released build from `0596d30` (2026-08-02) onward, including
the accepted 0.4.64 baseline. Automatic dormant-terrain snow refresh, its canary and its regional
campaigns have therefore never run once. Nothing reported it, because the status line reads
configured state - candidate counts, rates, campaign totals - rather than whether any work has
happened, so it always printed as healthy.

**Safer action:** clear a guard in the same commit that sets it, and prefer a counter of work done
over a description of configuration in any status line that is meant to show a background arm is
alive. A feature that reports its settings will look identical whether it is running or dead. When
reviving such an arm, check what it does before letting it run: this one issued engine priority
loads with none of the brakes the load pass had gained in the meantime.

**Found:** 2026-09-20, Session 103, while building the audit into the same dead code path.

### G238 - A continuously-modified item's age is not a queue latency

**Trigger:** reading, or gating on, an "oldest pending" age printed beside a pending count.

**Trap:** the owner's 2026-09-20 log read `52 dirty, oldest dirty 307.6s, 7.38 saves/s`. Those three
figures cannot describe one queue - 52 items at 7.38 a second drain in seconds - and the pairing
reads as a stalled writer. It is not one. `LodSaveDirtySet.Add` returns false for a key already
present, so a re-dirtied section keeps its original first-dirty timestamp, and `LodWorld.CompleteSave`
clears dirty state only when the completed revision is still the newest, so a section modified again
while its save is in flight stays dirty. During a sweep the coarse parent sections are re-dirtied
continuously by mip propagation, so one of them ages without bound while the writes keep up
perfectly. A brake on that age would have fired permanently during every sweep.

**Safer action:** trace a status number to its definition before making it a scheduling input, and
check whether the figures printed in one line can describe the same thing - inconsistent arithmetic
between them is the cheapest available signal that one of them does not mean what it says. Report
"has been changing for" separately from "has been waiting to be written", and print the real queue
depth next to it. The save queue was already bounded at `MaxStorageBacklog`, so no new brake was
warranted at all.

**Found:** 2026-09-20, Session 104, while building a storage-pressure brake that turned out to be
unnecessary; the plan's §11.3 direction and a recommendation to the owner were both reversed.

### G239 - A whole-row hash reports a berry bush as loudly as a landslide

**Trigger:** fingerprinting stored data to decide whether work can be skipped, by hashing the whole
stored record because that is the obvious thing to hash.

**Trap:** `LodSavegameScan` hashed every byte of a chunk row, and a chunk row carries far more than
terrain: block light, light saturation, entities, every block entity's private state, other mods'
chunk data, and placed/removed counters. Measured on the owner's world across 25 days of play,
13,754 of 62,390 columns moved their fingerprint and **17.8% of those moved only for state a
captured section cannot show** - berry-bush growth timers, mycelium, transient countdowns, mobs
walking past, Watersheds' groundwater simulation. The cost is a load and a recapture per column,
repeated every session. In byte terms the row is not mostly terrain either: other mods' chunk data
was 84% of the bytes in a sample of his rows.

**Safer action:** hash what the consumer reads, and express the rule as an exclusion list so an
unrecognised field still counts as a change. Identify the exceptions from the game's own registry
rather than by name - the capture reads chiselled blocks, and hard-coding `MicroBlock` and `Chisel`
would have dropped a modded microblock subclass out of the fingerprint silently. Version the rule
and drop stored claims when it changes: a claim under an older rule cannot match, so it would report
its column as changed forever, quietly.

**Found:** 2026-09-20, Session 105, measuring the terrain-change plan's section 10 question.

### G240 - A claim recorded before the work is a claim about a state you never held

**Trigger:** recording "we hold this version of X" using a reading taken before the operation that
produced the holding.

**Trap:** the sweep fingerprints the savegame once at world start, then loads a column and captures
it. Loading a dormant column is itself what changes it: the engine catches up its transient blocks,
its snow and its block entities on the way in, and the next save writes that. The capture is
correct, because it reads the loaded chunk - but the claim carried the *pre-load* fingerprint, so
the file no longer matched it and the next start loaded the column again for nothing. Measured at
7.0% of claims on 0.4.116 and 399 of 10,439 columns (3.8%) on 0.4.118. Three independent signals
agreed: claims made in the sweep's first 90 seconds were stale at 10.6% against 5.2% for later ones;
in the sandbox, columns the sweep had loaded changed at 33.6% against 0.019% elsewhere; and 48 of
162 disputed columns had had their cached section rewritten after the claim, against 2.2% of
agreeing claims.

**Safer action:** separate taking the work from claiming it. Wait for the write that follows, then
read the state back and claim that. Keep a fallback for the tail - a session ending before the next
save must still claim something, or a whole save interval's proven work is re-examined next start -
and refuse to claim while any related work is still queued.

**Found:** 2026-09-20, Session 105; fixed in 0.4.119.

### G241 - An arm that starts when a job finishes has no voice if the log speaks only while jobs run

**Trigger:** background work that begins at the completion of a startup job, in a system whose
status line is gated on "something is still running".

**Trap:** `LodServerCaptureSystem` printed its status only while `HasActiveCacheWork()` was true,
and that returns false once the sweep is `Done` - which is exactly when the background audit and the
dormant-terrain snow refresh start. Both ran for nine minutes on the owner's world on 2026-09-20 and
the log recorded nothing about either, so the question "what does the audit actually catch on his
setup?" stayed unanswerable after the feature had already run. Reading the cache afterwards showed
336 claims written in those quiet minutes but could not attribute them between audit recaptures and
the capture queue draining.

**Safer action:** print on evidence of work done rather than on a job being incomplete, and give the
arm a signature that changes when it acts so an idle world still stays quiet. This is G237's shape
repeating: an arm with no voice cannot be checked, whether it is dead or merely unobserved.

**Found:** 2026-09-20, Session 105; fixed in 0.4.119.

### G242 - Section.ModifiedMs is uptime; ColumnFingerprint.ModifiedMs is wall clock

**Trigger:** correlating cache rows with each other, with log timestamps, or with a session's
timeline during offline analysis.

**Trap:** `LodStore.ExecuteUpsert` stamps `Section.ModifiedMs` with `Environment.TickCount64`, which
counts milliseconds since the machine booted, while `SaveColumnFingerprint` stamps
`ColumnFingerprint.ModifiedMs` with `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`. Subtracting
one from the other yields about -1.79 billion seconds, and rows written before the last reboot carry
a *larger* value than rows written after it, so "newest" sorts backwards across a reboot.

**Safer action:** convert through the machine's boot time and restrict any such comparison to a
single boot, or the conclusion is an artefact. Worth remembering before reading a section's
timestamp as evidence of when a capture happened.

**Found:** 2026-09-20, Session 105, testing whether disputed claims had already been recaptured.

### G243 - Background terrain maintenance invalidates the claims that let it be skipped

**Trigger:** a feature that loads dormant terrain in the background, in a system that also records
claims about what the savegame held.

**Trap:** the dormant-terrain snow refresh issues about one priority load a second and the audit
reloads whatever it disputes. Each of those wakes a dormant column, which the engine updates on the
way in and writes at the next save - which disputes that column's claim. The mod therefore generates
a floor of claim staleness proportional to how much dormant terrain it touches itself. On the
owner's 0.4.119 session, 23 of 8,190 far claims went stale this way while he stood still, and the
snow refresh still had 12,104 columns queued.

**Safer action:** read a residual staleness rate as the cost of the maintenance rather than as a
defect in the claim path, and account for the maintenance backlog when interpreting any measurement
taken while it is draining. Expect it to converge one audit pass later, not immediately.

**Found:** 2026-09-20, Session 105.
