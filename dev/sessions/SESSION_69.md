# Session 69 — The accurate rule was gated behind the inaccurate one

**Date:** `2026-08-30`
**Branch/commit:** `codex/exact-vanilla-draw-range`, from `14bfe4b`
**Mod version:** `0.3.153` through `0.3.160`
**Assist protocol / blob / schema:** `1 / 4 / 6`

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The owner asked for a thorough investigation, with no code changes, of a recurring complaint:
cached terrain overlapping live vanilla terrain, guarded crudely by an overlap ring that is
"unsightly at times". They correctly identified that the engine relates chunk visibility to the
camera and that a chunk can be loaded without being drawn.

That is G43, established in session 28. The mod already knew the mechanism; what it did not do
was measure it. `VanillaRenderReadiness.BeyondVanillaDrawRange` **bounded** the engine's range
cull rather than reading it, and the bound is the overlap:

| blocks | constant | why |
|---|---|---|
| 46 | `SphereCentreSlackBlocks` | the mesh bounding-sphere centre could be anywhere in the column |
| 16 | `OverlapMarginBlocks` | loss is only detected on the 1 Hz revalidation |

At a 256-block view distance vanilla draws to about 257 and ownership stopped at 194 - a
63-block ring of cached terrain drawn on top of live terrain, permanently, by design.

The investigation found the slack unnecessary: `ModelDataPoolLocation.FrustumCullSphere` and
`LodLevel` are public fields on arrays `VanillaChunkGeometry` already bound by reflection, and
`IRenderAPI.DefaultFrustumCuller` hands back `ClientMain.frustumCuller` itself - public API, used
nowhere in this codebase until now.

Everything after the first report was driven by the owner's playtest logs. The session's shape is
four rounds of "check logs", and three of those rounds were spent on defects in the diagnostics
rather than in the renderer.

## Work narrative

1. **The transcription, and why it is a transcription.** `FrustumCulling.InFrustumAndRange` ends
   in `playerPos.HorDistanceSqTo(sphere.x, sphere.z)` against a per-LOD bound.
   `VanillaDrawRangeState` copies that switch verbatim from the installed 1.22.7 IL, including the
   widths: `BlockPos.HorDistanceSqTo` computes in double and returns a **float**, `ViewDistanceSq`
   is an int, `lod0BiasSq` a float, `lod2BiasSq` a double. `VanillaDrawRangeChecks` runs the real
   `FrustumCulling` object from the installed game against the copy over ~500,000 points; G97 does
   not apply, because there is no re-implementation to share an assumption with.

   Two anchors were wrong and had been invisible under 62 blocks of padding. The engine measures
   from `player.Entity.Pos.AsBlockPos` - the **player**, truncated to whole blocks - while the
   renderer measured from `Entity.CameraPos`, which trails by several blocks in third person. And
   the engine culls at `ClientSettings.ViewDistance`, the **client** setting, while
   `ApprovedViewDistance()` returns the server-clamped minimum.

2. **Fast loss without a spatial guard.** The 16-block margin paid for latency, so the fix was to
   remove the latency: the sphere is recorded on the cell when ownership is granted, and a sweep
   re-decides the whole committed set from stored state with no chunk lookup and no lock. It runs
   only when one of the engine's own inputs moves, and every one of them is quantised by the
   engine - `AsBlockPos` truncates, the biases are settings - so a settled view sweeps zero times
   and walking sweeps once per block. Whole-set rather than cursored, because G39.

3. **The margin's direction was backwards, and a fixture caught it.** The first version inflated
   the distance so a chunk looked further away. Level 3 is far-**only**: its clause has a lower
   bound as well as an upper one, so inflating switched it **on**. The hole guard was granting
   ownership the exact rule refused. `DrawsWithMargin` is now `Draws(d) && Draws(d + margin)`,
   which makes the direction structural rather than arguable.

4. **An unsound optimisation, deleted rather than fixed.** A column-level pre-filter for the sweep
   reasoned about `ViewDistanceSq`, which is level 1's bound and the widest of the four. A chunk
   holding only near-detail geometry is cut off at `lod2BiasSq`, a hundred blocks nearer at the
   defaults. Making the filter sound needs the cell's pool mask, which is only readable inside the
   loop the filter existed to avoid entering. Deleted, with a check recording why.

5. **Then the logs, and the first two answers were both wrong.**

   `0.3.155` added `.vhskip why`, which reports what the current whole-column classification says
   **and** what a rule counting only the band each mesh occupies would say. It answered
   **0 of 1248** - the proposed fix was worthless, because the cells the cached mesh occupies were
   not owned either. A day of work saved by printing both columns instead of one.

   `0.3.156` added an overshoot measurement and it reported **54 of 63 columns**. The mask owned
   nine. 63 was the same nine columns counted once per LOD level, and a coarse section's height
   span is the maximum across a footprint four thousand blocks wide, so the comparison was
   measuring terrain slope. Retracted and restricted to L0 as an upper bound.

6. **The measurement that asked the game.** Every report to that point measured the mod against
   itself. `.vhexact audit` (0.3.157) walks the draw radius, evaluates the engine's whole
   predicate per chunk, and counts cells where the engine draws and the mask does not hide - the
   only report in the mod that can see the failure the player reports. It answered immediately:

   ```
   the engine is drawing 15; the mask covers 8 (53%), leaving 7 uncovered
   Reasons: 0 air / 7 not committed yet / 0 range / 0 no-geometry
   Nearest uncovered: chunk 15983,3,16035 at 28 blocks (we say Pending)
   ```

7. **The deadlock, and it was mine.** 0.3.154 gated the exact rule on `atOwnershipDecision` to
   avoid a chunk lookup on first sightings. Everything else fell back to `viewDistance - 46`,
   which at the owner's 64-block view distance is **18 blocks**. A chunk 28 blocks away - meshed,
   cull-visible, well inside the 67 blocks the engine draws to - was refused, stayed `Pending`,
   and a `Pending` cell is not an ownership decision, so it never reached the exact rule that
   would have admitted it. The inaccurate rule permanently locked out the accurate one. At a
   32-block view distance the gate is zero and no cell can ever be owned at all.

   The audit line proved it arithmetically before a line was changed: 28 blocks against an
   18-block gate.

8. **Two of the mod's own diagnostics were lying.** `DescribeSignals` claims to print every signal
   the engine offers and omitted `quantityDrawn` - the counter `IsChunkRendered` reads, the first
   gate every probe passes. Two rounds were spent reasoning about which gate refused a chunk with
   that gate absent from the output. And the audit's `refused for range` bucket was dead code
   (`&& !ExactDrawRange`, never true), filing all seven misses under "not committed yet".

9. **The audit as acceptance evidence.** 0.3.159 measured **23 of 23, 100%, zero uncovered, zero
   holes**, at the default `overlap +16` rather than the owner's manual `overlap 0`. Ownership
   went from 9 mask cells to 25, from 0 fully-owned columns to 5, and the underground bands from
   `0/0/0` to `6/5/6`. Churn fell to one lost transition in thirty seconds.

10. **A correctness audit found six more, two of them pre-existing.** Detailed under Delivered.
    The one worth naming here is an observation-queue flag leak: a cell leaving `ObservedRendered`
    kept its "already queued" mark while its ring entry was discarded, so `QueueObservation`
    refused it forever after and it fell back to waiting for a sweep. Nothing incorrect was ever
    committed - the two-observation rule is enforced by the frame stamp, not the flag - which is
    exactly why it survived: the symptom is slower ownership, not wrong ownership.

---

## Delivered

Source, 0.3.153-0.3.154:

- `VanillaDrawRange.cs`: `VanillaDrawRangeState`, a verbatim transcription of the engine's range
  clause with its exact numeric widths, plus `DrawsWithMargin`, `WidestDrawBoundBlocks`,
  `ColumnCertainlyNotDrawn` and `DiffersFrom`.
- `VanillaChunkGeometry.TryReadDrawBounds` reads the bounding sphere and pool-level mask from the
  chunk's own pool locations, skipping hidden ones.
- `VanillaRenderReadiness` stores the sphere and level mask per cell, cleared on eviction with
  everything else (G44's shape, new payload), and `CollectRangeLosses` re-decides the committed
  set from stored state.
- `LodTerrainRenderer.CaptureDrawRange` reads `IRenderAPI.DefaultFrustumCuller` once per frame;
  `SweepRangeLosses` withdraws ownership in the frame the engine stops drawing.
- One chunk lookup per ownership decision, shared by three consumers that each used to fetch their
  own - a net reduction in lock traffic (G45).
- `RefusedOwnershipRingBlocks` is the single definition the water dilation and the band's inner
  offset both derive from, so both follow the boundary instead of describing where it used to be.

Source, 0.3.155-0.3.157 (diagnostics):

- `.vhskip why`, `.vhexact map`, `.vhexact audit`; `.vhinfo` and the status body now reach the log,
  which previously received the full readiness line **once**, thirty seconds after joining.
- `DescribeSignals` prints the drawn counter, the sphere, and the pool levels.

Source, 0.3.158-0.3.160:

- The range rule runs only where an observation can change ownership, plus a sound first-sighting
  gate: the column's nearest face against the widest bound any level draws to. Everything it
  refuses, the exact rule refuses - so nothing is kept off the ownership path that could have
  stayed on it.
- The no-sphere fallback (air, unmeshed) uses the same sound column test instead of
  `viewDistance - 46`, which was refusing air past half a chunk and under-reporting `ready per Y`
  badly enough to misdirect this investigation twice.
- Observation-queue flag released whenever a cell leaves `ObservedRendered`; a per-probe heap
  allocation removed from `AllHidden`; `.vhskip` accepts `true/false` again; the coverage audit
  bounded to the draw radius rather than the window, which grows as the square of view distance.
- G68's other half, six sessions late: the main shader program reports success once, re-arming on
  a genuine failure. Only the indirect variant ever did, so a healthy start logged three engine
  link errors and no statement that anything compiled.

Checks: 5,885 Release assertions (was 5,730). New suite `VanillaDrawRangeChecks` (63), plus
tracker and static coverage for the sphere lifecycle, the flag lifecycle, the early gate's
soundness, and the shader success report.

## Decisions

- **Transcribe the engine's switch, do not model it.** Including the float rounding in
  `HorDistanceSqTo`. A cell disagreeing by one ulp is a fragment nobody draws.
- **Drop the frustum clause from ownership.** It is view-dependent, so including it would flap the
  mask on every turn to hide terrain that is off screen anyway - and omitting it is conservative
  by construction, since what it admits is a superset of the engine's answer.
- **Keep the 16-block margin, lose the 46.** The slack was not knowing where the sphere is, and
  reading it removes that provably. The margin covers vanilla unloading a chunk before a probe
  notices, which reading the sphere does not answer. `.vhband overlap 0` remains live for anyone
  who wants the boundary exact.
- **Delete the sweep's column filter rather than repair it.** A sound version would have to do the
  work it was saving.
- **Measure the proposed fix before building it.** `.vhskip why` reports both classifications side
  by side specifically so the band-counting change could be rejected on evidence.
- **Report the air/no-sphere case rather than leaving it wrong.** It costs no pixel - air is
  excluded from the mask - but it cost accurate telemetry, and inaccurate telemetry cost two
  rounds of this session.

## Traps

- **Trigger:** gating an accurate rule on a state the inaccurate rule controls. **Failure:** the
  exact range test needed `atOwnershipDecision`; the conservative bound prevented cells from ever
  reaching it. Ownership was capped at `viewDistance - 46` and impossible below 46. **Safer:** a
  rule applied to cells that have not yet been decided must be a strict subset of the rule that
  decides them, and that subset relation should be asserted over a dense sweep. Promoted to G133.
- **Trigger:** a diagnostic that names the signals it prints. **Failure:** `DescribeSignals`
  documented the drawn counter as the first signal and did not print it; two rounds were spent
  reasoning about which gate refused a chunk. **Safer:** assert that a report prints what its own
  summary claims. Promoted to G134.
- **Trigger:** a per-section measurement compared against a per-column fact. **Failure:** the
  overshoot report counted each column once per LOD level and compared an L6 height span, four
  thousand blocks wide, against one column's ground - 54 of 63 where nine columns existed.
  **Safer:** when a measurement aggregates over a footprint, restrict it to the finest level or
  state it as an upper bound. Promoted to G135.
- **Trigger:** a hole guard expressed as a distance adjustment. **Failure:** inflating the distance
  switched the far-only LOD level on, so the guard granted ownership. **Safer:** express a guard
  as an intersection with the unguarded answer, so its direction is structural.
- **Trigger:** a static check bounded by a character count. **Failure:** a 6,000-character window
  and the assertion it needed at 6,028. **Safer:** bound by the next member.
- **Trigger:** packaging before the review is finished. **Failure:** 0.3.153 was built and
  installed, then the review found the unsound filter; the version was spent and 0.3.154 had to
  follow immediately. **Safer:** package last.

## Flagged and unverified

Judgement calls awaiting human review:

- 0.3.159 is human-tested by the coverage audit reading 100% with zero holes, in one world at a
  64-block vanilla view distance. Not evaluated at other view distances, in other worlds, in third
  person, or in multiplayer - and third person is exactly where the camera/player anchor
  correction should matter most.
- The water footprint's derived dilation now falls to zero under the exact rule. That is the
  correct consequence of the ground boundary moving out, it follows G132's arithmetic, and it has
  **not** been looked at in play. 0.3.151's chunk-wide dry strip came from this quantity.
- The band's inner offset likewise follows the refused ring and is now 16 rather than 46. The band
  was human-tuned against the old value.
- 0.3.160's shader line has not been seen in a log; it is a logging change and the evidence is
  simply that the line appears at next start.

Claims lacking their evidence level:

- **No benchmark since 0.3.9.** The sweep's measured peak fell from 1.899 ms to 0.386 ms once the
  world settled, which is telemetry from the feature's own counter, not a frame-time measurement.
  Probe rate is unchanged at roughly 28,000/s.
- The whole-mesh skip has still **never fired**, and the radial handoff is still pinned at zero.
  Both need complete columns, and the underground cells that block them are a separate problem
  this session measured but did not solve. Five fully-owned columns now exist where zero did.
- `ClientChunk.bufIndex` is read on the render thread while `ChunkCuller` writes it on another;
  one frame of disagreement is possible and self-correcting. Pre-existing, unmeasured.
- The stored sphere goes stale if a chunk retessellates into a different shape, bounded by the 1 Hz
  revalidation and by `ChunkDirty` arriving sooner. The stale window fails toward overlap in one
  direction and toward a brief hole in the other; neither has been observed.
