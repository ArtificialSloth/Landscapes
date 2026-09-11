# Handoff - Session 85 (2026-09-11): sky patches, tree textures, murk dark rim

> **Resolved 2026-09-11.** This is the incoming working handoff preserved for history, not current
> state. Session 85 continued the capture analysis, found glacier/ice blocks misclassified as
> water, built and installed 0.4.55 with that correction and the Redwood fix, and created the
> versioned-build checkpoint workflow. Read [Session 85](../sessions/SESSION_85.md),
> [STATUS](../../STATUS.md), and the top of [TODO](../TODO.md) for current state.

Branch `codex/water-depth-light`, last commit `579f7f6` (0.4.53). Everything below is
uncommitted. The documentation ritual has NOT been run: STATUS, TODO, CHANGELOG and the session
record still describe 0.4.53, and `dev/DocCheck.ps1` will flag version 0.4.54 until it is.

## Builds and state

- **0.4.54** - built, Release 0 warnings/0 errors, fast checks 22,978 assertions/0 failures.
  `dist/vintagehorizons_0.4.54.zip`, SHA-256
  `f39e50f3ac142109753b4f86a3a38896763cf05200683c6d20e9d7217b50b984`, copy-installed to Mods.
  Contains the tree wall-texture fix and `.vhwhy` / `.vhholes` now writing to the client log.
- **Source after 0.4.54 (not packaged):** the redwood fix below. Fast checks 22,982/0. The next
  artifact is 0.4.55 (bump `modinfo.json` and the csproj together).
- The owner's Mods folder currently holds a bisection set (0.3.127, 0.4.0, 0.4.13, 0.4.39-0.4.41,
  0.4.51-0.4.54); his last logged session ran 0.4.13. His view distance is now **128** (it was 64
  in every session from Sep 10 to 11:44 on Sep 11; the Sep 5-9 water captures were at 128 too).
- Nothing committed, pushed or published. The assistant never launched the game.

## 1. Small sky patches (TODO "Handoff flash follow-ups") - no action needed

The owner's `.vhsky` run on 0.4.53 (client log 11:45-11:48) contains **none** of the old handoff
flashes (0.4.52 had 83 in 90 s). What remains: 32 runs of cache-owned sky, 73% of them in the
outermost screen columns, 40-200 blocks out, just below the horizon. The owner reports seeing
nothing. A camera-lag cause is ruled out: `PlayerCamera` rebuilds `CameraMatrixOriginf` at
Before/0.0 from this frame's mouse yaw (decompiled 1.22.7). The witness flags rises over an
adapting baseline, so a still hole is logged as it slides in during a turn. Suggest closing the
TODO item as not visible.

## 2. Tree textures - fixed in 0.4.54, redwood fixed in source

**Cause:** `ResolveBlockTexture` picks the `up` texture first, and one slot per block was applied
to every face, so a standing log (`up` = cut end) showed rings on its sides. Introduced in
**0.3.139** (`04ea3e6`, 2026-08-28), the build that added real block textures. The owner called
it recent; textures were on in every archived session, and nothing since changed that code.

**Fix (0.4.54):** `LodBlockTextureRegistry.FloatsPerSlot` 16 -> 32, `RowsPerSlot` = 8. Rows 4/5
hold the X-wall (east/west) rectangle and mean, rows 6/7 the Z-wall (north/south) pair. A wall
with no distinct texture, or one on another atlas page, gets a copy of the top's rows, so
unchanged blocks draw as before. The resolver picks walls by face name (`east`/`west` or
`north`/`south`, then `horizontals`, `sides`, `all`). TopSoil is excluded, so grass and soil look
exactly as before. `lodterrainbody.fsh` chooses rows by normal, and the table upload uses
`RowsPerSlot`. New checks: `PackedQuadChecks.WallsUseTheirOwnTextureRows` and
`LogSidesResolveToBark`, plus two `StaticAssetChecks.BlockTextureWiring` assertions. The owner
confirmed trees look fixed **except redwood**.

**Redwood (source only):** redwood trunks are `logsection` quarter blocks drawn from their shape
(`#bark` outside, `#wood` top, `#inside`). The block file's unused face keys name `treetrunk`
(the cut end) for `north` and `up`, so north and south walls still showed rings.
`PickFaceTexture(..., skipId)` now makes a wall pass over the top's own texture; a sideways log
is unaffected. Redwood cases were added to `LogSidesResolveToBark`. Not yet seen in game.

Files: `LodBlockTextureRegistry.cs`; `VintageHorizonsModSystem.cs` (`ResolveBlockTexture`,
`WallTexture`, `PickFaceTexture`, the face-name lists, and the `LogReportLines` calls in
`.vhwhy`/`.vhholes`); `LodTerrainRenderer.cs` (table upload); `lodterrainbody.fsh`;
`PackedQuadChecks.cs`; `StaticAssetChecks.cs`; `modinfo.json`; the csproj.

## 3. Dark rim around snowy peaks - cause isolated to ground murk, NOT yet fixed

**Symptom:** a 1-4 px dark blue outline where white cached terrain meets sky. Screenshot
`%USERPROFILE%/Pictures/Vintagestory/2026-09-11_12-35-02.png`; the peak is about 280 blocks away,
so cached terrain. Measured: the sky 2-3 px above snow edges is dimmed to 0.88 of the sky higher
up, with no dimming beside rock. None of the other 170 screenshots shows it (the Sep 9 0.4.41
snowy peak is clean).

**Owner tests:** 0.4.0 clean; 0.4.13 has it ("slightly different"). **`.vhwater murk off`
removes it completely.** Check the client log's "ground murk off" line for which build he tested
that on (0.4.13 or 0.4.54).

**Murk** was added in **0.4.11** (`waterGroundMurk`; `lodsurfacefinish.fsh` calls the game's
`getUnderwaterMurkiness()` and `applyUnderwaterEffects()`). The game's function takes the max of
two quarter-resolution `liquidDepth` samples (the pixel, and 3 px up), linearizes both depths,
and computes `clamp((fdepth - ldepth) * 350)`. The result mixes the colour toward
`waterMurkColor * 0.4`.

**Not yet explained:** where `liquidDepth` is 1.0 (no water), murk is exactly 0. So at the rim,
the sampled liquid depth must be below 1.0, or the input is not the real liquid depth. Checked and
ruled out:
- **Sampler slots:** all 15 samplers are pinned in `LodTerrainSamplerLayout`. 0.4.11 had no
  layout and 0.4.12 added it; Session 79 records that 0.4.11's murk darkened bright terrain
  through sampler aliasing.
- **Water-depth pass:** it draws `waterMeshes` band 1 only.
- **Repair and HZB passes:** they use texture unit 0 and restore state.
- **ZFar:** consistent. `ApplyZFar` sets `MainCamera.ZFar`, and the engine's `Use()` uploads
  `zNear`/`zFar` from it.
- **Old captures:** the 0.4.17-0.4.35 water scenes show no murk on dry outlines.
- **Game post-processing:** SSAO skips zero `gPosition`, which cached terrain writes; bloom's
  blur is far wider than the rim; FXAA only blends between neighbours.

**Hypothesis (unproven):** the rim needs view distance 128. The owner saw a "blurry edge" during
the water work at 128, then ran at 64, and is back at 128 today.

**Next steps:**
1. On 0.4.54 at the peak with murk on, run `.vhwater capture` (the folder path is logged). Files
   are under `%APPDATA%/VintagestoryData/VintageHorizons/water-captures/<utc>/`: float32 little
   endian, bottom row first, with a JSON per image; `frame.json` holds the camera and
   projection. Sample `01`/`02` liquid depth (480x270) at the rim pixels, and compare
   `02c-before-cached-ground-color` with `05-opaque-stage-end` to confirm murk adds the rim.
   Find what puts liquid depth below 1.0 there.
2. Fix at that cause. Keep murk on real seabed viewed through water (the owner valued it), and
   remove it from dry silhouettes. Add a deterministic check.
3. Ship together with the redwood fix as 0.4.55. Ask the owner to re-check the peak (murk on) and
   redwoods.
4. For the owner, name the causing change with evidence (see memory
   `vh-name-the-causing-change`): so far, 0.4.11 introduced murk; the exact rim trigger is still
   open.

## Other notes

- 0.4.54's 12 shader link errors at startup are the known first-attempt artefact; 0.4.53 logged
  the same 12, and "shaders ready after 2 attempt(s)" follows.
- The game's FXAA reads luma from the colour alpha (`FXAA_GREEN_AS_LUMA 0`).
- New assistant memory: `vh-sky-witness-reads-changes`.
