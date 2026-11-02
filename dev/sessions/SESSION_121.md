# Session 121 — 0.5.2-dev.18: the void, the terrain pool, and plants drawn as what they are

**Date:** `2026-10-31..2026-11-01`
**Branch/commit:** `main`; `bd3bf66` (dev.8), `d50b127` (dev.9), `a6e2130` (dev.10), `bb1083a` (dev.11), `c65096c` (dev.12), `e9cd144` (dev.13), `d80f173` (dev.14), `82d3c84` (dev.15), `87df726` (dev.16), `e66f8b1` (dev.17), `39d14bd` (dev.18). Not pushed.
**Mod version:** `0.5.2-dev.18` (a test build; the owner played dev.8, dev.9, dev.10, dev.12, dev.13, dev.14, dev.16 and dev.17 on 1.22.7)
**Assist protocol / blob / schema:** `1 / 4 / 6` (unchanged; a third append-only tail inside format 4)

> Session records are Tier 3 history. Write narrative as needed, but preserve the four required tail sections so future harvesting remains mechanical.

## Context and investigation

Session 120 ended on 0.5.2-dev.7 with one open report: a Cache distance below the explored distance left a void before
Farseer and the fog. That was fixed first, and the owner's test of it found a second fault. From there the session
became one long arc set by his screenshots: draw each plant as what it is, keep every one switchable to something
cheaper, and draw everything useful to the player. He took vanilla and cached shots of the same spots, and those pairs
were the acceptance evidence throughout.

His stated direction at the end of it: an **Ultra** preset above Quality for very high visual fidelity. He will go
through every preset at the end of this work and re-designate what each one holds.

## Work narrative

### 1. The void past a short Cache distance (dev.8)

`SilhouetteStableReplacementCoverage` asked which meshes were resident. The cap only cut the draw (section centre
within cap + 64), and the terrain shader discards past its fade's end (`dist > 1.0`). Coarse sections stay resident past
the cap, so Farseer and the fog were told land was covered that nothing drew. Ownership from residency dates from
0.4.100 (`df4bf43`), and the low slider values of dev.2 and dev.6 (`ccd72be`, `e2fb684`) made it easy to reach.

A cell now counts only if its selected section passes the shared cap test and the whole cell is inside the terrain's
end (`LodFarDistance.CellInsideTerrain`). The fade distance is `TerrainFadeDistance`, `min(Effective, cap + 512)`;
`EffectiveFarDistance` keeps the floor for the projection and the globe (G5). No shader changed.

An offline replay of cache `71c0c6af`, modelling the discard: 800 replays (8 logged positions, 5 offsets in the chunk,
10 caps, fresh and live), 4,832,311 void cells before, 0 after. The first replay left out the discard and looked
perfect while the real result kept a ring of void (G316). Owner, in play: looks good.

### 2. Crops and bushes (dev.9, dev.10)

`BlockCrop` joined the crossed plants, with height in `SectionSnapshot.PaletteCrossedShapes` because the palette flag
byte is full. Crops stay out of `ReedTable`: they stop rain and were always captured. Bushes first got a solid layered
tile for the seam band. The Cache slider took 128-block steps at his request.

His screenshots on dev.9 found three things. Bushes drew black, because the texture slot delegate hands the table colour
0 for a block entity. Crops took `PlantTintFallback`, a tint meant for grey ferns, which turned rye orange. The blue-grey
plant reported as flax was fennel, whose plain planes are solid, so crops with no picture became short and grow by
stage, and fennel and licorice layer their sheets into one see-through picture (`ComposePlant`). Owner: "Crops look
good, so do bushes."

### 3. The terrain pool (dev.11)

Testing dev.10 at a short Cache distance he saw huge areas of ground missing and water floating, with `REQUIRED PACKED
TERRAIN PATH FAILED ... absent from or rejected by the packed arenas`; a relog cleared it. `CeilingBytesFor` was given
the Cache distance at login. Arena pages are one 8 MiB page per region per level, so they follow what is resident, which
a short cap barely changes: 23 pages at 30 s on 512 and 8,192 alike, while a pool for 384 held 21 and one for 512 held
33. A refused section is never retried, so holes stayed until login.

The ceiling is now always the full horizon, a cap committed on demand, and a failure logs `mirror.Describe()` once per
episode. The cause is inferred from page counts, the pattern across sessions and the code: the ceiling counter was only
in the 30-second report, which showed 0 rejections (G317).

### 4. Leaf shapes, fruit trees and the Plants tab (dev.12)

He asked for several leaf models to choose between, fruit tree species, and one master toggle over sub-toggles on a new
tab. Five shapes at level 0: Cubes, Crossed Planes, Planes with Tops, Leaf Clusters, Clusters with Tops. A leaf plane
reads as a crossed plant in the private snapshot only, so everything that asks about cover gets the right answer, and
neighbours are told too. `LeafField` prices each through the real mesher on real caches.

Fruit tree state lives in the block entity, so capture reads it and palette entries carry a variant, stored as a third
append-only tail in format 4 (`WIRE_HISTORY`). The agent had called this not worth it, believing it needed a format and
a sync change. That was wrong, and it was said so.

| Shape | All quads vs cubes | Quads reaching pixels vs cubes |
|---|---:|---:|
| Crossed Planes | 85.4 / 88.9% | 112 / 109% |
| Planes with Tops | 93.0 / 95.0% | 127 / 121% |
| Leaf Clusters | 68.9 / 77.5% | 79 / 86% |
| Clusters with Tops | 73.9 / 81.9% | 89 / 95% |

Worlds `71c0c6af` / `6bab2982`, 100 sections, the whole level-0 mesh, so these include what planes stop hiding.

### 5. Choosing a shape (dev.13)

The owner's shots against vanilla, measured by region: planes read 5-13% brighter than vanilla, Clusters with Tops was
best in birch and a carpet in thick pine. One frame-rate trio at a pine spot: Cubes 260/145, Clusters 258/141, Clusters
with Tops 257/141. Removing the tops did not change the carpet (tree-scale spread 6.4 against 6.1, vanilla 8.8), so the
agent's guess that they caused it was wrong. Clusters with Tops became the default.

He reported the drop-down reverting to Cubes. The choice reached the renderer and drew, but never the config, and
`SaveConfig` reads the plant switches back from the config (G318). Every switch on the tab had the fault, and it came in
with dev.12.

Each stack of leaves now carries how buried it is (`LeafBuried`, 0-11: the eight spots three blocks around it, plus 3
for leaves overhead), in spare bits of its slot field, and a foot in its own shade. Fruit wood stops where the foliage
starts: the game's branches are a thin model, and a whole block of them stood as a black slot in the tree.

### 6. Vines, grass, stripes and anti-aliasing (dev.14)

Bright stripes across the canopy were flat tops seen edge-on, their cut-outs blurred into solid bars, and dev.13 had made
them worse by raising their shade. They now take the planes' shade and dissolve between 30 and 14 degrees in a noise
pattern anti-aliasing smooths (G319).

The planned lift for deep forest was dropped. The agent read pines as 15% too dark; the owner pointed out the time of
day had changed. Rock fell 16% and ground 9% between those shots, which left the pines within about 2% (G320).

He reported tall grass as blocks under trees, suspecting the cattail change. It was not that. The rain map starts at the
canopy there, so capture has always read that grass, and cube leaves hid it until dev.12 stopped them (G321). A scan of
400 sections found about 67,000 blocks of it, half-height cubes. It is thin cover now, and old caches heal on load.

Vines became one flat sheet per run. The cache settled the side: `wildvine-*-north` is held from the south 2,306 times
against 1,162 for the next neighbour, so a vine hangs on the far edge of its cell. Anti-aliasing reaches full strength
from vanilla's edge (`LodTaaMath.SeamReach`), so see-through sheets stop shimmering beside the seam.

### 7. Forage markers and resin (dev.15)

"We need to render everything that is useful to the player." Mushrooms and loose items let rain through, so capture
skipped them in the open and they drew only under leaves, as cubes or half cubes. Asked for something cheaper than a
half block, the agent proposed a mini-cube's look for one quad: a record with no footprint, which the vertex stage
raises as a square turned to the camera, never under two pixels. Loose stones are skipped, at his word. Resin wears flat
amber in front of its log, three pixels at least, from any direction; logs were stored already, so it needs no recapture.

Fruit trunks were dark green. A block entity's colour is sampled by position, which came back near-black, and the plant
tint then turned it green (G322).

### 8. Flowers and Ultra (dev.16)

Flowers stand up as one camera-facing quad at true size. A flower's art is two pictures, a tinted stem and untinted
petals, which take the path a cattail's cobs take, so nothing goes on the atlas. Off by default as purely cosmetic.
Owner: "Flowers look great, actually."

Ultra: everything, Far Biome Colors, and the 1-pixel detail target, which carries level 0 from about 620 to about 930
blocks. Far biome and the target were the agent's choices, to sit above Quality, and are his to change. The first-run
prompt keeps its three places.

### 9. Bushes, mushrooms and ferns (dev.17, dev.18)

The game builds a bush from two-block planes of sheets each 5-7% leaf, so bushes stand as two crossed planes and one flat
one, wearing the sheets layered into one see-through picture, in a leaf's shade. Three quads against a cube's five. That
picture replaced the solid tile, which nothing else drew.

The model's numbers were wrong for the job. Planes that say two blocks floated as a crown over bare ground in dev.17,
where the game's bush stands about a block and a quarter beside its selection box. dev.18 matches the screen (G323).

Mushrooms took two passes. dev.17 read the model's `cap` picture, but only a few models call it that; most use the
mushroom's own name. And the plant tint fallback gave every mushroom a tint the game does not, so a white puffball came
out orange (G324). Both are fixed in dev.18.

Eagle ferns were the wrong colour for the season. A small fern names its season map and its pictures only inside its
model. `ReadModelPartColor` gave up on a block listing no textures, and no block carries that map pair, so the tint had
no season at all. It now reads the pictures from the model, and a carrier block takes the maps to the tint table (G325).

---

## Delivered

- **0.5.2-dev.8** (`bd3bf66`): the void closed. **dev.9** (`d50b127`): crops on crossed planes, bush tiles, 128-block
  Cache steps. **dev.10** (`a6e2130`): black bushes, crop tint, plain crops, layered fennel and licorice.
- **0.5.2-dev.11** (`bb1083a`): the arena ceiling at the full horizon.
- **0.5.2-dev.12** (`c65096c`): five leaf shapes, fruit tree variants, the Plants tab, `PlantDetailChecks`, `LeafField`.
- **0.5.2-dev.13** (`e9cd144`): plant settings save, leaf shading, fruit wood trimmed, Clusters with Tops the default.
- **0.5.2-dev.14** (`d80f173`): vine sheets, tall grass flat, flat tops dissolve, anti-aliasing from vanilla's edge.
- **0.5.2-dev.15** (`82d3c84`): Forage Markers, amber resin, loose stones gone, brown fruit trunks.
- **0.5.2-dev.16** (`87df726`): Flower Sheets and the Ultra preset.
- **0.5.2-dev.17** (`e66f8b1`): berry bush sheets, cap colours, fern seasons. **dev.18** (`39d14bd`): bush height,
  mushroom pictures and tint. Each build was verified and copy-installed to both Mods folders; none is pushed.
- `leaffield --cache <path> --samples N [--under]`: prices each shape on a real cache, or lists what stands under the
  canopy and what holds a vine up.
- Checks: 26,875 assertions, 0 failures on 1.22.0 and 1.22.7 at dev.18.
- Documentation (2026-11-01, at the owner's request): this record, CHANGELOG, GOTCHAS G316-G325, WIRE_HISTORY,
  ARCHITECTURE decisions 68-72, TODO, STATUS, README and the private working rules.

## Decisions

- **A cell is covered only where terrain really draws:** the cap test and the fade's end, never residency.
- **The arena ceiling is the full horizon.** A limit derived from a setting that does not bound the demand protects
  nothing; the ceiling is a cap and commits on demand.
- **Leaf Clusters with Tops is the default**, owner's choice from shots and a frame-rate trio. Cubes and the other three
  stay in the drop-down. Rejected: Crossed Planes with Tops, the most faces and the worst look.
- **Every plant feature has its own switch under Plant Detail**, off being the cheaper form (owner). Performance turns
  Plant Detail off. Flower Sheets is off by default until the preset review.
- **Vines and markers draw nothing past their reach as sheets:** vines nothing at all past level 0; mushrooms, flint and
  ore thin mats there, on the established fern path.
- **One quad instead of a mini-cube**, since from any distance these are seen at it reads the same at a sixth of the
  faces. Owner asked; answered as yes, and cheaper.
- **Loose stones are not drawn at all** (owner). Boulders are.
- **Tall grass stays out of the above-rain capture.** It would raise every meadow by a block.
- **Fruit trunks are one brown** (116, 92, 67): every species sampled but olive is within a few shades, and a trunk's
  block does not say its species.
- **Ultra is settings-only.** The first-run prompt asks what a machine can carry. The agent chose Far Biome Colors and
  the 1-pixel target; the owner re-designates presets at the end.
- **Decision 68 narrowed:** it was about texture on cubes. See-through plants wear their sheets to the end of level 0 at
  the owner's direction, since a plane needs its sheet to have a shape at all.
- **Rejected:** thinning the leaf cell rule, held back as the shading closed the pine carpet well enough; an in-canopy
  lift for pines (a lighting artefact, G320); mini-cubes as real geometry.

## Traps

- **A replay that omits the discard looks perfect.** Terrain ends at the fade's end, on a circle, not at section edges (G316).
- **A limit taken from a setting that does not bound the demand** failed quietly as holes, cured only by a relog (G317).
- **A setting read back from the config on save** must be written there first, or it applies, draws and is lost (G318).
- **Flat see-through planes blur solid when edge-on** (G319).
- **Brightness across screenshots mixes the time of day:** compare with rock or ground in the same shots (G320).
- **Removing cover shows what was always there.** Tall grass was not new, and the likely recent change was not the cause (G321).
- **A block entity's position-sampled colour** can be near-black, and a tint makes it look like a real colour (G322).
- **A model's numbers are not its size on screen.** Check against the selection box in a vanilla shot (G323).
- **The plant tint fallback catches anything of material Plant with no maps**, and a picture's name is not reliable (G324).
- **A season can live only in a model** (G325).

## Flagged and unverified

Judgement calls awaiting human review:

- What each preset holds: flowers in Quality (he may want that), Far Biome Colors and the 1-pixel target in Ultra.
- Whether bushes should show berries. Offered, not answered. It would be a variant like a fruit tree's.
- Plant Detail off in Performance, on in Balanced and Quality; Plant Textures on in Balanced.
- Shade values, the fruit share, the marker sizes and the two minimum pixel widths are judged, not measured.
- The planes' 0.82 shade came from an average over forest; birch may now sit a little bright.

Claims without evidence at the level needed:

- **dev.18 is unplayed:** bush height, cap pictures and the mushroom tint are harness-tested only.
- **Ultra has not been run.** His config at 19:04 held Quality's far biome and detail values with Flower Sheets on, so
  its frame cost is unknown; Far Biome Colors is the most expensive single feature in the mod.
- **Resin, flint and ore in the open** have no report from him. Land already explored gains flint, ore and mushrooms only
  as chunks load again, since `FingerprintRule` was not bumped.
- **Cattails** go through the same code as the ferns, so they may have shifted colour. Not reported on.
- **Anti-aliasing from vanilla's edge:** stills cannot show shimmer or smearing. No frame-rate pair was taken. His
  sessions afterwards held 243-281 FPS with no failed packed terrain, across different scenes.
- **The dev.11 pool:** never tested directly by flying at 384 or 512. No failure has recurred, and the last log holds none.
- **Shaders** have only ever compiled on his card; nothing here can compile GLSL.
- **Fennel as the "flax" he saw** is inferred. **Fruit tree species** past plain green were not reported on separately.
- **Seen, not investigated:** a ruin at 18:48 drew Aged roofing pale against vanilla's dark wood.
