# Session 119 — 0.5.1: an NVIDIA shader fix, the whole-world sweep, the import notice, and fresh-install fixes

**Date:** `2026-10-21..22`
**Branch/commit:** `main`; dev builds `621ac7c` (dev.2), `6b46739` (dev.3), `102d1e1` (dev.4), `939af75` (dev.5), `4421cc3` (dev.6), `9fa0026` (dev.7); TODO `eaf59a4`; release `7959339`
**Mod version:** `0.5.1` (promoted from the owner-played `0.5.1-dev.7`)
**Assist protocol / blob / schema:** `1 / 4 / 6` (append-only: `AssistHello.ImportStatus`, field 6, and the `AssistImportStatus` message type; `AssistServerSettings.SweepRadiusChunks` retired, sent as 0)

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

Three outside reports started the session. A player with an RTX 5090 (driver 617.42) saw no cached land
at all: the log showed `REQUIRED PACKED TERRAIN PATH FAILED` and three `ambiguous overloaded function
reference "clamp(...)"` errors in `lodterrainpacked.fsh`. Another player reported that land explored
before installing the mod never appeared, only the seed-built Farseer scenery; an earlier session
(not this one) had traced that to the savegame sweep being a fixed square around spawn and left the
owner three questions. Midway the owner began testing on a second machine with an NVIDIA card, which
surfaced a two-tone flicker in the seam band, "sideways" grass in the band, and a camera-motion warble.
Late in the session he reported distant cattails that sometimes drew a neon green.

Evidence used: the players' and owner's client logs, the owner's caches on the AMD machine (palettes and
runs decoded offline), decompiled engine source (`ilspycmd`: `SemVer`, `ClientSettings`,
`TextureAtlasManager`, `ShapeTesselator`, `BlockTextureAtlasManager`, `BlockReeds`, `Shape`), the
installed assets (shapes, textures, decoded with a standard-library PNG reader), a 60-minute headless
sandbox run, `.dltint` from his live session, and two of his screenshots. The NVIDIA machine has no
agent access; every observation there came through the owner.

## Work narrative

### 1. The packed shader on NVIDIA 617 (dev.2)

The three error lines map exactly onto the game's own includes, not ours: `fogandlight.fsh` lines 64 and
341 (`clamp(..., 0.09, 1)`, `clamp(..., 0, 0.9)`) and `skycolor.fsh` 116 (`clamp(..., 0, 0.5)`) - the
line gaps, 277 and 123, match. Only `lodterrainpacked.fsh` declared `#version 430 core`, which brings the
fp64 overloads of `clamp` into the candidate set; NVIDIA 617.42 calls mixed int/float literals ambiguous
there. The packed fragment stage uses no 4.30 feature (`VH_PACKED` is read only by the vertex stage), so
it went back to `#version 330 core` with the explicit-attrib-location extension, identical to the
indirect fragment stage the same player's log showed compiling. Desktop GLSL links a 4.30 vertex stage
with a 3.30 fragment stage; the varyings match by name. A static check keeps the stage at 3.30. G299.

### 2. The savegame sweep covers all explored land (dev.2)

Owner's three decisions: sweep every explored column, nearest an online player first and nearest spawn
while nobody is online; keep a 4,096-block limit only for the probe fallback, centred on the first
player to join; retire `SweepRadiusChunks`. `LodSweepOrder` sorts what is left by squared distance to the
nearest target on a thread-pool task and swaps the new order in at `Collect`; columns visited while a
sort runs are remembered and skipped, so nothing is visited twice. The pass re-checks every 5 s and
re-sorts when someone joins or leaves or moves more than 8 chunks (256 blocks). The absence check, with
no square left to sample, samples the absent position beside frontier columns
(`LodColumnMap.AbsentBesideFrontier`). `SweepRadiusChunks` is read once - a saved 0 moves onto
`SweepSavegame = false`, because 0 was a documented off switch - and never written back
(`ShouldSerialize`, verified against the engine's plain `JsonConvert` use). The wire field stays and is
sent as 0. Bench configs dropped the radius; the `completed-sweep` scenario now sweeps its whole world.

### 3. Headless sandbox run (0.5.1-dev.1 code)

One 60-minute dedicated-server run on a throwaway copy of the frozen bodanboys world (62,098 explored
columns, 30,622 of them outside the old square around spawn at chunk 8000,8000; the save names no
default spawn, so it is the map middle). Result: no terrain generated (the copy still held 62,098
columns), 17,410 proven captures, all 17,020 claims read back after a save, clean stop. The sweep
reached 89 chunks from spawn; the first explored land outside the old square starts at 129, so the run
did not reach it, and the owner stopped the test there. The rate was capture-bound near 8 columns/s
(backlog 24-31 of 32, G190), then fell to about 2.7/s after roughly 35 minutes as "held for engine
terrain demand" climbed by about 3,300 ticks every 6 minutes with no player connected and no new
columns created. Not explained.

### 4. Import status, popup and progress line (dev.2)

Owner: everyone affected sees the popup, multiplayer included, but only about land inside their own
Cache distance. The server tallies what is still to import in 512-block cells (`LodImportTally`), counts
the load pace over two minutes (`LodRateMeter`), and every 5 s sends each player who asked
(`AssistHello.ImportStatus`) a per-distance summary (`AssistImportStatus`: 65 bands, the last for
everything past 32,768 blocks). The client sums the bands inside its own Cache distance, so the figure
follows the setting with no message back. `LodImportPresenter` decides: a faint bottom-right line
(alpha 0.55) once the land left nearby would take a minute, held until done, "Explored land nearby is
imported" for 8 s and a 2 s fade; a popup once a session at ten minutes, held back while another of the
mod's windows is open. An assumed 8 columns/s stands in until the server has measured 30 s. A status
older than 30 s is ignored. The popup's first sentence is the owner's wording.

### 5. Version labels

Owner: all of this ships as 0.5.1, release numbers stay consecutive, dev builds take a sub-number, and
promoting a dev build to the release always needs his approval. He suggested 0.5.1.2; the engine's
`SemVer` parser rejects a fourth part and only best-guesses it on every load, so dev builds are
`0.5.1-dev.N`, which sorts below 0.5.1 (G304). The sandbox script accepts the suffix.

### 6. NVIDIA flicker in the seam band (dev.3)

`.dlband speckle 0` stopped the two-tone flicker on his NVIDIA machine. The speckle floored
`terrainPos`; a block top at 87 arrives there as 86.9999 at some pixels and 87.0 at others, so
neighbouring pixels hashed different blocks. The ownership test already snapped chunk planes for the same
reason. `snapToPlane` now moves a section-local coordinate onto the nearest multiple of a spacing within
`MASK_PLANE_SLACK`; the speckle addresses its block from the integer section origin plus snapped local
coordinates, and the band's 8-block steps snap walls on a step line. Owner-confirmed: "the z-fighting is
gone". G300.

### 7. Exact face for band textures (dev.4) - not the cause

"Sideways" grass looked like a wrong top-or-wall decision from the derivative normal, so packed quads
now hand the band their exact face (`vhFaceAxis`) and other meshes fall back to the derivative normal.
The grass did not change, which ruled the guess out; the change stays as a harmless robustness gain.

### 8. Non-square atlas on a fresh install (dev.5)

The owner described the grass precisely - three quarters grass, a quarter dirt, on every block - and
recalled the same symptom fixed on the AMD machine long ago (G140, 0.3.169). `IsTwoTileStrip` compared
the TopSoil overlay rectangle in raw atlas UV "because the block atlas is square". It is not always:
`ClientSettings` defaults `maxTextureAtlasWidth/Height` to 4096 x 2048 and raises the height to 4096
only in the 1.5 -> 1.6 settings migration. The AMD install is upgraded (4096 x 4096); a fresh install
(settings 1.16) keeps 2:1, where a 64 x 32 strip measures square, the top tile is never cut out, and
the whole strip - fringe and all - lands on every top. The test now scales each axis by
`BlockTextureAtlas.Size`, and `blockAtlasSize` became a vec2 so the band's mip footprint and the water
inset measure each axis in its own pixels. Owner on dev.5: the NVIDIA machine "looked much better and
performed well", with little warble after a restart. G301.

### 9. Part-tinted models: neon cattails, first cause (dev.6)

Cattails (`tallplant-coopersreed-*`) name no colour map on the block; the game tints their leaves and
stems part by part in the model and leaves the cobs brown and tips tan. The mod read "a plant with no
map" and lent it the fern fallback's plain `climatePlantTint`. `LodModelParts` now composes such a block
like `LodTopSoil` does grass over soil: each textured face averaged over its UV area, weighted by face
area times coverage, the untinted parts becoming the share, and the slot taking its maps from a block
that names the tinted parts' own pair (`climatePlantTint`/`seasonalFoliage`; the first such block is
`leaves-grown-acacia`). Models are read only when a part is tinted, cached per model file; a full slot
table falls back to the old slot rather than grey. The same rule now covers flowers, lupines, herbs,
saplings, berry bushes, bamboo, water lilies, some seaweed, tree ferns, ferns and sod roofing (which drew
untinted grey before). G303.

### 10. See-through texels: neon cattails, the real cause (dev.7)

`.dltint` showed dev.6 working as built (stored rgb(117,107,98), share (0.198,0.122,0.041), drawn about
rgb(73,79,8)), yet his screenshots showed a pale lime cube with dark specks, as bright at night as at
noon. Cattails name no up/all/top texture, so the band falls back to `TextureSubIdForBlockColor`: the cob
sheet `reeds/lod2-1`, 97% clear, whose clear pixels store pure white over an alpha-weighted mean of
rgb(56,34,17). `texel / mean` multiplied the albedo by 4.6x, 7.4x and 15x. Clear texels now read as the
mean. Measured first: leaves (acacia 129 in clear pixels against a mean of 129) and crops already store
about their mean there. Owner on dev.7: "the color is correct". G302.

Cattails not underneath a leaf block never render at distance, and never did (owner). The caches hold
about 3,300 such runs, flag `FlagCutout`, so they are captured and dropped somewhere later; left in TODO.

### 11. Release

Owner approved promoting dev.7 to 0.5.1. Only the two version strings changed; the zip holds the same 47
files as dev.7's, with `modinfo.json` and the DLL's version stamp the only differences. Copy-installed.
The owner's Mods folder lost its eleven 0.4.x zips between dev.2 and dev.3; nothing the agent ran
deletes there, and it was left alone.

---

## Delivered

- **0.5.1-dev.1** (sandbox only, never installed): the packed shader fix and the whole-world sweep.
- **0.5.1-dev.2** (`621ac7c`): dev.1 plus the import status, popup and progress line.
- **0.5.1-dev.3** (`6b46739`): `snapToPlane` for the speckle and the band's steps.
- **0.5.1-dev.4** (`102d1e1`): exact face for band textures.
- **0.5.1-dev.5** (`939af75`): two-tile strips judged in pixels; `blockAtlasSize` vec2.
- **0.5.1-dev.6** (`4421cc3`): `LodModelParts`.
- **0.5.1-dev.7** (`9fa0026`): clear texels read as the mean.
- **0.5.1** (`7959339`): dev.7 renumbered; TODO entry `eaf59a4`.
- Checks: 26,453-26,512 assertions, 0 failures, at each build; new checks cover the packed stage's
  version, the retired radius through Newtonsoft, the sweep order and its no-revisit rule across a
  re-sort, the frontier absence sample, the import tally, rate meter, Cache-distance bands and presenter,
  the popup wording, the wire layout of the new message and field, the snapped speckle and steps, the
  exact face, the 2:1 atlas strip test, the part-mixing identity and per-face texture areas, and the
  clear-texel rule.
- Documentation (this session close): CHANGELOG 0.5.1, GOTCHAS G299-G305, WIRE_HISTORY, TODO, STATUS,
  README's sweep description, and the private working rules for dev versions.

## Decisions

- **Sweep reach:** all explored land, nearest an online player first, spawn while empty; the probe
  fallback keeps 4,096 blocks around the first player; the radius setting is retired and a saved 0 stays
  off. Rejected: keeping the radius (every old file carries 128 and would cap the sweep forever).
- **Import notice:** every affected player, multiplayer included, only for land within their own Cache
  distance; popup at ten minutes, once a session; a faint line at one minute, bottom right; the first
  sentence in the owner's words.
- **Version labels:** dev builds `0.5.1-dev.N`; release numbers consecutive; promotion only with the
  owner's approval. Rejected: `0.5.1.2` (the engine's parser rejects it).
- **Cattails:** the proper per-part mix with the leaves' own seasonal tint, not the quick "no fallback"
  fix that would have drawn them grey-brown.
- **Deferred by the owner:** the NVIDIA warble (now "much better", watched), and cattails that are not
  under leaves.

## Traps

- GLSL 4.x pulls fp64 overloads into the game's int/float `clamp` calls; NVIDIA 617 rejects them (G299).
- NVIDIA hands a face's own plane coordinate back a hair below a whole number; `floor` then flickers
  between neighbouring cells (G300).
- A fresh install's block atlas is 4096 x 2048; an aspect test in raw UV is wrong there (G301).
- Clear texels store arbitrary colour - pure white in the cob sheet - and the band's `texel / mean` blows
  it up (G302).
- Plants that tint per model part name no block-level map; a fallback tint over-greens the whole block
  (G303).
- The engine rejects four-part mod versions; use a pre-release label (G304).
- A new assist message type must be registered last on both sides and sent only to a client that asked
  (G305).
- From the session itself: the Windows sandbox scripts are the `.ps1` ones (the `.sh` scripts need
  `mkfifo`, `ss` and `/proc`); and `scripts/package.sh` deletes an existing zip of the same version, so
  never run it while the source still carries a released version number.

## Flagged and unverified

- **Judgement calls for the owner:** the popup's other wording (his sentence is set; the rest is draft
  he accepted), the overlay's corner and faintness, the ten-minute and one-minute thresholds.
- **Never seen in game:** the import popup and the progress line; the sweep reaching land beyond the old
  square; the absence check sampling beside the frontier.
- **Unexplained:** the sandbox sweep's fall from about 8 to 2.7 columns/s after 35 minutes ("held for
  engine terrain demand" climbing with no player and no new columns).
- **Watched, not explained:** the NVIDIA camera-motion warble near the seam. Not anti-aliasing, not depth
  fighting (`.dlband sink 8` changed nothing), not speckle or textures; the owner saw little of it on dev.5
  after a restart.
- **Unmeasured:** how many of the 64 tint slots part-tinted plants take on a large modded world.
- **Known gaps:** cattails not under leaves never render (TODO); an older client shows the retired sweep
  radius as "0 chunks" in its diagnostics window; the ModDB text still says the sweep works around spawn.
