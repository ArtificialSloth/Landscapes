# Session 122 — 0.5.2-dev.22: pale ruins, bushes in cave sections, lupines, and Distant Lights

**Date:** `2026-11-01`
**Branch/commit:** `main`; `a331c86` (dev.19), `6205660` (dev.20), `c59e268` (dev.21), `37cf29d` (dev.22). Not pushed.
**Mod version:** `0.5.2-dev.22` (a test build; the owner played dev.19, dev.20, dev.21 and dev.22 on 1.22.7)
**Assist protocol / blob / schema:** `1 / 4 / 6` (unchanged; a fourth append-only tail inside format 4)

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

Session 121 ended on dev.18 with berries, the pale ruin and a preset review open. The owner opened this one by asking for
berries on bushes, dropped them on seeing what distance does to them, and took the pale ruin next. His screenshots then
found two more faults, and he asked whether light sources could reach cached terrain. All of it was settled offline
from the cache, the game's assets and the decompiled engine, with his vanilla and cached pairs as the acceptance evidence.
No game was launched from here.

## Work narrative

### 1. Berries: studied and dropped

Feasible: `BEBehaviorFruitingBush.BState` holds the stage, and the game ships a sheet per type, stage and health. But a
cluster is about a tenth of a block. At his scale of one block to a pixel at 935 blocks, a bush is about 7 pixels across at
256 blocks and 3 at 620, so a cluster is under a pixel at the nearest cached distance. An offline mock-up showed a
faint tint at 256 that was gone by 620 except for reds. Enlarged clusters read further but covered 61% of a healthy
blackberry bush against 24%. The owner: not worth it at high distances. Nothing was built.

### 2. Pale ruins (dev.19)

The hover name "Aged roofing" belongs to no block type. It is the `blockName` of a chiselled block in the schematics
(their `BlockEntities` are Ascii85 tree attributes). Such a chisel's material is `meta-selectablecollider`, invisible,
and the roof you see is support beams stored on it by `BEBehaviorSupportBeam`. Capture refuses Meta materials as a
stand-in, so it stayed a chisel, and the game's answer for it was the meta texture, stored as 212 grey. Across the game's
schematics 7,982 of 59,707 chisels are meta-only; 4,672 of those carry beams, the rest are colliders round gears, tanks and
banners. In the same ruins `multiblock-monolithic-*` fills door tops and banners. Its shape is `basic/nothing`, the main
block draws it, and with no art it stored 252 near-white (962 palette entries in cache `71c0c6af`).

Both now take one structure wood colour, 54,46,33, the game's rotten beam texture, against 56,50,39 measured from a
vanilla shot of the roof. It is exempted in all five colour paths (G322), so `Reclassify` corrects stored caches on load
with no recapture. The owner confirmed the roof in his next shots. Both faults date from 0.4.62.

### 3. Three-tall bushes (dev.20)

dev.18's height change had not touched them. The crop gave the signature: a plane one block wide and 2.75 tall with the
picture repeating per block, which is `AddCrossedPlanes`, not `AddBushSheets`. The run heights in the cache were all 1, so
it was not stacked blocks. The mesher draws from `cave.Self`, and `LodCaveCull` rebuilds that snapshot for a section with a
sealed cave. The copy left out `PaletteCanopy`, so `IsBushSheet` went false while the snapshot's `FlagCrossed` stayed.
That is why two-wide bushes and stacks shared one shot. The same field carries resin and fruit wood. `PlantDetailChecks`
now requires the copy to name every public snapshot field (G326).

### 4. Lupines (dev.20)

Lupines name their pictures `plant1a` and `plant1astem`, which `FlowerStemNames` and `FlowerPetalNames` did not know, so
no sheet resolved and they stayed patches. They now wear a sheet at `CrossedShapeTallFlower`, a block and a half.
Existing caches correct on load.

### 5. Lag spikes: read, then set aside

Every frame of about 320-360 ms is the `finfr` step and lands on an F12 screenshot: all eight in his dev.19 session, and
the same pattern in the five sessions the archive still holds, back to dev.12 (G329). In minutes with no screenshot the
slowest 1% ran at 123-155 FPS. What remained were 20-50 ms frames from mixed sources: entity work, a gen1 collection near
login, one `doneMTT`, and a burst of `end` frames that may have been him switching windows. dev.12 to dev.19 showed no change,
and older logs are gone. The owner set the subject aside.

### 6. Distant Lights (dev.21)

Cached terrain was lit by sky and sun alone. Chunk packets carry `Light`, `LightSat` and `LightPositions`, so a plain
client can read them. Three real caches held surface lights in 59 of 7,353, 24 of 3,716 and 63 of 5,811 sections, an
undercount because open torches let rain through and are not captured as blocks (G312). Offered ground glow or lamp dots,
he chose ground glow.

Capture reads the cell above the rain-map top, so walls stop light as they do up close and an open torch counts. Only
observed captures read it, or a generated peek would put every lamp out. `LodSection.BlockLight` is replaced whole, and
`SetLight` runs before `ReplaceColumns`, because a lamp can change with no block moving in the column it lights. Mips keep
the brightest of four. A section's light is a 64x64 tile in one 2048x2048 texture, one texel per column at any level,
written when the section is sent to be meshed since it belongs to a place rather than a mesh. The tile number plus one
sits in `noiseOrigin.w`, the record's reserved float. The shader mixes as `applyLight` does. Sampler unit 5 was free.

The owner on dev.21: "seems to work great", with a night shot of warm pools far away. Shaders compiled first time on his card.

### 7. Holes under fruit trees (dev.22)

His 21:36-21:38 shots on dev.20 showed fog banks over open land and a sky-coloured hole at the foot of a fruit tree. A
trunk counted as cover, so the soil below dropped its top. dev.13 stops fruit wood at the foliage, and foliage beside the
foot trims the whole run. `LodMeshGroundCoverage` calls a 32-block cell covered only if every column has an opaque top, so
one hole left the cell uncovered, Farseer stayed under it and the seam fog stood there, spreading past the cell (G327).
dev.20's fix had spread the trimming into sealed-cave sections, which had been spared.

`SectionSnapshot.Of` now sets `FlagCutout` on canopy wood, which `IsCutoutRun` alone reads, and neighbours get the table
too, so a wall beside a trunk keeps its face. The new check failed with the flag disabled. The owner on dev.22: the fix
worked, everything looks good.

---

## Delivered

- **0.5.2-dev.19** (`a331c86`): `HasNoLookOfItsOwn` and `StructureWoodColor`.
- **0.5.2-dev.20** (`6205660`): `PaletteCanopy` on the cave copy, lupine sheets, the snapshot field check.
- **0.5.2-dev.21** (`c59e268`): Distant Lights: `LodBlockLight`, `LodBlockLightTiles`, a fourth format-4 tail, the General
  switch, `BlockLightChecks` (31 assertions).
- **0.5.2-dev.22** (`37cf29d`): `FlagCutout` on canopy wood. Each build was verified and copy-installed to both Mods
  folders; none is pushed.
- Checks: 26,938 assertions, 0 failures on 1.22.0 and 1.22.7 at dev.22.
- Documentation (2026-11-01, at the owner's request): this record, CHANGELOG, GOTCHAS G326-G329, WIRE_HISTORY,
  ARCHITECTURE decisions 73-74, TODO, STATUS, README and the private working rules.

## Decisions

- **Berries are dropped** (owner): a cluster is under a pixel at cached distance. Do not re-propose.
- **What shows nothing of its own draws as structure wood.** One colour, no capture change, and stored caches correct on
  load. Beams as stand-ins and fillers recorded as their main block were left out for the size of the change.
- **Collider shells stay as dark cubes**, though vanilla draws nothing there, so huge gears keep a shape at distance.
- **Ground glow over lamp dots** (owner). About 40 pixels at 1,000 blocks against 2.
- **Light is read from the game's finished solution**, not worked out from sources, so walls are right with no code.
- **Tiles over a world-space field or quads split by light:** an unlit section pays one comparison, and packed quads
  have no spare bits. Only the terrain programs read them; cached water stays unlit.
- **Distant Lights is on by default, off in Performance:** the agent's call, for the owner's preset review.
- **Fruit wood covers nothing while leaves are planes,** matching `FlagCutout`'s own meaning. Rejected: keeping a one-block
  stub at the foot.
- **No re-sweep for light.** Land gains it as chunks load again. A `FingerprintRule` bump remains available.

## Traps

- **A hand-written copy of a type loses the field added last** (G326).
- **One column with no top unsettles a 32-block cell**, and a fog bank in open country means a hole first (G327).
- **A hover name is not a block.** Read the schematic before searching the block types (G328).
- **F12 is the biggest spike in a hitch report** (G329).
- **A `DispatchProxy` must assign `out` arguments**, or the generated stub unboxes a null: `NullReferenceException` inside
  `generatedProxy`, with the proxy's own code not on the stack.
- **The engine's sampler defaults count declarations,** so a new sampler moved `liquidDepth` off `climateField`, and the
  check pinned to that one name failed while nothing was wrong. It now asks for a collision with any of ours.
- **`sed -i` from Git Bash rewrote a CRLF source file to LF** and a pattern ending in `\r` matched nothing. Use the Edit
  tool on these files.

## Flagged and unverified

Judgement calls awaiting human review:

- Distant Lights on in Balanced, Quality and Ultra, off in Performance.
- `StructureWoodColor` for fillers: banners and door tops draw dark rather than their own colour.
- The lantern draws as a solid orange cube and an awning as flat grey, both seen in his 20:59 shots and left alone.

Claims without evidence at the level needed:

- **Distant Lights' frame cost is unmeasured.** No on/off pair at one spot. Sessions in different scenes prove nothing.
- **NVIDIA:** the new shader code has only compiled on his AMD card.
- **Every fog bank coming from trunks** is inferred. The cause is proved for one and 16 trunks were counted, an undercount
  that takes no leaves from the next section. He reports the fog gone. Another column with no top would do the same.
- **Tiles past 1,024** stay unlit, counted in `Refused` and not logged. Surface lava was not measured for this.
- **Small lag spikes** are unexplained and set aside by the owner, not solved.
- **Pale stored colours not looked at:** `drycarcass-*` and `glass-vintage`.
- **`flower-croton-*` and `flower-rafflesia-*`** use other picture names and stay patches. Not seen in these worlds.
