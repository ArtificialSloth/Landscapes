# Session 84 — handoff flash: root cause found (one-frame ownership lag since 0.4.13) and fixed in 0.4.53

**Date:** `2026-09-10` to `2026-09-11`
**Branch/commit:** `codex/water-depth-light` over `8ace702`; committed with the documentation pass, together with Session 83's work
**Mod version:** `0.4.50–0.4.53`; 0.4.53 installed and human-tested (fixes the flash), not yet accepted as the baseline
**Assist protocol / blob / schema:** `1 / 4 / 6` (unchanged)

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

> Finalized on 2026-09-11 in the documentation pass the owner requested. Earlier drafts were
> written mid-session, at his request, before context compactions.

## Context and investigation

The session began from Session 83's open release blocker: a conspicuous flash when vanilla
terrain is evicted and cached terrain takes over. 0.4.39 remains the accepted baseline.

The owner restated the symptom this session: the flicker is on the chunk the cache takes over
after the player walks away from a chunk vanilla was drawing, in between vanilla dropping its
chunk and the cached terrain appearing. It is very brief and hard to make out.

**Correction to Session 83's evidence (log-measured, source-traced).** `STATUS.md`,
`dev/TODO.md` and G169 treat the 0.4.49 log as decisive against readiness loss because its only
counter block showed 158 gains and 0 losses. That block is written once, 30 seconds after
joining. The run lasted about 75 seconds, and the block shows `2 window changes/2 resizes` (no
chunk crossing) and 0 lost transitions. Both loss paths - a probe seeing `IsChunkRendered`
false, and the exact range sweep - count an ordinary eviction, so that interval contained no
vanilla-to-cache handoff at all and says nothing about the flash. The documentation pass at the
end of this session corrected all three.

Owner observations this session (human-tested; each is a brief visual judgement):

| Test | Result |
|---|---|
| `.vhpaint on` (0.4.49) | The flash appears magenta; without paint it looked bluish, possibly sky |
| Location | Everywhere; snow cover has no clear influence |
| Vanilla occlusion culling off, later back on | No change either way |
| 0.4.50 `.vhgap release on` | No help at all |
| 0.4.50 `.vhtemporal off` | No help |
| Build bisection from `dist/` | 0.4.39 clean; **0.4.40 first bad** ("very present"); 0.4.41 flashes, less than 0.4.50; 0.4.45 flashes |
| 0.4.51 `.vhsnowline legacy` | Does not fix it |
| 0.4.52 `.vhsnowline legacy` (now including `fetch`) | Does not help |
| 0.4.52 `.vhsnowline spatial` then `.vhhzb off` | Does not help |
| 0.4.52 `.vhsky verify on` | Does not help (it is a measurement, not a remedy) |
| 0.4.53 (late range check) | **Fixed**: "This fixed it!" (2026-09-11) |

Owner direction: the ownership overlap margin (`OverlapMarginBlocks`, `.vhband overlap`) stays
at 0. It was 0 through every accepted build and is not a cause; it must not be changed or
proposed as a lever. The owner also asked that the end of the investigation name the change
that caused the problem (answered in section 10).

## Work narrative

### 1. Handoff gap log (0.4.50)

`.vhgap` (session-only) asks the engine, every frame, about every cell whose mask texel
suppresses cached terrain, after all ordinary ownership paths have run and before the mask is
published. It classifies why vanilla is not drawing such a cell: unloaded, fresh (drawn counter
0), air, no mesh, hidden, occlusion-culled, or out of range (the engine's range clause with the
chunk's live sphere and zero margin, flagging when the recorded sphere still said in range).
Episodes are logged one line each with frames, milliseconds, end cause, distance and the start
signals. A summary line per busy second (a 10-second heartbeat when quiet) adds counts by
reason, chunk events by reason, chunk crossings, probe queue and scan cost.

`.vhgap release on` is a comparison: it hands releasable cells (unloaded, fresh, air, no mesh,
out of range) to the cache in the frame they are found, through the range sweep's publication
handshake. Hidden and culled cells are never released because the gain path would retake them
at once. It adds no overlap and leaves the margin alone.

Supporting code: `VanillaRenderReadiness.CollectCommittedCells`,
`VanillaChunkGeometry.TryDrawnCount`/`TryIsCullVisible`, the pure `VanillaHandoffGap`
classifier and `VanillaHandoffGapTracker` (new `HandoffGapChecks`, 60 assertions). The
`.vhpaint` help text now says magenta; the shader has painted magenta, not red, for some time.

Scan cost measured in play: about 11-30 us mean per frame, with peaks up to 12.6 ms and once
28.6 ms during chunk-load bursts (the client chunk lock, G45). Acceptable for a diagnostic, not
for an always-on mechanism. Section 10 explains why this scan could not see the flash: it runs
in the same pre-physics slot as the decision it audits.

### 2. Engine facts established from the installed 1.22.7 IL

- `ChunkCuller.CullInvisibleChunks` runs on a separate thread. It marks every chunk invisible in
  the back buffer, re-marks those its rays reach, then swaps; `SetVisible` writes
  `CullVisible[(bufIndex + 1) % 2]`, so the renderer never reads a half-built verdict. It re-runs
  when the camera changes chunk or the regen-traversal queue moves by 10 or more.
- Client `SystemUnloadChunks.HandleChunkUnload` (packet handler 11) calls
  `RemoveDataPoolLocations` immediately and raises no client event. `ChunkColumnUnloaded` exists
  only on `IServerEventAPI`. `ClientMain.PacketHandlers` is a public array.
- Retessellation swaps pool locations inside one `AddTesselatedChunk` call, followed by
  `TriggerChunkRetesselated`; `ClientEventManager.RegisterOnChunkRetesselated` offers per-chunk
  one-shot listeners.
- `WorldChunk.Unpack` sets `PotentialBlockOrLightingChanges`, which only affects recompression;
  the mod's chunk reads cannot cause the server to resend chunks.
- Client `GetClimateAt(pos, baseClimate, mode, days)` only raises `OnGetClimate`, and
  `WeatherSystemBase.Event_OnGetClimate` returns at once for `ForSuppliedDate_TemperatureOnly`,
  so the snow line's per-cell climate lookups have no weather side effects.
- `LoadOrUpdateTextureFromPixels` builds mipmaps only on the creation branch; updates use
  `TexSubImage2D`. The mod's texture units are distinct (6-15).
- The frame order and the culler are in section 9.

### 3. Sandbox runs (integration-tested; no visual judgement possible)

Route: four 400-block legs at 6.7 blocks/s looking back at the trailing edge; legs one and two
with the log only, three and four with release on. The route files live in the session
scratchpad, not the repository.

- The first launch stalled: the owner's own game was open, and the sandbox client sat at a
  "Multiple Instances" prompt. It was stopped with `scripts/test-stop.ps1`. The owner then
  authorised closing his game; it exited cleanly on a window-close request and saved.
- Default integrated sandbox (grassland), view distance 64, owner's server config: the only
  short gaps were out of range with a stale recorded sphere, 1-5 frames, all in chunk layers
  0-2. Release took every one to 0 frames.
- Copy of the owner's bodanboys world (save, both cache databases, terrainslabs and
  placeonslabs through a sandbox `ExtraMods` path, view 64, owner's server config; it loaded
  "7219 sections from cache" exactly as his game does): 73 stale-sphere gaps of 1-7 frames whose
  live spheres sat exactly on the chunk face toward the neighbour just unloaded (in-chunk offset
  x = 0 or 32, z = 16). That is the signature of a fully solid buried chunk whose only geometry
  is the wall vanilla builds toward an unloaded neighbour, so these gaps are invisible. Also 2
  eviction gaps (unloaded before the mod noticed): 4 and 7 frames, 64-65 blocks out, layers 4
  and 6. Release took every gap to 0 frames.

Sandbox settings were restored afterwards. The bodanboys copy, its caches and `ExtraMods`
remain under `.testdata/integrated` for re-runs (about 4 GB).

### 4. The owner's own 0.4.50 run (human-tested, log-measured)

Log only, two stretches totalling about 40 seconds: 50 stale-sphere out-of-range gaps of 1-7
frames (at most 24 ms at about 222 fps) in layers 0-4, plus long no-mesh episodes on buried
chunks. No eviction gaps; no culled episodes (occlusion culling was off). Release on, 29
seconds: all 285 detected gaps were handed over at 0 frames, and the flash was unchanged.

So the visible flash is not "the mask gives a cell to vanilla while vanilla draws nothing" in
any form the CPU-side scan can see. A proposed fix that would hand chunks over from vanilla's
unload packet (wrapping `PacketHandlers[11]`) was withdrawn.

### 5. Build bisection and per-build diff

From the `dist/` zips, shaders changed only in 0.4.40 (spatial snow line) and 0.4.44 (snow
marker band 3 and frost); the DLL changed in every build. Each build from 0.4.39 to 0.4.45 was
decompiled with `ilspycmd` and diffed step by step:

| Step | What changed |
|---|---|
| 0.4.40 | `LodClimateField` (+73 lines), `LodTerrainRenderer` (+62), four shader includes: the spatial snow line |
| 0.4.41 | `LodClimateField` read-back cleanup, 9 lines |
| 0.4.42 | Observed-snow provenance (`LodWorld`, `LodMip`, `LodSection`, store) and the snow-observation early climate refresh |
| 0.4.43 | Server refresh/assist, local-offer refresh delivery, the rolling refresher |
| 0.4.44 | Mesher snow marker (band 3; snow layers emit no geometry), frost, tint/mip/store |
| 0.4.45 | `/vhrefresh` only, inert unless typed |

The owner's results place the first bad build at 0.4.40. `vanillaOwnsCell` reads the ground
channel for band 3 (only band 1 reads the water channel), which ruled out a band-3 ownership
mix-up before the bisection reached it. Section 10 records why this bisection conflicts with the
root cause.

### 6. `.vhsnowline` comparison (0.4.51)

Each switch undoes one runtime part of 0.4.40:

- `cells`: per-cell thresholds. Off, every cell takes the player-local threshold computed once
  per refresh, so the roughly 33,000 per-cell climate lookups per refresh disappear.
- `shader`: terrain reads its threshold from the climate texture. Off, it uses the `snowLineY`
  uniform as 0.4.39 did (new `snowLineUniformOnly` uniform).
- `alpha`: the texture's alpha byte carries the threshold. Off, a constant 255 as in 0.4.39's
  texels (`LodClimateField.EncodeSnowLine`).
- `early`: the snow-observation early refresh added in 0.4.42. Off, the 30-second cadence only.

`legacy` switches all four off and `spatial` back on. Changes restart the climate refresh and are
logged. New check `TheSnowLineCanLeaveTheAlphaByteAlone`. The owner found `legacy` does not fix
the flash. The remaining 0.4.40 difference in legacy mode was the vertex shader's climate lookup
running for every vertex; section 7 added a switch for it.

### 7. Sky witness and the fetch comparison (0.4.52)

- **`.vhsky on`** (session-only, new `SkyWitness.cs`, `LodTerrainRenderer.SkyWitness.cs`,
  shaders `lodsky.vsh`/`lodsky.fsh`). Straight after cached opaque terrain, one full-screen
  triangle on the far plane runs with `early_fragment_tests` and depth LEQUAL, so only pixels
  nothing opaque covered reach the shader. A pixel whose view ray points down and would meet
  sea level - 64 inside 90% of the cache's reach should be ground; it is counted, with a
  bounding box and a 16x9 tile grid, into one of four fenced SSBO slots read back one to three
  frames later. Every path discards; nothing visible is drawn. A frame above a slow running
  level (`SkyWitnessBaseline`) is logged with the captured mask window and texels for that
  frame, mask changes/uploads, chunk crossing, depth split and GPU cull, and for the two busiest
  tiles a ray walked through the loaded world from the captured camera to the first solid
  block, that chunk's ownership in the captured mask, and the engine's current
  `IsChunkRendered`. Per-second summaries otherwise; a 10-second heartbeat when quiet.
- **`.vhsky verify on`**: after every mask upload, reads the texture back with `GetTexImage`
  and compares it with the CPU texels, and records GL errors and non-default pixel-unpack state.
- **`.vhsnowline fetch on|off`** (new `climateFetchTintOnly` uniform), included in `legacy`: off
  puts the vertex climate lookup back where 0.4.39 had it, inside the tint branch.
- Checks: `SkyWitnessChecks` (45 assertions: shader layout against `SkyWitnessLayout`, baseline,
  tiles, ray reconstruction, captured-mask reading, the fetch gate).

### 8. What the witness measured (owner's 0.4.52 run; log-measured)

About 90 seconds of walking across all four modes of section 7's tests, 149 flash lines:

- **83 frames of 20,000 px or more** of sky below the horizon, peak 215,282 px of a 1920x1080
  frame. In every one of them the busiest tile's ray reached real loaded vanilla ground
  (tallgrass, muddy gravel) in a chunk whose centre was **65-67 blocks** from the camera, the
  captured mask had handed that chunk's ground to **vanilla** (typically layers 0-5, the surface
  in layer 5), and the engine reported the chunk rendered when it was checked one to three
  frames later.
- 78 of those 83 frames show **"mask unchanged"**. None were consecutive: each flash is a single
  frame, one to eight per second while walking, and none while standing.
- The same in every mode: spatial, legacy with `fetch` off, with and without the depth split,
  and with `verify` on.
- `verify` read back 156 uploads while walking: **0 differed from the CPU copy, 0 GL errors**, no
  non-default unpack state. The GPU mask is exactly the CPU mask.
- 66 smaller frames: 34 in vanilla-owned chunks like the above, and 32 whose ray left loaded
  chunks in the air at 44-66 blocks (so the ground it would reach is cache-owned). The latter,
  a few hundred to 1,500 px in runs over about 15 frames, are not explained (section 13).
- Witness cost: under 0.1 ms CPU per frame; readback one frame late on average, no frame
  skipped.
- The game's archive holds five sessions from the bisection evening (0.4.42, 0.4.40, 0.4.41,
  0.4.40, 0.4.51). Every one ran at p50 4250 us (~235 fps) with the same p95 and p99, so frame
  rate did not change between those builds. No 0.4.39 session survives.

The 65-67 block distance is the engine's own range edge at view distance 64:
`ViewDistanceSq` = 64 * 64 + 400, whose root is 67.05 blocks.

### 9. The engine's frame (source-traced from 1.22.7 `VintagestoryLib`, `VintagestoryAPI`, `VSEssentials`)

`ClientMain` renders a frame in this order:

1. `TriggerGameTick`.
2. The **Before** stage, renderers sorted by order: camera (0.0), ambient (0.0), shadow-map setup
   (0.0), GUI (0.1), player effects (0.1), entities (0.4), chunk tessellation upload `chtema`
   (0.99), **this mod's preparation `vintagehorizons-prepare` (0.994)**, vanilla liquid depth
   `ret-prep` (0.995), this mod's cached-water depth (0.996), and **`EntityBehaviorPlayerPhysics`
   (1.0)**, which advances the local player's physics in fixed 1/60 s steps and so moves
   `Entity.Pos`.
3. The shadow stages (the shadow system also calls `CalcFrustumEquations` on the same culler).
4. `frustumCuller.CalcFrustumEquations(player.Entity.Pos.AsBlockPos, ...)`, then `lod0BiasSq`
   and `lod2BiasSq` for the frame (at view distance 64 or less, `lod2BiasSq` is the view distance
   squared).
5. The **Opaque** stage: `ChunkRenderer.OnBeforeRenderOpaque` (`masterPool.OnFrame`, which retires
   delayed pool-location removals through a four-frame queue), vanilla chunks at 0.37 through
   `ModelDataPoolLocation.IsVisible`, then cached terrain at 0.38.

`FrustumCulling.InFrustumAndRange` tests planes 0-4 (not the far plane), then the horizontal
distance from the player's integer block to the location's sphere centre: level 1 below
`ViewDistanceSq`, level 2 at or below `lod2BiasSq`, level 3 between the two. The previous
visibility argument is unused; there is no hysteresis.

### 10. Root cause, and the change that introduced it

The mod decides which chunks vanilla owns in its preparation (`PrepareRenderFrame` ->
`UpdateReadinessShadow`: `CaptureDrawRange`, the exact range sweep `SweepRangeLosses`, the gap
scan, and mask publication). That runs at Before/0.994. The engine moves the player at
Before/1.0 and only then fills the culler from `player.Entity.Pos.AsBlockPos`. So the sweep
measured vanilla's range from where the player stood **before this frame's physics step**, while
the engine drew from where the player stood after it. On any physics step that carried the player
across a block boundary and took a chunk's sphere centre past 67.05 blocks, vanilla stopped
drawing that chunk in that frame, and the mask kept hiding cached terrain there until the next
frame's sweep: one frame with nothing drawn. That matches every measurement in section 8: single
frames, only while moving, at the range edge, mask unchanged in the flash frame, the GPU mask
equal to the CPU mask. `.vhgap` and `release` ran in the same pre-physics slot, so they shared the
blind spot. `CaptureDrawRange`'s comment had assumed that a capture from any render callback was
this frame's; that holds for the culler's settings but not for the player's position.

Build history (scan of every `dist/` DLL and decompiles of 0.4.12, 0.4.13 and 0.4.39):

- The exact range rule (`SweepRangeLosses`, `CaptureDrawRange`, `ExactDrawRange`), which hands a
  chunk to vanilla right up to vanilla's own range edge, first appears in **0.3.153**. In 0.4.12
  it still ran inside `RenderFrame`, the opaque-stage draw callback, after the culler had been
  filled, so it saw the same player block as the engine.
- **0.4.13** moved the per-frame preparation, including `UpdateReadinessShadow`, into a new
  Before-stage callback at 0.994 so that it precedes vanilla's liquid-depth pass (0.995) for the
  cached-water depth work. **This is the change that introduced the one-frame lag.**
- 0.4.39 and 0.4.40 both have the 0.4.13 arrangement (`PrepareBeforeOrder` 0.994), and the
  0.4.39-to-0.4.40 diff is climate code only.

The bisection result (0.4.39 clean, 0.4.40 first bad) therefore conflicts with the code. The
timing is identical in those builds and the frame rate is identical across the archived
sessions. Why 0.4.39 was judged clean is not established. A single-frame artefact is easy to miss
in a short visual test, and `.vhsky` now gives a count that does not depend on catching it.

### 11. The fix (0.4.53)

The preparation cannot simply move, because it must precede vanilla's liquid-depth pass. New
`LodTerrainRenderer.LateRangeCheck.cs` registers a second Before-stage callback,
`vintagehorizons-laterange`, at `LateRangeBeforeOrder` = 1.001: after the physics step and before
anything draws. It captures the range inputs again. Only if the player's block or the culler's
settings moved since the preparation's sweep does it run the same loss-only sweep, through the
same publication handshake, and then publish the ground texels and the seam field. It can hand
ground to the cache sooner and never takes ground away.

Water's footprint is deferred to the next preparation (`PublishReadinessMask(rebuildWaterFootprint:
false)`), so the cached-water depth pass that already ran at 0.996 and the water passes still to
come read the same footprint within a frame. A `footprintRebuilt` guard makes the next
preparation upload a deferred footprint even when nothing else changed.

`.vhlaterange on|off` (on by default, session-only) restores the old timing for an A/B, and
`.vhsky` summaries report its re-sweeps and handovers. The misleading comment in
`CaptureDrawRange` was corrected. New `LateRangeCheckChecks` (13 assertions) reads
`EntityBehaviorPlayerPhysics.RenderOrder` from the installed VSEssentials, so a game update
that moves the physics step fails a check rather than bringing the flash back. It also holds
the water-pass orders and that the late check writes no texel itself.

Human-tested on 2026-09-11: the owner reports the flash is gone. No `.vhsky` count was taken on
the fixed build.

---

## Delivered

- **0.4.50 (test build):** `.vhgap` gap log and release comparison; `.vhpaint` magenta text;
  new `VanillaHandoffGap.cs`, `LodTerrainRenderer.HandoffGaps.cs`, `HandoffGapChecks.cs`;
  `CollectCommittedCells`, `TryDrawnCount`, `TryIsCullVisible`; side-panel rows. Release build
  with zero warnings and errors; 22,873 assertions, 0 failures.
  `dist/vintagehorizons_0.4.50.zip`, 490,484 bytes, SHA-256
  `b769fbf4306a5fa8ee10f434d7777f4ce8fdcc58050e58392d6af86e2cf8f12b`; copy-installed.
- **0.4.51 (test build):** `.vhsnowline` switches in `LodTerrainRenderer.SnowLineComparison.cs`;
  `LodClimateField.EncodeSnowLine`; `snowLineUniformOnly` shader uniform; side-panel rows; new
  climate-field check. Release build with zero warnings and errors; 22,881 assertions, 0
  failures. `dist/vintagehorizons_0.4.51.zip`, 491,956 bytes, SHA-256
  `f049cdd416a0a52d9eaa76ed240522a6129cd5ffacc4fa6737274a9bf2f7e413`; copy-installed.
- **0.4.52 (test build):** `.vhsky` witness and `verify`; `.vhsnowline fetch`; side-panel rows;
  `SkyWitnessChecks`. Zero warnings and errors; 22,928 assertions, 0 failures.
  `dist/vintagehorizons_0.4.52.zip`, 504,436 bytes, SHA-256
  `a78b0dcf786f11eded00b4a553ecb65243b2ee050d9170a9000eda7868becf33`; copy-installed.
- **0.4.53 (test build, the fix; human-tested):** the late range check and `.vhlaterange`; the deferred water
  footprint; the corrected `CaptureDrawRange` comment; late-check counters in `.vhsky`
  summaries; side-panel row; `LateRangeCheckChecks`. Zero warnings and errors; 22,941
  assertions, 0 failures. `dist/vintagehorizons_0.4.53.zip`, 505,483 bytes, SHA-256
  `97b16452998681af2be3c04f3de7475a1308dd80602ec31458082234c8096685`; copy-installed.
- **Evidence:** two sandbox runs and three owner runs analysed; the bisection; per-build
  decompiled diffs; the engine's frame order from decompiled `VintagestoryLib` and
  `VSEssentials`; the build history of the exact range rule and the Before-stage preparation.
- **Documentation:** the full pass on 2026-09-11: this record, `dev/sessions/INDEX.md`,
  `CHANGELOG.md`, `dev/GOTCHAS.md` (G169 correction, G170-G172), `dev/ARCHITECTURE.md` (settled
  decision 33), `dev/TODO.md`, `dev/history/DONE.md`, `STATUS.md`, the README command table and
  one `CLAUDE.md` routing row. `dev/WIRE_HISTORY.md` is unchanged (no compatibility change).
- Committed with the documentation pass on `codex/water-depth-light`, together with Session 83's
  uncommitted 0.4.40-0.4.49 work; not pushed or published. No game launched after the owner's
  first absence; 0.4.53 was built while the owner was asleep and could not be asked.

## Decisions

- The overlap margin remains 0 (owner). It is not a cause and not an A/B lever.
- Withdrawn: handing chunks over from vanilla's unload packet. The release comparison showed
  that a same-frame handover of every detected gap leaves the flash unchanged.
- Ruled out by owner tests: vanilla occlusion culling, temporal occlusion, CPU ownership latency
  of any class `.vhgap` could see, every `.vhsnowline` part including `fetch`, and the depth
  pyramid.
- Fix by a late, loss-only re-check after the physics step rather than by moving the
  preparation, which must precede vanilla's liquid-depth pass.
- The late check defers water's footprint by one frame instead of changing it mid-frame between
  the water passes.
- `.vhgap`, `.vhsnowline`, `.vhsky` and `.vhlaterange` are session-only switches; the late check
  is on by default.
- The owner confirmed the fix on 2026-09-11. Accepting 0.4.53 as the new baseline is his call.
- A sandbox copy of the owner's world (save, cache databases, enabled mods, his view distance and
  server config) is the faithful reproduction; the default sandbox world is not.

## Traps

- **Trigger:** reading `Entity.Pos`, or anything else the player's physics moves, from a
  Before-stage callback ordered below 1.0. **Failure:** it is where the player stood before this
  frame's physics step, while the engine culls and draws from where the player stands after it.
  **Safer:** capture after Before/1.0 or in the opaque stage; `LateRangeCheckChecks` reads the
  physics order from the installed engine.
- **Trigger:** a CPU-side diagnostic that runs in the same slot as the decision it audits.
  **Failure:** it shares the decision's blind spot, as `.vhgap` did. **Safer:** measure the picture
  (`.vhsky`), or audit where the engine decides.
- **Trigger:** a visual bisection of a one-frame artefact. **Failure:** 0.4.39 was judged clean
  with the same timing and frame rate as builds judged bad. **Safer:** an objective per-build
  count such as `.vhsky`.
- **Trigger:** citing the periodic 30-second counter block for a movement symptom.
  **Failure:** the block covers the join only. **Safer:** confirm lost transitions above zero
  and window changes above resizes, or use an on-demand log.
- **Trigger:** reading `.vhpaint` magenta as "vanilla stopped drawing". **Failure:** magenta
  also shows any cached fragment in an owned cell that passes the depth test, such as cached
  geometry standing in front of live vanilla terrain. **Safer:** pair it with `.vhsky`.
- **Trigger:** launching the sandbox while the owner's game is running. **Failure:** the second
  client stalls at "Multiple Instances". **Safer:** check `Get-Process Vintagestory` first.
- **Trigger:** using the default sandbox world to reproduce an owner report. **Failure:**
  different terrain, season and settings (view 256, sweep off). **Safer:** copy his save, cache
  databases and enabled mods; the runner wipes `.testdata/integrated/Mods`, so add mods through
  the sandbox's `stringListSettings.modPaths`.
- **Trigger:** exact-match patching of tracked files. **Failure:** some tracked files mix CRLF
  and LF (`ClimateFieldChecks.cs`, `lodvertexinputs.vsh`, `lodvertexsurface.vsh`), and long Bash
  heredocs failed in this harness. **Safer:** try the anchor in both endings, insert in the ending
  that matched, and verify line endings afterwards.
- **Trigger:** a per-frame engine scan of every owned cell. **Failure:** chunk-lock peaks of
  several milliseconds under load. **Safer:** keep it a diagnostic.
- **Trigger:** vanilla rebuilding a solid chunk next to a newly unloaded neighbour.
  **Failure:** the rebuild adds a wall toward that neighbour and moves the chunk's sphere onto
  that face, so the range sweep's recorded sphere disagrees for 1-7 frames. **Safer:** refresh
  the recorded sphere when vanilla re-tessellates; the gap is invisible for buried chunks but real.

## Flagged and unverified

Judgement calls awaiting review:

- The late check as the fix, rather than a restructuring of the preparation.
- Whether to keep `.vhsky` as a permanent diagnostic.
- Whether to fix the invisible stale-sphere gaps and the rare eviction gaps as separate cleanups.

Not established:

- An objective count on 0.4.53. The owner's test is visual; `.vhsky on` with `.vhlaterange on` and
  then off would measure the fix and any residual rate.
- Why 0.4.39 was judged clean when its timing is the same as 0.4.40's.
- The smaller cache-owned sky patches in section 8 (rays leaving loaded chunks in the air at
  44-66 blocks).
- `.vhgap` still scans at 0.994, before the physics step, so its range verdicts can be a frame old.
- Whether the 2 sandbox eviction gaps were visible.
- The sandbox still holds the bodanboys copy (about 4 GB) under `.testdata/integrated`.
