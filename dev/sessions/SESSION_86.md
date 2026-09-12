# Session 86 — distant-terrain shimmer: diagnosis, distant anti-aliasing, automatic LOD thresholds and the tuning tab

**Date:** `2026-09-11 to 2026-09-12`
**Branch/commit:** `codex/water-depth-light`; started after 0.4.56 `223bfbb`; checkpoints 0.4.57 `1dc2907`, 0.4.58 `f255b08`, 0.4.59 `c4fbbdd`, 0.4.60 `092f518`, 0.4.61 `b1fd007`
**Mod version:** `0.4.61`; built, verified, copy-installed and checkpointed. The owner tuned distant anti-aliasing by eye on 0.4.60; 0.4.61 only makes his values the defaults and has not itself been run.
**Assist protocol / blob / schema:** `1 / 4 / 6` (unchanged)

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The owner reported shimmer on distant terrain, strongest on snow, visible only while the camera
moves, and asked for an investigation with no code changes. A previous assistant (another tool)
had started and blamed `.vhtexture detail 2` (block textures two mips sharper than the footprint)
plus the 75% render scale with FXAA. The owner later clarified the shimmer is on terrain about
1,000 blocks and further; near terrain is fine. No game was launched by this session.

Evidence, all offline:

1. Screenshots 2026-09-11 13:43 (0.4.54) with the settings panel open: `.vhtexture detail 2.0`,
   `mips 6.0`, band width 128, speckle 0.15.
2. `clientsettings.json`: `ssaa` 0.75 on a 2560x1440 window (1920x1080 rendered), FXAA on,
   `mipmapLevel` 3, view distance 128, `fieldOfView` 60.
3. Decompiled engine 1.22.7:
   - Render scale: the primary colour texture is NEAREST at `ssaa <= 1` and LINEAR above, but the
     final pass samples the Luma framebuffer, which is always LINEAR, so the upscale is smooth. The
     menu slider is 25-100%; values above 1 are supported in code only (G176).
   - Block atlas: `GL_TEXTURE_MAX_LEVEL = mipmapLevel` (3), min filter NEAREST_MIPMAP_LINEAR, bias 0.
   - Near plane: camera default 0.1; `OnFowChanged` sets `clamp(0.1 - fov/90/25, 0.025, 0.1)` only
     when FOV changes mid-session (G177). Depth is `GL_DEPTH_COMPONENT32`, conventional, `Less`,
     cleared to 1.
   - FOV slider 20-150.
4. Shader: snow is pure white on top faces only (`snowMix * upness`) and observed snow (band 3) is
   binary; coarse levels keep the majority block and a majority snow bit (`LodMip`: 2 of 4 counts
   as covered); block textures, speckle and the band corrections apply only where `bandWeight > 0`,
   within about 128 blocks of the vanilla edge.
5. The 13:38 `.vhwater capture` (`water-captures/20260911-183813-663-a4c10eac`, same game session
   as the screenshots): projection near 0.1, far about 8,696, fovy 60, 1920x1080. One block spans
   one rendered pixel at 935 blocks.

| Band (blocks) | Blocks per px sideways | Ground depth per px row | Depth resolution (24-bit est.) | One-row sharp features | White px one row tall |
|---|---|---|---|---|---|
| < 256 | 0.13-0.15 | ~0 | 0.01 | 0.3-0.6% | 4-7% |
| 256-512 | 0.34 | 0.35 | 0.06 | 1.8% | 3% |
| 512-1,000 | 0.76 | 1.1 | 0.3 | 8.6% | 84% |
| 1,000-1,500 | 1.24 | 2.4 | 0.8 | 9.8% | 85% |
| 1,500-2,000 | 1.9 | 2.3 | 1.8 | 9.6% | 79% |
| 2,000-4,000 | 2.5 | 5.6 | 3.3 | 9.3% | 89% |

Diagnosis: sub-pixel (temporal) aliasing - shimmering. Past about 1,000 blocks a pixel covers
several blocks but shows only the one under its centre, and a sub-pixel camera movement swaps it.
Snow is worst because white tops alternate with bare walls and coarse snow is a vote. The
texture-detail lead is out of range (band only). Depth precision (conventional depth: about
0.6 / 2.4 / 5.4 blocks resolvable at 1k / 2k / 3k) was a possible second contributor; the owner's
FOV 60 -> 20 test (blocks 3.3x larger on screen, depth precision nearly unchanged) "greatly
diminished" the shimmer, so aliasing is the cause.

## Work narrative

### 1. Choosing the fix

Weighed: temporal anti-aliasing confined to cached terrain (chosen); MSAA (4 samples, cost grows
with sub-pixel geometry, needs its own framebuffer and depth hand-off - rejected); supersampling via
`ssaa` above 1 in `clientsettings.json` (about 7x the current pixel work - reference only); a smooth
far heightfield with filtered textures (changes how snowy slopes look and risks a band where it
switches - held in reserve); per-face colour smoothing (cannot work: the white slivers are thin top
faces between walls). Two statements made earlier in the session were wrong and were corrected in
the conversation: that colour smoothing was "the proper general fix", and that TAA "would have to
cover the whole screen including vanilla" - the mod uploads its own `projectionMatrix` and draws in
its own pass after vanilla (order 0.38), so the nudge and the blend can be confined to its pixels.

The pass cost was estimated at 0.05-0.15 ms at 1920x1080 on the RX 9070 XT and put to the owner as
frame rates: at 239 fps, +0.1 ms is about 233 fps and +0.5 ms about 214. Section 8 has the
measurement.

Owner rule: nothing keyed to a fixed distance, because players change the vanilla view distance.
Ownership is therefore "pixels whose depth moved nearer than a snapshot taken before cached terrain
drew", and strength is on-screen block size from the live projection and render height.

### 2. Thresholds

A level's columns are twice as wide as the previous level's, so a doubling sequence hands every
level over at the same on-screen width. The owner's list 1,024 / 1,536 / 2,048 / 4,096 / 8,192 /
16,384 ended level 0 at 0.9 px and level 1 at 1.2 px, while levels 2 and up ended at 1.8 px. The
manual equivalent at 60 degrees and 75% is `.vhdetail 640` (every level ends at about 1.46 px; a
rough area estimate gives about 20% fewer distant columns). The owner chose automatic thresholds
from FOV and resolution, with both features as permanent config-window switches for players.

### 3. 0.4.57

- `Render/LodScreenScale.cs`: `LodScreenScale.BlockScale = renderHeight * P[5] / 2` (pixels per
  block at view depth 1); `LodTaaMath` (ramp, Halton 2/3 eight-frame nudge, history share);
  `LodAutoThresholds.Compute` (L1 = scale / target, rounded to 16 blocks, floored, doubled,
  normalized).
- Terrain vertex body: after `lodvertexsurface.vsh`, `gl_Position.xy += nudge * weight * w`. The
  uniforms are declared in `lodvertexinputs.vsh` (G174); water is never nudged.
- `Render/LodTerrainRenderer.Taa.cs`: `BeginTaaFrame` at the start of the opaque callback blits the
  primary depth to a private snapshot. `ResolveTaa`, after thin cover, runs a resolve pass into a
  private RGBA16F history, then a composite pass back into the primary colour through a private
  framebuffer holding only that texture, alpha masked. Full GL state capture and restore; GL error
  checks for the first 30 frames after allocation; history resets on world change, a gap over 30
  frames, an FOV or aspect change, or a camera step over 64 blocks. A failure stops the feature for
  the session with one error line. GPU timers cover the resolve plus composite and the snapshot.
- `UpdateAutoThresholds` runs each prepared frame, recomputes only when render height, FOV, floor or
  target change (perspective projections only, at most once a second), and logs what it applies.
  Level 0 is held past the vanilla view distance plus the band width (when the band is on) plus one
  section, because band textures exist only on level-0 quads.
- Config: `DistantAntiAliasing` and `AutoLodThresholds` (both default on), `LodTargetPixels` 1.5. The
  manual `LodThresholds` stay in the file while automatic is on.
- `.vhconfig`: an "Automatic" switch on the LOD row (scale read-only, showing the live automatic
  values), and a "Distant Anti-Aliasing" row (cost Low; off in Performance, on in Balanced, Quality
  and Defaults). The dialog is 740 px tall. Defaults turns automatic on.
- Commands: `.vhtaa` reports state and GPU cost (and arms the timers); `on`/`off` saved; `show`
  tints anti-aliased pixels magenta by strength; `blend`. `.vhdetail` reports; `auto`, `manual`,
  `target <px>`, or `<blocks>`. Replies go to chat and the client log.

### 4. Owner test of 0.4.57: cleaner, but jitters when still

The owner: "It definitely looks visually better, cleaner" - but the anti-aliased area vibrated,
even with the camera completely still. Source-traced causes, measured on the same capture for
cached pixels in the anti-aliased range (467-935 / 935-2,048 / 2,048+ blocks):

- **Depth rejection against the nudge.** History was refused when the reprojected depth differed by
  more than 3% + 0.5 blocks. 17.9% / 23.1% / 17.5% of those pixels have a neighbour beyond that
  tolerance - tree tops, ridges and steps over farther ground. The half-pixel nudge swaps their
  surface, history is refused, and the pixel shows the raw nudged frame (G175).
- **Ownership boundaries.** A pixel alternating between cached terrain and sky or vanilla was never
  averaged. 0.6% / 3.5% / 10.5% of those pixels sit beside a non-owned pixel.
- **Partial-strength zone.** Geometry was nudged by the weight but the history share was
  feedback x weight, so the nudge was mostly not averaged out.

### 5. 0.4.58: steady with the camera still

Before blending sky pixels, the capture was checked for whether the sky is drawn before cached
terrain. Before the first cached draw, 45.4% of the screen had nothing in depth and already held
sky colours (thousands of distinct values from 0.07 to 0.78 per channel, not a clear colour), and
those pixels were unchanged at the end of the opaque stage. Cached terrain over earlier geometry
covered 0.01% of the screen; sky pixels beside cached terrain, 0.20%.

- `lodtaaresolve.fsh`: no depth test. A pixel is cached terrain, sky, or other; history is taken
  only from cached terrain or sky, never from other pixels, which would trail creatures. Sky pixels
  beside cached terrain are blended with their neighbours' strongest ramp. Alpha carries kind and
  ramp (1 + ramp, -(1 + ramp), or 0), which the composite reads instead of recomputing.
- The ramp runs from the reach to half of it; history comes in over the first half and the vertex
  nudge over the second, so no pixel is nudged before its history is at full share.
- A still mode: below 0.05 px of per-pixel motion the neighbourhood clamp was released and the share
  raised to 0.97.
- `.vhtaa reach <1-32>` (saved): where anti-aliasing begins, as the on-screen width of one block;
  full strength at half that width.

Owner: "much better"; a few spots still vibrated; smear while turning, and smear that stayed even
standing still. He wanted it sharper without the shimmer, and more dials.

### 6. 0.4.59: less smear, sharpening and dials

Source-traced contributors, not separately measured: a settled anti-aliased pixel is the average
of what it covers, so thin snow tops become soft grey, and the 75% render-scale upscale and FXAA
soften it again; bilinear history re-sampling softened the history a little every frame of a turn;
the min/max neighbourhood check on snow, where nearly every 3x3 holds both a white top and a dark
wall, let almost any history through; the per-pixel still mode counted walking as still; and
`.vhtaa blend` only set the turning share while the still share stayed locked at 0.97 - a mistake of
this session, which explained why moving that dial changed little.

- History re-sampled by Catmull-Rom (nine bilinear reads; exact at a texel centre).
- The safety check uses mean +/- clip x standard deviation of the 3x3, inside its min/max, and
  clips toward the box centre.
- Still mode requires the whole camera unchanged (under 5e-4 blocks and 1e-5 in every view-matrix
  element per frame), eased in over 16 frames, and still yields to the pixel's own motion.
- Fast turns (8 px a frame and more) keep 85% of the turning memory.
- Contrast-adaptive sharpening, after the idea in AMD's CAS, in the composite only, so it never
  reaches the history.
- Dials, all saved: `nudge`, `sharpen`, `blend` (turning), `still`, `clip` and `reset`, beside
  `reach`. The report adds every dial and the share of frames with the camera fully still.

No GLSL validator exists on this machine; the shaders were reviewed by hand. A compile failure
switches the feature off for the session with one log line.

### 7. 0.4.60: the Anti-Aliasing tab

The owner asked for a second `.vhconfig` tab of sliders so he could tune faster. Session 77 had
removed a tabbed version of this window because its tab strip clipped the labels to "Det" and
"Visual fe", and a static check pinned "one page". The request reverses that: each tab is as wide
as its label, and the strip sits above the window, as vanilla's character window has it.

- One slider per dial, a reach readout in blocks at the live FOV and resolution, and a switch for
  the magenta coverage tint. Sliders show their effect while dragging; Save keeps them; Cancel, the
  title-bar cross or Escape puts back what was in force when the window opened. Defaults resets the
  tab that is showing; General still applies only on Save.
- `LodTaaDials` carries the six dials as one value; `LodTaaSliders` pins each slider to its dial's
  range, with every default on a slider step. While deferring to another LOD mod, Save writes the
  dials straight to the file.
- Engine facts used: a slider reports on every drag step; `CreateCompo` disposes a composer already
  registered under the same name, so the tab re-compose is queued until after the click (G178).
- The side-panel check requires every renderer setting assigned in the command block to appear on
  the diagnostics panel; the window's combined preview tripped it, so the preview moved to its own
  method. Each dial is on the panel under its own name.

Owner: both tab labels read in full and switching works; the sliders act at once and Cancel
restores; no more vibrating spots; "All of these new dials let me tune it in to look very good."

### 8. 0.4.61: the owner's tuning as the defaults

The owner's saved configuration (written 11:49:58) and the matching `.vhtaa` log line (11:50:01)
agree: reach 2.5, nudge 0.5, sharpen 1.00, memory 0.97 turning and 0.99 still, clip 2.25. They
replace 2, 1, 0.5, 0.9, 0.97 and 1.25. Sharpen and both memories sit at the top of their ranges;
the ranges were left alone pending the owner's view. No screenshot folder exists at the game's
default location, so the log and the configuration were the sources.

The same log carries the first measurements of the feature in his session (1920x1080 rendered,
RX 9070 XT): resolve and composite p95 100 us and depth snapshot p95 25 us, over about 464,000
resolved frames; 63-67% of resolved frames had the camera fully still; three history resets, all
render-target reallocations. The 0.4.61 package was checked by decompiling its `LodTaaMath`: all six
constants carry the tuned values.

---

## Delivered

- Investigation with the capture measurements above; an enlarged far-terrain crop was sent to the
  owner.
- 0.4.57 (`1dc2907`): distant anti-aliasing, automatic LOD thresholds, the two `.vhconfig`
  switches, `.vhtaa`, the `.vhdetail` modes, GPU timers; SHA-256
  `51E9AB03BC1162F41EC295C91845B093F9E7C5B89D7C4BAD0419AF0248B23D93`, 519,250 bytes.
- 0.4.58 (`f255b08`): still-camera fixes, sky edge, history-first ramp, `.vhtaa reach`; SHA-256
  `0E8EA701EC54671F7127C47DDD7B942EF5FFAFF2C2F5C3477823CFB1FD0215FF`, 520,635 bytes.
- 0.4.59 (`c4fbbdd`): Catmull-Rom history, variance check, whole-camera stillness, fast-turn
  memory, sharpening, saved dials; SHA-256
  `0CB11CF2251C8823BDF22CE7DA6B84CABA4C2209D63C0595147168FD0C444277`, 523,610 bytes.
- 0.4.60 (`092f518`): the Anti-Aliasing tab; SHA-256
  `C9331DCE1418FCE908340DEEAAC4DDFE78075DB76B845EF0F649D720A1D412E3`, 529,206 bytes.
- 0.4.61 (`b1fd007`): the owner's tuning as defaults; 38 entries, SHA-256
  `C00F84D4024E4DC4EF9980C1C6C2AF4090BBB2272C2301BD4F3E8E8F1347B715`, 529,199 bytes.
- Every build: warning-free Release; all fast checks passing (23,059, 23,079, 23,105, 23,128 and
  23,129 assertions; the distant anti-aliasing suite ends at 126); zip contents and DLL checked;
  copy-installed with a matching hash, older zips untouched; exact source/test/version checkpoint.
  Nothing pushed.
- Documentation, at the owner's request on 2026-09-12: this record and its index row; CHANGELOG
  0.4.56-0.4.61; G174-G179; settled decisions 34-35; TODO, DONE and STATUS; README command rows; a
  CLAUDE.md routing row; `dev/DocCheck.ps1` as recorded in STATUS.

## Decisions

- The shimmer is sub-pixel aliasing (FOV-20 test); depth precision is secondary at most.
- Anti-aliasing is confined to cached pixels and the sky beside them, keyed to on-screen block size
  against the player's reach; nothing is keyed to a fixed distance (owner rule; settled decision 34).
- Automatic thresholds double from the 1.5 px distance; level 0 is held past the vanilla edge, band
  and one section; the manual list is preserved (settled decision 35).
- History before nudge; a neighbourhood check rather than depth for static distant terrain; history
  only from cached terrain or sky; sharpening on the output only.
- Every dial is saved. `.vhconfig` got a second tab at the owner's request, reversing Session 77's
  one-page rule; the Anti-Aliasing sliders preview live while General still applies on Save.
- The defaults are the owner's tuning; the ranges stay as they are although three dials sit at
  their tops.
- The Performance preset turns distant anti-aliasing off, since that preset is defined as the
  0.3.127 frame-time floor.
- Rejected: MSAA, supersampling as the fix, a smooth far heightfield, per-face colour smoothing,
  content-only changes as a substitute, and per-pixel depth rejection of history (G175). A single
  "strength" dial was offered and dropped in favour of the separate dials, of which nudge and the
  two memories are its halves. `.vhtexture detail` is not involved in shimmer at 1,000+ blocks.

## Traps

1. **Trigger:** uploading a uniform from a shared frame-uniform setter. **Failure:** the engine's
   `Uniform(name)` throws on a name the program never declared, and the water programs share the
   upload. **Safer:** declare in the shared include. G174.
2. **Trigger:** invariant formatting of a message split across lines. **Failure:**
   `FormattableString.Invariant($"a" + $"b")` does not compile, and
   `string.Create(CultureInfo.InvariantCulture, $"a" + $"b")` compiles only if every piece is
   interpolated (a plain `"..."` piece gives CS1620). **Safer:** prefix every piece with `$`.
3. **Trigger:** judging the render-scale upscale from the primary framebuffer. **Failure:** the
   final pass reads the LINEAR Luma target. **Safer:** follow the texture the final pass reads. G176.
4. **Trigger:** validating temporal history by per-pixel depth under a sub-pixel nudge. **Failure:**
   about one distant pixel in five fails every frame and vibrates with a still camera. **Safer:**
   the neighbourhood check, history before nudge. G175.
5. **Trigger:** an in-game FOV test. **Failure:** the near plane changes until restart. **Safer:**
   restart before depth evidence. G177.
6. **Trigger:** re-composing a dialog in its own click handler. **Failure:** `CreateCompo` disposes
   the composer that is still handling the click. **Safer:** queue the re-compose. G178.
7. **Trigger:** searching a DLL's UTF-16 text for a literal. **Failure:** odd-aligned literals are
   missed. **Safer:** search both alignments, or decompile. G179.
8. **Trigger:** a green C# build and fast tier. **Failure:** neither compiles GLSL, and no validator
   is installed here, so a shader error shows only in game. **Safer:** review shader changes by
   hand, keep the visible failure path, and read `.vhtaa`'s state after the owner installs.
9. **Trigger:** assigning a renderer member inside `RegisterCommands`. **Failure:** the side-panel
   static check treats it as a command setting that must be displayed. **Safer:** keep composite
   previews outside the command block, or display the setting.

## Flagged and unverified

Judgement calls awaiting owner review:

- The dial ranges: sharpen reaches CAS's strongest lobe at 1, memory stops at 0.97 turning and 0.99
  still; three of the owner's values sit at the top.
- Both features on by default; the automatic target of 1.5 px; distant anti-aliasing excluded from
  the Performance preset.
- Constants inside the resolve: the stillness tolerances, the 16-frame ease, the 0.5 px moving
  threshold, and fast turns from 8 px keeping 85%.

Claims without the required evidence:

- 0.4.61 has not been run; its only change is the defaults, identical to the owner's saved values.
- The pass's cost is measured only on the owner's machine.
- Anti-aliasing at a large reach across the vanilla edge, on distant water edges and at low frame
  rates is not reported.
- Which of 0.4.58's smear contributors mattered most was not measured separately; the 0.4.59
  changes together with the owner's tuning resolved it.
- Automatic thresholds at other FOVs, render scales and vanilla view distances are not evaluated.
- Depth fighting as a secondary contributor to shimmer is estimated, not measured.
