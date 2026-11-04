# Session 125 — 0.5.2-dev.52: a settings window pass, leaves closer to vanilla, and NVIDIA stalls found

**Date:** `2026-11-03`
**Branch/commit:** `main`; dev.33 `6277210`, dev.34 `b0c81b9`, dev.35 `33c72fb`, dev.36 `11d14c4`, dev.37 `7e885a7`,
dev.38 `60e39e9`, dev.39 `700d38c`, dev.40 `e4e4e64`, dev.41 `1df0b63`, dev.42 `9de44b2`, dev.43 `98d22c3`,
dev.44 `cd9a2e1`, dev.45 `bb7385a`, dev.46 `e6bf4b9`, dev.47 `fa68923`, dev.48 `1823eea`, dev.49 `10c72ff`,
dev.50 `b37d9aa`, dev.51 `e224d9c`, dev.52 `6395851`. Not pushed.
**Mod version:** `0.5.2-dev.52` (a test build; the owner played it on his AMD RX 9070 XT and on an NVIDIA RTX 3060 Ti)
**Assist protocol / blob / schema:** `1 / 4 / 6` (unchanged)

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

Session 124 left the settings window's cost words to be relabelled and dev.32's presets unplayed. The owner worked through
the window build by build from his screenshots, then compared cached trees with vanilla's in shot pairs, then tested on a
second machine: Ryzen 5 5600G, NVIDIA RTX 3060 Ti, 1080p fullscreen, whose game packs block pictures on two 4096 x 2048
atlas pages. Nothing was launched by the agent; all game evidence is the owner's screenshots and the two machines' logs.

## Work narrative

### 1. Cost words, tips and the Automatic flash (dev.33)

The cost tags became a four-word scale from Session 124's numbers: Minimal under 0.05 ms, Low 0.05-0.15, Moderate
0.15-0.5, High over 0.5. Seam blending, Close Biome, Distant AA and Plant Textures are Low; Farseer Low; the rest Minimal.
Hover tips showed through an open drop-down list (picking a preset read out the row beneath it): every tip is now added
through `AddTip`, which records its key, and a frame hook hides them all while any recorded drop-down is open. Dragging a
locked L1-L6 handle while Automatic is on flashes the Automatic label red twice.

### 2. Plant Detail on General, the plant switches under Developer (dev.34-dev.36)

The seam fog's cost word is **Varies** (owner: its cost follows how much fog is on screen). Plant Detail moved to General;
the Plants tab became a Developer > Plants group. The mod's version shows dim in the window (dev.35), the number alone at
the owner's request (dev.36), read from the mod the game loaded.

### 3. Cached trees against vanilla (dev.37-dev.39)

Vanilla gives every leaf block two crossed planes two blocks across at one of seven angles, with the large-leaf sheet, and
within about a third of the view distance adds a cube of small leaves and two more planes (`lod0Shape`). Ours used the same
sheet on one pair of planes per two-block cell. The owner asked for a shape mimicking vanilla: **Vanilla Style** (dev.37),
each visible leaf block its own pair, two blocks tall, on 2x1 diagonals (the packed format runs planes corner to corner on
whole-block corners). It priced at +27% level-0 quads on the frozen cave-tales cache and 280 to 235 FPS at his spot, and
barely changed the look. The owner's three shots measured the real difference: equal screen coverage and brightness, but
13% near-black pixels in our pines against 5% in vanilla's, because the large-leaf sheet is 46-60% leaf.

**Dense Leaves** (dev.38) layers each leaf sheet with a half-tile-shifted and a mirrored copy at full brightness, the game's
sheet on top (oak 56% to 81%, pine 46% to 74%), one texture fetch as before; each leaf slot keeps both sets of table rows and
the switch swaps them without a remesh. A threshold dial was first proposed and withdrawn: leaves use the full-detail sheet to
about 220 blocks at his 1440p/FOV 60 (texture detail 2), so a cut-off change would not reach the trees in his shots. Owner:
"much better" but square corners showed, the dense sheet now showing each plane's rectangle (G345). dev.39 rounds each
standing leaf tile's corners in the shader with a ragged cut that fades along an edge so stacked tiles open no seam. Owner:
"Bravo! That looks good!" Vanilla Style stays in the drop-down; dev.44 adds "Too High Cost: no preset uses it" to its note.

### 4. The settings window (dev.40-dev.43)

The preset drop-down became a row of buttons at the top of General, a dimmer Custom lit when nothing matches, a rule under
it, and the LOD title shrank (dev.40). The owner asked for a fresh review by an Opus sub-agent, which measured the game's
fonts against his screenshot: "Performance" is 149 units in the 24-point button face against a 136 box; the top gaps were
4-8 units; "Visual features" was 50% larger than the LOD title; the no-sampler note and the version took separate bottom
lines. dev.41 applied it: buttons 128 wide at 18 points, about 10 units between top elements, one 22-point heading size,
the switches ending on the bar's right edge, even footer buttons. dev.42 put the version at the right end of the preset row,
centred "Visual features" and moved the rows and Cost captions down. dev.43 replaced the bottom no-sampler line with a red
**NO SAMPLER** beside Farseer's title and a fuller explanation in Farseer's hover; the rows took the freed line.

### 5. The preset prompt (dev.45)

The login prompt now offers all four presets, "Quality (Recommended)", at 16 points in a 900-wide dialog. It shows once per
preset round (`PresetPromptRound`, round 2 for 0.5.2) instead of once ever, so a player who answered 0.5.0's prompt is asked
once more, with a line saying the presets were redone.

### 6. Stalls on NVIDIA (dev.46-dev.49)

Constant frame-graph spikes on the RTX 3060 Ti, flat with the mod off, none on AMD. Garbage collection was ruled out by the
hitch report (All-Cores on, under 10 ms a minute). With VSync on every slow frame read as 'end' at 13.9 ms, exactly two
144 Hz refreshes (G341). With VSync off they were 'vh:lod' +8-19 ms two or three times a second, on driver 576.52 and 621.01
alike. In-game toggles: `.dltemporal off` and anti-aliasing off changed nothing; `.dlhzb off` lowered the spikes most.
`.dlgpu off` then `on` left cached terrain undrawn on that machine for the rest of the session (works on AMD; not
investigated), which first read as a clue.

dev.46 gave the depth pyramid's box list fresh storage per write; no change. dev.47 split 'vh:lod' into timed parts
(`LodHitchMarks.EnterPart`: pyramid, opaque, water, classify, anti-aliasing, Farseer, between): **'vh:lod classify'** held
79 of the 80 slowest frames a minute, +18.5 ms. The classification only measures what the pyramid could hide (0% in his
logs) and nothing that draws reads it; the start-up culling setup (`DepthCull`) had been turning it on. dev.48 runs it only
when `.dlhzb on` asks for the measurement; the slowest 1% went from 23.5 to 18.6 ms, and the stall moved to **'vh:lod
opaque'**, +14 ms 4.6 times a second, the rate of the cull pass's live counters, sampled every 30 frames for the `.dlhzb`
report and read back the same way. dev.49 samples them only while measuring. Steady stretch: 3 frames over twice the median
in 26 s (was 274-537 a minute), slowest 1% 10.1 ms. Both read-backs were fenced and read only once their fence signalled; on
NVIDIA the read still held the frame (G342).

### 7. Dense Leaves and bush sheets on two-page atlases (dev.50-dev.51)

The NVIDIA machine's log read "0 leaf sheets with a dense picture"; the AMD machine's 105. The dense pictures were inserted
after the game built its atlas, and a late insert goes to the first page with room: the second page on a fresh install's two
4096 x 2048 pages, which textured terrain cannot read (G343). dev.50 makes every leaf picture's dense version at
`AssetsLoaded` (main thread, before the loading threads) as a PNG asset of this mod (G344), and a Harmony prefix on
`BlockTextureAtlasManager.CollectTextures` names it on every leaf block under an extra texture key, so the game packs it with
its own mipmaps. Owner: "Excellent!" Berry bushes then stayed cubes there for the same reason; dev.51 layers every bush leaf
folder (any domain) the same way, and registers every picture the mod makes before any block's, so they open the first page
whichever page the blocks' own pictures land on (decision 80). Owner, on both machines: everything good, and on NVIDIA "the
water blip issue from before - that's solved".

### 8. Finds Stay Visible (dev.52)

Resin kept a three-pixel minimum at any distance, a yellow square on far trees, "almost cheating" (owner, both machines);
mushrooms, flint and ore kept two. The minimum is now a Developer > Plants switch, **Finds Stay Visible**, off by default:
off, markers keep their true size and shrink and vanish with distance (decision 71 revised). A shader uniform; no remesh.

---

## Delivered

- **0.5.2-dev.33** (`6277210`): four-word cost scale; tips hidden while a drop-down is open; Automatic flashes red.
- **0.5.2-dev.34** (`b0c81b9`): seam fog "Varies"; Plant Detail on General; Developer > Plants group.
- **0.5.2-dev.35-dev.36** (`33c72fb`, `11d14c4`): the version in the window, the number alone.
- **0.5.2-dev.37** (`7e885a7`): Vanilla Style leaf shape; the leaf sheet tiles by a plane's longer side.
- **0.5.2-dev.38-dev.39** (`60e39e9`, `700d38c`): Dense Leaves; rounded leaf tile corners.
- **0.5.2-dev.40-dev.43** (`e4e4e64`-`98d22c3`): preset buttons, the window spacing pass, NO SAMPLER beside Farseer.
- **0.5.2-dev.44** (`cd9a2e1`): Vanilla Style "Too High Cost".
- **0.5.2-dev.45** (`bb7385a`): the preset prompt with Max and Quality (Recommended), asked again per preset round.
- **0.5.2-dev.46-dev.49** (`e6bf4b9`-`10c72ff`): the box list's fresh storage; timed parts of 'vh:lod'; the depth-pyramid
  classification and the cull counters only while `.dlhzb on` measures.
- **0.5.2-dev.50-dev.51** (`b37d9aa`, `e224d9c`): dense leaf pictures and bush sheets packed with the game's atlas.
- **0.5.2-dev.52** (`6395851`): Finds Stay Visible, off by default.
- Each build verified, copy-installed to both Mods folders, compiled against 1.22.7; none pushed. Checks on 1.22.0 and
  1.22.7, 0 failures: 27,021 (dev.33) to 27,056 (dev.52); the 1.22.0 count varied between runs (26,779-26,952) as before.
- Owner-played: dev.33, dev.38-dev.43 (AMD), dev.45-dev.51 (NVIDIA), dev.52 (both). "Everything looked good" on NVIDIA, the
  Session 123 water blips solved there; "everything seems great" on AMD.
- Documentation (2026-11-03, at the owner's request): this record, CHANGELOG, GOTCHAS G341-G345, ARCHITECTURE decisions 80-81
  with 63, 64, 69, 70 and 71 brought up to date, README, TODO, DONE, STATUS and the private working rules.

## Decisions

- **Cost words** Minimal / Low / Moderate / High from measurements; the seam fog says Varies (owner).
- **Plant Detail on General; the plant switches under Developer > Plants** (owner).
- **Leaves:** Vanilla Style kept in the drop-down, in no preset, "Too High Cost" (owner); Dense Leaves on by default with
  rounded tile corners (owner). Not pursued: per-tree colour mix and per-puff shading.
- **Preset buttons at the top of General**, a rule beneath, the version at the row's end (owner); the sub-agent's spacing
  proposal adopted, its trim of the bar's top lane not.
- **The preset prompt** offers Max and recommends Quality, and asks again once per preset round (owner).
- **Measurement never runs in default play:** the depth-pyramid classification and the cull counters only under
  `.dlhzb on` (decision 81). The pyramid itself stays on by default for culling.
- **Pictures the mod builds are packed with the game's atlas at load**, registered before any block's (decision 80); the
  late insert stays as the fallback.
- **Finds Stay Visible**, off by default (owner); markers otherwise keep their true size.
- **Release:** keep building dev builds; the owner calls the 0.5.2 promotion.

## Traps

- **VSync hides the cause of a stall** (G341).
- **On NVIDIA a fenced GPU-to-CPU read-back still holds the frame,** and removing one moves the stall to the next (G342).
- **A picture inserted after the atlas is built goes to page two on a fresh install** (G343).
- **The game's asset table is a plain dictionary** (G344).
- **A dense cut-out sheet shows its plane's rectangle** (G345).
- **A run that changes resolution or Farseer between builds** cannot compare their numbers; the dev.46 run was fullscreen
  1080p with Farseer on, the earlier ones 1440x810 with it off.
- **perl one-liners against CRLF files** died on `\n`-anchored patterns; the Edit tool or `\r?\n` worked.

## Flagged and unverified

Judgement calls awaiting human review:

- The depth pyramid is still built every frame by default for culling, and its classification found 0% hidden; whether it
  should stay on.
- Vanilla Style's place in the drop-down at about 0.7 ms.

Claims without evidence at the level needed:

- **Crops drawn from several layers** use a late-inserted picture too and may show as flat patches on two-page atlases;
  not seen. Leaf pictures outside `block/plant/leaves` and bushes without a `leavescenter1` sheet fall back the same way.
- **`.dlgpu off` then `on` leaves cached terrain undrawn on the RTX 3060 Ti** until the next session; not investigated.
- **The NVIDIA steady figure** comes from 26 s of standing still before the owner left; he then judged it by eye
  ("Everything looked good").
- Distant Lights on NVIDIA was not looked at specifically.
