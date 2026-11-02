# Session 123 — 0.5.2-dev.27: shore foam, water blips, and the seam band's reach per preset

**Date:** `2026-11-02`
**Branch/commit:** `main`; `e482117` (dev.23), `239b0f5` (dev.24), `6551457` (dev.25), `ece970d` (dev.26), `61885d3` (dev.27). Not pushed.
**Mod version:** `0.5.2-dev.27` (a test build; the owner played dev.23, dev.24 and dev.25 on 1.22.7 with his AMD card)
**Assist protocol / blob / schema:** `1 / 4 / 6` (unchanged)

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

The owner asked what was next. The recommendation was the preset review, the last step he had set for the plants work. He
asked for a cost per setting first, and that benchmark was designed but not run (section 1). Water Effects reminded him of
two old water faults, shore foam and the moving blips, and those took the session. It ended with two setting changes he
asked for. All evidence came from his screenshots, two water captures, the game's shaders and the decompiled engine.
No game was launched from here.

## Work narrative

### 1. A benchmark per setting: designed, deferred to a new session

The bench can already walk a route whose waypoints run commands, settle and measure (`regression-127-194.txt` did this for
0.3.194). Proposed: a copy of his most recent world, `vintage cave tales-2` (cache `71c0c6af`, 0.94 GB save), with his eight
enabled mods and graphics settings (2560x1440 at 0.75 scale, view 256). The sweep would remove one setting at a time from
everything-on, each re-measured against a fresh baseline, in milliseconds. Five scenes: forest ground, high horizon, coast,
lit base at night, a moving flight. Each preset would also be measured whole, and memory and one-off rebuild pauses recorded.
Gaps found: about half the settings have no chat command (the whole Plants tab, leaf shape, Distant Lights), so one
`.dl` command reading the window's own rows is needed. The runner deploys Debug builds, which inflates CPU costs. The sandbox
needs the sweep off. Estimated 2.5-3 hours for a full pass, after a 20-minute pilot. The owner wants it in a new session.

### 2. Shore foam (dev.23, dev.24)

His 00:54-00:56 shots showed vanilla's shallows pale and mottled and ours clear down to the bed. Vanilla's foam term
(`chunkliquid.fsh`) compares the water fragment with the scene depth behind it; ours held it at zero, with a note that cached
water drew in the opaque stage and could not read depth. It has drawn in OIT for a long time, and the OIT framebuffer
shares the primary depth texture, which vanilla reads there with depth writes off. dev.23 reads it the same way: five
samples scaled to vanilla's 25 over 16, the fine noise fading by a pixel's size in the world, strength apart from the
accepted 1.6 on the grain, under Water Effects and the game's foam setting. It draws no mesh or cache data.

The near and far planes go under the mod's own names. Our water programs had left `fogandlight.fsh`'s `zNear`/`zFar` at
their defaults, 0.3/1500, and the accepted underwater murk runs on those. Vanilla's `linearDepth` is a share of the far
plane, and the mod lifts that plane from 3,000 to about 8,700 blocks. So with the mod installed, vanilla's own foam fades
at about 3.7 blocks of depth instead of 1.3; that was left alone, and both sides use the same scale (G331).

dev.23 drew nothing visible (01:12 shot). Vanilla's `bright` is `rgba`, the light on the surface; the line copied from
the grain took the water's own dark colour, about five times too faint (G330). dev.24 scales by the light and adds
`.dlwater shore paint`. The owner on dev.24: "Looks very good!" Patch brightness across the seam read cached 20% brighter
in one scene, at unmatched depths; he tried `.dlwater shore 0.6` and found it "way too light", so 1.0 stays.

### 3. Water blips (dev.25, dev.26)

With `.dlgpu off` the old Session 79-82 blips showed as soft grey squares beside and under water, shifting with every
one-pixel turn. The orange specks in the same shots were not them; the dark dashes on a lake were ice blocks hidden by
`.dlgpu off` (owner). `.dlwater coverage off` removed every square; `.dlwater repair off` changed nothing. Two captures a
pixel apart measured about 870 cached-depth texels per frame over mostly dry blocks and about 750 mostly-water texels with
no depth; 98 of the first kind survived the turn. The engine's liquid depth is quarter resolution and each texel took
cached water from its centre sample, so water narrower than a texel toggled in and out. The sky, clouds, cached ground and
Farseer all darken through that image, blurred (G332).

dev.25 draws cached water depth at full resolution into our own image and resolves each quarter texel by majority: at
least half its pixels water (`lodwaterresolve`, `.dlwater majority`). The junction test reads its quarter snapshot through
`vhWaterDepthTexelScale`. Owner, majority off/on pair at 01:58: "huge difference", most of it gone, still noticeable at
water edges. Cached ground under a false rim also loses its atmospheric fog (`groundFog = fogAmount - 50 * murk`), so at
distance it showed as a clear dark spot. dev.26 gives cached ground, its fade to the sky and Farseer the engine's own
formulas reading the full-resolution image for cached water and the vanilla-only snapshot, bilinear, for vanilla's
(`lodwatermurk.fsh`). Owner: edges look the same with terrain off (that rim is the engine's sky and clouds); normal play
did not seem to be an issue.

### 4. Speckle and the band's reach (dev.27)

Owner: speckle 0.1, was 0.15, and the band reaching all of level 0 beyond vanilla's edge on Ultra, 66% on Quality, 33% on
Balanced. The width had been a fixed 128. Automatic thresholds already stretch level 0 to cover the band, so a width taken
from the applied level 0 would grow it by its own width each time; the width is taken from level 0 before that stretch
(G333). It is capped where the seam field's margin would cut the ramp. `.dlband reach` sets the share, saved;
`.dlband width` pins a width for the session and `auto` hands it back. Not played.

---

## Delivered

- **0.5.2-dev.23** (`e482117`): shore foam from scene depth in the OIT water program, `.dlwater shore`.
- **0.5.2-dev.24** (`239b0f5`): shore foam scaled by the surface light; `.dlwater shore paint`.
- **0.5.2-dev.25** (`6551457`): full-resolution cached water depth with a majority resolve; `.dlwater majority`.
- **0.5.2-dev.26** (`ece970d`): `lodwatermurk.fsh` exact readers for cached ground, its sky fade and Farseer.
- **0.5.2-dev.27** (`61885d3`): `LodVisualSettings.BandReach` per preset and in the config, `.dlband reach`,
  `.dlband width auto`, speckle 0.1.
- Each build verified, copy-installed to both Mods folders, compiled against 1.22.7; none pushed. Checks: 26,998 assertions,
  0 failures on 1.22.0 and 1.22.7 at dev.27; deliberate breaks of the majority rule and of the exact ground murk failed them.
- Two sampler units added (16 shore depth, 17 cached water depth); both unit checks now hold numbers under 48.
- Documentation (2026-11-02, at the owner's request): this record, CHANGELOG, GOTCHAS G330-G334, ARCHITECTURE decisions
  75-77, TODO, STATUS, the water handoff's watch item and the private working rules.

## Decisions

- **Shore foam reads the scene's depth in OIT, as vanilla does** (decision 75). Rejected: mesh-carried depth (more geometry)
  and a guessed constant (pinned foam over every ocean before).
- **Strength 1.0** (owner): 0.6 was "way too light".
- **Vanilla's foam widened by our far plane is left alone;** matching it keeps the seam continuous.
- **Cached water's share of liquid depth is decided at full resolution, by majority** (decision 76), and this mod's own
  readers read the exact images. The engine's sky and clouds keep the quarter image; their rim with terrain off is theirs.
- **The band's width is a share of level 0 beyond vanilla's edge, per preset** (owner, decision 77). Performance, with the
  band off, holds Balanced's share.
- **Speckle 0.1** (owner).
- **The benchmark runs in a new session** (owner), on a copy of `vintage cave tales-2`.

## Traps

- **Vanilla's foam brightness is the light on the surface, not the water's colour** (G330).
- **`linearDepth` is a share of the far plane, and our programs keep fogandlight's default planes** (G331).
- **The quarter-size liquid depth takes each texel from its centre** (G332). With `.dlgpu off`, ice and other opaque blocks
  in water vanish and look like marks; the owner caught one.
- **A band sized from the level 0 it stretches feeds itself** (G333).
- **A unit-number check confused with the per-stage sampler limit** (G334).
- **A path with an apostrophe broke a `pwsh -Command` call** (`Doden's Landscapes/water-captures`); pass paths as `-File`
  parameters.

## Flagged and unverified

Judgement calls awaiting human review:

- Five depth samples rather than vanilla's 25; the fine noise fading from 0.25 to 1 block per pixel.
- Performance's band share 0.33; the band cap at 1,016 blocks less the inner offset.
- dev.26's sky-fade murk keeps vanilla's +3/+6 pixel samples, so a soft six-pixel edge remains there by design.

Claims without evidence at the level needed:

- **NVIDIA:** none of dev.23-dev.27 has run there; the blips were worst there.
- **Costs unmeasured:** shore foam's depth reads, the extra full-resolution water depth pass, the exact readers, and the
  wider band (its seam field grows from 128 to 256 cells at Quality and Ultra). `.dlwater shore 0/1` and
  `.dlwater majority off/on` give clean pairs.
- **dev.27 unplayed:** band reach and speckle 0.1 not seen.
- **Far shore foam shimmer** not looked for.
- **Vanilla's weak-foam flag** (bit 28 halves its brightness) was not found set; not reproduced.
- **The 20% brighter cached shallows** in one scene rest on one patch pair at unmatched depths.
